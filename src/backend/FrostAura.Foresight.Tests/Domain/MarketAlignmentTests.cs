using FluentAssertions;
using FrostAura.Foresight.Domain.Markets;
using Xunit;

namespace FrostAura.Foresight.Tests.Domain;

/// <summary>
/// Pure unit tests for <see cref="MarketAlignmentEvaluator"/>. No I/O, no EF, no DI required —
/// the evaluator is a pure static function of its inputs.
/// </summary>
public class MarketAlignmentTests
{
    private const long CandleOpen = 1_700_000_000_000L;  // arbitrary epoch ms
    private const long CandleClose = 1_700_000_300_000L; // + 5 minutes
    private const string Symbol = "BTCUSDT";
    private const string Interval = "5m";
    private const string MarketId = "test-market-001";
    private const string ExpectedRef = "Binance:BTCUSDT";

    [Fact]
    public void Exact_when_window_matches_and_reference_matches()
    {
        var result = MarketAlignmentEvaluator.Evaluate(
            symbol: Symbol, interval: Interval,
            candleOpenMs: CandleOpen, candleCloseMs: CandleClose,
            marketWindowStartMs: CandleOpen, marketWindowEndMs: CandleClose,
            referenceSource: ExpectedRef,
            expectedReferenceSource: ExpectedRef,
            marketExternalId: MarketId);

        result.Verdict.Should().Be(AlignmentVerdict.Exact);
    }

    [Fact]
    public void Mismatch_when_window_offset_by_60_seconds()
    {
        var result = MarketAlignmentEvaluator.Evaluate(
            symbol: Symbol, interval: Interval,
            candleOpenMs: CandleOpen, candleCloseMs: CandleClose,
            marketWindowStartMs: CandleOpen + 60_000L, // offset by 60s
            marketWindowEndMs: CandleClose + 60_000L,
            referenceSource: ExpectedRef,
            expectedReferenceSource: ExpectedRef,
            marketExternalId: MarketId);

        result.Verdict.Should().Be(AlignmentVerdict.Mismatch);
    }

    [Fact]
    public void Tolerated_when_window_matches_but_reference_source_differs()
    {
        var result = MarketAlignmentEvaluator.Evaluate(
            symbol: Symbol, interval: Interval,
            candleOpenMs: CandleOpen, candleCloseMs: CandleClose,
            marketWindowStartMs: CandleOpen, marketWindowEndMs: CandleClose,
            referenceSource: "Coinbase:BTC-USD",  // different known non-empty source
            expectedReferenceSource: ExpectedRef,
            marketExternalId: MarketId);

        result.Verdict.Should().Be(AlignmentVerdict.Tolerated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mismatch_when_reference_source_is_null_or_empty(string? referenceSource)
    {
        var result = MarketAlignmentEvaluator.Evaluate(
            symbol: Symbol, interval: Interval,
            candleOpenMs: CandleOpen, candleCloseMs: CandleClose,
            marketWindowStartMs: CandleOpen, marketWindowEndMs: CandleClose,
            referenceSource: referenceSource,
            expectedReferenceSource: ExpectedRef,
            marketExternalId: MarketId);

        result.Verdict.Should().Be(AlignmentVerdict.Mismatch);
    }

    [Fact]
    public void Result_carries_through_all_metadata()
    {
        var result = MarketAlignmentEvaluator.Evaluate(
            symbol: Symbol, interval: Interval,
            candleOpenMs: CandleOpen, candleCloseMs: CandleClose,
            marketWindowStartMs: CandleOpen, marketWindowEndMs: CandleClose,
            referenceSource: ExpectedRef,
            expectedReferenceSource: ExpectedRef,
            marketExternalId: MarketId);

        result.Symbol.Should().Be(Symbol);
        result.Interval.Should().Be(Interval);
        result.PredictedCandleOpenMs.Should().Be(CandleOpen);
        result.PredictedCandleCloseMs.Should().Be(CandleClose);
        result.MarketExternalId.Should().Be(MarketId);
        result.ReferenceSource.Should().Be(ExpectedRef);
    }

    [Fact]
    public void Tolerated_when_no_expected_reference_set_but_window_matches_and_source_known()
    {
        // No expectation set (expectedReferenceSource null) — windows match and source is non-empty.
        var result = MarketAlignmentEvaluator.Evaluate(
            symbol: Symbol, interval: Interval,
            candleOpenMs: CandleOpen, candleCloseMs: CandleClose,
            marketWindowStartMs: CandleOpen, marketWindowEndMs: CandleClose,
            referenceSource: "SomeExchange:BTCUSD",
            expectedReferenceSource: null,
            marketExternalId: MarketId);

        result.Verdict.Should().Be(AlignmentVerdict.Tolerated);
    }
}
