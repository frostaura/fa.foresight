using System.Text.Json;

namespace FrostAura.Foresight.Domain.Descriptions;

/// <summary>
/// Deterministic, self-contained generator for model/strategy descriptions. Replaces the previous
/// OpenRouter (LLM) describers: there is no external dependency, no API key, and the same structure
/// always yields the same prose — so descriptions are stable, free, and offline.
///
/// It reads what the entity actually IS (kind, backtestability, and the flow-DAG's node types) and
/// renders a plain-English "simple" line plus a denser "technical" line. Pure functions — no I/O.
/// </summary>
public static class DescriptionTemplater
{
    // Must match the column limits in ForesightDbContext (SimpleDescription varchar(500),
    // TechnicalDescription varchar(1000)). Generated prose scales with the DAG's node-type count, so
    // a node-rich model can overflow — clamp here so the deterministic backfill can never violate the
    // constraint and abort the whole batch save.
    private const int SimpleMaxLen = 500;
    private const int TechnicalMaxLen = 1000;

    public static (string Simple, string Technical) ForModel(
        string name, string kind, bool supportsBacktesting, string? definitionJson)
    {
        var nodeTypes = ExtractNodeTypes(definitionJson);
        var signals = nodeTypes.Count > 0
            ? string.Join(", ", nodeTypes.Select(Humanize))
            : "its configured signal nodes";
        var deterministic = string.Equals(kind, "deterministic", StringComparison.OrdinalIgnoreCase);

        var simple =
            $"{name} is a {(deterministic ? "deterministic" : kind)} model that predicts short-horizon " +
            $"BTC up/down direction on 5m and 15m candles. It derives its signal from {signals}, and " +
            $"{(supportsBacktesting ? "can be backtested and chaos-tested against historical candles." : "is evaluated live rather than backtested.")}";

        var technical =
            $"{name} runs a {kind} flow DAG" +
            (nodeTypes.Count > 0 ? $" of {nodeTypes.Count} node type(s) — {signals}" : "") +
            $". It outputs a calibrated up-probability per candle (rescaled per interval) that feeds the " +
            $"odds-based staking layer. " +
            $"{(supportsBacktesting ? "Backtest/chaos-eligible" : "Live-only")}; honest single-candle " +
            $"directional accuracy for short-horizon crypto sits near the 50–55% regime, so edge comes " +
            $"from calibration and bet sizing rather than raw hit-rate.";

        return (Clamp(simple, SimpleMaxLen), Clamp(technical, TechnicalMaxLen));
    }

    public static (string Simple, string Technical) ForStrategy(
        string name, string? description, string? definitionJson)
    {
        // Built-in catalogue strategies are implemented in code and ship a curated Description — reuse
        // it verbatim rather than inventing weaker prose.
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            var desc = string.IsNullOrWhiteSpace(description)
                ? $"{name} sizes each bet according to its built-in staking rule."
                : description!.Trim();
            var simpleBuiltIn = FirstSentence(desc);
            return (Clamp(simpleBuiltIn, SimpleMaxLen), Clamp(desc, TechnicalMaxLen));
        }

        var nodeTypes = ExtractNodeTypes(definitionJson);
        var nodes = nodeTypes.Count > 0
            ? string.Join(", ", nodeTypes.Select(Humanize))
            : "its configured nodes";

        var simple =
            $"{name} is a custom staking strategy that decides each bet's size from {nodes}. " +
            $"It outputs a stake the engine sizes against the conservative entry price.";

        var technical =
            $"{name} is a DAG strategy composed of {nodeTypes.Count} node type(s) — {nodes} — terminating " +
            $"in a stake output. Sizing is a pure function of the upcoming candle's calibrated pUp, the " +
            $"current bankroll, and the prior outcome; a stake of 0 is a no-bet, and bets are floored at " +
            $"the venue minimum.";

        return (Clamp(simple, SimpleMaxLen), Clamp(technical, TechnicalMaxLen));
    }

    /// <summary>Distinct node-type strings from a flow-DAG definition, in stable sorted order. Empty on any parse issue.</summary>
    private static List<string> ExtractNodeTypes(string? definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson)) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return new List<string>();
            return nodes.EnumerateArray()
                .Select(n => n.TryGetProperty("type", out var t) ? t.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Turn a node-type id ("sma-cross", "rsi", "logistic_regression") into a readable label.</summary>
    private static string Humanize(string raw)
    {
        var tokens = raw.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens.Select(tok =>
            tok.Length <= 3 ? tok.ToUpperInvariant() : char.ToUpperInvariant(tok[0]) + tok[1..]));
    }

    private static string FirstSentence(string text)
    {
        var idx = text.IndexOf('.');
        return idx > 0 ? text[..(idx + 1)] : text;
    }

    /// <summary>
    /// Truncate to <paramref name="maxLen"/> characters, preferring a word boundary and appending an
    /// ellipsis. Guarantees the result fits its database column so a node-rich entity can't overflow.
    /// </summary>
    private static string Clamp(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        // Reserve one char for the ellipsis; back up to the last word boundary if one is close.
        var cut = maxLen - 1;
        var slice = text[..cut];
        var lastSpace = slice.LastIndexOf(' ');
        if (lastSpace > cut - 40) slice = slice[..lastSpace];
        return slice.TrimEnd(',', ' ', '—', '-', '.') + "…";
    }
}
