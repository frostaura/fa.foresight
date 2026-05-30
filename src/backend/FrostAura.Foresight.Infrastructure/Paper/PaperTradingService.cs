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

        // Find any unresolved bet — there's only ever one under strict 1-at-a-time.
        var openBet = tracked.Bets.FirstOrDefault(b => !b.Resolved);

        // ── Settlement ──
        if (openBet is not null)
        {
            var candleClosed = openBet.TargetOpenTime + intervalMs <= nowMs;
            if (candleClosed)
            {
                // Use the canonical Binance close for the target candle. Pulled fresh per tick
                // rather than cached so a delayed processor (laptop sleep, restart) settles against
                // the truth instead of a stale snapshot.
                var candles = await _binance.GetKlinesAsync(tracked.Symbol, tracked.Interval, 5, ct);
                var candle = candles.FirstOrDefault(c => c.OpenTime == openBet.TargetOpenTime);
                // After a longer outage (downtime spanning more than the recent window above) the
                // target candle is older than the last 5, so the quick fetch misses it and the bet
                // would never settle — freezing placement forever. Fall back to a precise historical
                // range fetch for that exact candle so stale bets ALWAYS back-resolve on start-up,
                // no matter how long the processor was down.
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
                if (candle is not null)
                {
                    var actualClose = candle.Close;
                    var balanceBeforeSettle = tracked.CurrentBalance;
                    // Settlement goes through the shared StakingEngine with odds-based payoff.
                    // entryPrice was set at placement time (bet.EntryPrice); outcomeUp is the target
                    // candle's OWN-BODY direction (close > open), matching how Polymarket settles its
                    // BTC up/down market. AnchorClose stays for display (change%/band) only. Side was
                    // decided at placement.
                    var entryPrice = openBet.EntryPrice ?? (openBet.Side == "UP" ? 0.5m : 0.5m);
                    var outcomeUp = candle.Close > candle.Open;
                    var step = StakingEngine.Settle(
                        side: openBet.Side,
                        entryPrice: entryPrice,
                        stake: openBet.Size,
                        currentBalance: balanceBeforeSettle,
                        outcomeUp: outcomeUp,
                        allowBorrow: false);
                    tracked.CurrentBalance = step.BalanceAfter;
                    // Next bet size is NOT derived at settlement — it is sized at the NEXT placement.
                    // Keep CurrentBetSize reflecting the last stake for reference; sizing happens in
                    // ProcessAsync placement block via strategy.NextBetSize.
                    tracked.CurrentBetSize = openBet.Size;
                    // Zero-crossings + peak-borrowed tracking (iter-4). With the live bust check
                    // active the balance can't actually go negative, so PeakBorrowed almost always
                    // stays 0 in live mode. Wiring it the same way as backtests keeps the schema
                    // and UI parity clean across both surfaces.
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
                        // Settlement lost a concurrency race — leave openBet pending so the next
                        // tick reloads fresh state and re-settles. Do NOT bubble; bubbling drops
                        // the whole tick AND the placement step that follows, which is what froze
                        // the session for 90 min after a restart-time race historically.
                        _logger.LogWarning(ex, "Settlement concurrency loss for bet {Id} — will retry next tick", openBet.Id);
                    }
                    if (settled)
                    {
                        // The BetResolved event drives the Telegram per-bet chart photo (chart + the
                        // same win/loss card as its caption) via TelegramChartComposer — that IS the
                        // paper bet notification now, so we don't also fire a separate text card here.
                        _events.Publish(new PaperTradingEvent(PaperTradingEventKind.BetResolved, tracked, openBet));
                        if (tracked.Bust)
                            _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionBust, tracked, null));
                        if (tracked.Bust)
                            await _notifier.NotifySessionBustAsync(tracked.TenantId, tracked.Id, "paper", tracked.CurrentBalance, ct);
                        openBet = null; // freed; placement can now consider this candle's slot
                    }
                }
                else
                {
                    // Binance hasn't published the candle yet (rare; usually <1s lag). Skip — next
                    // tick will retry.
                    _logger.LogDebug("Bet {Id} target candle {Target} not yet in Binance feed; will retry", openBet.Id, openBet.TargetOpenTime);
                }
            }
        }

        // ── Placement ──
        if (!tracked.Bust && tracked.StoppedAt is null && openBet is null)
        {
            // Place anywhere inside the candle's lifetime that still leaves a safety buffer
            // before close. After a prediction outage this is what lets the session resume on
            // the first candle that gets a fresh prediction, rather than waiting an entire
            // candle for the next clean boundary. PlacedAt stays anchored to the candle's
            // openTime so the ledger always shows the round boundary.
            var msIntoCandle = nowMs - currentCandleOpenMs;
            if (msIntoCandle < intervalMs - PlacementSafetyBufferMs)
            {
                var alreadyOnThisCandle = tracked.Bets.Any(b => b.TargetOpenTime == currentCandleOpenMs);
                if (!alreadyOnThisCandle)
                {
                    var pred = await _db.LivePredictions.AsNoTracking()
                        .FirstOrDefaultAsync(p =>
                            p.TenantId == tracked.TenantId &&
                            p.Symbol == tracked.Symbol &&
                            p.Interval == tracked.Interval &&
                            p.TargetOpenTime == currentCandleOpenMs, ct);
                    if (pred is not null)
                    {
                        // 5m staking pause (iter-3). Predictions keep flowing and logging — the
                        // env-gated guard short-circuits placement only, so the LLM dataset
                        // continues to grow against actual outcomes for later analysis.
                        if (Disable5m && tracked.Interval == "5m")
                        {
                            _logger.LogDebug("5m paper-trading disabled by env flag — skipping placement for session {SessionId}", tracked.Id);
                        }
                        else
                        {
                            // Calibrated probability is still computed for instrumentation and the
                            // side selection (calibrated curve occasionally flips on extreme tails),
                            // but the previous abstain-on-low-conviction skip is gone — see comment
                            // on the removed AbstainHalfWidth constant.
                            var calibratedPUp = await _calibration.RescaleAsync(tracked.TenantId, tracked.Interval, pred.DirectionUpProbability, ct);
                            // Confidence gate (opt-in safety mode). On a gated session, skip placing
                            // when the calibrated prob sits in the ±2pp no-bet band — the SAME equation
                            // the backtest gate + chart GATE use. The candle passes with no bet placed,
                            // so balance + bet size are untouched, exactly like an abstention. This is
                            // the "don't bet at times" branch the user toggles on to test whether the
                            // safety limitation improves P&L vs always-betting.
                            if (tracked.Gated && StakingEngine.IsNoBet(calibratedPUp, StakingEngine.DefaultNoBetBand))
                            {
                                _logger.LogDebug("Gated session {SessionId}: no bet on candle {Tgt} — calibrated pUp {PUp} inside the no-bet band", tracked.Id, currentCandleOpenMs, calibratedPUp);
                            }
                            else
                            {
                            // ── Odds-based placement ────────────────────────────────────────────
                            // Fetch entry quote (or synthesise one) at placement time.
                            var entryQuote = await _venuePrices.EnsureEntryAsync(
                                "polymarket", tracked.Symbol, tracked.Interval, currentCandleOpenMs, calibratedPUp, ct);

                            var side = StakingEngine.DecideSide(calibratedPUp);
                            var entryInputs = new StakingInputs(calibratedPUp, entryQuote.YesPrice, entryQuote.NoPrice);

                            // Derive lastStake and lastWon from the most-recently settled bet.
                            var lastSettled = tracked.Bets.Where(b => b.Resolved).OrderByDescending(b => b.TargetOpenTime).FirstOrDefault();
                            var lastStake = lastSettled?.Size ?? tracked.InitialBetSize;
                            var lastWon = lastSettled?.Outcome == "win";

                            // Placement-time sizing: use IStrategyEvaluator which handles both
                            // built-in code strategies and custom DAG strategies transparently.
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
                                _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionStopped, tracked, null));
                                await _notifier.NotifySessionErrorAsync(tracked.TenantId, tracked.Id, "paper", ex.Message, ct);
                                return tracked;
                            }

                            if (nextStake > 0m)
                            {
                                // Escalation bust: if the sizing exceeds the bankroll end the session.
                                if (nextStake > tracked.CurrentBalance)
                                {
                                    tracked.Bust = true;
                                    tracked.StoppedAt = DateTimeOffset.UtcNow;
                                    await _db.SaveChangesAsync(ct);
                                    _events.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionBust, tracked, null));
                                    await _notifier.NotifySessionBustAsync(tracked.TenantId, tracked.Id, "paper", tracked.CurrentBalance, ct);
                                }
                                else
                                {
                                    var entryPrice = side == "UP" ? entryQuote.YesPrice : entryQuote.NoPrice;
                                    var shares = StakingEngine.Shares(nextStake, entryPrice);
                                    tracked.CurrentBetSize = nextStake;

                                    var notes = JsonSerializer.Serialize(new
                                    {
                                        iterationTag = "iter-3",
                                        action = "placed",
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
                                        TargetOpenTime = currentCandleOpenMs,
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
                                        // Logical entry time — the candle's open. Survives a delayed
                                        // processor tick so the ledger always reads as round boundaries.
                                        PlacedAt = DateTimeOffset.FromUnixTimeMilliseconds(currentCandleOpenMs)
                                    };
                                    // Add via the DbSet, NOT via the navigation. The navigation path
                                    // (`tracked.Bets.Add(bet)`) was historically chosen to dedupe with
                                    // explicit `_db.PaperBets.Add(bet)`, but on its own it triggers EF
                                    // Core's collection-change detection in a way that issues a spurious
                                    // UPDATE on the parent session — observed as a persistent
                                    // DbUpdateConcurrencyException ("expected 1, got 0") that stalled the
                                    // session for ~90 min. DbSet.Add inserts the new row cleanly; EF's
                                    // relationship fixup still appends it to `tracked.Bets` exactly once,
                                    // so the SSE payload stays correct without manual duplication.
                                    _db.PaperBets.Add(bet);
                                    try
                                    {
                                        await _db.SaveChangesAsync(ct);
                                        _events.Publish(new PaperTradingEvent(PaperTradingEventKind.BetPlaced, tracked, bet));
                                    }
                                    catch (DbUpdateConcurrencyException ex)
                                    {
                                        // Optimistic-concurrency loss (a parallel scope mutated the session
                                        // between load and save — e.g. an HTTP DELETE or another tick).
                                        // The bet didn't insert; detach so the next tick reloads fresh and
                                        // tries again on the next candle. WITHOUT this catch, the exception
                                        // bubbles to the processor loop and the whole tick gets dropped,
                                        // which historically silenced the session for 90+ minutes after a
                                        // restart-time race condition.
                                        _db.Entry(bet).State = EntityState.Detached;
                                        _logger.LogWarning(ex, "Bet placement concurrency loss for session {SessionId} candle {Tgt} — retrying next tick", tracked.Id, currentCandleOpenMs);
                                    }
                                    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
                                    {
                                        // Two processor ticks racing for the same candle slot — partial
                                        // unique index resolves the collision; we just discard the second.
                                        _db.Entry(bet).State = EntityState.Detached;
                                        _logger.LogDebug(ex, "Bet placement race for session {SessionId} candle {Tgt}", tracked.Id, currentCandleOpenMs);
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogDebug("Session {SessionId}: strategy returned 0 stake — skipping candle {Tgt}", tracked.Id, currentCandleOpenMs);
                            }
                        } // end else (not gated out)
                        } // end else (not 5m disabled)
                    } // end if pred is not null
                } // end if !alreadyOnThisCandle
            } // end if msIntoCandle check
        } // end if !Bust && StoppedAt == null && openBet == null

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
}
