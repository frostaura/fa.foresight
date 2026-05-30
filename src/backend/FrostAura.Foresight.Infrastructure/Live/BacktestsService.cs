using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Domain.Backtesting;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

public sealed class BacktestsService : IBacktestsService
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly BacktestRunner _runner;
    private readonly IServiceScopeFactory _scopes;
    private readonly IBacktestEventHub _events;
    private readonly ILogger<BacktestsService> _logger;

    public BacktestsService(
        ForesightDbContext db,
        ITenantContext tenant,
        BacktestRunner runner,
        IServiceScopeFactory scopes,
        IBacktestEventHub events,
        ILogger<BacktestsService> logger)
    {
        _db = db;
        _tenant = tenant;
        _runner = runner;
        _scopes = scopes;
        _events = events;
        _logger = logger;
    }

    public async Task<Backtest> RunAsync(BacktestRequest req, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;

        // Referential-integrity gates — referenced symbol + interval must be in the supported list,
        // and the range can't exceed the 365-day cap (matches the historical-cache warm window).
        if (!SupportedSymbols.IsSupportedSymbol(req.Symbol))
            throw new InvalidOperationException($"Symbol '{req.Symbol}' is not supported. Allowed: {string.Join(", ", SupportedSymbols.All)}.");
        if (!SupportedSymbols.IsSupportedInterval(req.Interval))
            throw new InvalidOperationException($"Interval '{req.Interval}' is not supported. Allowed: {string.Join(", ", SupportedSymbols.Intervals)}.");
        // 730 days = 2 years. Matches the new interval-aware default (15m → 24 months) + the
        // cache warmer window. Backtests beyond this point start hitting Binance pagination
        // costs that aren't worth the marginal statistical signal.
        const long MaxRangeMs = 730L * 86_400_000L;
        if (req.EndTime - req.StartTime > MaxRangeMs)
            throw new InvalidOperationException("Backtest range exceeds 730 days; clamp the lookback window.");
        if (req.EndTime <= req.StartTime)
            throw new InvalidOperationException("Backtest endTime must be after startTime.");
        // Allow either a known built-in id OR a Guid (custom DAG strategy from the strategies table).
        if (!StakingStrategies.IsKnown(req.StrategyId) && !Guid.TryParse(req.StrategyId, out _))
            throw new InvalidOperationException($"Staking strategy '{req.StrategyId}' is unknown. Allowed: built-in ids ({string.Join(", ", StakingStrategies.All.Select(s => s.Id))}) or a custom DAG strategy Guid.");

        var model = await _db.Models.AsNoTracking()
            .FirstOrDefaultAsync(m => (m.TenantId == tenantId || m.TenantId == null) && m.Id == req.ModelId, ct)
            ?? throw new InvalidOperationException($"Model {req.ModelId} not found.");
        if (!model.SupportsBacktesting)
            throw new InvalidOperationException($"Model '{model.Name}' does not support backtesting.");

        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, JsonOpts.Web); }
        catch (JsonException ex) { throw new InvalidOperationException($"Model definition JSON invalid: {ex.Message}"); }
        if (flow is null) throw new InvalidOperationException("Model definition is empty.");

        var backtest = new Backtest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ModelId = req.ModelId,
            Symbol = req.Symbol,
            Interval = req.Interval,
            StartTime = req.StartTime,
            EndTime = req.EndTime,
            InitialBalance = req.InitialBalance,
            InitialBetSize = req.InitialBetSize,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
            AllowBorrow = req.AllowBorrow,
            BatchId = req.BatchId,
            StrategyId = req.StrategyId,
            BatchKind = req.BatchKind,
            LookbackDay = req.LookbackDay,
            ApplyGate = req.ApplyGate,
        };
        _db.Backtests.Add(backtest);
        await _db.SaveChangesAsync(ct);

        // Fire-and-forget background execution on a fresh DI scope. Heavy runs (365-day 1m =
        // ~525k iterations / ~15 minutes) would otherwise time out the HTTP request. Frontend
        // listens to /api/backtests/{id}/stream for progress + completion events.
        var backtestId = backtest.Id;
        var modelId = model.Id;
        var initialBetSize = req.InitialBetSize;
        var initialBalance = req.InitialBalance;
        var symbol = req.Symbol;
        var interval = req.Interval;
        var startTime = req.StartTime;
        var endTime = req.EndTime;
        var allowBorrow = req.AllowBorrow;
        var strategyId = req.StrategyId;
        var applyGate = req.ApplyGate;
        var gateBand = req.GateBand;
        var trainedState = model.TrainedState ?? "{}";
        // Estimate total candles so the progress bar can render immediately (the runner doesn't know
        // the exact count until it has fetched the range). The first real Progress event corrects it.
        var estIntervalMs = interval switch { "1m" => 60_000L, "5m" => 300_000L, "15m" => 900_000L, _ => 300_000L };
        var estTotalCandles = (int)Math.Max(0, (endTime - startTime) / estIntervalMs);
        _ = Task.Run(() => ExecuteBacktestAsync(
            backtestId, modelId, tenantId, flow, trainedState,
            symbol, interval, startTime, endTime, initialBalance, initialBetSize, allowBorrow, strategyId, applyGate, gateBand, estTotalCandles), CancellationToken.None);

        return backtest;
    }

    /// <summary>
    /// Runs ONE backtest (against an already-created row) to completion: flips it to "running",
    /// streams progress, persists the outcome, and marks it complete / no-bets / failed. Owns its
    /// own DI scope so it survives the HTTP request. Used by RunAsync (fire-and-forget, one run) AND
    /// by the bust-test worker, which AWAITS it per rung so an N-day sweep runs one rung at a time
    /// instead of spawning N concurrent backtests that flood the server.
    /// </summary>
    private async Task ExecuteBacktestAsync(
        Guid backtestId, Guid modelId, Guid tenantId,
        FlowDefinition flow, string trainedState,
        string symbol, string interval, long startTime, long endTime,
        decimal initialBalance, decimal initialBetSize, bool allowBorrow, string strategyId,
        bool applyGate, decimal? gateBand, int estTotalCandles)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<BacktestRunner>();
        var hub = scope.ServiceProvider.GetRequiredService<IBacktestEventHub>();
        var ct = CancellationToken.None;

        try
        {
            // Flip to "running" now (bust-test rungs are created "queued" so the table doesn't open
            // an SSE stream for every pending rung — only the one actually executing) and announce
            // the estimated total so the progress bar renders immediately.
            var starting = await db.Backtests.FirstOrDefaultAsync(b => b.Id == backtestId, ct);
            if (starting is null) return;   // row deleted before we started (e.g. Clear all) — nothing to do
            starting.Status = "running";
            await db.SaveChangesAsync(ct);
            hub.Publish(new BacktestEvent(backtestId, BacktestEventKind.Started, 0, estTotalCandles, 0, 0, initialBalance, null, null));

            var progress = new Progress<BacktestProgress>(p =>
            {
                hub.Publish(new BacktestEvent(backtestId, BacktestEventKind.Progress,
                    p.CandlesProcessed, p.TotalCandles, p.BetsPlaced, 0, null, null, null));
            });

            // Resolve the staking strategy. Built-in ids go through the catalogue directly;
            // custom DAG strategy Guids use the DagStakingStrategyAdapter which wraps IStrategyEvaluator.
            IStakingStrategy strategy;
            if (StakingStrategies.IsKnown(strategyId))
            {
                strategy = StakingStrategies.Resolve(strategyId);
            }
            else
            {
                var evaluator = scope.ServiceProvider.GetRequiredService<IStrategyEvaluator>();
                var strategyCtx = DagStakingStrategyAdapter.MakeStrategyFlowContext(tenantId, modelId, symbol, interval);
                strategy = new DagStakingStrategyAdapter(strategyId, evaluator, strategyCtx);
            }
            var outcome = await runner.RunAsync(
                flow, trainedState, tenantId, modelId,
                symbol, interval, startTime, endTime,
                initialBalance, initialBetSize, allowBorrow, strategy, progress, ct, applyGate: applyGate, gateBand: gateBand);

            var tracked = await db.Backtests.FirstOrDefaultAsync(b => b.Id == backtestId, ct);
            if (tracked is null) return;   // row deleted mid-run (e.g. Clear all) — discard the result
            tracked.BetsPlaced = outcome.BetsPlaced;
            tracked.BetsWon = outcome.BetsWon;
            tracked.HitRate = outcome.HitRate;
            tracked.FinalBalance = outcome.FinalBalance;
            tracked.PeakBalance = outcome.PeakBalance;
            tracked.TroughBalance = outcome.TroughBalance;
            tracked.MaxDrawdown = outcome.MaxDrawdown;
            tracked.PeakBorrowed = outcome.PeakBorrowed;
            tracked.ZeroCrossingsCount = outcome.ZeroCrossingsCount;
            tracked.MaxMartingaleStep = outcome.MaxMartingaleStep;
            tracked.MarkersJson = outcome.MarkersJson;
            // A run that placed ZERO bets is not a normal "complete" — it's a no-op. Previously this
            // rendered as a colourless row with "—" hit-rate and an unchanged balance, which is
            // confusing (the v1+ofx 720d/15m case: order-flow dumps don't cover the window, so every
            // candle abstains). Mark it distinctly so the UI can show a clear reason rather than a
            // blank row. Distinguishes the intentional Flat Baseline control too — both are "no bets".
            if (outcome.BetsPlaced == 0)
            {
                tracked.Status = "no-bets";
                tracked.Error = "No bets were placed — the model abstained on every candle in this window. "
                    + "Likely causes: a baseline/constant control, or feature data (e.g. order-flow dumps) "
                    + "not yet published for the selected range/interval. Try a 5m window ending ~2 days ago.";
            }
            else
            {
                tracked.Status = "complete";
            }
            tracked.CompletedAt = DateTimeOffset.UtcNow;

            // Persist SyntheticBetFraction on the summary row.
            tracked.SyntheticBetFraction = outcome.SyntheticBetFraction;

            foreach (var bet in outcome.Bets)
            {
                db.BacktestBets.Add(new BacktestBet
                {
                    Id = bet.Id,
                    BacktestId = backtestId,
                    TargetOpenTime = bet.TargetOpenTime,
                    Side = bet.Side,
                    PUpRaw = bet.PUpRaw,
                    PUpCalibrated = bet.PUpCalibrated,
                    Size = bet.Size,
                    BalanceBefore = bet.BalanceBefore,
                    BalanceAfter = bet.BalanceAfter,
                    Won = bet.Won,
                    BorrowedShortfall = bet.BorrowedShortfall,
                    EntryPrice = bet.EntryPrice,
                    Shares = bet.Shares,
                    Payout = bet.Payout,
                    Synthetic = bet.Synthetic,
                    MarketExternalId = bet.MarketExternalId,
                });
            }
            await db.SaveChangesAsync(ct);

            var modelTracked = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
            if (modelTracked is not null)
            {
                modelTracked.BacktestAccuracy = outcome.HitRate;
                modelTracked.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            hub.Publish(new BacktestEvent(backtestId, BacktestEventKind.Completed,
                outcome.TotalCandles, outcome.TotalCandles, outcome.BetsPlaced, outcome.BetsWon,
                outcome.FinalBalance, outcome.FinalBalance, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest {Id} failed in background", backtestId);
            try
            {
                var tracked = await db.Backtests.FirstOrDefaultAsync(b => b.Id == backtestId, ct);
                if (tracked is not null)   // may have been deleted mid-run (Clear all)
                {
                    tracked.Status = "failed";
                    tracked.CompletedAt = DateTimeOffset.UtcNow;
                    tracked.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Failed to mark backtest {Id} as failed", backtestId); }

            hub.Publish(new BacktestEvent(backtestId, BacktestEventKind.Failed,
                0, 0, 0, 0, null, null, ex.Message));
        }
    }

    public async Task<IReadOnlyList<Backtest>> RunBustTestAsync(BustTestRequest req, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        if (req.MaxLookbackDays < 1) throw new InvalidOperationException("maxLookbackDays must be ≥ 1.");
        if (req.MaxLookbackDays > 365) throw new InvalidOperationException("maxLookbackDays exceeds 365; choose a smaller sweep.");

        var tenantId = _tenant.TenantId!.Value;
        if (!SupportedSymbols.IsSupportedSymbol(req.Symbol))
            throw new InvalidOperationException($"Symbol '{req.Symbol}' is not supported.");
        if (!SupportedSymbols.IsSupportedInterval(req.Interval))
            throw new InvalidOperationException($"Interval '{req.Interval}' is not supported.");
        if (!StakingStrategies.IsKnown(req.StrategyId) && !Guid.TryParse(req.StrategyId, out _))
            throw new InvalidOperationException($"Staking strategy '{req.StrategyId}' is unknown.");

        var model = await _db.Models.AsNoTracking()
            .FirstOrDefaultAsync(m => (m.TenantId == tenantId || m.TenantId == null) && m.Id == req.ModelId, ct)
            ?? throw new InvalidOperationException($"Model {req.ModelId} not found.");
        if (!model.SupportsBacktesting)
            throw new InvalidOperationException($"Model '{model.Name}' does not support backtesting.");
        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, JsonOpts.Web); }
        catch (JsonException ex) { throw new InvalidOperationException($"Model definition JSON invalid: {ex.Message}"); }
        if (flow is null) throw new InvalidOperationException("Model definition is empty.");
        var trainedState = model.TrainedState ?? "{}";

        // One shared batch id links every rung so the recent-runs UI collapses them into one row.
        var batchId = Guid.NewGuid();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long dayMs = 86_400_000L;
        var estIntervalMs = req.Interval switch { "1m" => 60_000L, "5m" => 300_000L, "15m" => 900_000L, _ => 300_000L };

        // Create all rung rows up-front as "queued" so the batch is visible immediately (and the UI
        // can collapse it). Rung k covers the last k days: window = [now − k days, now]. A SINGLE
        // background worker then runs them ONE AT A TIME, so an N-day sweep never spawns N concurrent
        // backtests (that flood was what froze the progress bar + button on a 180-day sweep).
        var created = new List<Backtest>(req.MaxLookbackDays);
        var rungs = new List<(Guid Id, long Start, long End, int Est)>(req.MaxLookbackDays);
        for (var k = 1; k <= req.MaxLookbackDays; k++)
        {
            var start = nowMs - k * dayMs;
            var bt = new Backtest
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ModelId = req.ModelId,
                Symbol = req.Symbol,
                Interval = req.Interval,
                StartTime = start,
                EndTime = nowMs,
                InitialBalance = req.InitialBalance,
                InitialBetSize = req.InitialBetSize,
                Status = "queued",
                StartedAt = DateTimeOffset.UtcNow,
                AllowBorrow = req.AllowBorrow,
                BatchId = batchId,
                StrategyId = req.StrategyId,
                BatchKind = "bust-test",
                LookbackDay = k,
            };
            _db.Backtests.Add(bt);
            created.Add(bt);
            rungs.Add((bt.Id, start, nowMs, (int)Math.Max(0, (nowMs - start) / estIntervalMs)));
        }
        await _db.SaveChangesAsync(ct);

        var modelId = req.ModelId; var symbol = req.Symbol; var interval = req.Interval;
        var ib = req.InitialBalance; var ibs = req.InitialBetSize; var ab = req.AllowBorrow; var sid = req.StrategyId;
        var maxStart = nowMs - req.MaxLookbackDays * dayMs;
        var deepEst = (int)Math.Max(0, (nowMs - maxStart) / estIntervalMs);

        if (sid == "flat")
        {
            // EFFICIENT PATH (flat staking): every rung shares the SAME causal per-candle prediction,
            // and flat even-money staking makes the bankroll path a deterministic function of the
            // win/loss sequence. So we run the model ONCE over the deepest window, then DERIVE every
            // rung by replaying the relevant suffix as cheap arithmetic — O(N) flow executions and a
            // single candle fetch, instead of N independent backtests re-fetching + re-computing
            // overlapping windows (the O(N²) blow-up that made a 180-day sweep fall over).
            _ = Task.Run(() => RunFlatBustSweepAsync(rungs.Select(r => (r.Id, r.Start)).ToList(),
                modelId, tenantId, flow, trainedState, symbol, interval, maxStart, nowMs, ib, ibs, ab, deepEst),
                CancellationToken.None);
        }
        else
        {
            // Non-flat staking is path-dependent (bet size reacts to outcomes), so each rung must be
            // run for real. Sequential to avoid the concurrent-flood. Rare path — bust test is flat.
            _ = Task.Run(async () =>
            {
                foreach (var r in rungs)
                {
                    try { await ExecuteBacktestAsync(r.Id, modelId, tenantId, flow, trainedState, symbol, interval, r.Start, r.End, ib, ibs, ab, sid, false, null, r.Est); }
                    catch (Exception ex) { _logger.LogError(ex, "Bust-test rung {Id} failed", r.Id); }
                }
            }, CancellationToken.None);
        }

        return created;
    }

    /// <summary>
    /// Flat-staking bust sweep done efficiently: ONE full-window model pass (allow-borrow so it bets
    /// every candle and never halts), then each rung is derived by replaying the suffix of that
    /// win/loss sequence under flat strict-bust. Exact (flat even-money staking is path-determined by
    /// the outcomes), and O(candles) instead of O(rungs × candles).
    /// </summary>
    private async Task RunFlatBustSweepAsync(
        List<(Guid Id, long Start)> rungs, Guid modelId, Guid tenantId,
        FlowDefinition flow, string trainedState, string symbol, string interval,
        long fullStartMs, long endMs, decimal initialBalance, decimal initialBetSize, bool allowBorrow, int estTotalCandles)
    {
        try
        {
            // Single full-window pass. allowBorrow=true so it places a bet on EVERY candle (never
            // halts), giving the canonical per-candle win/loss the rungs derive from.
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<BacktestRunner>();
                var hub = scope.ServiceProvider.GetRequiredService<IBacktestEventHub>();
                // Drive the deepest rung's progress bar off the single pass (it's the long part).
                var deepestId = rungs.OrderBy(r => r.Start).First().Id;
                var startEvtDb = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
                var deepRow = await startEvtDb.Backtests.FirstAsync(b => b.Id == deepestId);
                deepRow.Status = "running"; await startEvtDb.SaveChangesAsync();
                var progress = new Progress<BacktestProgress>(p => hub.Publish(new BacktestEvent(
                    deepestId, BacktestEventKind.Progress, p.CandlesProcessed, p.TotalCandles, p.BetsPlaced, 0, null, null, null)));
                hub.Publish(new BacktestEvent(deepestId, BacktestEventKind.Started, 0, estTotalCandles, 0, 0, initialBalance, null, null));

                var full = await runner.RunAsync(flow, trainedState, tenantId, modelId, symbol, interval,
                    fullStartMs, endMs, initialBalance, initialBetSize, allowBorrow: true,
                    StakingStrategies.Resolve("flat"), progress, CancellationToken.None);
                var fullBets = full.Bets.OrderBy(b => b.TargetOpenTime).ToList();

                // Derive + persist every rung from the shared win/loss sequence.
                foreach (var (rungId, start) in rungs.OrderBy(r => r.Start))
                    await DeriveFlatRungAsync(rungId, fullBets, start, initialBalance, initialBetSize, allowBorrow, hub);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flat bust sweep failed");
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            foreach (var (rungId, _) in rungs)
            {
                var row = await db.Backtests.FirstOrDefaultAsync(b => b.Id == rungId);
                if (row is not null && row.Status is "queued" or "running")
                { row.Status = "failed"; row.CompletedAt = DateTimeOffset.UtcNow; row.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message; }
            }
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Replays one rung's suffix of the shared win/loss sequence under flat strict-bust and
    /// persists the rung row + (capped) ledger. Pure arithmetic — no flow execution or candle fetch.</summary>
    private async Task DeriveFlatRungAsync(
        Guid rungId, List<Domain.Backtesting.BacktestBet> fullBets, long startMs,
        decimal initialBalance, decimal initialBetSize, bool allowBorrow, IBacktestEventHub hub)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var betSize = initialBetSize;
        var balance = initialBalance;
        decimal peak = initialBalance, trough = initialBalance, maxDd = 0m;
        int won = 0, placed = 0, zeroCrossings = 0;
        var derived = new List<Domain.Backtesting.BacktestBet>();
        foreach (var fb in fullBets)
        {
            if (fb.TargetOpenTime < startMs) continue;
            if (!allowBorrow && betSize > balance) { zeroCrossings = 1; break; }  // busted — halt
            var before = balance;
            var after = fb.Won ? balance + betSize : balance - betSize;
            derived.Add(new Domain.Backtesting.BacktestBet
            {
                Id = Guid.NewGuid(),
                BacktestId = rungId,
                TargetOpenTime = fb.TargetOpenTime,
                Side = fb.Side,
                PUpRaw = fb.PUpRaw,
                PUpCalibrated = null,
                Size = betSize,
                BalanceBefore = before,
                BalanceAfter = after,
                Won = fb.Won,
                BorrowedShortfall = 0m,
                // Carry odds fields from the full-pass bet (same candle, same prices).
                EntryPrice = fb.EntryPrice,
                Shares = fb.Shares,
                Payout = fb.Payout,
                Synthetic = fb.Synthetic,
                MarketExternalId = fb.MarketExternalId,
            });
            placed++; if (fb.Won) won++;
            balance = after;
            if (balance > peak) peak = balance;
            if (balance < trough) trough = balance;
            if (peak - balance > maxDd) maxDd = peak - balance;
            if (Math.Sign(before) != Math.Sign(after)) zeroCrossings++;
        }

        var row = await db.Backtests.FirstOrDefaultAsync(b => b.Id == rungId);
        if (row is null) return;   // rung deleted mid-sweep (e.g. Clear all) — skip persistence
        row.BetsPlaced = placed;
        row.BetsWon = won;
        row.HitRate = placed == 0 ? null : (decimal)won / placed;
        row.FinalBalance = balance;
        row.PeakBalance = peak;
        row.TroughBalance = trough;
        row.MaxDrawdown = maxDd;
        row.PeakBorrowed = 0m;
        row.ZeroCrossingsCount = zeroCrossings;
        row.MaxMartingaleStep = 0;
        row.Status = placed == 0 ? "no-bets" : "complete";
        if (placed == 0) row.Error = "No bets were placed in this window.";
        row.CompletedAt = DateTimeOffset.UtcNow;

        // Cap the persisted ledger to the most-recent N bets — a 180-day rung has ~50k candles and a
        // 180-rung sweep would otherwise write millions of rows + megabytes of markers. The cap keeps
        // the BUST region (strict-bust halts AT the bust, so the bust is the LAST bet → always kept).
        const int LedgerCap = 2000;
        var toPersist = derived.Count > LedgerCap ? derived.GetRange(derived.Count - LedgerCap, LedgerCap) : derived;
        row.MarkersJson = JsonSerializer.Serialize(
            toPersist.Select(b => new { t = b.TargetOpenTime, hit = b.Won, side = b.Side }), JsonOpts.Web);
        db.BacktestBets.AddRange(toPersist);
        await db.SaveChangesAsync();

        hub.Publish(new BacktestEvent(rungId, BacktestEventKind.Completed,
            placed, placed, placed, won, balance, balance, null));
    }

    public async Task<IReadOnlyList<Backtest>> GetBatchAsync(Guid batchId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var rows = await _db.Backtests.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.BatchId == batchId)
            .OrderBy(b => b.LookbackDay)
            .ToListAsync(ct);
        // Batch report shows per-rung summary only; drop the per-candle markers from the payload.
        foreach (var r in rows) r.MarkersJson = null;
        return rows;
    }

    public async Task<IReadOnlyList<Backtest>> ListAsync(Guid? modelId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var q = _db.Backtests.AsNoTracking().Where(b => b.TenantId == tenantId);
        if (modelId is not null) q = q.Where(b => b.ModelId == modelId.Value);
        // Wider take than the old 50 so a bust-test sweep (up to 365 rungs sharing one StartedAt
        // burst) doesn't push genuine run history off the list — the frontend collapses each
        // bust-test batch into a single row by BatchId. MarkersJson is NULLED here: it's per-candle
        // data the table never renders, and 180 rungs each carrying ~50k markers would bloat the
        // list response into megabytes. The run-report modal fetches per-bet detail via /bets.
        var rows = await q.OrderByDescending(b => b.StartedAt).Take(500).ToListAsync(ct);
        foreach (var r in rows) r.MarkersJson = null;
        return rows;
    }

    public async Task<Backtest?> GetAsync(Guid id, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        return await _db.Backtests.AsNoTracking()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == id, ct);
    }

    public async Task<IReadOnlyList<BacktestBet>> GetBetsAsync(Guid id, int take, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var ownership = await _db.Backtests.AsNoTracking().AnyAsync(b => b.Id == id && b.TenantId == tenantId, ct);
        if (!ownership) return Array.Empty<BacktestBet>();
        return await _db.BacktestBets.AsNoTracking()
            .Where(b => b.BacktestId == id)
            .OrderBy(b => b.TargetOpenTime)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var bt = await _db.Backtests.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct);
        if (bt is null) return false;
        _db.Backtests.Remove(bt);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ClearAsync(Guid? modelId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        // EF's ExecuteDeleteAsync emits a single DELETE that the FK cascade handles in one
        // statement — much faster than loading rows + calling Remove, and avoids the
        // multi-minute cascading per-row pattern from the earlier implementation.
        var query = _db.Backtests.Where(b => b.TenantId == tenantId);
        if (modelId is { } mid) query = query.Where(b => b.ModelId == mid);
        return await query.ExecuteDeleteAsync(ct);
    }

    private static class JsonOpts
    {
        public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    }
}

public sealed class ModelTrainingService : IModelTrainingService
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ModelTrainer _trainer;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ModelTrainingService> _logger;

    public ModelTrainingService(
        ForesightDbContext db,
        ITenantContext tenant,
        ModelTrainer trainer,
        IServiceScopeFactory scopes,
        ILogger<ModelTrainingService> logger)
    {
        _db = db;
        _tenant = tenant;
        _trainer = trainer;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Per-interval lookback (days). The shorter the candle, the shorter the wall-clock window
    /// needs to be for the same statistical power — 30d of 1m candles is 43,200 samples, far
    /// past the point where adding more rows changes the coefficients meaningfully. Same logic
    /// scales up: 5m gets 90d (~26k samples), 15m gets 365d (~35k). Total compute roughly equal
    /// across intervals so the parallel training tasks finish around the same time and no one
    /// task drags the wall-time.
    /// </summary>
    private static int LookbackDaysFor(string interval) => interval switch
    {
        "1m" => 30,
        "5m" => 90,
        "15m" => 365,
        _ => 90,
    };

    private static long IntervalMs(string interval) => interval switch
    {
        "1m" => 60_000L,
        "5m" => 300_000L,
        "15m" => 900_000L,
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };

    /// <summary>
    /// True when the flow reads order-flow microstructure — i.e. it carries a
    /// <c>source.microstructure.orderflow</c> node. Such flows need the microstructure cache hydrated
    /// (and any on-demand backfill error surfaced) before fitting; candle-only flows do not.
    /// </summary>
    internal static bool FlowUsesMicrostructure(FlowDefinition flow) =>
        flow.Nodes.Any(n => n.Type == "source.microstructure.orderflow");

    /// <summary>
    /// Pre-fetches every historical window the trainer will read for one interval, BEFORE the fit, so
    /// the trainer's own fetches hit a warm cache and produce real rows. Mirrors the trainer's window
    /// math exactly (target-tf warmup, off-tf extended warmup, micro warmup).
    ///
    /// Failure policy matches the trainer's faithfulness contract:
    /// <list type="bullet">
    ///   <item>Target-tf candles: required — let a fetch error propagate (no usable model without them).</item>
    ///   <item>Off-tf candles: best-effort — legitimately sparse for some symbols; log + continue.</item>
    ///   <item>Microstructure (only when the flow uses it): NOT swallowed — if the adapter throws its
    ///   actionable cap/availability error, it propagates and becomes the training failure, instead of
    ///   silently leaving an empty pool that the trainer would later report as zero rows.</item>
    /// </list>
    /// </summary>
    internal static async Task HydrateTrainingDataAsync(
        FlowDefinition flow,
        IHistoricalCandleProvider candleProvider,
        IHistoricalMicrostructureProvider? microProvider,
        string symbol,
        string interval,
        long startMs,
        long endMs,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var warmupMs = (long)flow.WarmupCandles * IntervalMs(interval);

        // Target-interval candles — the spine of the training window. Required.
        await candleProvider.GetRangeAsync(symbol, interval, startMs - warmupMs, endMs, ct);

        // Off-tf candles for every other supported interval (e.g. 15m regime feeding a 5m model).
        // These may legitimately be sparse — never fail the whole train on an off-tf miss.
        foreach (var otherTf in FrostAura.Foresight.Domain.MarketData.SupportedSymbols.Intervals)
        {
            if (otherTf == interval) continue;
            var offWarmupMs = Math.Max(warmupMs, 60L * IntervalMs(otherTf));
            try { await candleProvider.GetRangeAsync(symbol, otherTf, startMs - offWarmupMs, endMs, ct); }
            catch (Exception ex) { logger?.LogWarning(ex, "Off-tf hydration miss for {Symbol}/{OtherTf} (continuing)", symbol, otherTf); }
        }

        // Microstructure — only when the flow actually reads order-flow. Do NOT swallow: a cap /
        // availability error here is the REAL, actionable training failure we want surfaced.
        if (microProvider is not null && FlowUsesMicrostructure(flow))
            await microProvider.GetRangeAsync(symbol, interval, startMs - warmupMs, endMs, ct);
    }

    public async Task<ModelTrainResult> TrainAsync(Guid modelId, string symbol, int holdoutDays, string? interval, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var model = await _db.Models.FirstOrDefaultAsync(m => m.Id == modelId && (m.TenantId == tenantId || m.TenantId == null), ct)
            ?? throw new InvalidOperationException($"Model {modelId} not found.");
        if (model.IsBuiltIn) throw new InvalidOperationException("Built-in models cannot be trained.");
        if (!FrostAura.Foresight.Domain.MarketData.SupportedSymbols.IsSupportedSymbol(symbol))
            throw new InvalidOperationException($"Symbol '{symbol}' is not supported.");

        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { throw new InvalidOperationException($"Model definition JSON invalid: {ex.Message}"); }
        if (flow is null) throw new InvalidOperationException("Model definition is empty.");

        // Structural gate: only flows with a feature.matrix_builder produce trainable rows. Flows
        // that emit constants (Flat Baseline) or route through an LLM have no coefficients to fit
        // and would fall through to the trainer's row-count check with a misleading "widen the
        // date range" message. Caught here so the user gets the real reason.
        if (!flow.Nodes.Any(n => n.Type == "feature.matrix_builder"))
        {
            throw new InvalidOperationException(
                "This model has no trainable parameters — its flow emits predictions directly without a feature.matrix_builder, so there's nothing for the trainer to fit. Measure its accuracy via Backtesting against historical candles, or by accumulating live shadow predictions over time.");
        }

        // Train one variant per supported interval IN PARALLEL. Each interval is independent —
        // different candle distributions, different coefficients, no shared mutable state — so
        // wall-time collapses from sum(per-interval) to max(per-interval) (~5 min instead of ~10).
        // Each task creates its own DI scope so EF Core's non-thread-safe DbContext isn't shared
        // across tasks. The outer service's DbContext stays untouched until the merge/write at
        // the bottom of this method.
        // Single-interval fast path: train only the requested interval (merged into existing
        // variants below); null = the full 1m/5m/15m sweep.
        var allIntervals = FrostAura.Foresight.Domain.MarketData.SupportedSymbols.Intervals;
        var requested = interval is null
            ? allIntervals.ToArray()
            : allIntervals.Contains(interval)
                ? new[] { interval }
                : throw new InvalidOperationException($"Unsupported interval '{interval}'. Supported: {string.Join(", ", allIntervals)}.");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var holdoutMs = (long)Math.Max(0, holdoutDays) * 86_400_000L;
        var intervalTasks = requested
            .Select(async iv =>
            {
                var lookbackMs = (long)LookbackDaysFor(iv) * 86_400_000L;
                var endMs = nowMs - holdoutMs;
                var startMs = endMs - lookbackMs;
                await using var scope = _scopes.CreateAsyncScope();
                var trainer = scope.ServiceProvider.GetRequiredService<ModelTrainer>();
                // Pre-training data hydration: fetch (and, for on-demand adapters, backfill) the exact
                // windows the trainer will read BEFORE fitting. This turns the trainer's own fetches into
                // warm-cache hits — so a model never dies with the cryptic "Not enough training rows (0)
                // after warmup" because a swallowed download timeout left an empty pool. Critically, when
                // the flow needs order-flow microstructure we DON'T swallow the hydration error: if the
                // dump-cap / availability error fires, it propagates and becomes the (actionable) training
                // failure stored in TrainingError, instead of being masked as a zero-row count later.
                var candleProvider = scope.ServiceProvider.GetRequiredService<IHistoricalCandleProvider>();
                var microProvider = scope.ServiceProvider.GetService<IHistoricalMicrostructureProvider>();
                await HydrateTrainingDataAsync(flow, candleProvider, microProvider, symbol, iv, startMs, endMs, ct, _logger);
                _logger.LogInformation("Training variant {Symbol}/{Interval} on {Days}d", symbol, iv, LookbackDaysFor(iv));
                var result = await trainer.TrainAsync(flow, tenantId, modelId, symbol, iv, startMs, endMs, ct);
                _logger.LogInformation("Variant {Symbol}/{Interval} fit complete — WF accuracy {Pct:P2}", symbol, iv, result.ValidationAccuracy);
                return (Interval: iv, StartMs: startMs, EndMs: endMs, Json: result.TrainedStateJson, Accuracy: result.ValidationAccuracy);
            })
            .ToList();

        var results = await Task.WhenAll(intervalTasks);

        // Seed with EXISTING variants when doing a partial (single-interval) train, so retraining
        // just 5m doesn't wipe a previously-trained 1m/15m. Then override with the retrained ones.
        var variantJsons = new Dictionary<string, string>();
        if (interval is not null && !string.IsNullOrWhiteSpace(model.TrainedState))
        {
            try
            {
                using var prevDoc = JsonDocument.Parse(model.TrainedState);
                if (prevDoc.RootElement.TryGetProperty("variants", out var prevVariants) && prevVariants.ValueKind == JsonValueKind.Object)
                    foreach (var p in prevVariants.EnumerateObject())
                        variantJsons[p.Name] = p.Value.GetRawText();
            }
            catch { /* malformed previous state — fall through and write just the retrained set */ }
        }
        var variants = new List<TrainedVariantSummary>(results.Length);
        foreach (var r in results)
        {
            variantJsons[r.Interval] = r.Json;
            variants.Add(new TrainedVariantSummary(
                Interval: r.Interval,
                TrainStartMs: r.StartMs,
                TrainEndMs: r.EndMs,
                ValidationAccuracy: r.Accuracy));
        }

        // Compose the multi-variant TrainedState JSON. Shape:
        //   { "trainedAt": "...", "trainSymbol": "BTCUSDT", "variants": { "1m": {...}, "5m": {...}, "15m": {...} } }
        // The inference path peels off `variants[interval]` and remaps its modelLinearRegression /
        // modelLogisticRegression fields the same way single-interval training used to expose them.
        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        var variantBlob = "{" + string.Join(",", variantJsons.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";
        var combined = "{\"trainedAt\":\"" + nowIso + "\",\"trainSymbol\":\"" + symbol + "\",\"variants\":" + variantBlob + "}";

        model.TrainedState = combined;
        // The flat training-validation accuracy column becomes the MEAN of the per-interval WF
        // accuracies — a single summary number for any consumer that hasn't migrated to
        // per-variant reads yet. The card UI reads from `variants` directly for the per-interval
        // breakdown.
        model.TrainingValidationAccuracy = variants.Count == 0 ? null : variants.Average(v => v.ValidationAccuracy);
        model.LastTrainedAt = DateTimeOffset.UtcNow;
        // Flat fields summarize the overall training: min start, max end, primary interval (15m
        // when present, else the longest-window interval). Kept for legacy callers that read
        // these columns directly without knowing about variants.
        model.TrainStartMs = variants.Min(v => v.TrainStartMs);
        model.TrainEndMs = variants.Max(v => v.TrainEndMs);
        model.TrainSymbol = symbol;
        model.TrainInterval = variants.Any(v => v.Interval == "15m") ? "15m" : variants.Last().Interval;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new ModelTrainResult(variants, model.LastTrainedAt.Value);
    }

    public async Task StartTrainingAsync(Guid modelId, string symbol, int holdoutDays, string? interval, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var tenantSlug = _tenant.TenantSlug ?? "default";

        var model = await _db.Models.FirstOrDefaultAsync(m => m.Id == modelId && (m.TenantId == tenantId || m.TenantId == null), ct)
            ?? throw new InvalidOperationException($"Model {modelId} not found.");
        if (model.IsBuiltIn) throw new InvalidOperationException("Built-in models cannot be trained.");
        if (model.TrainingStatus == "training")
            throw new InvalidOperationException("This model is already training. Wait for the current run to finish.");
        if (!FrostAura.Foresight.Domain.MarketData.SupportedSymbols.IsSupportedSymbol(symbol))
            throw new InvalidOperationException($"Symbol '{symbol}' is not supported.");

        // Validate the flow is trainable up-front so the caller gets an immediate, real error
        // instead of a background failure surfaced minutes later via TrainingStatus='failed'.
        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { throw new InvalidOperationException($"Model definition JSON invalid: {ex.Message}"); }
        if (flow is null) throw new InvalidOperationException("Model definition is empty.");
        if (!flow.Nodes.Any(n => n.Type == "feature.matrix_builder"))
            throw new InvalidOperationException(
                "This model has no trainable parameters — its flow emits predictions directly without a feature.matrix_builder, so there's nothing for the trainer to fit. Measure its accuracy via Backtesting against historical candles, or by accumulating live shadow predictions over time.");

        model.TrainingStatus = "training";
        model.TrainingStartedAt = DateTimeOffset.UtcNow;
        model.TrainingError = null;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Fire-and-forget on a fresh DI scope with CancellationToken.None so the fit outlives the
        // HTTP request (and the browser tab) that started it. The UI watches TrainingStatus.
        _ = Task.Run(() => RunTrainingInBackgroundAsync(modelId, tenantId, tenantSlug, symbol, holdoutDays, interval), CancellationToken.None);
    }

    /// <summary>
    /// Background worker for a persistent training run. Owns its own DI scope (so EF's DbContext
    /// isn't shared with the request that started it) and resets the model's TrainingStatus on
    /// completion — null on success, "failed" + message on error — which is what lets the UI sync
    /// the outcome when the user comes back, even after closing the browser mid-train.
    /// </summary>
    private async Task RunTrainingInBackgroundAsync(
        Guid modelId, Guid tenantId, string tenantSlug, string symbol, int holdoutDays, string? interval)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        // TrainAsync reads the tenant via ITenantContext; set it in this scope since there's no
        // request middleware to populate it on a background thread.
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, tenantSlug);
        var trainingSvc = scope.ServiceProvider.GetRequiredService<IModelTrainingService>();
        try
        {
            await trainingSvc.TrainAsync(modelId, symbol, holdoutDays, interval, CancellationToken.None);
            var m = await db.Models.FirstAsync(x => x.Id == modelId);
            m.TrainingStatus = null;
            m.TrainingError = null;
            m.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training {Id} failed in background", modelId);
            try
            {
                var m = await db.Models.FirstAsync(x => x.Id == modelId);
                m.TrainingStatus = "failed";
                m.TrainingError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                m.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Failed to mark training {Id} as failed", modelId); }
        }
    }
}

/// <summary>
/// Runs the rolling-origin walk-forward evaluator for a model over a range and returns the honest
/// out-of-sample verdict. Synchronous (the caller — an iteration script — waits): each fold
/// retrains + backtests, so this can take minutes on a wide window; keep folds modest and the range
/// focused. Uses the full microstructure adapter (on-demand dump ingest is fine offline).
/// </summary>
public sealed class WalkForwardService : IWalkForwardService
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly WalkForwardEvaluator _wf;

    public WalkForwardService(ForesightDbContext db, ITenantContext tenant, WalkForwardEvaluator wf)
    {
        _db = db;
        _tenant = tenant;
        _wf = wf;
    }

    public async Task<WalkForwardReport> EvaluateAsync(
        Guid modelId, string symbol, string interval, long startMs, long endMs, int folds, CancellationToken ct, int horizonSteps = 2)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var model = await _db.Models.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == modelId && (m.TenantId == tenantId || m.TenantId == null), ct)
            ?? throw new InvalidOperationException($"Model {modelId} not found.");

        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { throw new InvalidOperationException($"Model definition JSON invalid: {ex.Message}"); }
        if (flow is null) throw new InvalidOperationException("Model definition is empty.");
        if (!flow.SupportsBacktesting) throw new InvalidOperationException("Model flow does not support backtesting.");
        if (!flow.Nodes.Any(n => n.Type == "feature.matrix_builder"))
            throw new InvalidOperationException("Walk-forward needs a trainable flow (one with a feature.matrix_builder node).");

        var result = await _wf.EvaluateAsync(flow, tenantId, modelId, symbol, interval, startMs, endMs, folds, ct, horizonSteps);
        var (pass, reason) = result.PassesGuards();
        return new WalkForwardReport(result, pass, reason);
    }
}
