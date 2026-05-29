using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// Minimal typed HttpClient for OpenRouter's chat-completions endpoint.
/// Used exclusively for metadata generation (AI model descriptions) — this is NOT the
/// prediction-LLM pipeline that was removed; it is a lightweight, background-only helper.
///
/// Degrades gracefully when <c>OpenRouter:ApiKey</c> is absent or empty — every call returns
/// null without throwing, so the request path and tests remain unaffected.
/// </summary>
public sealed class OpenRouterClient
{
    private const string ChatCompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string Model = "openai/gpt-5.4-mini";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly ILogger<OpenRouterClient> _logger;

    public OpenRouterClient(HttpClient http, IConfiguration config, ILogger<OpenRouterClient> logger)
    {
        _http = http;
        _apiKey = config["OpenRouter:ApiKey"];
        _logger = logger;
    }

    /// <summary>
    /// Sends a single-turn chat completion and returns the assistant message content.
    /// Returns <c>null</c> on missing API key, HTTP failure, or any other error — never throws
    /// into the caller (all callers are background continuations).
    /// </summary>
    public async Task<string?> CompleteAsync(string system, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        var body = new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user },
            },
            max_tokens = 300,
        };

        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Headers.Add("HTTP-Referer", "https://foresight.frostaura.com");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenRouter returned {Status} for model description generation", response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenRouter request failed during model description generation");
            return null;
        }
    }
}
