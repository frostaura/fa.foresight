using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Positions;

public sealed class Position : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required Guid MarketId { get; init; }
    public required Guid? ForecastId { get; init; }
    public required PositionMode Mode { get; init; }
    public required PositionSide Side { get; init; }
    public required decimal Shares { get; set; }
    public required decimal AverageEntryPrice { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? RealizedPnlUsd { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.Open;
    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ExternalOrderId { get; set; }
}

public enum PositionMode { Live, Shadow }
public enum PositionSide { Yes, No }
public enum PositionStatus { Open, Closed, Cancelled }
