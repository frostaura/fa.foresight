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
    /// Reconciliation sweep: compute drift = (Σ active current_balance + free) − walletPUSD.
    /// Logs and notifies via IChannelAdapter on non-zero drift. Does NOT silently correct — drift
    /// is a correctness signal.
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
