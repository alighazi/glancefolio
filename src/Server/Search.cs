namespace PortfolioWeb;

// ===========================================================================
// Categories
// ===========================================================================

/// <summary>
/// Portfolio categories and the rules that pick one automatically.
///
/// Auto-detection leans on the provider's own classification rather than on the
/// shape of a ticker: Yahoo says whether something is an EQUITY, ETF, INDEX,
/// FUTURE or CURRENCY, and CoinGecko says whether it is a coin. Two judgement
/// calls are layered on top, both visible in the UI and both overridable:
///
///   - tokenised metals (PAX Gold, Tether Gold) are Crypto by provider but are
///     economically a commodity position, so they land in Commodities;
///   - fiat-pegged stablecoins are Crypto by provider but are cash equivalents,
///     so they land in Cash.
/// </summary>
public static class Categories
{
    public const string Cash = "Cash";
    public const string Crypto = "Crypto";
    public const string Stocks = "Stocks";
    public const string Funds = "ETFs & Funds";
    public const string Commodities = "Commodities";
    public const string Indices = "Indices";
    public const string RealEstate = "Real Estate";
    public const string Bonds = "Bonds";
    public const string Collectibles = "Collectibles";
    public const string Other = "Other";

    /// <summary>Order matters: this is the order the report lists groups in.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Cash, Crypto, Stocks, Funds, Commodities, Indices, Bonds, RealEstate, Collectibles, Other
    ];

    /// <summary>Categories only a human would pick - never auto-detected.</summary>
    public static readonly IReadOnlyList<string> ManualOnly = [RealEstate, Bonds, Collectibles, Other];

    /// <summary>
    /// What a holding is filed under when it matches no category in the list.
    /// Better than quietly misfiling it: the report shows the group, says how
    /// many holdings are in it, and the fix is obvious.
    /// </summary>
    public const string Uncategorised = "Uncategorised";

    // Detection keys. These are stable identifiers, unlike display names, which
    // the user may rename - so a renamed "Crypto" still receives coins.
    public const string KCash = "cash";
    public const string KCrypto = "crypto";
    public const string KStocks = "stocks";
    public const string KFunds = "funds";
    public const string KCommodities = "commodities";
    public const string KIndices = "indices";
    public const string KOther = "other";

    /// <summary>Every key auto-detection can produce, for validation.</summary>
    public static readonly IReadOnlyList<string> DetectKeys =
        [KCash, KCrypto, KStocks, KFunds, KCommodities, KIndices, KOther];

    /// <summary>The starting list, in report order. Restorable from Settings.</summary>
    public static readonly IReadOnlyList<CategoryDef> Defaults =
    [
        new CategoryDef { Name = Cash, Detect = KCash },
        new CategoryDef { Name = Crypto, Detect = KCrypto },
        new CategoryDef { Name = Stocks, Detect = KStocks },
        new CategoryDef { Name = Funds, Detect = KFunds },
        new CategoryDef { Name = Commodities, Detect = KCommodities },
        new CategoryDef { Name = Indices, Detect = KIndices },
        new CategoryDef { Name = Bonds, Detect = null },
        new CategoryDef { Name = RealEstate, Detect = null },
        new CategoryDef { Name = Collectibles, Detect = null },
        new CategoryDef { Name = Other, Detect = KOther },
    ];

    /// <summary>The detection key a default name carries, if it is one.</summary>
    public static string? DefaultKeyFor(string name) =>
        Defaults.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Detect;

    /// <summary>A fresh copy of the defaults, so callers cannot mutate the static.</summary>
    public static List<CategoryDef> DefaultList() =>
        Defaults.Select(d => new CategoryDef { Name = d.Name, Detect = d.Detect }).ToList();

    private static readonly HashSet<string> ManualOnlySet = new(ManualOnly, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when nothing but a human would ever pick this category.</summary>
    public static bool IsManualOnly(string name) => ManualOnlySet.Contains(name);

    private static readonly HashSet<string> MetalCoinIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "pax-gold", "tether-gold", "kinesis-gold", "kinesis-silver",
        "perth-mint-gold-token", "meld-gold", "gold-coin-2"
    };

    private static readonly HashSet<string> MetalSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAXG", "XAUT", "KAU", "KAG", "PMGT", "XAUTON"
    };

    private static readonly HashSet<string> StablecoinIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "tether", "usd-coin", "dai", "frax", "true-usd", "binance-usd", "paxos-standard",
        "gemini-dollar", "ethena-usde", "usds", "ripple-usd", "usd1", "tether-eurt",
        "first-digital-usd", "usdd", "nusd", "usdp", "liquity-usd", "magic-internet-money",
        "tether-gold", // excluded below by the metal rule winning first
        "usdt0", "paypal-usd", "global-dollar", "resolv-usr", "falcon-usd"
    };

    private static readonly HashSet<string> StablecoinSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "DAI", "FRAX", "TUSD", "BUSD", "USDP", "GUSD", "USDE", "USDS",
        "RLUSD", "USD1", "EURT", "FDUSD", "USDD", "USDL", "PYUSD", "USR", "USD0", "USDX"
    };

    public static bool IsStablecoin(string? assetId, string? symbol) =>
        (assetId is not null && StablecoinIds.Contains(assetId) && !MetalCoinIds.Contains(assetId)) ||
        (symbol is not null && StablecoinSymbols.Contains(symbol));

    public static bool IsTokenisedMetal(string? assetId, string? symbol) =>
        (assetId is not null && MetalCoinIds.Contains(assetId)) ||
        (symbol is not null && MetalSymbols.Contains(symbol));

    /// <summary>
    /// What kind of thing an asset is, as a stable key rather than a display
    /// name - the user can rename their categories, and detection has to keep
    /// working when they do.
    /// </summary>
    public static string AutoDetectKey(AssetKind kind, string? assetId = null, string? symbol = null) => kind switch
    {
        AssetKind.Currency => KCash,
        AssetKind.Crypto when IsTokenisedMetal(assetId, symbol) => KCommodities,
        AssetKind.Crypto when IsStablecoin(assetId, symbol) => KCash,
        AssetKind.Crypto => KCrypto,
        AssetKind.Stock => KStocks,
        AssetKind.Etf => KFunds,
        AssetKind.Index => KIndices,
        AssetKind.Future => KCommodities,
        _ => KOther
    };

    /// <summary>The default display name for a detection key.</summary>
    public static string NameOfKey(string key) =>
        Defaults.FirstOrDefault(d => string.Equals(d.Detect, key, StringComparison.Ordinal))?.Name ?? Other;

    /// <summary>
    /// The category a holding gets when the user does not choose one, expressed
    /// with the out-of-the-box names. The report uses the list-aware
    /// <see cref="Effective(Holding, IReadOnlyList{CategoryDef})"/> instead.
    /// </summary>
    public static string AutoDetect(AssetKind kind, string? assetId = null, string? symbol = null) =>
        NameOfKey(AutoDetectKey(kind, assetId, symbol));

    /// <summary>Effective category against the default list.</summary>
    public static string Effective(Holding h) => Effective(h, Defaults);

    /// <summary>
    /// The category a holding is reported under, given the user's list:
    ///
    ///   1. the user's own choice, when it still names a category in the list;
    ///   2. otherwise what the asset is detected as, matched on the detection
    ///      key so a renamed "Crypto" still receives coins, and a holding left
    ///      pointing at a deleted name self-heals instead of vanishing;
    ///   3. otherwise <see cref="Uncategorised"/>, which the report surfaces.
    ///      Silently refiling gold into "Cash" because someone deleted a
    ///      category would be worse than admitting the gap.
    ///
    /// An unrecognised choice counts as "not chosen", so a typo cannot invent a
    /// group and cannot hide a stock inside a catch-all bucket.
    /// </summary>
    /// <summary>
    /// What detection alone would say about this holding, ignoring any hand
    /// pick. The UI shows this next to the "Auto" choice so an override can be
    /// seen against what it overrode.
    /// </summary>
    public static string Detected(Holding h, IReadOnlyList<CategoryDef>? categories = null)
    {
        var list = categories is { Count: > 0 } ? categories : Defaults;
        var key = AutoDetectKey(h.Kind, h.AssetId, h.Symbol);

        // Whatever category used to receive this asset type may have been
        // deleted or renamed away from it. Rather than guess, the row is
        // reported as uncategorised and the report says so.
        var detected = list.FirstOrDefault(c => string.Equals(c.Detect, key, StringComparison.Ordinal));
        return detected?.Name ?? Uncategorised;
    }

    public static string Effective(Holding h, IReadOnlyList<CategoryDef> categories)
    {
        var list = categories is { Count: > 0 } ? categories : Defaults;

        // A hand pick wins as long as the category still exists.
        var chosen = h.Category?.Trim();
        if (!string.IsNullOrEmpty(chosen))
        {
            var match = list.FirstOrDefault(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Name;
        }

        return Detected(h, list);
    }
}

// ===========================================================================
// Search
// ===========================================================================

/// <summary>
/// Merges the three free sources into one ranked list.
///
/// The interesting part is what it does *not* do: it does not collapse a
/// collision. "PAXG" legitimately matches a Nasdaq equity and a tokenised gold
/// coin, so both appear, each with its own kind, provider and icon, and the
/// human picks. Every result carries the provider-native id, so the choice made
/// here is the choice used later for pricing - nothing has to be re-guessed.
/// </summary>
public sealed class SearchService
{
    private readonly CryptoService _crypto;
    private readonly YahooService _yahoo;
    private readonly FxService _fx;
    private readonly ILogger<SearchService> _log;

    public SearchService(CryptoService crypto, YahooService yahoo, FxService fx, ILogger<SearchService> log)
    {
        _crypto = crypto;
        _yahoo = yahoo;
        _fx = fx;
        _log = log;
    }

    public async Task<SearchResponse> SearchAsync(string query, int take = 12, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var q = (query ?? "").Trim();
        var degraded = new List<string>();
        var sources = new List<string>();

        take = Math.Clamp(take, 1, 40);

        var fx = await _fx.GetTableAsync(ct).ConfigureAwait(false);
        if (!fx.Available)
            degraded.Add("currency rates are unavailable, so cash items may show no price");

        // An empty query is a browse gesture: show the most useful starting
        // points rather than an empty sheet.
        if (q.Length == 0)
        {
            var browse = new List<SearchItem>();
            var top = await _crypto.GetTopIndexAsync(ct).ConfigureAwait(false);
            if (top.Count > 0) sources.Add("coingecko");
            browse.AddRange(top.Take(6).Select(c => ToItem(c, fx)));
            foreach (var code in new[] { "USD", "EUR", "GBP", "JPY", "CHF" })
                browse.Add(CurrencyItem(code, fx));
            return new SearchResponse
            {
                Items = browse.Take(take).ToList(),
                Sources = sources,
                Degraded = degraded,
                FromCache = true,
                ElapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds
            };
        }

        // Local matches first - they cost nothing and cover the common case.
        var coinMatches = await _crypto.SearchIndexAsync(q, take, ct).ConfigureAwait(false);
        if (coinMatches.Count > 0) sources.Add("coingecko");
        var currencyMatches = MatchCurrencies(q, fx, take).ToList();
        if (currencyMatches.Count > 0) sources.Add("fx-table");

        // Network sources run concurrently; each is independently cached.
        var yahooTask = SafeAsync(() => _yahoo.SearchAsync(q, ct), "yahoo", degraded, sources);
        // Only spend a rate-limited /search call when the local index cannot
        // answer - i.e. the user is looking for something outside the top 250.
        var remoteCoinsTask = coinMatches.Count < 3 && q.Length >= 2
            ? SafeAsync(() => _crypto.SearchRemoteAsync(q, ct), "coingecko-search", degraded, sources)
            : Task.FromResult(new List<CoinSearchHit>());

        var yahooQuotes = await yahooTask.ConfigureAwait(false);
        var remoteCoins = await remoteCoinsTask.ConfigureAwait(false);

        var items = new List<SearchItem>();
        var seenCoinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in coinMatches)
        {
            items.Add(ToItem(c, fx));
            seenCoinIds.Add(c.Id);
        }
        foreach (var c in remoteCoins.Take(6))
        {
            if (string.IsNullOrWhiteSpace(c.Id) || seenCoinIds.Contains(c.Id)) continue;
            items.Add(ToItem(c, fx));
            seenCoinIds.Add(c.Id);
        }
        items.AddRange(currencyMatches);

        foreach (var y in yahooQuotes)
        {
            var kind = YahooKinds.From(y.QuoteType);
            if (kind is null || string.IsNullOrWhiteSpace(y.Symbol)) continue;

            // Yahoo also lists crypto (as "PAXG-USD"). CoinGecko wins there
            // because it brings the logo, the rank and a price in one call.
            if (kind == AssetKind.Crypto)
            {
                var baseSym = StripCryptoSuffix(y.Symbol);
                if (coinMatches.Any(c => string.Equals(c.Symbol, baseSym, StringComparison.OrdinalIgnoreCase)) ||
                    remoteCoins.Any(c => string.Equals(c.Symbol, baseSym, StringComparison.OrdinalIgnoreCase)))
                    continue;
                items.Add(new SearchItem
                {
                    Kind = AssetKind.Crypto,
                    Id = y.Symbol,          // Yahoo's own pair symbol is the id
                    Symbol = baseSym,
                    Name = FirstNonEmpty(y.LongName, y.ShortName) ?? baseSym,
                    Exchange = y.ExchangeDisplay ?? y.Exchange,
                    IconUrl = null,
                    Source = "yahoo",
                    Category = Categories.AutoDetect(AssetKind.Crypto, null, baseSym)
                });
                continue;
            }

            items.Add(new SearchItem
            {
                Kind = kind.Value,
                Id = y.Symbol,
                Symbol = y.Symbol,
                Name = FirstNonEmpty(y.LongName, y.ShortName) ?? y.Symbol,
                Exchange = y.ExchangeDisplay ?? y.Exchange,
                Sector = NullIfEmpty(y.Sector),
                Industry = NullIfEmpty(y.Industry),
                IconUrl = null,
                Source = "yahoo",
                Category = Categories.AutoDetect(kind.Value, y.Symbol, y.Symbol)
            });
        }

        var ranked = Rank(items, q).Take(take).ToList();

        return new SearchResponse
        {
            Items = ranked,
            Sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Degraded = degraded.Distinct(StringComparer.Ordinal).ToList(),
            ElapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds
        };
    }

    // ---------------------------------------------------------------- helpers

    private async Task<List<T>> SafeAsync<T>(Func<Task<List<T>>> call, string label,
                                             List<string> degraded, List<string> sources)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            if (result.Count > 0) lock (sources) sources.Add(label);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Label} search failed", label);
            lock (degraded) degraded.Add($"{label} search failed: {ex.Message}");
            return [];
        }
    }

    private static string StripCryptoSuffix(string symbol)
    {
        var i = symbol.IndexOf('-');
        return i > 0 ? symbol[..i] : symbol;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private IEnumerable<SearchItem> MatchCurrencies(string q, FxTable fx, int take)
    {
        var candidates = Currencies.Search(q, take);

        // Also match codes that the FX table knows but the bundled name list
        // does not, so an obscure currency is still findable by its code.
        if (fx.Available)
        {
            var qu = q.Trim().ToUpperInvariant();
            var extra = fx.UnitsPerUsd.Keys
                .Where(k => k.StartsWith(qu, StringComparison.Ordinal) && !Currencies.Names.ContainsKey(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .Take(3);
            candidates = candidates.Concat(extra);
        }

        return candidates.Distinct().Take(take).Select(code => CurrencyItem(code, fx));
    }

    private static SearchItem CurrencyItem(string code, FxTable fx) => new()
    {
        Kind = AssetKind.Currency,
        Id = code,
        Symbol = code,
        Name = Currencies.NameOf(code),
        IconUrl = null,
        PriceUsd = fx.UsdPerUnit(code),
        Source = "fx-table",
        Category = Categories.AutoDetect(AssetKind.Currency, code, code)
    };

    private SearchItem ToItem(CoinMarket c, FxTable fx) => new()
    {
        Kind = AssetKind.Crypto,
        Id = c.Id,
        Symbol = (c.Symbol ?? "").ToUpperInvariant(),
        Name = c.Name,
        IconUrl = c.Image,
        PriceUsd = c.CurrentPrice is > 0 ? c.CurrentPrice : null,
        Change24hPct = c.ChangePct24h,
        Rank = c.MarketCapRank,
        Source = "coingecko",
        Category = Categories.AutoDetect(AssetKind.Crypto, c.Id, c.Symbol)
    };

    private SearchItem ToItem(CoinSearchHit c, FxTable fx) => new()
    {
        Kind = AssetKind.Crypto,
        Id = c.Id,
        Symbol = (c.Symbol ?? "").ToUpperInvariant(),
        Name = c.Name,
        IconUrl = FirstNonEmpty(c.Large, c.Thumb),
        Rank = c.MarketCapRank,
        Source = "coingecko",
        Category = Categories.AutoDetect(AssetKind.Crypto, c.Id, c.Symbol)
    };

    /// <summary>
    /// Relevance ordering: exact symbol first, then symbol prefix, then name
    /// matches. Within a bucket, prominence decides - market cap rank for coins,
    /// Yahoo's own score for equities, and a fixed order for currencies.
    /// </summary>
    private static IEnumerable<SearchItem> Rank(IEnumerable<SearchItem> items, string query)
    {
        var q = query.Trim();
        var qu = q.ToUpperInvariant();

        // De-duplicate on (kind, id) keeping the richest entry, so a coin that
        // matched both locally and remotely appears once.
        var distinct = items
            .GroupBy(i => (i.Kind, i.Id.ToUpperInvariant()))
            .Select(g => g.OrderByDescending(i => i.PriceUsd is not null).ThenBy(i => i.Source == "coingecko" ? 0 : 1).First())
            .ToList();

        return distinct
            .Select(i => (Item: i, Bucket: BucketOf(i, qu, q), Prominence: ProminenceOf(i)))
            .OrderBy(x => x.Bucket)
            .ThenBy(x => x.Prominence)
            .ThenBy(x => x.Item.Symbol, StringComparer.Ordinal)
            .Select(x => x.Item);
    }

    private static int BucketOf(SearchItem i, string qu, string q)
    {
        var sym = i.Symbol.ToUpperInvariant();
        if (sym == qu) return 0;
        // "euro" should surface EUR, not every token with EURO in its name.
        if (string.Equals(i.Name.Trim(), q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (sym.StartsWith(qu, StringComparison.Ordinal)) return 1;
        if (i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 2;
        if (i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
        if (i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static double ProminenceOf(SearchItem i) => i.Kind switch
    {
        // Lower is better. Rank 1 (bitcoin) sorts ahead of rank 250.
        AssetKind.Crypto => i.Rank ?? 5_000,
        AssetKind.Currency => 100 + (i.PriceUsd is null ? 50 : 0),
        _ => 500
    };
}
