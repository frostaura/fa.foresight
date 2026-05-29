using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Ledger;

/// <summary>
/// Append-only audit row for the account reservation ledger.
/// Every significant ledger event (reserve, recompute, release, reconcile) appends one row.
/// The table is append-only — no rows are deleted or updated.
/// </summary>
public sealed class AccountLedgerEntry : ITenantScoped
{
    public Guid   Id        { get; init; }
    public Guid   TenantId  { get; init; }
    /// <summary>Venue identifier (e.g. "polymarket").</summary>
    public required string  Venue     { get; init; }
    /// <summary>Event kind: "reserve" | "recompute" | "release" | "reconcile".</summary>
    public required string  EntryKind { get; init; }
    /// <summary>Associated live session id, if applicable.</summary>
    public Guid?  SessionId { get; init; }
    /// <summary>The amount involved in this event (reservation size, released amount, etc.).</summary>
    public decimal Amount    { get; init; }
    /// <summary>Wallet pUSD balance at time of entry (from on-chain query or cache).</summary>
    public decimal WalletPusd { get; init; }
    /// <summary>Computed free balance after this entry.</summary>
    public decimal FreeAfter { get; init; }
    /// <summary>Drift amount (non-zero only on reconcile entries).</summary>
    public decimal? Drift    { get; init; }
    /// <summary>Free-form JSON context note.</summary>
    public string?  Note     { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
