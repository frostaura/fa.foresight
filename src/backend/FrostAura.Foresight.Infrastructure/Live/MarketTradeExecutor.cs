using FrostAura.Foresight.Domain.Bankroll;
using FrostAura.Foresight.Domain.Markets;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Positions;
using FrostAura.Foresight.Domain.Sizing;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Live;

public sealed record TradeOutcome(bool Traded, string Message, Guid? PositionId = null);

/// <summary>
/// Single source of truth for "size a trade, run the guardrails, execute, persist".
/// Used by both the autonomous loop (per discovered market) and the manual /forcebuy command, so the
/// trade decision and its safety rails can never diverge between the two paths. Scoped — owns a
/// DbContext for the duration of one trade decision.
///
/// Execution mode (Shadow vs Live) is derived from the registered <see cref="IExecutionProvider"/>:
/// the NullExecutionProvider yields Shadow positions; a real venue adapter (Phase 3) yields Live.
///
/// NOTE: LLM forecasting removed. The pYes/pNo inputs must be supplied by the caller (e.g. from
/// the Polymarket market provider or a deterministic signal). The Forecast entity and its DB table
/// are no longer part of the live-trading path.
/// </summary>
public sealed class MarketTradeExecutor
{
    private readonly ForesightDbContext _db;
    private readonly IPositionSizer _sizer;
    private readonly IExecutionProvider _exec;
    private readonly IChannelAdapter _channel;
    private readonly ILiveTradingArm _arm;
    private readonly TradingGuardrailOptions _opts;
    private readonly ILogger<MarketTradeExecutor> _logger;

    public MarketTradeExecutor(
        ForesightDbContext db,
        IPositionSizer sizer,
        IExecutionProvider exec,
        IChannelAdapter channel,
        ILiveTradingArm arm,
        IOptions<TradingGuardrailOptions> opts,
        ILogger<MarketTradeExecutor> logger)
    {
        _db = db;
        _sizer = sizer;
        _exec = exec;
        _channel = channel;
        _arm = arm;
        _opts = opts.Value;
        _logger = logger;
    }

    private bool IsShadow => _exec.ProviderId == "null-execution";

    /// <summary>Upsert a provider-discovered market into the markets table, returning the persisted
    /// row (stable Id) so a Position can reference it.</summary>
    public async Task<Market> EnsureMarketAsync(Guid tenantId, Market discovered, CancellationToken ct)
    {
        var existing = await _db.Markets
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ProviderId == discovered.ProviderId && m.ExternalId == discovered.ExternalId, ct);
        if (existing is not null)
        {
            existing.Status = discovered.Status;
            existing.ResolutionCriteria = discovered.ResolutionCriteria;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var market = new Market
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderId = discovered.ProviderId,
            ExternalId = discovered.ExternalId,
            Question = discovered.Question,
            Category = discovered.Category,
            CreatedAt = DateTimeOffset.UtcNow,
            // Polymarket endDate often parses to a non-UTC offset; Npgsql timestamptz requires offset 0.
            ResolvesAt = discovered.ResolvesAt?.ToUniversalTime(),
            Status = discovered.Status,
            ResolutionCriteria = discovered.ResolutionCriteria
        };
        _db.Markets.Add(market);
        await _db.SaveChangesAsync(ct);
        return market;
    }

    public async Task<TradeOutcome> TradeMarketAsync(Guid tenantId, Market market, decimal pYes, decimal yesPrice, decimal noPrice, bool manual, CancellationToken ct)
    {
        var providerId = market.ProviderId;
        var bankroll = await GetOrSeedBankrollAsync(tenantId, providerId, ct);

        // Circuit breaker: drawdown from the all-time bankroll peak.
        var peak = await _db.Bankrolls.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.ProviderId == providerId)
            .MaxAsync(b => (decimal?)b.TotalUsd, ct) ?? bankroll.TotalUsd;
        if (peak > 0m && (peak - bankroll.TotalUsd) / peak >= _opts.CircuitBreakerDrawdownPct)
        {
            await TripBreakerAsync(tenantId, peak, bankroll.TotalUsd, ct);
            return new TradeOutcome(false, $"Circuit breaker tripped — drawdown {(peak - bankroll.TotalUsd) / peak:P0} from peak. Loop disabled.");
        }

        var available = bankroll.AvailableUsd;
        if (available <= 0m)
            return new TradeOutcome(false, "No available bankroll.");

        var openCount = await _db.Positions.CountAsync(p => p.TenantId == tenantId && p.Status == PositionStatus.Open, ct);
        if (openCount >= _opts.MaxConcurrentPositions)
            return new TradeOutcome(false, $"Max concurrent positions ({_opts.MaxConcurrentPositions}) reached.");

        var sinceMidnight = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        var dailyNotional = await _db.Positions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.OpenedAt >= sinceMidnight)
            .SumAsync(p => p.Shares * p.AverageEntryPrice, ct);

        // Size the trade using the supplied pYes signal.
        var sizing = _sizer.Size(pYes, yesPrice, noPrice, available, _opts.KellyFraction);
        if (!sizing.ShouldTrade)
            return new TradeOutcome(false, $"No trade: {sizing.Reason} (p_yes {pYes:0.###} vs price {yesPrice:0.###}).");

        if (!manual && sizing.Edge < _opts.MinEdge)
            return new TradeOutcome(false, $"Edge {sizing.Edge:0.###} below min {_opts.MinEdge:0.###} — skipped.");

        // Clamp stake to every cap, then re-derive shares from the clamped stake.
        var stake = Math.Min(sizing.StakeUsd, _opts.MaxPerTradeUsd);
        stake = Math.Min(stake, Math.Round(_opts.MaxPositionPctBankroll * bankroll.TotalUsd, 2));
        stake = Math.Min(stake, available);
        if (dailyNotional + stake > _opts.MaxDailyNotionalUsd)
            return new TradeOutcome(false, $"Daily notional cap ${_opts.MaxDailyNotionalUsd} would be exceeded (used ${dailyNotional:0.00}).");
        if (stake <= 0m)
            return new TradeOutcome(false, "Stake clamped to zero by guardrails.");

        var shares = Math.Round(stake / sizing.LimitPrice, 4, MidpointRounding.ToZero);
        if (shares <= 0m)
            return new TradeOutcome(false, "Share size rounded to zero after clamping.");

        // Live safety arm: even when a real execution provider is wired, refuse to place a live order
        // until the operator has explicitly armed it via /golive. Shadow never needs arming.
        if (!IsShadow && !_arm.IsArmed(tenantId))
            return new TradeOutcome(false, "Live execution is configured but NOT armed — send /golive to arm before real orders place.");

        var orderSide = sizing.Side == PositionSide.Yes ? OrderSide.Yes : OrderSide.No;
        var receipt = await _exec.PlaceOrderAsync(new OrderRequest(market.ExternalId, orderSide, shares, sizing.LimitPrice, tenantId), ct);

        var position = new Position
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MarketId = market.Id,
            ForecastId = null,
            Mode = IsShadow ? PositionMode.Shadow : PositionMode.Live,
            Side = sizing.Side,
            Shares = shares,
            AverageEntryPrice = sizing.LimitPrice,
            CurrentPrice = sizing.LimitPrice,
            Status = PositionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            ExternalOrderId = receipt.OrderId
        };
        _db.Positions.Add(position);

        _db.Bankrolls.Add(new BankrollEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderId = providerId,
            TotalUsd = bankroll.TotalUsd,
            InFlightUsd = bankroll.InFlightUsd + stake,
            RecordedAt = DateTimeOffset.UtcNow,
            Note = FormattableString.Invariant($"open {sizing.Side} {shares:0.##}@{sizing.LimitPrice:0.###} on {market.ExternalId}")
        });
        await _db.SaveChangesAsync(ct);

        var modeTag = IsShadow ? "SHADOW" : "LIVE";
        var q = Trim(market.Question, 120);
        var body = FormattableString.Invariant(
            $"[{modeTag}] {sizing.Side} {shares:0.##} @ {sizing.LimitPrice:0.###} (${stake:0.00})\n{q}\np_yes {pYes:0.###} vs price {yesPrice:0.###} · edge {sizing.Edge:0.###}");
        await _channel.SendAsync(tenantId, new OutboundNotification(NotificationKind.AutotradeExecution, $"{modeTag} trade", body), ct);

        _logger.LogInformation("{Mode} trade {Side} {Shares}@{Price} ${Stake} on {Market}", modeTag, sizing.Side, shares, sizing.LimitPrice, stake, market.ExternalId);
        return new TradeOutcome(true, body, position.Id);
    }

    private async Task<BankrollEntry> GetOrSeedBankrollAsync(Guid tenantId, string providerId, CancellationToken ct)
    {
        var latest = await _db.Bankrolls.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.ProviderId == providerId)
            .OrderByDescending(b => b.RecordedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is not null) return latest;

        var seed = new BankrollEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderId = providerId,
            TotalUsd = _opts.StartingBankrollUsd,
            InFlightUsd = 0m,
            RecordedAt = DateTimeOffset.UtcNow,
            Note = "seed"
        };
        _db.Bankrolls.Add(seed);
        await _db.SaveChangesAsync(ct);
        return seed;
    }

    private async Task TripBreakerAsync(Guid tenantId, decimal peak, decimal current, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is not null && tenant.Settings.AutotradeEnabled)
        {
            tenant.Settings.AutotradeEnabled = false;
            // Settings is a value-converted jsonb column (reference-equality comparer), so an in-place
            // mutation isn't auto-detected — Update() marks the whole row modified to force the write.
            _db.Tenants.Update(tenant);
            await _db.SaveChangesAsync(ct);
        }
        await _channel.SendAsync(tenantId, new OutboundNotification(
            NotificationKind.CircuitBreakerTripped, "Circuit breaker tripped",
            $"Bankroll fell to ${current:0.00} from peak ${peak:0.00} ({(peak - current) / peak:P0}). Autonomous loop disabled — re-enable with /enable after review."), ct);
        _logger.LogWarning("Circuit breaker tripped for tenant {Tenant}: peak {Peak} current {Current}", tenantId, peak, current);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
