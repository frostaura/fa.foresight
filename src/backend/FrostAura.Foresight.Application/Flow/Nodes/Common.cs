using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Small helpers shared by node implementations. Keeps the per-node files focused on logic instead
/// of repeating JSON-element unwrapping ceremony.
/// </summary>
internal static class NodeParams
{
    public static T? Get<T>(JsonElement p, string name, T? fallback = default)
    {
        if (p.ValueKind != JsonValueKind.Object) return fallback;
        if (!p.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        try { return v.Deserialize<T>(JsonOpts.Web); }
        catch { return fallback; }
    }

    public static string GetString(JsonElement p, string name, string fallback)
    {
        if (p.ValueKind != JsonValueKind.Object) return fallback;
        return p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
    }

    public static int GetInt(JsonElement p, string name, int fallback)
    {
        if (p.ValueKind != JsonValueKind.Object) return fallback;
        if (!p.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;
    }

    public static decimal GetDecimal(JsonElement p, string name, decimal fallback)
    {
        if (p.ValueKind != JsonValueKind.Object) return fallback;
        if (!p.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : fallback;
    }
}

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>Tiny shape for the matrix output port — feature columns + rows × cols matrix.</summary>
public sealed record FeatureMatrix(IReadOnlyList<string> Columns, double[,] Rows)
{
    public int RowCount => Rows.GetLength(0);
    public int ColumnCount => Rows.GetLength(1);
}

/// <summary>The output of a model node — what the terminal output.prediction node consumes.</summary>
public sealed record ModelOutput(decimal PUp, decimal Confidence);
