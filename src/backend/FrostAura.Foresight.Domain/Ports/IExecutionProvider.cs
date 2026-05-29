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

    /// <summary>
    /// Place a SELL (close/exit) order for the given token.
    /// SELL: deliver <paramref name="quantityShares"/> outcome tokens to receive pUSD collateral.
    /// V2 amounts: makerAmount=floor(size*1e6), takerAmount=floor(price*size*1e6).
    /// Returns null (gracefully) when LiveTrading is disarmed or the order is below min-order-size.
    /// </summary>
    Task<OrderReceipt?> SellAsync(SellRequest request, CancellationToken ct);

    Task<OrderState> GetOrderStateAsync(string orderId, CancellationToken ct);

    Task CancelOrderAsync(string orderId, CancellationToken ct);

    Task<IReadOnlyList<PositionState>> GetOpenPositionsAsync(CancellationToken ct);

    /// <summary>
    /// Query the market's own resolution status for <paramref name="conditionId"/>.
    /// Returns null when the market is not yet resolved, unreachable, or live trading is disarmed.
    /// Never throws — all network errors are caught and logged; caller treats null as "not resolved yet".
    /// </summary>
    Task<MarketResolution?> GetMarketResolutionAsync(string conditionId, CancellationToken ct);
}

public sealed record OrderRequest(
    string MarketExternalId,
    OrderSide Side,
    decimal QuantityShares,
    decimal LimitPrice,
    Guid TenantId);

public sealed record SellRequest(
    string MarketExternalId,
    OrderSide Side,          // Yes or No — which token is being sold
    decimal QuantityShares,  // shares to sell
    decimal LimitPrice,      // collateral price per share expected
    Guid TenantId);

/// <summary>
/// Market resolution result from the venue's own settlement oracle.
/// WinningOutcomeIndex: 0 = Yes won, 1 = No won (Polymarket CTF convention).
/// </summary>
public sealed record MarketResolution(
    string ConditionId,
    bool   Resolved,
    int?   WinningOutcomeIndex)
{
    /// <summary>True when the market resolved with YES winning (outcomeIndex=0).</summary>
    public bool? YesWon => Resolved ? WinningOutcomeIndex == 0 : null;
}

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
