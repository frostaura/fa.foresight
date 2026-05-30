namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Operational risk rails for the autonomous loop. These are NOT the shadow-mode calibration gate
/// (that was overridden at parent level) — they are the always-on safety caps that bound blast radius
/// regardless of shadow/live. Bound from the "Trading" config section.
/// </summary>
public sealed class TradingGuardrailOptions
{
    /// <summary>Master switch for the autonomous loop. Per-tenant activation is the tenant's
    /// AutotradeEnabled flag (toggled via /enable, /disable); this is the deployment-wide kill switch.</summary>
    public bool LoopEnabled { get; set; } = true;

    /// <summary>Seconds between loop ticks. Default 5 min — prediction markets move slowly.</summary>
    public int TickIntervalSeconds { get; set; } = 300;

    /// <summary>Seconds between resolution sweeps.</summary>
    public int ResolutionIntervalSeconds { get; set; } = 600;

    /// <summary>Bankroll seeded on first run when no BankrollEntry exists for the tenant+provider.</summary>
    public decimal StartingBankrollUsd { get; set; } = 80m;

    /// <summary>Fraction of full Kelly to apply (¼-Kelly default).</summary>
    public decimal KellyFraction { get; set; } = 0.25m;

    /// <summary>Minimum model-vs-price edge required to auto-trade. /forcebuy bypasses this.</summary>
    public decimal MinEdge { get; set; } = 0.05m;

    public decimal MaxPerTradeUsd { get; set; } = 5m;
    public decimal MaxDailyNotionalUsd { get; set; } = 50m;
    public int MaxConcurrentPositions { get; set; } = 10;
    public decimal MaxPositionPctBankroll { get; set; } = 0.10m;

    /// <summary>Drawdown from the all-time bankroll peak that trips the breaker and auto-disables the loop.</summary>
    public decimal CircuitBreakerDrawdownPct { get; set; } = 0.25m;

    // Market-discovery scope per tick.
    public string? MarketCategory { get; set; } = null;
    public decimal MinMarketVolumeUsd { get; set; } = 10000m;
    public int MaxMarketsPerTick { get; set; } = 5;

    /// <summary>Don't re-forecast a market more than once within this many hours.</summary>
    public int MinReforecastHours { get; set; } = 12;

    // ── Workstream E: live session guardrails ─────────────────────────────────

    /// <summary>Maximum number of simultaneously active live sessions (not paper) per tenant.</summary>
    public int MaxConcurrentLiveSessions { get; set; } = 2;

    /// <summary>
    /// Per-session drawdown fraction that trips the session circuit breaker and auto-stops.
    /// Default 0.50 = stop when the session has lost 50% of its initial balance.
    /// </summary>
    public decimal SessionDrawdownCircuitBreakerPct { get; set; } = 0.50m;

}
