using FrostAura.Foresight.Domain.Markets;

namespace FrostAura.Foresight.Domain.Ports;


/// <summary>
/// Read-side port for a prediction-market venue. Adapters: Polymarket, Kalshi, Manifold, Augur.
/// Execution lives behind <see cref="IExecutionProvider"/>; this port is read-only.
/// </summary>
public interface IPredictionMarketProvider
{
    /// <summary>Stable identifier matching <see cref="Market.ProviderId"/>.</summary>
    string ProviderId { get; }

    Task<IReadOnlyList<Market>> DiscoverMarketsAsync(MarketDiscoveryQuery query, CancellationToken ct);

    /// <summary>
    /// Discovery enriched with display-tier fields (image, current prices, volumes, liquidity).
    /// Default implementation falls back to <see cref="DiscoverMarketsAsync"/> with empty enrichment.
    /// </summary>
    async Task<IReadOnlyList<MarketDiscoveryResult>> DiscoverMarketsRichAsync(MarketDiscoveryQuery query, CancellationToken ct)
    {
        var markets = await DiscoverMarketsAsync(query, ct);
        return markets.Select(m => new MarketDiscoveryResult(m, null, null, null, null, null, null, null)).ToList();
    }

    Task<Market?> GetMarketAsync(string externalId, CancellationToken ct);

    /// <summary>
    /// Single-market lookup enriched with display-tier fields. Default falls back to
    /// <see cref="GetMarketAsync"/> with empty enrichment.
    /// </summary>
    async Task<MarketDiscoveryResult?> GetMarketRichAsync(string externalId, CancellationToken ct)
    {
        var m = await GetMarketAsync(externalId, ct);
        return m is null ? null : new MarketDiscoveryResult(m, null, null, null, null, null, null, null);
    }

    Task<MarketPrice> GetCurrentPriceAsync(string externalId, CancellationToken ct);

    Task<IReadOnlyList<MarketPrice>> GetPriceHistoryAsync(string externalId, DateTimeOffset since, CancellationToken ct);

    /// <summary>
    /// YES-probability time series for charting. Interval is provider-defined ("1h", "6h", "1d", "max").
    /// Default returns empty.
    /// </summary>
    Task<IReadOnlyList<MarketHistoryPoint>> GetPriceSeriesAsync(string externalId, string interval, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MarketHistoryPoint>>(Array.Empty<MarketHistoryPoint>());

    /// <summary>
    /// Best-effort: fetch the recurring market window (resolution start/end, token ids, quotes)
    /// for the candle described by (symbol, interval, targetOpenTimeMs). Returns null when no live
    /// data is available — the store falls back to a synthetic-flat row.
    /// </summary>
    Task<VenueMarketWindow?> GetRecurringMarketWindowAsync(
        string symbol, string interval, long targetOpenTimeMs, CancellationToken ct)
        => Task.FromResult<VenueMarketWindow?>(null);

    /// <summary>
    /// Best-effort: fetch the YES/NO price for a specific market at a specific wall-clock moment.
    /// Returns null when no live data is available.
    /// </summary>
    Task<VenuePriceQuote?> GetOddsAtAsync(
        string marketExternalId, long atMs, CancellationToken ct)
        => Task.FromResult<VenuePriceQuote?>(null);
}

public enum MarketDiscoverySort
{
    Volume,
    Volume24h,
    Liquidity,
    EndDate,
    Newest
}

public sealed record MarketDiscoveryQuery(
    string? Category = null,
    string? SearchTerm = null,
    decimal? MinVolume = null,
    DateTimeOffset? ResolvesAfter = null,
    DateTimeOffset? ResolvesBefore = null,
    MarketDiscoverySort Sort = MarketDiscoverySort.Volume24h,
    int Take = 100,
    int Skip = 0,
    bool IncludeClosed = false);

public sealed record MarketDiscoveryResult(
    Market Market,
    string? ImageUrl,
    string? IconUrl,
    decimal? YesPrice,
    decimal? NoPrice,
    decimal? Volume,
    decimal? Volume24h,
    decimal? Liquidity);
