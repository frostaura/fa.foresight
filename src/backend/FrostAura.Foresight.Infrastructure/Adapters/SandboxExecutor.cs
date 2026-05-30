using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// HTTP adapter for the Python sandbox sidecar. Sends a <see cref="SandboxRequest"/> as
/// camelCase JSON to POST {SandboxBaseUrl}/execute and deserialises the <see cref="SandboxResult"/>
/// response. Registered as a typed HttpClient; base URL is read from Sandbox:BaseUrl (default
/// "http://sandbox:8000" for compose, "http://localhost:8000" for local dev).
///
/// If the sidecar is down the call throws a <see cref="FlowExecutionException"/> — the sidecar
/// being unavailable is a node-execution failure, not a startup failure.
/// </summary>
public sealed class SandboxExecutor : ISandboxExecutor
{
    private readonly HttpClient _http;
    private readonly ILogger<SandboxExecutor> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SandboxExecutor(HttpClient http, ILogger<SandboxExecutor> logger, IConfiguration config)
    {
        var baseUrl = config.GetSection("Sandbox")["BaseUrl"]
            ?? "http://sandbox:8000";
        http.BaseAddress = new Uri(baseUrl);
        _http = http;
        _logger = logger;
    }

    public async Task<SandboxResult> ExecuteAsync(SandboxRequest req, CancellationToken ct)
    {
        try
        {
            var content = JsonContent.Create(req, options: _jsonOpts);
            var response = await _http.PostAsync("/execute", content, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SandboxResult>(_jsonOpts, ct)
                ?? throw new InvalidOperationException("Sandbox returned null response body.");
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Sandbox sidecar unreachable at {BaseUrl}", _http.BaseAddress);
            throw new FlowExecutionException(req.NodeId,
                new InvalidOperationException($"Sandbox sidecar unreachable: {ex.Message}", ex));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FlowExecutionException(req.NodeId,
                new InvalidOperationException("Sandbox request timed out."));
        }
    }
}
