using FrostAura.Foresight.Domain.Trading;

namespace FrostAura.Foresight.Application.Chaos;

/// <summary>
/// Pure chaos/bust test engine — no I/O, no DI, no side effects.
///
/// The engine is a pure computation over a precomputed <see cref="BetCandidate"/> array:
/// 1. <see cref="GenerateStartOffsets"/> — splitmix64-seeded uniform draws over [0, candidates − windowLen].
/// 2. <see cref="ReplayWindow"/> — replays one window of BetCandidates under a staking strategy.
/// 3. <see cref="Aggregate"/> — computes survival/profit percentiles across the sampled results.
///
/// DETERMINISM CONTRACT (load-bearing — do NOT break):
///   same seed + same candidates ⇒ identical offsets, identical per-window outcomes,
///   identical aggregates, byte-for-byte, across runs and machines.
///
/// BANNED: <see cref="System.Random"/>, <see cref="DateTime"/>, any wall-clock or thread-local
///   entropy source. The <see cref="DeterministicRng"/> is the only allowed random source.
/// </summary>
public static class ChaosRunner
{
    // ──────────────────────────────────────────────────────────────────────────────────────
    // 1.  GENERATE START OFFSETS
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates <paramref name="sampleCount"/> uniformly distributed, unique-ish start indices
    /// into a candidate array of length <paramref name="totalCandidates"/>, each leaving at least
    /// <paramref name="windowLen"/> candidates to the right.
    ///
    /// If the candidate set is too short (totalCandidates &lt;= windowLen) we return an empty list
    /// rather than crashing — the caller surfaces a "not enough candidates" diagnostic and moves on.
    /// If sampleCount &gt; available slots, we silently clamp to the available count.
    ///
    /// Note: draws are WITH replacement (duplicates possible for very small candidate sets) —
    /// this is intentional; sampling is fast and the window length makes any two overlapping
    /// windows cover different price regimes.
    /// </summary>
    public static IReadOnlyList<int> GenerateStartOffsets(
        int sampleCount, int windowLen, int totalCandidates, long seed)
    {
        var maxStart = totalCandidates - windowLen;
        if (maxStart <= 0) return Array.Empty<int>();

        var rng = new DeterministicRng(seed);
        var count = Math.Min(sampleCount, maxStart);        // clamp — can't draw more distinct slots than exist
        var offsets = new List<int>(count);
        for (var i = 0; i < count; i++)
            offsets.Add(rng.NextInt(maxStart));              // [0, maxStart)

        return offsets;
    }

    /// <summary>
    /// A sampled chaos window expressed as a slice of the candidate array: the candidates whose
    /// TargetOpenTime falls inside [start, start + windowLen·interval). Count may be 0 — a window
    /// in which the model abstained on every candle is a legitimate outcome (survived, zero
    /// profit), not an error.
    /// </summary>
    public readonly record struct TimeWindow(int Start, int Count);

    /// <summary>
    /// Time-based window sampling. <see cref="GenerateStartOffsets"/> indexes windows by
    /// candidate-array position, which silently assumes one candidate per candle. Models that
    /// ABSTAIN (confidence gate / OOD guard emit pUp = 0.5) produce sparse candidate arrays, so a
    /// window length expressed in candles can exceed the candidate count even when the candidates
    /// span weeks — the v3-bag models hit exactly that. Here windows are sampled on the TIME axis
    /// instead: a window is windowLen·intervalMs of market time, and its candidates are whatever
    /// the model chose to bet inside it. Same determinism contract: seeded splitmix64 only.
    /// </summary>
    public static IReadOnlyList<TimeWindow> GenerateTimeWindows(
        int sampleCount, int windowLen, long intervalMs, IReadOnlyList<BetCandidate> candidates, long seed)
    {
        if (candidates.Count == 0) return Array.Empty<TimeWindow>();
        var spanMs = (long)windowLen * intervalMs;
        var t0 = candidates[0].TargetOpenTime;
        var tLast = candidates[^1].TargetOpenTime;
        // Distinct interval-aligned start slots whose full window still fits inside the range.
        var maxStartSlot = (int)((tLast - t0 - spanMs) / intervalMs);
        if (maxStartSlot <= 0) return Array.Empty<TimeWindow>();

        var rng = new DeterministicRng(seed);
        var count = Math.Min(sampleCount, maxStartSlot);
        var windows = new List<TimeWindow>(count);
        for (var i = 0; i < count; i++)
        {
            var ws = t0 + rng.NextInt(maxStartSlot) * intervalMs;
            var we = ws + spanMs;
            var start = LowerBound(candidates, ws);
            var end = LowerBound(candidates, we);
            windows.Add(new TimeWindow(start, end - start));
        }
        return windows;

        static int LowerBound(IReadOnlyList<BetCandidate> c, long t)
        {
            int lo = 0, hi = c.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (c[mid].TargetOpenTime < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // 2.  REPLAY WINDOW
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replays one window of <see cref="BetCandidate"/>s under a <see cref="IStakingStrategy"/>.
    ///
    /// State initialised per-window:
    ///   balance = initialBalance, lastStake = initialBet, lastWon = true.
    ///
    /// Per candidate:
    ///   1. Skip if pUp == 0.5m (no-opinion signal — mirrors the backtest abstention rule).
    ///   2. Size the next bet via the strategy.
    ///   3. If stake ≤ 0 → skip (gate signal).
    ///   4. If !allowBorrow &amp;&amp; stake &gt; balance → BUST, stop.
    ///   5. Settle via <see cref="StakingEngine.Settle"/> and update running metrics.
    ///
    /// Survived = !busted AND balance stayed &gt; 0 throughout (a balance that goes negative via
    /// allowBorrow=true is recorded but marks the window as NOT survived — matches the live contract).
    /// </summary>
    public static ChaosSampleResult ReplayWindow(
        IReadOnlyList<BetCandidate> candidates,
        int start,
        int windowLen,
        IStakingStrategy strategy,
        decimal initialBalance,
        decimal initialBet,
        bool allowBorrow)
    {
        var balance = initialBalance;
        var lastStake = initialBet;
        var lastWon = true;
        var peakBalance = initialBalance;
        var maxDrawdown = 0m;
        var zeroCrossings = 0;
        var busted = false;

        var end = Math.Min(start + windowLen, candidates.Count);
        for (var i = start; i < end; i++)
        {
            var c = candidates[i];

            // Flat-Baseline abstention: a model that emits exactly 0.5 has no opinion — skip.
            if (c.PUp == 0.5m) continue;

            // Size via the strategy's pure function.
            var stake = strategy.NextBetSize(
                new StrategyStep(lastStake, lastWon, initialBet, balance, new StakingInputs(c.PUp, c.YesPrice, c.NoPrice)));

            if (stake <= 0m) continue;                      // gate / sub-$1 skip

            // Strict-bust gate: a stake that exceeds the balance is an unaffordable bet.
            if (!allowBorrow && stake > balance)
            {
                busted = true;
                break;
            }

            // Settle.
            var side = StakingEngine.DecideSide(c.PUp);
            var entryPrice = side == "UP" ? c.YesPrice : c.NoPrice;

            StakingStep step;
            try
            {
                step = StakingEngine.Settle(side, entryPrice, stake, balance, c.OutcomeUp, allowBorrow);
            }
            catch (InvalidOperationException)
            {
                // Belt-and-suspenders: allowBorrow=false with stake > balance should have been caught
                // above; this catches any other degenerate state.
                busted = true;
                break;
            }

            // Update running state.
            var balanceBefore = balance;
            balance = step.BalanceAfter;
            lastStake = stake;
            lastWon = step.Won;

            if (step.CrossedZero || (StakingEngine.SignChanged(balanceBefore, balance)))
                zeroCrossings++;

            if (balance > peakBalance) peakBalance = balance;
            var drawdown = peakBalance - balance;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;

            // A negative balance (borrow path) is an implicit bust in the survival metric.
            if (balance <= 0m)
            {
                busted = true;
                break;
            }
        }

        var survived = !busted && balance > 0m;
        return new ChaosSampleResult(
            StartMs: candidates.Count > start ? candidates[start].TargetOpenTime : 0L,
            Survived: survived,
            FinalBalance: balance,
            MaxDrawdown: maxDrawdown,
            ZeroCrossings: zeroCrossings);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // 3.  AGGREGATE
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates an array of per-window sample results into a single combo summary.
    ///
    /// BustRate = (#windows where Survived == false) / N (zero when all survive).
    /// Profit percentiles are computed over (FinalBalance − initialBalance) using a simple
    /// sort-based O(N log N) percentile (exact for small N, fast enough for SampleCount ≤ 1000).
    /// Pass = BustRate == 0 AND ProfitP50 &gt; 0.
    /// </summary>
    public static ChaosComboAggregate Aggregate(
        Guid modelId,
        string strategyId,
        int windowLen,
        ChaosSampleResult[] samples,
        decimal initialBalance,
        double syntheticFraction)
    {
        var n = samples.Length;
        if (n == 0)
        {
            return new ChaosComboAggregate(
                ModelId: modelId,
                StrategyId: strategyId,
                WindowLength: windowLen,
                BustRate: 1.0,
                ProfitP5: 0m,
                ProfitP50: 0m,
                ProfitP95: 0m,
                ProfitMean: 0m,
                WorstDrawdown: 0m,
                MeanZeroCrossings: 0.0,
                SyntheticBetFraction: (decimal)syntheticFraction,
                Pass: false);
        }

        var bustCount = samples.Count(s => !s.Survived);
        var bustRate = (double)bustCount / n;

        // Compute profits and sort for percentiles.
        var profits = samples.Select(s => s.FinalBalance - initialBalance).OrderBy(p => p).ToArray();
        var p5 = Percentile(profits, 0.05);
        var p50 = Percentile(profits, 0.50);
        var p95 = Percentile(profits, 0.95);
        var profitMean = profits.Average();

        var worstDd = samples.Max(s => s.MaxDrawdown);
        var meanZero = samples.Average(s => (double)s.ZeroCrossings);

        var pass = bustRate == 0.0 && p50 > 0m;

        return new ChaosComboAggregate(
            ModelId: modelId,
            StrategyId: strategyId,
            WindowLength: windowLen,
            BustRate: bustRate,
            ProfitP5: p5,
            ProfitP50: p50,
            ProfitP95: p95,
            ProfitMean: profitMean,
            WorstDrawdown: worstDd,
            MeanZeroCrossings: meanZero,
            SyntheticBetFraction: (decimal)syntheticFraction,
            Pass: pass);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Linear-interpolation percentile on a sorted array (same definition Excel uses for PERCENTILE.INC).
    /// For N=1, returns the single value. For an empty array, returns 0.
    /// </summary>
    private static decimal Percentile(decimal[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0m;
        if (sorted.Length == 1) return sorted[0];

        var position = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];

        var t = (decimal)(position - lower);
        return sorted[lower] * (1m - t) + sorted[upper] * t;
    }
}
