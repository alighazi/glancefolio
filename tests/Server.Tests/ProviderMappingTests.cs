using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// Yahoo's own type strings are what make auto-categorisation trustworthy, so
/// the mapping is pinned here: SPY must stay a fund and ^GSPC an index, because
/// guessing from the shape of a ticker is how a gold coin ends up priced as a
/// shell company.
/// </summary>
public class ProviderMappingTests
{
    [Theory]
    [InlineData("EQUITY", AssetKind.Stock)]
    [InlineData("ETF", AssetKind.Etf)]
    [InlineData("MUTUALFUND", AssetKind.Etf)]
    [InlineData("INDEX", AssetKind.Index)]
    [InlineData("FUTURE", AssetKind.Future)]
    [InlineData("CURRENCY", AssetKind.Currency)]
    [InlineData("CRYPTOCURRENCY", AssetKind.Crypto)]
    public void From_maps_every_quote_type_yahoo_returns(string quoteType, AssetKind expected)
    {
        Assert.Equal(expected, YahooKinds.From(quoteType));
    }

    [Theory]
    [InlineData("equity")]
    [InlineData("  ETF  ")]
    [InlineData("index")]
    public void From_ignores_case_and_padding(string quoteType)
    {
        Assert.NotNull(YahooKinds.From(quoteType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OPTION")]        // deliberately not tracked
    [InlineData("WARRANT")]
    [InlineData("SOMETHING_NEW")] // an unmapped future type must not become a stock
    public void From_returns_null_for_anything_it_does_not_know(string? quoteType)
    {
        Assert.Null(YahooKinds.From(quoteType));
    }

    [Fact]
    public void An_unknown_type_is_dropped_rather_than_misfiled()
    {
        // The search pipeline skips null kinds, so a type Yahoo adds later
        // shows up as "no results" instead of as a bogus equity.
        Assert.All(new[] { "OPTION", "WARRANT", "" }, t => Assert.Null(YahooKinds.From(t)));
    }

    [Theory]
    // The collision that started all this: the same four letters, two assets.
    // Yahoo lists crypto as a "-USD" pair; the search pipeline strips that
    // suffix before categorising, which is what these rows reflect.
    [InlineData("PAXG", AssetKind.Stock, Categories.Stocks)]           // Pine Tree Acquisition Corp.
    [InlineData("PAXG", AssetKind.Crypto, Categories.Commodities)]     // PAX Gold, tokenised metal
    [InlineData("BTC", AssetKind.Crypto, Categories.Crypto)]
    [InlineData("USDT", AssetKind.Crypto, Categories.Cash)]
    [InlineData("GC=F", AssetKind.Future, Categories.Commodities)]
    [InlineData("^GSPC", AssetKind.Index, Categories.Indices)]
    [InlineData("SPY", AssetKind.Etf, Categories.Funds)]
    [InlineData("EURUSD=X", AssetKind.Currency, Categories.Cash)]
    public void The_provider_classification_decides_the_category(string symbol, AssetKind kind, string expected)
    {
        // Same symbol, different provider classification, different category:
        // the kind is what disambiguates PAXG, not the letters.
        Assert.Equal(expected, Categories.AutoDetect(kind, assetId: null, symbol));
    }
}
