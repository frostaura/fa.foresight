using System.Collections.Concurrent;
using System.Security.Cryptography;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Runtime safety arm for live execution. Even when the deployment is live-configured (a real
/// execution provider + wallet key + LiveTrading=true), NO real order is placed until the operator
/// explicitly arms it per tenant via the /golive confirmation flow. This is the 2FA-style gate the
/// shadow-mode doctrine mandated. Disarmed by /killswitch and on circuit-breaker trips.
///
/// Singleton: the arm state must survive across per-command DI scopes. The armed flag is also
/// MIRRORED to the <c>live_arm_state</c> table so it survives a PROCESS restart — an armed tenant
/// resumes automated trading after a restart (the operator's chosen behaviour). Pending confirmation
/// codes stay in-memory only (short-lived; a restart simply requires re-requesting a code).
/// </summary>
public interface ILiveTradingArm
{
    bool IsArmed(Guid tenantId);
    /// <summary>Issue a one-time confirmation code the operator must echo back to arm.</summary>
    string RequestCode(Guid tenantId);
    /// <summary>Confirm the code; arms live execution on success.</summary>
    bool Confirm(Guid tenantId, string code);
    void Disarm(Guid tenantId);
    /// <summary>Load persisted armed state into memory at startup. No-op when persistence is absent.</summary>
    Task HydrateAsync(CancellationToken ct);
}

public sealed class LiveTradingArm : ILiveTradingArm
{
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, bool> _armed = new();
    private readonly ConcurrentDictionary<Guid, (string Code, DateTimeOffset Expiry)> _pending = new();

    // Optional persistence — null in unit tests (pure in-memory state machine), wired in production
    // so the armed flag survives a restart.
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger<LiveTradingArm>? _logger;
    // Optional SSE fan-out — null in unit tests; in production lets the Live page react to arm/disarm
    // without polling /api/live/status.
    private readonly ILiveEventHub? _events;

    public LiveTradingArm(IServiceScopeFactory? scopes = null, ILogger<LiveTradingArm>? logger = null, ILiveEventHub? events = null)
    {
        _scopes = scopes;
        _logger = logger;
        _events = events;
    }

    public bool IsArmed(Guid tenantId) => _armed.TryGetValue(tenantId, out var a) && a;

    public string RequestCode(Guid tenantId)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _pending[tenantId] = (code, DateTimeOffset.UtcNow.Add(CodeTtl));
        return code;
    }

    public bool Confirm(Guid tenantId, string code)
    {
        if (!_pending.TryGetValue(tenantId, out var p)) return false;
        if (DateTimeOffset.UtcNow > p.Expiry) { _pending.TryRemove(tenantId, out _); return false; }
        if (!string.Equals(p.Code, code.Trim(), StringComparison.Ordinal)) return false;
        _pending.TryRemove(tenantId, out _);
        _armed[tenantId] = true;
        Persist(tenantId, armed: true);
        _events?.Publish(new LiveEvent(tenantId, LiveEventKind.ArmChanged, Armed: true, SessionId: null));
        return true;
    }

    public void Disarm(Guid tenantId)
    {
        _armed[tenantId] = false;
        _pending.TryRemove(tenantId, out _);
        Persist(tenantId, armed: false);
        _events?.Publish(new LiveEvent(tenantId, LiveEventKind.ArmChanged, Armed: false, SessionId: null));
    }

    public async Task HydrateAsync(CancellationToken ct)
    {
        if (_scopes is null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            var armed = await db.LiveArmStates.AsNoTracking()
                .Where(s => s.Armed)
                .Select(s => s.TenantId)
                .ToListAsync(ct);
            foreach (var tenantId in armed) _armed[tenantId] = true;
            if (armed.Count > 0)
                _logger?.LogWarning("LiveTradingArm: restored ARMED state for {Count} tenant(s) after restart", armed.Count);
        }
        catch (Exception ex)
        {
            // Fail safe: if hydration fails, tenants stay disarmed (never auto-arm on a read error).
            _logger?.LogError(ex, "LiveTradingArm: failed to hydrate armed state — all tenants remain disarmed");
        }
    }

    /// <summary>
    /// Mirror the armed flag to the DB on its own scope. Fire-and-forget: the in-memory map is the
    /// runtime source of truth, so a write failure degrades to fail-safe (a restart would not resume
    /// arming) rather than blocking the operator action.
    /// </summary>
    private void Persist(Guid tenantId, bool armed)
    {
        if (_scopes is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
                var now = DateTimeOffset.UtcNow;
                var row = await db.LiveArmStates.FirstOrDefaultAsync(s => s.TenantId == tenantId);
                if (row is null)
                {
                    row = new LiveArmState { TenantId = tenantId };
                    db.LiveArmStates.Add(row);
                }
                row.Armed = armed;
                if (armed) row.ArmedAt = now;
                row.UpdatedAt = now;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "LiveTradingArm: failed to persist armed={Armed} for tenant {Tenant}", armed, tenantId);
            }
        });
    }
}
