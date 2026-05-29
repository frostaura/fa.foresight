using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Runtime safety arm for live execution. Even when the deployment is live-configured (a real
/// execution provider + wallet key + Polymarket:LiveTrading=true), NO real order is placed until the
/// operator explicitly arms it per tenant via the /golive confirmation flow. This is the 2FA-style
/// gate the shadow-mode doctrine mandated, preserved as an operational rail after the founder
/// overrode the calibration gate. Disarmed by /disable (kill switch) and on circuit-breaker trips.
///
/// Singleton: the arm state must survive across per-command DI scopes.
/// </summary>
public interface ILiveTradingArm
{
    bool IsArmed(Guid tenantId);
    /// <summary>Issue a one-time confirmation code the operator must echo back to arm.</summary>
    string RequestCode(Guid tenantId);
    /// <summary>Confirm the code; arms live execution on success.</summary>
    bool Confirm(Guid tenantId, string code);
    void Disarm(Guid tenantId);
}

public sealed class LiveTradingArm : ILiveTradingArm
{
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, bool> _armed = new();
    private readonly ConcurrentDictionary<Guid, (string Code, DateTimeOffset Expiry)> _pending = new();

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
        return true;
    }

    public void Disarm(Guid tenantId)
    {
        _armed[tenantId] = false;
        _pending.TryRemove(tenantId, out _);
    }
}
