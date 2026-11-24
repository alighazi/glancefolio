using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// Valuation.Build is pure: holdings + quotes + FX in, report out. That makes
/// the money maths testable without a network, which matters because it is the
/// part a user would notice being wrong.
/// </summary>
public class ValuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static FxTable Fx() => new(
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["EUR"] = 0.9m,
            ["GBP"] = 0.8m,
            ["JPY"] = 150m,
        },
        Now, stale: false, error: null);

    private static Holding H(string symbol, AssetKind kind, decimal qty, string? category = null,
                             string assetId = "", decimal? cost = null, string? costCurrency = null,
                             string? sector = null, decimal? manualPrice = null) => new()
    {
        Id = symbol,
        Symbol = symbol,
        Name = symbol,
        Kind = kind,
        AssetId = string.IsNullOrEmpty(assetId) ? symbol : assetId,
        Quantity = qty,
        Category = category,
        UnitCost = cost,
        CostCurrency = costCurrency,
        Sector = sector,
        ManualUnitPrice = manualPrice,
    };

    private static PriceQuote Q(string id, AssetKind kind, decimal? usdPerUnit, string? error = null, bool stale = false) =>
        new()
        {
            AssetId = id,
            Kind = kind,
            UsdPerUnit = usdPerUnit,
            Source = error is null ? "test" : "none",
            Error = error,
            Stale = stale,
            AsOfUtc = error is null ? Now : null,
        };

    private static QuoteSet Quotes(Dictionary<string, PriceQuote> byId, params string[] problems) =>
        new(byId, Now, false, problems);

    private static Portfolio P(string baseCurrency, params Holding[] holdings) =>
        new() { BaseCurrency = baseCurrency, Holdings = holdings.ToList() };

    private static HoldingValuation Row(ValuationReport r, string symbol) =>
        r.Holdings.Single(h => h.Symbol == symbol);

    /// <summary>Build with a fixed clock, so nothing here depends on the wall time.</summary>
    private static ValuationReport Build(Portfolio p, QuoteSet q) => Valuation.Build(p, q, Fx(), Now);

    // ---------------------------------------------------------------- values

    [Fact]
    public void Value_is_quantity_times_unit_price()
    {
        var btc = H("BTC", AssetKind.Crypto, 0.5m);
        var r = Build(P("USD", btc), Quotes(new() { ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m) }));

        Assert.Equal(60_000m, Row(r, "BTC").UnitPriceBase);
        Assert.Equal(30_000m, Row(r, "BTC").ValueBase);
        Assert.Equal(30_000m, r.TotalBase);
        Assert.Equal(100m, Row(r, "BTC").WeightPct);
        Assert.Equal(Now, r.AsOfUtc);
    }

    [Fact]
    public void Total_is_the_sum_of_the_rows_and_weights_add_to_a_hundred()
    {
        var holdings = new[]
        {
            H("BTC", AssetKind.Crypto, 1m),
            H("AAPL", AssetKind.Stock, 10m),
            H("EUR", AssetKind.Currency, 1000m),
        };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["EUR"] = Q("EUR", AssetKind.Currency, 1.1m),
        };

        var r = Build(P("USD", holdings), Quotes(quotes));

        Assert.Equal(60_000m + 2_000m + 1_100m, r.TotalBase);
        Assert.Equal(r.TotalBase, r.Holdings.Sum(h => h.ValueBase ?? 0m));
        Assert.Equal(100m, r.Holdings.Sum(h => h.WeightPct), 1);   // 2dp each, so allow a rounding hair
        Assert.Equal(3, r.PricedCount);
        Assert.Equal(0, r.UnpricedCount);
        Assert.Equal(0, r.StaleCount);
    }

    [Fact]
    public void Rows_come_back_largest_first()
    {
        var holdings = new[]
        {
            H("SMALL", AssetKind.Stock, 1m),
            H("BIG", AssetKind.Stock, 1m),
            H("MID", AssetKind.Stock, 1m),
        };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SMALL"] = Q("SMALL", AssetKind.Stock, 10m),
            ["BIG"] = Q("BIG", AssetKind.Stock, 10_000m),
            ["MID"] = Q("MID", AssetKind.Stock, 100m),
        };

        var r = Build(P("USD", holdings), Quotes(quotes));
        Assert.Equal(new[] { "BIG", "MID", "SMALL" }, r.Holdings.Select(h => h.Symbol).ToArray());
    }

    [Fact]
    public void Money_rounds_midpoints_away_from_zero()
    {
        // 0.125 is the discriminating case: away-from-zero gives 0.13, banker's
        // rounding would give 0.12. Money that drifts downward on every row is
        // the kind of bug nobody can see in a total.
        var h = H("MID", AssetKind.Stock, 1m);
        var r = Build(P("USD", h), Quotes(new() { ["MID"] = Q("MID", AssetKind.Stock, 0.125m) }));

        Assert.Equal(0.13m, Row(r, "MID").ValueBase);
        Assert.Equal(0.13m, r.TotalBase);
        Assert.Equal(2, Scale(r.TotalBase));
    }

    [Fact]
    public void Unit_prices_keep_six_places_so_tiny_quotes_survive()
    {
        var rial = H("IRR", AssetKind.Currency, 50_000_000m);
        var r = Build(P("USD", rial), Quotes(new() { ["IRR"] = Q("IRR", AssetKind.Currency, 1m / 1_784_324.55m) }));

        Assert.Equal(0.000001m, Row(r, "IRR").UnitPriceBase);        // not rounded to zero
        Assert.Equal(50m, Row(r, "IRR").ValueBase);
    }

    private static int Scale(decimal d)
    {
        var s = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dot = s.IndexOf('.');
        return dot < 0 ? 0 : s.Length - dot - 1;
    }

    // ------------------------------------------------------------ base currency

    [Fact]
    public void A_non_usd_base_converts_every_value_and_the_unit_price()
    {
        var holdings = new[] { H("AAPL", AssetKind.Stock, 10m), H("EUR", AssetKind.Currency, 1000m) };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["EUR"] = Q("EUR", AssetKind.Currency, 1m / 0.9m),
        };

        var usd = Build(P("USD", holdings), Quotes(quotes));
        var eur = Build(P("EUR", holdings), Quotes(quotes));

        Assert.Equal("EUR", eur.BaseCurrency);
        Assert.Equal(3_111.11m, usd.TotalBase);                       // 2000 + 1000 EUR in dollars
        Assert.Equal(2_800m, eur.TotalBase);                          // 1800 + 1000
        Assert.Equal(usd.TotalBase * 0.9m, eur.TotalBase, 2);         // the same money, one currency over
        Assert.Equal(180m, Row(eur, "AAPL").UnitPriceBase!.Value, 2); // 200 USD -> 180 EUR
        Assert.Equal(1m, Row(eur, "EUR").UnitPriceBase!.Value, 6);    // a euro is exactly one euro
        Assert.Equal(usd.Holdings.Sum(h => h.WeightPct), eur.Holdings.Sum(h => h.WeightPct), 1);
    }

    [Fact]
    public void Weights_do_not_depend_on_the_base_currency()
    {
        var holdings = new[] { H("A", AssetKind.Stock, 1m), H("B", AssetKind.Stock, 3m) };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["A"] = Q("A", AssetKind.Stock, 100m),
            ["B"] = Q("B", AssetKind.Stock, 100m),
        };

        var usd = Build(P("USD", holdings), Quotes(quotes));
        var jpy = Build(P("JPY", holdings), Quotes(quotes));

        Assert.Equal(25m, Row(usd, "A").WeightPct);
        Assert.Equal(25m, Row(jpy, "A").WeightPct);
        Assert.Equal(75m, Row(jpy, "B").WeightPct);
        Assert.Equal(15_000m, Row(jpy, "A").UnitPriceBase);           // 100 USD -> 15000 JPY
    }

    [Fact]
    public void An_unknown_base_currency_shows_usd_and_says_so()
    {
        // Blanking the whole report would be worse than showing dollars with a
        // note, and the note is what tells the user to fix the setting.
        var h = H("AAPL", AssetKind.Stock, 10m);
        var r = Build(P("ZZZ", h), Quotes(new() { ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m) }));

        Assert.Equal("ZZZ", r.BaseCurrency);
        Assert.Equal(2_000m, r.TotalBase);
        Assert.Contains(r.Problems, p => p.Contains("ZZZ", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------- categories

    [Fact]
    public void Category_totals_group_in_the_canonical_order()
    {
        var holdings = new[]
        {
            H("GOLDBAR", AssetKind.Other, 1m, category: "Commodities", manualPrice: 3600m),
            H("BTC", AssetKind.Crypto, 1m),
            H("EUR", AssetKind.Currency, 100m),
            H("AAPL", AssetKind.Stock, 1m),
            H("SPY", AssetKind.Etf, 1m),
            H("^GSPC", AssetKind.Index, 1m),
            H("HOUSE", AssetKind.Other, 1m, category: "Real Estate", manualPrice: 100m),
        };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["GOLDBAR"] = Q("GOLDBAR", AssetKind.Other, 3600m),
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
            ["EUR"] = Q("EUR", AssetKind.Currency, 1.1m),
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["SPY"] = Q("SPY", AssetKind.Etf, 500m),
            ["^GSPC"] = Q("^GSPC", AssetKind.Index, 7000m),
            ["HOUSE"] = Q("HOUSE", AssetKind.Other, 100m),
        };

        var r = Build(P("USD", holdings), Quotes(quotes));
        var order = r.ByCategory.Select(g => g.Name).ToArray();

        // The taxonomy order with the empty groups omitted: no Bonds, no
        // Collectibles, no Other, because nothing is in them.
        Assert.Equal(new[] { "Cash", "Crypto", "Stocks", "ETFs & Funds", "Commodities", "Indices", "Real Estate" }, order);
        Assert.Equal(Categories.All.Where(order.Contains).ToArray(), order);
    }

    [Fact]
    public void A_category_outside_the_taxonomy_is_re_detected_not_invented()
    {
        // Effective treats an unknown label as "not chosen", so a hand-edited
        // file cannot split the report with an eleventh group.
        var h = H("MYSTERY", AssetKind.Stock, 1m, category: "Zebra Assets");
        var r = Build(P("USD", h), Quotes(new() { ["MYSTERY"] = Q("MYSTERY", AssetKind.Stock, 10m) }));

        Assert.Equal("Stocks", r.ByCategory.Single().Name);
        Assert.Equal("Stocks", Row(r, "MYSTERY").Category);
        Assert.Equal(10m, r.ByCategory.Single().ValueBase);
    }

    [Fact]
    public void Groups_outside_the_taxonomy_are_appended_alphabetically()
    {
        // Sector names are free text from the provider, so none of them are in
        // Categories.All: they all take the append path, and it must be stable
        // between refreshes rather than whatever order the dictionary gave.
        var holdings = new[]
        {
            H("Z", AssetKind.Stock, 1m, sector: "Zebra Mining"),
            H("A", AssetKind.Stock, 1m, sector: "Alpha Energy"),
            H("M", AssetKind.Stock, 1m, sector: "Mid Utilities"),
        };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["Z"] = Q("Z", AssetKind.Stock, 10m),
            ["A"] = Q("A", AssetKind.Stock, 10m),
            ["M"] = Q("M", AssetKind.Stock, 10m),
        };

        var r = Build(P("USD", holdings), Quotes(quotes));
        Assert.Equal(new[] { "Alpha Energy", "Mid Utilities", "Zebra Mining" },
                     r.BySector.Select(g => g.Name).ToArray());
    }

    [Fact]
    public void Category_values_sum_to_the_total_and_carry_counts()
    {
        var holdings = new[]
        {
            H("BTC", AssetKind.Crypto, 1m),
            H("ETH", AssetKind.Crypto, 10m),
            H("EUR", AssetKind.Currency, 100m),
        };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
            ["ETH"] = Q("ETH", AssetKind.Crypto, 3_000m),
            ["EUR"] = Q("EUR", AssetKind.Currency, 1.1m),
        };

        var r = Build(P("USD", holdings), Quotes(quotes));
        var crypto = r.ByCategory.Single(g => g.Name == "Crypto");
        var cash = r.ByCategory.Single(g => g.Name == "Cash");

        Assert.Equal(90_000m, crypto.ValueBase);
        Assert.Equal(2, crypto.Count);
        Assert.Equal(110m, cash.ValueBase);
        Assert.Equal(1, cash.Count);
        Assert.Equal(r.TotalBase, r.ByCategory.Sum(g => g.ValueBase));
        Assert.Equal(100m, r.ByCategory.Sum(g => g.WeightPct), 1);
        Assert.Equal(90_000m / 90_110m * 100m, crypto.WeightPct, 2);
    }

    [Fact]
    public void A_user_category_overrides_detection_and_tokenised_metal_becomes_a_commodity()
    {
        var paxg = H("PAXG", AssetKind.Crypto, 2m, assetId: "pax-gold");
        var usdt = H("USDT", AssetKind.Crypto, 500m, assetId: "tether");
        var house = H("FLAT", AssetKind.Other, 1m, category: "Real Estate", manualPrice: 250_000m);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["PAXG"] = Q("PAXG", AssetKind.Crypto, 3_600m),
            ["USDT"] = Q("USDT", AssetKind.Crypto, 1m),
            ["FLAT"] = Q("FLAT", AssetKind.Other, 250_000m),
        };

        var r = Build(P("USD", paxg, usdt, house), Quotes(quotes));

        Assert.Equal("Commodities", Row(r, "PAXG").Category);
        Assert.Equal("Cash", Row(r, "USDT").Category);
        Assert.Equal("Real Estate", Row(r, "FLAT").Category);
        Assert.Equal(7_200m, r.ByCategory.Single(g => g.Name == "Commodities").ValueBase);
        Assert.Equal(250_000m, r.ByCategory.Single(g => g.Name == "Real Estate").ValueBase);
    }

    // ---------------------------------------------------------- gain and loss

    [Fact]
    public void Gain_is_computed_only_where_both_a_value_and_a_cost_exist()
    {
        var withCost = H("AAPL", AssetKind.Stock, 10m, cost: 150m, costCurrency: "USD");
        var noCost = H("BTC", AssetKind.Crypto, 1m);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
        };

        var r = Build(P("USD", withCost, noCost), Quotes(quotes));

        Assert.Equal(1_500m, Row(r, "AAPL").CostBase);
        Assert.Equal(500m, Row(r, "AAPL").GainBase);
        Assert.Equal(33.33m, Row(r, "AAPL").GainPct);
        Assert.Null(Row(r, "BTC").CostBase);
        Assert.Null(Row(r, "BTC").GainBase);
        Assert.Null(Row(r, "BTC").GainPct);

        Assert.Equal(1_500m, r.TotalCostBase);
        Assert.Equal(500m, r.TotalGainBase);
        Assert.Equal(33.33m, r.TotalGainPct);
    }

    [Fact]
    public void Total_gain_ignores_holdings_without_a_cost()
    {
        // Otherwise a missing cost basis would read as a 100% gain and the
        // headline number would be fiction.
        var costed = H("AAPL", AssetKind.Stock, 10m, cost: 150m, costCurrency: "USD");
        var free = H("BTC", AssetKind.Crypto, 1m);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
        };

        var r = Build(P("USD", costed, free), Quotes(quotes));

        Assert.Equal(62_000m, r.TotalBase);
        Assert.Equal(1_500m, r.TotalCostBase);
        Assert.Equal(500m, r.TotalGainBase);      // over AAPL alone, not over 62000
        Assert.Equal(33.33m, r.TotalGainPct);
    }

    [Fact]
    public void A_loss_is_negative_and_a_cost_in_another_currency_is_converted()
    {
        var loser = H("TSLA", AssetKind.Stock, 10m, cost: 300m, costCurrency: "USD");
        var tokyo = H("7203.T", AssetKind.Stock, 100m, cost: 2_000m, costCurrency: "JPY");
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["TSLA"] = Q("TSLA", AssetKind.Stock, 250m),
            ["7203.T"] = Q("7203.T", AssetKind.Stock, 20m),
        };

        var r = Build(P("USD", loser, tokyo), Quotes(quotes));

        Assert.Equal(3_000m, Row(r, "TSLA").CostBase);
        Assert.Equal(2_500m, Row(r, "TSLA").ValueBase);
        Assert.Equal(-500m, Row(r, "TSLA").GainBase);
        Assert.Equal(-16.67m, Row(r, "TSLA").GainPct);

        // Cost was 2000 JPY per share: 2000 / 150 = 13.33 USD, value 20 USD.
        Assert.Equal(100m * 2_000m / 150m, Row(r, "7203.T").CostBase!.Value, 2);
        Assert.Equal(2_000m, Row(r, "7203.T").ValueBase);
        Assert.Equal(50m, Row(r, "7203.T").GainPct!.Value, 1);   // 20 vs 13.33 per share
    }

    [Fact]
    public void A_cost_currency_with_no_fx_path_is_reported_not_guessed()
    {
        var h = H("AAPL", AssetKind.Stock, 10m, cost: 150m, costCurrency: "ZZZ");
        var r = Build(P("USD", h), Quotes(new() { ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m) }));

        Assert.Null(Row(r, "AAPL").CostBase);
        Assert.Null(Row(r, "AAPL").GainBase);
        Assert.Equal(2_000m, Row(r, "AAPL").ValueBase);          // the value is still fine
        Assert.Contains(r.Problems, p => p.Contains("ZZZ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_cost_currency_is_taken_to_be_the_base_currency()
    {
        var h = H("AAPL", AssetKind.Stock, 10m, cost: 150m, costCurrency: null);
        var r = Build(P("USD", h), Quotes(new() { ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m) }));

        Assert.Equal(1_500m, Row(r, "AAPL").CostBase);
        Assert.Equal(500m, Row(r, "AAPL").GainBase);
    }

    [Fact]
    public void Category_gain_aggregates_the_rows_that_have_a_cost()
    {
        var a = H("AAPL", AssetKind.Stock, 10m, cost: 100m, costCurrency: "USD");   // 1000 -> 2000
        var b = H("MSFT", AssetKind.Stock, 10m, cost: 300m, costCurrency: "USD");   // 3000 -> 4000
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["MSFT"] = Q("MSFT", AssetKind.Stock, 400m),
        };

        var r = Build(P("USD", a, b), Quotes(quotes));
        var stocks = r.ByCategory.Single(g => g.Name == "Stocks");

        Assert.Equal(6_000m, stocks.ValueBase);
        Assert.Equal(4_000m, stocks.CostBase);
        Assert.Equal(2_000m, stocks.GainBase);
        Assert.Equal(50m, stocks.GainPct);
        Assert.Equal(2, stocks.Count);
    }

    // ------------------------------------------------------- failures and gaps

    [Fact]
    public void An_unpriced_holding_keeps_its_row_instead_of_vanishing()
    {
        var ok = H("BTC", AssetKind.Crypto, 1m);
        var dead = H("ZZZZ", AssetKind.Stock, 5m);
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
            ["ZZZZ"] = Q("ZZZZ", AssetKind.Stock, null, error: "Yahoo: HTTP 404"),
        };

        // PriceService collects the per-quote errors into QuoteSet.Problems;
        // Build carries them through to the report so the UI can say why.
        var r = Build(P("USD", ok, dead), Quotes(quotes, "ZZZZ: Yahoo: HTTP 404"));

        Assert.Equal(2, r.Holdings.Count);
        Assert.Null(Row(r, "ZZZZ").ValueBase);
        Assert.Null(Row(r, "ZZZZ").UnitPriceBase);
        Assert.Equal("Yahoo: HTTP 404", Row(r, "ZZZZ").Error);
        Assert.Equal(0m, Row(r, "ZZZZ").WeightPct);
        Assert.Equal(60_000m, r.TotalBase);                       // the total ignores it
        Assert.Equal(100m, Row(r, "BTC").WeightPct);              // weight of what is known
        Assert.Equal(1, r.PricedCount);
        Assert.Equal(1, r.UnpricedCount);
        Assert.Contains("ZZZZ: Yahoo: HTTP 404", r.Problems);
        Assert.Equal("Stocks", Row(r, "ZZZZ").Category);          // still categorised
        Assert.Equal(5m, Row(r, "ZZZZ").Quantity);                // still shown
    }

    [Fact]
    public void Provider_problems_are_carried_into_the_report_without_duplicates()
    {
        var h = H("BTC", AssetKind.Crypto, 1m);
        var r = Build(
            P("USD", h),
            Quotes(new() { ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m) },
                   "crypto provider degraded", "crypto provider degraded"));

        Assert.Equal(new[] { "crypto provider degraded" }, r.Problems.ToArray());
        Assert.Equal(60_000m, r.TotalBase);   // a warning is not a failure
    }

    [Fact]
    public void A_stale_price_is_flagged_but_still_counted()
    {
        var h = H("BTC", AssetKind.Crypto, 1m);
        var r = Build(P("USD", h), Quotes(new() { ["BTC"] = Q("BTC", AssetKind.Crypto, 55_000m, stale: true) }));

        Assert.True(Row(r, "BTC").Stale);
        Assert.Equal(55_000m, r.TotalBase);
        Assert.Equal(1, r.StaleCount);
        Assert.Equal(1, r.PricedCount);
        Assert.Equal(0, r.UnpricedCount);
    }

    [Fact]
    public void A_manual_holding_is_valued_from_its_own_price_and_marked_manual()
    {
        var bar = H("GOLDBAR", AssetKind.Other, 2m, category: "Commodities", manualPrice: 3_600m);
        var r = Build(P("USD", bar), Quotes(new() { ["GOLDBAR"] = Q("GOLDBAR", AssetKind.Other, 3_600m) }));

        Assert.True(Row(r, "GOLDBAR").Manual);
        Assert.Equal(7_200m, Row(r, "GOLDBAR").ValueBase);
        Assert.Equal("Commodities", Row(r, "GOLDBAR").Category);
    }

    [Fact]
    public void A_holding_with_a_manual_price_is_manual_even_when_it_is_listed()
    {
        // IsManual is "the user is the price provider", which overrides the kind.
        var delisted = H("OLD", AssetKind.Stock, 100m, manualPrice: 0.5m);
        Assert.True(delisted.IsManual);
        Assert.False(H("AAPL", AssetKind.Stock, 1m).IsManual);
        Assert.True(H("FLAT", AssetKind.Other, 1m).IsManual);
    }

    // ---------------------------------------------------------------- sectors

    [Fact]
    public void Sectors_group_only_the_holdings_that_have_one()
    {
        var holdings = new[]
        {
            H("AAPL", AssetKind.Stock, 10m, sector: "Technology"),
            H("MSFT", AssetKind.Stock, 5m, sector: "Technology"),
            H("JPM", AssetKind.Stock, 4m, sector: "Financial Services"),
            H("BTC", AssetKind.Crypto, 1m),                       // no sector
        };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
            ["MSFT"] = Q("MSFT", AssetKind.Stock, 400m),
            ["JPM"] = Q("JPM", AssetKind.Stock, 150m),
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
        };

        var r = Build(P("USD", holdings), Quotes(quotes));
        var tech = r.BySector.Single(g => g.Name == "Technology");

        Assert.Equal(2, tech.Count);
        Assert.Equal(4_000m, tech.ValueBase);
        Assert.Equal(2, r.BySector.Count);
        // Weights are of the whole portfolio, so sectors do not sum to 100.
        Assert.True(r.BySector.Sum(g => g.WeightPct) < 100m);
    }

    [Fact]
    public void An_empty_portfolio_produces_an_empty_report_not_a_crash()
    {
        var r = Build(P("USD"), Quotes(new Dictionary<string, PriceQuote>()));

        Assert.Equal(0m, r.TotalBase);
        Assert.Empty(r.Holdings);
        Assert.Empty(r.ByCategory);
        Assert.Empty(r.BySector);
        Assert.Null(r.TotalGainPct);
        Assert.Null(r.TotalCostBase);
        Assert.Equal("USD", r.BaseCurrency);
        Assert.Equal(Now, r.AsOfUtc);
    }

    [Fact]
    public void A_blank_base_currency_defaults_to_usd()
    {
        var h = H("AAPL", AssetKind.Stock, 10m);
        var r = Build(P("   ", h), Quotes(new() { ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m) }));

        Assert.Equal("USD", r.BaseCurrency);
        Assert.Equal(2_000m, r.TotalBase);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void The_report_is_deterministic_for_the_same_inputs()
    {
        var holdings = new[] { H("BTC", AssetKind.Crypto, 1m), H("AAPL", AssetKind.Stock, 10m, cost: 100m, costCurrency: "USD") };
        var quotes = new Dictionary<string, PriceQuote>
        {
            ["BTC"] = Q("BTC", AssetKind.Crypto, 60_000m),
            ["AAPL"] = Q("AAPL", AssetKind.Stock, 200m),
        };

        var a = Build(P("USD", holdings), Quotes(quotes));
        var b = Build(P("USD", holdings), Quotes(quotes));

        Assert.Equal(a.TotalBase, b.TotalBase);
        Assert.Equal(a.TotalGainPct, b.TotalGainPct);
        Assert.Equal(a.Holdings.Select(h => h.Symbol), b.Holdings.Select(h => h.Symbol));
        Assert.Equal(a.ByCategory.Select(g => g.Name), b.ByCategory.Select(g => g.Name));
        Assert.Equal(a.AsOfUtc, b.AsOfUtc);   // injected, so Build never reads the clock
    }
}
