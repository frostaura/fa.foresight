using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Live;

/// <summary>
/// A live (real-money) trading session on a prediction-market venue.
/// Mirrors PaperSession shape but adds: Mode, ReservedAmount, ConfigHash, ExternalOrderId on bets.
///
/// Mode: "paper" | "live".
/// ReservedAmount: denormalised pUSD reserved for this session (display cache; the authoritative
///   figure for the ledger invariant is the current_balance itself).
/// ConfigHash: stable hash of session config EXCLUDING mode — used for dedup across both paper and live.
/// </summary>
public sealed class LiveSession : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required string Venue { get; init; }
    public required string Mode { get; init; } // "paper" | "live"

    /// <summary>Stable config hash — dedup key across active sessions (paper+live).</summary>
    public required string ConfigHash { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; set; }

    public required decimal InitialBalance { get; init; }
    public required decimal InitialBetSize { get; init; }
    public required string StrategyId { get; init; }
    public bool Gated { get; init; }

    public decimal CurrentBalance { get; set; }
    public decimal CurrentBetSize { get; set; }
    public bool Bust { get; set; }
    public int ZeroCrossingsCount { get; set; }
    public decimal PeakBorrowed { get; set; }

    /// <summary>Denormalised display cache — pUSD currently reserved for this session.</summary>
    public decimal ReservedAmount { get; set; }

    public DateTimeOffset? LastProcessedAt { get; set; }

    public List<LiveBet> Bets { get; init; } = new();
}

/// <summary>
/// A single candle-aligned bet within a live session. ExternalOrderId is the CLOB order ID returned
/// by PlaceOrderAsync; DivergenceNote records any discrepancy between expected and actual settlement.
/// </summary>
public sealed class LiveBet : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SessionId { get; init; }

    public required long TargetOpenTime { get; init; }
    public required string Side { get; init; } // "UP" | "DOWN"
    public required decimal PredictedProbUp { get; init; }
    public required decimal AnchorClose { get; init; }
    public required decimal Size { get; init; }
    public required decimal BalanceBefore { get; init; }

    public DateTimeOffset PlacedAt { get; init; }

    /// <summary>External CLOB order ID returned by PlaceOrderAsync. Null until placed.</summary>
    public string? ExternalOrderId { get; set; }

    public bool Resolved { get; set; }
    public string? Outcome { get; set; } // "win" | "loss"
    public decimal? Payout { get; set; }
    public decimal? BalanceAfter { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>The market's own resolved outcome (true = UP). Null until settled.</summary>
    public bool? MarketOutcomeUp { get; set; }

    public decimal? EntryPrice { get; set; }
    public decimal? Shares { get; set; }

    /// <summary>Non-null when the expected outcome differed from the market's resolution (correctness signal).</summary>
    public string? DivergenceNote { get; set; }

    public string? NotesJson { get; set; }
    public string? MarketExternalId { get; set; }
}
