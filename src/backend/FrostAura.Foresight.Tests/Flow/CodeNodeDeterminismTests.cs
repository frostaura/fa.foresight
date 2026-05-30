using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Purity contract for the executable code node — a node is a pure function of its declared inputs;
/// the same definition produces byte-identical output in step-through and batch. Uses a fake
/// <see cref="ISandboxExecutor"/> so the .NET marshalling + determinism contract is proven without a
/// running container (the real-container determinism test runs in CI against the sidecar).
/// </summary>
public class CodeNodeDeterminismTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    private static FlowContext Ctx(FlowMode mode) => new(
        TenantId: Guid.Empty, ModelId: Guid.Empty, Symbol: "BTCUSDT", Interval: "5m",
        TargetOpenTime: 0, Horizon: 1, Mode: mode, HistoricalCandles: new StubCandles(), TrainedState: null);

    private static List<HistoricalCandle> Candles(params double[] closes)
        => closes.Select((c, i) => new HistoricalCandle
        {
            Symbol = "BTCUSDT",
            Interval = "5m",
            OpenTime = i * 300_000L,
            Open = (decimal)c,
            High = (decimal)c,
            Low = (decimal)c,
            Close = (decimal)c,
            Volume = 1m,
        }).ToList();

    private const string PortsJson =
        """{ "code": "run", "ports": { "inputs": { "candles": "candles" }, "outputs": { "signal": "scalar" } }, "seed": 7 }""";

    [Fact]
    public async Task Same_inputs_produce_identical_output_across_two_runs()
    {
        var fake = new FakeSandbox();
        var node = new CodePythonNode(fake);
        var inputs = new Dictionary<string, object?> { ["candles"] = Candles(10, 20, 30) };

        var a = await node.ExecuteAsync(inputs, Params(PortsJson), Ctx(FlowMode.Backtest), default);
        var b = await node.ExecuteAsync(inputs, Params(PortsJson), Ctx(FlowMode.Backtest), default);

        JsonSerializer.Serialize(a).Should().Be(JsonSerializer.Serialize(b));
        ((double)a["signal"]!).Should().BeApproximately(20.0, 1e-9); // mean(10,20,30)
        fake.LastResult!.OutputHash.Should().Be(fake.OutputHashFor(new[] { 10.0, 20.0, 30.0 }));
    }

    [Fact]
    public async Task Candles_input_marshals_to_columnar_candles_tag()
    {
        var fake = new FakeSandbox();
        var node = new CodePythonNode(fake);
        var inputs = new Dictionary<string, object?> { ["candles"] = Candles(1, 2, 3, 4) };

        await node.ExecuteAsync(inputs, Params(PortsJson), Ctx(FlowMode.Backtest), default);

        var pv = fake.LastRequest!.Inputs["candles"];
        pv.Tag.Should().Be("candles");
        var cv = (SandboxCandlesValue)pv.Value!;
        cv.Close.Should().Equal(1.0, 2.0, 3.0, 4.0);
        cv.OpenTime.Should().HaveCount(4);
    }

    [Fact]
    public async Task Step_n1_equals_batch_1_element()
    {
        var fake = new FakeSandbox();
        var node = new CodePythonNode(fake);
        var inputs = new Dictionary<string, object?> { ["candles"] = Candles(42) };

        var step = await node.ExecuteAsync(inputs, Params(PortsJson), Ctx(FlowMode.Live), default);     // mode "step"
        var batch = await node.ExecuteAsync(inputs, Params(PortsJson), Ctx(FlowMode.Backtest), default); // mode "batch"

        JsonSerializer.Serialize(step).Should().Be(JsonSerializer.Serialize(batch));
    }

    // --- Fakes -----------------------------------------------------------------------------------

    private sealed class FakeSandbox : ISandboxExecutor
    {
        public SandboxRequest? LastRequest { get; private set; }
        public SandboxResult? LastResult { get; private set; }

        public string OutputHashFor(double[] closes)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join(",", closes.Select(c => c.ToString("R"))))));

        public Task<SandboxResult> ExecuteAsync(SandboxRequest req, CancellationToken ct)
        {
            LastRequest = req;
            // Deterministic compute: mean of the candles' close series. Pure function of inputs.
            var cv = (SandboxCandlesValue)req.Inputs["candles"].Value!;
            var mean = cv.Close.Length == 0 ? 0.0 : cv.Close.Average();
            var result = new SandboxResult
            {
                Ok = true,
                Outputs = new Dictionary<string, SandboxPortValue> { ["signal"] = new("scalar", mean) },
                OutputHash = OutputHashFor(cv.Close),
                DurationMs = 1,
            };
            LastResult = result;
            return Task.FromResult(result);
        }
    }

    private sealed class StubCandles : IHistoricalCandleProvider
    {
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval,
            long startMs, long endMs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
    }
}
