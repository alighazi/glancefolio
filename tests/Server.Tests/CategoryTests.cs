using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// Category assignment is the one place where a wrong answer is invisible: the
/// number still looks right, the report just quietly puts gold in with crypto.
/// These pin the taxonomy, the two overrides, and what a user choice does.
/// </summary>
public class CategoryTests
{
    [Theory]
    // The PAXG regression: a tokenised metal must not land in Crypto just
    // because its provider is a crypto exchange.
    [InlineData(AssetKind.Crypto, "pax-gold", "PAXG", Categories.Commodities)]
    [InlineData(AssetKind.Crypto, "tether-gold", "XAUT", Categories.Commodities)]
    [InlineData(AssetKind.Crypto, "kinesis-gold", "KAU", Categories.Commodities)]
    [InlineData(AssetKind.Crypto, "meld-gold", "PMGT", Categories.Commodities)]
    // Stablecoins are cash, not an investment.
    [InlineData(AssetKind.Crypto, "tether", "USDT", Categories.Cash)]
    [InlineData(AssetKind.Crypto, "usd-coin", "USDC", Categories.Cash)]
    [InlineData(AssetKind.Crypto, "dai", "DAI", Categories.Cash)]
    [InlineData(AssetKind.Crypto, "paypal-usd", "PYUSD", Categories.Cash)]
    // Ordinary coins.
    [InlineData(AssetKind.Crypto, "bitcoin", "BTC", Categories.Crypto)]
    [InlineData(AssetKind.Crypto, "ethereum", "ETH", Categories.Crypto)]
    // Listed instruments, straight from Yahoo's own quoteType.
    [InlineData(AssetKind.Stock, "AAPL", "AAPL", Categories.Stocks)]
    [InlineData(AssetKind.Stock, "7203.T", "7203.T", Categories.Stocks)]
    [InlineData(AssetKind.Etf, "SPY", "SPY", Categories.Funds)]
    [InlineData(AssetKind.Etf, "VWCE.L", "VWCE.L", Categories.Funds)]
    [InlineData(AssetKind.Index, "^GSPC", "^GSPC", Categories.Indices)]
    [InlineData(AssetKind.Index, "^IXIC", "^IXIC", Categories.Indices)]
    [InlineData(AssetKind.Future, "GC=F", "GC=F", Categories.Commodities)]
    [InlineData(AssetKind.Future, "CL=F", "CL=F", Categories.Commodities)]
    [InlineData(AssetKind.Future, "SI=F", "SI=F", Categories.Commodities)]
    [InlineData(AssetKind.Currency, "EUR", "EUR", Categories.Cash)]
    [InlineData(AssetKind.Currency, "EURUSD=X", "EURUSD=X", Categories.Cash)]
    [InlineData(AssetKind.Currency, "IRR", "IRR", Categories.Cash)]
    public void AutoDetect_uses_the_provider_classification(AssetKind kind, string assetId, string symbol, string expected)
    {
        Assert.Equal(expected, Categories.AutoDetect(kind, assetId, symbol));
    }

    [Fact]
    public void AutoDetect_is_case_insensitive_about_the_metal_and_stablecoin_overrides()
    {
        Assert.Equal(Categories.Commodities, Categories.AutoDetect(AssetKind.Crypto, "PAX-GOLD", "paxg"));
        Assert.Equal(Categories.Commodities, Categories.AutoDetect(AssetKind.Crypto, null, "Paxg"));
        Assert.Equal(Categories.Cash, Categories.AutoDetect(AssetKind.Crypto, "TETHER", "usdt"));
        Assert.Equal(Categories.Cash, Categories.AutoDetect(AssetKind.Crypto, null, "Usdt"));
    }

    [Fact]
    public void AutoDetect_never_picks_a_manual_only_category()
    {
        // Real estate, bonds and collectibles describe things no provider can
        // classify, so only a human may choose them. "Other" is the exception:
        // it is what a manual holding (AssetKind.Other) genuinely is.
        var listed = Enum.GetValues<AssetKind>().Where(k => k != AssetKind.Other).ToArray();
        foreach (var kind in listed)
        {
            foreach (var probe in new[] { null, "", "X", "REIT", "BOND", "HOUSE", "STAMP" })
            {
                var detected = Categories.AutoDetect(kind, probe, probe);
                Assert.DoesNotContain(detected, Categories.ManualOnly);
                Assert.Contains(detected, Categories.All);
            }
        }

        Assert.Equal(Categories.Other, Categories.AutoDetect(AssetKind.Other, "anything", "ANYTHING"));
    }

    [Fact]
    public void All_is_the_report_order_and_covers_the_manual_only_subset()
    {
        Assert.Equal(
            new[] { "Cash", "Crypto", "Stocks", "ETFs & Funds", "Commodities", "Indices", "Bonds", "Real Estate", "Collectibles", "Other" },
            Categories.All.ToArray());
        Assert.Equal(new[] { "Real Estate", "Bonds", "Collectibles", "Other" }, Categories.ManualOnly.ToArray());
        Assert.All(Categories.ManualOnly, m => Assert.Contains(m, Categories.All));
        Assert.Equal(Categories.All.Count, Categories.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Effective_prefers_the_user_choice_over_auto_detection()
    {
        var paxg = new Holding { Kind = AssetKind.Crypto, AssetId = "pax-gold", Symbol = "PAXG", Quantity = 1m };

        Assert.Equal(Categories.Commodities, Categories.Effective(paxg));          // auto-detected
        paxg.Category = "Collectibles";
        Assert.Equal("Collectibles", Categories.Effective(paxg));                  // user wins
        paxg.Category = "  cash  ";
        Assert.Equal("Cash", Categories.Effective(paxg));                          // trimmed, case-insensitive
    }

    [Fact]
    public void Effective_falls_back_to_auto_detection_for_an_unknown_choice()
    {
        // A hand-edited file could carry a category that is not in the
        // taxonomy. Silently reporting it would invent an eleventh group, so it
        // is treated as "not chosen".
        var h = new Holding { Kind = AssetKind.Stock, AssetId = "AAPL", Symbol = "AAPL", Quantity = 1m, Category = "Unicorns" };
        Assert.Equal(Categories.Stocks, Categories.Effective(h));

        h.Category = "   ";
        Assert.Equal(Categories.Stocks, Categories.Effective(h));
        h.Category = null;
        Assert.Equal(Categories.Stocks, Categories.Effective(h));
    }

    [Theory]
    [InlineData("pax-gold", "PAXG", true)]
    [InlineData("tether-gold", "XAUT", true)]
    [InlineData("bitcoin", "BTC", false)]
    [InlineData(null, "PAXG", true)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void IsTokenisedMetal_recognises_metal_coins_by_id_or_symbol(string? id, string? symbol, bool expected)
    {
        Assert.Equal(expected, Categories.IsTokenisedMetal(id, symbol));
    }

    [Theory]
    [InlineData("tether", "USDT", true)]
    [InlineData("usd-coin", "USDC", true)]
    [InlineData("bitcoin", "BTC", false)]
    [InlineData(null, "USDT", true)]
    [InlineData(null, null, false)]
    public void IsStablecoin_recognises_pegged_coins(string? id, string? symbol, bool expected)
    {
        Assert.Equal(expected, Categories.IsStablecoin(id, symbol));
    }
}
