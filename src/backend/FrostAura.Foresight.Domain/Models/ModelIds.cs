namespace FrostAura.Foresight.Domain.Models;

/// <summary>
/// Hard-coded ids for global built-in models. Stable across deployments so foreign-key references
/// (e.g. live_predictions.ModelId default) remain valid through tenant seeding.
/// </summary>
public static class ModelIds
{
    /// <summary>The flow-DAG port of the existing LLM pipeline. Seeded with <c>TenantId = NULL</c>.</summary>
    public static readonly Guid ForesightDefaultLlm = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Null-hypothesis baseline — always emits pUp = 0.50. Seeded with <c>TenantId = NULL</c>.</summary>
    public static readonly Guid ForesightFlatBaseline = new("00000000-0000-0000-0000-000000000002");

    /// <summary>
    /// Foresight v6 — the deterministic trained model iteration line. Backtestable. Iter-0 is a
    /// plain logistic regression on the FeaturePack columns; subsequent iterations add new
    /// indicator nodes and reshape the matrix. The id is stable across iterations; the flow
    /// definition is what changes (re-seeded on every boot via DatabaseInitializer).
    /// </summary>
    public static readonly Guid ForesightV6 = new("00000000-0000-0000-0000-000000000003");

    /// <summary>
    /// Foresight | 5m | v1 — a clean-sheet, 5m-only model (not a v6 derivative). Built on
    /// 5m-specialised causal features (intraday session seasonality, 15m regime alignment, 1m
    /// sub-bar pressure) and a pluggable estimator behind the matrix → pUp+confidence contract.
    /// The id is stable across iterations; the flow definition changes (re-seeded on every boot).
    /// </summary>
    public static readonly Guid ForesightFiveMinV1 = new("00000000-0000-0000-0000-000000000004");

    /// <summary>
    /// Foresight | 5m | v1+ofx — v1 plus backtestable order-flow microstructure features
    /// (taker imbalance / CVD / large-order skew / trade intensity). The lever the plan bets on for
    /// pushing past the ~53% TA ceiling. Backtest/training only until a live recent-trades feed
    /// exists (daily dumps lag ~1 day), so it abstains live — kept separate from v1 for that reason.
    /// </summary>
    public static readonly Guid ForesightFiveMinV1Ofx = new("00000000-0000-0000-0000-000000000005");

    /// <summary>
    /// Foresight | 5m | v1+ofx2 — v1+ofx plus INTRA-BAR (high-frequency) order-flow features
    /// (late-window imbalance, imbalance acceleration into the close, late trade-intensity burst),
    /// reconstructed from the same aggTrades ticks the coarse per-bar order-flow throws away. The
    /// hypothesis: the ~53% ceiling held because every prior feature was a single per-5m aggregate;
    /// intra-bar structure is the untried, still-backtestable resolution. Clean A/B sibling of
    /// v1+ofx (identical except the microflow pack), so the walk-forward delta is attributable.
    /// </summary>
    public static readonly Guid ForesightFiveMinV1Ofx2 = new("00000000-0000-0000-0000-000000000006");

    /// <summary>
    /// Foresight | 5m | v2 — the non-linear engine. Same leakage-proof v1 feature matrix, but fit
    /// with gradient-boosted trees instead of logistic regression — the one modeling lever the v1/v6
    /// iteration logs flag as genuinely untested (projected 56–58%). Carries a confidence gate so the
    /// reporting subset can isolate "accuracy on the bets we'd most believe" without the headline
    /// diverging from the always-bet live number. The push for an honest, defensible-sample 60%.
    /// </summary>
    public static readonly Guid ForesightFiveMinV2 = new("00000000-0000-0000-0000-000000000007");
}
