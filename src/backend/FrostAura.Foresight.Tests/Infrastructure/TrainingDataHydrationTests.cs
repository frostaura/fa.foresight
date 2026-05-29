using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Covers the pre-training data hydration step that <see cref="ModelTrainingService"/> runs in its
/// background task BEFORE fitting. The contract being pinned:
///   • A flow that reads order-flow (<c>source.microstructure.orderflow</c>) has its microstructure
///     window hydrated — and if the on-demand adapter throws its actionable cap/availability error,
///     that error PROPAGATES and becomes the training failure (which the background runner stores in
///     TrainingError), instead of being swallowed and later surfacing as the cryptic
///     "Not enough training rows (0) after warmup".
///   • A candle-only flow never requires the microstructure provider.
/// </summary>
public sealed class TrainingDataHydrationTests
{
    private const string Symbol = "BTCUSDT";

    [Fact]
    public void Micro_dependent_flow_is_detected()
    {
        var ofx = Flow(BuiltInModels.BuildForesight5mV1OfxFlow());
        var candleOnly = Flow(BuiltInModels.BuildForesight5mV1Flow());

        ModelTrainingService.FlowUsesMicrostructure(ofx).Should().BeTrue("the v1+ofx flow carries a source.microstructure.orderflow node");
        ModelTrainingService.FlowUsesMicrostructure(candleOnly).Should().BeFalse("the v1 flow is candle-only");
    }

    [Fact]
    public async Task Hydration_invokes_microstructure_provider_for_micro_dependent_flow()
    {
        var flow = Flow(BuiltInModels.BuildForesight5mV1OfxFlow());
        var candles = new RecordingCandleProvider();
        var micro = new RecordingMicroProvider();

        await ModelTrainingService.HydrateTrainingDataAsync(
            flow, candles, micro, Symbol, "5m", 1_000_000L, 2_000_000L, default, NullLogger.Instance);

        candles.Calls.Should().NotBeEmpty("target + off-tf candles must be pre-fetched");
        micro.Calls.Should().HaveCount(1, "the order-flow window must be hydrated exactly once for the target interval");
        micro.Calls[0].Interval.Should().Be("5m");
    }

    [Fact]
    public async Task Hydration_does_not_require_microstructure_for_candle_only_flow()
    {
        var flow = Flow(BuiltInModels.BuildForesight5mV1Flow());
        var candles = new RecordingCandleProvider();
        var micro = new RecordingMicroProvider();

        await ModelTrainingService.HydrateTrainingDataAsync(
            flow, candles, micro, Symbol, "5m", 1_000_000L, 2_000_000L, default, NullLogger.Instance);

        candles.Calls.Should().NotBeEmpty("candle-only flows still hydrate candles");
        micro.Calls.Should().BeEmpty("a candle-only flow must never touch the microstructure provider");
    }

    [Fact]
    public async Task Hydration_propagates_microstructure_cap_error_for_micro_dependent_flow()
    {
        var flow = Flow(BuiltInModels.BuildForesight5mV1OfxFlow());
        var candles = new RecordingCandleProvider();
        var capMessage =
            "Order-flow data needs 90 uncached days of Binance trade dumps for this window — over the 95-day on-demand cap. " +
            "Order-flow models (v1+ofx / v1+ofx2) download a large trade dump per day, so use a shorter lookback (≤ 95 days) for them, or pre-ingest the range first.";
        var micro = new ThrowingMicroProvider(capMessage);

        Func<Task> act = () => ModelTrainingService.HydrateTrainingDataAsync(
            flow, candles, micro, Symbol, "5m", 1_000_000L, 2_000_000L, default, NullLogger.Instance);

        // The actionable cap error surfaces — NOT a "Not enough training rows" message.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(capMessage)
            .And.NotContain("Not enough training rows");
    }

    [Fact]
    public async Task End_to_end_micro_cap_error_is_the_failure_not_zero_rows()
    {
        // Simulate the real background path: hydrate (which throws the cap error) → if it threw we never
        // reach the trainer, so the user sees the actionable message rather than the trainer's
        // "Not enough training rows (0) after warmup". Proves the pre-hydration short-circuits the
        // swallow-and-zero-rows failure mode.
        var flow = Flow(BuiltInModels.BuildForesight5mV1OfxFlow());
        var (executor, _) = BuildExecutor();
        var candles = SyntheticCandles(seed: 21);
        var capMessage = "Order-flow data needs 90 uncached days … over the 95-day on-demand cap. Use a shorter lookback.";
        var micro = new ThrowingMicroProvider(capMessage);

        var start = candles.Min("5m") + 100L * 300_000L;
        var end = candles.Max("5m") - 5L * 300_000L;

        Exception? captured = null;
        try
        {
            await ModelTrainingService.HydrateTrainingDataAsync(
                flow, candles, micro, Symbol, "5m", start, end, default, NullLogger.Instance);
            // (Unreached) — only here would the trainer run.
            var trainer = new ModelTrainer(executor, candles, micro);
            await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);
        }
        catch (Exception ex) { captured = ex; }

        captured.Should().BeOfType<InvalidOperationException>();
        captured!.Message.Should().Be(capMessage);
        captured.Message.Should().NotContain("Not enough training rows");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static FlowDefinition Flow(string json) =>
        JsonSerializer.Deserialize<FlowDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static (IFlowExecutor Executor, FlowValidator Validator) BuildExecutor()
    {
        var nodes = new IFlowNode[]
        {
            new BinanceKlinesNode(),
            new MomentumPackNode(), new NormPackNode(), new VolumePackNode(),
            new TemporalPackNode(), new HtfRegimePackNode(), new SubBarPackNode(),
            new MatrixBuilderNode(), new LogisticRegressionNode(), new GradientBoostedTreesNode(), new OutputPredictionNode(),
            new MicrostructureSourceNode(), new OrderFlowPackNode(), new DerivativesPackNode(),
        };
        var registry = new NodeRegistry(nodes);
        var validator = new FlowValidator(registry);
        var executor = new FlowExecutor(registry, validator, NullLogger<FlowExecutor>.Instance);
        return (executor, validator);
    }

    private static SyntheticCandleProvider SyntheticCandles(int seed)
    {
        var baseMs = (1_700_000_000_000L / 900_000L) * 900_000L;
        var rng = new Random(seed);
        var data = new Dictionary<string, List<HistoricalCandle>>
        {
            ["1m"]  = Walk("1m", 1_000 * 5 + 400, baseMs, rng),
            ["5m"]  = Walk("5m", 1_000, baseMs, rng),
            ["15m"] = Walk("15m", 1_000 / 3 + 100, baseMs, rng),
        };
        return new SyntheticCandleProvider(data);
    }

    private static List<HistoricalCandle> Walk(string tf, int count, long baseMs, Random rng)
    {
        var ms = tf switch { "1m" => 60_000L, "5m" => 300_000L, "15m" => 900_000L, _ => throw new ArgumentException(tf) };
        var candles = new List<HistoricalCandle>(count);
        var close = 50_000m;
        for (var k = 0; k < count; k++)
        {
            var open = close;
            var openTime = baseMs + (long)k * ms;
            var up = rng.NextDouble() < 0.5;
            var mag = 0.0004m + (decimal)(rng.NextDouble() * 0.0006);
            close = up ? open * (1 + mag) : open * (1 - mag);
            candles.Add(new HistoricalCandle
            {
                Symbol = Symbol, Interval = tf, OpenTime = openTime,
                Open = open, High = Math.Max(open, close) * 1.0002m, Low = Math.Min(open, close) * 0.9998m,
                Close = close, Volume = 5m + (decimal)(rng.NextDouble() * 10),
            });
        }
        return candles;
    }

    /// <summary>Candle provider that records every GetRangeAsync call.</summary>
    private sealed class RecordingCandleProvider : IHistoricalCandleProvider
    {
        public List<(string Interval, long Start, long End)> Calls { get; } = new();
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        {
            Calls.Add((interval, startMs, endMs));
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
        }
    }

    /// <summary>Microstructure provider that records every GetRangeAsync call.</summary>
    private sealed class RecordingMicroProvider : IHistoricalMicrostructureProvider
    {
        public List<(string Interval, long Start, long End)> Calls { get; } = new();
        public Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        {
            Calls.Add((interval, startMs, endMs));
            return Task.FromResult<IReadOnlyList<MicrostructureBar>>(Array.Empty<MicrostructureBar>());
        }
    }

    /// <summary>Microstructure provider that throws the actionable cap/availability error.</summary>
    private sealed class ThrowingMicroProvider : IHistoricalMicrostructureProvider
    {
        private readonly string _message;
        public ThrowingMicroProvider(string message) => _message = message;
        public Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
            => throw new InvalidOperationException(_message);
    }

    /// <summary>In-memory candle provider with min/max helpers for the end-to-end test.</summary>
    private sealed class SyntheticCandleProvider : IHistoricalCandleProvider
    {
        private readonly IReadOnlyDictionary<string, List<HistoricalCandle>> _byTf;
        public SyntheticCandleProvider(IReadOnlyDictionary<string, List<HistoricalCandle>> byTf) => _byTf = byTf;
        public long Min(string tf) => _byTf[tf][0].OpenTime;
        public long Max(string tf) => _byTf[tf][^1].OpenTime;
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        {
            if (!_byTf.TryGetValue(interval, out var series))
                return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(
                series.Where(c => c.Symbol == symbol && c.OpenTime >= startMs && c.OpenTime <= endMs).ToList());
        }
    }
}
