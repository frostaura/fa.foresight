namespace FrostAura.Foresight.Domain.Trading;

/// <summary>
/// Pure staking-step math shared by live paper trading, backtest replay, the chaos/bust engine, and
/// live execution. The strategy-specific size dynamic comes from an <see cref="IStakingStrategy"/>;
/// the engine owns the common pieces — side selection from the calibrated probability, share-count
/// and odds-based payoff resolution, and bust / borrow handling.
///
/// Payoff is FAITHFUL PREDICTION-MARKET ODDS, not even money. A stake buys <c>stake / entryPrice</c>
/// shares of the chosen outcome at price <c>entryPrice ∈ (0,1)</c>. A win pays <c>shares × $1</c>
/// (profit <c>= stake × (1 − p) / p</c>); a loss forfeits the stake. Bust is <c>balance ≤ 0</c>;
/// strict mode (allowBorrow = false) means a stake may never exceed the balance.
///
/// SIZING HAPPENS AT PLACEMENT, NOT AT SETTLEMENT. Edge-aware strategies size against the UPCOMING
/// candle's calibrated pUp and market price, so the next stake cannot be known when the prior bet
/// settles. Callers therefore: (1) <see cref="Settle"/> the resolved bet to update the balance, then
/// (2) call <see cref="IStakingStrategy.NextBetSize"/> at the next placement with that candle's
/// edge inputs. <see cref="Step"/> composes both for the same-tick backtest convenience.
/// </summary>
public static class StakingEngine
{
    /// <summary>Side selection: pUp ≥ 0.5 ⇒ UP (buy YES), else DOWN (buy NO).</summary>
    public static string DecideSide(decimal pUpCalibrated) => pUpCalibrated >= 0.5m ? "UP" : "DOWN";

    /// <summary>
    /// Confidence "no-bet" band, expressed as min-confidence where confidence = |pUp − 0.5|·2.
    /// 0.04 ⇒ a ±2pp band around 0.5 (pUp ∈ (0.48, 0.52)). Shared with the chart's gate toggle.
    /// </summary>
    public const decimal DefaultNoBetBand = 0.04m;

    /// <summary>
    /// Bankroll ceiling so compounding strategies can't overflow during a long backtest. The
    /// persistence columns are numeric(20,4) (~1e16); the ceiling sits well under that at 1e12.
    /// </summary>
    public const decimal MaxBalance = 1_000_000_000_000m;

    /// <summary>True when |pUp − 0.5|·2 &lt; <paramref name="band"/> (too close to a coin-flip to bet).</summary>
    public static bool IsNoBet(decimal pUp, decimal band) => Math.Abs(pUp - 0.5m) * 2m < band;

    /// <summary>Shares purchased for <paramref name="stake"/> at <paramref name="entryPrice"/> ∈ (0,1). 0 at degenerate prices.</summary>
    public static decimal Shares(decimal stake, decimal entryPrice)
        => entryPrice <= 0m ? 0m : Math.Round(stake / entryPrice, 6, MidpointRounding.ToZero);

    /// <summary>Profit on a winning position: shares×$1 − stake = stake × (1 − p) / p. 0 at degenerate prices.</summary>
    public static decimal WinProfit(decimal stake, decimal entryPrice)
        => entryPrice <= 0m || entryPrice >= 1m ? 0m : stake * ((1m - entryPrice) / entryPrice);

    /// <summary>
    /// Settle a placed bet against the market's resolved outcome. <paramref name="side"/> is the side
    /// bought (UP=YES, DOWN=NO); <paramref name="entryPrice"/> is the price PAID for that side
    /// (YES price if UP, NO price if DOWN); <paramref name="outcomeUp"/> is the MARKET's resolution
    /// (true = resolved UP/YES). The outcome is supplied by the caller (venue settlement in live /
    /// chaos; the alignment-mapped candle in pure-candle backtests) — the engine never infers it.
    /// </summary>
    public static StakingStep Settle(
        string side,
        decimal entryPrice,
        decimal stake,
        decimal currentBalance,
        bool outcomeUp,
        bool allowBorrow)
    {
        var won = (side == "UP" && outcomeUp) || (side == "DOWN" && !outcomeUp);

        decimal shortfall = 0m;
        if (stake > currentBalance)
        {
            if (!allowBorrow)
                throw new InvalidOperationException(
                    $"StakingEngine.Settle called with stake ({stake}) > balance ({currentBalance}) but allowBorrow=false.");
            shortfall = stake - Math.Max(0m, currentBalance);
        }

        var shares = Shares(stake, entryPrice);
        var payout = won ? shares * 1m : 0m;
        var balanceAfter = won ? currentBalance + WinProfit(stake, entryPrice) : currentBalance - stake;
        if (balanceAfter > MaxBalance) balanceAfter = MaxBalance;

        var crossedZero = shortfall > 0m || SignChanged(currentBalance, balanceAfter);
        return new StakingStep(side, won, shares, payout, balanceAfter, shortfall, crossedZero);
    }

    /// <summary>
    /// Backtest/chaos convenience: size the bet from the strategy + this candle's inputs, decide side
    /// from pUp, settle in one call. Returns the settled step plus the stake actually placed. A stake
    /// of 0 (gate / sub-$1 skip / no edge) is a no-bet — callers skip the candle.
    /// </summary>
    public static StakingStep? Step(
        IStakingStrategy strategy,
        decimal lastStake,
        bool lastWon,
        decimal currentBalance,
        decimal pUpCalibrated,
        decimal yesPrice,
        decimal noPrice,
        bool outcomeUp,
        decimal initialBetSize,
        bool allowBorrow)
    {
        var inputs = new StakingInputs(pUpCalibrated, yesPrice, noPrice);
        var stake = strategy.NextBetSize(new StrategyStep(lastStake, lastWon, initialBetSize, currentBalance, inputs));
        if (stake <= 0m) return null;                       // gate / sub-$1 / no-edge ⇒ no bet
        if (!allowBorrow && stake > currentBalance) return null; // strict: unaffordable ⇒ caller treats as bust
        var side = DecideSide(pUpCalibrated);
        var entryPrice = side == "UP" ? yesPrice : noPrice;
        return Settle(side, entryPrice, stake, currentBalance, outcomeUp, allowBorrow);
    }

    /// <summary>True when the balance's sign changed across the step (0↔non-zero counts).</summary>
    public static bool SignChanged(decimal before, decimal after) => Math.Sign(before) != Math.Sign(after);
}

/// <summary>The edge inputs a strategy may size against: the calibrated up-probability and the venue YES/NO prices.</summary>
public readonly record struct StakingInputs(decimal PUp, decimal YesPrice, decimal NoPrice);

/// <summary>
/// Sizing context for the NEXT bet. <paramref name="CurrentBetSize"/>/<paramref name="Won"/> describe
/// the just-resolved bet (for path-dependent strategies like Martingale); <paramref name="NextBankroll"/>
/// is the post-settlement balance (for compounding strategies); <paramref name="Inputs"/> are the
/// UPCOMING candle's edge inputs (for edge-aware strategies).
/// </summary>
public readonly record struct StrategyStep(
    decimal CurrentBetSize, bool Won, decimal InitialBetSize, decimal NextBankroll, StakingInputs Inputs);

public sealed record StakingStep(
    string Side,
    bool Won,
    decimal Shares,
    decimal Payout,
    decimal BalanceAfter,
    decimal BorrowedShortfall,
    bool CrossedZero);
