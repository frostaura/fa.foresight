namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Port for the Python sandbox sidecar. Executes user-supplied Python snippets in an isolated
/// process and returns their outputs. The concrete adapter lives in Infrastructure and calls the
/// sidecar HTTP endpoint; tests supply a fake implementation.
/// </summary>
public interface ISandboxExecutor
{
    Task<SandboxResult> ExecuteAsync(SandboxRequest req, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// Request / result shapes — mirror the Python sidecar's HTTP contract exactly.
// ---------------------------------------------------------------------------

/// <summary>
/// Wire format sent to <c>POST {sidecar}/execute</c>. Must be serialised as camelCase JSON.
/// tag ∈ "scalar" | "series" | "candles" | "matrix"
/// </summary>
public sealed record SandboxRequest
{
    public int ProtocolVersion { get; init; } = 1;
    /// <summary>"step" (single-bar live / step-through) or "batch" (backtest).</summary>
    public required string Mode { get; init; }
    public required string NodeId { get; init; }
    /// <summary>The Python source code to execute (user-authored).</summary>
    public required string Code { get; init; }
    public long Seed { get; init; }
    /// <summary>Node-level params forwarded verbatim to the Python execution context.</summary>
    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>();
    /// <summary>Number of rows in batch mode (ignored for "step").</summary>
    public int SeriesLength { get; init; }
    /// <summary>Input port values keyed by port name.</summary>
    public required IReadOnlyDictionary<string, SandboxPortValue> Inputs { get; init; }
    /// <summary>Declares the expected output ports and their type tags.</summary>
    public required IReadOnlyDictionary<string, string> OutputSchema { get; init; }
    public SandboxLimits Limits { get; init; } = new();
}

public sealed record SandboxPortValue(
    /// <summary>tag ∈ "scalar" | "series" | "candles" | "matrix"</summary>
    string Tag,
    /// <summary>
    /// For "scalar": number | bool | string.
    /// For "series": number[].
    /// For "candles": SandboxCandlesValue (columnar arrays).
    /// For "matrix": SandboxMatrixValue.
    /// </summary>
    object? Value);

public sealed record SandboxCandlesValue(
    long[] OpenTime,
    double[] Open,
    double[] High,
    double[] Low,
    double[] Close,
    double[] Volume);

public sealed record SandboxMatrixValue(
    string[] Columns,
    double[][] Rows);

public sealed record SandboxLimits(int TimeoutMs = 5_000, int MemMb = 256);

// ---------------------------------------------------------------------------
// Result
// ---------------------------------------------------------------------------

public sealed record SandboxResult
{
    public bool Ok { get; init; }
    public IReadOnlyDictionary<string, SandboxPortValue> Outputs { get; init; }
        = new Dictionary<string, SandboxPortValue>();
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public int DurationMs { get; init; }
    /// <summary>Stable hash over the serialised outputs; used for determinism checks.</summary>
    public string? OutputHash { get; init; }
    public SandboxError? Error { get; init; }
}

public sealed record SandboxError(string Kind, string Message);
