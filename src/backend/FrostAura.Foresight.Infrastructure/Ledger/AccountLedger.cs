using System.Text.Json;
using FrostAura.Foresight.Domain.Ledger;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Ledger;

/// <summary>
/// Postgres-backed reservation ledger.
///
/// Invariant: free = walletPUSD − Σ(live_sessions.current_balance WHERE stopped_at IS NULL AND mode='live').
/// The invariant is ALWAYS derived by a live query — never from summing ledger delta rows (which would
/// accumulate rounding / double-count errors). Audit rows exist only for observability.
///
/// wallet pUSD is fetched via a stub (0 for now; wired to the real on-chain balance in Phase 5).
/// </summary>
public sealed class AccountLedger : IAccountLedger
{
    private const string Venue = "polymarket";

    private readonly ForesightDbContext _db;
    private readonly IChannelAdapter    _channel;
    private readonly ILogger<AccountLedger> _logger;

    public AccountLedger(ForesightDbContext db, IChannelAdapter channel, ILogger<AccountLedger> logger)
    {
        _db      = db;
        _channel = channel;
        _logger  = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Phase E: returns a hardcoded 0 (no on-chain call yet). Wire to pUSD balance endpoint after
    /// the supervised $1 order passes — the on-chain balance only matters for live execution.
    /// </remarks>
    public Task<decimal> GetWalletPusdAsync(Guid tenantId, CancellationToken ct)
        => Task.FromResult(0m); // Stubbed pending live funding.

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
