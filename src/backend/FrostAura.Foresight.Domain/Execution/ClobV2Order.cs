using System.Globalization;
using System.Text.Json;

namespace FrostAura.Foresight.Domain.Execution;

/// <summary>
/// Polymarket CLOB V2 signed order struct (11 fields, exact ordering from the spec).
/// Produces the EIP-712 typed-data JSON used for signing. Pure record — no I/O.
///
/// CLOB V2 verifying contract routing (from mvp-plan §6):
///   negRisk market  → NegRiskCtfExchange  0xe2222d279d744050d28e00520010520000310F59
///   standard market → CtfExchange         0xE111180000d2663C0091e4f400237545B87B996B
///
/// V2 EIP-712 domain: name="Polymarket CTF Exchange", version="2", chainId=137.
///
/// REMOVED vs V1: taker, expiration, nonce, feeRateBps.
/// ADDED: timestamp(ms), metadata(bytes32), builder(bytes32).
/// </summary>
public sealed record ClobV2Order
{
    // ── Routing constants ────────────────────────────────────────────────────────
    public const string DomainName    = "Polymarket CTF Exchange";
    public const string DomainVersion = "2";
    public const int    ChainId       = 137;

    // NegRisk variant (neg-risk flag set on market info).
    public const string NegRiskCtfExchangeAddress = "0xe2222d279d744050d28e00520010520000310F59";
    // Standard CTF exchange (default).
    public const string CtfExchangeAddress         = "0xE111180000d2663C0091e4f400237545B87B996B";

    private static readonly string ZeroBytes32 = "0x" + new string('0', 64);

    // ── Order fields (exact V2 spec order) ───────────────────────────────────────
    /// <summary>Random uniqueness salt — uint256, emitted as decimal string in EIP-712.</summary>
    public required string Salt          { get; init; }
    /// <summary>Funder address (defaults to signer EOA when no POLY_GNOSIS_SAFE/POLY_PROXY).</summary>
    public required string Maker         { get; init; }
    /// <summary>Signing EOA address.</summary>
    public required string Signer        { get; init; }
    /// <summary>Polymarket CTF token id for the chosen outcome — uint256, decimal string.</summary>
    public required string TokenId       { get; init; }
    /// <summary>Collateral in (BUY) or tokens in (SELL) — uint256, decimal string, 6dp scaled.</summary>
    public required string MakerAmount   { get; init; }
    /// <summary>Tokens out (BUY) or collateral out (SELL) — uint256, decimal string, 6dp scaled.</summary>
    public required string TakerAmount   { get; init; }
    /// <summary>0=BUY, 1=SELL — uint8, number in JSON.</summary>
    public required int    Side          { get; init; }
    /// <summary>0=EOA, 1=POLY_PROXY, 2=POLY_GNOSIS_SAFE — uint8, number in JSON.</summary>
    public required int    SignatureType { get; init; }
    /// <summary>Placement timestamp in milliseconds — uint256, decimal string.</summary>
    public required string Timestamp     { get; init; }
    /// <summary>Builder metadata — bytes32, 0x-hex string. Zero unless a builder code is used.</summary>
    public string Metadata { get; init; } = ZeroBytes32;
    /// <summary>Builder address — bytes32, 0x-hex string. Zero unless a builder code is used.</summary>
    public string Builder  { get; init; } = ZeroBytes32;

    // ── EIP-712 typed-data JSON builder ─────────────────────────────────────────

    /// <summary>
    /// Produce the EIP-712 typed-data JSON payload for signing.
    /// Routes the verifyingContract based on <paramref name="negRisk"/>.
    /// uint256 fields are emitted as decimal strings (JSON numbers would corrupt the hash via float64
    /// truncation). bytes32 fields are emitted as 0x-prefixed 64-char hex strings.
    /// Side and SignatureType are emitted as numbers (uint8).
    /// </summary>
    public string ToEip712Json(bool negRisk)
    {
        var verifyingContract = negRisk ? NegRiskCtfExchangeAddress : CtfExchangeAddress;

        var typed = new
        {
            types = new
            {
                EIP712Domain = new[]
                {
                    new { name = "name",              type = "string"  },
                    new { name = "version",           type = "string"  },
                    new { name = "chainId",           type = "uint256" },
                    new { name = "verifyingContract", type = "address" }
                },
                Order = new[]
                {
                    new { name = "salt",          type = "uint256" },
                    new { name = "maker",         type = "address" },
                    new { name = "signer",        type = "address" },
                    new { name = "tokenId",       type = "uint256" },
                    new { name = "makerAmount",   type = "uint256" },
                    new { name = "takerAmount",   type = "uint256" },
                    new { name = "side",          type = "uint8"   },
                    new { name = "signatureType", type = "uint8"   },
                    new { name = "timestamp",     type = "uint256" },
                    new { name = "metadata",      type = "bytes32" },
                    new { name = "builder",       type = "bytes32" }
                }
            },
            primaryType = "Order",
            domain = new
            {
                name              = DomainName,
                version           = DomainVersion,
                chainId           = (object)ChainId.ToString(CultureInfo.InvariantCulture),
                verifyingContract
            },
            message = new Dictionary<string, object>
            {
                // uint256 fields → decimal strings
                ["salt"]          = Salt,
                ["maker"]         = Maker,
                ["signer"]        = Signer,
                ["tokenId"]       = TokenId,
                ["makerAmount"]   = MakerAmount,
                ["takerAmount"]   = TakerAmount,
                // uint8 fields → numbers
                ["side"]          = (object)Side,
                ["signatureType"] = (object)SignatureType,
                // uint256 as decimal string
                ["timestamp"]     = Timestamp,
                // bytes32 as 0x-hex string
                ["metadata"]      = Metadata,
                ["builder"]       = Builder
            }
        };

        return JsonSerializer.Serialize(typed);
    }

    /// <summary>
    /// Build the ClobAuth L1 credential-derivation typed-data JSON.
    /// Domain: name="ClobAuthDomain", version="1", chainId=137 (no verifyingContract).
    /// Type: ClobAuth { address, timestamp(string), nonce(uint256), message(string) }.
    /// nonce is always "0"; timestamp is unix SECONDS as a string; message is the attestation text.
    /// </summary>
    public static string BuildClobAuthTypedData(string walletAddress, string timestampSeconds)
    {
        const string authMessage = "This message attests that I control the given wallet";
        var typed = new
        {
            types = new
            {
                EIP712Domain = new[]
                {
                    new { name = "name",    type = "string"  },
                    new { name = "version", type = "string"  },
                    new { name = "chainId", type = "uint256" }
                },
                ClobAuth = new[]
                {
                    new { name = "address",   type = "address" },
                    new { name = "timestamp", type = "string"  },
                    new { name = "nonce",     type = "uint256" },
                    new { name = "message",   type = "string"  }
                }
            },
            primaryType = "ClobAuth",
            domain = new { name = "ClobAuthDomain", version = "1", chainId = ChainId.ToString(CultureInfo.InvariantCulture) },
            message = new { address = walletAddress, timestamp = timestampSeconds, nonce = "0", message = authMessage }
        };
        return JsonSerializer.Serialize(typed);
    }
}
