using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Adapters;

public sealed class PolymarketExecutionOptions
{
    public string ClobBaseUrl { get; set; } = "https://clob.polymarket.com";
    public string GammaBaseUrl { get; set; } = "https://gamma-api.polymarket.com";
    /// <summary>Master live-trading switch. Even with a key configured, execution stays in shadow
    /// unless this is true AND a /golive confirmation has been given.</summary>
    public bool LiveTrading { get; set; } = false;
    /// <summary>CTF Exchange contract on Polygon that orders are signed against.</summary>
    public string ExchangeAddress { get; set; } = "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";
    public int ChainId { get; set; } = 137;
}

/// <summary>
/// Live Polymarket CLOB execution. Signs orders with EIP-712 via <see cref="IKeyVault"/>, authenticates
/// with L2 HMAC API credentials derived from the wallet, and submits to the CLOB REST API.
///
/// IMPORTANT — UNVALIDATED AGAINST LIVE. The signing/scaling/auth here follows the documented Polymarket
/// CLOB spec but has NOT been exercised against the live exchange (that needs a funded wallet). Before
/// trusting it: enable with a single $1 supervised order and confirm the fill, then ramp. The autonomous
/// loop only ever sends BUY orders (open a position); selling/closing live is a follow-up.
/// </summary>
public sealed class PolymarketExecutionProvider : IExecutionProvider
{
    private const string AuthMessage = "This message attests that I control the given wallet";
    private const decimal Usdc = 1_000_000m;   // 6 decimals
    private const decimal Shares = 1_000_000m;  // CTF outcome tokens scaled to 6 decimals on the CLOB

    private readonly HttpClient _http;
    private readonly IKeyVault _vault;
    private readonly PolymarketExecutionOptions _opts;
    private readonly ILogger<PolymarketExecutionProvider> _logger;

    private readonly SemaphoreSlim _credLock = new(1, 1);
    private ApiCreds? _creds;
    private string? _address;

    public string ProviderId => "polymarket-clob";

    public PolymarketExecutionProvider(HttpClient http, IKeyVault vault, IOptions<PolymarketExecutionOptions> opts, ILogger<PolymarketExecutionProvider> logger)
    {
        _http = http;
        _vault = vault;
        _opts = opts.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_opts.ClobBaseUrl.TrimEnd('/') + "/");
    }

    public async Task<OrderReceipt> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        if (!_opts.LiveTrading)
        {
            // Defence in depth: if this provider is ever resolved while LiveTrading is off, do NOT trade.
            _logger.LogWarning("[BLOCKED] PolymarketExecutionProvider invoked with LiveTrading=false — refusing to place a live order.");
            throw new InvalidOperationException("Polymarket live trading is disabled (Polymarket:LiveTrading=false).");
        }

        var address = await EnsureAddressAsync(ct);
        var creds = await EnsureCredsAsync(ct);
        var tokenId = await ResolveTokenIdAsync(request.MarketExternalId, request.Side, ct);

        // Open positions are BUYs of the chosen outcome token. price∈(0,1); pay USDC, receive tokens.
        var price = decimal.Round(request.LimitPrice, 4, MidpointRounding.ToZero);
        var makerAmount = decimal.ToInt64(decimal.Round(price * request.QuantityShares * Usdc, 0, MidpointRounding.ToZero)); // USDC paid
        var takerAmount = decimal.ToInt64(decimal.Round(request.QuantityShares * Shares, 0, MidpointRounding.ToZero));      // tokens received
        var salt = Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);

        var order = new Dictionary<string, object>
        {
            ["salt"] = salt,
            ["maker"] = address,
            ["signer"] = address,
            ["taker"] = "0x0000000000000000000000000000000000000000",
            ["tokenId"] = tokenId,
            ["makerAmount"] = makerAmount.ToString(CultureInfo.InvariantCulture),
            ["takerAmount"] = takerAmount.ToString(CultureInfo.InvariantCulture),
            ["expiration"] = "0",
            ["nonce"] = "0",
            ["feeRateBps"] = "0",
            ["side"] = 0,   // BUY
            ["signatureType"] = 0 // EOA
        };

        var typedData = BuildOrderTypedData(order, address);
        var signature = await _vault.SignTypedDataAsync(typedData, ct);

        var signedOrder = new Dictionary<string, object>(order) { ["signature"] = signature };
        var body = JsonSerializer.Serialize(new { order = signedOrder, owner = creds.ApiKey, orderType = "GTC" });

        using var req = new HttpRequestMessage(HttpMethod.Post, "order") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        AddL2Headers(req, creds, address, "POST", "/order", body);

        using var resp = await _http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Polymarket order failed: {Status} {Body}", resp.StatusCode, respBody);
            throw new HttpRequestException($"Polymarket order {(int)resp.StatusCode}: {respBody}");
        }

        var parsed = JsonSerializer.Deserialize<OrderPostResponse>(respBody);
        var orderId = parsed?.OrderId ?? parsed?.OrderHashes?.FirstOrDefault() ?? $"clob-{salt}";
        _logger.LogInformation("Polymarket order accepted: {OrderId} ({Body})", orderId, respBody);
        var state = new OrderState(orderId, request.Side, request.QuantityShares, 0m, price, OrderStatus.Pending, DateTimeOffset.UtcNow);
        return new OrderReceipt(orderId, state);
    }

    public async Task<OrderState> GetOrderStateAsync(string orderId, CancellationToken ct)
    {
        try
        {
            var address = await EnsureAddressAsync(ct);
            var creds = await EnsureCredsAsync(ct);
            var path = $"/data/order/{orderId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
            AddL2Headers(req, creds, address, "GET", path, "");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new OrderState(orderId, OrderSide.Yes, 0m, 0m, 0m, OrderStatus.Pending, DateTimeOffset.UtcNow);
            var dto = await resp.Content.ReadFromJsonAsync<ClobOrderDto>(cancellationToken: ct);
            var status = (dto?.Status ?? "").ToLowerInvariant() switch
            {
                "matched" or "filled" => OrderStatus.Filled,
                "cancelled" or "canceled" => OrderStatus.Cancelled,
                "live" or "open" => OrderStatus.Pending,
                _ => OrderStatus.Pending
            };
            var size = ParseDec(dto?.OriginalSize);
            var filled = ParseDec(dto?.SizeMatched);
            return new OrderState(orderId, OrderSide.Yes, size, filled, ParseDec(dto?.Price), status, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polymarket GetOrderState failed for {OrderId}", orderId);
            return new OrderState(orderId, OrderSide.Yes, 0m, 0m, 0m, OrderStatus.Pending, DateTimeOffset.UtcNow);
        }
    }

    public async Task CancelOrderAsync(string orderId, CancellationToken ct)
    {
        var address = await EnsureAddressAsync(ct);
        var creds = await EnsureCredsAsync(ct);
        var body = JsonSerializer.Serialize(new { orderID = orderId });
        using var req = new HttpRequestMessage(HttpMethod.Delete, "order") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        AddL2Headers(req, creds, address, "DELETE", "/order", body);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var b = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Polymarket cancel failed: {Status} {Body}", resp.StatusCode, b);
        }
    }

    public Task<IReadOnlyList<PositionState>> GetOpenPositionsAsync(CancellationToken ct)
        // Positions live on the data API and are reconstructable from fills; the platform tracks its own
        // positions in Postgres, so this stays empty until a reconciliation pass is needed.
        => Task.FromResult<IReadOnlyList<PositionState>>(Array.Empty<PositionState>());

    // ── Auth ─────────────────────────────────────────────────────────────────────────────────
    private async Task<string> EnsureAddressAsync(CancellationToken ct)
        => _address ??= await _vault.GetPublicAddressAsync(ct);

    private async Task<ApiCreds> EnsureCredsAsync(CancellationToken ct)
    {
        if (_creds is not null) return _creds;
        await _credLock.WaitAsync(ct);
        try
        {
            if (_creds is not null) return _creds;
            _creds = await DeriveApiKeyAsync(ct);
            return _creds;
        }
        finally { _credLock.Release(); }
    }

    private async Task<ApiCreds> DeriveApiKeyAsync(CancellationToken ct)
    {
        var address = await EnsureAddressAsync(ct);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        const string nonce = "0";
        var l1Sig = await _vault.SignTypedDataAsync(BuildClobAuthTypedData(address, ts, nonce), ct);

        // Try to derive existing creds first; fall back to creating a new key.
        foreach (var (method, path) in new[] { (HttpMethod.Get, "auth/derive-api-key"), (HttpMethod.Post, "auth/api-key") })
        {
            using var req = new HttpRequestMessage(method, path);
            req.Headers.TryAddWithoutValidation("POLY_ADDRESS", address);
            req.Headers.TryAddWithoutValidation("POLY_SIGNATURE", l1Sig);
            req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", ts);
            req.Headers.TryAddWithoutValidation("POLY_NONCE", nonce);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var creds = await resp.Content.ReadFromJsonAsync<ApiCreds>(cancellationToken: ct);
                if (creds is not null && !string.IsNullOrWhiteSpace(creds.ApiKey))
                {
                    _logger.LogInformation("Polymarket CLOB API creds acquired via {Path}", path);
                    return creds;
                }
            }
            else
            {
                _logger.LogDebug("Polymarket {Path} returned {Status}", path, resp.StatusCode);
            }
        }
        throw new InvalidOperationException("Could not derive or create Polymarket CLOB API credentials.");
    }

    private void AddL2Headers(HttpRequestMessage req, ApiCreds creds, string address, string method, string path, string body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var hmac = BuildHmacSignature(creds.Secret, ts, method, path, body);
        req.Headers.TryAddWithoutValidation("POLY_ADDRESS", address);
        req.Headers.TryAddWithoutValidation("POLY_API_KEY", creds.ApiKey);
        req.Headers.TryAddWithoutValidation("POLY_PASSPHRASE", creds.Passphrase);
        req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", ts);
        req.Headers.TryAddWithoutValidation("POLY_SIGNATURE", hmac);
    }

    // L2 HMAC: base64url( HMAC_SHA256( base64url-decode(secret), ts + method + path + body ) ).
    private static string BuildHmacSignature(string secret, string ts, string method, string path, string body)
    {
        var key = Base64UrlDecode(secret);
        using var hmac = new HMACSHA256(key);
        var message = ts + method + path + body;
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Base64UrlEncode(hash);
    }

    private async Task<string> ResolveTokenIdAsync(string marketExternalId, OrderSide side, CancellationToken ct)
    {
        var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(marketExternalId)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("clobTokenIds", out var idsEl)) throw new InvalidOperationException("Market has no clobTokenIds.");
        var raw = idsEl.GetString();
        using var idsDoc = JsonDocument.Parse(raw ?? "[]");
        var arr = idsDoc.RootElement;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2) throw new InvalidOperationException("Unexpected clobTokenIds shape.");
        // Index 0 = YES outcome, index 1 = NO outcome (Polymarket convention).
        return arr[side == OrderSide.Yes ? 0 : 1].GetString() ?? throw new InvalidOperationException("Null clob token id.");
    }

    // ── EIP-712 typed-data builders (serialized to JSON for IKeyVault.SignTypedDataAsync) ───────
    private string BuildOrderTypedData(IReadOnlyDictionary<string, object> order, string address)
    {
        var typed = new
        {
            types = new
            {
                EIP712Domain = new[]
                {
                    new { name = "name", type = "string" },
                    new { name = "version", type = "string" },
                    new { name = "chainId", type = "uint256" },
                    new { name = "verifyingContract", type = "address" }
                },
                Order = new[]
                {
                    new { name = "salt", type = "uint256" },
                    new { name = "maker", type = "address" },
                    new { name = "signer", type = "address" },
                    new { name = "taker", type = "address" },
                    new { name = "tokenId", type = "uint256" },
                    new { name = "makerAmount", type = "uint256" },
                    new { name = "takerAmount", type = "uint256" },
                    new { name = "expiration", type = "uint256" },
                    new { name = "nonce", type = "uint256" },
                    new { name = "feeRateBps", type = "uint256" },
                    new { name = "side", type = "uint8" },
                    new { name = "signatureType", type = "uint8" }
                }
            },
            primaryType = "Order",
            domain = new
            {
                name = "Polymarket CTF Exchange",
                version = "1",
                chainId = _opts.ChainId,
                verifyingContract = _opts.ExchangeAddress
            },
            message = order
        };
        return JsonSerializer.Serialize(typed);
    }

    private string BuildClobAuthTypedData(string address, string timestamp, string nonce)
    {
        var typed = new
        {
            types = new
            {
                EIP712Domain = new[]
                {
                    new { name = "name", type = "string" },
                    new { name = "version", type = "string" },
                    new { name = "chainId", type = "uint256" }
                },
                ClobAuth = new[]
                {
                    new { name = "address", type = "address" },
                    new { name = "timestamp", type = "string" },
                    new { name = "nonce", type = "uint256" },
                    new { name = "message", type = "string" }
                }
            },
            primaryType = "ClobAuth",
            domain = new { name = "ClobAuthDomain", version = "1", chainId = _opts.ChainId },
            message = new { address, timestamp, nonce, message = AuthMessage }
        };
        return JsonSerializer.Serialize(typed);
    }

    private static decimal ParseDec(string? s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }

    private sealed class ApiCreds
    {
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
        [JsonPropertyName("secret")] public string Secret { get; set; } = "";
        [JsonPropertyName("passphrase")] public string Passphrase { get; set; } = "";
    }

    private sealed class OrderPostResponse
    {
        [JsonPropertyName("orderID")] public string? OrderId { get; set; }
        [JsonPropertyName("orderHashes")] public List<string>? OrderHashes { get; set; }
        [JsonPropertyName("success")] public bool? Success { get; set; }
    }

    private sealed class ClobOrderDto
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("original_size")] public string? OriginalSize { get; set; }
        [JsonPropertyName("size_matched")] public string? SizeMatched { get; set; }
        [JsonPropertyName("price")] public string? Price { get; set; }
    }
}
