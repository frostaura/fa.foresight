using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Ledger;
using FrostAura.Foresight.Infrastructure.Persistence;
using FrostAura.Foresight.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Live session lifecycle and per-tick engine.
///
/// Start/Stop/Query for live sessions. ProcessAsync drives one session tick: settle resolved bets
/// (against the MARKET's own resolution, not the Binance candle) → place the next bet via the
/// per-tenant IPlatformConnector → recompute the ledger.
///
/// Key constraints:
///  - All live placement is REFUSED unless LiveTradingArm.IsArmed AND the tenant connection's
///    LiveTrading gate=true. This is belt-and-suspenders — the config gate is enforced by the
///    connector factory (it returns a live PolymarketExecutionProvider only when the tenant
///    connection has a usable key AND LiveTrading=true; otherwise the shadow NullExecutionProvider).
///    The arm check is the second layer.
///  - ConfigHash is computed over {venue,symbol,interval,modelId,strategyId,params,initialBalance,
///    initialBetSize} EXCLUDING mode. A live session with the same config as an active paper session
///    (or another live session) is rejected → 409.
///  - Idempotency: the partial unique index on (session_id, target_open_time) prevents double-placement.
/// </summary>
public interface ILiveSessionEngine
{
    Task<LiveSession> StartAsync(LiveSessionStartRequest request, CancellationToken ct);
    Task<LiveSession?> StopAsync(Guid sessionId, CancellationToken ct);
    Task<LiveSession?> GetByIdAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<LiveSession>> ListAsync(CancellationToken ct);
    Task<LiveSession?> ProcessAsync(LiveSession session, CancellationToken ct);
}

public sealed record LiveSessionStartRequest(
    string  Venue,
    string  Symbol,
    string  Interval,
    decimal InitialBalance,
    decimal InitialBetSize,
    string? StrategyId,
    bool    Gated);

public sealed class LiveSessionEngine : ILiveSessionEngine
{
    private const long PlacementSafetyBufferMs = 5_000;

    private readonly ForesightDbContext _db;
    private readonly ITenantContext     _tenant;
    private readonly IPlatformConnectorFactory _connectorFactory;
    private readonly ILiveTradingArm    _arm;
    private readonly IAccountLedger     _ledger;
    private readonly ICalibrationRescaler _calibration;
    private readonly IVenuePriceStore   _venuePrices;
    private readonly TradingGuardrailOptions _guardrails;
    private readonly TradingNotifier    _notifier;
    private readonly ILogger<LiveSessionEngine> _logger;

    public LiveSessionEngine(
        ForesightDbContext db,
        ITenantContext tenant,
        IPlatformConnectorFactory connectorFactory,
        ILiveTradingArm arm,
        IAccountLedger ledger,
        ICalibrationRescaler calibration,
        IVenuePriceStore venuePrices,
        IOptions<TradingGuardrailOptions> guardrails,
        TradingNotifier notifier,
        ILogger<LiveSessionEngine> logger)
    {
        _db          = db;
        _tenant      = tenant;
        _connectorFactory = connectorFactory;
        _arm         = arm;
        _ledger      = ledger;
        _calibration = calibration;
        _venuePrices = venuePrices;
        _guardrails  = guardrails.Value;
        _notifier    = notifier;
        _logger      = logger;
    }

    public async Task<LiveSession> StartAsync(LiveSessionStartRequest request, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant not resolved.");
        if (request.InitialBalance <= 0) throw new ArgumentException("initialBalance must be > 0");
        if (request.InitialBetSize <= 0 || request.InitialBetSize > request.InitialBalance)
            throw new ArgumentException("initialBetSize must be > 0 and <= initialBalance");

        var resolvedStrategy = StakingStrategies.Resolve(request.StrategyId).Id;
        var configHash       = ComputeConfigHash(request.Venue, request.Symbol, request.Interval, resolvedStrategy, request.InitialBalance, request.InitialBetSize);

        // Config-hash collision check: active paper OR live session with the same config → 409.
        // Both paper_sessions.ConfigHash and live_sessions.ConfigHash are computed with the same
        // algorithm (EXCLUDING mode) so a live session and a paper session with identical settings
        // correctly collide here.
        var paperConflict = await _db.PaperSessions
            .AnyAsync(s => s.TenantId == _tenant.TenantId!.Value && s.ConfigHash == configHash && s.StoppedAt == null, ct);
        if (paperConflict)
            throw new InvalidOperationException($"An active paper session with this configuration already exists (config_hash={configHash}). Stop it first before starting a live session with the same settings. [409]");

        var liveConflict = await _db.LiveSessions
            .AnyAsync(s => s.TenantId == _tenant.TenantId!.Value && s.ConfigHash == configHash && s.StoppedAt == null, ct);
        if (liveConflict)
            throw new InvalidOperationException($"An active session with this configuration already exists (config_hash={configHash}). Stop it first. [409]");

        // Guardrail: max concurrent live sessions.
        var activeLiveCount = await _db.LiveSessions
            .CountAsync(s => s.TenantId == _tenant.TenantId!.Value && s.StoppedAt == null && s.Mode == "live", ct);
        if (activeLiveCount >= _guardrails.MaxConcurrentLiveSessions)
            throw new InvalidOperationException($"Max concurrent live sessions ({_guardrails.MaxConcurrentLiveSessions}) reached.");

        // Pre-flight affordability check BEFORE creating the session.
        // GetFreeAsync queries Σ(active live session current_balance WHERE stopped_at IS NULL AND mode='live').
        // We must check BEFORE saving so the new session does not appear in that sum yet — otherwise a
        // subsequent ReserveAsync would double-count the reservation and falsely fail.
        var free = await _ledger.GetFreeAsync(_tenant.TenantId!.Value, ct);
        if (request.InitialBalance > free)
            throw new InsufficientPusdException(request.InitialBalance, free);

        var session = new LiveSession
        {
            Id             = Guid.NewGuid(),
            TenantId       = _tenant.TenantId!.Value,
            Symbol         = request.Symbol,
            Interval       = request.Interval,
            Venue          = request.Venue,
            Mode           = "live",
            ConfigHash     = configHash,
            StartedAt      = DateTimeOffset.UtcNow,
            InitialBalance = request.InitialBalance,
            InitialBetSize = request.InitialBetSize,
            StrategyId     = resolvedStrategy,
            Gated          = request.Gated,
            CurrentBalance = request.InitialBalance,
            CurrentBetSize = request.InitialBetSize,
            ReservedAmount = request.InitialBalance
        };
        _db.LiveSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        // Write the reservation audit row for session.Id exactly once.
        // The pre-flight check above already confirmed affordability; the session is now saved, so
        // its current_balance is included in Σactive — calling IAccountLedger.ReserveAsync again
        // would double-count and falsely fail.  WriteReserveAuditAsync writes only the observability
        // row, without re-running the free-balance check.
        await _ledger.WriteReserveAuditAsync(_tenant.TenantId!.Value, session.Id, request.InitialBalance, ct);

        _logger.LogInformation("Live session started: {SessionId} {Venue} {Symbol} {Interval} balance={Balance}",
            session.Id, request.Venue, request.Symbol, request.Interval, request.InitialBalance);
        return session;
    }

    public async Task<LiveSession?> StopAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return null;
        var session = await _db.LiveSessions
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.TenantId == _tenant.TenantId!.Value && s.Id == sessionId && s.StoppedAt == null, ct);
        if (session is null) return null;
        session.StoppedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _ledger.ReleaseAsync(_tenant.TenantId!.Value, sessionId, ct);
        return session;
    }

    public async Task<LiveSession?> GetByIdAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return null;
        return await _db.LiveSessions
            .AsNoTracking()
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.TenantId == _tenant.TenantId!.Value && s.Id == sessionId && s.StoppedAt == null, ct);
    }

    public async Task<IReadOnlyList<LiveSession>> ListAsync(CancellationToken ct)
    {
        if (!_tenant.IsResolved) return Array.Empty<LiveSession>();
        return await _db.LiveSessions
            .AsNoTracking()
            .Include(s => s.Bets)
            .Where(s => s.TenantId == _tenant.TenantId!.Value && s.StoppedAt == null)
            .ToListAsync(ct);
    }

    public async Task<LiveSession?> ProcessAsync(LiveSession session, CancellationToken ct)
    {
        if (session.StoppedAt is not null || session.Bust) return session;

        // Arm check: live execution only when the arm is confirmed AND the tenant connection's
        // LiveTrading gate is true. Even in paper-shadow mode this method may be called for paper
        // sessions routed through this engine — the connector will be NullExecutionProvider when the
        // tenant has no usable key or LiveTrading=false.
        var tenantId = session.TenantId;
        var isLive   = session.Mode == "live";
        var connector = await _connectorFactory.GetForTenantAsync(tenantId, ct);
        if (isLive && connector.ConnectorId != "null-execution" && !_arm.IsArmed(tenantId))
        {
            _logger.LogInformation("Live session {SessionId} skipped — arm not confirmed", session.Id);
            return session;
        }

        // Per-session drawdown guardrail.
        var drawdown = session.InitialBalance > 0m
            ? (session.InitialBalance - session.CurrentBalance) / session.InitialBalance
            : 0m;
        if (drawdown >= _guardrails.SessionDrawdownCircuitBreakerPct)
        {
            _logger.LogWarning("Live session {SessionId} circuit breaker: drawdown {Drawdown:P0} >= threshold {Threshold:P0}",
                session.Id, drawdown, _guardrails.SessionDrawdownCircuitBreakerPct);
            var tracked2 = await _db.LiveSessions.FirstOrDefaultAsync(s => s.Id == session.Id, ct);
            if (tracked2 is not null) { tracked2.Bust = true; tracked2.StoppedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
            await _ledger.ReleaseAsync(tenantId, session.Id, ct);
            await _notifier.NotifyCircuitBreakerAsync(tenantId, session.Id, drawdown, _guardrails.SessionDrawdownCircuitBreakerPct, ct);
            return tracked2 ?? session;
        }

        var tracked = await _db.LiveSessions
            .Include(s => s.Bets)
            .FirstOrDefaultAsync(s => s.Id == session.Id, ct);
        if (tracked is null) return null;
        if (tracked.StoppedAt is not null || tracked.Bust) return tracked;

        var intervalMs = BinanceMarketDataClient.IntervalMs(tracked.Interval);
        var nowMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentCandleOpenMs = nowMs / intervalMs * intervalMs;
        var openBet    = tracked.Bets.FirstOrDefault(b => !b.Resolved);

        // ── Settlement ──────────────────────────────────────────────────────────
        if (openBet is not null)
        {
            var candleClosed = openBet.TargetOpenTime + intervalMs <= nowMs;
            if (candleClosed && openBet.ExternalOrderId is not null)
            {
                // Poll the CLOB for fill status.
                var orderState = await connector.GetOrderStateAsync(openBet.ExternalOrderId, ct);
                if (orderState.Status == OrderStatus.Filled || orderState.Status == OrderStatus.Cancelled)
                {
                    // Query the market's own resolution outcome (not the Binance candle).
                    // Degraded gracefully to null when LiveTrading is disarmed or the venue is unreachable.
                    bool? marketOutcomeUp = null;
                    if (openBet.MarketExternalId is not null)
                    {
                        var resolution = await connector.GetMarketResolutionAsync(openBet.MarketExternalId, ct);
                        if (resolution is { Resolved: true })
                        {
                            marketOutcomeUp = resolution.YesWon; // true=Yes won, false=No won
                            _logger.LogDebug("Live bet {BetId}: market {Market} resolved — YesWon={YesWon}",
                                openBet.Id, openBet.MarketExternalId, marketOutcomeUp);
                        }
                        else
                        {
                            _logger.LogDebug("Live bet {BetId}: market {Market} not yet resolved",
                                openBet.Id, openBet.MarketExternalId);
                        }
                    }

                    // FIX: for LIVE sessions, do NOT settle until the market has actually resolved.
                    // The old optimistic proxy (assume the model was right) inflated live P&L until
                    // real resolution arrived. Paper/backtest sessions keep the candle-direction proxy.
                    if (isLive && !marketOutcomeUp.HasValue)
                    {
                        // Market not yet resolved — leave the bet open and return. The placement
                        // guard (openBet is null) will keep us from placing a new bet this tick.
                        _logger.LogDebug("Live session {SessionId}: bet {BetId} awaiting market resolution — skipping settlement this tick",
                            tracked.Id, openBet.Id);
                        tracked.LastProcessedAt = DateTimeOffset.UtcNow;
                        try { await _db.SaveChangesAsync(ct); }
                        catch (DbUpdateConcurrencyException ex)
                        { _logger.LogDebug(ex, "Heartbeat save concurrency loss for live session {SessionId}", tracked.Id); }
                        return tracked;
                    }

                    var entryPrice = openBet.EntryPrice ?? 0.5m;
                    // For live: marketOutcomeUp is guaranteed non-null here (guarded above).
                    // For paper/backtest: use the candle-direction proxy (bet side "UP" means model predicted UP).
                    var outcomeUp = marketOutcomeUp ?? (openBet.Side == "UP"); // live: real outcome; paper: proxy
                    var step = StakingEngine.Settle(
                        side: openBet.Side,
                        entryPrice: entryPrice,
                        stake: openBet.Size,
                        currentBalance: tracked.CurrentBalance,
                        outcomeUp: outcomeUp,
                        allowBorrow: false);
                    tracked.CurrentBalance = step.BalanceAfter;
                    tracked.CurrentBetSize = openBet.Size;
                    if (step.CrossedZero) tracked.ZeroCrossingsCount++;
                    if (tracked.CurrentBalance < 0)
                        tracked.PeakBorrowed = Math.Max(tracked.PeakBorrowed, Math.Abs(tracked.CurrentBalance));
                    openBet.Resolved       = true;
                    openBet.Outcome        = step.Won ? "win" : "loss";
                    openBet.Payout         = step.Payout;
                    openBet.Shares         = step.Shares;
                    openBet.BalanceAfter   = tracked.CurrentBalance;
                    openBet.MarketOutcomeUp = marketOutcomeUp;
                    openBet.ResolvedAt     = DateTimeOffset.UtcNow;

                    // Divergence signal: when the market resolved and it disagrees with the model's predicted side.
                    // A divergence is a correctness signal (the model called direction wrong) not a loss-cause note.
                    if (marketOutcomeUp.HasValue)
                    {
                        var modelPredictedUp = openBet.Side == "UP";
                        if (modelPredictedUp != marketOutcomeUp.Value)
                        {
                            openBet.DivergenceNote = $"Model predicted {(modelPredictedUp ? "UP" : "DOWN")} but market resolved {(marketOutcomeUp.Value ? "YES (UP)" : "NO (DOWN)")}. Correctness signal — check model calibration.";
                            _logger.LogInformation("Live bet {BetId} divergence: model={ModelSide} market={MarketSide}",
                                openBet.Id, openBet.Side, marketOutcomeUp.Value ? "YES" : "NO");
                        }
                    }
                    if (tracked.CurrentBalance <= 0) { tracked.Bust = true; tracked.StoppedAt = DateTimeOffset.UtcNow; }

                    try { await _db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException ex)
                    { _logger.LogWarning(ex, "Settlement concurrency loss for live bet {Id}", openBet.Id); }

                    // Recompute ledger reservation after settle.
                    await _ledger.RecomputeAsync(tenantId, tracked.Id, tracked.CurrentBalance, ct);
                    if (tracked.Bust) await _ledger.ReleaseAsync(tenantId, tracked.Id, ct);

                    // Notify bet resolution (best-effort).
                    await _notifier.NotifyBetResolvedAsync(
                        tenantId, tracked.Id, openBet.Id,
                        openBet.Side, openBet.Size, step.Payout, step.Won, tracked.CurrentBalance, ct);
                    if (tracked.Bust)
                        await _notifier.NotifySessionBustAsync(tenantId, tracked.Id, "live", tracked.CurrentBalance, ct);

                    openBet = null;
                }
            }
        }

        // ── Placement ───────────────────────────────────────────────────────────
        if (!tracked.Bust && tracked.StoppedAt is null && openBet is null)
        {
            var msIntoCandle = nowMs - currentCandleOpenMs;
            if (msIntoCandle < intervalMs - PlacementSafetyBufferMs)
            {
                var alreadyOnThisCandle = tracked.Bets.Any(b => b.TargetOpenTime == currentCandleOpenMs);
                if (!alreadyOnThisCandle)
                {
                    var pred = await _db.LivePredictions.AsNoTracking()
                        .FirstOrDefaultAsync(p =>
                            p.TenantId == tracked.TenantId &&
                            p.Symbol   == tracked.Symbol   &&
                            p.Interval == tracked.Interval &&
                            p.TargetOpenTime == currentCandleOpenMs, ct);

                    if (pred is not null)
                    {
                        var calibratedPUp = await _calibration.RescaleAsync(tracked.TenantId, tracked.Interval, pred.DirectionUpProbability, ct);
                        if (tracked.Gated && StakingEngine.IsNoBet(calibratedPUp, StakingEngine.DefaultNoBetBand))
                        {
                            _logger.LogDebug("Live session {SessionId}: gated out on candle {Tgt}", tracked.Id, currentCandleOpenMs);
                        }
                        else
                        {
                            var entryQuote = await _venuePrices.EnsureEntryAsync(
                                tracked.Venue, tracked.Symbol, tracked.Interval, currentCandleOpenMs, calibratedPUp, ct);
                            var side        = StakingEngine.DecideSide(calibratedPUp);
                            var entryInputs = new StakingInputs(calibratedPUp, entryQuote.YesPrice, entryQuote.NoPrice);
                            var lastSettled = tracked.Bets.Where(b => b.Resolved).OrderByDescending(b => b.TargetOpenTime).FirstOrDefault();
                            var lastStake   = lastSettled?.Size ?? tracked.InitialBetSize;
                            var lastWon     = lastSettled?.Outcome == "win";
                            var strategy    = StakingStrategies.Resolve(tracked.StrategyId);
                            var nextStake   = strategy.NextBetSize(new StrategyStep(lastStake, lastWon, tracked.InitialBetSize, tracked.CurrentBalance, entryInputs));

                            if (nextStake > 0m)
                            {
                                // Per-trade cap guardrail.
                                if (nextStake > _guardrails.MaxPerTradeUsd)
                                {
                                    _logger.LogInformation("Live bet capped from {Original} to {Cap} by per-trade guardrail", nextStake, _guardrails.MaxPerTradeUsd);
                                    nextStake = _guardrails.MaxPerTradeUsd;
                                }

                                if (nextStake > tracked.CurrentBalance)
                                {
                                    tracked.Bust = true; tracked.StoppedAt = DateTimeOffset.UtcNow;
                                    await _db.SaveChangesAsync(ct);
                                    await _ledger.ReleaseAsync(tenantId, tracked.Id, ct);
                                    await _notifier.NotifySessionBustAsync(tenantId, tracked.Id, "live", tracked.CurrentBalance, ct);
                                }
                                else
                                {
                                    var entryPrice = side == "UP" ? entryQuote.YesPrice : entryQuote.NoPrice;
                                    var shares     = StakingEngine.Shares(nextStake, entryPrice);
                                    tracked.CurrentBetSize = nextStake;

                                    string? externalOrderId = null;
                                    if (isLive && _arm.IsArmed(tenantId) && connector.ConnectorId != "null-execution")
                                    {
                                        try
                                        {
                                            var orderSide = side == "UP" ? OrderSide.Yes : OrderSide.No;
                                            var receipt   = await connector.PlaceOrderAsync(
                                                new OrderRequest(entryQuote.MarketExternalId ?? "", orderSide, shares, entryPrice, tenantId), ct);
                                            externalOrderId = receipt.OrderId;
                                            _logger.LogInformation("Live bet placed: {OrderId} {Side} {Shares}@{Price}", externalOrderId, side, shares, entryPrice);
                                            // Notify trade placed (best-effort).
                                            await _notifier.NotifyTradePlacedAsync(
                                                tenantId, tracked.Id, externalOrderId,
                                                side, nextStake, entryPrice, entryQuote.MarketExternalId, ct);
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException)
                                        {
                                            _logger.LogError(ex, "Live order placement failed — bet NOT recorded");
                                            return tracked;
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogInformation("[SHADOW/DISARMED] Would place live bet {Side} {Shares}@{Price} on {Market}",
                                            side, shares, entryPrice, entryQuote.MarketExternalId);
                                    }

                                    var bet = new LiveBet
                                    {
                                        Id              = Guid.NewGuid(),
                                        TenantId        = tracked.TenantId,
                                        SessionId       = tracked.Id,
                                        TargetOpenTime  = currentCandleOpenMs,
                                        Side            = side,
                                        PredictedProbUp = pred.DirectionUpProbability,
                                        AnchorClose     = pred.AnchorClose,
                                        Size            = nextStake,
                                        BalanceBefore   = tracked.CurrentBalance,
                                        ExternalOrderId = externalOrderId,
                                        EntryPrice      = entryPrice,
                                        Shares          = shares,
                                        MarketExternalId = entryQuote.MarketExternalId,
                                        PlacedAt        = DateTimeOffset.FromUnixTimeMilliseconds(currentCandleOpenMs),
                                        NotesJson       = JsonSerializer.Serialize(new
                                        {
                                            calibratedPUp,
                                            side,
                                            entryPrice,
                                            externalOrderId,
                                            armed = _arm.IsArmed(tenantId)
                                        })
                                    };
                                    _db.LiveBets.Add(bet);
                                    try { await _db.SaveChangesAsync(ct); }
                                    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
                                    {
                                        _db.Entry(bet).State = EntityState.Detached;
                                        _logger.LogDebug("Duplicate live bet race for session {SessionId} candle {Tgt}", tracked.Id, currentCandleOpenMs);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        tracked.LastProcessedAt = DateTimeOffset.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex)
        { _logger.LogDebug(ex, "Heartbeat save concurrency loss for live session {SessionId}", tracked.Id); }

        return tracked;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stable SHA-256 config hash over the session's key parameters EXCLUDING mode.
    /// Prevents running two sessions (paper or live) with identical configurations simultaneously.
    /// </summary>
    public static string ComputeConfigHash(
        string venue, string symbol, string interval,
        string strategyId, decimal initialBalance, decimal initialBetSize)
    {
        var raw = $"{venue}|{symbol}|{interval}|{strategyId}|{initialBalance:F6}|{initialBetSize:F6}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
