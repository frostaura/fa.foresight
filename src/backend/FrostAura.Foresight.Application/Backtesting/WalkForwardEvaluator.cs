using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Foresight.Application.Backtesting;

/// <summary>
/// Rolling-origin (expanding-window) walk-forward evaluation — the honest measurement tool for
/// "does this model actually generalise?". For each fold it RE-TRAINS the model on the train block,
/// inserts an embargo gap (so the last train label, which looks <c>2</c> candles ahead under the
/// 2-step canon, can't overlap the first out-of-sample sample), then backtests strictly on the
/// following out-of-sample block. It also backtests on the train block to measure the in-sample
/// vs out-of-sample gap — the overfit tripwire.
///
/// Why this exists: a single-pass backtest with statically-trained coefficients can be trained and
/// scored on overlapping data, inflating the headline. Walk-forward is the gold standard against
/// both look-ahead leakage and regime-dependence: every reported number is genuinely out-of-sample,
/// across multiple temporally-disjoint folds.
/// </summary>
public sealed class WalkForwardEvaluator
{
    private readonly ModelTrainer _trainer;
    private readonly BacktestRunner _backtester;
    private readonly ILogger<WalkForwardEvaluator> _logger;

    // The 2-step label reaches close(i+2); an embargo of a few candles between train-end and
    // OOS-start guarantees no train label overlaps an OOS feature window.
    private const int EmbargoCandles = 3;

    public WalkForwardEvaluator(
        IFlowExecutor executor,
        IHistoricalCandleProvider candles,
        ILogger<WalkForwardEvaluator>? logger = null,
        IHistoricalMicrostructureProvider? micro = null)
    {
        _trainer = new ModelTrainer(executor, candles, micro);
        _backtester = new BacktestRunner(executor, candles, NullLogger<BacktestRunner>.Instance, micro);
        _logger = logger ?? NullLogger<WalkForwardEvaluator>.Instance;
    }

    public async Task<WalkForwardResult> EvaluateAsync(
        FlowDefinition flow,
        Guid tenantId,
        Guid modelId,
        string symbol,
        string interval,
        long rangeStartMs,
        long rangeEndMs,
        int folds,
        CancellationToken ct,
        int horizonSteps = 2)
    {
        if (folds < 2) throw new ArgumentOutOfRangeException(nameof(folds), "Need at least 2 folds.");
        if (rangeEndMs <= rangeStartMs) throw new ArgumentException("rangeEndMs must be > rangeStartMs.");

        var intervalMs = BacktestRunner.PublicIntervalMs(interval);
        // Embargo must be ≥ the horizon: the last train label reaches close(trainEnd_index + horizon),
        // so the gap before OOS must cover that lookahead to prevent any train/OOS overlap.
        var embargoMs = Math.Max(EmbargoCandles, horizonSteps) * intervalMs;

        // Split [rangeStart, rangeEnd] into folds+1 equal time buckets. Bucket 0 is the initial
        // train seed; folds k=1..folds each train on [start, boundary_k] (expanding) and validate
        // OOS on (boundary_k + embargo, boundary_{k+1}].
        var bucketMs = (rangeEndMs - rangeStartMs) / (folds + 1);
        if (bucketMs <= embargoMs)
            throw new ArgumentException($"Range too short for {folds} folds at interval {interval}: each bucket {bucketMs}ms ≤ embargo {embargoMs}ms.");

        var foldResults = new List<WalkForwardFold>(folds);
        int totalOosBets = 0, totalOosWins = 0;
        double brierWeighted = 0.0;

        for (var k = 1; k <= folds; k++)
        {
            ct.ThrowIfCancellationRequested();

            var trainStart = rangeStartMs;
            var trainEnd = rangeStartMs + k * bucketMs;
            var oosStart = trainEnd + embargoMs;
            var oosEnd = rangeStartMs + (k + 1) * bucketMs;
            if (oosEnd <= oosStart) continue;

            TrainResult trained;
            try
            {
                trained = await _trainer.TrainAsync(flow, tenantId, modelId, symbol, interval, trainStart, trainEnd, ct, horizonSteps);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Walk-forward fold {Fold}: training failed, skipping", k);
                continue;
            }

            var oos = await SafeBacktest(flow, trained.TrainedStateJson, tenantId, modelId, symbol, interval, oosStart, oosEnd, ct, horizonSteps);
            // In-sample: same trained coefficients scored on (a tail of) the train window. Measures
            // how much better the model looks on data it was fit on — the overfit gap.
            var inSample = await SafeBacktest(flow, trained.TrainedStateJson, tenantId, modelId, symbol, interval, trainStart, trainEnd, ct, horizonSteps);

            var oosBets = oos?.BetsPlaced ?? 0;
            var oosWins = oos?.BetsWon ?? 0;
            totalOosBets += oosBets;
            totalOosWins += oosWins;
            if (oos?.BrierScore is { } b) brierWeighted += (double)b * oosBets;

            foldResults.Add(new WalkForwardFold(
                Index: k,
                TrainStart: trainStart, TrainEnd: trainEnd, OosStart: oosStart, OosEnd: oosEnd,
                OosBets: oosBets,
                OosHitRate: oos?.HitRate,
                OosBrier: oos?.BrierScore,
                InSampleHitRate: inSample?.HitRate,
                ValidationAccuracy: trained.ValidationAccuracy));

            _logger.LogInformation(
                "Walk-forward fold {Fold}/{Folds}: OOS hit {Oos:P2} ({Bets} bets), in-sample {In:P2}, val {Val:P2}, Brier {Brier:F4}",
                k, folds, oos?.HitRate, oosBets, inSample?.HitRate, trained.ValidationAccuracy, oos?.BrierScore);
        }

        decimal? oosHit = totalOosBets == 0 ? null : (decimal)totalOosWins / totalOosBets;
        var (ciLo, ciHi) = WilsonCi(totalOosWins, totalOosBets);
        decimal? meanBrier = totalOosBets == 0 ? null : (decimal)(brierWeighted / totalOosBets);
        var inSampleRates = foldResults.Where(f => f.InSampleHitRate is not null).Select(f => f.InSampleHitRate!.Value).ToList();
        decimal? inSampleMean = inSampleRates.Count == 0 ? null : inSampleRates.Average();
        decimal? overfitGap = inSampleMean is not null && oosHit is not null ? inSampleMean - oosHit : null;
        var foldsAboveHalf = foldResults.Count(f => f.OosHitRate is { } h && h > 0.5m);

        return new WalkForwardResult(
            Folds: foldResults,
            TotalOosBets: totalOosBets,
            TotalOosWins: totalOosWins,
            OosHitRate: oosHit,
            OosHitRateCiLow: totalOosBets == 0 ? null : (decimal)ciLo,
            OosHitRateCiHigh: totalOosBets == 0 ? null : (decimal)ciHi,
            MeanBrier: meanBrier,
            InSampleHitRate: inSampleMean,
            OverfitGap: overfitGap,
            FoldsAboveHalf: foldsAboveHalf);
    }

    private async Task<BacktestOutcome?> SafeBacktest(
        FlowDefinition flow, string trainedStateJson, Guid tenantId, Guid modelId,
        string symbol, string interval, long startMs, long endMs, CancellationToken ct, int horizonSteps = 2)
    {
        try
        {
            return await _backtester.RunAsync(
                flow, trainedStateJson, tenantId, modelId, symbol, interval, startMs, endMs,
                initialBalance: 1000m, initialBetSize: 10m, allowBorrow: true,
                strategy: new FlatStakingStrategy(), progress: null, ct: ct, horizonSteps: horizonSteps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Walk-forward backtest failed for [{Start},{End}]", startMs, endMs);
            return null;
        }
    }

    /// <summary>
    /// Wilson score interval for a binomial proportion — the honest "is this edge real?" band on a
    /// hit-rate. Narrower and better-behaved at small n than the normal approximation.
    /// </summary>
    internal static (double Low, double High) WilsonCi(int wins, int n, double z = 1.96)
    {
        if (n == 0) return (0, 0);
        var p = (double)wins / n;
        var denom = 1 + z * z / n;
        var center = (p + z * z / (2.0 * n)) / denom;
        var margin = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denom;
        return (center - margin, center + margin);
    }
}

public sealed record WalkForwardFold(
    int Index,
    long TrainStart,
    long TrainEnd,
    long OosStart,
    long OosEnd,
    int OosBets,
    decimal? OosHitRate,
    decimal? OosBrier,
    decimal? InSampleHitRate,
    decimal ValidationAccuracy);

/// <summary>
/// Aggregate walk-forward result. <see cref="OosHitRate"/> + <see cref="OosHitRateCiLow"/> are the
/// numbers that decide whether an edge is real; <see cref="OverfitGap"/> (in-sample − OOS) is the
/// overfit tripwire; <see cref="FoldsAboveHalf"/> checks regime robustness (not a single lucky
/// window). See <see cref="PassesGuards"/> for the iteration-loop accept/reject gate.
/// </summary>
public sealed record WalkForwardResult(
    IReadOnlyList<WalkForwardFold> Folds,
    int TotalOosBets,
    int TotalOosWins,
    decimal? OosHitRate,
    decimal? OosHitRateCiLow,
    decimal? OosHitRateCiHigh,
    decimal? MeanBrier,
    decimal? InSampleHitRate,
    decimal? OverfitGap,
    int FoldsAboveHalf)
{
    /// <summary>
    /// The honesty gate from the plan, encoded. An edge is "real" only when ALL hold:
    ///   • OOS hit-rate ≥ <paramref name="targetHitRate"/> AND its 95% CI lower bound &gt; 0.50;
    ///   • OOS sample ≥ <paramref name="minBets"/> (tight enough CI to trust);
    ///   • OOS &gt; 50% in a MAJORITY of folds (regime robustness, not one lucky window);
    ///   • in-sample − OOS gap ≤ <paramref name="maxOverfitGap"/> (not memorising the train set).
    /// Returns the verdict plus a human-readable reason for the iteration log.
    /// </summary>
    public (bool Pass, string Reason) PassesGuards(
        decimal targetHitRate = 0.60m,
        int minBets = 1000,
        decimal maxOverfitGap = 0.03m)
    {
        if (OosHitRate is null) return (false, "no OOS bets placed");
        if (TotalOosBets < minBets) return (false, $"sample too small: {TotalOosBets} < {minBets} OOS bets");
        if (OosHitRateCiLow is not { } lo || lo <= 0.5m) return (false, $"CI lower bound {OosHitRateCiLow:P2} ≤ 50% — edge not distinguishable from coinflip");
        if (FoldsAboveHalf * 2 <= Folds.Count) return (false, $"only {FoldsAboveHalf}/{Folds.Count} folds beat 50% — not regime-robust");
        if (OverfitGap is { } gap && gap > maxOverfitGap) return (false, $"overfit gap {gap:P2} > {maxOverfitGap:P2} (in-sample {InSampleHitRate:P2} vs OOS {OosHitRate:P2})");
        if (OosHitRate < targetHitRate) return (false, $"OOS hit-rate {OosHitRate:P2} < target {targetHitRate:P2} (honest, but below goal)");
        return (true, $"PASS: OOS {OosHitRate:P2} [CI {OosHitRateCiLow:P2}-{OosHitRateCiHigh:P2}] on {TotalOosBets} bets, gap {OverfitGap:P2}, {FoldsAboveHalf}/{Folds.Count} folds > 50%");
    }
}
