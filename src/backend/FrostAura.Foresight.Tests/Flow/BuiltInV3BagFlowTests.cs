using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Structural coverage for the two v3-bag built-in definitions (the 2026-06-10 campaign
/// productionisation): both flows deserialize, pass FlowValidator against the real node registry,
/// wire every matrix column from a matching pack output edge, and carry the seed-bagged estimator
/// contract params (bags / seed / coverage) the trainer consumes. Pure-JSON checks — training /
/// gating behaviour is covered by the trainer-side tests.
/// </summary>
public sealed class BuiltInV3BagFlowTests
{
    [Theory]
    [InlineData("15m")]
    [InlineData("5m")]
    public void V3Bag_flow_passes_structural_validation(string variant)
    {
        var (flow, validator) = Load(variant);
        var result = validator.Validate(flow);
        result.IsValid.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData("15m")]
    [InlineData("5m")]
    public void V3Bag_matrix_columns_exactly_match_wired_edges(string variant)
    {
        var (flow, _) = Load(variant);
        var matrix = flow.Nodes.Single(n => n.Type == "feature.matrix_builder");
        var columns = matrix.Params.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        var wiredPorts = flow.Edges
            .Select(e => e.To.SplitEndpoint())
            .Where(t => t.NodeId == matrix.Id)
            .Select(t => t.Port)
            .ToList();

        columns.Should().OnlyHaveUniqueItems();
        wiredPorts.Should().BeEquivalentTo(columns, "every matrix column must be fed by exactly one edge or the matrix never readies");
    }

    [Theory]
    [InlineData("15m")]
    [InlineData("5m")]
    public void V3Bag_estimator_carries_the_bagged_contract_params(string variant)
    {
        var (flow, _) = Load(variant);
        var model = flow.Nodes.Single(n => n.Type == "model.gbt");
        model.Params.GetProperty("bags").GetInt32().Should().Be(5);
        model.Params.GetProperty("seed").GetInt32().Should().Be(101);
        model.Params.GetProperty("coverage").GetDouble().Should().Be(0.05);
        model.Params.GetProperty("n_estimators").GetInt32().Should().Be(350);
        model.Params.GetProperty("max_depth").GetInt32().Should().Be(6);
        model.Params.GetProperty("min_samples_leaf").GetInt32().Should().Be(150);
    }

    [Fact]
    public void V3Bag_15m_uses_1h_regime_and_5m_subbar_sources()
    {
        var (flow, _) = Load("15m");
        var sources = flow.Nodes.Where(n => n.Type == "source.binance.klines")
            .ToDictionary(n => n.Id, n => n.Params.GetProperty("tf").GetString());
        sources.Values.Should().BeEquivalentTo(new[] { "target", "1h", "5m" });

        // htf regime reads the 1h source; sub-bar pressure reads the 5m source.
        var htfFeed = flow.Edges.Single(e => e.To == "htf.candles").From.SplitEndpoint().NodeId;
        var subFeed = flow.Edges.Single(e => e.To == "sub.candles").From.SplitEndpoint().NodeId;
        sources[htfFeed].Should().Be("1h");
        sources[subFeed].Should().Be("5m");
    }

    [Theory]
    [InlineData("15m")]
    [InlineData("5m")]
    public void V3Bag_flow_level_seed_attributes_are_deterministic_and_backtestable(string variant)
    {
        // The DatabaseInitializer seeds these definitions verbatim — the flow-level attributes are
        // what the trainer/backtester/chaos read, so pin them here.
        var (flow, _) = Load(variant);
        flow.ModelKind.Should().Be("deterministic");
        flow.SupportsBacktesting.Should().BeTrue();
        flow.WarmupCandles.Should().Be(60);
    }

    private static (FlowDefinition Flow, FlowValidator Validator) Load(string variant)
    {
        var json = variant == "15m"
            ? BuiltInModels.BuildForesight15mV3BagFlow()
            : BuiltInModels.BuildForesight5mV3BagFlow();
        var flow = JsonSerializer.Deserialize<FlowDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var registry = new NodeRegistry(new IFlowNode[]
        {
            new BinanceKlinesNode(),
            new TechPackNode(), new FeaturePackNode(),
            new MomentumPackNode(), new NormPackNode(), new VolumePackNode(),
            new TemporalPackNode(), new HtfRegimePackNode(), new SubBarPackNode(),
            new MatrixBuilderNode(), new GradientBoostedTreesNode(), new OutputPredictionNode(),
        });
        return (flow, new FlowValidator(registry));
    }
}
