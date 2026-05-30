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

    private readonly ForesightDbContext _db;
    private readonly IChannelAdapter _channel;
    private readonly IKeyVault _vault;
    private readonly HttpClient _http;
    private readonly PolygonOptions _polygonOpts;
    private readonly KeyVaultOptions _keyVaultOpts;
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
        _db = db;
        _channel = channel;
        _vault = vault;
        _http = http;
        _polygonOpts = polygonOpts.Value;
        _keyVaultOpts = keyVaultOpts.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the real on-chain pUSD ERC-20 balance from Polygon for the tenant's configured wallet.
    /// Returns 0 when the balance is UNKNOWN (no wallet configured, RPC unreachable, parse failure) —
    /// callers that need to distinguish unknown from a real 0 (reconciliation) use
    /// <see cref="ReadWalletPusdKnownAsync"/>. For reservation math a 0 is safe-conservative: free
    /// goes ≤ 0, so a reserve attempt fails loudly rather than over-committing unconfirmed funds.
    /// </remarks>
    public async Task<decimal> GetWalletPusdAsync(Guid tenantId, CancellationToken ct)
        => (await ReadWalletPusdKnownAsync(tenantId, ct)).Balance;

    /// <summary>
    /// Read the tenant's on-chain pUSD balance, distinguishing a confirmed value from UNKNOWN.
    /// Sources the wallet address + RPC endpoint from the tenant's platform connection (falling back
    /// to the env-bound vault/options for the bootstrap/dev path). <c>Known=false</c> means the balance
    /// could not be confirmed (no wallet, RPC error, bad response) — never treat that as a real 0.
    /// </summary>
    internal async Task<(decimal Balance, bool Known)> ReadWalletPusdKnownAsync(Guid tenantId, CancellationToken ct)
    {
        var (address, rpcUrl) = await ResolveWalletConfigAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(address))
            return (0m, false); // no wallet configured for this tenant — balance is N/A, not a real 0

        try
        {
            var balance = await ReadPusdBalanceKnownAsync(address!, rpcUrl, ct);
            return balance;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReadWalletPusdKnownAsync: on-chain read failed for tenant {Tenant} — balance UNKNOWN (not assumed 0)", tenantId);
            return (0m, false);
        }
    }

    /// <summary>
    /// Resolve the tenant's wallet address + Polygon RPC URL. Prefers the per-tenant platform
    /// connection (WalletAddress + RpcUrl); falls back to the env-bound vault/options when no
    /// connection row exists (bootstrap/dev). Returns a null address when no wallet is available.
    /// </summary>
    private async Task<(string? Address, string RpcUrl)> ResolveWalletConfigAsync(Guid tenantId, CancellationToken ct)
    {
        var conn = await _db.PlatformConnections.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsDefault)
            .Select(c => new { c.WalletAddress, c.RpcUrl })
            .FirstOrDefaultAsync(ct);

        var rpcUrl = !string.IsNullOrWhiteSpace(conn?.RpcUrl) ? conn!.RpcUrl! : _polygonOpts.RpcUrl;

        if (!string.IsNullOrWhiteSpace(conn?.WalletAddress))
            return (conn!.WalletAddress, rpcUrl);

        // Env/bootstrap fallback: use the global key vault's derived address when a key is configured.
        if (_keyVaultOpts.HasKey)
        {
            try { return (await _vault.GetPublicAddressAsync(ct), rpcUrl); }
            catch (Exception ex) { _logger.LogWarning(ex, "ResolveWalletConfigAsync: could not derive env wallet address"); }
        }
        return (null, rpcUrl);
    }

    /// <summary>
    /// Read pUSD ERC-20 balance from Polygon via eth_call to balanceOf(address) on the given RPC.
    /// Uses minimal JSON-RPC (no Nethereum ABI overhead). Returns <c>Known=false</c> on any RPC/parse
    /// failure so the caller never mistakes an unconfirmed read for a real 0 balance.
    /// </summary>
    internal async Task<(decimal Balance, bool Known)> ReadPusdBalanceKnownAsync(string walletAddress, string rpcUrl, CancellationToken ct)
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
            method = "eth_call",
            @params = new object[]
            {
                new { to = _polygonOpts.PusdContractAddress, data = callData },
                "latest"
            },
            id = 1
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
        { Content = new StringContent(rpcBody, Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Polygon RPC returned {Status} for balanceOf — balance UNKNOWN", resp.StatusCode);
            return (0m, false);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("result", out var resultEl))
        {
            _logger.LogWarning("Polygon RPC response missing 'result' field — balance UNKNOWN. Body: {Body}", body[..Math.Min(body.Length, 200)]);
            return (0m, false);
        }

        var hex = resultEl.GetString() ?? "0x0";
        if (hex == "0x" || hex == "0x0") return (0m, true); // a real, confirmed zero balance

        // Parse the 32-byte ABI-encoded uint256 result.
        var cleanHex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (!BigInteger.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out var raw))
        {
            _logger.LogWarning("Could not parse Polygon RPC result '{Hex}' as uint256 — balance UNKNOWN", hex);
            return (0m, false);
        }

        // Scale from integer (6 decimals) to decimal pUSD.
        var divisor = BigInteger.Pow(10, _polygonOpts.PusdDecimals);
        var whole = (decimal)(raw / divisor);
        var frac = (decimal)(raw % divisor) / (decimal)divisor;
        var balance = whole + frac;

        _logger.LogDebug("On-chain pUSD balance for {Address}: {Balance} (raw={Raw})", walletAddress, balance, raw);
        return (balance, true);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// free = walletPUSD − Σ(live_session.current_balance WHERE stopped_at IS NULL AND mode='live').
    /// Derived by a single query — NOT by reading ledger deltas.
    /// </remarks>
    public async Task<decimal> GetFreeAsync(Guid tenantId, CancellationToken ct)
    {
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var reserved = await ActiveLiveReservedAsync(tenantId, ct);
        return walletPusd - reserved;
    }

    /// <inheritdoc/>
    public async Task ReserveAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct)
    {
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var free = await GetFreeAsync(tenantId, ct);

        if (amount > free)
            throw new InsufficientPusdException(amount, free);

        var freeAfter = free - amount;
        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = Venue,
            EntryKind = "reserve",
            SessionId = sessionId,
            Amount = amount,
            WalletPusd = walletPusd,
            FreeAfter = freeAfter,
            Note = JsonSerializer.Serialize(new { sessionId, amount }),
            CreatedAt = DateTimeOffset.UtcNow
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
        var free = await GetFreeAsync(tenantId, ct); // free already reflects the saved session

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = Venue,
            EntryKind = "reserve",
            SessionId = sessionId,
            Amount = amount,
            WalletPusd = walletPusd,
            FreeAfter = free, // correct: session already in sum, no additional deduction needed
            Note = JsonSerializer.Serialize(new { sessionId, amount, auditOnly = true }),
            CreatedAt = DateTimeOffset.UtcNow
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
        var free = await GetFreeAsync(tenantId, ct);

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = Venue,
            EntryKind = "recompute",
            SessionId = sessionId,
            Amount = currentBalance,
            WalletPusd = walletPusd,
            FreeAfter = free,
            Note = JsonSerializer.Serialize(new { sessionId, currentBalance }),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        // Session already stopped_at set by caller — it falls out of the active sum automatically.
        var walletPusd = await GetWalletPusdAsync(tenantId, ct);
        var free = await GetFreeAsync(tenantId, ct);

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = Venue,
            EntryKind = "release",
            SessionId = sessionId,
            Amount = 0m,
            WalletPusd = walletPusd,
            FreeAfter = free,
            Note = JsonSerializer.Serialize(new { sessionId, action = "session stopped" }),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Ledger release: tenant {Tenant} session {Session} free-after {Free}", tenantId, sessionId, free);
    }

    /// <inheritdoc/>
    public async Task ReconcileAsync(Guid tenantId, CancellationToken ct)
    {
        var (walletPusd, known) = await ReadWalletPusdKnownAsync(tenantId, ct);
        var activeReserved = await ActiveLiveReservedAsync(tenantId, ct);

        // When the on-chain balance can't be confirmed, do NOT compute or claim a drift — recording a
        // drift of 0 against an assumed-0 wallet would silently mask a real divergence. Write an audit
        // row marking the balance unknown and skip the alert.
        if (!known)
        {
            _db.AccountLedger.Add(new AccountLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Venue = Venue,
                EntryKind = "reconcile",
                Amount = 0m,
                WalletPusd = 0m,
                FreeAfter = -activeReserved,
                Drift = null, // unknown — not computed
                Note = JsonSerializer.Serialize(new { balanceKnown = false, activeReserved, note = "on-chain pUSD balance unconfirmed (no wallet / RPC error) — drift not computed" }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning("Ledger reconcile SKIPPED for tenant {Tenant}: on-chain pUSD balance unconfirmed (no wallet or RPC error)", tenantId);
            return;
        }

        var free = walletPusd - activeReserved;
        // Meaningful drift: how much MORE the active live sessions collectively claim than the wallet
        // actually holds. ≤ 0 in a healthy account (wallet funds at least the reservations); > 0 is a
        // real shortfall (external withdrawal, unsettled divergence) that must be surfaced.
        var drift = activeReserved - walletPusd;

        _db.AccountLedger.Add(new AccountLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = Venue,
            EntryKind = "reconcile",
            Amount = walletPusd,
            WalletPusd = walletPusd,
            FreeAfter = free,
            Drift = drift,
            Note = JsonSerializer.Serialize(new { balanceKnown = true, walletPusd, activeReserved, free, drift }),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        // Alert only on a real shortfall (active reservations exceed the on-chain wallet, beyond a
        // tiny rounding epsilon). A healthy positive free balance is not drift.
        if (drift > 0.000001m)
        {
            var msg = $"[LEDGER DRIFT] active reservations={activeReserved:F6} exceed on-chain walletPUSD={walletPusd:F6} by {drift:F6}. Live sessions claim more than the wallet holds — check balances/withdrawals.";
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
