using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Domain.Strategies;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Generates and persists AI-authored descriptions for a staking strategy.
/// Mirrors <see cref="ModelDescriber"/> in pattern: fire-and-forget via <see cref="EnqueueAsync"/>,
/// two parallel OpenRouter calls (simple + technical), graceful degradation when the API key
/// is absent or a call fails.
/// </summary>
public sealed class StrategyDescriber
{
    private readonly OpenRouterClient _openRouter;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StrategyDescriber> _logger;

    public StrategyDescriber(OpenRouterClient openRouter, IServiceScopeFactory scopes, ILogger<StrategyDescriber> logger)
    {
        _openRouter = openRouter;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Fire-and-forget: generates descriptions for <paramref name="strategyId"/> in a background
    /// task that owns its own DI scope. The caller receives control back immediately.
    /// </summary>
    public void EnqueueAsync(Guid strategyId) =>
        _ = Task.Run(() => GenerateAndPersistAsync(strategyId, CancellationToken.None), CancellationToken.None);

    private async Task GenerateAndPersistAsync(Guid strategyId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        try
        {
            var strategy = await db.Strategies.FirstOrDefaultAsync(s => s.Id == strategyId, ct);
            if (strategy is null)
            {
                _logger.LogWarning("StrategyDescriber: strategy {Id} not found — skipping description generation", strategyId);
                return;
            }

            var (simple, technical) = await GenerateAsync(strategy, ct);
            if (simple is null && technical is null)
                return; // no API key or call failed — leave existing nulls intact

            strategy.SimpleDescription = simple ?? strategy.SimpleDescription;
            strategy.TechnicalDescription = technical ?? strategy.TechnicalDescription;
            strategy.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("StrategyDescriber: persisted descriptions for strategy {Id} ({Name})", strategy.Id, strategy.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyDescriber: background generation failed for strategy {Id}", strategyId);
        }
    }

    /// <summary>
    /// Calls OpenRouter with two separate prompts and returns (simple, technical).
    /// Either value may be null if the key is absent or the call fails — always degrades gracefully.
    /// </summary>
    private async Task<(string? Simple, string? Technical)> GenerateAsync(Strategy strategy, CancellationToken ct)
    {
        var strategySummary = SummarizeStrategy(strategy);

        const string sharedSystem =
            "You are a concise, precise technical writer for a quantitative trading platform called Foresight. " +
            "Write only the description requested — no preamble, no trailing commentary.";

        // Simple description prompt
        var simpleUser = new StringBuilder();
        simpleUser.AppendLine($"Strategy name: {strategy.Name}");
        simpleUser.AppendLine($"Strategy summary: {strategySummary}");
        simpleUser.AppendLine();
        simpleUser.AppendLine(
            "Write 1–2 plain-English sentences describing how this staking strategy works and what kind of " +
            "trader it suits. Focus on: what drives bet sizing, when it bets big vs small, and its key risk " +
            "characteristic. Avoid jargon.");

        // Technical description prompt
        var technicalUser = new StringBuilder();
        technicalUser.AppendLine($"Strategy name: {strategy.Name}");
        technicalUser.AppendLine($"Strategy summary: {strategySummary}");
        technicalUser.AppendLine();
        technicalUser.AppendLine(
            "Write 2–3 sentences for a quantitative trader describing this staking strategy. " +
            "Include: the sizing formula or rule, what inputs it uses (edge, bankroll, prior outcome), " +
            "ruin risk, and when it outperforms or underperforms relative to the flat baseline.");

        var simple    = await _openRouter.CompleteAsync(sharedSystem, simpleUser.ToString(),    ct);
        var technical = await _openRouter.CompleteAsync(sharedSystem, technicalUser.ToString(), ct);

        return (simple, technical);
    }

    /// <summary>
    /// Extracts a human-readable summary of the strategy for LLM grounding.
    /// For built-in code strategies: uses Name + Description.
    /// For DAG strategies: lists node types from the Definition JSON.
    /// </summary>
    private static string SummarizeStrategy(Strategy strategy)
    {
        if (strategy.Definition is null)
        {
            // Built-in code strategy: ground with name + description text.
            var desc = string.IsNullOrWhiteSpace(strategy.Description)
                ? "No description available."
                : strategy.Description;
            return $"Built-in code strategy. Description: {desc}";
        }

        // DAG strategy: extract node types from the flow definition (mirrors ModelDescriber).
        try
        {
            using var doc = JsonDocument.Parse(strategy.Definition);
            var root = doc.RootElement;

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
                ? $"Custom DAG strategy. Node types present: {string.Join(", ", unique)}"
                : "Custom DAG strategy (node types not extracted)";
        }
        catch
        {
            return "Custom DAG strategy (definition available)";
        }
    }
}
