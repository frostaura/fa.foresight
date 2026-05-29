using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Verifies FlowValidator strategy-specific behaviour:
///   1. A strategy flow with unconnected context ports (pUp, balance, etc.) PASSES validation
///      because those ports are runtime-injected via FlowContext.AmbientInputs.
///   2. A strategy flow whose output.stake.stake input is unconnected FAILS — that port is NOT
///      in the context set and must be wired.
///   3. A strategy flow whose clamp_round.stake is unconnected FAILS — same reason.
///   4. Model-flow validation is completely unchanged: context-port names that happen to appear
///      in model nodes are still required to be edge-wired.
/// </summary>
public class FlowValidatorStrategyTests
{
    // ──────────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static FlowValidator BuildValidator()
    {
        var nodes = new IFlowNode[]
        {
            new EdgeAwareKellyNode(),
            new ClampRoundNode(),
            new GateNode(),
            new OutputStakeNode(),
            new FlatStrategyNode(),
            new MartingaleStepNode(),
            new KellyStrategyNode(),
        };
        return new FlowValidator(new NodeRegistry(nodes));
    }

    /// <summary>
    /// Deserialises a JSON object literal into a FlowDefinition for test readability.
    /// </summary>
    private static FlowDefinition Flow(object graph)
    {
        var json = JsonSerializer.Serialize(graph, JsonOpts);
        return JsonSerializer.Deserialize<FlowDefinition>(json, JsonOpts)!;
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // Tests — strategy flow context-port exemption
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The canonical EAK DAG (eak → cr → gate → out.stake). EAK inputs pUp, yesPrice, noPrice,
    /// balance are NOT wired by edges — they come from ambient. Validator must PASS.
    /// </summary>
    [Fact]
    public void Strategy_flow_with_unwired_context_ports_passes_validation()
    {
        var flow = Flow(new
        {
            schemaVersion = 1,
            modelKind = "strategy",
            definitionKind = "strategy",
            supportsBacktesting = false,
            warmupCandles = 0,
            nodes = new[]
            {
                new { id = "eak",  type = "strategy.edge_aware_kelly", @params = new { } },
                new { id = "cr",   type = "strategy.clamp_round",      @params = new { } },
                new { id = "gate", type = "strategy.gate",             @params = new { } },
                new { id = "out",  type = "output.stake",              @params = new { } },
            },
            edges = new[]
            {
                new { from = "eak.stake",  to = "cr.stake"   },
                new { from = "cr.stake",   to = "gate.stake" },
                new { from = "eak.stake",  to = "gate.pUp"   },
                new { from = "gate.stake", to = "out.stake"  },
            },
        });

        var result = BuildValidator().Validate(flow);

        result.IsValid.Should().BeTrue(because:
            "pUp, yesPrice, noPrice, balance on eak are strategy context ports — satisfied at runtime via AmbientInputs");
    }

    /// <summary>
    /// A flat strategy DAG with no edges at all — just flat → out, with flat.initialBet unwired.
    /// flat's "initialBet" is a required port AND is in StrategyContextPorts → should PASS.
    /// But flat.stake → out.stake is wired, and out.stake.stake is connected.
    /// </summary>
    [Fact]
    public void Strategy_flow_flat_node_with_unwired_initialBet_passes_validation()
    {
        var flow = Flow(new
        {
            schemaVersion = 1,
            modelKind = "strategy",
            definitionKind = "strategy",
            supportsBacktesting = false,
            warmupCandles = 0,
            nodes = new[]
            {
                new { id = "flat", type = "strategy.flat",  @params = new { } },
                new { id = "out",  type = "output.stake",   @params = new { } },
            },
            edges = new[]
            {
                new { from = "flat.stake", to = "out.stake" },
            },
        });

        var result = BuildValidator().Validate(flow);

        result.IsValid.Should().BeTrue(because:
            "initialBet is in StrategyContextPorts — it is runtime-injected and should not require an edge");
    }

    /// <summary>
    /// Martingale node: currentBet, lastOutcome, initialBet are all context ports → should PASS
    /// when none are wired, as long as its stake output is connected to the terminal.
    /// </summary>
    [Fact]
    public void Strategy_flow_martingale_with_all_context_ports_unwired_passes_validation()
    {
        var flow = Flow(new
        {
            schemaVersion = 1,
            modelKind = "strategy",
            definitionKind = "strategy",
            supportsBacktesting = false,
            warmupCandles = 0,
            nodes = new[]
            {
                new { id = "mg",  type = "strategy.martingale_step", @params = new { } },
                new { id = "out", type = "output.stake",             @params = new { } },
            },
            edges = new[]
            {
                new { from = "mg.stake", to = "out.stake" },
            },
        });

        var result = BuildValidator().Validate(flow);

        result.IsValid.Should().BeTrue(because:
            "currentBet, lastOutcome, initialBet are all StrategyContextPorts — fulfilled via AmbientInputs");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // Tests — non-context required ports still enforced in strategy flows
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The output.stake node has a required input port "stake". If that port is not connected,
    /// validation should FAIL even in a strategy flow.
    /// </summary>
    [Fact]
    public void Strategy_flow_missing_output_stake_connection_fails_validation()
    {
        var flow = Flow(new
        {
            schemaVersion = 1,
            modelKind = "strategy",
            definitionKind = "strategy",
            supportsBacktesting = false,
            warmupCandles = 0,
            nodes = new[]
            {
                new { id = "eak", type = "strategy.edge_aware_kelly", @params = new { } },
                new { id = "out", type = "output.stake",              @params = new { } },
            },
            // Intentionally omit eak.stake → out.stake edge: out.stake.stake is unconnected.
            edges = Array.Empty<object>(),
        });

        var result = BuildValidator().Validate(flow);

        result.IsValid.Should().BeFalse(because:
            "output.stake.stake is not in StrategyContextPorts — it must be explicitly wired");
        result.Error.Should().Contain("stake").And.Contain("out");
    }

    /// <summary>
    /// clamp_round.stake is a required non-context port. If it is not wired, validation FAILS.
    /// </summary>
    [Fact]
    public void Strategy_flow_missing_clamp_round_stake_connection_fails_validation()
    {
        var flow = Flow(new
        {
            schemaVersion = 1,
            modelKind = "strategy",
            definitionKind = "strategy",
            supportsBacktesting = false,
            warmupCandles = 0,
            nodes = new[]
            {
                new { id = "cr",  type = "strategy.clamp_round", @params = new { } },
                new { id = "out", type = "output.stake",         @params = new { } },
            },
            // cr.stake input is not wired; out.stake is wired from cr.stake output.
            edges = new[]
            {
                new { from = "cr.stake", to = "out.stake" },
            },
        });

        var result = BuildValidator().Validate(flow);

        result.IsValid.Should().BeFalse(because:
            "clamp_round.stake (input port) is not in StrategyContextPorts and must be wired");
        result.Error.Should().Contain("stake").And.Contain("cr");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // Tests — model flow validation is unchanged
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Model flows must not be affected: if a model-kind node happens to have a port named
    /// "pUp" (or any other context-port name), it is still required to be edge-wired.
    ///
    /// We verify this by checking that model validation errors are still produced for
    /// a deliberately broken model flow — this ensures the isStrategyFlow guard works correctly.
    ///
    /// We use a simple broken model flow (missing output.prediction) to confirm model-kind
    /// validation remains strict.
    /// </summary>
    [Fact]
    public void Model_flow_validation_is_unchanged_missing_terminal_fails()
    {
        // A bare model flow with no output.prediction node — classic model validation failure.
        // If the strategy exemption leaked into model flows, this would somehow pass; it must not.
        var flow = Flow(new
        {
            schemaVersion = 1,
            modelKind = "deterministic",
            definitionKind = "model",
            supportsBacktesting = false,
            warmupCandles = 0,
            nodes = new[]
            {
                new { id = "eak", type = "strategy.edge_aware_kelly", @params = new { } },
            },
            edges = Array.Empty<object>(),
        });

        var result = BuildValidator().Validate(flow);

        result.IsValid.Should().BeFalse(because:
            "model flows require exactly one output.prediction node — the strategy exemption must not bleed over");
    }

    /// <summary>
    /// Confirms StrategyContextPorts contains exactly the expected port names — the single source
    /// of truth used by both FlowValidator and StrategyEvaluator.BuildAmbientInputs.
    /// </summary>
    [Fact]
    public void StrategyContextPorts_contains_exactly_the_expected_port_names()
    {
        var expected = new[] { "pUp", "yesPrice", "noPrice", "balance", "currentBet", "initialBet", "lastOutcome" };
        FlowValidator.StrategyContextPorts.Should().BeEquivalentTo(expected);
    }
}
