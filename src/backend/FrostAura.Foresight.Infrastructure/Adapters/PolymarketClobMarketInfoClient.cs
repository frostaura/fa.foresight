using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// Resolves Polymarket CLOB market info (tokenIds, negRisk, mts, mos, fees) by conditionId.
/// Results are cached per conditionId for the lifetime of the instance (session-scoped in practice).
///
/// Uses the CLOB API: GET /markets/{conditionId} (or gamma-api fallback).
/// The negRisk flag routes the EIP-712 verifyingContract:
///   negRisk=true  → NegRiskCtfExchange  0xe2222d279d744050d28e00520010520000310F59
///   negRisk=false → CtfExchange         0xE111180000d2663C0091e4f400237545B87B996B
/// </summary>
public sealed class PolymarketClobMarketInfoClient
{
    private readonly HttpClient _http;
    private readonly PolymarketExecutionOptions _opts;
    private readonly ILogger _logger;

    // conditionId → MarketInfo cache (never evicted — markets don't change mts/mos mid-session).
    private readonly ConcurrentDictionary<string, ClobMarketInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PolymarketClobMarketInfoClient(
        HttpClient http,
        PolymarketExecutionOptions opts,
        ILogger logger)
    {
        _http = http;
        _opts = opts;
        _logger = logger;
    }

    /// <summary>
    /// Return market info for the given conditionId (= MarketExternalId).
    /// On cache miss: calls the CLOB REST endpoint, parses, and caches.
    /// </summary>
    public async Task<ClobMarketInfo> GetMarketInfoAsync(string conditionId, CancellationToken ct)
    {
        if (_cache.TryGetValue(conditionId, out var cached))
            return cached;

        var info = await FetchAsync(conditionId, ct);
        _cache[conditionId] = info;
        return info;
    }

    private async Task<ClobMarketInfo> FetchAsync(string conditionId, CancellationToken ct)
    {
        // Primary: CLOB API /markets/{conditionId}
        var url = $"{_opts.ClobBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(conditionId)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var info = ParseClobMarketInfo(conditionId, body);
                if (info is not null)
                {
                    _logger.LogDebug("CLOB market info loaded for {ConditionId}: negRisk={NegRisk} mts={Mts} mos={Mos}",
                        conditionId, info.NegRisk, info.Mts, info.Mos);
                    return info;
                }
            }
            else
            {
                _logger.LogDebug("CLOB /markets/{ConditionId} returned {Status}; will try gamma-api", conditionId, resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLOB /markets/{ConditionId} failed; will try gamma-api", conditionId);
        }

        // Fallback: gamma-api /markets/{conditionId}
        var gammaUrl = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(conditionId)}";
        using var gammaResp = await _http.GetAsync(gammaUrl, ct);
        gammaResp.EnsureSuccessStatusCode();
        var gammaBody = await gammaResp.Content.ReadAsStringAsync(ct);
        var gammaInfo = ParseGammaMarketInfo(conditionId, gammaBody);
        _logger.LogDebug("gamma-api market info loaded for {ConditionId}: negRisk={NegRisk}", conditionId, gammaInfo.NegRisk);
        return gammaInfo;
    }

    private static ClobMarketInfo? ParseClobMarketInfo(string conditionId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Tokens array: [{ tokenID, outcome }, ...]
        if (!root.TryGetProperty("tokens", out var tokensEl) || tokensEl.ValueKind != JsonValueKind.Array)
            return null;

        string? yesId = null, noId = null;
        foreach (var t in tokensEl.EnumerateArray())
        {
            var outcome = t.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "";
            var tokenId = t.TryGetProperty("token_id", out var tid) ? tid.GetString() ?? "" : "";
            if (string.Equals(outcome, "Yes", StringComparison.OrdinalIgnoreCase)) yesId = tokenId;
            if (string.Equals(outcome, "No", StringComparison.OrdinalIgnoreCase)) noId = tokenId;
        }
        if (yesId is null || noId is null) return null;

        var negRisk = root.TryGetProperty("neg_risk", out var nr) && nr.GetBoolean();
        var hasMts = root.TryGetProperty("minimum_tick_size", out var mtsEl);
        var hasMos = root.TryGetProperty("minimum_order_size", out var mosEl);
        var mts = hasMts ? ParseDecProp(mtsEl) : 0.01m;
        var mos = hasMos ? ParseDecProp(mosEl) : 0m;
        // Trusted only when BOTH min-size fields parsed to a usable value. Otherwise the caller must
        // apply a conservative floor rather than silently placing a possibly sub-minimum order.
        var trusted = hasMts && mts > 0m && hasMos && mos > 0m;

        return new ClobMarketInfo(conditionId, yesId, noId, negRisk, mts, mos, trusted);
    }

    private static ClobMarketInfo ParseGammaMarketInfo(string conditionId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // gamma-api format: clobTokenIds as a JSON-encoded string "["yesId","noId"]"
        string yesId = "", noId = "";
        if (root.TryGetProperty("clobTokenIds", out var idsEl))
        {
            var raw = idsEl.GetString() ?? "[]";
            using var idsDoc = JsonDocument.Parse(raw);
            if (idsDoc.RootElement.ValueKind == JsonValueKind.Array && idsDoc.RootElement.GetArrayLength() >= 2)
            {
                yesId = idsDoc.RootElement[0].GetString() ?? "";
                noId = idsDoc.RootElement[1].GetString() ?? "";
            }
        }

        var negRisk = root.TryGetProperty("negRisk", out var nr) && nr.GetBoolean();
        // gamma-api doesn't expose mts/mos — mark UNTRUSTED so the caller applies a conservative floor.
        return new ClobMarketInfo(conditionId, yesId, noId, negRisk, Mts: 0.01m, Mos: 0m, MinSizesTrusted: false);
    }

    private static decimal ParseDecProp(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String
            && decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return 0m;
    }
}

/// <summary>
/// Market info returned by the CLOB market-info endpoint.
/// Mts = minimum tick size (e.g. 0.01). Mos = minimum order size in shares.
/// <see cref="MinSizesTrusted"/> is false when the venue did not supply usable min-size fields — the
/// Mts/Mos are then placeholder defaults and the execution layer must apply a conservative floor
/// rather than risk placing a sub-minimum order that the exchange would reject.
/// </summary>
public sealed record ClobMarketInfo(
    string ConditionId,
    string YesTokenId,
    string NoTokenId,
    bool NegRisk,
    decimal Mts,
    decimal Mos,
    bool MinSizesTrusted);
