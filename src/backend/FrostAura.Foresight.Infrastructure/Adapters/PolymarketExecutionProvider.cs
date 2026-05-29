using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Foresight.Domain.Execution;
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
    public int ChainId { get; set; } = 137;
}

/// <summary>
/// Live Polymarket CLOB V2 execution adapter.
///
/// Signs orders with EIP-712 v2 via <see cref="IKeyVault"/>, authenticates with L2 HMAC API
/// credentials derived from the wallet (L1 ClobAuth), and submits to the CLOB REST API.
///
/// Key V2 changes from V1:
///   - domain version = "2"; verifyingContract routed by negRisk flag.
///   - Order struct: 11 fields. REMOVED: taker, expiration, nonce, feeRateBps.
///     ADDED: timestamp(ms), metadata(bytes32), builder(bytes32).
///   - L2 HMAC: `=` padding KEPT (V1 bug fixed — do NOT strip).
///   - Amounts: floor to 6dp (1e6). BUY: makerAmount=floor(price*size*1e6), takerAmount=floor(size*1e6).
///
/// IMPORTANT — UNVALIDATED AGAINST LIVE. The signing/scaling/auth here follows the documented
/// Polymarket CLOB V2 spec but has NOT been exercised against the live exchange (that needs a funded
/// wallet). Before trusting it: enable with a single $1 supervised order and confirm the fill, then
/// ramp. All live placement is guarded by the LiveTrading config gate + ILiveTradingArm.IsArmed check.
/// </summary>
public sealed class PolymarketExecutionProvider : IExecutionProvider
{
    private readonly HttpClient _http;
    private readonly IKeyVault  _vault;
    private readonly PolymarketExecutionOptions _opts;
    private readonly KeyVaultOptions _keyOpts;
    private readonly ILogger<PolymarketExecutionProvider> _logger;

    private readonly SemaphoreSlim _credLock = new(1, 1);
    private ApiCreds? _creds;
    private string?   _address;

    public string ProviderId => "polymarket-clob";

    public PolymarketExecutionProvider(
        HttpClient http,
        IKeyVault vault,
        IOptions<PolymarketExecutionOptions> opts,
        IOptions<KeyVaultOptions> keyOpts,
        ILogger<PolymarketExecutionProvider> logger)
    {
        _http    = http;
        _vault   = vault;
        _opts    = opts.Value;
        _keyOpts = keyOpts.Value;
        _logger  = logger;
        _http.BaseAddress = new Uri(_opts.ClobBaseUrl.TrimEnd('/') + "/");
    }

    // ── IExecutionProvider ───────────────────────────────────────────────────────

    public async Task<OrderReceipt> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        if (!_opts.LiveTrading)
        {
            _logger.LogWarning("[BLOCKED] PolymarketExecutionProvider invoked with LiveTrading=false — refusing to place a live order.");
            throw new InvalidOperationException("Polymarket live trading is disabled (Polymarket:LiveTrading=false).");
        }

        var address = await EnsureAddressAsync(ct);
        var creds   = await EnsureCredsAsync(ct);

        // Resolve market info: tokenId, negRisk flag, min-tick-size, min-order-size.
        var info = await _marketInfo.GetMarketInfoAsync(request.MarketExternalId, ct);
        var tokenId = request.Side == OrderSide.Yes ? info.YesTokenId : info.NoTokenId;

        // Tick-round price and compute V2 integer amounts (floor, 6dp).
        var rawPrice = decimal.Round(request.LimitPrice, 4, MidpointRounding.ToZero);
        var amounts  = OrderMath.SizeBuy(rawPrice, request.QuantityShares, info.Mts, info.Mos);
        if (amounts is null)
        {
            _logger.LogWarning("Order skipped: size {Qty} below min-order-size {Mos} or price rounded to zero on {Market}",
                request.QuantityShares, info.Mos, request.MarketExternalId);
            throw new InvalidOperationException($"Order size {request.QuantityShares} is below min-order-size {info.Mos}.");
        }

        // V2 salt = random positive uint256 truncated to long; timestamp = unix ms (also decimal string).
        var salt      = Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        var tsMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var makerAddr = string.IsNullOrWhiteSpace(_keyOpts.Funder) ? address : _keyOpts.Funder;

        var order = new ClobV2Order
        {
            Salt          = salt,
            Maker         = makerAddr,
            Signer        = address,
            TokenId       = tokenId,
            MakerAmount   = amounts.MakerAmount.ToString(CultureInfo.InvariantCulture),
            TakerAmount   = amounts.TakerAmount.ToString(CultureInfo.InvariantCulture),
            Side          = 0, // BUY = 0 (uint8 in struct; body gets "BUY" string)
            SignatureType = _keyOpts.SignatureType,
            Timestamp     = tsMs
        };

        var typedDataJson = order.ToEip712Json(info.NegRisk);
        var signature     = await _vault.SignTypedDataAsync(typedDataJson, ct);

        // POST /order body: side is "BUY"/"SELL" STRING; no taker/expiration/nonce/feeRateBps.
        var bodyObj = new
        {
            order = new
            {
                salt          = order.Salt,
                maker         = order.Maker,
                signer        = order.Signer,
                tokenId       = order.TokenId,
                makerAmount   = order.MakerAmount,
                takerAmount   = order.TakerAmount,
                side          = "BUY",   // string in body
                signatureType = order.SignatureType,
                timestamp     = order.Timestamp,
                metadata      = order.Metadata,
                builder       = order.Builder,
                signature
            },
            owner     = creds.ApiKey,
            orderType = "GTC"
        };
        var body = JsonSerializer.Serialize(bodyObj);

        using var req = new HttpRequestMessage(HttpMethod.Post, "order")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        AddL2Headers(req, creds, address, "POST", "/order", body);

        using var resp = await _http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Polymarket V2 order failed: {Status} {Body}", resp.StatusCode, respBody);
            throw new HttpRequestException($"Polymarket V2 order {(int)resp.StatusCode}: {respBody}");
        }

        var parsed  = JsonSerializer.Deserialize<OrderPostResponse>(respBody);
        var orderId = parsed?.OrderId ?? parsed?.OrderHashes?.FirstOrDefault() ?? $"clob-{salt}";
        _logger.LogInformation("Polymarket V2 order accepted: {OrderId}", orderId);
        var state = new OrderState(orderId, request.Side, request.QuantityShares, 0m, amounts.TickPrice, OrderStatus.Pending, DateTimeOffset.UtcNow);
        return new OrderReceipt(orderId, state);
    }

    public async Task<OrderState> GetOrderStateAsync(string orderId, CancellationToken ct)
    {
        try
        {
            var address = await EnsureAddressAsync(ct);
            var creds   = await EnsureCredsAsync(ct);
            var path    = $"/data/order/{orderId}";
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
            var size   = ParseDec(dto?.OriginalSize);
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
        var creds   = await EnsureCredsAsync(ct);
        var body    = JsonSerializer.Serialize(new { orderID = orderId });
        using var req = new HttpRequestMessage(HttpMethod.Delete, "order")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        AddL2Headers(req, creds, address, "DELETE", "/order", body);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var b = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Polymarket cancel failed: {Status} {Body}", resp.StatusCode, b);
        }
    }

    public Task<IReadOnlyList<PositionState>> GetOpenPositionsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PositionState>>(Array.Empty<PositionState>());

    // ── L1 auth — derive CLOB API credentials ────────────────────────────────────

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
        var ts      = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        const string nonce = "0";
        var l1Sig = await _vault.SignTypedDataAsync(ClobV2Order.BuildClobAuthTypedData(address, ts), ct);

        foreach (var (method, path) in new[] { (HttpMethod.Get, "auth/derive-api-key"), (HttpMethod.Post, "auth/api-key") })
        {
            using var req = new HttpRequestMessage(method, path);
            req.Headers.TryAddWithoutValidation("POLY_ADDRESS",   address);
            req.Headers.TryAddWithoutValidation("POLY_SIGNATURE", l1Sig);
            req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", ts);
            req.Headers.TryAddWithoutValidation("POLY_NONCE",     nonce);
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

    // ── L2 auth — HMAC headers ────────────────────────────────────────────────────

    private void AddL2Headers(HttpRequestMessage req, ApiCreds creds, string address, string method, string path, string body)
    {
        var ts   = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var hmac = BuildHmacSignature(creds.Secret, ts, method, path, body);
        req.Headers.TryAddWithoutValidation("POLY_ADDRESS",    address);
        req.Headers.TryAddWithoutValidation("POLY_API_KEY",    creds.ApiKey);
        req.Headers.TryAddWithoutValidation("POLY_PASSPHRASE", creds.Passphrase);
        req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP",  ts);
        req.Headers.TryAddWithoutValidation("POLY_SIGNATURE",  hmac);
    }

    /// <summary>
    /// L2 HMAC: base64url( HMAC_SHA256( base64-decode(secret), ts+method+path+body ) ).
    /// V2 FIX: `=` padding is KEPT — the V1 code wrongly called TrimEnd('='); that is removed here.
    /// base64url = standard base64 with '+' → '-' and '/' → '_' only (padding preserved).
    /// </summary>
    internal static string BuildHmacSignature(string secret, string ts, string method, string path, string body)
    {
        var key     = Base64UrlDecodeSecret(secret);
        using var hmac = new HMACSHA256(key);
        var message = ts + method + path + body;
        var hash    = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        // KEEP '=' padding — do NOT strip.
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecodeSecret(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }

    private static decimal ParseDec(string? s)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    // ── Market info cache (lazy-initialised) ─────────────────────────────────────

    private PolymarketClobMarketInfoClient? _marketInfoBacking;
    private PolymarketClobMarketInfoClient _marketInfo => _marketInfoBacking ??=
        new PolymarketClobMarketInfoClient(_http, _opts, _logger);

    // ── Private DTO types ────────────────────────────────────────────────────────

    private sealed class ApiCreds
    {
        [JsonPropertyName("apiKey")]     public string ApiKey     { get; set; } = "";
        [JsonPropertyName("secret")]     public string Secret     { get; set; } = "";
        [JsonPropertyName("passphrase")] public string Passphrase { get; set; } = "";
    }

    private sealed class OrderPostResponse
    {
        [JsonPropertyName("orderID")]     public string?       OrderId     { get; set; }
        [JsonPropertyName("orderHashes")] public List<string>? OrderHashes { get; set; }
        [JsonPropertyName("success")]     public bool?         Success     { get; set; }
    }

    private sealed class ClobOrderDto
    {
        [JsonPropertyName("status")]        public string? Status       { get; set; }
        [JsonPropertyName("original_size")] public string? OriginalSize { get; set; }
        [JsonPropertyName("size_matched")]  public string? SizeMatched  { get; set; }
        [JsonPropertyName("price")]         public string? Price        { get; set; }
    }
}
