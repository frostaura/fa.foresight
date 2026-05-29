using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Bankroll;

public sealed class BankrollEntry : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string ProviderId { get; init; }
    public required decimal TotalUsd { get; set; }
    public required decimal InFlightUsd { get; set; }
    public DateTimeOffset RecordedAt { get; init; }
    public string? Note { get; set; }

    public decimal AvailableUsd => TotalUsd - InFlightUsd;
}
