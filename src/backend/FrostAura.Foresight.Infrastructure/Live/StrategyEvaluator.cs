using System.Text.Json;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Scoped implementation of <see cref="IStrategyEvaluator"/>. Resolves strategy ids in priority
/// order:
///   1. Built-in catalogue (<see cref="StakingStrategies.All"/>) — pure synchronous math.
///   2. Custom DAG strategies in the <c>strategies</c> DB table — executed via <see cref="IFlowExecutor"/>.
///
/// The DAG execution path loads the strategy's <see cref="FrostAura.Foresight.Application.Flow.FlowDefinition"/>
/// once per call (the definition is small and the hot path caches at the caller level). No trained
/// state is injected — strategy nodes are pure functions of their declared inputs and node-level params.
/// </summary>
public sealed class StrategyEvaluator : IStrategyEvaluator
{
    private readonly ForesightDbContext _db;
    private readonly IFlowExecutor _executor;
    private readonly ILogger<StrategyEvaluator> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public StrategyEvaluator(ForesightDbContext db, IFlowExecutor executor, ILogger<StrategyEvaluator> logger)
    {
        _db = db;
        _executor = executor;
        _logger = logger;
    }

    public bool IsBuiltIn(string strategyId) => StakingStrategies.IsKnown(strategyId);

    public async Task<decimal> NextStakeAsync(
        string strategyId,
        StrategyStep step,
        FlowContext flowCtx,
        CancellationToken ct)
    {
        // ── Fast path: built-in code strategy ────────────────────────────────────────────────
        if (StakingStrategies.IsKnown(strategyId))
            return StakingStrategies.Resolve(strategyId).NextBetSize(step);

        // ── Custom DAG strategy path ──────────────────────────────────────────────────────────
        // Try to parse as a Guid (custom strategy ids are Guids).
        if (!Guid.TryParse(strategyId, out var strategyGuid))
        {
            _logger.LogWarning("StrategyEvaluator: unknown strategy id '{Id}' — not a built-in and not a valid Guid; returning 0", strategyId);
            return 0m;
        }

        // Load from DB — visible to the current tenant (own rows) or global (TenantId = null).
        var row = await _db.Strategies.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyGuid, ct);

        if (row is null)
        {
            _logger.LogWarning("StrategyEvaluator: strategy {Id} not found in DB; returning 0", strategyId);
            return 0m;
        }

        if (string.IsNullOrWhiteSpace(row.Definition))
        {
            _logger.LogWarning("StrategyEvaluator: strategy {Id} has no DAG definition; returning 0", strategyId);
            return 0m;
        }

        FlowDefinition? flow;
        try
        {
            flow = JsonSerializer.Deserialize<FlowDefinition>(row.Definition, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "StrategyEvaluator: strategy {Id} definition is not valid JSON; returning 0", strategyId);
            return 0m;
        }

        if (flow is null)
        {
            _logger.LogWarning("StrategyEvaluator: strategy {Id} definition deserialized to null; returning 0", strategyId);
            return 0m;
        }

        // Execute the strategy DAG. Strategy nodes (edge_aware_kelly, clamp_round, gate, etc.) are
        // pure functions of their declared input ports. Ports in the StrategyContextPorts set are
        // injected via FlowContext.AmbientInputs rather than requiring upstream edges — this matches
        // the validator exemption so the same set drives both creation and execution.
        var ambientInputs = BuildAmbientInputs(step);
        var strategyCtx = flowCtx with { AmbientInputs = ambientInputs };
        try
        {
            var result = await _executor.ExecuteAsync(flow, strategyCtx, ct);

            // Pull the terminal output.stake value.
            if (result.OutputPrediction.TryGetValue("stake", out var stakeObj) && stakeObj is decimal stakeDecimal)
                return stakeDecimal;

            // Fallback coerce for double/int that might come through JsonElement deserialization.
            if (stakeObj is double stakeDouble) return (decimal)stakeDouble;
            if (stakeObj is int stakeInt) return stakeInt;

            _logger.LogWarning("StrategyEvaluator: strategy {Id} output.stake was null or unrecognised type {Type}; returning 0",
                strategyId, stakeObj?.GetType().Name ?? "null");
            return 0m;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StrategyEvaluator: strategy {Id} DAG execution failed; returning 0", strategyId);
            return 0m;
        }
    }

    /// <summary>
    /// Builds the ambient-inputs dictionary from a <see cref="StrategyStep"/> so that strategy
    /// DAG nodes can read their context ports without requiring upstream edges. Port names are the
    /// EXACT names declared in <see cref="FlowValidator.StrategyContextPorts"/> — this is the
    /// single mapping that keeps execution and validation in sync.
    ///
    /// Mapping:
    ///   pUp        ← step.Inputs.PUp          (calibrated up-probability)
    ///   yesPrice   ← step.Inputs.YesPrice      (Polymarket YES price)
    ///   noPrice    ← step.Inputs.NoPrice        (Polymarket NO price)
    ///   balance    ← step.NextBankroll          (post-settlement bankroll)
    ///   currentBet ← step.CurrentBetSize        (size of the just-settled bet)
    ///   initialBet ← step.InitialBetSize        (session initial-bet size)
    ///   lastOutcome← step.Won                   (true = last bet won)
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildAmbientInputs(StrategyStep step) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pUp"]         = step.Inputs.PUp,
            ["yesPrice"]    = step.Inputs.YesPrice,
            ["noPrice"]     = step.Inputs.NoPrice,
            ["balance"]     = step.NextBankroll,
            ["currentBet"]  = step.CurrentBetSize,
            ["initialBet"]  = step.InitialBetSize,
            ["lastOutcome"] = step.Won,
        };
}

