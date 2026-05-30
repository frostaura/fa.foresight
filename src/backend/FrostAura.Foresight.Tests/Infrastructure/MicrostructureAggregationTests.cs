using FluentAssertions;
using FrostAura.Foresight.Infrastructure.Adapters;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Pins the Binance aggTrades → MicrostructureBar aggregation — the riskiest part of the (otherwise
/// network-bound) ingest. Covers the taker convention, 5m bucketing, the large-trade threshold,
/// header-row tolerance, malformed-line skipping, and the microsecond-timestamp guard.
/// </summary>
public sealed class MicrostructureAggregationTests
{
    private const long FiveM = 300_000L;
    private const long T0 = 1_700_000_100_000L; // exact 5m boundary
    private const decimal LargeQty = 1.0m;

    [Fact]
    public void AggregateTrades_buckets_and_signs_trades_correctly()
    {
        var lines = new[]
        {
            "a,p,q,f,l,T,m,M",                                   // header row → skipped
            "1,50000,0.5,1,1,1700000101000,false,true",          // bucket0 BUY 0.5
            "2,50000,2.0,2,2,1700000102000,false,true",          // bucket0 BUY 2.0 (large)
            "3,50000,1.5,3,3,1700000103000,true,true",           // bucket0 SELL 1.5 (large)
            "4,50000,0.3,4,4,1700000104000,true,true",           // bucket0 SELL 0.3
            "garbage",                                            // malformed → skipped
            "5,50000,notanumber,5,5,1700000105000,false,true",   // bad qty → skipped
            "6,50000,1.0,6,6,1700000401000,false,true",          // bucket1 BUY 1.0 (large)
            "7,50000,0.2,7,7,1700000402000000,true,true",        // bucket1 SELL 0.2, MICROSECOND ts
        };

        var bars = BinanceHistoricalMicrostructureAdapter.AggregateTrades(lines, "BTCUSDT", "5m", FiveM, LargeQty);

        bars.Should().HaveCount(2);
        bars.Should().BeInAscendingOrder(b => b.OpenTime);

        var b0 = bars[0];
        b0.OpenTime.Should().Be(T0);
        b0.TradeCount.Should().Be(4);
        b0.BuyVolume.Should().Be(2.5m);            // 0.5 + 2.0
        b0.SellVolume.Should().Be(1.8m);           // 1.5 + 0.3
        b0.BuyTradeCount.Should().Be(2);
        b0.LargeBuyVolume.Should().Be(2.0m);       // only the 2.0 buy ≥ 1.0
        b0.LargeSellVolume.Should().Be(1.5m);      // only the 1.5 sell ≥ 1.0

        var b1 = bars[1];
        b1.OpenTime.Should().Be(T0 + FiveM);       // microsecond ts landed in the right bucket
        b1.TradeCount.Should().Be(2);
        b1.BuyVolume.Should().Be(1.0m);
        b1.SellVolume.Should().Be(0.2m);
        b1.BuyTradeCount.Should().Be(1);
        b1.LargeBuyVolume.Should().Be(1.0m);
        b1.LargeSellVolume.Should().Be(0m);        // 0.2 < threshold
    }

    [Fact]
    public void AggregateTrades_empty_input_yields_no_bars()
    {
        BinanceHistoricalMicrostructureAdapter.AggregateTrades(Array.Empty<string>(), "BTCUSDT", "5m", FiveM, LargeQty)
            .Should().BeEmpty();
    }

    [Fact]
    public void AggregateTrades_splits_intrabar_early_and_late_windows()
    {
        // 5m bar opening at T0. Early window = first 20% [T0, T0+60s); late = final 20% [T0+240s, T0+300s).
        // Trades placed deliberately in early / mid / late so the window splits are unambiguous.
        var lines = new[]
        {
            "a,p,q,f,l,T,m,M",
            "1,50000,0.5,1,1,1700000101000,false,true",   // +1s   → EARLY buy 0.5
            "2,50000,0.3,2,2,1700000102000,true,true",    // +2s   → EARLY sell 0.3
            "3,50000,1.0,3,3,1700000250000,false,true",   // +150s → mid (neither early nor late)
            "4,50000,2.0,4,4,1700000350000,false,true",   // +250s → LATE buy 2.0
            "5,50000,0.4,5,5,1700000390000,true,true",     // +290s → LATE sell 0.4
        };

        var bar = BinanceHistoricalMicrostructureAdapter.AggregateTrades(lines, "BTCUSDT", "5m", FiveM, LargeQty)
            .Should().ContainSingle().Subject;

        // Whole-bar totals unchanged by the split.
        bar.BuyVolume.Should().Be(3.5m);   // 0.5 + 1.0 + 2.0
        bar.SellVolume.Should().Be(0.7m);  // 0.3 + 0.4
        bar.TradeCount.Should().Be(5);

        // Intra-bar windows: only the trades inside each sub-window, never the mid trade.
        bar.EarlyBuyVolume.Should().Be(0.5m);
        bar.EarlySellVolume.Should().Be(0.3m);
        bar.LateBuyVolume.Should().Be(2.0m);
        bar.LateSellVolume.Should().Be(0.4m);
        bar.LateTradeCount.Should().Be(2);
    }
}
