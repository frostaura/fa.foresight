using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Markets;

/// <summary>
/// A prediction market identified across providers.
/// (ProviderId, ExternalId) is the natural key; Id is the local UUID.
/// </summary>
public sealed class Market : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string ProviderId { get; init; }
    public required string ExternalId { get; init; }
    public required string Question { get; init; }
    public required string Category { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvesAt { get; init; }
    public MarketStatus Status { get; set; } = MarketStatus.Open;
    public string? ResolutionCriteria { get; set; }
    public bool ResolutionCriteriaAmbiguous { get; set; } = false;
}

public enum MarketStatus
{
    Open,
    ResolvedYes,
    ResolvedNo,
    Cancelled,
    Disputed
}

public sealed record MarketPrice(
    Guid MarketId,
    decimal YesPrice,
    decimal NoPrice,
    decimal Volume24h,
    decimal OpenInterest,
    DateTimeOffset ObservedAt);

public sealed record MarketHistoryPoint(DateTimeOffset Timestamp, decimal YesPrice);
