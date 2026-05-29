using System.Numerics;
using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Domain.Ledger;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Ledger;

/// <summary>Options for the Polygon RPC used to read the on-chain pUSD balance.</summary>
public sealed class PolygonOptions
{
    /// <summary>Polygon RPC endpoint. Defaults to a public node; can be overridden with Alchemy/Infura.</summary>
    public string RpcUrl { get; set; } = "https://polygon-rpc.com";

    /// <summary>ERC-20 contract address for pUSD on Polygon Mainnet (chain 137).</summary>
    public string PusdContractAddress { get; set; } = "0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB";

    /// <summary>Decimal places for pUSD (6, like USDC).</summary>
    public int PusdDecimals { get; set; } = 6;
}

/// <summary>
/// Postgres-backed reservation ledger.
///
/// Invariant: free = walletPUSD − Σ(live_sessions.current_balance WHERE stopped_at IS NULL AND mode='live').
/// The invariant is ALWAYS derived by a live query — never from summing ledger delta rows (which would
/// accumulate rounding / double-count errors). Audit rows exist only for observability.
///
/// Wallet pUSD is fetched from the Polygon chain via a minimal eth_call (balanceOf) to the pUSD ERC-20
/// contract. Returns 0 gracefully when no wallet is configured or the RPC is unreachable.
/// </summary>
public sealed class AccountLedger : IAccountLedger
{
    private const string Venue = "polymarket";

    private readonly ForesightDbContext  _db;
    private readonly IChannelAdapter     _channel;
    private readonly IKeyVault           _vault;
    private readonly HttpClient          _http;
    private readonly PolygonOptions      _polygonOpts;
    private readonly KeyVaultOptions     _keyVaultOpts;
    private readonly ILogger<AccountLedger> _logger;

    public AccountLedger(
        ForesightDbContext db,
        IChannelAdapter channel,
        IKeyVault vault,
        HttpClient http,
        IOptions<PolygonOptions> polygonOpts,
        IOptions<KeyVaultOptions> keyVaultOpts,
        ILogger<AccountLedger> logger)
    {
        _db           = db;
        _channel      = channel;
        _vault        = vault;
        _http         = http;
        _polygonOpts  = polygonOpts.Value;
        _keyVaultOpts = keyVaultOpts.Value;
        _logger       = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the real on-chain pUSD ERC-20 balance from Polygon via a JSON-RPC eth_call to
    /// balanceOf(walletAddress) on the pUSD contract. Returns 0 gracefully when:
    ///   - no wallet private key is configured (KeyVault:PrivateKey is empty)
    ///   - no wallet address can be resolved
    ///   - the RPC endpoint is unreachable or returns an error
    ///   - the live trading gate is disarmed (balance stays at 0 so reservation logic is inert)
    /// </remarks>
    public async Task<decimal> GetWalletPusdAsync(Guid tenantId, CancellationToken ct)
    {
        // Only meaningful with a wallet configured (live path only).
        if (!_keyVaultOpts.HasKey) return 0m;

        try
        {
            var address = await _vault.GetPublicAddressAsync(ct);
            return await ReadPusdBalanceAsync(address, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetWalletPusdAsync: on-chain read failed — returning 0 (pUSD balance unconfirmed)");
            return 0m;
        }
    }

    /// <summary>
    /// Read pUSD ERC-20 balance from Polygon via eth_call to balanceOf(address).
    /// Uses minimal JSON-RPC (no Nethereum ABI overhead) to keep the dependency surface light.
    /// ABI-encodes the balanceOf(address) call manually: 4-byte selector + 32-byte padded address.
    /// </summary>
    internal async Task<decimal> ReadPusdBalanceAsync(string walletAddress, CancellationToken ct)
    {
        // balanceOf(address) selector: keccak256("balanceOf(address)")[0:4] = 0x70a08231
        const string selector = "70a08231";
        // ABI-encode: 32 bytes, left-padded with zeros, last 20 bytes = address (strip 0x prefix).
        var cleanAddr = walletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? walletAddress[2..] : walletAddress;
        var callData = "0x" + selector + cleanAddr.PadLeft(64, '0');

        var rpcBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method  = "eth_call",
            @params = new object[]
            {
                new { to = _polygonOpts.PusdContractAddress, data = callData },
                "latest"
            },
            id = 1
        });

        using var req  = new HttpRequestMessage(HttpMethod.Post, _polygonOpts.RpcUrl)
            { Content = new StringContent(rpcBody, Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Polygon RPC returned {Status} for balanceOf — returning 0", resp.StatusCode);
            return 0m;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("result", out var resultEl))
        {
            _logger.LogWarning("Polygon RPC response missing 'result' field — returning 0. Body: {Body}", body[..Math.Min(body.Length, 200)]);
            return 0m;
        }

        var hex = resultEl.GetString() ?? "0x0";
        if (hex == "0x" || hex == "0x0") return 0m;

        // Parse the 32-byte ABI-encoded uint256 result.
        var cleanHex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (!BigInteger.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out var raw))
        {
            _logger.LogWarning("Could not parse Polygon RPC result '{Hex}' as uint256 — returning 0", hex);
            return 0m;
        }

        // Scale from integer (6 decimals) to decimal pUSD.
        var divisor = BigInteger.Pow(10, _polygonOpts.PusdDecimals);
        var whole   = (decimal)(raw / divisor);
        var frac    = (decimal)(raw % divisor) / (decimal)divisor;
        var balance = whole + frac;

        _logger.LogDebug("On-chain pUSD balance for {Address}: {Balance} (raw={Raw})", walletAddress, balance, raw);
        return balance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// free = walletPUSD − Σ(live_session.current_balance WHERE stopped_at IS NULL AND mode='live').
    /// Derived by a single query — NOT by reading ledger deltas.
    /// </remarks>
    public async Task<decimal> GetFreeAsync(Guid tenantId, CancellationToken ct)
    {
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var reserved   = await ActiveLiveReservedAsync(tenantId, ct);
        return walletPusd - reserved;
    }

    /// <inheritdoc/>
    public async Task ReserveAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct)
    {
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var free       = await GetFreeAsync(tenantId, ct);

        if (amount > free)
            throw new InsufficientPusdException(amount, free);

        var freeAfter = free - amount;
        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            Venue      = Venue,
            EntryKind  = "reserve",
            SessionId  = sessionId,
            Amount     = amount,
            WalletPusd = walletPusd,
            FreeAfter  = freeAfter,
            Note       = JsonSerializer.Serialize(new { sessionId, amount }),
            CreatedAt  = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Ledger reserve: tenant {Tenant} session {Session} amount {Amount} free-after {Free}",
            tenantId, sessionId, amount, freeAfter);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Writes a "reserve" audit entry for the given session WITHOUT re-running the free-balance
    /// affordability check. The caller must have already verified <c>GetFreeAsync &gt;= amount</c>
    /// before persisting the session. Because the session is already saved, its current_balance is
    /// already included in Σactive and a full ReserveAsync call would double-count it.
    /// </remarks>
    public async Task WriteReserveAuditAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct)
    {
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var free       = await GetFreeAsync(tenantId, ct); // free already reflects the saved session

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            Venue      = Venue,
            EntryKind  = "reserve",
            SessionId  = sessionId,
            Amount     = amount,
            WalletPusd = walletPusd,
            FreeAfter  = free, // correct: session already in sum, no additional deduction needed
            Note       = JsonSerializer.Serialize(new { sessionId, amount, auditOnly = true }),
            CreatedAt  = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Ledger reserve (audit-only): tenant {Tenant} session {Session} amount {Amount} free-after {Free}",
            tenantId, sessionId, amount, free);
    }

    /// <inheritdoc/>
    public async Task RecomputeAsync(Guid tenantId, Guid sessionId, decimal currentBalance, CancellationToken ct)
    {
        // The session's current_balance already reflects the new value (caller updated it before calling us).
        // We just append an audit entry so the ledger timeline shows each settlement.
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var free       = await GetFreeAsync(tenantId, ct);

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            Venue      = Venue,
            EntryKind  = "recompute",
            SessionId  = sessionId,
            Amount     = currentBalance,
            WalletPusd = walletPusd,
            FreeAfter  = free,
            Note       = JsonSerializer.Serialize(new { sessionId, currentBalance }),
            CreatedAt  = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        // Session already stopped_at set by caller — it falls out of the active sum automatically.
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var free       = await GetFreeAsync(tenantId, ct);

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            Venue      = Venue,
            EntryKind  = "release",
            SessionId  = sessionId,
            Amount     = 0m,
            WalletPusd = walletPusd,
            FreeAfter  = free,
            Note       = JsonSerializer.Serialize(new { sessionId, action = "session stopped" }),
            CreatedAt  = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Ledger release: tenant {Tenant} session {Session} free-after {Free}", tenantId, sessionId, free);
    }

    /// <inheritdoc/>
    public async Task ReconcileAsync(Guid tenantId, CancellationToken ct)
    {
        var walletPusd   = await GetWalletPusdAsync(tenantId, ct);
        var activeReserved = await ActiveLiveReservedAsync(tenantId, ct);
        var free         = walletPusd - activeReserved;
        // drift = (Σ active_balance + free) − walletPUSD. If ledger is consistent, drift == 0.
        var drift = (activeReserved + free) - walletPusd;

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id         = Guid.NewGuid(),
            TenantId   = tenantId,
            Venue      = Venue,
            EntryKind  = "reconcile",
            Amount     = walletPusd,
            WalletPusd = walletPusd,
            FreeAfter  = free,
            Drift      = drift,
            Note       = JsonSerializer.Serialize(new { walletPusd, activeReserved, free, drift }),
            CreatedAt  = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        if (drift != 0m)
        {
            var msg = $"[LEDGER DRIFT] Σ(active+free)={activeReserved + free:F6} vs walletPUSD={walletPusd:F6}, drift={drift:F6}. Check live session balances.";
            _logger.LogWarning(msg);
            await _channel.SendAsync(tenantId, new OutboundNotification(
                NotificationKind.CircuitBreakerTripped, "Ledger drift detected", msg), ct);
        }
        else
        {
            _logger.LogDebug("Ledger reconcile OK for tenant {Tenant}: walletPUSD={WalletPusd} free={Free}", tenantId, walletPusd, free);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Σ(live_sessions.current_balance WHERE stopped_at IS NULL AND mode='live') for this tenant.</summary>
    private Task<decimal> ActiveLiveReservedAsync(Guid tenantId, CancellationToken ct)
        => _db.LiveSessions
              .Where(s => s.TenantId == tenantId && s.StoppedAt == null && s.Mode == "live")
              .SumAsync(s => (decimal?)s.CurrentBalance ?? 0m, ct);
}
