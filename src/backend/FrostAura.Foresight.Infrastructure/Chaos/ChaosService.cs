using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Chaos;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Domain.Chaos;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Foresight.Infrastructure.Chaos;

/// <summary>
/// Orchestrates a full chaos/bust test matrix run. For each (model, strategy, windowLen) combo:
///   1. Precompute the model's BetCandidate array ONCE over the full available candle range.
///   2. Draw SampleCount random start offsets via the deterministic RNG.
///   3. Replay each window under the staking strategy (pure).
///   4. Aggregate, persist, and broadcast progress via SSE.
///
/// Background execution pattern copied verbatim from
/// <see cref="FrostAura.Foresight.Infrastructure.Live.BacktestsService"/>: Task.Run on a fresh
/// DI scope, CancellationToken.None, status lifecycle via the DB row.
///
/// DETERMINISM: The seed is resolved once per batch (null ⇒ default 0) and persisted, making
/// every run byte-reproducible. No DateTime, no System.Random anywhere in the engine path.
/// </summary>
public sealed class ChaosService : IChaosService
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IServiceScopeFactory _scopes;
    private readonly IChaosEventHub _events;
    private readonly ILogger<ChaosService> _logger;

    public ChaosService(
        ForesightDbContext db,
        ITenantContext tenant,
        IServiceScopeFactory scopes,
        IChaosEventHub events,
        ILogger<ChaosService> logger)
    {
        _db = db;
        _tenant = tenant;
        _scopes = scopes;
        _events = events;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // START
    // ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<Guid> RunAsync(ChaosRequest req, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;

        if (!SupportedSymbols.IsSupportedSymbol(req.Symbol))
            throw new InvalidOperationException($"Symbol '{req.Symbol}' is not supported.");
        if (!SupportedSymbols.IsSupportedInterval(req.Interval))
            throw new InvalidOperationException($"Interval '{req.Interval}' is not supported.");
        if (req.SampleCount < 1) throw new InvalidOperationException("SampleCount must be ≥ 1.");
        if (req.WindowLengthCandles < 2) throw new InvalidOperationException("WindowLengthCandles must be ≥ 2.");
        if (req.InitialBalance <= 0) throw new InvalidOperationException("InitialBalance must be > 0.");
        if (req.InitialBetSize <= 0) throw new InvalidOperationException("InitialBetSize must be > 0.");

        foreach (var sid in req.StrategyIds)
            if (!StakingStrategies.IsKnown(sid) && !Guid.TryParse(sid, out _))
                throw new InvalidOperationException($"Unknown strategy '{sid}'. Provide a built-in strategy id or a custom DAG strategy Guid.");

        // Resolve seed: null ⇒ use 0 (fixed default, fully reproducible).
        var seed = req.Seed ?? 0L;
        var batchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Enumerate all (model, strategy, windowLen) combos and create "running" rows up-front.
        var windowLens = new HashSet<int> { req.WindowLengthCandles };
        if (req.LengthSweep is { Length: > 0 } sweep)
            foreach (var l in sweep) windowLens.Add(l);

        var runIds = new List<Guid>();
        foreach (var modelId in req.ModelIds)
        {
            // Validate the model exists (fast tenant-scoped check).
            var model = await _db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => (m.TenantId == tenantId || m.TenantId == null) && m.Id == modelId, ct)
                ?? throw new InvalidOperationException($"Model {modelId} not found.");
            if (!model.SupportsBacktesting)
                throw new InvalidOperationException($"Model '{model.Name}' does not support backtesting.");

            foreach (var strategyId in req.StrategyIds)
            {
                foreach (var windowLen in windowLens)
                {
                    var run = new ChaosRun
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        BatchId = batchId,
                        ModelId = modelId,
                        StrategyId = strategyId,
                        Symbol = req.Symbol,
                        Interval = req.Interval,
                        WindowLength = windowLen,
                        SampleCount = req.SampleCount,
                        AllowBorrow = req.AllowBorrow,
                        Seed = seed,
                        Status = "running",
                        StartedAt = now,
                    };
                    _db.ChaosRuns.Add(run);
                    runIds.Add(run.Id);
                }
            }
        }
        await _db.SaveChangesAsync(ct);

        // Fire-and-forget on a fresh DI scope.
        _ = Task.Run(() => ExecuteBatchAsync(
            batchId, tenantId, runIds, req, seed), CancellationToken.None);

        return batchId;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // BACKGROUND EXECUTION
    // ──────────────────────────────────────────────────────────────────────────────────────

    private async Task ExecuteBatchAsync(
        Guid batchId, Guid tenantId, List<Guid> runIds, ChaosRequest req, long seed)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<IFlowExecutor>();
        var venuePriceStore = scope.ServiceProvider.GetRequiredService<IVenuePriceStore>();
        var hub = scope.ServiceProvider.GetRequiredService<IChaosEventHub>();
        var ct = CancellationToken.None;

        var totalCombos = runIds.Count;
        hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Started, 0, totalCombos, 0, 0, null));

        // We process runs in order of creation (model → strategy → windowLen).
        // Load the runs we created so we know what model/strategy/window each ID maps to.
        var runRows = await db.ChaosRuns
            .Where(r => r.BatchId == batchId)
            .ToListAsync(ct);

        // Cache: precomputed candidates per model (expensive — one full-range model replay each).
        var candidateCache = new Dictionary<Guid, (BetCandidate[] Candidates, double SyntheticFraction)>();

        for (var comboIdx = 0; comboIdx < runRows.Count; comboIdx++)
        {
            var row = runRows[comboIdx];
            try
            {
                // ── 1. Precompute candidates for this model (cache hit avoids re-replay) ──────
                if (!candidateCache.TryGetValue(row.ModelId, out var cached))
                {
                    _logger.LogInformation("Chaos batch {BatchId}: precomputing candidates for model {ModelId}", batchId, row.ModelId);
                    var (cands, synFrac) = await PrecomputeCandidatesAsync(
                        db, executor, venuePriceStore, tenantId, row.ModelId,
                        row.Symbol, row.Interval, req, ct);
                    cached = (cands, synFrac);
                    candidateCache[row.ModelId] = cached;
                    _logger.LogInformation("Chaos batch {BatchId}: model {ModelId} → {N} candidates, synthetic {S:P2}",
                        batchId, row.ModelId, cands.Length, synFrac);
                }

                var (candidates, syntheticFraction) = cached;

                // ── 2. Resolve the strategy ───────────────────────────────────────────────────
                // Built-in: use the catalogue directly (fast path, pure math).
                // Custom DAG: pre-evaluate stakes for ALL candidates ONCE async, then pass the
                // precomputed array into the sync ReplayWindow loop (keeps ChaosRunner pure).
                IStakingStrategy strategy;
                decimal[]? precomputedStakes = null;

                if (StakingStrategies.IsKnown(row.StrategyId))
                {
                    strategy = StakingStrategies.Resolve(row.StrategyId);
                }
                else
                {
                    // DAG strategy: resolve via IStrategyEvaluator.
                    // We need a temporary IStakingStrategy that uses the precomputed stakes;
                    // build the precomputed array here async, then pass it to the window replay.
                    var evaluator = scope.ServiceProvider.GetRequiredService<IStrategyEvaluator>();
                    var strategyCtx = DagStakingStrategyAdapter.MakeStrategyFlowContext(
                        tenantId, row.ModelId, row.Symbol, row.Interval);

                    // Pre-evaluate stake for every candidate using a representative StrategyStep.
                    // For path-dependent sizing (e.g. martingale) this is only approximate since we
                    // can't know the prior won/balance state without replaying. For DAG strategies
                    // the sizing is typically edge-based (depends on pUp/yesPrice/noPrice/balance)
                    // so we use the per-candidate edge inputs and the initial balance as context.
                    // The precomputed stake is then used verbatim in ReplayWindow (via a wrapper).
                    // Note: this is a DAG-strategy-specific design choice documented in CLAUDE.md.
                    precomputedStakes = new decimal[candidates.Length];
                    var stepForPrecompute = new StrategyStep(req.InitialBetSize, true, req.InitialBetSize, req.InitialBalance, default);
                    for (var ci = 0; ci < candidates.Length; ci++)
                    {
                        var c = candidates[ci];
                        var edgeStep = stepForPrecompute with
                        {
                            Inputs = new StakingInputs(c.PUp, c.YesPrice, c.NoPrice)
                        };
                        precomputedStakes[ci] = await evaluator.NextStakeAsync(row.StrategyId, edgeStep, strategyCtx, ct);
                    }
                    _logger.LogInformation("Chaos: pre-evaluated {N} DAG strategy stakes for {Strategy}", candidates.Length, row.StrategyId);
                    // strategy remains null for DAG path — per-window instances are created below.
                    strategy = null!; // will be overridden per-window using precomputedStakes
                }

                // ── 3. Generate deterministic start offsets ────────────────────────────────────
                var offsets = ChaosRunner.GenerateStartOffsets(
                    row.SampleCount, row.WindowLength, candidates.Length, seed);

                if (offsets.Count == 0)
                {
                    row.Status = "failed";
                    row.CompletedAt = DateTimeOffset.UtcNow;
                    row.Error = $"Not enough candidates ({candidates.Length}) for window length {row.WindowLength}. Widen the candle range or reduce the window size.";
                    await db.SaveChangesAsync(ct);
                    hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Progress, comboIdx + 1, totalCombos, 0, 0, row.Error));
                    continue;
                }

                // ── 4. Replay windows + collect sample results ────────────────────────────────
                var sampleResults = new ChaosSampleResult[offsets.Count];
                for (var si = 0; si < offsets.Count; si++)
                {
                    // For DAG strategies: create a fresh PrecomputedStakesStrategy per window
                    // so the internal sequential call index aligns with the window's candidates.
                    // For built-in strategies: reuse the same instance (stateless pure function).
                    IStakingStrategy windowStrategy = precomputedStakes is not null
                        ? new PrecomputedStakesStrategy(row.StrategyId, precomputedStakes, offsets[si])
                        : strategy;

                    sampleResults[si] = ChaosRunner.ReplayWindow(
                        candidates, offsets[si], row.WindowLength,
                        windowStrategy, req.InitialBalance, req.InitialBetSize, row.AllowBorrow);

                    if (si % 50 == 0)
                        hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Progress, comboIdx + 1, totalCombos, si, offsets.Count, null));
                }

                // ── 5. Aggregate ──────────────────────────────────────────────────────────────
                var agg = ChaosRunner.Aggregate(
                    row.ModelId, row.StrategyId, row.WindowLength,
                    sampleResults, req.InitialBalance, syntheticFraction);

                // ── 6. Persist row + capped samples ───────────────────────────────────────────
                row.BustRate = (decimal)agg.BustRate;
                row.ProfitP5 = agg.ProfitP5;
                row.ProfitP50 = agg.ProfitP50;
                row.ProfitP95 = agg.ProfitP95;
                row.WorstDrawdown = agg.WorstDrawdown;
                row.MeanZeroCrossings = agg.MeanZeroCrossings;
                row.SyntheticBetFraction = agg.SyntheticBetFraction;
                row.Pass = agg.Pass;
                row.Status = "complete";
                row.CompletedAt = DateTimeOffset.UtcNow;

                // Cap persisted samples at 500 (same spirit as the backtest ledger cap).
                const int SampleCap = 500;
                var toPersist = sampleResults.Length > SampleCap
                    ? sampleResults[^SampleCap..]
                    : sampleResults;

                foreach (var s in toPersist)
                {
                    db.ChaosSamples.Add(new ChaosSample
                    {
                        Id = Guid.NewGuid(),
                        ChaosRunId = row.Id,
                        StartMs = s.StartMs,
                        Survived = s.Survived,
                        FinalBalance = s.FinalBalance,
                        MaxDrawdown = s.MaxDrawdown,
                        ZeroCrossings = s.ZeroCrossings,
                    });
                }

                await db.SaveChangesAsync(ct);

                hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Progress,
                    comboIdx + 1, totalCombos, offsets.Count, offsets.Count, null));

                _logger.LogInformation(
                    "Chaos {Id}: {Strategy}/{Window} — BustRate {Bust:P2}, P50 profit {P50:+0.##;-0.##}, Pass={Pass}",
                    row.Id, row.StrategyId, row.WindowLength, agg.BustRate, agg.ProfitP50, agg.Pass);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chaos run {Id} failed", row.Id);
                try
                {
                    var tracked = await db.ChaosRuns.FirstOrDefaultAsync(r => r.Id == row.Id, ct);
                    if (tracked is not null)
                    {
                        tracked.Status = "failed";
                        tracked.CompletedAt = DateTimeOffset.UtcNow;
                        tracked.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception saveEx) { _logger.LogError(saveEx, "Failed to mark chaos run {Id} as failed", row.Id); }

                hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Progress, comboIdx + 1, totalCombos, 0, 0, ex.Message));
            }
        }

        hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Completed, totalCombos, totalCombos, 0, 0, null));
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // CANDIDATE PRECOMPUTE
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Precomputes the BetCandidate array for a model by replaying the model over the cached candle
    /// range. The candidate array is built ONCE per model and reused across all (strategy × windowLen)
    /// combos in the matrix.
    ///
    /// Each candidate contains: pUp from the model, anti-look-ahead venue prices from
    /// <see cref="IVenuePriceStore.EnsureEntryAsync"/>, and the realised direction
    /// (outcomeUp = target.Close &gt; anchor.Close).
    ///
    /// Candidates where pUp == 0.5m (model abstained) are dropped — they carry no information and
    /// would silently be skipped by ReplayWindow anyway.
    ///
    /// PERFORMANCE: candles for ALL intervals are loaded from the DB in a single query per interval
    /// and served via an in-memory <see cref="CacheOnlyCandleProvider"/>. A temporary
    /// <see cref="BacktestRunner"/> is constructed with this provider so the replay is entirely
    /// in-memory — zero Binance network calls. The Binance-backed provider (injected into the scoped
    /// <c>BacktestRunner</c>) re-fetches the live edge on every call; using it here would page Binance
    /// hundreds of times for uncached off-tf candles over the full historical range, causing the chaos
    /// run to never complete.
    /// </summary>
    private static async Task<(BetCandidate[] Candidates, double SyntheticFraction)> PrecomputeCandidatesAsync(
        ForesightDbContext db,
        IFlowExecutor executor,
        IVenuePriceStore venuePriceStore,
        Guid tenantId,
        Guid modelId,
        string symbol,
        string interval,
        ChaosRequest req,
        CancellationToken ct)
    {
        var model = await db.Models.AsNoTracking()
            .FirstAsync(m => m.Id == modelId, ct);

        FlowDefinition flow;
        try
        {
            flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition,
                       new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Model {modelId} definition is invalid JSON: {ex.Message}");
        }

        if (flow is null || !flow.SupportsBacktesting)
            throw new InvalidOperationException($"Model {modelId} does not support backtesting.");

        var trainedStateJson = model.TrainedState ?? "{}";

        // Bound the candidate window to the ALREADY-CACHED candle range for this (symbol, interval).
        // Passing 0..far-future makes the candle adapter backfill the ENTIRE Binance history (years
        // of candles) on a cache miss — an unbounded fetch that hangs the sweep. The chaos pool is
        // exactly the candles already stored (warmed by training / backtests / the cache warmer):
        // deterministic and bounded, with no wall-clock (banned in the engine). To widen the pool,
        // run a backtest / warm more candles for the interval first.
        var range = await db.HistoricalCandles
            .Where(c => c.Symbol == symbol && c.Interval == interval)
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(c => c.OpenTime), Max = g.Max(c => c.OpenTime) })
            .FirstOrDefaultAsync(ct);
        if (range is null)
            return (Array.Empty<BetCandidate>(), 0.0); // no candles cached yet — nothing to sample

        // Load ALL cached candles for every supported interval from DB into memory, then replay
        // fully in-memory via a DB-only provider. This is the O(1)-network fix: the Binance-backed
        // adapter would re-fetch the live edge + any uncached off-tf range on every GetRangeAsync
        // call, producing hundreds of Binance pages for large historical windows.
        //
        // We include a warmup buffer on the left (flow.WarmupCandles intervals) so the slice
        // provider can satisfy indicator warmup for the first bettable candle, exactly mirroring
        // what BacktestRunner.ReplayDirectionsAsync does when it calls _candles.GetRangeAsync with
        // startMs - warmupMs. Because the warmup data lives in the DB cache already (it was fetched
        // when the cache was first warmed), no Binance call is needed.
        var intervalMs = BacktestRunner.PublicIntervalMs(interval);
        var warmupBuffer = (long)flow.WarmupCandles * intervalMs;
        // Cap the candidate pool to the most RECENT N candles. After training, the candle cache can
        // hold MONTHS of candles; replaying the model flow per-candle over the whole range
        // (~tens of ms/candle) would take many minutes. A bounded recent pool keeps the precompute to
        // seconds while giving ample random-start variety. Deterministic (derived from the cached
        // range, no wall-clock); auto-widened if a sweep's window/sample count needs more.
        const int MaxCandidatePool = 2000;
        var sweepMax = req.LengthSweep is { Length: > 0 } ? req.LengthSweep.Max() : req.WindowLengthCandles;
        var neededForSweep = sweepMax + req.SampleCount + flow.WarmupCandles;
        var poolCandles = Math.Max(MaxCandidatePool, neededForSweep);
        var effStart = Math.Max(range.Min, range.Max - (long)poolCandles * intervalMs);
        // Earliest candle we need: the warmup window before the first candidate start.
        var fetchStart = effStart - warmupBuffer;

        // Load target-interval candles (includes the warmup buffer before range.Min).
        var targetCandles = await db.HistoricalCandles
            .AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Interval == interval &&
                        c.OpenTime >= fetchStart && c.OpenTime <= range.Max)
            .OrderBy(c => c.OpenTime)
            .ToListAsync(ct);

        // Load off-tf candles. Each off-tf needs its own warmup window (≥ 60 off-tf bars) so
        // the regime/sub-bar feature packs have enough context at the first candidate candle.
        var offTfCandles = new Dictionary<string, IReadOnlyList<HistoricalCandle>>();
        foreach (var otherTf in SupportedSymbols.Intervals)
        {
            if (otherTf == interval) continue;
            var offIntervalMs = BacktestRunner.PublicIntervalMs(otherTf);
            var offWarmupMs = Math.Max(warmupBuffer, 60L * offIntervalMs);
            var offFetchStart = range.Min - offWarmupMs;

            var offCandles = await db.HistoricalCandles
                .AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Interval == otherTf &&
                            c.OpenTime >= offFetchStart && c.OpenTime <= range.Max)
                .OrderBy(c => c.OpenTime)
                .ToListAsync(ct);
            offTfCandles[otherTf] = offCandles;
        }

        // Build a DB-only in-memory provider and a temporary BacktestRunner that uses it.
        // This runner produces identical replay results to the scoped one but makes zero network
        // calls — all data is served from the pre-loaded lists above.
        var dbOnlyProvider = new CacheOnlyCandleProvider(symbol, interval, targetCandles, offTfCandles);
        var runner = new BacktestRunner(executor, dbOnlyProvider, NullLogger<BacktestRunner>.Instance);

        // ReplayDirectionsAsync is the pure leakage-free per-candle prediction replay — reuse it
        // so candidate precompute stays in sync with the backtest path.
        var replayPoints = await runner.ReplayDirectionsAsync(
            flow, trainedStateJson, tenantId, modelId,
            symbol, interval, startMs: range.Min, endMs: range.Max,
            ct: ct, horizonSteps: 2);

        if (replayPoints.Count == 0)
            return (Array.Empty<BetCandidate>(), 0.0);

        // Map ReplayPoint → BetCandidate by fetching the anti-look-ahead venue price for each.
        var candidates = new List<BetCandidate>(replayPoints.Count);
        var syntheticCount = 0;

        foreach (var rp in replayPoints)
        {
            // Drop abstentions — no information content.
            if (rp.PUp == 0.5m) continue;

            Domain.Markets.EntryQuote entry;
            try
            {
                entry = await venuePriceStore.EnsureEntryAsync(
                    "polymarket", symbol, interval, rp.TargetOpenTime, rp.PUp, ct);
            }
            catch
            {
                // Venue price fetch failed — synthesise in-memory without persisting.
                var rawYes = 0.5m + (rp.PUp - 0.5m) * 0.8m;
                var synYes = Math.Max(0.02m, Math.Min(0.98m, rawYes));
                entry = new Domain.Markets.EntryQuote(synYes, 1m - synYes, true, null);
            }

            if (entry.Synthetic) syntheticCount++;

            var outcomeUp = rp.ActualClose > rp.AnchorClose;

            candidates.Add(new BetCandidate(
                TargetOpenTime: rp.TargetOpenTime,
                PUp: rp.PUp,
                YesPrice: entry.YesPrice,
                NoPrice: entry.NoPrice,
                Synthetic: entry.Synthetic,
                OutcomeUp: outcomeUp));
        }

        var synFrac = candidates.Count == 0 ? 0.0 : (double)syntheticCount / candidates.Count;
        return (candidates.ToArray(), synFrac);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // QUERY METHODS
    // ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ChaosRun>> GetBatchAsync(Guid batchId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        return await _db.ChaosRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.BatchId == batchId)
            // Pass rows first, then by P50 profit desc (best-to-worst ranking).
            .OrderByDescending(r => r.Pass)
            .ThenByDescending(r => r.ProfitP50)
            .ToListAsync(ct);
    }

    public async Task<ChaosRun?> GetAsync(Guid id, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        return await _db.ChaosRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, ct);
    }

    public async Task<IReadOnlyList<ChaosSample>> GetSamplesAsync(Guid id, int take, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        // Ownership check via the parent ChaosRun row.
        var owned = await _db.ChaosRuns.AsNoTracking()
            .AnyAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (!owned) return Array.Empty<ChaosSample>();

        return await _db.ChaosSamples.AsNoTracking()
            .Where(s => s.ChaosRunId == id)
            .OrderBy(s => s.StartMs)
            .Take(take)
            .ToListAsync(ct);
    }
}

/// <summary>
/// Pure in-memory <see cref="IHistoricalCandleProvider"/> for the chaos precompute path.
/// All candles are pre-loaded from the DB cache before construction; every <see cref="GetRangeAsync"/>
/// call is a binary-range scan over sorted in-memory lists — zero network calls.
///
/// This replaces the Binance-backed adapter in the chaos replay path. The Binance adapter has a
/// freshness re-fetch on the live edge and will backfill any uncached off-tf range from Binance,
/// producing hundreds of REST pages when the chaos window spans the full cached history. The
/// DB-only provider deliberately omits any network fallback: the chaos pool is defined as "what is
/// already cached" so backfilling is both incorrect (non-deterministic — data arrives mid-run) and
/// catastrophically slow.
/// </summary>
internal sealed class CacheOnlyCandleProvider : IHistoricalCandleProvider
{
    private readonly string _targetInterval;
    private readonly IReadOnlyList<HistoricalCandle> _targetCandles;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<HistoricalCandle>> _offTfCandles;

    public CacheOnlyCandleProvider(
        string symbol,
        string interval,
        IReadOnlyList<HistoricalCandle> targetCandles,
        IReadOnlyDictionary<string, IReadOnlyList<HistoricalCandle>> offTfCandles)
    {
        _ = symbol; // validated by caller; symbol filtering happens in SortedRange via the passed parameter
        _targetInterval = interval;
        _targetCandles  = targetCandles;
        _offTfCandles   = offTfCandles;
    }

    public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        IReadOnlyList<HistoricalCandle> pool;
        if (interval == _targetInterval)
        {
            pool = _targetCandles;
        }
        else if (!_offTfCandles.TryGetValue(interval, out pool!))
        {
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
        }

        // Binary-range scan identical to BacktestRunner.SortedRange — O(log n + window).
        var result = BacktestRunner.SortedRange(pool, symbol, startMs, endMs);
        return Task.FromResult<IReadOnlyList<HistoricalCandle>>(result);
    }
}

/// <summary>
/// Adapts a precomputed stake array (evaluated async before the sync chaos loop) behind the
/// <see cref="IStakingStrategy"/> interface. Used by <see cref="ChaosService"/> for custom DAG
/// strategies: the stakes are pre-evaluated once per candidate index and served synchronously
/// inside <see cref="ChaosRunner.ReplayWindow"/> without breaking its pure/deterministic contract.
///
/// IMPORTANT: The stake returned here is the EDGE-BASED size for candidate i — it was evaluated with
/// the initial balance and initial bet size as context (not the running window balance). This is
/// consistent with how edge-aware strategies like EdgeAwareKellyStakingStrategy work: they size
/// against the edge (pUp, price) rather than bet history. Path-dependent strategies (Martingale)
/// should NOT be implemented as DAG strategies since the precomputed array cannot capture the
/// evolving window state. By convention, DAG strategies are edge-based.
///
/// One instance is created per <see cref="ChaosRunner.ReplayWindow"/> call. The instance starts
/// at the window's <paramref name="startOffset"/> (same index into the candidates array) and
/// advances sequentially, so the call index is always aligned with the candidate index the window
/// is currently processing.
/// </summary>
internal sealed class PrecomputedStakesStrategy : IStakingStrategy
{
    private readonly decimal[] _stakes;
    private int _nextIndex;

    public string Id { get; }
    public string Name => $"DAG({Id})";
    public string Description => "Custom DAG strategy with precomputed stakes.";
    public bool RequiresEdgeInputs => true;

    public PrecomputedStakesStrategy(string strategyId, decimal[] stakes, int startOffset)
    {
        Id = strategyId;
        _stakes = stakes;
        _nextIndex = startOffset;
    }

    public decimal NextBetSize(StrategyStep step)
    {
        // ReplayWindow iterates i = start; i < start+windowLen. We advance _nextIndex in lockstep.
        // If the index overflows (bounded window is always within array; guard for safety), skip.
        if (_nextIndex >= _stakes.Length) return 0m;
        return _stakes[_nextIndex++];
    }
}
