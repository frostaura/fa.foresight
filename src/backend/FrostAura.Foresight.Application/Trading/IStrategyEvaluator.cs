using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Domain.Trading;

namespace FrostAura.Foresight.Application.Trading;

/// <summary>
/// Unified strategy evaluator that handles BOTH built-in code strategies (from
/// <see cref="StakingStrategies"/>) and custom DAG strategies stored in the
/// <c>strategies</c> table.
///
/// For a built-in id (flat / martingale / kelly / kelly-d1 / kelly-edge), delegates directly to
/// <see cref="IStakingStrategy.NextBetSize"/> — zero overhead vs. the existing direct call.
///
/// For a custom DAG strategy id (a Guid string matching a row in <c>strategies</c>), executes the
/// flow via <see cref="IFlowExecutor"/>, mapping the <see cref="StrategyStep"/> context onto the
/// strategy flow's named input ports and reading the terminal <c>output.stake</c> result.
///
/// Input-port mapping for DAG strategies (all optional — nodes fall back to defaults when absent):
///   pUp       → StrategyStep.Inputs.PUp
///   yesPrice  → StrategyStep.Inputs.YesPrice
///   noPrice   → StrategyStep.Inputs.NoPrice
///   balance   → StrategyStep.NextBankroll
///   currentBet   → StrategyStep.CurrentBetSize
///   initialBet   → StrategyStep.InitialBetSize
///   lastOutcome  → StrategyStep.Won  (bool)
/// </summary>
public interface IStrategyEvaluator
{
    /// <summary>
    /// Compute the next stake for <paramref name="strategyId"/> given the current step context.
    ///
    /// Returns 0 (a legitimate no-bet) when:
    ///  — the strategy gates out (no edge, sub-$1 rounding, confidence band),
    ///  — the DAG flow's output.stake node emits 0, or
    ///  — the strategy id cannot be resolved (unknown built-in; strategy row not found / no definition).
    ///
    /// THROWS <see cref="StrategyEvaluationException"/> when a custom DAG strategy is BROKEN — invalid
    /// JSON, the DAG throws during execution, or it produces no usable stake. This is deliberately NOT
    /// swallowed as a 0: a broken strategy must fail loud (stop the session / fail the run), not read
    /// as a silent no-bet forever.
    ///
    /// For built-ins the call is synchronous-equivalent (no async I/O). For DAG strategies, the
    /// flow executor runs in-process — no network calls from pure strategy nodes.
    /// </summary>
    Task<decimal> NextStakeAsync(
        string strategyId,
        StrategyStep step,
        FlowContext flowCtx,
        CancellationToken ct);

    /// <summary>
    /// Returns true when <paramref name="strategyId"/> resolves to a known built-in. False for
    /// custom DAG strategy Guid ids.
    /// </summary>
    bool IsBuiltIn(string strategyId);
}
