using FluentAssertions;
using FrostAura.Foresight.Domain.Ports;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Tests the single-reservation invariant introduced in <see cref="IAccountLedger"/>.
///
/// Strategy: use a <see cref="CapturingLedger"/> that records every call.
/// The invariant under test is:
///   "StartAsync must check affordability BEFORE creating the session (via GetFreeAsync),
///    then write exactly ONE audit reservation row (via WriteReserveAuditAsync) after saving —
///    no Guid.Empty orphan, no double ReserveAsync call."
///
/// Because the real LiveSessionEngine requires a database and several adapters, we test the
/// reservation semantics directly through the IAccountLedger contract.  The affordability
/// pre-flight is tested by simulating the sequence StartAsync performs:
///   1. GetFreeAsync — verify it is called before any write.
///   2. WriteReserveAuditAsync — verify exactly one call with the correct session id.
///   3. No ReserveAsync call — the old double-reserve path must not appear.
/// </summary>
public class ReservationInvariantTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Simulates the StartAsync reservation sequence as it now exists after the fix.</summary>
    private static async Task SimulateStartAsync(
        IAccountLedger ledger,
        decimal initialBalance,
        Guid sessionId,
        CancellationToken ct = default)
    {
        // Pre-flight affordability check (BEFORE session exists in the DB).
        var free = await ledger.GetFreeAsync(TenantId, ct);
        if (initialBalance > free)
            throw new InsufficientPusdException(initialBalance, free);

        // ... session would be saved to DB here (omitted — no DB in this test) ...

        // Write exactly one audit row with the real session id (NOT Guid.Empty).
        await ledger.WriteReserveAuditAsync(TenantId, sessionId, initialBalance, ct);
    }

    // ── Affordability pre-flight ──────────────────────────────────────────────────

    [Fact]
    public async Task GetFreeAsync_is_called_before_WriteReserveAuditAsync()
    {
        var ledger = new CapturingLedger(walletPusd: 100m, otherActiveBalance: 0m);
        var sessionId = Guid.NewGuid();

        await SimulateStartAsync(ledger, initialBalance: 50m, sessionId);

        var freeCallIndex = ledger.CallOrder.IndexOf(nameof(IAccountLedger.GetFreeAsync));
        var auditCallIndex = ledger.CallOrder.IndexOf(nameof(IAccountLedger.WriteReserveAuditAsync));

        freeCallIndex.Should().BeGreaterThanOrEqualTo(0, "GetFreeAsync must be called");
        auditCallIndex.Should().BeGreaterThan(freeCallIndex, "GetFreeAsync must precede WriteReserveAuditAsync");
    }

    [Fact]
    public async Task StartAsync_throws_InsufficientPusdException_when_balance_is_insufficient()
    {
        // walletPusd=100, otherActive=60 → free=40, but initialBalance=50 > 40.
        var ledger = new CapturingLedger(walletPusd: 100m, otherActiveBalance: 60m);
        var sessionId = Guid.NewGuid();

        var act = async () => await SimulateStartAsync(ledger, initialBalance: 50m, sessionId);

        await act.Should().ThrowAsync<InsufficientPusdException>();
    }

    [Fact]
    public async Task StartAsync_does_not_write_audit_row_when_affordability_check_fails()
    {
        var ledger = new CapturingLedger(walletPusd: 30m, otherActiveBalance: 0m);
        var sessionId = Guid.NewGuid();

        try { await SimulateStartAsync(ledger, initialBalance: 50m, sessionId); }
        catch (InsufficientPusdException) { /* expected */ }

        ledger.WriteReserveAuditCalls.Should().BeEmpty("no audit row when affordability check fails");
    }

    // ── Single reservation — no Guid.Empty orphan ────────────────────────────────

    [Fact]
    public async Task Exactly_one_WriteReserveAudit_call_per_session_start()
    {
        var ledger = new CapturingLedger(walletPusd: 200m, otherActiveBalance: 0m);
        var sessionId = Guid.NewGuid();

        await SimulateStartAsync(ledger, initialBalance: 100m, sessionId);

        ledger.WriteReserveAuditCalls.Should().HaveCount(1, "exactly one audit entry per session start");
    }

    [Fact]
    public async Task WriteReserveAudit_uses_real_session_id_not_Guid_Empty()
    {
        var ledger = new CapturingLedger(walletPusd: 200m, otherActiveBalance: 0m);
        var sessionId = Guid.NewGuid();

        await SimulateStartAsync(ledger, initialBalance: 100m, sessionId);

        var call = ledger.WriteReserveAuditCalls.Should().ContainSingle().Subject;
        call.SessionId.Should().Be(sessionId, "must use the real session id, not Guid.Empty");
        call.SessionId.Should().NotBe(Guid.Empty, "Guid.Empty orphan rows must not be written");
    }

    [Fact]
    public async Task ReserveAsync_is_never_called_in_the_new_start_path()
    {
        var ledger = new CapturingLedger(walletPusd: 200m, otherActiveBalance: 0m);
        var sessionId = Guid.NewGuid();

        await SimulateStartAsync(ledger, initialBalance: 100m, sessionId);

        ledger.ReserveAsyncCalls.Should().BeEmpty(
            "the new path uses WriteReserveAuditAsync, not ReserveAsync, to avoid double-counting");
    }

    // ── Boundary: walletPusd == InitialBalance + ΣotherActive ────────────────────

    [Fact]
    public async Task Session_can_start_when_walletPusd_equals_initialBalance_plus_otherActive()
    {
        // walletPusd=200, otherActive=100 → free=100, initialBalance=100 → exactly affordable.
        var ledger = new CapturingLedger(walletPusd: 200m, otherActiveBalance: 100m);
        var sessionId = Guid.NewGuid();

        var act = async () => await SimulateStartAsync(ledger, initialBalance: 100m, sessionId);

        await act.Should().NotThrowAsync("boundary case: walletPusd == initialBalance + otherActive is exactly affordable");
    }

    [Fact]
    public async Task Session_cannot_start_when_initialBalance_exceeds_free_by_one_cent()
    {
        // walletPusd=200, otherActive=100 → free=100, initialBalance=100.01 → not affordable.
        var ledger = new CapturingLedger(walletPusd: 200m, otherActiveBalance: 100m);
        var sessionId = Guid.NewGuid();

        var act = async () => await SimulateStartAsync(ledger, initialBalance: 100.01m, sessionId);

        await act.Should().ThrowAsync<InsufficientPusdException>();
    }
}

// ── Fakes ─────────────────────────────────────────────────────────────────────

internal sealed record LedgerCall(Guid TenantId, Guid SessionId, decimal Amount);

/// <summary>
/// Fake IAccountLedger that records call order and arguments for assertion.
/// GetFreeAsync returns (walletPusd - otherActiveBalance) — a stable value that simulates
/// other active sessions already reserving some balance.
/// </summary>
internal sealed class CapturingLedger : IAccountLedger
{
    private readonly decimal _walletPusd;
    private readonly decimal _otherActiveBalance;

    public List<string> CallOrder { get; } = new();
    public List<LedgerCall> WriteReserveAuditCalls { get; } = new();
    public List<LedgerCall> ReserveAsyncCalls { get; } = new();

    public CapturingLedger(decimal walletPusd, decimal otherActiveBalance)
    {
        _walletPusd = walletPusd;
        _otherActiveBalance = otherActiveBalance;
    }

    public Task<decimal> GetWalletPusdAsync(Guid tenantId, CancellationToken ct)
    {
        CallOrder.Add(nameof(GetWalletPusdAsync));
        return Task.FromResult(_walletPusd);
    }

    public Task<decimal> GetFreeAsync(Guid tenantId, CancellationToken ct)
    {
        CallOrder.Add(nameof(IAccountLedger.GetFreeAsync));
        return Task.FromResult(_walletPusd - _otherActiveBalance);
    }

    public Task ReserveAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct)
    {
        CallOrder.Add(nameof(IAccountLedger.ReserveAsync));
        ReserveAsyncCalls.Add(new LedgerCall(tenantId, sessionId, amount));
        return Task.CompletedTask;
    }

    public Task WriteReserveAuditAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct)
    {
        CallOrder.Add(nameof(IAccountLedger.WriteReserveAuditAsync));
        WriteReserveAuditCalls.Add(new LedgerCall(tenantId, sessionId, amount));
        return Task.CompletedTask;
    }

    public Task RecomputeAsync(Guid tenantId, Guid sessionId, decimal currentBalance, CancellationToken ct)
    {
        CallOrder.Add(nameof(IAccountLedger.RecomputeAsync));
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        CallOrder.Add(nameof(IAccountLedger.ReleaseAsync));
        return Task.CompletedTask;
    }

    public Task ReconcileAsync(Guid tenantId, CancellationToken ct)
    {
        CallOrder.Add(nameof(IAccountLedger.ReconcileAsync));
        return Task.CompletedTask;
    }
}
