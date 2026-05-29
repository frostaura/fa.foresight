using System.Text.Json;
using FrostAura.Foresight.Domain.Trading;
using static FrostAura.Foresight.Application.Flow.Nodes.StrategyNodeCoerce;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Terminal node for strategy DAGs. Mirrors <see cref="OutputPredictionNode"/> but for staking:
/// accepts the computed stake size (and optional side override) and surfaces it as the headline
/// result. Exactly one <c>output.stake</c> is required by the validator when
/// <c>FlowDefinition.DefinitionKind == "strategy"</c>.
/// </summary>
public sealed class OutputStakeNode : IFlowNode
{
    public string TypeId => "output.stake";

    public NodePortSpec Spec { get; } = new(
        Category: "output",
        Inputs: new[]
        {
            new PortDef("stake", "decimal"),
            new PortDef("side",  "string", Required: false,
                Description: "\"UP\" or \"DOWN\". If omitted, side is determined by StakingEngine.DecideSide."),
        },
        Outputs: Array.Empty<PortDef>(),
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = inputs.GetValueOrDefault("stake"),
            ["side"]  = inputs.GetValueOrDefault("side"),
        });
    }
}

// ---------------------------------------------------------------------------
// Strategy primitive nodes — pure wrappers over Domain.Trading math.
// Each node is a pure IFlowNode: its output is fully determined by its inputs + params.
// ---------------------------------------------------------------------------

/// <summary>
/// Flat staking: always emit <c>initialBet</c> regardless of prior outcome.
/// Inputs: initialBet (decimal), balance (decimal optional).
/// Output: stake (decimal).
/// </summary>
public sealed class FlatStrategyNode : IFlowNode
{
    public string TypeId => "strategy.flat";

    public NodePortSpec Spec { get; } = new(
        Category: "strategy",
        Inputs: new[]
        {
            new PortDef("initialBet", "decimal"),
        },
        Outputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var bet = ToDecimal(inputs.GetValueOrDefault("initialBet")) ?? 0m;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = bet,
        });
    }
}

/// <summary>
/// Martingale step: double on loss, reset to initialBet on win.
/// Inputs: currentBet, lastOutcome (bool — true=won), initialBet.
/// Output: stake (decimal).
/// </summary>
public sealed class MartingaleStepNode : IFlowNode
{
    public string TypeId => "strategy.martingale_step";

    public NodePortSpec Spec { get; } = new(
        Category: "strategy",
        Inputs: new[]
        {
            new PortDef("currentBet",   "decimal"),
            new PortDef("lastOutcome",  "bool"),
            new PortDef("initialBet",   "decimal"),
        },
        Outputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var current = ToDecimal(inputs.GetValueOrDefault("currentBet")) ?? 0m;
        var won     = ToBool(inputs.GetValueOrDefault("lastOutcome")) ?? true;
        var initial = ToDecimal(inputs.GetValueOrDefault("initialBet")) ?? 0m;
        var step = new StrategyStep(current, won, initial, 0m, default);
        var stake = new MartingaleStakingStrategy().NextBetSize(step);
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = stake,
        });
    }
}

/// <summary>
/// Fractional Kelly: 2.5% of the current bankroll, rounded to 2 dp.
/// Input: balance (decimal).
/// Output: stake (decimal).
/// </summary>
public sealed class KellyStrategyNode : IFlowNode
{
    public string TypeId => "strategy.kelly";

    public NodePortSpec Spec { get; } = new(
        Category: "strategy",
        Inputs: new[]
        {
            new PortDef("balance",    "decimal"),
            new PortDef("initialBet", "decimal", Required: false),
        },
        Outputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var balance = ToDecimal(inputs.GetValueOrDefault("balance")) ?? 0m;
        var initial = ToDecimal(inputs.GetValueOrDefault("initialBet")) ?? 1m;
        var step = new StrategyStep(initial, true, initial, balance, default);
        var stake = new FractionalKellyStakingStrategy().NextBetSize(step);
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = stake,
        });
    }
}

/// <summary>
/// Edge-aware Kelly: sizes against the calibrated edge f* = (winProb − price) / (1 − price)
/// at quarter-Kelly with whole-dollar rounding. Emits 0 when there is no edge.
/// Inputs: pUp (decimal), yesPrice (decimal), noPrice (decimal), balance (decimal).
/// Output: stake (decimal).
/// Params: fraction (decimal, default 0.25).
/// </summary>
public sealed class EdgeAwareKellyNode : IFlowNode
{
    public string TypeId => "strategy.edge_aware_kelly";

    public NodePortSpec Spec { get; } = new(
        Category: "strategy",
        Inputs: new[]
        {
            new PortDef("pUp",       "decimal"),
            new PortDef("yesPrice",  "decimal"),
            new PortDef("noPrice",   "decimal"),
            new PortDef("balance",   "decimal"),
            new PortDef("initialBet","decimal", Required: false),
        },
        Outputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["fraction"] = new("decimal", false, 0.25m,
                "Multiplier on full Kelly (quarter-Kelly default)."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var pUp      = ToDecimal(inputs.GetValueOrDefault("pUp"))      ?? 0.5m;
        var yesPrice = ToDecimal(inputs.GetValueOrDefault("yesPrice"))  ?? 0.5m;
        var noPrice  = ToDecimal(inputs.GetValueOrDefault("noPrice"))   ?? 0.5m;
        var balance  = ToDecimal(inputs.GetValueOrDefault("balance"))   ?? 0m;
        var initial  = ToDecimal(inputs.GetValueOrDefault("initialBet")) ?? 1m;
        var fraction = NodeParams.GetDecimal(nodeParams, "fraction", EdgeAwareKellyStakingStrategy.Fraction);

        var up       = pUp >= 0.5m;
        var price    = up ? yesPrice : noPrice;
        var winProb  = up ? pUp : 1m - pUp;
        var fStar    = KellyMath.FullKelly(winProb, price);
        if (fStar <= 0m)
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["stake"] = 0m });
        }
        var target = Math.Round(fraction * fStar * balance, 2, MidpointRounding.AwayFromZero);
        var stake  = target < 1m ? 0m : target;

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = stake,
        });
    }
}

/// <summary>
/// Clamp and round to whole dollars. Emits 0 below a configurable minimum (default $1 sub-dollar skip).
/// Input: stake (decimal).
/// Output: stake (decimal).
/// Params: minBet (decimal, default 1.0).
/// </summary>
public sealed class ClampRoundNode : IFlowNode
{
    public string TypeId => "strategy.clamp_round";

    public NodePortSpec Spec { get; } = new(
        Category: "strategy",
        Inputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Outputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["minBet"] = new("decimal", false, 1.0m, "Minimum bet size; positions below this emit 0 (skip)."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var raw    = ToDecimal(inputs.GetValueOrDefault("stake")) ?? 0m;
        var minBet = NodeParams.GetDecimal(nodeParams, "minBet", 1m);
        var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        var stake = rounded < minBet ? 0m : rounded;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = stake,
        });
    }
}

/// <summary>
/// Confidence gate: emits the upstream stake unchanged when <c>|pUp − 0.5| · 2 ≥ band</c>;
/// emits 0 (no-bet) when the signal is too close to a coin-flip. Mirrors
/// <see cref="StakingEngine.IsNoBet"/>.
/// Inputs: stake (decimal), pUp (decimal).
/// Output: stake (decimal).
/// Params: band (decimal, default 0.04).
/// </summary>
public sealed class GateNode : IFlowNode
{
    public string TypeId => "strategy.gate";

    public NodePortSpec Spec { get; } = new(
        Category: "strategy",
        Inputs: new[]
        {
            new PortDef("stake", "decimal"),
            new PortDef("pUp",   "decimal"),
        },
        Outputs: new[]
        {
            new PortDef("stake", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["band"] = new("decimal", false, StakingEngine.DefaultNoBetBand,
                "No-bet band: emits 0 when |pUp−0.5|·2 < band."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var stake = ToDecimal(inputs.GetValueOrDefault("stake")) ?? 0m;
        var pUp   = ToDecimal(inputs.GetValueOrDefault("pUp"))   ?? 0.5m;
        var band  = NodeParams.GetDecimal(nodeParams, "band", StakingEngine.DefaultNoBetBand);

        var result = StakingEngine.IsNoBet(pUp, band) ? 0m : stake;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["stake"] = result,
        });
    }
}

// ---------------------------------------------------------------------------
// Shared coerce helpers for strategy nodes — input port values arrive as object
// (decimal/double/int/bool) depending on the upstream node; normalise here.
// ---------------------------------------------------------------------------
internal static class StrategyNodeCoerce
{
    public static decimal? ToDecimal(object? v) => v switch
    {
        decimal d  => d,
        double  dd => (decimal)dd,
        float   f  => (decimal)f,
        int     i  => i,
        long    l  => l,
        _          => null,
    };

    public static bool? ToBool(object? v) => v switch
    {
        bool b => b,
        int  i => i != 0,
        _      => null,
    };
}
