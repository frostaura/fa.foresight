using System.Text.Json;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Ports;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// Authoring-support endpoints for the dual-view flow designer: structural validation and the
/// notebook-style step-through runner. Both accept an unsaved <see cref="FlowDefinition"/> so the
/// designer can validate / run before persisting. Used for models AND strategies (the validator
/// keys the terminal-node rule off <see cref="FlowDefinition.DefinitionKind"/>).
/// </summary>
public static class FlowsEndpoints
{
    public static void MapFlowsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/flows").WithTags("flows");

        // Structural validation — unique ids, known types, single terminal (kind-aware), resolvable
        // typed ports, no cycles, backtest flows reject live-data nodes.
        g.MapPost("/validate", (FlowDefinition definition, FlowValidator validator) =>
        {
            var result = validator.Validate(definition);
            return Results.Ok(new { ok = result.IsValid, error = result.Error });
        });

        // Step-through: execute the flow and return one node's outputs + the full node trace, with a
        // determinism check (run twice, compare the target node's outputs byte-for-byte). Latency-
        // tolerant authoring aid — runs the whole flow and extracts the requested node from the trace.
        g.MapPost("/run-node", async (
            RunNodeRequest req,
            ITenantContext tc,
            FlowValidator validator,
            IFlowExecutor executor,
            IHistoricalCandleProvider candles,
            CancellationToken ct) =>
        {
            var valid = validator.Validate(req.Definition);
            if (!valid.IsValid) return Results.BadRequest(new { error = valid.Error });

            var tenantId = tc.IsResolved ? tc.TenantId!.Value : Guid.Empty;
            var ctx = new FlowContext(
                TenantId: tenantId,
                ModelId: Guid.Empty,
                Symbol: req.Symbol ?? "BTCUSDT",
                Interval: req.Interval ?? "5m",
                TargetOpenTime: req.TargetOpenTime ?? 0,
                Horizon: 1,
                Mode: FlowMode.Live,
                HistoricalCandles: candles,
                TrainedState: null);

            try
            {
                var first = await executor.ExecuteAsync(req.Definition, ctx, ct);
                if (!first.NodeOutputs.TryGetValue(req.NodeId, out var outputs))
                    return Results.BadRequest(new { error = $"Node '{req.NodeId}' produced no output (not in flow or upstream skipped)." });

                // Determinism check: a second identical execution must produce byte-identical output
                // for the target node. This is the author-facing surface of the purity contract.
                var second = await executor.ExecuteAsync(req.Definition, ctx, ct);
                second.NodeOutputs.TryGetValue(req.NodeId, out var outputs2);
                var deterministic = JsonSerializer.Serialize(outputs) == JsonSerializer.Serialize(outputs2);

                return Results.Ok(new
                {
                    outputs,
                    trace = first.NodeOutputs,
                    deterministic,
                });
            }
            catch (FlowExecutionException ex)
            {
                return Results.Json(new { error = ex.Message, node = ex.NodeId }, statusCode: 502);
            }
        });
    }
}

public sealed record RunNodeRequest(
    FlowDefinition Definition,
    string NodeId,
    string? Symbol,
    string? Interval,
    long? TargetOpenTime,
    JsonElement? UpstreamOverrides);
