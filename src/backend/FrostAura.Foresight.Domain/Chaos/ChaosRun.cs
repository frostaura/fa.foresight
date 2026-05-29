using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Chaos;

/// <summary>
/// One chaos/bust test combo row: model × strategy × window-length, run over SampleCount random
/// start offsets, with aggregated survival and profit statistics. Persisted after completion;
/// rows in the same batch share a BatchId.
/// </summary>
public sealed class ChaosRun : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    /// <summary>Groups all combo rows started by a single <c>POST /api/chaos</c> call.</summary>
    public Guid BatchId { get; init; }
    public Guid ModelId { get; init; }
    /// <summary>Staking strategy id (e.g. "flat", "kelly-edge").</summary>
    public required string StrategyId { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public int WindowLength { get; init; }
    public int SampleCount { get; init; }
    public bool AllowBorrow { get; init; }
    /// <summary>PRNG seed used for this batch — persisted so the run is exactly reproducible.</summary>
    public long Seed { get; init; }
    public required string Status { get; set; }  // "running" | "complete" | "failed"

    // --- Aggregate outcome fields (null while running) ---

    /// <summary>Fraction of windows where the run busted (balance hit 0 or stake exceeded balance in strict mode).</summary>
    public decimal? BustRate { get; set; }
    /// <summary>5th-percentile profit (FinalBalance − InitialBalance) across windows.</summary>
    public decimal? ProfitP5 { get; set; }
    /// <summary>Median profit.</summary>
    public decimal? ProfitP50 { get; set; }
    /// <summary>95th-percentile profit.</summary>
    public decimal? ProfitP95 { get; set; }
    /// <summary>Maximum drawdown across all windows.</summary>
    public decimal? WorstDrawdown { get; set; }
    /// <summary>Mean zero-crossings across all windows.</summary>
    public double? MeanZeroCrossings { get; set; }
    /// <summary>Fraction of bets in the precomputed candidate set that used synthetic odds.</summary>
    public decimal? SyntheticBetFraction { get; set; }
    /// <summary>True when BustRate == 0 AND ProfitP50 &gt; 0 — the Phase-1 exit criterion.</summary>
    public bool Pass { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }

    public List<ChaosSample> Samples { get; init; } = new();
}

/// <summary>
/// One random-window outcome within a chaos run. Stored up to a cap per run (matching the backtest
/// ledger pattern) so a large sweep doesn't write millions of rows.
/// </summary>
public sealed class ChaosSample
{
    public Guid Id { get; init; }
    public Guid ChaosRunId { get; init; }
    /// <summary>OpenTime (ms) of the first candidate in this window.</summary>
    public long StartMs { get; init; }
    public bool Survived { get; init; }
    public decimal FinalBalance { get; init; }
    public decimal MaxDrawdown { get; init; }
    public int ZeroCrossings { get; init; }
}
