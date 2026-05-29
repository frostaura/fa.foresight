namespace FrostAura.Foresight.Domain.Trading;

/// <summary>
/// Shared Kelly-fraction math so the per-step <see cref="EdgeAwareKellyStakingStrategy"/> and the
/// live <c>KellyPositionSizer</c> can never drift. Full Kelly for a binary outcome bought at a
/// market price is f* = (winProb − price) / (1 − price), which is algebraically identical to the
/// spec form f* = winProb − (1 − winProb)/b with b = (1 − price)/price.
/// </summary>
public static class KellyMath
{
    /// <summary>
    /// Full-Kelly fraction of bankroll for an outcome with win probability <paramref name="winProb"/>
    /// bought at <paramref name="price"/> ∈ (0,1). Returns 0 at degenerate prices; can be negative
    /// when there is no edge (caller clamps / skips).
    /// </summary>
    public static decimal FullKelly(decimal winProb, decimal price)
    {
        if (price <= 0m || price >= 1m) return 0m;
        return (winProb - price) / (1m - price);
    }
}
