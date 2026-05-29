namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Static port description for a node type. Drives validation (edge connectivity, type-tag
/// compatibility) and the AI assistant's catalogue prompt.
/// </summary>
public sealed record NodePortSpec(
    string Category,
    IReadOnlyList<PortDef> Inputs,
    IReadOnlyList<PortDef> Outputs,
    IReadOnlyDictionary<string, ParamDef> Params,
    /// <summary>If true, the validator accepts arbitrary additional input ports by name (used by
    /// feature.matrix_builder, which collects N named scalar columns).</summary>
    bool AcceptsAdditionalInputs = false,
    /// <summary>If true, sourcing this node requires non-replayable live data (orderbook,
    /// tradeflow, funding, etc.) — flows that contain it must declare <c>supportsBacktesting=false</c>.</summary>
    bool RequiresLiveData = false);

public sealed record PortDef(string Name, string TypeTag, bool Required = true, string? Description = null);

public sealed record ParamDef(string TypeTag, bool Required, object? Default = null, string? Description = null);
