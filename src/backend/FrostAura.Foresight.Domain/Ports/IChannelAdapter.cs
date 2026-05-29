namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Outbound notifications + inbound commands. Adapters: Discord, Telegram (v1); Slack/SMS/email/push later.
/// </summary>
public interface IChannelAdapter : INotificationChannel, ICommandChannel
{
    string ChannelId { get; }
    bool SupportsRichContent { get; }
}

public interface INotificationChannel
{
    Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct);

    /// <summary>
    /// Send channel-agnostic rich content (text + optional table / chart / button rows). Adapters that
    /// support rich primitives (Telegram inline keyboards + photo, Discord components + embeds) override
    /// this to render natively; the default flattens to a plain-text notification so every channel
    /// renders *something* without bespoke code. Buttons degrade to a hint line in the fallback.
    /// </summary>
    Task SendRichAsync(Guid tenantId, NotificationKind kind, string title, RichContent content, CancellationToken ct)
        => SendAsync(tenantId, new OutboundNotification(kind, title, content.RenderText()), ct);
}

public interface ICommandChannel
{
    Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler);
    Task StartListeningAsync(Guid tenantId, CancellationToken ct);
    Task StopListeningAsync(Guid tenantId, CancellationToken ct);
}

public sealed record OutboundNotification(
    NotificationKind Kind,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record InboundCommand(
    Guid TenantId,
    Guid PrincipalId,
    PermissionTier PermissionTier,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record CommandResponse(bool Success, string Message, IReadOnlyDictionary<string, string>? Metadata = null, RichContent? Rich = null);

/// <summary>Result of a channel connectivity self-test (e.g. Telegram getMe, Discord users/@me).</summary>
public sealed record ChannelHealth(bool Ok, string Detail);

/// <summary>
/// Channel-agnostic rich payload. The router builds this; each adapter renders it to its native
/// primitives (or the default text fallback). Any field may be null — a response can be pure text,
/// a table, a chart, a button menu, or any combination.
/// </summary>
public sealed record RichContent(
    string? Text = null,
    RichTable? Table = null,
    RichChart? Chart = null,
    IReadOnlyList<RichButtonRow>? Buttons = null,
    byte[]? ImagePng = null,
    string? ImageCaption = null)
{
    /// <summary>Plain-text rendering used by the fallback path and any text-only channel (e.g. log).</summary>
    public string RenderText()
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(Text)) sb.AppendLine(Text);
        if (Table is not null) sb.AppendLine(Table.RenderMonospace());
        if (Chart is not null) sb.AppendLine(Chart.RenderAscii());
        if (Buttons is { Count: > 0 })
        {
            var labels = Buttons.SelectMany(r => r.Buttons).Select(b => $"[{b.Label}]");
            sb.AppendLine("Actions: " + string.Join(" ", labels));
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A simple table; rendered as a monospace block on text channels, an embed/grid where supported.</summary>
public sealed record RichTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string? Title = null)
{
    public string RenderMonospace()
    {
        var cols = Headers.Count;
        var widths = new int[cols];
        for (var c = 0; c < cols; c++) widths[c] = Headers[c].Length;
        foreach (var row in Rows)
            for (var c = 0; c < cols && c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);

        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", Enumerable.Range(0, cols).Select(c => (c < cells.Count ? cells[c] : "").PadRight(widths[c])));

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(Title)) sb.AppendLine(Title);
        sb.AppendLine("```");
        sb.AppendLine(Line(Headers));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in Rows) sb.AppendLine(Line(row));
        sb.Append("```");
        return sb.ToString();
    }
}

public enum RichChartKind { Line, Bar }

/// <summary>A chart spec. Adapters that can render images turn this into a PNG; text channels get an ASCII sparkline.</summary>
public sealed record RichChart(string Title, RichChartKind Kind, IReadOnlyList<RichSeries> Series, IReadOnlyList<string>? XLabels = null)
{
    private const string Spark = "▁▂▃▄▅▆▇█";

    public string RenderAscii()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Title);
        foreach (var s in Series)
        {
            if (s.Points.Count == 0) { sb.AppendLine($"{s.Name}: (no data)"); continue; }
            var min = s.Points.Min();
            var max = s.Points.Max();
            var range = max - min;
            var chars = s.Points.Select(p =>
            {
                var idx = range == 0m ? 0 : (int)Math.Round((p - min) / range * (Spark.Length - 1));
                return Spark[Math.Clamp(idx, 0, Spark.Length - 1)];
            });
            sb.AppendLine($"{s.Name}: {new string(chars.ToArray())}  ({min:0.##}→{max:0.##})");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record RichSeries(string Name, IReadOnlyList<decimal> Points, string? ColorHex = null);

/// <summary>A row of buttons. Pressing a button re-enters the command pipeline with Command [+ Arg].</summary>
public sealed record RichButtonRow(IReadOnlyList<RichButton> Buttons);

public sealed record RichButton(string Label, string Command, string? Arg = null);

public enum NotificationKind
{
    HighEdgeMarket,
    PositionResolution,
    DailyBriefing,
    WhaleAlert,
    AutotradeExecution,
    CircuitBreakerTripped
}

public enum PermissionTier
{
    ReadOnly,
    Command,
    AutotradeControl
}
