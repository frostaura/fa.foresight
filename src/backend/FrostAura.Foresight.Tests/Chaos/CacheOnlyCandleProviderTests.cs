using FluentAssertions;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Infrastructure.Chaos;
using Xunit;

namespace FrostAura.Foresight.Tests.Chaos;

/// <summary>
/// Verifies that <see cref="CacheOnlyCandleProvider"/> is a pure in-memory provider that makes
/// zero network calls — the core guarantee of the chaos precompute performance fix.
///
/// Previously, <c>ChaosService.PrecomputeCandidatesAsync</c> called
/// <c>BacktestRunner.ReplayDirectionsAsync</c> using the DI-injected <c>BacktestRunner</c>, which
/// is backed by <c>BinanceHistoricalCandleAdapter</c>. That adapter re-fetches the trailing 48h
/// freshness window on every call AND backfills any uncached off-timeframe range from Binance,
/// producing hundreds of REST pages when the chaos window spans the full cached history (e.g. one
/// year at 5m). This caused every chaos run to "hang" — status stayed "running" indefinitely while
/// Binance klines requests continued back-to-back.
///
/// The fix: <c>PrecomputeCandidatesAsync</c> pre-loads all candles for all intervals from the DB,
/// builds a <see cref="CacheOnlyCandleProvider"/> from the pre-loaded data, then constructs a
/// temporary <see cref="BacktestRunner"/> with that provider and calls
/// <c>ReplayDirectionsAsync</c> on IT — not the scoped Binance-backed runner.
/// </summary>
public sealed class CacheOnlyCandleProviderTests
{
    private const string Symbol = "BTCUSDT";
    private const string TargetInterval = "5m";

    // ──────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────────────

    private static HistoricalCandle MakeCandle(string interval, long openTime) => new()
    {
        Symbol = Symbol,
        Interval = interval,
        OpenTime = openTime,
        Open = 100m,
        High = 101m,
        Low = 99m,
        Close = 100.5m,
        Volume = 1000m,
    };

    private static IReadOnlyList<HistoricalCandle> MakeCandles(string interval, long startMs, int count)
    {
        var intervalMs = BacktestRunner.PublicIntervalMs(interval);
        return Enumerable.Range(0, count)
            .Select(i => MakeCandle(interval, startMs + (long)i * intervalMs))
            .ToList();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // GetRangeAsync — target interval
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRangeAsync_returns_target_interval_candles_in_range()
    {
        var intervalMs = BacktestRunner.PublicIntervalMs(TargetInterval);
        var baseMs = 1_716_000_000_000L; // arbitrary epoch anchor
        var targetCandles = MakeCandles(TargetInterval, baseMs, 100);
        var offTf = new Dictionary<string, IReadOnlyList<HistoricalCandle>>();

        var provider = new CacheOnlyCandleProvider(Symbol, TargetInterval, targetCandles, offTf);

        // Request a sub-range in the middle.
        var startMs = baseMs + 10 * intervalMs;
        var endMs = baseMs + 29 * intervalMs;
        var result = await provider.GetRangeAsync(Symbol, TargetInterval, startMs, endMs);

        result.Should().HaveCount(20, "exactly 20 candles fall in [startMs, endMs]");
        result.All(c => c.OpenTime >= startMs && c.OpenTime <= endMs).Should().BeTrue();
    }

    [Fact]
    public async Task GetRangeAsync_returns_empty_when_range_misses_target_candles()
    {
        var baseMs = 1_716_000_000_000L;
        var targetCandles = MakeCandles(TargetInterval, baseMs, 50);
        var offTf = new Dictionary<string, IReadOnlyList<HistoricalCandle>>();

        var provider = new CacheOnlyCandleProvider(Symbol, TargetInterval, targetCandles, offTf);

        // Request a window entirely BEFORE the cached range.
        var result = await provider.GetRangeAsync(Symbol, TargetInterval, 0L, baseMs - 1L);

        result.Should().BeEmpty("no candles exist before the pre-loaded range");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // GetRangeAsync — off-tf interval
    // ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRangeAsync_returns_off_tf_candles_when_provided()
    {
        var fiveMIntervalMs = BacktestRunner.PublicIntervalMs("5m");
        var oneMIntervalMs = BacktestRunner.PublicIntervalMs("1m");
        var baseMs = 1_716_000_000_000L;

        var targetCandles = MakeCandles("5m", baseMs, 50);
        var oneMCandles = MakeCandles("1m", baseMs, 300); // 5 candles per 5m bar

        var offTf = new Dictionary<string, IReadOnlyList<HistoricalCandle>>
        {
            ["1m"] = oneMCandles,
        };

        var provider = new CacheOnlyCandleProvider(Symbol, "5m", targetCandles, offTf);

        var startMs = baseMs + 10 * oneMIntervalMs;
        var endMs = baseMs + 59 * oneMIntervalMs;
        var result = await provider.GetRangeAsync(Symbol, "1m", startMs, endMs);

        result.Should().HaveCount(50, "50 1m candles fall in the requested range");
        result.All(c => c.OpenTime >= startMs && c.OpenTime <= endMs).Should().BeTrue();
    }

    [Fact]
    public async Task GetRangeAsync_returns_empty_for_unconfigured_off_tf()
    {
        var baseMs = 1_716_000_000_000L;
        var targetCandles = MakeCandles("5m", baseMs, 50);
        // No 15m candles configured.
        var offTf = new Dictionary<string, IReadOnlyList<HistoricalCandle>>();

        var provider = new CacheOnlyCandleProvider(Symbol, "5m", targetCandles, offTf);

        var result = await provider.GetRangeAsync(Symbol, "15m", baseMs, baseMs + 9_999_999L);

        result.Should().BeEmpty("15m data was not pre-loaded into this provider");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // Call count — the performance guarantee
    // ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that calling GetRangeAsync many times (simulating a replay loop) never touches the
    /// network: results are served synchronously from memory (Task.IsCompletedSynchronously = true).
    /// This is the O(1)-network guarantee — no matter how many candles the replay iterates over,
    /// zero Binance calls are made.
    /// </summary>
    [Fact]
    public void GetRangeAsync_completes_synchronously_proving_zero_io()
    {
        var baseMs = 1_716_000_000_000L;
        var intervalMs = BacktestRunner.PublicIntervalMs(TargetInterval);
        var targetCandles = MakeCandles(TargetInterval, baseMs, 5_000); // simulate large historical range
        var offTf = new Dictionary<string, IReadOnlyList<HistoricalCandle>>
        {
            ["1m"] = MakeCandles("1m", baseMs, 25_000),
            ["15m"] = MakeCandles("15m", baseMs, 1_500),
        };

        var provider = new CacheOnlyCandleProvider(Symbol, TargetInterval, targetCandles, offTf);

        // Simulate a replay loop hitting the provider once per candle (worst case: one request per bar).
        for (var i = 0; i < 1_000; i++)
        {
            var startMs = baseMs + (long)i * intervalMs;
            var endMs = startMs + 59 * intervalMs;

            // GetRangeAsync returns a synchronously-completed Task — no await/scheduler hop means no I/O.
            var task = provider.GetRangeAsync(Symbol, TargetInterval, startMs, endMs);
            task.IsCompletedSuccessfully.Should().BeTrue($"call {i} must complete synchronously (no I/O)");
        }
    }
}
