using FluentAssertions;
using FrostAura.Foresight.Application.Chaos;
using FrostAura.Foresight.Domain.Trading;
using Xunit;

namespace FrostAura.Foresight.Tests.Chaos;

/// <summary>
/// Pure unit tests for the chaos/bust test engine — no I/O, no DI, no database.
/// Verifies: determinism, known-edge survival, guaranteed-loss bust, percentile math.
/// </summary>
public class ChaosRunnerTests
{
    // ──────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a candidate set where every candle resolves UP with pUp > 0.5 and prices ~0.55/0.45.
    /// A strategy that sizes above 0 on UP should survive and profit on this set.
    /// </summary>
    private static BetCandidate[] BuildAlwaysWinCandidates(int n)
    {
        var result = new BetCandidate[n];
        for (var i = 0; i < n; i++)
            result[i] = new BetCandidate(
                TargetOpenTime: (long)(i + 1) * 300_000L,
                PUp: 0.60m,
                YesPrice: 0.55m,
                NoPrice: 0.45m,
                Synthetic: false,
                OutcomeUp: true);
        return result;
    }

    /// <summary>
    /// Builds a candidate set where every candle resolves DOWN with pUp > 0.5 (model always wrong).
    /// A flat bettor will lose every bet and should bust (with allowBorrow=false).
    /// </summary>
    private static BetCandidate[] BuildAlwaysLoseCandidates(int n)
    {
        var result = new BetCandidate[n];
        for (var i = 0; i < n; i++)
            result[i] = new BetCandidate(
                TargetOpenTime: (long)(i + 1) * 300_000L,
                PUp: 0.60m,
                YesPrice: 0.55m,
                NoPrice: 0.45m,
                Synthetic: false,
                OutcomeUp: false);   // model predicts UP, market resolves DOWN → loss every time
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // DeterministicRng tests
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeterministicRng_same_seed_produces_identical_sequence()
    {
        var rng1 = new DeterministicRng(42L);
        var rng2 = new DeterministicRng(42L);

        for (var i = 0; i < 1000; i++)
            rng1.NextDouble().Should().Be(rng2.NextDouble(), $"at step {i}");
    }

    [Fact]
    public void DeterministicRng_different_seeds_produce_different_sequences()
    {
        var rng1 = new DeterministicRng(1L);
        var rng2 = new DeterministicRng(2L);

        var a = rng1.NextDouble();
        var b = rng2.NextDouble();
        a.Should().NotBe(b);
    }

    [Fact]
    public void DeterministicRng_NextDouble_in_zero_one_range()
    {
        var rng = new DeterministicRng(999L);
        for (var i = 0; i < 10_000; i++)
        {
            var v = rng.NextDouble();
            v.Should().BeGreaterThanOrEqualTo(0.0, $"at step {i}");
            v.Should().BeLessThan(1.0, $"at step {i}");
        }
    }

    [Fact]
    public void DeterministicRng_NextInt_in_range()
    {
        var rng = new DeterministicRng(7L);
        for (var i = 0; i < 1_000; i++)
        {
            var v = rng.NextInt(100);
            v.Should().BeGreaterThanOrEqualTo(0);
            v.Should().BeLessThan(100);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // GenerateStartOffsets determinism
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenerateStartOffsets_same_seed_produces_identical_offsets()
    {
        var a = ChaosRunner.GenerateStartOffsets(sampleCount: 20, windowLen: 10, totalCandidates: 100, seed: 123L);
        var b = ChaosRunner.GenerateStartOffsets(sampleCount: 20, windowLen: 10, totalCandidates: 100, seed: 123L);

        a.Should().Equal(b);
    }

    [Fact]
    public void GenerateStartOffsets_different_seeds_produce_different_offsets()
    {
        var a = ChaosRunner.GenerateStartOffsets(sampleCount: 20, windowLen: 10, totalCandidates: 100, seed: 1L);
        var b = ChaosRunner.GenerateStartOffsets(sampleCount: 20, windowLen: 10, totalCandidates: 100, seed: 2L);

        // Very likely to differ; if by some coincidence they match the test is still valid.
        a.SequenceEqual(b).Should().BeFalse("different seeds should give different offsets");
    }

    [Fact]
    public void GenerateStartOffsets_all_offsets_within_valid_range()
    {
        const int windowLen = 10;
        const int total = 50;
        var offsets = ChaosRunner.GenerateStartOffsets(sampleCount: 30, windowLen: windowLen, totalCandidates: total, seed: 0L);

        foreach (var o in offsets)
        {
            o.Should().BeGreaterThanOrEqualTo(0);
            o.Should().BeLessThan(total - windowLen, $"offset {o} leaves room for a window of {windowLen}");
        }
    }

    [Fact]
    public void GenerateStartOffsets_returns_empty_when_candidates_too_short()
    {
        // window >= total → no valid start → empty result
        var offsets = ChaosRunner.GenerateStartOffsets(sampleCount: 10, windowLen: 50, totalCandidates: 30, seed: 0L);
        offsets.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // ReplayWindow — known-edge scenario
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReplayWindow_survives_and_profits_on_always_win_candidates()
    {
        var candidates = BuildAlwaysWinCandidates(50);
        var flat = new FlatStakingStrategy();

        var result = ChaosRunner.ReplayWindow(candidates, start: 0, windowLen: 40, flat,
            initialBalance: 100m, initialBet: 2m, allowBorrow: false);

        result.Survived.Should().BeTrue("model always wins, balance should stay positive");
        result.FinalBalance.Should().BeGreaterThan(100m, "40 winning bets should increase the balance");
        result.MaxDrawdown.Should().Be(0m, "no losses ⇒ balance never drops from its peak");
    }

    [Fact]
    public void ReplayWindow_busts_on_always_lose_candidates_strict_mode()
    {
        // With flat $2 bets and a $100 bankroll, the model loses every bet.
        // After 50 losses the balance is $0; strict mode (allowBorrow=false) halts at bust.
        var candidates = BuildAlwaysLoseCandidates(200);
        var flat = new FlatStakingStrategy();

        var result = ChaosRunner.ReplayWindow(candidates, start: 0, windowLen: 100, flat,
            initialBalance: 100m, initialBet: 2m, allowBorrow: false);

        result.Survived.Should().BeFalse("continuous losses deplete the bankroll");
    }

    [Fact]
    public void ReplayWindow_busts_with_martingale_on_always_lose_candidates()
    {
        // Martingale doubles on every loss — it hits bust faster than flat.
        var candidates = BuildAlwaysLoseCandidates(100);
        var martingale = new MartingaleStakingStrategy();

        var result = ChaosRunner.ReplayWindow(candidates, start: 0, windowLen: 50, martingale,
            initialBalance: 100m, initialBet: 2m, allowBorrow: false);

        result.Survived.Should().BeFalse("Martingale on continuous losses busts exponentially");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // Aggregate — correctness of percentile math + Pass flag
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_returns_all_zeros_for_empty_samples()
    {
        var agg = ChaosRunner.Aggregate(Guid.NewGuid(), "flat", 10, Array.Empty<ChaosSampleResult>(), 100m, 0.0);

        agg.BustRate.Should().Be(1.0);
        agg.Pass.Should().BeFalse();
    }

    [Fact]
    public void Aggregate_BustRate_is_fraction_of_not_survived()
    {
        var samples = new[]
        {
            new ChaosSampleResult(0L, Survived: true,  FinalBalance: 110m, MaxDrawdown: 0m,  ZeroCrossings: 0),
            new ChaosSampleResult(0L, Survived: true,  FinalBalance: 120m, MaxDrawdown: 5m,  ZeroCrossings: 0),
            new ChaosSampleResult(0L, Survived: false, FinalBalance:  50m, MaxDrawdown: 50m, ZeroCrossings: 1),
            new ChaosSampleResult(0L, Survived: false, FinalBalance:   0m, MaxDrawdown: 100m, ZeroCrossings: 2),
        };

        var agg = ChaosRunner.Aggregate(Guid.NewGuid(), "flat", 10, samples, 100m, 0.0);

        agg.BustRate.Should().BeApproximately(0.5, 0.0001, "2 of 4 windows did not survive");
        agg.Pass.Should().BeFalse("BustRate > 0 → not passing");
    }

    [Fact]
    public void Aggregate_Pass_is_true_when_all_survive_and_median_positive()
    {
        var samples = new[]
        {
            new ChaosSampleResult(0L, true, 105m, 5m,  0),
            new ChaosSampleResult(0L, true, 110m, 2m,  0),
            new ChaosSampleResult(0L, true, 115m, 0m,  0),
            new ChaosSampleResult(0L, true, 108m, 1m,  0),
            new ChaosSampleResult(0L, true, 112m, 3m,  0),
        };

        var agg = ChaosRunner.Aggregate(Guid.NewGuid(), "kelly-edge", 20, samples, 100m, 0.1);

        agg.BustRate.Should().Be(0.0);
        agg.ProfitP50.Should().BeGreaterThan(0m, "all windows profited");
        agg.Pass.Should().BeTrue();
    }

    [Fact]
    public void Aggregate_Pass_is_false_when_median_profit_negative()
    {
        // All windows survive but lose money → median profit < 0 → Pass = false.
        var samples = new[]
        {
            new ChaosSampleResult(0L, true, 90m, 10m, 0),
            new ChaosSampleResult(0L, true, 95m,  5m, 0),
            new ChaosSampleResult(0L, true, 92m,  8m, 0),
        };

        var agg = ChaosRunner.Aggregate(Guid.NewGuid(), "flat", 10, samples, 100m, 0.0);

        agg.BustRate.Should().Be(0.0);
        agg.ProfitP50.Should().BeLessThan(0m);
        agg.Pass.Should().BeFalse();
    }

    [Fact]
    public void Aggregate_percentiles_on_known_distribution()
    {
        // Profits: -10, 0, 10, 20, 30 (5 elements). With initialBalance=100:
        //   FinalBalances: 90, 100, 110, 120, 130.
        // P5  = profit at index 0.2 → linear interp(90,100)×t = between -10 and 0.
        // P50 = profit at index 2.0 → exactly 10.
        // P95 = profit at index 3.8 → between 20 and 30.
        var samples = new[]
        {
            new ChaosSampleResult(0L, true, 90m,  0m, 0),
            new ChaosSampleResult(0L, true, 100m, 0m, 0),
            new ChaosSampleResult(0L, true, 110m, 0m, 0),
            new ChaosSampleResult(0L, true, 120m, 0m, 0),
            new ChaosSampleResult(0L, true, 130m, 0m, 0),
        };

        var agg = ChaosRunner.Aggregate(Guid.NewGuid(), "flat", 10, samples, 100m, 0.0);

        // P5:  position = 0.05 × 4 = 0.2 → lower=0, upper=1, t=0.2
        //      = sorted[0]×0.8 + sorted[1]×0.2 = -10×0.8 + 0×0.2 = -8
        // P50: position = 0.50 × 4 = 2.0 → exact index 2 → sorted[2] = 10
        // P95: position = 0.95 × 4 = 3.8 → lower=3, upper=4, t=0.8
        //      = sorted[3]×0.2 + sorted[4]×0.8 = 20×0.2 + 30×0.8 = 4+24 = 28
        agg.ProfitP50.Should().BeApproximately(10m, 0.001m, "median of [-10,0,10,20,30] is 10");
        agg.ProfitP5.Should().BeApproximately(-8m, 0.001m, "P5 interpolates: -10×0.8 + 0×0.2 = -8");
        agg.ProfitP95.Should().BeApproximately(28m, 0.001m, "P95 interpolates: 20×0.2 + 30×0.8 = 28");
    }

    [Fact]
    public void Aggregate_SyntheticFraction_is_preserved_from_input()
    {
        var samples = new[] { new ChaosSampleResult(0L, true, 110m, 0m, 0) };
        var agg = ChaosRunner.Aggregate(Guid.NewGuid(), "flat", 10, samples, 100m, syntheticFraction: 0.42);

        agg.SyntheticBetFraction.Should().BeApproximately(0.42m, 0.001m);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // End-to-end determinism: same seed → identical aggregates
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Full_chaos_run_is_byte_identical_for_same_seed()
    {
        var candidates = BuildAlwaysWinCandidates(200);
        var flat = new FlatStakingStrategy();
        var modelId = Guid.NewGuid();

        static ChaosComboAggregate Run(BetCandidate[] cands, IStakingStrategy strat, Guid mid, long seed)
        {
            var offsets = ChaosRunner.GenerateStartOffsets(30, 50, cands.Length, seed);
            var results = offsets.Select(o => ChaosRunner.ReplayWindow(cands, o, 50, strat, 100m, 2m, false)).ToArray();
            return ChaosRunner.Aggregate(mid, strat.Id, 50, results, 100m, 0.0);
        }

        var agg1 = Run(candidates, flat, modelId, seed: 77L);
        var agg2 = Run(candidates, flat, modelId, seed: 77L);

        agg1.Should().Be(agg2, "same seed must produce byte-identical aggregates");
        agg1.BustRate.Should().Be(0.0, "every bet wins, nothing busts");
        agg1.ProfitP50.Should().BeGreaterThan(0m);
        agg1.Pass.Should().BeTrue();
    }

    [Fact]
    public void Full_chaos_run_always_lose_busts_completely()
    {
        var candidates = BuildAlwaysLoseCandidates(200);
        var flat = new FlatStakingStrategy();
        var modelId = Guid.NewGuid();

        var offsets = ChaosRunner.GenerateStartOffsets(20, 60, candidates.Length, seed: 0L);
        var results = offsets.Select(o => ChaosRunner.ReplayWindow(candidates, o, 60, flat, 100m, 2m, false)).ToArray();
        var agg = ChaosRunner.Aggregate(modelId, flat.Id, 60, results, 100m, 0.0);

        agg.BustRate.Should().Be(1.0, "every window should bust on continuous losses");
        agg.Pass.Should().BeFalse();
    }
}
