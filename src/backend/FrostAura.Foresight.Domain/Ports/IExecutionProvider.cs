namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Write-side port for placing trades against a prediction-market venue.
/// One adapter per venue; lifecycle separated from <see cref="IPredictionMarketProvider"/>
/// so trading code can ship without read code and vice versa.
/// </summary>
public interface IExecutionProvider
{
    string ProviderId { get; }

    Task<OrderReceipt> PlaceOrderAsync(OrderRequest request, CancellationToken ct);

    Task<OrderState> GetOrderStateAsync(string orderId, CancellationToken ct);

    Task CancelOrderAsync(string orderId, CancellationToken ct);

    Task<IReadOnlyList<PositionState>> GetOpenPositionsAsync(CancellationToken ct);
}

public sealed record OrderRequest(
    string MarketExternalId,
    OrderSide Side,
    decimal QuantityShares,
    decimal LimitPrice,
    Guid TenantId);

public sealed record OrderReceipt(string OrderId, OrderState State);

public sealed record OrderState(
    string OrderId,
    OrderSide Side,
    decimal QuantityShares,
    decimal FilledShares,
    decimal AverageFillPrice,
    OrderStatus Status,
    DateTimeOffset UpdatedAt);

public sealed record PositionState(
    string MarketExternalId,
    OrderSide Side,
    decimal Shares,
    decimal AverageEntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPnl);

public enum OrderSide { Yes, No }
public enum OrderStatus { Pending, PartiallyFilled, Filled, Cancelled, Rejected }
