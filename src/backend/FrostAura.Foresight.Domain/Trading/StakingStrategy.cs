namespace FrostAura.Foresight.Domain.Trading;

/// <summary>
/// A staking strategy decides the size of the next bet. Side selection, odds-based payoff, and
/// bust / borrow handling are common across all strategies and live on <see cref="StakingEngine"/>;
/// the only thing a strategy owns is the size dynamic. Sizing is a pure function of the
/// <see cref="StrategyStep"/> context — no state, no I/O.
///
/// Outcome-only strategies (Flat, Martingale) read only the prior bet/outcome + bankroll and ignore
/// <see cref="StrategyStep.Inputs"/>. Edge-aware strategies (true Kelly) size against the UPCOMING
/// candle's calibrated pUp and venue price carried in <see cref="StrategyStep.Inputs"/>;
/// <see cref="RequiresEdgeInputs"/> lets runners assert a real price feed is present before using one.
/// A returned size of 0 is the canonical "no bet this step" signal.
/// </summary>
public interface IStakingStrategy
{
    /// <summary>Stable kebab-case identifier persisted on the session/backtest row and used in API calls.</summary>
    string Id { get; }
    /// <summary>Human-readable display name.</summary>
    string Name { get; }
    /// <summary>One-paragraph description shown in the UI tooltip / strategy picker.</summary>
    string Description { get; }
    /// <summary>True when the strategy sizes off calibrated pUp + market price (needs a real odds feed).</summary>
    bool RequiresEdgeInputs { get; }
    /// <summary>Size of the next bet given the prior step, post-settlement bankroll, and upcoming edge inputs.</summary>
    decimal NextBetSize(StrategyStep step);
}

/// <summary>
/// Classic Martingale: double the bet on every loss, reset to the initial unit on every win.
/// </summary>
public sealed class MartingaleStakingStrategy : IStakingStrategy
{
    public string Id => "martingale";
    public string Name => "Martingale";
    public string Description =>
        "Double the bet after every loss, reset to the initial bet after every win. One win recovers " +
        "the entire prior losing chain plus a single unit profit — at the cost of exponentially growing " +
        "exposure during cold streaks.";
    public bool RequiresEdgeInputs => false;
    public decimal NextBetSize(StrategyStep s) => s.Won ? s.InitialBetSize : s.CurrentBetSize * 2m;
}

/// <summary>
/// Flat staking: bet the initial size every step regardless of outcome. The honest baseline.
/// </summary>
public sealed class FlatStakingStrategy : IStakingStrategy
{
    public string Id => "flat";
    public string Name => "Flat";
    public string Description =>
        "Bet the initial size every step regardless of outcome. Bankroll evolves linearly with " +
        "edge × volume — no recovery on losses, no compounding from wins. The honest baseline.";
    public bool RequiresEdgeInputs => false;
    public decimal NextBetSize(StrategyStep s) => s.InitialBetSize;
}

/// <summary>
/// Fractional-Kelly (fixed-fractional) staking: bet 2.5% of the current bankroll every step, so wins
/// compound and drawdowns shrink exposure. Outcome-only — it does not look at the edge, only the
/// post-settlement bankroll. Ruin-resistant by construction.
/// </summary>
public sealed class FractionalKellyStakingStrategy : IStakingStrategy
{
    /// <summary>Fraction of the current bankroll staked per step.</summary>
    public const decimal KellyFraction = 0.025m;

    public string Id => "kelly";
    public string Name => "Fractional Kelly";
    public string Description =>
        "Bet a fixed 2.5% of the current bankroll every step. Wins compound, drawdowns shrink exposure, " +
        "and exposure can never exceed the bankroll. The principled way to harvest a sustained edge at " +
        "low variance — without conditioning on the per-bet edge.";
    public bool RequiresEdgeInputs => false;

    public decimal NextBetSize(StrategyStep s)
    {
        var sized = KellyFraction * s.NextBankroll;
        return sized <= 0m ? 0m : Math.Round(sized, 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Whole-dollar fractional Kelly — the same 2.5%-of-bankroll dynamic, discretised to a $1-minimum,
/// whole-dollar venue. Bets <c>round(2.5% × bankroll)</c>, floored at $1; size 0 (bust) once the bank
/// can no longer cover $1.
/// </summary>
public sealed class WholeDollarKellyStakingStrategy : IStakingStrategy
{
    public const decimal KellyFraction = 0.025m;

    public string Id => "kelly-d1";
    public string Name => "Fractional Kelly ($1 steps)";
    public string Description =>
        "Bet ~2.5% of the current bankroll, rounded to whole dollars with a $1 minimum — fractional Kelly " +
        "for a $1-minimum, whole-dollar venue. Busts only when the bank can no longer cover $1.";
    public bool RequiresEdgeInputs => false;

    public decimal NextBetSize(StrategyStep s)
    {
        if (s.NextBankroll < 1m) return 0m;
        var sized = Math.Round(KellyFraction * s.NextBankroll, MidpointRounding.AwayFromZero);
        return Math.Max(1m, sized);
    }
}

/// <summary>
/// Edge-aware TRUE Kelly: size off the realised edge of the chosen side, not just the bankroll.
/// f* = (winProb − price) / (1 − price) for the chosen outcome (UP ⇒ winProb = pUp at the YES price;
/// DOWN ⇒ winProb = 1 − pUp at the NO price) — equivalently the spec form f* = pUp − (1 − pUp)/b with
/// b = (1 − price)/price. Apply a fractional multiplier (quarter-Kelly default) for variance control,
/// round to whole dollars, and SKIP (size 0) when there is no edge (f* ≤ 0) or the target rounds below
/// $1. Requires real odds inputs.
/// </summary>
public sealed class EdgeAwareKellyStakingStrategy : IStakingStrategy
{
    /// <summary>Fractional multiplier on full Kelly (quarter-Kelly default).</summary>
    public const decimal Fraction = 0.25m;

    public string Id => "kelly-edge";
    public string Name => "Edge-aware Kelly";
    public string Description =>
        "True Kelly on the chosen side's realised edge: f* = (winProb − price)/(1 − price), at a " +
        "quarter-Kelly multiplier, whole-dollar rounded. Sizes up when calibrated pUp diverges from the " +
        "market price and skips entirely when there is no edge or the bet rounds below $1.";
    public bool RequiresEdgeInputs => true;

    public decimal NextBetSize(StrategyStep s)
    {
        var pUp = s.Inputs.PUp;
        var up = pUp >= 0.5m;
        var price = up ? s.Inputs.YesPrice : s.Inputs.NoPrice;
        var winProb = up ? pUp : 1m - pUp;
        var fStar = KellyMath.FullKelly(winProb, price);
        if (fStar <= 0m) return 0m;                                   // no edge ⇒ skip
        var target = Math.Round(Fraction * fStar * s.NextBankroll, 2, MidpointRounding.AwayFromZero);
        return target < 1m ? 0m : target;                             // whole-dollar floor + sub-$1 skip
    }
}

/// <summary>
/// Static catalogue of built-in staking strategies. New strategies added to <see cref="All"/> become
/// selectable in the UI + addressable by id without other plumbing. Flat is the default.
/// </summary>
public static class StakingStrategies
{
    public static readonly IReadOnlyList<IStakingStrategy> All = new IStakingStrategy[]
    {
        new FlatStakingStrategy(),
        new MartingaleStakingStrategy(),
        new FractionalKellyStakingStrategy(),
        new WholeDollarKellyStakingStrategy(),
        new EdgeAwareKellyStakingStrategy(),
    };

    public const string DefaultId = "flat";

    public static IStakingStrategy Resolve(string? id) =>
        All.FirstOrDefault(s => s.Id == id) ?? All[0];

    public static bool IsKnown(string id) => All.Any(s => s.Id == id);
}
