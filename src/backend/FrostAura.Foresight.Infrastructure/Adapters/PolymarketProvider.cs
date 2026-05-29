using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Foresight.Domain.Markets;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


namespace FrostAura.Foresight.Infrastructure.Adapters;

public sealed class PolymarketOptions
{
    public string GammaBaseUrl { get; set; } = "https://gamma-api.polymarket.com";
    public string ClobBaseUrl { get; set; } = "https://clob.polymarket.com";
}

/// <summary>
/// Read-only Polymarket adapter using the public Gamma API (no auth required).
/// Discovers markets, fetches metadata, returns latest YES/NO prices via the CLOB.
/// </summary>
public sealed class PolymarketProvider : IPredictionMarketProvider
{
    private readonly HttpClient _http;
    private readonly PolymarketOptions _opts;
    private readonly ILogger<PolymarketProvider> _logger;

    public string ProviderId => "polymarket";

    public PolymarketProvider(HttpClient http, IOptions<PolymarketOptions> opts, ILogger<PolymarketProvider> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Market>> DiscoverMarketsAsync(MarketDiscoveryQuery query, CancellationToken ct)
    {
        var dtos = await FetchDiscoveryDtosAsync(query, ct);
        return dtos.Select(d => MapToDomain(d)).ToList();
    }

    public async Task<IReadOnlyList<MarketDiscoveryResult>> DiscoverMarketsRichAsync(MarketDiscoveryQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            return await SearchAndEnrichAsync(query, ct);

        // Polymarket dominates the active list with auto-generated 5-min "Up or Down" markets that Polymarket
        // itself tags `hide-from-new`. Page through gamma and skip those, then hard-filter by category (since
        // gamma's `tag_slug` is silently ignored on /markets). Bound the scan so a single request can't
        // run away — Skip/Take is applied to the filtered stream.
        var wantedSlug = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category!.ToLowerInvariant();
        var wantedLabel = wantedSlug is not null && PrimaryCategoryLabels.TryGetValue(wantedSlug, out var lbl) ? lbl : null;

        const int upstreamPage = 200;
        const int maxScan = 1200;
        var needed = query.Skip + query.Take;
        var matches = new List<MarketDiscoveryResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var offset = 0; offset < maxScan && matches.Count < needed; offset += upstreamPage)
        {
            var page = query with { Take = upstreamPage, Skip = offset };
            var dtos = await FetchDiscoveryDtosAsync(page, ct);
            if (dtos.Count == 0) break;

            var eventIds = dtos
                .Select(d => d.Events?.FirstOrDefault()?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct()
                .ToList();
            var enrichment = await FetchEventEnrichmentAsync(eventIds, ct);

            var now = DateTimeOffset.UtcNow;
            foreach (var d in dtos)
            {
                var key = d.Id ?? d.Slug;
                if (key is null || !seen.Add(key)) continue;

                if (!query.IncludeClosed)
                {
                    if (d.Closed == true) continue;
                    if (DateTimeOffset.TryParse(d.EndDate, out var ed) && ed <= now) continue;
                }

                var evtId = d.Events?.FirstOrDefault()?.Id;
                EventEnrichment? info = null;
                if (evtId is not null) enrichment.TryGetValue(evtId, out info);
                var tags = info?.TagSlugs ?? EmptyTagSet;

                if (info?.HideFromNew == true) continue;

                if (wantedSlug is not null)
                {
                    var tagHit = tags.Contains(wantedSlug);
                    var labelHit = wantedLabel is not null && string.Equals(info?.CategoryLabel, wantedLabel, StringComparison.OrdinalIgnoreCase);
                    if (!tagHit && !labelHit) continue;
                }

                var (yes, no) = ParsePrices(d.OutcomePrices);
                decimal? yesP = yes == 0.5m && no == 0.5m && string.IsNullOrWhiteSpace(d.OutcomePrices) ? null : yes;
                decimal? noP = yes == 0.5m && no == 0.5m && string.IsNullOrWhiteSpace(d.OutcomePrices) ? null : no;
                var market = MapToDomain(d, info?.CategoryLabel);
                matches.Add(new MarketDiscoveryResult(market, d.Image, d.Icon, yesP, noP, ParseDecimal(d.Volume), d.Volume24hr, ParseDecimal(d.Liquidity)));
            }

            if (dtos.Count < upstreamPage) break;
        }

        return matches.Skip(query.Skip).Take(query.Take).ToList();
    }

    private async Task<IReadOnlyList<MarketDiscoveryResult>> SearchAndEnrichAsync(MarketDiscoveryQuery query, CancellationToken ct)
    {
        var dtos = await FetchSearchDtosAsync(query, ct);

        var eventIds = dtos
            .Select(d => d.Events?.FirstOrDefault()?.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();
        var enrichment = await FetchEventEnrichmentAsync(eventIds, ct);

        return dtos.Select(d =>
        {
            var (yes, no) = ParsePrices(d.OutcomePrices);
            decimal? yesP = yes == 0.5m && no == 0.5m && string.IsNullOrWhiteSpace(d.OutcomePrices) ? null : yes;
            decimal? noP = yes == 0.5m && no == 0.5m && string.IsNullOrWhiteSpace(d.OutcomePrices) ? null : no;
            var evtId = d.Events?.FirstOrDefault()?.Id;
            EventEnrichment? info = null;
            if (evtId is not null) enrichment.TryGetValue(evtId, out info);
            var market = MapToDomain(d, info?.CategoryLabel);
            return new MarketDiscoveryResult(market, d.Image, d.Icon, yesP, noP, ParseDecimal(d.Volume), d.Volume24hr, ParseDecimal(d.Liquidity));
        }).ToList();
    }

    // Tag slugs we treat as primary categories — lower-case, mapped to a display label.
    private static readonly Dictionary<string, string> PrimaryCategoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["politics"] = "Politics",
        ["geopolitics"] = "Geopolitics",
        ["crypto"] = "Crypto",
        ["sports"] = "Sports",
        ["tech"] = "Tech",
        ["economy"] = "Economy",
        ["business"] = "Business",
        ["culture"] = "Culture",
        ["pop-culture"] = "Pop culture",
        ["entertainment"] = "Entertainment",
        ["science"] = "Science",
        ["climate"] = "Climate",
        ["health"] = "Health",
        ["world"] = "World",
        ["finance"] = "Finance",
        ["elections"] = "Elections"
    };

    private sealed record EventEnrichment(string? CategoryLabel, bool HideFromNew, IReadOnlySet<string> TagSlugs);

    private static readonly IReadOnlySet<string> EmptyTagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, EventEnrichment>> FetchEventEnrichmentAsync(IReadOnlyList<string> eventIds, CancellationToken ct)
    {
        if (eventIds.Count == 0) return new();

        // Gamma silently caps /events?id=&id=... responses at ~20 items regardless of how many IDs are sent.
        // Chunk to stay under the ceiling, and fire the chunks concurrently — sequential chunking turned
        // a single discovery page into ~10s of latency.
        const int chunkSize = 18;
        var chunks = new List<List<string>>();
        for (var i = 0; i < eventIds.Count; i += chunkSize)
            chunks.Add(eventIds.Skip(i).Take(chunkSize).ToList());

        var perChunk = await Task.WhenAll(chunks.Select(async chunk =>
        {
            var local = new List<(string Id, EventEnrichment Info)>();
            var qs = string.Join("&", chunk.Select(id => $"id={Uri.EscapeDataString(id)}"));
            var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/events?{qs}";
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Polymarket events lookup failed: {Status}", resp.StatusCode);
                    return local;
                }
                var events = await resp.Content.ReadFromJsonAsync<List<PolymarketEventDto>>(cancellationToken: ct) ?? new();
                foreach (var e in events)
                {
                    if (e.Id is null) continue;
                    var label = PickCategoryLabel(e.Tags);
                    var slugs = new HashSet<string>(
                        (e.Tags ?? new()).Where(t => t.Slug is not null).Select(t => t.Slug!),
                        StringComparer.OrdinalIgnoreCase);
                    local.Add((e.Id, new EventEnrichment(label, slugs.Contains("hide-from-new"), slugs)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Polymarket events lookup error");
            }
            return local;
        }));

        var result = new Dictionary<string, EventEnrichment>();
        foreach (var batch in perChunk)
            foreach (var (id, info) in batch)
                result[id] = info;
        return result;
    }

    private static string? PickCategoryLabel(IReadOnlyList<PolymarketTagDto>? tags)
    {
        if (tags is null || tags.Count == 0) return null;
        // Prefer tags in our primary list, in priority order they appear in PrimaryCategoryLabels.
        foreach (var (slug, label) in PrimaryCategoryLabels)
        {
            if (tags.Any(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                return label;
        }
        // Fallback to the first non-utility tag's label (skip generic ones).
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "featured", "all", "trending", "new" };
        var first = tags.FirstOrDefault(t => t.Label is not null && !skip.Contains(t.Slug ?? ""));
        return first?.Label;
    }

    private async Task<List<PolymarketMarketDto>> FetchDiscoveryDtosAsync(MarketDiscoveryQuery query, CancellationToken ct)
    {
        // Polymarket's /markets endpoint silently ignores the `q` parameter, so route text searches
        // through /public-search and flatten event.markets — that endpoint actually does the matching.
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            return await FetchSearchDtosAsync(query, ct);

        var closedClause = query.IncludeClosed ? string.Empty : "&closed=false";
        var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets?active=true{closedClause}&limit={query.Take}&offset={query.Skip}";
        // Note: Gamma silently ignores tag_slug[] on /markets, so category filtering is applied in
        // DiscoverMarketsRichAsync after enrichment instead.
        if (query.MinVolume is not null) url += $"&volume_num_min={query.MinVolume.Value.ToString(CultureInfo.InvariantCulture)}";
        // Gamma's end_date_min/max validation rejects the "o" format (offset suffix); use Z-suffix UTC.
        // Also, when !IncludeClosed we must constrain end_date_min to now or gamma will surface huge swaths
        // of already-ended markets (active=true but past resolution date) and our in-memory filter
        // exhausts the scan budget — turning "Resolving soonest" into an empty list.
        var effectiveResolvesAfter = query.ResolvesAfter
            ?? (query.IncludeClosed ? (DateTimeOffset?)null : DateTimeOffset.UtcNow);
        if (query.ResolvesBefore is not null)
            url += $"&end_date_max={Uri.EscapeDataString(query.ResolvesBefore.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";
        if (effectiveResolvesAfter is not null)
            url += $"&end_date_min={Uri.EscapeDataString(effectiveResolvesAfter.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";
        var (orderField, ascending) = query.Sort switch
        {
            MarketDiscoverySort.Volume => ("volumeNum", false),
            MarketDiscoverySort.Volume24h => ("volume24hr", false),
            MarketDiscoverySort.Liquidity => ("liquidityNum", false),
            MarketDiscoverySort.EndDate => ("endDate", true),
            MarketDiscoverySort.Newest => ("startDate", false),
            _ => ("volume24hr", false)
        };
        url += $"&order={orderField}&ascending={(ascending ? "true" : "false")}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Polymarket discover failed: {Status}", resp.StatusCode);
            return new();
        }
        return await resp.Content.ReadFromJsonAsync<List<PolymarketMarketDto>>(cancellationToken: ct) ?? new();
    }

    // Common abbreviation → canonical-term map. Polymarket titles use full names ("Bitcoin"), so a
    // user searching "BTC ..." gets zero matches from public-search's loose phrase matching.
    private static readonly (string abbr, string canonical)[] SearchSynonyms = new[]
    {
        ("btc", "bitcoin"),
        ("eth", "ethereum"),
        ("sol", "solana"),
        ("doge", "dogecoin"),
        ("ada", "cardano"),
        ("xrp", "ripple"),
        ("us", "usa"),
        ("uk", "united kingdom"),
        ("eu", "european union"),
        ("ai", "artificial intelligence")
    };

    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "will","with","when","what","whom","whose","this","that","these","those","there","their",
        "have","been","were","does","doing","done","into","over","under","also","than","then",
        "more","most","much","some","each","every","very","just","only","make","made","from","onto",
        "before","after","during","while","still","since","upon","next","prior","both","other",
        "hourly","daily","weekly","monthly","yearly"
    };

    private static IReadOnlyList<string> BuildSearchAttempts(string term)
    {
        // Cascade from most-specific to most-permissive so a user query like "BTC Up or Down Hourly"
        // tries: full phrase → synonym-expanded ("bitcoin up or down hourly") → drop noise words
        // → first 2 substantive tokens → first salient token. public-search ranks by relevance, so
        // wider queries surface more candidates and the in-memory filters keep them honest.
        var attempts = new List<string>();
        var lower = term.ToLowerInvariant();
        void Add(string? q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            var t = q.Trim();
            if (!attempts.Contains(t, StringComparer.OrdinalIgnoreCase)) attempts.Add(t);
        }

        Add(term);

        // Synonym expansion — substitute abbr→canonical (and vice-versa) anywhere in the query.
        foreach (var (abbr, canonical) in SearchSynonyms)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\b{abbr}\b"))
                Add(System.Text.RegularExpressions.Regex.Replace(lower, $@"\b{abbr}\b", canonical));
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\b{canonical}\b"))
                Add(System.Text.RegularExpressions.Regex.Replace(lower, $@"\b{canonical}\b", abbr));
        }

        // Tokenize, drop stopwords/short tokens — try the two most substantive then the single most salient.
        var sb = new System.Text.StringBuilder(term.Length);
        foreach (var ch in term) sb.Append(char.IsLetterOrDigit(ch) || ch == ' ' ? ch : ' ');
        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3 && !SearchStopWords.Contains(t))
            .Select(t =>
            {
                var lo = t.ToLowerInvariant();
                var hit = SearchSynonyms.FirstOrDefault(s => s.abbr == lo);
                return hit.canonical ?? t;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count >= 2) Add(string.Join(" ", tokens.Take(2)));
        if (tokens.Count >= 1) Add(tokens.First());

        return attempts;
    }

    private async Task<List<PolymarketMarketDto>> FetchSearchDtosAsync(MarketDiscoveryQuery query, CancellationToken ct)
    {
        // /public-search returns events with nested markets; pull a wide page and filter/sort in-memory.
        var fetchLimit = Math.Max(query.Take * 4, 100);
        var attempts = BuildSearchAttempts(query.SearchTerm!);
        var seenEventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<PolymarketSearchEvent>();

        foreach (var attempt in attempts)
        {
            var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/public-search?q={Uri.EscapeDataString(attempt)}&limit={fetchLimit}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Polymarket public-search failed for '{Q}': {Status}", attempt, resp.StatusCode);
                continue;
            }
            var payload = await resp.Content.ReadFromJsonAsync<PolymarketSearchResponse>(cancellationToken: ct);
            foreach (var e in payload?.Events ?? new())
            {
                if (e.Id is null || !seenEventIds.Add(e.Id)) continue;
                events.Add(e);
            }
            // Stop early once we have enough candidates to satisfy the page.
            if (events.Count >= fetchLimit) break;
        }

        // Series-aware fallback: Polymarket hides recurring/hourly events from public-search and
        // marks them with `hide-from-new`. When the user searches for a series title (e.g.
        // "BTC Up or Down Hourly") we resolve it via /series and fetch its current events.
        var seriesEvents = await FetchSeriesEventsAsync(query.SearchTerm!, fetchLimit, ct);
        foreach (var e in seriesEvents)
        {
            if (e.Id is null || !seenEventIds.Add(e.Id)) continue;
            events.Add(e);
        }

        var flattened = new List<PolymarketMarketDto>();
        foreach (var e in events)
        {
            if (e.Markets is null) continue;
            // The user has explicitly searched, so we no longer filter `hide-from-new` — that flag
            // is meant for the default "what's new" view, not for direct lookups. Without this,
            // recurring series (hourly Bitcoin Up/Down, etc.) would never surface even when asked for.
            foreach (var m in e.Markets)
            {
                // public-search markets often lack volume24hr / liquidity / icon — fall back to event-level.
                m.Volume24hr ??= e.Volume24hr;
                m.Liquidity ??= e.Liquidity?.ToString(CultureInfo.InvariantCulture);
                m.Icon ??= e.Icon;
                m.Image ??= e.Image;
                m.StartDate ??= e.StartDate;
                if (m.Events is null && e.Id is not null)
                    m.Events = new List<PolymarketEventRef> { new() { Id = e.Id, Slug = e.Slug, Title = e.Title } };
                flattened.Add(m);
            }
        }

        IEnumerable<PolymarketMarketDto> filtered = flattened
            .Where(m => m.Active == true && m.Closed != true);

        if (!query.IncludeClosed)
        {
            var now = DateTimeOffset.UtcNow;
            filtered = filtered.Where(m => !DateTimeOffset.TryParse(m.EndDate, out var d) || d > now);
        }

        if (query.MinVolume is not null)
            filtered = filtered.Where(m => ParseDecimal(m.Volume) >= query.MinVolume.Value);
        if (query.ResolvesBefore is not null)
            filtered = filtered.Where(m => DateTimeOffset.TryParse(m.EndDate, out var d) && d <= query.ResolvesBefore.Value);
        if (query.ResolvesAfter is not null)
            filtered = filtered.Where(m => DateTimeOffset.TryParse(m.EndDate, out var d) && d >= query.ResolvesAfter.Value);

        filtered = query.Sort switch
        {
            MarketDiscoverySort.Volume => filtered.OrderByDescending(m => ParseDecimal(m.Volume) ?? 0m),
            MarketDiscoverySort.Volume24h => filtered.OrderByDescending(m => m.Volume24hr ?? 0m),
            MarketDiscoverySort.Liquidity => filtered.OrderByDescending(m => ParseDecimal(m.Liquidity) ?? 0m),
            MarketDiscoverySort.EndDate => filtered.OrderBy(m => DateTimeOffset.TryParse(m.EndDate, out var d) ? d : DateTimeOffset.MaxValue),
            MarketDiscoverySort.Newest => filtered.OrderByDescending(m => DateTimeOffset.TryParse(m.StartDate, out var d) ? d : DateTimeOffset.MinValue),
            _ => filtered.OrderByDescending(m => m.Volume24hr ?? 0m)
        };

        return filtered.Skip(query.Skip).Take(query.Take).ToList();
    }

    private static decimal? ParseDecimal(string? s)
        => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private async Task<List<PolymarketSearchEvent>> FetchSeriesEventsAsync(string searchTerm, int fetchLimit, CancellationToken ct)
    {
        // Polymarket has a Series concept (e.g. "BTC Up or Down Hourly") whose events public-search
        // ignores. We resolve the series by slug or by listing all and substring-matching the title,
        // then fetch its active events via /events?series_id={id}.
        var slugCandidate = SlugifyForSearch(searchTerm);
        var matchedSeries = new List<PolymarketSeriesDto>();

        // 1) Direct slug hit — exact match if the user typed a known series name.
        try
        {
            using var resp = await _http.GetAsync($"{_opts.GammaBaseUrl.TrimEnd('/')}/series?slug={Uri.EscapeDataString(slugCandidate)}", ct);
            if (resp.IsSuccessStatusCode)
            {
                var direct = await resp.Content.ReadFromJsonAsync<List<PolymarketSeriesDto>>(cancellationToken: ct);
                if (direct is not null) matchedSeries.AddRange(direct.Where(s => s.Id is not null));
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Series slug lookup failed."); }

        // 2) Title substring match — list active series and filter in-memory. Polymarket has ~100s
        // of series total so a single 200-limit page is enough.
        if (matchedSeries.Count == 0)
        {
            try
            {
                using var resp = await _http.GetAsync($"{_opts.GammaBaseUrl.TrimEnd('/')}/series?active=true&closed=false&limit=200", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var all = await resp.Content.ReadFromJsonAsync<List<PolymarketSeriesDto>>(cancellationToken: ct) ?? new();
                    var termTokens = SubstantiveSearchTokens(searchTerm).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (termTokens.Count > 0)
                    {
                        matchedSeries.AddRange(all.Where(s =>
                        {
                            var title = s.Title ?? string.Empty;
                            var titleTokens = SubstantiveSearchTokens(title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            // Require all user tokens to be present in the series title — strict
                            // enough to avoid noise, lenient enough to handle BTC/Bitcoin synonyms
                            // (those are already normalized into termTokens upstream).
                            return termTokens.IsSubsetOf(titleTokens);
                        }).Take(3));
                    }
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Series title lookup failed."); }
        }

        if (matchedSeries.Count == 0) return new();

        // 3) For each matched series, fetch its active events.
        var results = new List<PolymarketSearchEvent>();
        foreach (var s in matchedSeries)
        {
            try
            {
                var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/events?series_id={Uri.EscapeDataString(s.Id!)}&active=true&closed=false&limit={fetchLimit}";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var evts = await resp.Content.ReadFromJsonAsync<List<PolymarketSearchEvent>>(cancellationToken: ct);
                if (evts is not null) results.AddRange(evts);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Series {Id} events fetch failed.", s.Id); }
        }
        return results;
    }

    private static string SlugifyForSearch(string term)
    {
        // Lowercase + replace any non-alphanumeric run with a single hyphen — matches Polymarket
        // slug shape (e.g. "BTC Up or Down Hourly" → "btc-up-or-down-hourly").
        var lower = term.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastHyphen = true;
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastHyphen = false; }
            else if (!lastHyphen) { sb.Append('-'); lastHyphen = true; }
        }
        return sb.ToString().Trim('-');
    }

    private static IEnumerable<string> SubstantiveSearchTokens(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(char.IsLetterOrDigit(ch) || ch == ' ' ? ch : ' ');
        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3 && !SearchStopWords.Contains(t));
    }

    public async Task<Market?> GetMarketAsync(string externalId, CancellationToken ct)
    {
        var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(externalId)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var dto = await resp.Content.ReadFromJsonAsync<PolymarketMarketDto>(cancellationToken: ct);
        return dto is null ? null : MapToDomain(dto);
    }

    public async Task<MarketDiscoveryResult?> GetMarketRichAsync(string externalId, CancellationToken ct)
    {
        var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(externalId)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var dto = await resp.Content.ReadFromJsonAsync<PolymarketMarketDto>(cancellationToken: ct);
        if (dto is null) return null;

        var (yes, no) = ParsePrices(dto.OutcomePrices);
        decimal? yesP = yes == 0.5m && no == 0.5m && string.IsNullOrWhiteSpace(dto.OutcomePrices) ? null : yes;
        decimal? noP = yes == 0.5m && no == 0.5m && string.IsNullOrWhiteSpace(dto.OutcomePrices) ? null : no;
        return new MarketDiscoveryResult(MapToDomain(dto), dto.Image, dto.Icon, yesP, noP, ParseDecimal(dto.Volume), dto.Volume24hr, ParseDecimal(dto.Liquidity));
    }

    public async Task<MarketPrice> GetCurrentPriceAsync(string externalId, CancellationToken ct)
    {
        var market = await GetMarketAsync(externalId, ct)
            ?? throw new InvalidOperationException($"Polymarket market '{externalId}' not found.");

        // Gamma response contains 'outcomePrices' (JSON-encoded string array of YES/NO probabilities 0..1).
        // Re-fetch raw DTO to read it.
        var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(externalId)}";
        using var resp = await _http.GetAsync(url, ct);
        var dto = await resp.Content.ReadFromJsonAsync<PolymarketMarketDto>(cancellationToken: ct)
                  ?? throw new InvalidOperationException("Polymarket price unavailable.");
        var (yes, no) = ParsePrices(dto.OutcomePrices);
        var volume = decimal.TryParse(dto.Volume, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd) ? vd : 0m;
        return new MarketPrice(market.Id, yes, no, volume, OpenInterest: 0m, DateTimeOffset.UtcNow);
    }

    public Task<IReadOnlyList<MarketPrice>> GetPriceHistoryAsync(string externalId, DateTimeOffset since, CancellationToken ct)
    {
        // Gamma history endpoint requires CLOB token id; surface empty until Phase 1 finishes the CLOB integration.
        return Task.FromResult<IReadOnlyList<MarketPrice>>(Array.Empty<MarketPrice>());
    }

    /// <summary>
    /// Best-effort recurring-market window fetch. Returns null when Polymarket data is not
    /// available (offline, market not found, series not matched). The venue price store falls
    /// back to a synthetic-flat row when this returns null.
    /// </summary>
    public Task<VenueMarketWindow?> GetRecurringMarketWindowAsync(
        string symbol, string interval, long targetOpenTimeMs, CancellationToken ct)
    {
        // Best-effort: returning null is explicitly acceptable — the store synthesises a fallback.
        // A full implementation would query /series + /events by (symbol, interval, targetOpenTimeMs).
        _logger.LogDebug("GetRecurringMarketWindowAsync: no live Polymarket data available; falling back to synthetic.");
        return Task.FromResult<VenueMarketWindow?>(null);
    }

    /// <summary>
    /// Best-effort point-in-time price fetch. Returns null when Polymarket data is not available.
    /// </summary>
    public Task<VenuePriceQuote?> GetOddsAtAsync(string marketExternalId, long atMs, CancellationToken ct)
    {
        _logger.LogDebug("GetOddsAtAsync: no live Polymarket data available for {MarketId}; falling back to synthetic.", marketExternalId);
        return Task.FromResult<VenuePriceQuote?>(null);
    }

    public async Task<IReadOnlyList<MarketHistoryPoint>> GetPriceSeriesAsync(string externalId, string interval, CancellationToken ct)
    {
        var tokenId = await GetYesTokenIdAsync(externalId, ct);
        if (tokenId is null) return Array.Empty<MarketHistoryPoint>();

        var safeInterval = interval switch { "1h" or "6h" or "1d" or "max" => interval, _ => "1d" };
        var url = $"{_opts.ClobBaseUrl.TrimEnd('/')}/prices-history?market={Uri.EscapeDataString(tokenId)}&interval={safeInterval}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("Polymarket history failed: {Status}", resp.StatusCode);
            return Array.Empty<MarketHistoryPoint>();
        }
        var payload = await resp.Content.ReadFromJsonAsync<PolymarketHistoryResponse>(cancellationToken: ct);
        var pts = payload?.History ?? new();
        return pts
            .Where(p => p.T is not null && p.P is not null)
            .Select(p => new MarketHistoryPoint(DateTimeOffset.FromUnixTimeSeconds(p.T!.Value), p.P!.Value))
            .ToList();
    }

    private async Task<string?> GetYesTokenIdAsync(string externalId, CancellationToken ct)
    {
        var url = $"{_opts.GammaBaseUrl.TrimEnd('/')}/markets/{Uri.EscapeDataString(externalId)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var dto = await resp.Content.ReadFromJsonAsync<PolymarketMarketDto>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(dto?.ClobTokenIds)) return null;
        try
        {
            using var doc = JsonDocument.Parse(dto.ClobTokenIds);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() >= 1)
                return doc.RootElement[0].GetString();
        }
        catch { }
        return null;
    }

    private static Market MapToDomain(PolymarketMarketDto dto, string? categoryOverride = null)
    {
        var question = dto.Question ?? dto.Slug ?? "(unknown)";
        var category = categoryOverride ?? dto.TagSlug ?? dto.Category ?? "general";
        var status = dto.Closed == true
            ? MarketStatus.ResolvedYes
            : MarketStatus.Open;
        return new Market
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Empty,
            ProviderId = "polymarket",
            ExternalId = dto.Id ?? dto.Slug ?? "(unknown)",
            Question = question,
            Category = category,
            CreatedAt = DateTimeOffset.UtcNow,
            ResolvesAt = DateTimeOffset.TryParse(dto.EndDate, out var ed) ? ed : null,
            Status = status,
            ResolutionCriteria = dto.Description
        };
    }

    private static (decimal yes, decimal no) ParsePrices(string? outcomePrices)
    {
        if (string.IsNullOrWhiteSpace(outcomePrices)) return (0.5m, 0.5m);
        try
        {
            using var doc = JsonDocument.Parse(outcomePrices);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() >= 2)
            {
                var yes = decimal.Parse(doc.RootElement[0].GetString() ?? "0.5", CultureInfo.InvariantCulture);
                var no = decimal.Parse(doc.RootElement[1].GetString() ?? "0.5", CultureInfo.InvariantCulture);
                return (yes, no);
            }
        }
        catch { }
        return (0.5m, 0.5m);
    }

    private sealed class PolymarketMarketDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("question")] public string? Question { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("tagSlug")] public string? TagSlug { get; set; }
        [JsonPropertyName("startDate")] public string? StartDate { get; set; }
        [JsonPropertyName("endDate")] public string? EndDate { get; set; }
        [JsonPropertyName("closed")] public bool? Closed { get; set; }
        [JsonPropertyName("active")] public bool? Active { get; set; }
        [JsonPropertyName("volume")] public string? Volume { get; set; }
        [JsonPropertyName("volume24hr")] public decimal? Volume24hr { get; set; }
        [JsonPropertyName("liquidity")] public string? Liquidity { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("outcomePrices")] public string? OutcomePrices { get; set; }
        [JsonPropertyName("clobTokenIds")] public string? ClobTokenIds { get; set; }
        [JsonPropertyName("events")] public List<PolymarketEventRef>? Events { get; set; }
    }

    private sealed class PolymarketEventRef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    private sealed class PolymarketSearchResponse
    {
        [JsonPropertyName("events")] public List<PolymarketSearchEvent>? Events { get; set; }
    }

    private sealed class PolymarketSearchEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("volume24hr")] public decimal? Volume24hr { get; set; }
        [JsonPropertyName("liquidity")] public decimal? Liquidity { get; set; }
        [JsonPropertyName("startDate")] public string? StartDate { get; set; }
        [JsonPropertyName("tags")] public List<PolymarketTagDto>? Tags { get; set; }
        [JsonPropertyName("markets")] public List<PolymarketMarketDto>? Markets { get; set; }
    }

    private sealed class PolymarketEventDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tags")] public List<PolymarketTagDto>? Tags { get; set; }
    }

    private sealed class PolymarketTagDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
    }

    private sealed class PolymarketSeriesDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    private sealed class PolymarketHistoryResponse
    {
        [JsonPropertyName("history")] public List<PolymarketHistoryPoint>? History { get; set; }
    }

    private sealed class PolymarketHistoryPoint
    {
        [JsonPropertyName("t")] public long? T { get; set; }
        [JsonPropertyName("p")] public decimal? P { get; set; }
    }
}
