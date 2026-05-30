using FluentAssertions;
using FrostAura.Foresight.Domain.Positions;
using FrostAura.Foresight.Domain.Sizing;
using Xunit;

namespace FrostAura.Foresight.Tests.Domain;

/// <summary>
/// Fractional-Kelly sizing is pure math and the gate between a model signal and real money — it must
/// bet the higher-edge side, refuse no-edge / unusable prices, and round conservatively to zero
/// rather than place a degenerate order.
/// </summary>
public class KellyPositionSizerTests
{
    private readonly KellyPositionSizer _sizer = new();

    [Fact]
    public void Buys_yes_when_model_prob_exceeds_yes_price()
    {
        // q=0.70 vs yesPrice 0.50 → yesEdge 0.20; noEdge = 0.30 - 0.50 < 0. YES wins.
        var s = _sizer.Size(modelProbYes: 0.70m, yesPrice: 0.50m, noPrice: 0.50m,
            availableUsd: 1000m, kellyFraction: 0.25m);

        s.ShouldTrade.Should().BeTrue();
        s.Side.Should().Be(PositionSide.Yes);
        s.Edge.Should().BeApproximately(0.20m, 0.0001m);
        // f* = (0.70-0.50)/(1-0.50) = 0.40; frac = 0.40*0.25 = 0.10; stake = 0.10*1000 = 100.
        s.StakeUsd.Should().Be(100m);
        s.Shares.Should().Be(200m); // 100 / 0.50
        s.LimitPrice.Should().Be(0.50m);
    }

    [Fact]
    public void Buys_no_when_down_side_carries_the_edge()
    {
        // q=0.30 → yesEdge = 0.30 - 0.55 < 0; noEdge = (1-0.30) - 0.40 = 0.30 > 0. NO wins.
        var s = _sizer.Size(0.30m, yesPrice: 0.55m, noPrice: 0.40m, availableUsd: 1000m, kellyFraction: 0.25m);

        s.ShouldTrade.Should().BeTrue();
        s.Side.Should().Be(PositionSide.No);
        s.Edge.Should().BeApproximately(0.30m, 0.0001m);
    }

    [Fact]
    public void Refuses_when_no_side_has_positive_edge()
    {
        // Fairly priced both sides: q=0.50, prices 0.50/0.50 → both edges 0.
        var s = _sizer.Size(0.50m, 0.50m, 0.50m, 1000m, 0.25m);

        s.ShouldTrade.Should().BeFalse();
        s.StakeUsd.Should().Be(0m);
        s.Reason.Should().Contain("no positive edge");
    }

    [Fact]
    public void Refuses_unusable_price_at_or_above_one()
    {
        // Edge exists (q=0.99 vs yesPrice 1.0 → -0.01) so it falls to NO: noEdge = 0.01 - (-?)...
        // Use a price of exactly 1.0 on the chosen side to trip the p>=1 guard.
        var s = _sizer.Size(modelProbYes: 1.0m, yesPrice: 1.0m, noPrice: 0.99m,
            availableUsd: 1000m, kellyFraction: 0.25m);

        s.ShouldTrade.Should().BeFalse();
    }

    [Fact]
    public void Refuses_when_kelly_stake_rounds_to_zero()
    {
        // Tiny edge + tiny bankroll → stake rounds to 0.00 (f*≈0.02, frac≈0.005, *0.5 ≈ 0.0025 → 0.00).
        var s = _sizer.Size(0.51m, 0.50m, 0.50m, availableUsd: 0.5m, kellyFraction: 0.25m);

        s.ShouldTrade.Should().BeFalse();
        s.StakeUsd.Should().Be(0m);
    }

    [Fact]
    public void Clamps_model_probability_into_unit_interval()
    {
        // Out-of-range q is clamped to [0,1] before edge math — must not throw or over-bet.
        var s = _sizer.Size(modelProbYes: 5m, yesPrice: 0.40m, noPrice: 0.60m,
            availableUsd: 1000m, kellyFraction: 0.25m);

        s.ShouldTrade.Should().BeTrue();
        s.Side.Should().Be(PositionSide.Yes);
    }
}
