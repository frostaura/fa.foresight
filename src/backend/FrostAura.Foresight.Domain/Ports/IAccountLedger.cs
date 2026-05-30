namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Reservation ledger port — tracks how much pUSD is free vs. reserved across active live sessions.
///
/// Invariant: free = walletPUSD − Σ(current_balance of active live sessions WHERE stopped_at IS NULL).
/// The reservation IS each live session's current_balance. reserved_amount on each ledger entry is a
/// denormalised display cache updated at each settle call. On session start, initial_balance must be
/// &lt;= free; on session stop the balance leaves the sum and free rises. ReconcileAsync computes drift
/// without silently correcting it — it logs and notifies.
/// </summary>
public interface IAccountLedger
{
    /// <summary>Fetch the current on-chain pUSD wallet balance from the venue provider.</summary>
    Task<decimal> GetWalletPusdAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Compute the free (uncommitted) pUSD balance:
    ///   free = walletPUSD − Σ(current_balance of active live sessions WHERE stopped_at IS NULL).
    /// Pure derived query — never reads ledger delta rows.
    /// </summary>
    Task<decimal> GetFreeAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Reserve <paramref name="amount"/> pUSD for a new live session. Atomically checks
    /// free >= amount; throws <see cref="InsufficientPusdException"/> when the constraint fails.
    /// Appends a ledger audit entry.
    /// </summary>
    Task ReserveAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct);

    /// <summary>
    /// Write a reservation audit row for <paramref name="sessionId"/> WITHOUT re-running the
    /// free-balance check. Use this after a session has already been saved to the database so its
    /// current_balance is already included in the Σactive sum — calling ReserveAsync at that point
    /// would double-count and falsely fail when walletPusd ≈ InitialBalance + otherActive.
    /// The caller is responsible for verifying affordability before saving the session (via
    /// GetFreeAsync) and calling this method exactly once per session start.
    /// </summary>
    Task WriteReserveAuditAsync(Guid tenantId, Guid sessionId, decimal amount, CancellationToken ct);

    /// <summary>
    /// Update the denormalised reserved_amount display cache for a session after each settle.
    /// <paramref name="currentBalance"/> is the session's post-settle balance (= its current
    /// reservation footprint in the free query).
    /// </summary>
    Task RecomputeAsync(Guid tenantId, Guid sessionId, decimal currentBalance, CancellationToken ct);

    /// <summary>
    /// Release the reservation when a session stops (set stopped_at so it falls out of the
    /// active sum). Appends a ledger audit entry.
    /// </summary>
    Task ReleaseAsync(Guid tenantId, Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Reconciliation sweep: compares the on-chain pUSD wallet against active reservations and records
    /// drift = Σ(active current_balance) − walletPUSD. Alerts (logs + IChannelAdapter) only on a real
    /// shortfall (drift &gt; 0). When the on-chain balance can't be confirmed it records an "unknown"
    /// audit row and skips the alert — never treating an unconfirmed read as a real 0. Does NOT
    /// silently correct — drift is a correctness signal.
    /// </summary>
    Task ReconcileAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Thrown when a reservation request exceeds the free pUSD balance.</summary>
public sealed class InsufficientPusdException : Exception
{
    public decimal Requested { get; }
    public decimal Free      { get; }

    public InsufficientPusdException(decimal requested, decimal free)
        : base($"Insufficient pUSD: requested {requested:F6} but only {free:F6} is free.")
    {
        Requested = requested;
        Free      = free;
    }
}
