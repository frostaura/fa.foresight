using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Generates and persists AI-authored descriptions for a model.
/// <list type="bullet">
///   <item>Simple — 1–2 plain-English sentences for a non-expert (what it predicts + the gist).</item>
///   <item>Technical — 2–3 sentences for a data scientist (features, estimator, intervals, caveats).</item>
/// </list>
/// Generation is triggered via <see cref="EnqueueAsync"/> which fires-and-forgets a background
/// continuation on its own DI scope. The calling HTTP request returns immediately.
/// </summary>
public sealed class ModelDescriber
{
    private readonly OpenRouterClient _openRouter;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ModelDescriber> _logger;

    public ModelDescriber(OpenRouterClient openRouter, IServiceScopeFactory scopes, ILogger<ModelDescriber> logger)
    {
        _openRouter = openRouter;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Fire-and-forget: generates descriptions for <paramref name="modelId"/> in a background
    /// task that owns its own DI scope. The caller receives control back immediately.
    /// </summary>
    public void EnqueueAsync(Guid modelId) =>
        _ = Task.Run(() => GenerateAndPersistAsync(modelId, CancellationToken.None), CancellationToken.None);

    private async Task GenerateAndPersistAsync(Guid modelId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        try
        {
            var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
            if (model is null)
            {
                _logger.LogWarning("ModelDescriber: model {Id} not found — skipping description generation", modelId);
                return;
            }

            var (simple, technical) = await GenerateAsync(model, ct);
            if (simple is null && technical is null)
                return; // no API key or call failed — leave existing nulls intact

            model.SimpleDescription = simple ?? model.SimpleDescription;
            model.TechnicalDescription = technical ?? model.TechnicalDescription;
            model.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("ModelDescriber: persisted descriptions for model {Id} ({Name})", model.Id, model.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ModelDescriber: background generation failed for model {Id}", modelId);
        }
    }

    /// <summary>
    /// Calls OpenRouter with two separate prompts and returns (simple, technical).
    /// Either value may be null if the key is absent or the call fails — always degrades gracefully.
    /// </summary>
    private async Task<(string? Simple, string? Technical)> GenerateAsync(Model model, CancellationToken ct)
    {
        var flowSummary = SummarizeFlow(model.Definition);

        const string sharedSystem =
            "You are a concise, precise technical writer for a quantitative trading platform called Foresight. " +
            "Write only the description requested — no preamble, no trailing commentary.";

        // Simple description prompt
        var simpleUser = new StringBuilder();
        simpleUser.AppendLine($"Model name: {model.Name}");
        simpleUser.AppendLine($"Model kind: {model.Kind}");
        simpleUser.AppendLine($"Flow summary: {flowSummary}");
        simpleUser.AppendLine();
        simpleUser.AppendLine(
            "Write 1–2 plain-English sentences describing what this model does and how it works, " +
            "suitable for a non-expert trader. Focus on: what it predicts, what data it uses (in general terms), " +
            "and whether it can be backtested. Avoid jargon.");

        // Technical description prompt
        var technicalUser = new StringBuilder();
        technicalUser.AppendLine($"Model name: {model.Name}");
        technicalUser.AppendLine($"Model kind: {model.Kind}");
        technicalUser.AppendLine($"Flow summary: {flowSummary}");
        technicalUser.AppendLine();
        technicalUser.AppendLine(
            "Write 2–3 sentences for a data scientist describing this prediction model. " +
            "Include: the feature set (indicator nodes used), the estimator/algorithm, supported intervals, " +
            "and an honest framing of its expected accuracy ceiling and limitations.");

        var simple    = await _openRouter.CompleteAsync(sharedSystem, simpleUser.ToString(),    ct);
        var technical = await _openRouter.CompleteAsync(sharedSystem, technicalUser.ToString(), ct);

        return (simple, technical);
    }

    /// <summary>
    /// Extracts a human-readable summary of the flow DAG — lists the unique node types present
    /// so the LLM prompt has enough grounding without embedding the full JSON (~10 KB).
    /// </summary>
    private static string SummarizeFlow(string definitionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            var root = doc.RootElement;

            // Collect node types
            var nodeTypes = new List<string>();
            if (root.TryGetProperty("nodes", out var nodes))
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    if (node.TryGetProperty("type", out var t))
                        nodeTypes.Add(t.GetString() ?? "unknown");
                }
            }

            var unique = nodeTypes.Distinct().OrderBy(x => x).ToList();
            return unique.Count > 0
                ? $"Node types present: {string.Join(", ", unique)}"
                : "Flow definition available (node types not extracted)";
        }
        catch
        {
            return "Flow definition available";
        }
    }
}
