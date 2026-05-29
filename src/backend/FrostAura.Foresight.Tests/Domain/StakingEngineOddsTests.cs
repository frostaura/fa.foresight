using FluentAssertions;
using FrostAura.Foresight.Domain.Trading;
using Xunit;

namespace FrostAura.Foresight.Tests.Domain;

public class StakingEngineOddsTests
{
    // --- Odds-based payoff (replaces even-money) ------------------------------------------------

    [Theory]
    [InlineData(0.52, 3.846153)]   // shares = 2 / 0.52
    [InlineData(0.55, 3.636363)]
    [InlineData(0.56, 3.571428)]
    public void Win_payout_is_shares_times_one_dollar(decimal entry, decimal expectedPayout)
    {
        var step = StakingEngine.Settle(side: "UP", entryPrice: entry, stake: 2m,
            currentBalance: 100m, outcomeUp: true, allowBorrow: false);

        step.Won.Should().BeTrue();
        step.Payout.Should().BeApproximately(expectedPayout, 0.0005m);
        // A $2 win at ~0.52–0.56 nets ~$3.5–3.8 (the WS-B acceptance number).
        step.Payout.Should().BeInRange(3.5m, 3.9m);
    }

    [Fact]
    public void Win_profit_is_stake_times_odds_ratio()
    {
        // $2 at 0.55 → profit = 2 * (0.45/0.55) ≈ 1.6364 → balance 100 → 101.6364.
        var step = StakingEngine.Settle("UP", 0.55m, 2m, 100m, outcomeUp: true, allowBorrow: false);
        step.BalanceAfter.Should().BeApproximately(101.6364m, 0.0005m);
    }

    [Fact]
    public void Loss_forfeits_the_stake_only()
    {
        var step = StakingEngine.Settle("UP", 0.55m, 2m, 100m, outcomeUp: false, allowBorrow: false);
        step.Won.Should().BeFalse();
        step.Payout.Should().Be(0m);
        step.BalanceAfter.Should().Be(98m);
    }

    [Fact]
    public void Down_side_wins_when_market_resolves_down()
    {
        // Bought NO at 0.55; market resolved DOWN ⇒ win.
        var step = StakingEngine.Settle("DOWN", 0.55m, 2m, 100m, outcomeUp: false, allowBorrow: false);
        step.Won.Should().BeTrue();
        step.BalanceAfter.Should().BeApproximately(101.6364m, 0.0005m);
    }

    [Fact]
    public void Strict_mode_rejects_settling_an_unaffordable_stake()
    {
        var act = () => StakingEngine.Settle("UP", 0.55m, 50m, 10m, outcomeUp: true, allowBorrow: false);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Allow_borrow_records_shortfall_and_busts()
    {
        var step = StakingEngine.Settle("UP", 0.55m, 50m, 10m, outcomeUp: false, allowBorrow: true);
        step.BorrowedShortfall.Should().Be(40m);
        step.BalanceAfter.Should().Be(-40m);
        step.CrossedZero.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.50, 0.55, "DOWN")]   // exactly 0.5 ⇒ UP per >= rule? 0.50 → UP
    [InlineData(0.60, 0.55, "UP")]
    [InlineData(0.40, 0.55, "DOWN")]
    public void DecideSide_threshold(decimal pUp, decimal _ignore, string _expectedIgnored)
    {
        // 0.5 maps to UP (>= rule); below 0.5 maps to DOWN.
        StakingEngine.DecideSide(0.5m).Should().Be("UP");
        StakingEngine.DecideSide(0.4999m).Should().Be("DOWN");
    }

    // --- Edge-aware true Kelly ------------------------------------------------------------------

    [Fact]
    public void EdgeAwareKelly_sizes_off_chosen_side_edge()
    {
        var k = new EdgeAwareKellyStakingStrategy();
        // pUp 0.60, YES priced 0.55 ⇒ f* = (0.60-0.55)/0.45 = 0.1111; quarter-Kelly × 1000 ≈ 27.78.
        var size = k.NextBetSize(new StrategyStep(0m, true, 2m, 1000m, new StakingInputs(0.60m, 0.55m, 0.45m)));
        size.Should().BeApproximately(27.78m, 0.01m);
    }

    [Fact]
    public void EdgeAwareKelly_skips_when_no_edge()
    {
        var k = new EdgeAwareKellyStakingStrategy();
        // Model barely up (0.52) but YES priced 0.55 ⇒ negative edge ⇒ skip.
        k.NextBetSize(new StrategyStep(0m, true, 2m, 1000m, new StakingInputs(0.52m, 0.55m, 0.45m)))
            .Should().Be(0m);
    }

    [Fact]
    public void EdgeAwareKelly_skips_when_target_rounds_below_one_dollar()
    {
        var k = new EdgeAwareKellyStakingStrategy();
        // Same edge but tiny bankroll ⇒ 0.25 * 0.1111 * 30 ≈ 0.83 < $1 ⇒ skip.
        k.NextBetSize(new StrategyStep(0m, true, 2m, 30m, new StakingInputs(0.60m, 0.55m, 0.45m)))
            .Should().Be(0m);
    }

    [Fact]
    public void EdgeAwareKelly_down_side_uses_no_price()
    {
        var k = new EdgeAwareKellyStakingStrategy();
        // pUp 0.40 ⇒ DOWN; winProb 0.60 at NO price 0.55 ⇒ same f* as the up case.
        var size = k.NextBetSize(new StrategyStep(0m, true, 2m, 1000m, new StakingInputs(0.40m, 0.45m, 0.55m)));
        size.Should().BeApproximately(27.78m, 0.01m);
    }

    [Fact]
    public void EdgeAwareKelly_matches_the_spec_p_minus_q_over_b_form()
    {
        // f* = pUp − (1−pUp)/b, b = (1−price)/price, must equal FullKelly(pUp, price).
        decimal pUp = 0.60m, price = 0.55m;
        var b = (1m - price) / price;
        var specForm = pUp - (1m - pUp) / b;
        KellyMath.FullKelly(pUp, price).Should().BeApproximately(specForm, 0.0000001m);
    }

    // --- Outcome-only strategies (unchanged dynamics, ignore edge inputs) -----------------------

    [Fact]
    public void Martingale_doubles_on_loss_resets_on_win_and_ignores_inputs()
    {
        var m = new MartingaleStakingStrategy();
        var edgyInputs = new StakingInputs(0.99m, 0.01m, 0.99m); // would matter if it leaked
        m.NextBetSize(new StrategyStep(4m, Won: false, 2m, 1000m, edgyInputs)).Should().Be(8m);
        m.NextBetSize(new StrategyStep(16m, Won: true, 2m, 5m, edgyInputs)).Should().Be(2m);
        m.RequiresEdgeInputs.Should().BeFalse();
    }

    [Fact]
    public void Flat_is_always_initial()
    {
        var f = new FlatStakingStrategy();
        f.NextBetSize(new StrategyStep(99m, false, 2m, 1000m, default)).Should().Be(2m);
        f.NextBetSize(new StrategyStep(99m, true, 2m, 5m, default)).Should().Be(2m);
    }

    [Fact]
    public void FractionalKelly_is_two_and_a_half_percent_of_bankroll()
    {
        var k = new FractionalKellyStakingStrategy();
        k.NextBetSize(new StrategyStep(10m, true, 2m, 1000m, default)).Should().Be(25.00m);
        k.NextBetSize(new StrategyStep(10m, false, 2m, 0m, default)).Should().Be(0m);
    }

    [Fact]
    public void WholeDollarKelly_floors_at_one_and_busts_below_one()
    {
        var k = new WholeDollarKellyStakingStrategy();
        k.NextBetSize(new StrategyStep(0m, true, 2m, 50m, default)).Should().Be(1m);   // round(1.25)=1
        k.NextBetSize(new StrategyStep(0m, true, 2m, 2000m, default)).Should().Be(50m);
        k.NextBetSize(new StrategyStep(10m, false, 2m, 0.5m, default)).Should().Be(0m);
    }

    [Fact]
    public void Catalogue_exposes_all_strategies_including_edge_aware()
    {
        StakingStrategies.IsKnown("kelly-edge").Should().BeTrue();
        StakingStrategies.Resolve("kelly-edge").Should().BeOfType<EdgeAwareKellyStakingStrategy>();
        StakingStrategies.Resolve(null).Id.Should().Be(StakingStrategies.DefaultId);
        StakingStrategies.All.Should().HaveCount(5);
    }

    // --- Step composition ------------------------------------------------------------------------

    [Fact]
    public void Step_returns_null_when_strategy_skips()
    {
        var edge = new EdgeAwareKellyStakingStrategy();
        // negative edge ⇒ strategy sizes 0 ⇒ Step is a no-bet.
        var step = StakingEngine.Step(edge, lastStake: 0m, lastWon: true, currentBalance: 1000m,
            pUpCalibrated: 0.52m, yesPrice: 0.55m, noPrice: 0.45m, outcomeUp: true,
            initialBetSize: 2m, allowBorrow: false);
        step.Should().BeNull();
    }

    [Fact]
    public void Step_settles_a_flat_bet_on_odds()
    {
        var flat = new FlatStakingStrategy();
        var step = StakingEngine.Step(flat, lastStake: 2m, lastWon: true, currentBalance: 100m,
            pUpCalibrated: 0.60m, yesPrice: 0.55m, noPrice: 0.45m, outcomeUp: true,
            initialBetSize: 2m, allowBorrow: false);
        step.Should().NotBeNull();
        step!.Side.Should().Be("UP");
        step.BalanceAfter.Should().BeApproximately(101.6364m, 0.0005m);
    }
}
