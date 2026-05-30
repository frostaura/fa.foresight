using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// Thin server-side client over Binance's public spot REST API. No key required. Used by the
/// live-prediction service for technicals and for resolving past predictions (fetching the actual
/// close of a candle that has since closed).
/// </summary>
public sealed class BinanceMarketDataClient
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://api.binance.com";

    public BinanceMarketDataClient(HttpClient http)
    {
        _http = http;
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(BaseUrl);
        if (_http.Timeout == TimeSpan.FromSeconds(100)) _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public sealed record Candle(
        long OpenTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        long CloseTime);

    public async Task<IReadOnlyList<Candle>> GetKlinesAsync(
        string symbol, string interval, int limit = 120, CancellationToken ct = default)
    {
        var url = $"/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
        return await FetchKlinesAsync(url, ct);
    }

    /// <summary>
    /// Historical klines for a precise time window. Binance returns at most 1000 rows per request;
    /// the historical-candle adapter pages over <c>startTime</c> by stepping forward by
    /// <c>1000 * intervalMs</c> until <c>endTime</c> is reached.
    /// </summary>
    public async Task<IReadOnlyList<Candle>> GetKlinesRangeAsync(
        string symbol, string interval, long startTimeMs, long endTimeMs,
        int limit = 1000, CancellationToken ct = default)
    {
        var url = $"/api/v3/klines?symbol={symbol}&interval={interval}" +
                  $"&startTime={startTimeMs}&endTime={endTimeMs}&limit={limit}";
        return await FetchKlinesAsync(url, ct);
    }

    private async Task<IReadOnlyList<Candle>> FetchKlinesAsync(string url, CancellationToken ct)
    {
        using var stream = await _http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var arr = doc.RootElement;
        var list = new List<Candle>(arr.GetArrayLength());
        foreach (var row in arr.EnumerateArray())
        {
            list.Add(new Candle(
                OpenTime: row[0].GetInt64(),
                Open: decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture),
                High: decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture),
                Low: decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture),
                Close: decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture),
                Volume: decimal.Parse(row[5].GetString()!, CultureInfo.InvariantCulture),
                CloseTime: row[6].GetInt64()));
        }
        return list;
    }

    public sealed record AggTrade(long AggId, long Timestamp, decimal Quantity, bool IsBuyerMaker);

    /// <summary>
    /// All aggregated trades in <c>[startMs, endMs)</c>. Binance caps a time-bounded query to a
    /// 1-hour window and 1000 rows, so we page hour-by-hour and, within a dense hour, continue by
    /// <c>fromId</c> until the window is exhausted. Used by the live microstructure follower to
    /// reconstruct recent order-flow bars (the part the daily dumps don't yet cover).
    /// NOTE: network path — unit-tested aggregation downstream; the REST pagination itself needs a
    /// live-API smoke test before being relied on in production.
    /// </summary>
    public async Task<IReadOnlyList<AggTrade>> GetAggTradesRangeAsync(string symbol, long startMs, long endMs, CancellationToken ct = default)
    {
        const long HourMs = 3_600_000L;
        var results = new List<AggTrade>();
        var windowStart = startMs;
        while (windowStart < endMs && !ct.IsCancellationRequested)
        {
            var windowEnd = Math.Min(windowStart + HourMs - 1, endMs);
            var batch = await FetchAggTradesAsync($"/api/v3/aggTrades?symbol={symbol}&startTime={windowStart}&endTime={windowEnd}&limit=1000", ct);
            if (batch.Count > 0)
            {
                results.AddRange(batch);
                var lastId = batch[^1].AggId;
                while (batch.Count == 1000 && !ct.IsCancellationRequested)
                {
                    batch = await FetchAggTradesAsync($"/api/v3/aggTrades?symbol={symbol}&fromId={lastId + 1}&limit=1000", ct);
                    if (batch.Count == 0) break;
                    var inWindow = batch.Where(t => t.Timestamp <= windowEnd).ToList();
                    results.AddRange(inWindow);
                    if (inWindow.Count < batch.Count) break; // crossed past this hour window
                    lastId = batch[^1].AggId;
                }
            }
            windowStart = windowEnd + 1;
        }
        return results;
    }

    private async Task<List<AggTrade>> FetchAggTradesAsync(string url, CancellationToken ct)
    {
        using var stream = await _http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var arr = doc.RootElement;
        var list = new List<AggTrade>(arr.GetArrayLength());
        foreach (var row in arr.EnumerateArray())
        {
            list.Add(new AggTrade(
                AggId: row.GetProperty("a").GetInt64(),
                Timestamp: row.GetProperty("T").GetInt64(),
                Quantity: decimal.Parse(row.GetProperty("q").GetString()!, CultureInfo.InvariantCulture),
                IsBuyerMaker: row.GetProperty("m").GetBoolean()));
        }
        return list;
    }

    public sealed record Ticker24h(decimal LastPrice, decimal PriceChangePercent, decimal QuoteVolume);

    public async Task<Ticker24h> GetTicker24hAsync(string symbol, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/v3/ticker/24hr?symbol={symbol}", ct);
        resp.EnsureSuccessStatusCode();
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return new Ticker24h(
            LastPrice: decimal.Parse(j.GetProperty("lastPrice").GetString()!, CultureInfo.InvariantCulture),
            PriceChangePercent: decimal.Parse(j.GetProperty("priceChangePercent").GetString()!, CultureInfo.InvariantCulture),
            QuoteVolume: decimal.Parse(j.GetProperty("quoteVolume").GetString()!, CultureInfo.InvariantCulture));
    }

    public sealed record OrderBookSummary(
        decimal BidVolume, decimal AskVolume, decimal Imbalance,
        decimal BestBid, decimal BestAsk, decimal Spread);

    /// <summary>
    /// Top-N order book snapshot + computed imbalance. Imbalance = (bidVol - askVol)/(bidVol+askVol),
    /// so +1 means all bids / strong buying pressure, -1 means all asks / strong selling pressure.
    /// Short-horizon direction signal that candles alone don't capture.
    /// </summary>
    public async Task<OrderBookSummary> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken ct = default)
    {
        using var stream = await _http.GetStreamAsync($"/api/v3/depth?symbol={symbol}&limit={depth}", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        decimal bidVol = 0, askVol = 0;
        decimal bestBid = 0, bestAsk = 0;
        var bids = root.GetProperty("bids");
        var asks = root.GetProperty("asks");
        var i = 0;
        foreach (var b in bids.EnumerateArray())
        {
            var price = decimal.Parse(b[0].GetString()!, CultureInfo.InvariantCulture);
            var qty = decimal.Parse(b[1].GetString()!, CultureInfo.InvariantCulture);
            bidVol += qty * price;
            if (i == 0) bestBid = price;
            i++;
        }
        i = 0;
        foreach (var a in asks.EnumerateArray())
        {
            var price = decimal.Parse(a[0].GetString()!, CultureInfo.InvariantCulture);
            var qty = decimal.Parse(a[1].GetString()!, CultureInfo.InvariantCulture);
            askVol += qty * price;
            if (i == 0) bestAsk = price;
            i++;
        }
        var total = bidVol + askVol;
        var imbalance = total > 0 ? (bidVol - askVol) / total : 0m;
        var spread = bestAsk - bestBid;
        return new OrderBookSummary(bidVol, askVol, imbalance, bestBid, bestAsk, spread);
    }

    public sealed record TradeFlowSummary(
        int TradeCount, decimal BuyVolume, decimal SellVolume,
        decimal BuyPressureRatio, decimal AvgTradeSize);

    /// <summary>
    /// Aggregate recent trades into buy/sell pressure. Buy = aggressor on the ask (market buy).
    /// Pressure ratio = buyVol / (buyVol + sellVol). Strong tactical signal for "what side just
    /// got hit".
    /// </summary>
    public async Task<TradeFlowSummary> GetTradeFlowAsync(string symbol, int limit = 500, CancellationToken ct = default)
    {
        using var stream = await _http.GetStreamAsync($"/api/v3/trades?symbol={symbol}&limit={limit}", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        decimal buyVol = 0, sellVol = 0;
        var count = 0;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var qty = decimal.Parse(t.GetProperty("qty").GetString()!, CultureInfo.InvariantCulture);
            var price = decimal.Parse(t.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
            var isBuyerMaker = t.GetProperty("isBuyerMaker").GetBoolean();
            // If buyer is maker, the aggressor was the seller → sellVol. And vice versa.
            if (isBuyerMaker) sellVol += qty * price;
            else buyVol += qty * price;
            count++;
        }
        var total = buyVol + sellVol;
        var ratio = total > 0 ? buyVol / total : 0.5m;
        var avgSize = count > 0 ? total / count : 0m;
        return new TradeFlowSummary(count, buyVol, sellVol, ratio, avgSize);
    }

    public static long IntervalMs(string interval) => interval switch
    {
        "1m" => 60_000L,
        "5m" => 5 * 60_000L,
        "15m" => 15 * 60_000L,
        _ => throw new ArgumentException($"Unsupported interval: {interval}")
    };

    // ── Futures (perp) signals ────────────────────────────────────────────────────────────────
    // Orthogonal to spot price/volume — perp positioning + crowd skew often diverge from spot
    // and provide the strongest leading signal for short-horizon mean-reversion vs continuation.
    private const string FuturesBase = "https://fapi.binance.com";

    public sealed record FuturesPremiumIndex(decimal LastFundingRate, decimal MarkPrice, long NextFundingTime);

    /// <summary>
    /// Funding rate on the perp — positive means longs pay shorts (crowded long, mild bearish
    /// pressure on imbalances); negative means shorts pay longs (crowded short, mild bullish).
    /// Values are tiny (1 bp = 0.0001) but persistently extreme funding is a strong fade signal.
    /// </summary>
    public async Task<FuturesPremiumIndex> GetFuturesPremiumIndexAsync(string symbol, CancellationToken ct = default)
    {
        using var stream = await _http.GetStreamAsync($"{FuturesBase}/fapi/v1/premiumIndex?symbol={symbol}", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var r = doc.RootElement;
        return new FuturesPremiumIndex(
            LastFundingRate: decimal.Parse(r.GetProperty("lastFundingRate").GetString()!, CultureInfo.InvariantCulture),
            MarkPrice: decimal.Parse(r.GetProperty("markPrice").GetString()!, CultureInfo.InvariantCulture),
            NextFundingTime: r.GetProperty("nextFundingTime").GetInt64());
    }

    public sealed record OpenInterestDelta(decimal Latest, decimal DeltaPct5m, decimal DeltaPct1h);

    /// <summary>
    /// Pulls the last hour of 5m open-interest bins and computes short-horizon deltas. Rising OI
    /// during a price advance = new positioning (continuation likely); falling OI during a price
    /// advance = covering (less conviction). 5m and 1h give two horizons of context.
    /// </summary>
    public async Task<OpenInterestDelta> GetFuturesOpenInterestDeltaAsync(string symbol, CancellationToken ct = default)
    {
        using var stream = await _http.GetStreamAsync($"{FuturesBase}/futures/data/openInterestHist?symbol={symbol}&period=5m&limit=13", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var pts = new List<decimal>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            pts.Add(decimal.Parse(r.GetProperty("sumOpenInterest").GetString()!, CultureInfo.InvariantCulture));
        }
        if (pts.Count == 0) return new OpenInterestDelta(0, 0, 0);
        var latest = pts[^1];
        // limit=13 → ~65 min of 5m bins. delta5m = vs one bin back; delta1h = vs ~12 bins back.
        var d5 = pts.Count >= 2 && pts[^2] != 0 ? (latest - pts[^2]) / pts[^2] * 100m : 0m;
        var d60 = pts.Count >= 13 && pts[0] != 0 ? (latest - pts[0]) / pts[0] * 100m : 0m;
        return new OpenInterestDelta(latest, Math.Round(d5, 3), Math.Round(d60, 3));
    }

    public sealed record TopLongShortRatio(decimal Ratio, decimal LongAccountPct, decimal ShortAccountPct);

    /// <summary>
    /// Top-trader (Binance whales / large accounts) long/short positioning. Crowd-skew indicator —
    /// e.g. 3.0 = 75% of top accounts are long vs short, often a contrarian signal at extremes.
    /// </summary>
    public async Task<TopLongShortRatio?> GetTopLongShortRatioAsync(string symbol, CancellationToken ct = default)
    {
        using var stream = await _http.GetStreamAsync($"{FuturesBase}/futures/data/topLongShortAccountRatio?symbol={symbol}&period=5m&limit=1", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var arr = doc.RootElement.EnumerateArray();
        foreach (var r in arr)
        {
            return new TopLongShortRatio(
                Ratio: decimal.Parse(r.GetProperty("longShortRatio").GetString()!, CultureInfo.InvariantCulture),
                LongAccountPct: decimal.Parse(r.GetProperty("longAccount").GetString()!, CultureInfo.InvariantCulture) * 100m,
                ShortAccountPct: decimal.Parse(r.GetProperty("shortAccount").GetString()!, CultureInfo.InvariantCulture) * 100m);
        }
        return null;
    }
}
