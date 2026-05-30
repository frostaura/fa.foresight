using System.Text.Json;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Domain.Paper;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Paper;

/// <summary>
/// Server-side paper-trading lifecycle: start, stop, query, and the per-tick engine that settles
/// closed-candle bets and opens new ones. The engine logic is identical to what previously ran
/// client-side (Martingale + strict-1-position + bust-on-escalation) but is now authoritative and
/// runs on a `PaperTradingProcessorService` background loop so trades keep progressing while the
/// browser is closed. Sessions live until the user clicks Stop or escalation breaches bankroll.
/// </summary>
public interface IPaperTradingService
{
    Task<PaperSession> StartAsync(string symbol, string interval, decimal initialBalance, decimal initialBetSize, string? strategyId, bool gated, CancellationToken ct, string label = "");
    Task<PaperSession?> StopAsync(string symbol, string interval, CancellationToken ct, string label = "");
    Task<PaperSession?> GetAsync(string symbol, string interval, CancellationToken ct);
    /// <summary>Fetch one session by its id (with bets). Used by the processor to drive every active
    /// session independently — including non-primary labelled sessions on the same (symbol, interval).</summary>
    Task<PaperSession?> GetByIdAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<PaperSession>> ListAsync(CancellationToken ct);
    /// <summary>Run one engine tick for the given (symbol, interval): settle matured bets, then
    /// open the bet for the currently-forming candle if a prediction is available and no bet is
    /// already in flight. Idempotent — calling repeatedly within the same candle is safe.</summary>
    Task<PaperSession?> ProcessAsync(PaperSession session, CancellationToken ct);
}

public sealed class PaperTradingService : IPaperTradingService
{
    // Late-placement headroom (iter-3). After a prediction outage — API restart, OpenRouter
    // 500s, gap-filler catching up — the first fresh prediction for the *currently-forming*
    // candle commonly lands many seconds (sometimes tens of seconds) past the open. Previously
    // we capped placement at +10s and dropped everything past that, meaning a transient outage
    // cost an entire candle's worth of trading. We now place at any point during the candle's
    // lifetime that still leaves this much headroom before it closes — the buffer prevents a
    // placement from racing settlement on the same candle. CurrentBetSize is persisted across
    // restarts so the resumed bet carries the correct Martingale escalation level.
    private const long PlacementSafetyBufferMs = 5_000;
    // Gap recovery. When the processor missed candles (downtime, a restart, or the lag between an
    // export and its import on a fresh prod box), every missed CLOSED candle is reconstructed —
    // placed and settled against the actual Binance close, exactly as the live path would have — so
    // the ledger, balances, escalation level and chart read as if the session never stopped.
    //   • MaxBackfillPerTick bounds how many closed candles one 3s tick reconstructs, so a big gap
    //     is caught up over several ticks instead of one long-blocking pass (and Binance fetches stay
    //     gentle). The session resumes normal forming-candle placement the moment it's caught up.
    //   • MaxBackfillLookbackCandles bounds how far back we ever reconstruct, matching the
    //     deterministic prediction-replay cap so a long-dormant import can't replay indefinitely.
    private const int MaxBackfillPerTick = 30;
    private const int MaxBackfillLookbackCandles = 1000;
    // Iter-3 abstain band was originally ±0.10 around 0.5 to skip the middle-bucket noise. In
    // practice that ate every Martingale candle in long stretches where the new orthogonal-signal
    // prompt centred most calls in [0.40, 0.60] — the session sat idle for 12+ minutes at a time,
    // breaking the doubling-strategy rhythm the paper engine is built around. Disabled by the
    // founder per-session feedback: continuous placement is the desired behaviour; calibration
    // data still lands in live_predictions for offline analysis.

    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly BinanceMarketDataClient _binance;
    private readonly IPaperTradingEventHub _events;
    private readonly ICalibrationRescaler _calibration;
    private readonly IVenuePriceStore _venuePrices;
    private readonly Live.TradingNotifier _notifier;
    private readonly IStrategyEvaluator _strategyEvaluator;
    private readonly Live.ILivePredictionService _predictions;
    private readonly ILogger<PaperTradingService> _logger;
    private static readonly bool Disable5m = string.Equals(
        Environment.GetEnvironmentVariable("FORESIGHT_5M_PAPER_DISABLED"), "true",
        StringComparison.OrdinalIgnoreCase);

    public PaperTradingService(
        ForesightDbContext db,
        ITenantContext tenant,
        BinanceMarketDataClient binance,
        IPaperTradingEventHub events,
        ICalibrationRescaler calibration,
        IVenuePriceStore venuePrices,
        Live.TradingNotifier notifier,
        IStrategyEvaluator strategyEvaluator,
        Live.ILivePredictionService predictions,
        ILogger<PaperTradingService> logger)
    {
        _db = db;
        _tenant = tenant;
        _binance = binance;
        _events = events;
        _calibration = calibration;
        _venuePrices = venuePrices;
        _notifier = notifier;
        _strategyEvaluator = strategyEvaluator;
        _predictions = predictions;
        _logger = logger;
    }

    public async Task<PaperSession> StartAsync(string symbol, string interval, decimal initialBalance, decimal initialBetSize, string? strategyId, bool gated, CancellationToken ct, string label = "")
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant not resolved.");
        if (initialBalance <= 0) throw new ArgumentException("initialBalance must be > 0");
        if (initialBetSize <= 0 || initialBetSize > initialBalance)
            throw new ArgumentException("initialBetSize must be > 0 and <= initialBalance");

        // Resolve and persist the strategy id. Built-in ids are validated against the catalogue
        // and collapsed to the default on unknown; custom DAG strategy Guids are accepted as-is
        // (the evaluator will return 0 / log a warning if the row is missing at placement time).
        string resolvedStrategy;
        if (!string.IsNullOrWhiteSpace(strategyId) && Guid.TryParse(strategyId, out _) && !StakingStrategies.IsKnown(strategyId))
            resolvedStrategy = strategyId!;   // custom DAG strategy — persist Guid as-is
        else
            resolvedStrategy = StakingStrategies.Resolve(strategyId).Id;
        label ??= "";

        // Compute the config hash (same algorithm as LiveSessionEngine, venue always "polymarket").
        var configHash = LiveSessionEngine.ComputeConfigHash("polymarket", symbol, interval, resolvedStrategy, initialBalance, initialBetSize);

        // Cross-mode dedup: reject if an active live session or another active paper session has
        // the same config hash. This prevents running equivalent paper + live sessions concurrently
        // (which would produce misleading P&L comparison data) and guards against double-start.
        var liveConflict = await _db.LiveSessions
            .AnyAsync(s => s.TenantId == _tenant.TenantId!.Value && s.ConfigHash == configHash && s.StoppedAt == null, ct);
        if (liveConflict)
            throw new InvalidOperationException($"An active live session with this configuration already exists (config_hash={configHash}). Stop it before starting a paper session with the same settings. [409]");

        var paperHashConflict = await _db.PaperSessions
            .AnyAsync(s => s.TenantId == _tenant.TenantId!.Value && s.ConfigHash == configHash && s.StoppedAt == null, ct);
        if (paperHashConflict)
            throw new InvalidOperationException($"An active paper session with this configuration already exists (config_hash={configHash}). Stop it first. [409]");

        // Enforce the partial-unique-index contract (now keyed by label) at the API boundary too, so a
        // duplicate Start returns a clean error rather than a Postgres unique-violation 500. Different
        // labels on the same market are allowed (parallel comparison); same label collides.
        var existingActive = await _db.PaperSessions
            .Where(s => s.TenantId == _tenant.TenantId!.Value && s.Symbol == symbol && s.Interval == interval && s.Label == label && s.StoppedAt == null)
            .FirstOrDefaultAsync(ct);
        if (existingActive is not null)
            throw new InvalidOperationException($"An active paper session already exists for {symbol} {interval}{(label == "" ? "" : $" [{label}]")}; stop it first.");

        var session = new PaperSession
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId!.Value,
            Symbol = symbol,
            Interval = interval,
            Label = label,
            StartedAt = DateTimeOffset.UtcNow,
            InitialBalance = initialBalance,
            InitialBetSize = initialBetSize,
            StrategyId = resolvedStrategy,
            Gated = gated,
            ConfigHash = configHash,
            CurrentBalance = initialBalance,
            CurrentBetSize = initialBetSize,
            Bust = false
        };
        _db.PaperSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionStarted, session, null));
        return session;
    }

    public async Task<PaperSession?> StopAsync(string symbol, string interval, CancellationToken ct, string label = "")
    {
        if (!_tenant.IsResolved) return null;
        label ??= "";
        var session = await _db.PaperSessions
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.TenantId == _tenant.TenantId!.Value && s.Symbol == symbol && s.Interval == interval && s.Label == label && s.StoppedAt == null, ct);
        if (session is null) return null;
        session.StoppedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionStopped, session, null));
        return session;
    }

    // The chart UI drives the PRIMARY session (Label=''). Labelled bot-comparison sessions on the same
    // market are reached by id via GetByIdAsync (the processor) or listed via ListAsync.
    public async Task<PaperSession?> GetAsync(string symbol, string interval, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return null;
        return await _db.PaperSessions
            .AsNoTracking()
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.TenantId == _tenant.TenantId!.Value && s.Symbol == symbol && s.Interval == interval && s.Label == "" && s.StoppedAt == null, ct);
    }

    public async Task<PaperSession?> GetByIdAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return null;
        return await _db.PaperSessions
            .AsNoTracking()
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.TenantId == _tenant.TenantId!.Value && s.Id == sessionId && s.StoppedAt == null, ct);
    }

    public async Task<IReadOnlyList<PaperSession>> ListAsync(CancellationToken ct)
    {
        if (!_tenant.IsResolved) return Array.Empty<PaperSession>();
        return await _db.PaperSessions
            .AsNoTracking()
            .Include(s => s.Bets)
            .Where(s => s.TenantId == _tenant.TenantId!.Value && s.StoppedAt == null)
            .ToListAsync(ct);
    }

    public async Task<PaperSession?> ProcessAsync(PaperSession session, CancellationToken ct)
    {
        if (session.StoppedAt is not null || session.Bust) return session;

        // Reload tracked so we can mutate bet rows below.
        var tracked = await _db.PaperSessions
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.Id == session.Id, ct);
        if (tracked is null) return null;
        if (tracked.StoppedAt is not null || tracked.Bust) return tracked;

        var intervalMs = BinanceMarketDataClient.IntervalMs(tracked.Interval);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentCandleOpenMs = nowMs / intervalMs * intervalMs;

        // ── Settle any in-flight bet whose candle has closed ──
        // Strict 1-at-a-time: there's only ever one unresolved bet.
        var openBet = tracked.Bets.FirstOrDefault(b => !b.Resolved);
        if (openBet is not null && openBet.TargetOpenTime + intervalMs <= nowMs)
        {
            if (await SettleBetAsync(tracked, openBet, intervalMs, backfill: false, ct))
                openBet = null;
        }

        // ── Catch-up + placement ──
        // The engine used to place ONLY on the currently-forming candle, so any candle the processor
        // missed (downtime, a restart, or the lag between an export and its import on a fresh prod
        // box) left a permanent hole: no bet, no ledger/chart entry, and a broken Martingale chain.
        // We now walk every candle slot from the last bet forward to the forming candle. Closed slots
        // are BACKFILLED — placed AND settled in the same pass against the actual Binance close,
        // exactly as the live path would have — so the ledger, balances, escalation level and chart
        // all read as if the session never went down. The forming candle is placed and left open, as
        // before, for the next tick to settle once it closes.
        if (!tracked.Bust && tracked.StoppedAt is null && openBet is null)
        {
            var lastTarget = tracked.Bets.Count > 0 ? tracked.Bets.Max(b => b.TargetOpenTime) : (long?)null;
            // Catch-up begins at the candle after the last bet — or, for a session with no bets yet,
            // just the forming candle (no pre-start backfill). Never start before the session itself.
            var sessionStartSlot = tracked.StartedAt.ToUnixTimeMilliseconds() / intervalMs * intervalMs;
            var firstSlot = lastTarget.HasValue ? lastTarget.Value + intervalMs : currentCandleOpenMs;
            if (firstSlot < sessionStartSlot) firstSlot = sessionStartSlot;
            if (firstSlot > currentCandleOpenMs) firstSlot = currentCandleOpenMs;

            // Bound how far back we ever reconstruct so a long-dormant import can't replay forever
            // (the deterministic prediction replay is itself capped at 1000 candles).
            var oldestAllowed = currentCandleOpenMs - (long)MaxBackfillLookbackCandles * intervalMs;
            if (firstSlot < oldestAllowed)
            {
                _logger.LogWarning(
                    "Paper session {SessionId}: gap exceeds {Max} candles — backfilling only the most recent window; older missed candles are skipped.",
                    tracked.Id, MaxBackfillLookbackCandles);
                firstSlot = oldestAllowed;
            }

            var closedSlotsBehind = (int)((currentCandleOpenMs - firstSlot) / intervalMs);
            if (closedSlotsBehind > 0)
            {
                // Reconstruct the missing predictions for the gap deterministically (leakage-free
                // model replay). Idempotent, and a no-op for non-backtestable (LLM) models whose
                // historical calls can't be honestly reconstructed — those slots are simply skipped.
                try
                {
                    await _predictions.BackfillHistoryAsync(tracked.Symbol, tracked.Interval, closedSlotsBehind + 1, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Prediction backfill failed for session {SessionId}; will retry next tick", tracked.Id);
                }
            }

            var backfilledThisTick = 0;
            for (var slot = firstSlot; slot <= currentCandleOpenMs; slot += intervalMs)
            {
                if (tracked.Bust || tracked.StoppedAt is not null) break;
                var isForming = slot == currentCandleOpenMs;

                if (isForming)
                {
                    // Same late-placement guard as before: only open the forming candle while there's
                    // still headroom before it closes. PlacedAt stays anchored to the candle's open so
                    // the ledger always shows the round boundary.
                    var msIntoCandle = nowMs - currentCandleOpenMs;
                    if (msIntoCandle >= intervalMs - PlacementSafetyBufferMs) break;
                }
                else
                {
                    // Bound per-tick reconstruction work; the next 3s tick continues the catch-up.
                    if (backfilledThisTick >= MaxBackfillPerTick) break;
                }

                var placed = await TryPlaceForCandleAsync(tracked, slot, intervalMs, backfill: !isForming, ct);
                if (placed is null) continue;

                if (!isForming)
                {
                    // Closed candle → settle immediately so the balance + escalation chain forward
                    // before the next slot is sized. If the candle isn't settleable yet (Binance feed
                    // lag), stop the catch-up so we never leave two unresolved bets open at once.
                    if (!await SettleBetAsync(tracked, placed, intervalMs, backfill: true, ct)) break;
                    backfilledThisTick++;
                }
                // The forming-candle bet stays open; the next tick settles it once its candle closes.
            }
        }

        tracked.LastProcessedAt = DateTimeOffset.UtcNow;
        try
        {
            // Heartbeat-only save — concurrency loss here is benign (a parallel HTTP read holding
            // the row, or a stale tracker after an HTTP DELETE). Settlement and placement happened
            // earlier with their own SaveChanges and must still throw; only the LastProcessedAt
            // timestamp is best-effort. Without this guard, one DbUpdateConcurrencyException kills
            // the entire tick and the session goes silent until the next successful loop.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogDebug(ex, "Heartbeat save lost concurrency check for session {SessionId}; ignoring.", tracked.Id);
        }
        return tracked;
    }

    /// <summary>
    /// Settle one closed bet against the canonical Binance close for its target candle. Updates the
    /// balance, escalation reference, zero-crossing / peak-borrowed stats, and bust state. Returns
    /// true when the bet was settled (its slot is now free); false when the candle isn't available
    /// yet (rare feed lag) so the caller can retry next tick.
    ///
    /// <paramref name="backfill"/> marks the emitted events as reconstructed so the Telegram per-bet
    /// notifier skips them — a catch-up over many candles must not blast one photo per bet — while the
    /// SSE chart stream still receives them and patches in the dots + balance line.
    /// </summary>
    private async Task<bool> SettleBetAsync(PaperSession tracked, PaperBet openBet, long intervalMs, bool backfill, CancellationToken ct)
    {
        // Use the canonical Binance close for the target candle. Pulled fresh rather than cached so a
        // delayed processor (laptop sleep, restart) settles against the truth, not a stale snapshot.
        var candles = await _binance.GetKlinesAsync(tracked.Symbol, tracked.Interval, 5, ct);
        var candle = candles.FirstOrDefault(c => c.OpenTime == openBet.TargetOpenTime);
        // After a longer outage the target candle is older than the last 5, so the quick fetch misses
        // it and the bet would never settle — freezing placement forever. Fall back to a precise
        // historical range fetch for that exact candle so stale bets ALWAYS back-resolve on start-up,
        // no matter how long the processor (or the source instance, before an import) was down.
        if (candle is null)
        {
            var exact = await _binance.GetKlinesRangeAsync(
                tracked.Symbol, tracked.Interval,
                openBet.TargetOpenTime, openBet.TargetOpenTime + intervalMs - 1, limit: 2, ct);
            candle = exact.FirstOrDefault(c => c.OpenTime == openBet.TargetOpenTime);
            if (candle is not null)
                _logger.LogInformation(
                    "Back-resolving stale paper bet {Id} (target {Target}) via historical fetch after downtime",
                    openBet.Id, openBet.TargetOpenTime);
        }
        if (candle is null)
        {
            // Binance hasn't published the candle yet (rare; usually <1s lag). Skip — caller retries.
            _logger.LogDebug("Bet {Id} target candle {Target} not yet in Binance feed; will retry", openBet.Id, openBet.TargetOpenTime);
            return false;
        }

        var actualClose = candle.Close;
        var balanceBeforeSettle = tracked.CurrentBalance;
        // Settlement goes through the shared StakingEngine with odds-based payoff. entryPrice was set
        // at placement time (bet.EntryPrice); outcomeUp is the target candle's OWN-BODY direction
        // (close > open), matching how Polymarket settles its BTC up/down market. AnchorClose stays
        // for display (change%/band) only. Side was decided at placement.
        var entryPrice = openBet.EntryPrice ?? 0.5m;
        var outcomeUp = candle.Close > candle.Open;
        var step = StakingEngine.Settle(
            side: openBet.Side,
            entryPrice: entryPrice,
            stake: openBet.Size,
            currentBalance: balanceBeforeSettle,
            outcomeUp: outcomeUp,
            allowBorrow: false);
        tracked.CurrentBalance = step.BalanceAfter;
        // Next bet size is sized at the NEXT placement; keep CurrentBetSize reflecting the last stake.
        tracked.CurrentBetSize = openBet.Size;
        // Zero-crossings + peak-borrowed tracking (iter-4). With the live bust check active the
        // balance can't actually go negative, so PeakBorrowed almost always stays 0 — wiring it the
        // same way as backtests keeps the schema and UI parity clean across both surfaces.
        if (step.CrossedZero) tracked.ZeroCrossingsCount++;
        if (tracked.CurrentBalance < 0)
            tracked.PeakBorrowed = Math.Max(tracked.PeakBorrowed, Math.Abs(tracked.CurrentBalance));
        openBet.Resolved = true;
        openBet.Outcome = step.Won ? "win" : "loss";
        openBet.Payout = step.Payout;
        openBet.Shares = step.Shares;
        openBet.BalanceAfter = tracked.CurrentBalance;
        openBet.ResolvedAt = DateTimeOffset.FromUnixTimeMilliseconds(openBet.TargetOpenTime + intervalMs);
        openBet.ActualClose = actualClose;
        if (tracked.CurrentBalance <= 0)
        {
            tracked.Bust = true;
            tracked.StoppedAt = DateTimeOffset.UtcNow;
        }
        var settled = false;
        try
        {
            await _db.SaveChangesAsync(ct);
            settled = true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Settlement lost a concurrency race — leave openBet pending so the next tick reloads
            // fresh state and re-settles. Do NOT bubble; bubbling drops the whole tick AND the
            // placement step that follows, which is what froze the session for 90 min historically.
            _logger.LogWarning(ex, "Settlement concurrency loss for bet {Id} — will retry next tick", openBet.Id);
        }
        if (settled)
        {
            // The BetResolved event drives the Telegram per-bet chart photo (live bets only — the
            // widget service skips backfilled events) and the SSE chart patch. We don't fire a
            // separate text card here.
            _events.Publish(new PaperTradingEvent(PaperTradingEventKind.BetResolved, tracked, openBet, backfill));
            if (tracked.Bust)
            {
                _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionBust, tracked, null, backfill));
                await _notifier.NotifySessionBustAsync(tracked.TenantId, tracked.Id, "paper", tracked.CurrentBalance, ct);
            }
        }
        return settled;
    }

    /// <summary>
    /// Place a single bet for <paramref name="candleOpenMs"/> using the SAME calibration, confidence
    /// gate, fixed-fee entry and strategy-sizing path as live placement — so a reconstructed
    /// (backfilled) bet is byte-for-byte the bet the live engine would have written. Returns the
    /// placed bet, or null when the slot is skipped (already bet, no prediction, gated out,
    /// 5m-disabled, zero stake, or a placement race). A broken strategy stops the session and
    /// returns null. Escalation bust is handled here too (sizing exceeds bankroll).
    /// </summary>
    private async Task<PaperBet?> TryPlaceForCandleAsync(PaperSession tracked, long candleOpenMs, long intervalMs, bool backfill, CancellationToken ct)
    {
        if (tracked.Bets.Any(b => b.TargetOpenTime == candleOpenMs)) return null;

        var pred = await _db.LivePredictions.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.TenantId == tracked.TenantId &&
                p.Symbol == tracked.Symbol &&
                p.Interval == tracked.Interval &&
                p.TargetOpenTime == candleOpenMs, ct);
        if (pred is null) return null;

        // 5m staking pause (iter-3). Predictions keep flowing and logging — the env-gated guard
        // short-circuits placement only, so the dataset continues to grow against actual outcomes.
        if (Disable5m && tracked.Interval == "5m")
        {
            _logger.LogDebug("5m paper-trading disabled by env flag — skipping placement for session {SessionId}", tracked.Id);
            return null;
        }

        // Calibrated probability drives side selection and the gate; the abstain-on-low-conviction
        // skip is gone (see the removed AbstainHalfWidth note above).
        var calibratedPUp = await _calibration.RescaleAsync(tracked.TenantId, tracked.Interval, pred.DirectionUpProbability, ct);
        // Confidence gate (opt-in safety mode). On a gated session, skip placing when the calibrated
        // prob sits in the ±2pp no-bet band — the SAME equation the backtest gate + chart GATE use.
        if (tracked.Gated && StakingEngine.IsNoBet(calibratedPUp, StakingEngine.DefaultNoBetBand))
        {
            _logger.LogDebug("Gated session {SessionId}: no bet on candle {Tgt} — calibrated pUp {PUp} inside the no-bet band", tracked.Id, candleOpenMs, calibratedPUp);
            return null;
        }

        // ── Odds-based placement ──
        // Fetch the fixed-fee entry quote (anti-look-ahead) for this candle.
        var entryQuote = await _venuePrices.EnsureEntryAsync(
            "polymarket", tracked.Symbol, tracked.Interval, candleOpenMs, calibratedPUp, ct);

        var side = StakingEngine.DecideSide(calibratedPUp);
        var entryInputs = new StakingInputs(calibratedPUp, entryQuote.YesPrice, entryQuote.NoPrice);

        // Derive lastStake and lastWon from the most-recently settled bet so escalation chains
        // correctly — including across a backfill, where each reconstructed bet is settled before
        // the next slot is sized.
        var lastSettled = tracked.Bets.Where(b => b.Resolved).OrderByDescending(b => b.TargetOpenTime).FirstOrDefault();
        var lastStake = lastSettled?.Size ?? tracked.InitialBetSize;
        var lastWon = lastSettled?.Outcome == "win";

        // Placement-time sizing: IStrategyEvaluator handles both built-in code strategies and custom
        // DAG strategies transparently.
        var strategyCtx = DagStakingStrategyAdapter.MakeStrategyFlowContext(
            tracked.TenantId, tracked.Id, tracked.Symbol, tracked.Interval);
        decimal nextStake;
        try
        {
            nextStake = await _strategyEvaluator.NextStakeAsync(
                tracked.StrategyId,
                new StrategyStep(lastStake, lastWon, tracked.InitialBetSize, tracked.CurrentBalance, entryInputs),
                strategyCtx, ct);
        }
        catch (StrategyEvaluationException ex)
        {
            // A BROKEN strategy must stop the session, not silently no-bet forever.
            _logger.LogError(ex, "Paper session {SessionId}: strategy {Strategy} failed to evaluate — stopping session", tracked.Id, tracked.StrategyId);
            tracked.StoppedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionStopped, tracked, null, backfill));
            await _notifier.NotifySessionErrorAsync(tracked.TenantId, tracked.Id, "paper", ex.Message, ct);
            return null;
        }

        if (nextStake <= 0m)
        {
            _logger.LogDebug("Session {SessionId}: strategy returned 0 stake — skipping candle {Tgt}", tracked.Id, candleOpenMs);
            return null;
        }

        // Escalation bust: if the sizing exceeds the bankroll end the session.
        if (nextStake > tracked.CurrentBalance)
        {
            tracked.Bust = true;
            tracked.StoppedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionBust, tracked, null, backfill));
            await _notifier.NotifySessionBustAsync(tracked.TenantId, tracked.Id, "paper", tracked.CurrentBalance, ct);
            return null;
        }

        var entryPrice = side == "UP" ? entryQuote.YesPrice : entryQuote.NoPrice;
        var shares = StakingEngine.Shares(nextStake, entryPrice);
        tracked.CurrentBetSize = nextStake;

        var notes = JsonSerializer.Serialize(new
        {
            iterationTag = "iter-3",
            action = "placed",
            backfilled = backfill,
            rawPUp = pred.DirectionUpProbability,
            calibratedPUp,
            p50 = pred.ClosePercentile50,
            anchor = pred.AnchorClose,
            decidedSide = side,
            entryPrice,
            synthetic = entryQuote.Synthetic
        });
        var bet = new PaperBet
        {
            Id = Guid.NewGuid(),
            TenantId = tracked.TenantId,
            SessionId = tracked.Id,
            TargetOpenTime = candleOpenMs,
            Side = side,
            PredictedProbUp = pred.DirectionUpProbability,
            AnchorClose = pred.AnchorClose,
            Size = nextStake,
            BalanceBefore = tracked.CurrentBalance,
            NotesJson = notes,
            EntryPrice = entryPrice,
            Shares = shares,
            Synthetic = entryQuote.Synthetic,
            MarketExternalId = entryQuote.MarketExternalId,
            // Logical entry time — the candle's open. Survives a delayed processor tick so the ledger
            // always reads as round boundaries.
            PlacedAt = DateTimeOffset.FromUnixTimeMilliseconds(candleOpenMs)
        };
        // Add via the DbSet, NOT via the navigation. The navigation path (`tracked.Bets.Add(bet)`)
        // triggers EF Core's collection-change detection in a way that issues a spurious UPDATE on the
        // parent session — observed as a persistent DbUpdateConcurrencyException ("expected 1, got 0")
        // that stalled the session for ~90 min. DbSet.Add inserts the new row cleanly; EF's
        // relationship fixup still appends it to `tracked.Bets` exactly once, so the next slot's
        // already-bet check and the SSE payload stay correct without manual duplication.
        _db.PaperBets.Add(bet);
        try
        {
            await _db.SaveChangesAsync(ct);
            _events.Publish(new PaperTradingEvent(PaperTradingEventKind.BetPlaced, tracked, bet, backfill));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Optimistic-concurrency loss (a parallel scope mutated the session between load and save
            // — e.g. an HTTP DELETE or another tick). The bet didn't insert; detach so the next tick
            // reloads fresh and retries.
            _db.Entry(bet).State = EntityState.Detached;
            _logger.LogWarning(ex, "Bet placement concurrency loss for session {SessionId} candle {Tgt} — retrying next tick", tracked.Id, candleOpenMs);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Two processor ticks racing for the same candle slot — partial unique index resolves the
            // collision; we just discard the second.
            _db.Entry(bet).State = EntityState.Detached;
            _logger.LogDebug(ex, "Bet placement race for session {SessionId} candle {Tgt}", tracked.Id, candleOpenMs);
            return null;
        }
        return bet;
    }
}
