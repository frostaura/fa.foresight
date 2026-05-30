namespace FrostAura.Foresight.Domain.Markets;

/// <summary>
/// Pure static helper for determining whether a prediction-market resolution window aligns with
/// a backtest candle window. Placed in Domain so it can be unit-tested without pulling in any
/// Infrastructure dependencies.
///
/// Rules:
/// • <see cref="AlignmentVerdict.Exact"/> — [marketWindowStart,marketWindowEnd] == [candleOpen,candleClose]
///   AND referenceSource matches expectedReferenceSource (case-insensitive, non-empty on both sides).
/// • <see cref="AlignmentVerdict.Tolerated"/> — windows match exactly but referenceSource differs from
///   expected (both are non-empty known strings).
/// • <see cref="AlignmentVerdict.Mismatch"/> — the window differs by ANY amount, OR referenceSource
///   is null/empty/unknown.
/// </summary>
public static class MarketAlignmentEvaluator
{
    /// <summary>
    /// Evaluate the alignment of a market resolution window against a candle prediction window.
    /// </summary>
    /// <param name="symbol">Ticker symbol (informational, carried into the result).</param>
    /// <param name="interval">Candle interval string, e.g. "5m".</param>
    /// <param name="candleOpenMs">Candle open time in UTC ms.</param>
    /// <param name="candleCloseMs">Candle close time in UTC ms (= open + intervalMs).</param>
    /// <param name="marketWindowStartMs">Market resolution window start in UTC ms.</param>
    /// <param name="marketWindowEndMs">Market resolution window end in UTC ms.</param>
    /// <param name="referenceSource">The reference source reported by the market (may be null).</param>
    /// <param name="expectedReferenceSource">The reference source we expect (may be null to skip the check).</param>
    /// <param name="marketExternalId">Market identifier carried into the result.</param>
    public static MarketAlignment Evaluate(
        string symbol,
        string interval,
        long candleOpenMs,
        long candleCloseMs,
        long marketWindowStartMs,
        long marketWindowEndMs,
        string? referenceSource,
        string? expectedReferenceSource,
        string marketExternalId = "")
    {
        var windowsMatch = marketWindowStartMs == candleOpenMs && marketWindowEndMs == candleCloseMs;

        AlignmentVerdict verdict;
        if (!windowsMatch || string.IsNullOrWhiteSpace(referenceSource))
        {
            verdict = AlignmentVerdict.Mismatch;
        }
        else if (string.IsNullOrWhiteSpace(expectedReferenceSource))
        {
            // No expectation set — windows match and source is non-empty: Tolerated.
            verdict = AlignmentVerdict.Tolerated;
        }
        else if (string.Equals(referenceSource, expectedReferenceSource, StringComparison.OrdinalIgnoreCase))
        {
            verdict = AlignmentVerdict.Exact;
        }
        else
        {
            // Windows match but sources differ — known-non-empty source that is not the expected one.
            verdict = AlignmentVerdict.Tolerated;
        }

        return new MarketAlignment(
            Symbol: symbol,
            Interval: interval,
            PredictedCandleOpenMs: candleOpenMs,
            PredictedCandleCloseMs: candleCloseMs,
            MarketExternalId: marketExternalId,
            MarketWindowStartMs: marketWindowStartMs,
            MarketWindowEndMs: marketWindowEndMs,
            ReferenceSource: referenceSource,
            Verdict: verdict);
    }
}
