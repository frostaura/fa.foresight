using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Chaos;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Chaos;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            if (!StakingStrategies.IsKnown(sid))
                throw new InvalidOperationException($"Unknown strategy '{sid}'.");

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
        var runner = scope.ServiceProvider.GetRequiredService<BacktestRunner>();
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
                        db, runner, venuePriceStore, tenantId, row.ModelId,
                        row.Symbol, row.Interval, req, ct);
                    cached = (cands, synFrac);
                    candidateCache[row.ModelId] = cached;
                    _logger.LogInformation("Chaos batch {BatchId}: model {ModelId} → {N} candidates, synthetic {S:P2}",
                        batchId, row.ModelId, cands.Length, synFrac);
                }

                var (candidates, syntheticFraction) = cached;
                var strategy = StakingStrategies.Resolve(row.StrategyId);

                // ── 2. Generate deterministic start offsets ────────────────────────────────────
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

                // ── 3. Replay windows + collect sample results ────────────────────────────────
                var sampleResults = new ChaosSampleResult[offsets.Count];
                for (var si = 0; si < offsets.Count; si++)
                {
                    sampleResults[si] = ChaosRunner.ReplayWindow(
                        candidates, offsets[si], row.WindowLength,
                        strategy, req.InitialBalance, req.InitialBetSize, row.AllowBorrow);

                    if (si % 50 == 0)
                        hub.Publish(new ChaosEvent(batchId, ChaosEventKind.Progress, comboIdx + 1, totalCombos, si, offsets.Count, null));
                }

                // ── 4. Aggregate ──────────────────────────────────────────────────────────────
                var agg = ChaosRunner.Aggregate(
                    row.ModelId, row.StrategyId, row.WindowLength,
                    sampleResults, req.InitialBalance, syntheticFraction);

                // ── 5. Persist row + capped samples ───────────────────────────────────────────
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
    /// Precomputes the BetCandidate array for a model by reusing the BacktestRunner's per-candle
    /// prediction replay (<c>ReplayCandidatesAsync</c>). The candidate array is built ONCE per model
    /// and reused across all (strategy × windowLen) combos in the matrix.
    ///
    /// Each candidate contains: pUp from the model, anti-look-ahead venue prices from
    /// <see cref="IVenuePriceStore.EnsureEntryAsync"/>, and the realised direction
    /// (outcomeUp = target.Close &gt; anchor.Close).
    ///
    /// Candidates where pUp == 0.5m (model abstained) are dropped — they carry no information and
    /// would silently be skipped by ReplayWindow anyway.
    /// </summary>
    private static async Task<(BetCandidate[] Candidates, double SyntheticFraction)> PrecomputeCandidatesAsync(
        ForesightDbContext db,
        BacktestRunner runner,
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

        // We don't have a wall-clock reference here — and we CANNOT use DateTime.UtcNow (banned in
        // the engine). Instead we pass 0..FarFutureMs; ReplayDirectionsAsync will internally fetch
        // whatever candles are stored (start=0 = "from the beginning of stored data").
        // Using long.MaxValue risks overflow inside the warmup arithmetic, so we cap at a safe
        // 100-year horizon from the Unix epoch (≈ year 2070 in ms from epoch).
        const long FarFutureMs = 100L * 365 * 86_400_000L;

        // ReplayDirectionsAsync is the pure leakage-free per-candle prediction replay already
        // present on BacktestRunner — reuse it so candidate precompute stays in sync with the
        // backtest path. We request the full stored range (0..FarFutureMs) so the candidate
        // pool is as wide as possible.
        var replayPoints = await runner.ReplayDirectionsAsync(
            flow, trainedStateJson, tenantId, modelId,
            symbol, interval, startMs: 0L, endMs: FarFutureMs,
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
