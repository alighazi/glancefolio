using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// Every provider reduces to "USD per unit" and every report converts through
/// the FX table, so the inversion here is the arithmetic the whole app rests on.
/// er-api publishes units of X per 1 USD, which is the reciprocal of what a
/// valuation needs.
/// </summary>
public class FxTests
{
    private static FxTable Table() => new(
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["EUR"] = 0.9m,        // 0.9 EUR per USD
            ["GBP"] = 0.8m,
            ["JPY"] = 150m,
            ["IRR"] = 1_784_324.55m,
            ["BTC"] = 0m,          // a zero rate must never divide by zero
        },
        DateTimeOffset.UnixEpoch, stale: false, error: null);

    /// <summary>
    /// Decimal division is not exact (1 / (1 / 0.9) is not quite 0.9), so the
    /// rate assertions compare to a tolerance rather than bit for bit.
    /// </summary>
    private static void Near(decimal expected, decimal? actual, decimal tolerance = 0.0000001m)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual!.Value) <= tolerance,
                    $"expected ~{expected}, got {actual.Value}");
    }

    [Fact]
    public void UsdPerUnit_inverts_the_published_rate()
    {
        var fx = Table();
        Assert.Equal(1m, fx.UsdPerUnit("USD"));                       // identity, always available
        Near(1m / 0.9m, fx.UsdPerUnit("EUR"));                        // ~1.111 USD per EUR
        Near(1m / 150m, fx.UsdPerUnit("JPY"));
        Assert.True(fx.UsdPerUnit("IRR")! < 0.000001m);               // a rial is worth far less than a cent
    }

    [Fact]
    public void UsdPerUnit_is_null_for_unknown_or_non_positive_codes()
    {
        var fx = Table();
        Assert.Null(fx.UsdPerUnit("ZZZ"));
        Assert.Null(fx.UsdPerUnit("BTC"));    // present but zero - unusable, not infinity
        Assert.Null(fx.UsdPerUnit(null));
        Assert.Null(fx.UsdPerUnit(""));
        Assert.Null(fx.UsdPerUnit("   "));
    }

    [Fact]
    public void UsdPerUnit_normalises_case_and_whitespace()
    {
        var fx = Table();
        Assert.Equal(fx.UsdPerUnit("EUR"), fx.UsdPerUnit(" eur "));
        Assert.Equal(fx.UsdPerUnit("usd"), fx.UsdPerUnit("USD"));
    }

    [Fact]
    public void Rate_divides_the_two_legs_through_usd()
    {
        var fx = Table();
        Assert.Equal(1m, fx.Rate("USD", "USD"));
        Near(0.9m, fx.Rate("USD", "EUR"));                            // 0.9 EUR per USD
        Near(1m / 0.9m, fx.Rate("EUR", "USD"));                       // and back
        Near(0.8m / 0.9m, fx.Rate("EUR", "GBP"));                     // cross rate, no direct quote needed
    }

    [Fact]
    public void Rate_round_trips_within_a_tenth_of_a_basis_point()
    {
        var fx = Table();
        var there = fx.Rate("EUR", "JPY")!.Value;
        var back = fx.Rate("JPY", "EUR")!.Value;
        Assert.True(Math.Abs(there * back - 1m) < 0.00001m, $"round trip gave {there * back}");
    }

    [Fact]
    public void Rate_is_null_when_either_side_is_unknown()
    {
        var fx = Table();
        Assert.Null(fx.Rate("EUR", "ZZZ"));
        Assert.Null(fx.Rate("ZZZ", "EUR"));
        Assert.Null(fx.Rate("EUR", "BTC"));   // zero rate
        Assert.Null(fx.Rate(null, "EUR"));
    }

    [Fact]
    public void Available_reflects_whether_any_rate_loaded()
    {
        Assert.True(Table().Available);
        var empty = new FxTable(new Dictionary<string, decimal>(), null, stale: true, "provider down");
        Assert.False(empty.Available);
        Assert.Null(empty.UsdPerUnit("EUR"));
        Assert.Null(empty.Rate("USD", "EUR"));
        Assert.Equal("provider down", empty.Error);
        Assert.True(empty.Stale);
    }

    // -- the static ISO table ---------------------------------------------

    [Fact]
    public void Names_covers_the_majors_and_uses_uppercase_keys()
    {
        // The built-in table is the floor for what is accepted when the FX
        // provider has not been reached; the live payload widens it to ~166.
        // FxService.KnownCodes is the union, and that is what validation uses.
        Assert.True(Currencies.Names.Count >= 100, $"only {Currencies.Names.Count} codes");
        foreach (var code in new[] { "USD", "EUR", "GBP", "JPY", "CHF", "IRR", "TRY", "AED", "XDR" })
        {
            Assert.True(Currencies.Names.ContainsKey(code), $"missing {code}");
            Assert.False(string.IsNullOrWhiteSpace(Currencies.Names[code]), $"no name for {code}");
        }
        Assert.All(Currencies.Names.Keys, k => Assert.Equal(k, k.Trim().ToUpperInvariant()));
    }

    [Fact]
    public void Names_does_not_offer_metal_codes_the_providers_cannot_price()
    {
        // Yahoo has no XAUUSD=X and er-api does not carry it, so offering these
        // would let a user pick a base currency every value fails to convert to.
        // Gold stays reachable as GC=F, PAXG/XAUT, or a manual holding.
        foreach (var metal in new[] { "XAU", "XAG", "XPT", "XPD" })
            Assert.False(Currencies.Names.ContainsKey(metal), $"{metal} should not be selectable");
    }

    [Fact]
    public void NameOf_falls_back_to_the_code_itself()
    {
        Assert.Equal("Euro", Currencies.NameOf("EUR"));
        Assert.Equal("Euro", Currencies.NameOf("eur"));    // case-insensitive lookup
        Assert.Equal("ZZZ", Currencies.NameOf("ZZZ"));     // unknown -> the code
        Assert.Equal("", Currencies.NameOf(null!));
        Assert.Equal("", Currencies.NameOf("  "));
    }

    [Fact]
    public void Search_matches_codes_and_names_locally()
    {
        // Currency search never touches the network, so it can run per keystroke.
        var euro = Currencies.Search("euro", 10).ToList();
        Assert.Contains("EUR", euro);

        var code = Currencies.Search("irr", 10).ToList();
        Assert.Contains("IRR", code);

        var rial = Currencies.Search("rial", 10).ToList();
        Assert.Contains("IRR", rial);

        Assert.Empty(Currencies.Search("   ", 10));
        Assert.Empty(Currencies.Search(null!, 10));
        Assert.True(Currencies.Search("a", 5).Count() <= 5, "take must be honoured");
    }
}
