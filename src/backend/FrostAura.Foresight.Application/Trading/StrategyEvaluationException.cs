namespace FrostAura.Foresight.Application.Trading;

/// <summary>
/// Thrown when a custom DAG strategy is BROKEN — its definition is not valid JSON, its DAG throws
/// during execution, or it produces no usable stake output. This is distinct from a valid strategy
/// that legitimately sizes to 0 (a real no-bet). Callers must surface it (stop + notify the session,
/// or mark a backtest/chaos run failed) rather than swallowing it as a silent no-bet — a broken
/// strategy that reads as "no-bet forever" hides a real configuration/authoring error.
/// </summary>
public sealed class StrategyEvaluationException : Exception
{
    /// <summary>The strategy id that failed to evaluate.</summary>
    public string StrategyId { get; }

    public StrategyEvaluationException(string strategyId, string message, Exception? inner = null)
        : base(message, inner)
    {
        StrategyId = strategyId;
    }
}
