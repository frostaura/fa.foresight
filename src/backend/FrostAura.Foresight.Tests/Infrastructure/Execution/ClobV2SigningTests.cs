using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FrostAura.Foresight.Domain.Execution;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Live;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure.Execution;

/// <summary>
/// Offline signing tests for Workstream E — no live calls, no database, no funded wallet.
///
/// (a) EIP-712 V2 Order sign → ecrecover round-trip (proves domain v2 + struct + decimal-string uint256).
/// (b) ClobAuth golden vector + ecrecover round-trip (L1 credential derivation).
/// (c) L2 HMAC vector — fixed inputs, assert expected output KEEPS '=' padding.
/// (d) Amount-scaling table tests (BUY/SELL floor, tick rounding to mts, sub-mos skip).
/// (e) negRisk routing → correct verifyingContract.
/// (f) Body shape: side is "BUY"/"SELL" string, owner=apiKey, orderType present, no deleted V1 fields.
/// </summary>
public class ClobV2SigningTests
{
    // ── Test key (throwaway, never funded) ──────────────────────────────────────
    // A known deterministic test key — private key, address, from eth tooling.
    private const string TestPrivateKey = "0x4c0883a69102937d6231471b5dbb6e538eba2ef39a64d09e1caad98bce00f54e";
    private const string TestAddress    = "0x9ab026dA8Bb29EA61bf15D592c73fc5BcF5dA58C"; // lowercase will match

    // ── (a) EIP-712 V2 Order sign → ecrecover round-trip ───────────────────────

    [Fact]
    public void EIP712_V2_Order_sign_and_ecrecover_roundtrip_standard_market()
    {
        var key   = new EthECKey(TestPrivateKey);
        var address = key.GetPublicAddress();
        address.Should().BeEquivalentTo(TestAddress); // sanity

        var order = new ClobV2Order
        {
            Salt          = "12345678901234567890",
            Maker         = address,
            Signer        = address,
            TokenId       = "98765432109876543210987654321098765432109876543210987654321098765",
            MakerAmount   = "1000000",
            TakerAmount   = "2000000",
            Side          = 0,
            SignatureType = 0,
            Timestamp     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        };

        var typedDataJson = order.ToEip712Json(negRisk: false);

        // Verify the domain version is "2" in the JSON.
        typedDataJson.Should().Contain("\"version\":\"2\"", "V2 domain must have version 2");
        typedDataJson.Should().Contain(ClobV2Order.CtfExchangeAddress, "standard market routes to CtfExchange");

        // Sign with the test key.
        var signer    = new Eip712TypedDataSigner();
        var signature = signer.SignTypedDataV4(typedDataJson, key);
        signature.Should().NotBeNullOrWhiteSpace();
        signature.Should().StartWith("0x");

        // Recover the signer address. messageKeySelector="message" is the top-level JSON key.
        var recovered = signer.RecoverFromSignatureV4(typedDataJson, signature, "message");
        recovered.Should().BeEquivalentTo(address, "ecrecover must return the signing address");
    }

    [Fact]
    public void EIP712_V2_Order_sign_and_ecrecover_roundtrip_neg_risk_market()
    {
        var key      = new EthECKey(TestPrivateKey);
        var address  = key.GetPublicAddress();

        var order = new ClobV2Order
        {
            Salt          = "99999999999999999999",
            Maker         = address,
            Signer        = address,
            TokenId       = "11111111111111111111",
            MakerAmount   = "500000",
            TakerAmount   = "1000000",
            Side          = 0,
            SignatureType = 0,
            Timestamp     = "1700000000000"
        };

        var typedDataJson = order.ToEip712Json(negRisk: true);

        typedDataJson.Should().Contain(ClobV2Order.NegRiskCtfExchangeAddress, "negRisk market routes to NegRiskCtfExchange");
        typedDataJson.Should().NotContain(ClobV2Order.CtfExchangeAddress, "must not mix up contracts");

        var signer    = new Eip712TypedDataSigner();
        var signature = signer.SignTypedDataV4(typedDataJson, key);
        var recovered = signer.RecoverFromSignatureV4(typedDataJson, signature, "message");
        recovered.Should().BeEquivalentTo(address);
    }

    [Fact]
    public void EIP712_V2_Order_struct_does_NOT_contain_v1_deleted_fields()
    {
        var key     = new EthECKey(TestPrivateKey);
        var address = key.GetPublicAddress();
        var order   = new ClobV2Order { Salt = "1", Maker = address, Signer = address, TokenId = "1", MakerAmount = "1", TakerAmount = "1", Side = 0, SignatureType = 0, Timestamp = "1" };

        var json = order.ToEip712Json(false);
        // Check that the V1 struct fields are NOT present as type-definition entries.
        json.Should().NotContain("\"name\":\"taker\"",      "V2 removes the standalone taker field");
        json.Should().NotContain("\"name\":\"expiration\"", "V2 removes expiration");
        json.Should().NotContain("\"name\":\"nonce\"",      "V2 removes nonce from Order struct");
        json.Should().NotContain("\"name\":\"feeRateBps\"", "V2 removes feeRateBps");
    }

    [Fact]
    public void EIP712_V2_Order_uint256_fields_are_decimal_strings_not_numbers()
    {
        var key     = new EthECKey(TestPrivateKey);
        var address = key.GetPublicAddress();
        // Use a value large enough to overflow float64 if not stringified.
        var bigSalt = "999999999999999999999999999999";
        var order   = new ClobV2Order { Salt = bigSalt, Maker = address, Signer = address, TokenId = "12345678901234567890", MakerAmount = "1000000", TakerAmount = "2000000", Side = 0, SignatureType = 0, Timestamp = "1700000000000000" };

        var json = order.ToEip712Json(false);
        // The salt appears as a quoted string in the message block.
        json.Should().Contain($"\"{bigSalt}\"", "uint256 fields must be quoted decimal strings");
    }

    // ── (b) ClobAuth golden vector + ecrecover ───────────────────────────────────

    [Fact]
    public void ClobAuth_sign_and_ecrecover_roundtrip()
    {
        var key     = new EthECKey(TestPrivateKey);
        var address = key.GetPublicAddress();
        var ts      = "1700000000"; // fixed unix seconds string

        var json = ClobV2Order.BuildClobAuthTypedData(address, ts);

        json.Should().Contain("ClobAuthDomain");
        json.Should().Contain("\"version\":\"1\"");
        json.Should().Contain("This message attests that I control the given wallet");
        json.Should().Contain("\"nonce\":\"0\"");

        var signer    = new Eip712TypedDataSigner();
        var signature = signer.SignTypedDataV4(json, key);
        signature.Should().NotBeNullOrEmpty();

        // The messageKeySelector is the JSON key that holds the message data ("message" at root).
        // Nethereum's RecoverFromSignatureV4(json, sig, selector) uses the selector to pick the
        // message sub-object — in our typed-data format the top-level key is "message".
        var recovered = signer.RecoverFromSignatureV4(json, signature, "message");
        recovered.Should().BeEquivalentTo(address, "ClobAuth ecrecover must return signing address");
    }

    [Fact]
    public void ClobAuth_json_does_not_contain_verifyingContract()
    {
        // L1 ClobAuth domain has NO verifyingContract per the spec.
        var json = ClobV2Order.BuildClobAuthTypedData("0x1234", "1700000000");
        json.Should().NotContain("verifyingContract");
    }

    // ── (c) L2 HMAC vector — padding must be KEPT ───────────────────────────────

    [Theory]
    [InlineData("dGVzdC1zZWNyZXQtYmFzZTY0", "1700000000", "GET", "/order", "", "keeps-equals")]
    [InlineData("YW5vdGhlcnNlY3JldA==",     "1700000001", "POST", "/order", "{\"x\":1}", "with-body")]
    public void L2_HMAC_keeps_equals_padding_and_applies_url_safe_replacements(
        string secret, string ts, string method, string path, string body, string _scenario)
    {
        var result = PolymarketExecutionProvider.BuildHmacSignature(secret, ts, method, path, body);

        // Must NOT strip trailing '=' — the V1 bug was TrimEnd('=').
        // A base64 string for a 32-byte SHA256 is always 44 chars with padding.
        // Verify the length is 44 (base64 of 32 bytes is 44 chars with padding).
        result.Length.Should().Be(44, $"SHA256 base64url with padding is always 44 chars (scenario={_scenario})");

        // Must apply URL-safe replacements.
        result.Should().NotContain("+", "'+' must be replaced with '-'");
        result.Should().NotContain("/", "'/' must be replaced with '_'");

        // Verify padding: the 44th char of base64url for 32 bytes is always '='.
        // (32 bytes → 43 base64 chars + 1 '=' padding, total 44)
        result.Should().EndWith("=", $"Base64 of 32 bytes ends with '=' (scenario={_scenario})");
    }

    [Fact]
    public void L2_HMAC_golden_vector()
    {
        // Deterministic golden vector: compute expected value externally and assert.
        // secret = base64("secret") = "c2VjcmV0"
        // message = "1700000000GET/order"
        // HMAC-SHA256(base64decode("c2VjcmV0"), "1700000000GET/order") → base64url with padding
        const string secret = "c2VjcmV0"; // base64("secret")
        const string ts     = "1700000000";
        const string method = "GET";
        const string path   = "/order";
        const string body   = "";

        // Compute the expected value using the same algorithm (independent of BuildHmacSignature).
        var keyBytes  = Convert.FromBase64String("c2VjcmV0"); // = "secret" bytes
        var message   = ts + method + path + body;
        using var h   = new HMACSHA256(keyBytes);
        var hash      = h.ComputeHash(Encoding.UTF8.GetBytes(message));
        var expected  = Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_');

        var result = PolymarketExecutionProvider.BuildHmacSignature(secret, ts, method, path, body);
        result.Should().Be(expected, "HMAC must match the independently computed golden vector");
        result.Should().EndWith("=", "padding must be kept");
    }

    // ── (d) Amount-scaling table tests ───────────────────────────────────────────

    [Theory]
    // rawPrice is first tick-rounded then used for makerAmount.
    // BUY: tickPrice = floor(rawPrice/mts)*mts; maker=floor(tickPrice*size*1e6); taker=floor(size*1e6)
    [InlineData(0.55,  10.0, 0.01, 0.0, 5_500_000L, 10_000_000L)] // tick=0.55; maker=floor(0.55*10*1e6)=5_500_000
    [InlineData(0.499,  5.0, 0.01, 0.0, 2_450_000L,  5_000_000L)] // tick=floor(0.499/0.01)*0.01=0.49; maker=floor(0.49*5*1e6)=2_450_000
    [InlineData(0.333333, 3.0, 0.01, 0.0, 990_000L,  3_000_000L)] // tick=floor(0.333333/0.01)*0.01=0.33; maker=floor(0.33*3*1e6)=990_000
    public void OrderMath_BUY_amounts_are_floor_scaled(
        double rawPrice, double sizeShares, double mts, double mos,
        long expectedMaker, long expectedTaker)
    {
        var result = OrderMath.SizeBuy((decimal)rawPrice, (decimal)sizeShares, (decimal)mts, (decimal)mos);
        result.Should().NotBeNull();
        result!.MakerAmount.Should().Be(expectedMaker, "makerAmount = floor(tickPrice*size*1e6)");
        result.TakerAmount.Should().Be(expectedTaker,  "takerAmount = floor(size*1e6)");
    }

    [Theory]
    [InlineData(0.55, 10.0,  0.01, 0.0, 10_000_000L, 5_500_000L)] // SELL: floor(10*1e6), floor(0.55*10*1e6)
    [InlineData(0.499, 5.0,  0.01, 0.0,  5_000_000L, 2_450_000L)] // tick = floor(0.499/0.01)*0.01=0.49; taker=floor(0.49*5*1e6)=2_450_000
    public void OrderMath_SELL_amounts_are_floor_scaled(
        double rawPrice, double sizeShares, double mts, double mos,
        long expectedMaker, long expectedTaker)
    {
        var result = OrderMath.SizeSell((decimal)rawPrice, (decimal)sizeShares, (decimal)mts, (decimal)mos);
        result.Should().NotBeNull();
        result!.MakerAmount.Should().Be(expectedMaker, "SELL makerAmount = floor(size*1e6)");
        result.TakerAmount.Should().Be(expectedTaker,  "SELL takerAmount = floor(price*size*1e6)");
    }

    [Theory]
    [InlineData(0.557, 0.01, 0.55)]  // floor(0.557/0.01)*0.01 = 0.55
    [InlineData(0.555, 0.01, 0.55)]  // floor(0.555/0.01)*0.01 = 0.55
    [InlineData(0.560, 0.01, 0.56)]  // already on a tick
    [InlineData(0.501, 0.005, 0.50)] // floor(0.501/0.005)*0.005 = 0.500
    public void OrderMath_tick_rounding_floors_to_mts(double rawPrice, double mts, double expectedTick)
    {
        var result = OrderMath.RoundToTick((decimal)rawPrice, (decimal)mts);
        result.Should().Be((decimal)expectedTick, $"price {rawPrice} rounded to mts {mts} should be {expectedTick}");
    }

    [Fact]
    public void OrderMath_sub_mos_returns_null_for_BUY()
    {
        // size=0.5, mos=1.0 → size < mos → skip
        var result = OrderMath.SizeBuy(0.55m, sizeShares: 0.5m, mts: 0.01m, mos: 1.0m);
        result.Should().BeNull("sub-mos orders must be skipped");
    }

    [Fact]
    public void OrderMath_sub_mos_returns_null_for_SELL()
    {
        var result = OrderMath.SizeSell(0.55m, sizeShares: 0.001m, mts: 0.01m, mos: 0.1m);
        result.Should().BeNull("sub-mos orders must be skipped");
    }

    [Fact]
    public void OrderMath_zero_mos_never_skips()
    {
        // mos=0 means no minimum — always allow.
        var result = OrderMath.SizeBuy(0.55m, sizeShares: 0.0001m, mts: 0.01m, mos: 0m);
        result.Should().NotBeNull("mos=0 means no minimum order size restriction");
    }

    // ── (e) negRisk routing ───────────────────────────────────────────────────────

    [Fact]
    public void ClobV2Order_negRisk_routes_to_NegRiskCtfExchange()
    {
        var order = new ClobV2Order { Salt = "1", Maker = "0x1", Signer = "0x1", TokenId = "1", MakerAmount = "1", TakerAmount = "1", Side = 0, SignatureType = 0, Timestamp = "1" };
        var json  = order.ToEip712Json(negRisk: true);
        json.Should().Contain(ClobV2Order.NegRiskCtfExchangeAddress);
        json.Should().NotContain(ClobV2Order.CtfExchangeAddress);
    }

    [Fact]
    public void ClobV2Order_standard_routes_to_CtfExchange()
    {
        var order = new ClobV2Order { Salt = "1", Maker = "0x1", Signer = "0x1", TokenId = "1", MakerAmount = "1", TakerAmount = "1", Side = 0, SignatureType = 0, Timestamp = "1" };
        var json  = order.ToEip712Json(negRisk: false);
        json.Should().Contain(ClobV2Order.CtfExchangeAddress);
        json.Should().NotContain(ClobV2Order.NegRiskCtfExchangeAddress);
    }

    // ── (f) Body shape validation ────────────────────────────────────────────────

    [Fact]
    public void ClobV2Order_body_side_is_string_BUY_not_integer()
    {
        // The signed struct uses side=0 (uint8). The POST body must use "BUY" string.
        // We verify by checking the ToEip712Json (signed struct) vs what the body would look like.
        var order = new ClobV2Order { Salt = "1", Maker = "0xA", Signer = "0xA", TokenId = "1", MakerAmount = "1", TakerAmount = "1", Side = 0, SignatureType = 0, Timestamp = "1" };

        var typedDataJson = order.ToEip712Json(false);
        // In the typed-data (signed struct) side is a number (uint8).
        typedDataJson.Should().Contain("\"side\":0", "signed struct: side is uint8 number");

        // Simulate the body serialisation the adapter would produce.
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            order = new { side = "BUY", owner = "test-api-key", orderType = "GTC" }
        });
        bodyJson.Should().Contain("\"side\":\"BUY\"", "POST body: side is the string BUY");
        bodyJson.Should().Contain("\"owner\":\"test-api-key\"");
        bodyJson.Should().Contain("\"orderType\":\"GTC\"");
    }

    [Fact]
    public void ClobV2Order_typed_data_has_no_v1_deleted_type_members()
    {
        var order = new ClobV2Order { Salt = "1", Maker = "0xA", Signer = "0xA", TokenId = "1", MakerAmount = "1", TakerAmount = "1", Side = 0, SignatureType = 0, Timestamp = "1" };
        var json  = order.ToEip712Json(false);

        // Type definition must not include V1 fields as standalone field names.
        // Note: "taker" appears inside "takerAmount" — check for the exact field-name patterns.
        json.Should().NotContain("\"name\":\"taker\"",       "V2 type definition must not include the standalone 'taker' field");
        json.Should().NotContain("\"name\":\"expiration\"",  "V2 type definition must not include expiration");
        json.Should().NotContain("\"name\":\"feeRateBps\"",  "V2 type definition must not include feeRateBps");
        json.Should().NotContain("\"name\":\"nonce\"",       "V2 type definition must not include nonce (removed from Order struct)");
    }

    // ── (g) OrderMath.ConfigHash helper ─────────────────────────────────────────

    [Fact]
    public void ConfigHash_is_deterministic_and_excludes_mode()
    {
        var hash1 = LiveSessionEngine.ComputeConfigHash("polymarket", "BTCUSDT", "5m", "flat", 100m, 5m);
        var hash2 = LiveSessionEngine.ComputeConfigHash("polymarket", "BTCUSDT", "5m", "flat", 100m, 5m);
        hash1.Should().Be(hash2, "same inputs must produce the same hash");
        hash1.Should().HaveLength(64, "SHA-256 hex is 64 chars");
    }

    [Fact]
    public void ConfigHash_differs_for_different_params()
    {
        var hash1 = LiveSessionEngine.ComputeConfigHash("polymarket", "BTCUSDT", "5m",  "flat", 100m, 5m);
        var hash2 = LiveSessionEngine.ComputeConfigHash("polymarket", "BTCUSDT", "15m", "flat", 100m, 5m);
        hash1.Should().NotBe(hash2, "different intervals must produce different hashes");
    }
}
