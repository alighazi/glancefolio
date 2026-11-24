namespace PortfolioWeb;

// ---------------------------------------------------------------------------
// Asset identity
// ---------------------------------------------------------------------------
//
// The single most important decision in this codebase: an asset is identified
// by (Kind, Id) where Id is the *provider-native* key, never a bare ticker.
//
//     Crypto    -> CoinGecko id      "pax-gold"
//     Stock/…   -> Yahoo symbol      "AAPL", "GC=F", "^GSPC"
//     Currency  -> ISO 4217 code     "EUR"
//     Other     -> whatever the user typed for a manual holding
//
// A bare ticker is ambiguous and guessing wrong is expensive: "PAXG" is both a
// Nasdaq-listed shell company at ~$9.86 and PAX Gold at ~$3,600. Carrying the
// kind alongside the id makes that collision impossible to hit silently, and
// the search UI can simply show both and let the human choose.

public enum AssetKind
{
    Crypto,
    Stock,
    Etf,
    Index,
    Future,
    Currency,
    Other
}

// ---------------------------------------------------------------------------
// Search
// ---------------------------------------------------------------------------

public sealed record SearchItem
{
    public required AssetKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public string? Exchange { get; init; }
    public string? Sector { get; init; }
    public string? Industry { get; init; }
    public string? IconUrl { get; init; }
    public decimal? PriceUsd { get; init; }
    public decimal? Change24hPct { get; init; }
    public int? Rank { get; init; }
    public required string Source { get; init; }
    // Category this item would get if the user does not pick one.
    public required string Category { get; init; }
}

public sealed record SearchResponse
{
    public List<SearchItem> Items { get; init; } = new();
    // Which sources answered, so the UI can say "crypto results are rate
    // limited right now" instead of silently showing fewer rows.
    public List<string> Sources { get; init; } = new();
    public List<string> Degraded { get; init; } = new();
    public bool FromCache { get; init; }
    public int ElapsedMs { get; init; }
}

// ---------------------------------------------------------------------------
// Portfolio
// ---------------------------------------------------------------------------

public sealed class Holding
{
    public string Id { get; set; } = NewId();

    public AssetKind Kind { get; set; } = AssetKind.Other;
    public string AssetId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";

    // Carried over from the search result purely for display; never used to
    // resolve a price.
    public string? IconUrl { get; set; }
    public string? Exchange { get; set; }
    public string? Sector { get; set; }
    public string? Industry { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Null or empty means "use the automatically detected category".</summary>
    public string? Category { get; set; }

    // Optional cost basis, so the report can show unrealised gain/loss.
    public decimal? UnitCost { get; set; }
    public string? CostCurrency { get; set; }

    // An asset no provider knows about. The user supplies the price per unit;
    // ManualPriceCurrency says what they typed it in (defaults to the base
    // currency at the time) so it still converts if the base changes later.
    public decimal? ManualUnitPrice { get; set; }
    public string? ManualPriceCurrency { get; set; }

    public string? Note { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public bool IsManual => Kind == AssetKind.Other || ManualUnitPrice is not null;

    public static string NewId() => Guid.NewGuid().ToString("n")[..10];
}

/// <summary>
/// One entry in the user's category list. The list is editable, so what a
/// holding is filed under is data, not a constant in the code.
///
/// <see cref="Detect"/> is what keeps auto-categorisation working through a
/// rename: it is the canonical detection key ("crypto", "commodities", …) this
/// category receives. Renaming "Crypto" to "Digital assets" carries the key
/// over, so new coins still land in the right place. A null key means the
/// category is only ever chosen by hand - which is what Real Estate, Bonds and
/// Collectibles have always been, and what any category you add yourself gets.
/// </summary>
public sealed class CategoryDef
{
    public string Name { get; set; } = "";
    public string? Detect { get; set; }
}

public sealed class Portfolio
{
    public string BaseCurrency { get; set; } = "USD";
    public List<Holding> Holdings { get; set; } = new();

    /// <summary>
    /// The category list, in report order. Empty means "not set yet" and is
    /// filled with the defaults on load, so a portfolio exported before this
    /// existed still works.
    /// </summary>
    public List<CategoryDef> Categories { get; set; } = new();

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------------------
// Prices
// ---------------------------------------------------------------------------

/// <summary>
/// Every provider is reduced to one number: how many USD one unit of the asset
/// is worth. Any cross rate is then a ratio of two of these, which is what
/// lets one report mix euros, bitcoin, gold futures and a Nasdaq stock.
/// </summary>
public sealed record PriceQuote
{
    public required string AssetId { get; init; }
    public required AssetKind Kind { get; init; }
    public decimal? UsdPerUnit { get; init; }
    public DateTimeOffset? AsOfUtc { get; init; }
    public required string Source { get; init; }
    public bool Stale { get; init; }
    public string? Error { get; init; }

    public bool Ok => UsdPerUnit is > 0 && Error is null;
}

public sealed record PriceRequest(string Id, AssetKind Kind, string AssetId, decimal? ManualUnitPrice, string? ManualPriceCurrency);

// ---------------------------------------------------------------------------
// Valuation / reporting
// ---------------------------------------------------------------------------

public sealed record HoldingValuation
{
    public required string Id { get; init; }
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required AssetKind Kind { get; init; }
    public string? IconUrl { get; init; }
    public required string Category { get; init; }

    /// <summary>
    /// What automatic detection says on its own, so the UI can label the "Auto"
    /// choice even when this row has been filed by hand. Equals <see cref="Category"/>
    /// whenever there is no hand pick.
    /// </summary>
    public required string DetectedCategory { get; init; }
    public string? Sector { get; init; }
    public decimal Quantity { get; init; }
    public decimal? UnitPriceBase { get; init; }
    public decimal? ValueBase { get; init; }
    public decimal? CostBase { get; init; }
    public decimal? GainBase { get; init; }
    public decimal? GainPct { get; init; }
    public decimal WeightPct { get; init; }
    public bool Manual { get; init; }
    public bool Stale { get; init; }
    public DateTimeOffset? AsOfUtc { get; init; }
    public string? Error { get; init; }
}

public sealed record GroupTotal
{
    public required string Name { get; init; }
    public decimal ValueBase { get; init; }
    public decimal WeightPct { get; init; }
    public int Count { get; init; }
    public decimal? CostBase { get; init; }
    public decimal? GainBase { get; init; }
    public decimal? GainPct { get; init; }
}

public sealed record ValuationReport
{
    public required string BaseCurrency { get; init; }
    public decimal TotalBase { get; init; }
    public decimal? TotalCostBase { get; init; }
    public decimal? TotalGainBase { get; init; }
    public decimal? TotalGainPct { get; init; }
    public List<GroupTotal> ByCategory { get; init; } = new();
    public List<GroupTotal> BySector { get; init; } = new();
    public List<HoldingValuation> Holdings { get; init; } = new();
    public List<string> Problems { get; init; } = new();
    public DateTimeOffset AsOfUtc { get; init; }
    public int PricedCount { get; init; }
    public int UnpricedCount { get; init; }
    public int StaleCount { get; init; }
}
