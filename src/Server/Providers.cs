using System.Text.Json.Serialization;

namespace PortfolioWeb;

// ===========================================================================
// FX - open.er-api.com
// ===========================================================================

/// <summary>
/// The FX table, oriented both ways. `UnitsPerUsd[X]` is how many X one USD
/// buys (the provider's own orientation); <see cref="UsdPerUnit"/> inverts it
/// into the quantity the rest of the app reasons in.
/// </summary>
public sealed class FxTable
{
    public IReadOnlyDictionary<string, decimal> UnitsPerUsd { get; }
    public DateTimeOffset? AsOfUtc { get; }
    public bool Stale { get; }
    public string? Error { get; }

    public FxTable(IReadOnlyDictionary<string, decimal> unitsPerUsd, DateTimeOffset? asOfUtc,
                   bool stale, string? error)
    {
        UnitsPerUsd = unitsPerUsd;
        AsOfUtc = asOfUtc;
        Stale = stale;
        Error = error;
    }

    public bool Available => UnitsPerUsd.Count > 0;

    /// <summary>How many USD one unit of `code` is worth.</summary>
    public decimal? UsdPerUnit(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToUpperInvariant();
        if (c == "USD") return 1m;
        return UnitsPerUsd.TryGetValue(c, out var per) && per > 0 ? 1m / per : null;
    }

    /// <summary>Units of `to` per one unit of `from`, or null if either is unknown.</summary>
    public decimal? Rate(string? from, string? to)
    {
        var a = UsdPerUnit(from);
        var b = UsdPerUnit(to);
        if (a is null || b is null || b == 0) return null;
        return a / b;
    }
}

public sealed class FxService
{
    public const string Host = "open.er-api.com";
    private const string LatestUsdUrl = "https://open.er-api.com/v6/latest/USD";
    private const string CacheKey = "fx:latest-usd";

    // The provider refreshes once a day, so asking more often only burns quota.
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);
    public static readonly TimeSpan StaleTtl = TimeSpan.FromDays(10);

    private readonly ProviderClient _client;

    /// <summary>
    /// Every code the app can convert to: whatever the last FX payload carried,
    /// plus the built-in ISO list. Seeded from the disk cache in the
    /// constructor and refreshed on every fetch so validation can answer
    /// synchronously — a write endpoint must never wait on the network to
    /// decide whether "ANG" is a currency. The built-in list is the floor, so a
    /// cold cache narrows what is accepted rather than inventing codes.
    /// </summary>
    private volatile IReadOnlySet<string> _known;

    public IReadOnlySet<string> KnownCodes => _known;

    public FxService(ProviderClient client, DiskCache cache)
    {
        _client = client;
        _known = KnownCodesFrom(cache.Get<FxPayload>(CacheKey).Value?.Rates);
    }

    private static IReadOnlySet<string> KnownCodesFrom(Dictionary<string, decimal>? rates)
    {
        var set = new HashSet<string>(Currencies.Names.Keys, StringComparer.Ordinal) { "USD" };
        if (rates is null) return set;
        foreach (var code in rates.Keys)
        {
            var c = code.Trim().ToUpperInvariant();
            if (c.Length > 0) set.Add(c);
        }
        return set;
    }

    private sealed record FxPayload(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("rates")] Dictionary<string, decimal>? Rates,
        [property: JsonPropertyName("time_last_update_unix")] long? UpdatedUnix);

    public async Task<FxTable> GetTableAsync(CancellationToken ct = default)
    {
        var res = await _client.GetJsonAsync<FxPayload>(
            LatestUsdUrl, CacheKey, Ttl, StaleTtl, Host,
            requestsPerMinute: 20, burst: 2, ct: ct).ConfigureAwait(false);

        var rates = res.Value?.Rates;
        if (rates is null || rates.Count == 0)
        {
            return new FxTable(new Dictionary<string, decimal>(), res.CachedAtUtc, res.Stale,
                               res.Error ?? "FX response had no rates");
        }
        if (res.Value?.Result is { } r && !string.Equals(r, "success", StringComparison.OrdinalIgnoreCase))
        {
            return new FxTable(new Dictionary<string, decimal>(), res.CachedAtUtc, res.Stale,
                               $"FX provider reported \"{r}\"");
        }

        var clean = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (code, per) in rates)
        {
            if (per <= 0 || string.IsNullOrWhiteSpace(code)) continue;
            clean[code.Trim().ToUpperInvariant()] = per;
        }
        _known = KnownCodesFrom(clean);

        var asOf = res.Value?.UpdatedUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(res.Value.UpdatedUnix.Value)
            : res.CachedAtUtc;

        return new FxTable(clean, asOf, res.Stale, res.Error);
    }
}

// ===========================================================================
// Crypto - api.coingecko.com
// ===========================================================================

public sealed record CoinMarket
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("image")] public string? Image { get; init; }
    [JsonPropertyName("current_price")] public decimal? CurrentPrice { get; init; }
    [JsonPropertyName("market_cap_rank")] public int? MarketCapRank { get; init; }
    [JsonPropertyName("price_change_percentage_24h")] public decimal? ChangePct24h { get; init; }
}

public sealed record CoinSearchHit
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; init; } = "";
    [JsonPropertyName("market_cap_rank")] public int? MarketCapRank { get; init; }
    [JsonPropertyName("thumb")] public string? Thumb { get; init; }
    [JsonPropertyName("large")] public string? Large { get; init; }
}

public sealed record CoinSearchPayload
{
    [JsonPropertyName("coins")] public List<CoinSearchHit>? Coins { get; init; }
    [JsonPropertyName("status")] public CoinErrorStatus? Status { get; init; }
}

public sealed record CoinErrorStatus
{
    [JsonPropertyName("error_message")] public string? Message { get; init; }
}

/// <summary>
/// Crypto, with the quota work done up front.
///
/// One call to /coins/markets returns the top 250 coins with their logo, name,
/// rank, live price and 24h change. That single cached response serves three
/// purposes - autocomplete matching, the icon in the results list, and prices -
/// so a session that only touches well-known coins makes exactly one CoinGecko
/// request. /search and /simple/price are fallbacks for the long tail.
/// </summary>
public sealed class CryptoService
{
    public const string Host = "api.coingecko.com";

    private const string MarketsUrl =
        "https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page=250&page=1&price_change_percentage=24h";
    private const string SearchUrlBase = "https://api.coingecko.com/api/v3/search?query=";
    private const string PriceUrlBase = "https://api.coingecko.com/api/v3/simple/price?vs_currency=usd&ids=";

    // Short enough that prices stay useful, long enough that repeated UI
    // refreshes cost nothing.
    public static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan IndexStaleTtl = TimeSpan.FromHours(36);
    public static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SearchStaleTtl = TimeSpan.FromDays(3);
    public static readonly TimeSpan PriceTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PriceStaleTtl = TimeSpan.FromHours(24);

    private readonly ProviderClient _client;
    private readonly ILogger<CryptoService> _log;

    public CryptoService(ProviderClient client, ILogger<CryptoService> log)
    {
        _client = client;
        _log = log;
    }

    /// <summary>The cached top-250 index. Never throws; empty list on failure.</summary>
    public async Task<IReadOnlyList<CoinMarket>> GetTopIndexAsync(CancellationToken ct = default)
    {
        var res = await _client.GetJsonAsync<List<CoinMarket>>(
            MarketsUrl, "crypto:top250", IndexTtl, IndexStaleTtl, Host,
            requestsPerMinute: 10, burst: 2, ct: ct).ConfigureAwait(false);

        if (res.Value is null)
        {
            if (res.Error is not null) _log.LogWarning("coin index unavailable: {Error}", res.Error);
            return [];
        }
        return res.Value;
    }

    /// <summary>
    /// Local match against the cached index: no request, so it is free to run
    /// on every keystroke. Exact symbol &gt; symbol prefix &gt; name prefix &gt;
    /// name substring, then by market cap rank.
    /// </summary>
    public async Task<List<CoinMarket>> SearchIndexAsync(string query, int take, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var index = await GetTopIndexAsync(ct).ConfigureAwait(false);
        if (index.Count == 0) return [];

        var q = query.Trim();
        var qu = q.ToUpperInvariant();

        static int Rank(CoinMarket c, string qu, string q)
        {
            var sym = c.Symbol.ToUpperInvariant();
            if (sym == qu) return 0;
            if (sym.StartsWith(qu, StringComparison.Ordinal)) return 1;
            if (c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 2;
            if (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
            if (c.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) return 4;
            return 9;
        }

        return index
            .Select(c => (Coin: c, Bucket: Rank(c, qu, q)))
            .Where(x => x.Bucket < 9)
            .OrderBy(x => x.Bucket)
            .ThenBy(x => x.Coin.MarketCapRank ?? int.MaxValue)
            .Take(take)
            .Select(x => x.Coin)
            .ToList();
    }

    /// <summary>
    /// Remote /search, used only for coins outside the top 250. Cached per
    /// query, and rate limited hard because this is the endpoint that 429s.
    /// </summary>
    public async Task<List<CoinSearchHit>> SearchRemoteAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = SearchUrlBase + Uri.EscapeDataString(query.Trim());
        var key = "crypto:search:" + query.Trim().ToUpperInvariant();

        // This endpoint is the one that 429s, so it is bucketed hard - and
        // because it sits behind a keystroke, it is also not allowed to make
        // the user wait for a token. When the bucket is empty the call returns
        // an error, and SearchService reports crypto as degraded rather than
        // hanging the autocomplete.
        var res = await _client.GetJsonAsync<CoinSearchPayload>(
            url, key, SearchTtl, SearchStaleTtl, Host,
            requestsPerMinute: 6, burst: 1, ct: ct,
            maxWait: TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);

        if (res.Value is null)
        {
            if (res.Error is not null) _log.LogDebug("coin search failed: {Error}", res.Error);
            return [];
        }
        if (res.Value.Status?.Message is { } msg)
        {
            _log.LogWarning("coin search reported: {Message}", msg);
            return [];
        }
        return res.Value.Coins ?? [];
    }

    /// <summary>Live USD price for one coin id straight from the cached index (no request).</summary>
    public async Task<decimal?> PriceFromIndexAsync(string coinId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coinId)) return null;
        var index = await GetTopIndexAsync(ct).ConfigureAwait(false);
        var hit = index.FirstOrDefault(c => string.Equals(c.Id, coinId, StringComparison.OrdinalIgnoreCase));
        return hit?.CurrentPrice is > 0 ? hit.CurrentPrice : null;
    }

    /// <summary>
    /// Batched prices for coin ids, one request for the whole set. Used for the
    /// long tail the index does not cover.
    /// </summary>
    public async Task<Dictionary<string, decimal>> GetPricesAsync(
        IEnumerable<string> coinIds, CancellationToken ct = default)
    {
        var ids = coinIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        // One key for the sorted id set, so the same portfolio always hits the
        // same cache entry regardless of the order holdings were added in.
        var joined = string.Join(",", ids.Select(Uri.EscapeDataString));
        var url = PriceUrlBase + joined;
        var key = "crypto:prices:" + string.Join(",", ids);

        var res = await _client.GetJsonAsync<Dictionary<string, Dictionary<string, decimal>>>(
            url, key, PriceTtl, PriceStaleTtl, Host,
            requestsPerMinute: 10, burst: 2, ct: ct).ConfigureAwait(false);

        var outMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (res.Value is null) return outMap;
        foreach (var (id, byCurrency) in res.Value)
        {
            if (byCurrency is null) continue;
            foreach (var (cur, price) in byCurrency)
            {
                if (string.Equals(cur, "usd", StringComparison.OrdinalIgnoreCase) && price > 0)
                {
                    outMap[id] = price;
                    break;
                }
            }
        }
        return outMap;
    }
}

// ===========================================================================
// Equities, indices, futures, FX pairs - Yahoo Finance (unofficial)
// ===========================================================================

public sealed record YahooSearchQuote
{
    [JsonPropertyName("symbol")] public string Symbol { get; init; } = "";
    [JsonPropertyName("shortname")] public string? ShortName { get; init; }
    [JsonPropertyName("longname")] public string? LongName { get; init; }
    [JsonPropertyName("exchange")] public string? Exchange { get; init; }
    [JsonPropertyName("exchDisp")] public string? ExchangeDisplay { get; init; }
    [JsonPropertyName("quoteType")] public string? QuoteType { get; init; }
    [JsonPropertyName("typeDisp")] public string? TypeDisplay { get; init; }
    [JsonPropertyName("sector")] public string? Sector { get; init; }
    [JsonPropertyName("industry")] public string? Industry { get; init; }
    [JsonPropertyName("score")] public double? Score { get; init; }
}

public sealed record YahooSearchPayload
{
    [JsonPropertyName("quotes")] public List<YahooSearchQuote>? Quotes { get; init; }
}

public sealed record YahooMeta
{
    [JsonPropertyName("symbol")] public string? Symbol { get; init; }
    [JsonPropertyName("currency")] public string? Currency { get; init; }
    [JsonPropertyName("fullExchangeName")] public string? FullExchangeName { get; init; }
    [JsonPropertyName("instrumentType")] public string? InstrumentType { get; init; }
    [JsonPropertyName("regularMarketPrice")] public decimal? RegularMarketPrice { get; init; }
    [JsonPropertyName("regularMarketTime")] public long? RegularMarketTime { get; init; }
}

public sealed record YahooChartError
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed record YahooChartResult
{
    [JsonPropertyName("meta")] public YahooMeta? Meta { get; init; }
}

public sealed record YahooChartBody
{
    [JsonPropertyName("result")] public List<YahooChartResult>? Result { get; init; }
    [JsonPropertyName("error")] public YahooChartError? Error { get; init; }
}

public sealed record YahooChartPayload
{
    [JsonPropertyName("chart")] public YahooChartBody? Chart { get; init; }
}

/// <summary>A quote in whatever currency the listing uses, plus its context.</summary>
public sealed record YahooQuoteInfo
{
    public decimal Price { get; init; }
    public string Currency { get; init; } = "USD";
    public string? InstrumentType { get; init; }
    public string? Exchange { get; init; }
    public DateTimeOffset? AsOfUtc { get; init; }
    public bool Stale { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error is null && Price > 0;
}

public static class YahooKinds
{
    /// <summary>
    /// Maps Yahoo's own type strings onto our kinds. Using the provider's
    /// classification is what makes auto-categorisation trustworthy: SPY is an
    /// ETF, ^GSPC an index, GC=F a future, EURUSD=X a currency - no guessing
    /// from the shape of the ticker.
    /// </summary>
    public static AssetKind? From(string? quoteType)
    {
        if (string.IsNullOrWhiteSpace(quoteType)) return null;
        return quoteType.Trim().ToUpperInvariant() switch
        {
            "EQUITY" => AssetKind.Stock,
            "MUTUALFUND" => AssetKind.Etf,
            "ETF" => AssetKind.Etf,
            "INDEX" => AssetKind.Index,
            "FUTURE" => AssetKind.Future,
            "CURRENCY" => AssetKind.Currency,
            "CRYPTOCURRENCY" => AssetKind.Crypto,
            _ => null   // OPTION and anything unrecognised are left out
        };
    }
}

public sealed class YahooService
{
    public const string Host = "query1.finance.yahoo.com";

    private const string SearchUrlBase =
        "https://query1.finance.yahoo.com/v1/finance/search?quotesCount=12&newsCount=0&listsCount=0&quotesQueryId=tss_match_phrase_query&q=";
    private const string ChartUrlBase = "https://query1.finance.yahoo.com/v8/finance/chart/";

    // Search results barely change; prices do, but a 60s floor keeps a page of
    // holdings from turning into a burst of requests.
    public static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SearchStaleTtl = TimeSpan.FromDays(3);
    public static readonly TimeSpan PriceTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PriceStaleTtl = TimeSpan.FromHours(30);

    private readonly ProviderClient _client;
    private readonly ILogger<YahooService> _log;

    public YahooService(ProviderClient client, ILogger<YahooService> log)
    {
        _client = client;
        _log = log;
    }

    public async Task<List<YahooSearchQuote>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        var url = SearchUrlBase + Uri.EscapeDataString(q);
        var key = "yahoo:search:" + q.ToUpperInvariant();

        var res = await _client.GetJsonAsync<YahooSearchPayload>(
            url, key, SearchTtl, SearchStaleTtl, Host,
            requestsPerMinute: 30, burst: 4, ct: ct).ConfigureAwait(false);

        if (res.Value?.Quotes is null)
        {
            if (res.Error is not null) _log.LogDebug("yahoo search failed: {Error}", res.Error);
            return [];
        }
        return res.Value.Quotes;
    }

    /// <summary>
    /// One symbol's quote. Yahoo's v7 batch endpoint needs a crumb/cookie
    /// handshake and answers 401 without it, so this is per symbol - which is
    /// exactly why the cache TTL and the token bucket matter here.
    /// </summary>
    public async Task<YahooQuoteInfo> QuoteAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return new YahooQuoteInfo { Error = "empty symbol" };

        var url = ChartUrlBase + Uri.EscapeDataString(symbol.Trim()) + "?interval=1d&range=1d";
        var key = "yahoo:chart:" + symbol.Trim().ToUpperInvariant();

        var res = await _client.GetJsonAsync<YahooChartPayload>(
            url, key, PriceTtl, PriceStaleTtl, Host,
            requestsPerMinute: 40, burst: 5, ct: ct).ConfigureAwait(false);

        if (res.Value?.Chart is null)
            return new YahooQuoteInfo { Error = res.Error ?? "no chart payload", Stale = res.Stale };

        var chart = res.Value.Chart;
        var meta = chart.Result is { Count: > 0 } ? chart.Result[0].Meta : null;
        var price = meta?.RegularMarketPrice;

        if (meta is null || price is null)
        {
            var err = chart.Error;
            var text = err is null
                ? "no quote in response"
                : string.IsNullOrWhiteSpace(err.Description)
                    ? err.Code ?? "quote unavailable"
                    : $"{err.Code}: {err.Description}";
            return new YahooQuoteInfo { Error = text, Stale = res.Stale };
        }
        if (price <= 0)
            return new YahooQuoteInfo { Error = "quote price is not usable", Stale = res.Stale };

        return new YahooQuoteInfo
        {
            Price = price.Value,
            Currency = string.IsNullOrWhiteSpace(meta.Currency) ? "USD" : meta.Currency.Trim().ToUpperInvariant(),
            InstrumentType = meta.InstrumentType,
            Exchange = meta.FullExchangeName,
            AsOfUtc = meta.RegularMarketTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(meta.RegularMarketTime.Value)
                : res.CachedAtUtc,
            Stale = res.Stale,
            Error = res.Error
        };
    }
}
