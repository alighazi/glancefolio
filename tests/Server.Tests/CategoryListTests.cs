using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// Categories are user data now: the list can be renamed, reordered, added to
/// and cut down. These pin the two invariants that keep a rename from silently
/// re-filing somebody's portfolio — detection follows a stable key rather than a
/// display name, and anything that matches no category is reported rather than
/// guessed at.
/// </summary>
public class CategoryListTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CategoryDef C(string name, string? detect = null) => new() { Name = name, Detect = detect };

    private static Holding H(string symbol, AssetKind kind = AssetKind.Stock, string? category = null,
                             decimal qty = 1m, string? assetId = null, string? sector = null) => new()
    {
        Id = symbol,
        Symbol = symbol,
        Name = symbol,
        Kind = kind,
        AssetId = assetId ?? symbol,
        Quantity = qty,
        Category = category,
        Sector = sector,
    };

    private static List<CategoryDef> Renamed(string from, string to) =>
        Categories.DefaultList().Select(c => c.Name == from ? C(to, c.Detect) : c).ToList();

    // ------------------------------------------------------------- detection

    [Fact]
    public void A_renamed_category_keeps_receiving_what_it_always_did()
    {
        // The point of the detection key: the display name is the user's, the
        // binding is the app's.
        var list = Renamed(Categories.Crypto, "Digital assets");

        Assert.Equal("Digital assets", Categories.Effective(H("BTC", AssetKind.Crypto, assetId: "bitcoin"), list));
        Assert.Equal("Digital assets", Categories.Detected(H("BTC", AssetKind.Crypto, assetId: "bitcoin"), list));
    }

    [Fact]
    public void Detection_never_fills_a_category_the_user_added()
    {
        var list = Categories.DefaultList();
        list.Add(C("Pension"));

        // Nothing detects into "Pension", so an ordinary coin still goes to
        // Crypto rather than being spread over the new bucket.
        Assert.Equal(Categories.Crypto, Categories.Effective(H("BTC", AssetKind.Crypto, assetId: "bitcoin"), list));
        Assert.Null(list[^1].Detect);
    }

    [Fact]
    public void Deleting_a_category_sends_its_holdings_back_to_detection()
    {
        var withoutCrypto = Categories.DefaultList().Where(c => c.Detect != Categories.KCrypto).ToList();

        // A coin has nowhere to go: reported, not quietly folded into "Other".
        Assert.Equal(Categories.Uncategorised, Categories.Effective(H("BTC", AssetKind.Crypto, assetId: "bitcoin"), withoutCrypto));

        // Everything else is untouched.
        Assert.Equal(Categories.Cash, Categories.Effective(H("EUR", AssetKind.Currency), withoutCrypto));
        Assert.Equal(Categories.Commodities, Categories.Effective(H("PAXG", AssetKind.Crypto, assetId: "pax-gold"), withoutCrypto));
    }

    [Fact]
    public void Deleting_the_catch_all_leaves_manual_holdings_uncategorised()
    {
        var withoutOther = Categories.DefaultList().Where(c => c.Detect != Categories.KOther).ToList();
        var bar = H("GOLDBAR", AssetKind.Other);

        Assert.Equal(Categories.Other, Categories.Effective(bar, Categories.DefaultList()));
        Assert.Equal(Categories.Uncategorised, Categories.Effective(bar, withoutOther));
    }

    [Fact]
    public void A_hand_pick_still_beats_detection_as_long_as_the_category_exists()
    {
        var list = Renamed(Categories.Crypto, "Coins");

        Assert.Equal("Long term", Categories.Effective(H("BTC", AssetKind.Crypto, category: "Long term", assetId: "bitcoin"),
            [.. list, C("Long term")]));

        // ...and a pick that names a deleted category falls back to detection
        // rather than inventing a group the list does not have.
        Assert.Equal("Coins", Categories.Effective(H("BTC", AssetKind.Crypto, category: Categories.Crypto, assetId: "bitcoin"), list));
    }

    [Fact]
    public void Detected_ignores_the_hand_pick_so_the_ui_can_label_the_auto_option()
    {
        var bar = H("GOLDBAR", AssetKind.Other, category: Categories.Collectibles);

        Assert.Equal(Categories.Collectibles, Categories.Effective(bar, Categories.DefaultList()));
        Assert.Equal(Categories.Other, Categories.Detected(bar, Categories.DefaultList()));
    }

    [Fact]
    public void An_empty_list_behaves_as_the_defaults_rather_than_filing_nothing()
    {
        Assert.Equal(Categories.Crypto, Categories.Effective(H("BTC", AssetKind.Crypto, assetId: "bitcoin"), []));
        Assert.Equal(Categories.Crypto, Categories.Detected(H("BTC", AssetKind.Crypto, assetId: "bitcoin"), null));
    }

    [Fact]
    public void DefaultKeyFor_recognises_a_default_name_so_a_re_add_keeps_working()
    {
        Assert.Equal(Categories.KCrypto, Categories.DefaultKeyFor(Categories.Crypto));
        Assert.Equal(Categories.KCommodities, Categories.DefaultKeyFor("commodities"));
        Assert.Null(Categories.DefaultKeyFor("Bonds"));      // a default, but never detected
        Assert.Null(Categories.DefaultKeyFor("Pension"));    // not a default at all
    }

    // ------------------------------------------------- repairing a saved list

    [Fact]
    public void NormalizeCategories_seeds_the_defaults_into_an_older_portfolio()
    {
        Assert.Equal(Categories.Defaults.Select(c => c.Name), PortfolioStore.NormalizeCategories(null).Select(c => c.Name));
        Assert.Equal(Categories.Defaults.Select(c => c.Name), PortfolioStore.NormalizeCategories([]).Select(c => c.Name));
    }

    [Fact]
    public void NormalizeCategories_trims_and_dedupes_without_losing_the_detection_binding()
    {
        var result = PortfolioStore.NormalizeCategories(
        [
            C("  Crypto  ", Categories.KCrypto),
            C("crypto", null),                       // duplicate, different case
            C("   ", Categories.KCash),              // no name
            C("Pension", null),
        ]);

        Assert.Equal(["Crypto", "Pension"], result.Select(c => c.Name));
        Assert.Equal(Categories.KCrypto, result[0].Detect);
    }

    [Fact]
    public void NormalizeCategories_drops_a_key_it_does_not_know_and_a_second_claim_on_one_key()
    {
        var result = PortfolioStore.NormalizeCategories(
        [
            C("Coins", "cryptocurrency"),            // not a key detection produces
            C("Crypto", Categories.KCrypto),
            C("Also crypto", "CRYPTO"),              // already claimed
        ]);

        Assert.Equal(["Coins", "Crypto", "Also crypto"], result.Select(c => c.Name));
        Assert.Null(result[0].Detect);
        Assert.Equal(Categories.KCrypto, result[1].Detect);
        Assert.Null(result[2].Detect);               // one owner per key
    }

    [Fact]
    public void NormalizeCategories_caps_the_list_and_refuses_to_save_an_empty_one()
    {
        var many = Enumerable.Range(0, PortfolioStore.MaxCategories + 5).Select(i => C($"Cat {i}")).ToList();
        Assert.Equal(PortfolioStore.MaxCategories, PortfolioStore.NormalizeCategories(many).Count);

        var overlong = C(new string('x', PortfolioStore.MaxCategoryNameLength + 1));
        Assert.Single(PortfolioStore.NormalizeCategories([C("Cash", Categories.KCash), overlong]));

        // Everything dropped would leave every holding uncategorised.
        Assert.Equal(Categories.Defaults.Count, PortfolioStore.NormalizeCategories([overlong, C("")]).Count);
    }

    [Fact]
    public void Normalize_repairs_categories_before_it_looks_at_holdings()
    {
        var p = new Portfolio
        {
            Categories = Renamed(Categories.Crypto, "Coins"),
            Holdings =
            [
                H("BTC", AssetKind.Crypto, category: Categories.Crypto, assetId: "bitcoin"),  // old name
                H("PAXG", AssetKind.Crypto, category: "Not a category", assetId: "pax-gold"), // never existed
                H("AAPL", category: "  stocks  "),                                            // wrong spelling
            ]
        };

        var fixedUp = PortfolioStore.Normalize(p);

        Assert.Null(fixedUp.Holdings[0].Category);            // re-detected into "Coins"
        Assert.Null(fixedUp.Holdings[1].Category);
        Assert.Equal(Categories.Stocks, fixedUp.Holdings[2].Category);
        Assert.Equal("Coins", Categories.Effective(fixedUp.Holdings[0], fixedUp.Categories));
        Assert.Equal(Categories.Commodities, Categories.Effective(fixedUp.Holdings[1], fixedUp.Categories));
    }

    // ------------------------------------------------------------- the report

    private static QuoteSet Quotes(Dictionary<string, decimal> usdPerUnit) => new(
        usdPerUnit.ToDictionary(kv => kv.Key, kv => new PriceQuote
        {
            AssetId = kv.Key,
            Kind = AssetKind.Stock,
            UsdPerUnit = kv.Value,
            Source = "test",
            AsOfUtc = Now,
        }),
        Now, false, []);

    private static ValuationReport Build(Portfolio p, params string[] priced) =>
        Valuation.Build(p, Quotes(priced.ToDictionary(id => id, _ => 100m)),
            new FxTable(new Dictionary<string, decimal>(StringComparer.Ordinal) { ["EUR"] = 0.9m }, Now, false, null), Now);

    [Fact]
    public void The_report_lists_categories_in_the_users_order_and_appends_the_rest()
    {
        var list = new List<CategoryDef>
        {
            C("Pension"),                                   // hand-picked, first on purpose
            C("Coins", Categories.KCrypto),
            C("Cash", Categories.KCash),
        };
        var p = new Portfolio
        {
            BaseCurrency = "USD",
            Categories = list,
            Holdings =
            [
                H("BTC", AssetKind.Crypto, assetId: "bitcoin"),
                H("EUR", AssetKind.Currency),
                H("AAPL"),                                  // stocks: nothing in the list receives them
                H("GOLDBAR", AssetKind.Other, category: "Pension"),
            ]
        };

        var r = Build(p, "BTC", "EUR", "AAPL", "GOLDBAR");

        // The user's order first - not alphabetical, not by value - then the
        // group nothing in the list receives.
        Assert.Equal(["Pension", "Coins", "Cash", Categories.Uncategorised], r.ByCategory.Select(g => g.Name));
        Assert.Equal("AAPL", r.Holdings.Single(h => h.Category == Categories.Uncategorised).Symbol);
        Assert.Contains("1 holding matches no category", string.Join(" | ", r.Problems));
    }

    [Fact]
    public void An_uncategorised_holding_is_reported_as_a_group_and_as_a_problem()
    {
        var p = new Portfolio
        {
            BaseCurrency = "USD",
            Categories = [C("Cash", Categories.KCash)],
            Holdings = [H("BTC", AssetKind.Crypto, assetId: "bitcoin"), H("ETH", AssetKind.Crypto, assetId: "ethereum")],
        };

        var r = Build(p, "BTC", "ETH");

        Assert.Equal([Categories.Uncategorised], r.ByCategory.Select(g => g.Name));
        Assert.Equal(2, r.ByCategory[0].Count);
        Assert.Contains("2 holdings match no category", r.Problems.Single(x => x.Contains("no category")));
    }

    [Fact]
    public void Each_row_carries_both_the_category_it_is_in_and_what_detection_said()
    {
        var p = new Portfolio
        {
            BaseCurrency = "USD",
            Categories = Renamed(Categories.Commodities, "Hard assets"),
            Holdings = [H("GOLDBAR", AssetKind.Other, category: Categories.Collectibles)],
        };

        var r = Build(p, "GOLDBAR");
        var row = r.Holdings.Single();

        Assert.Equal(Categories.Collectibles, row.Category);
        Assert.Equal(Categories.Other, row.DetectedCategory);
        Assert.Equal(["Hard assets"], p.Categories.Where(c => c.Detect == Categories.KCommodities).Select(c => c.Name));
    }

    [Fact]
    public void Sectors_are_alphabetical_no_matter_how_the_categories_are_ordered()
    {
        var p = new Portfolio
        {
            BaseCurrency = "USD",
            Categories = [C("Zeta", Categories.KStocks), C("Alpha")],
            Holdings =
            [
                H("AAA", sector: "Utilities"),
                H("BBB", sector: "Energy"),
                H("CCC", sector: "Materials"),
            ]
        };

        var r = Build(p, "AAA", "BBB", "CCC");

        Assert.Equal(["Energy", "Materials", "Utilities"], r.BySector.Select(g => g.Name));
        Assert.Equal(["Zeta"], r.ByCategory.Select(g => g.Name));
    }
}
