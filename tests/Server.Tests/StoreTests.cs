using Microsoft.Extensions.Logging.Abstractions;
using PortfolioWeb;

namespace Server.Tests;

/// <summary>
/// The portfolio file is the user's only copy of their holdings, so persistence
/// is tested for the things that lose data: a crash mid-write, two edits at
/// once, a corrupt file, and a hand-edited file with values the report cannot
/// use.
/// </summary>
public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "portfolio-store-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly string _path;

    public StoreTests() => _path = Path.Combine(_dir, "nested", "portfolio.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private PortfolioStore NewStore() => new(_path, NullLogger<PortfolioStore>.Instance);

    private static Holding H(string symbol, AssetKind kind = AssetKind.Stock, decimal qty = 1m, string assetId = "") => new()
    {
        Symbol = symbol,
        Name = symbol,
        Kind = kind,
        AssetId = string.IsNullOrEmpty(assetId) ? symbol : assetId,
        Quantity = qty,
    };

    [Fact]
    public async Task A_missing_file_starts_empty_instead_of_failing()
    {
        Assert.False(File.Exists(_path));

        var p = await NewStore().GetAsync();

        Assert.Empty(p.Holdings);
        Assert.Equal("USD", p.BaseCurrency);
    }

    [Fact]
    public async Task The_directory_is_created_and_the_path_is_reported()
    {
        var store = NewStore();
        await store.GetAsync();

        Assert.True(Directory.Exists(Path.GetDirectoryName(_path)!));
        Assert.Equal(_path, store.FilePath);
    }

    [Fact]
    public async Task An_update_is_written_to_disk_and_read_back()
    {
        var store = NewStore();
        await store.UpdateAsync(p => { p.Holdings.Add(H("AAPL", qty: 10m)); return p; });

        // A brand new instance proves it came from the file, not from memory.
        var reloaded = await NewStore().GetAsync();

        Assert.Single(reloaded.Holdings);
        Assert.Equal("AAPL", reloaded.Holdings[0].Symbol);
        Assert.Equal(10m, reloaded.Holdings[0].Quantity);
        Assert.NotEqual("AAPL", reloaded.Holdings[0].Id);   // an id was assigned
        Assert.False(string.IsNullOrWhiteSpace(reloaded.Holdings[0].Id));
    }

    [Fact]
    public async Task The_file_is_valid_json_and_round_trips_every_field()
    {
        var holding = new Holding
        {
            Kind = AssetKind.Crypto,
            AssetId = "pax-gold",
            Symbol = "PAXG",
            Name = "PAX Gold",
            IconUrl = "https://example.invalid/paxg.png",
            Exchange = "CCC",
            Sector = null,
            Industry = null,
            Quantity = 2.5m,
            Category = "Commodities",
            UnitCost = 3000m,
            CostCurrency = "USD",
            Note = "cold wallet",
        };
        await NewStore().UpdateAsync(p => { p.BaseCurrency = "EUR"; p.Holdings.Add(holding); return p; });

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        Assert.Equal("EUR", doc.RootElement.GetProperty("baseCurrency").GetString());

        var back = (await NewStore().GetAsync()).Holdings.Single();
        Assert.Equal(holding.Kind, back.Kind);
        Assert.Equal("pax-gold", back.AssetId);
        Assert.Equal("PAX Gold", back.Name);
        Assert.Equal("https://example.invalid/paxg.png", back.IconUrl);
        Assert.Equal("CCC", back.Exchange);
        Assert.Equal(2.5m, back.Quantity);
        Assert.Equal("Commodities", back.Category);
        Assert.Equal(3000m, back.UnitCost);
        Assert.Equal("USD", back.CostCurrency);
        Assert.Equal("cold wallet", back.Note);
        Assert.Null(back.ManualUnitPrice);
    }

    [Fact]
    public async Task Concurrent_updates_lose_nothing()
    {
        // The semaphore is what stops two requests from each writing a copy of
        // the portfolio that does not contain the other's holding.
        var store = NewStore();
        await store.GetAsync();

        await Task.WhenAll(Enumerable.Range(0, 25).Select(i =>
            store.UpdateAsync(p => { p.Holdings.Add(H($"SYM{i}", qty: i)); return p; }, CancellationToken.None)));

        var reloaded = await NewStore().GetAsync();
        Assert.Equal(25, reloaded.Holdings.Count);
        Assert.Equal(25, reloaded.Holdings.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task No_temporary_files_are_left_behind()
    {
        var store = NewStore();
        for (var i = 0; i < 10; i++)
            await store.UpdateAsync(p => { p.Holdings.Add(H($"S{i}")); return p; });

        var dir = Path.GetDirectoryName(_path)!;
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task The_previous_version_is_kept_as_a_backup()
    {
        var store = NewStore();
        await store.UpdateAsync(p => { p.Holdings.Add(H("FIRST")); return p; });
        await store.UpdateAsync(p => { p.Holdings.Add(H("SECOND")); return p; });

        var backup = _path + ".bak";
        Assert.True(File.Exists(backup), "a .bak copy should exist after the second write");
        Assert.Contains("FIRST", await File.ReadAllTextAsync(backup), StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND", await File.ReadAllTextAsync(backup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_file_starts_clean_and_is_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{\"holdings\": [ this is not json");
        var before = await File.ReadAllTextAsync(_path);

        var p = await NewStore().GetAsync();

        Assert.Empty(p.Holdings);
        Assert.Equal("USD", p.BaseCurrency);
        Assert.Equal(before, await File.ReadAllTextAsync(_path));   // the damage is preserved for inspection
    }

    // ------------------------------------------------------------ Normalize

    [Fact]
    public void Normalize_defaults_a_blank_base_currency_to_usd()
    {
        Assert.Equal("USD", PortfolioStore.Normalize(new Portfolio { BaseCurrency = "" }).BaseCurrency);
        Assert.Equal("USD", PortfolioStore.Normalize(new Portfolio { BaseCurrency = "   " }).BaseCurrency);
        Assert.Equal("EUR", PortfolioStore.Normalize(new Portfolio { BaseCurrency = " eur " }).BaseCurrency);
    }

    [Fact]
    public void Normalize_keeps_a_base_currency_it_does_not_recognise()
    {
        // What the app can convert depends on the live FX payload (~166 codes),
        // which is wider than the built-in table (107). Clamping here would
        // silently rewrite a good choice like ANG into USD on every load, so
        // Normalize only trims and uppercases. An unusable code is reported by
        // Valuation.Build ("values are shown in USD") and rejected on write.
        Assert.Equal("ANG", PortfolioStore.Normalize(new Portfolio { BaseCurrency = "ang" }).BaseCurrency);
        Assert.Equal("ZZZ", PortfolioStore.Normalize(new Portfolio { BaseCurrency = " zzz " }).BaseCurrency);
        Assert.Equal("IRR", PortfolioStore.Normalize(new Portfolio { BaseCurrency = "irr" }).BaseCurrency);
    }

    [Fact]
    public void Normalize_assigns_unique_ids()
    {
        var p = new Portfolio();
        p.Holdings.Add(new Holding { Id = "", Symbol = "A" });
        p.Holdings.Add(new Holding { Id = "  ", Symbol = "B" });
        p.Holdings.Add(new Holding { Id = "same", Symbol = "C" });
        p.Holdings.Add(new Holding { Id = "same", Symbol = "D" });

        var result = PortfolioStore.Normalize(p);

        Assert.Equal(4, result.Holdings.Count);
        Assert.Equal(4, result.Holdings.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Holdings, h => Assert.False(string.IsNullOrWhiteSpace(h.Id)));
    }

    [Fact]
    public void Normalize_fills_in_a_missing_name_and_asset_id_from_the_symbol()
    {
        var p = new Portfolio();
        p.Holdings.Add(new Holding { Symbol = " AAPL ", Name = "", AssetId = "  " });

        var h = PortfolioStore.Normalize(p).Holdings.Single();

        Assert.Equal("AAPL", h.Symbol);
        Assert.Equal("AAPL", h.Name);
        Assert.Equal("AAPL", h.AssetId);
    }

    [Fact]
    public void Normalize_clears_an_unknown_category_so_detection_can_choose()
    {
        var p = new Portfolio();
        p.Holdings.Add(new Holding { Symbol = "A", Category = "Unicorns" });
        p.Holdings.Add(new Holding { Symbol = "B", Category = " cash " });
        p.Holdings.Add(new Holding { Symbol = "C", Category = "ETFs & Funds" });
        p.Holdings.Add(new Holding { Symbol = "D", Category = "" });

        var result = PortfolioStore.Normalize(p);

        Assert.Null(result.Holdings[0].Category);
        Assert.Equal("Cash", result.Holdings[1].Category);
        Assert.Equal("ETFs & Funds", result.Holdings[2].Category);
        Assert.Null(result.Holdings[3].Category);
    }

    [Fact]
    public void Normalize_drops_prices_that_cannot_be_used()
    {
        var p = new Portfolio();
        p.Holdings.Add(new Holding { Symbol = "A", ManualUnitPrice = 0m });
        p.Holdings.Add(new Holding { Symbol = "B", ManualUnitPrice = -5m });
        p.Holdings.Add(new Holding { Symbol = "C", ManualUnitPrice = 12.5m });
        p.Holdings.Add(new Holding { Symbol = "D", UnitCost = -1m });
        p.Holdings.Add(new Holding { Symbol = "E", UnitCost = 0m });

        var result = PortfolioStore.Normalize(p);

        // A manual price of zero would value the holding at nothing, which is
        // indistinguishable from a failed fetch. A zero cost basis is legal:
        // it means "received for free".
        Assert.Null(result.Holdings[0].ManualUnitPrice);
        Assert.Null(result.Holdings[1].ManualUnitPrice);
        Assert.Equal(12.5m, result.Holdings[2].ManualUnitPrice);
        Assert.Null(result.Holdings[3].UnitCost);
        Assert.Equal(0m, result.Holdings[4].UnitCost);
    }

    [Fact]
    public void Normalize_blanks_empty_optional_strings()
    {
        var p = new Portfolio();
        p.Holdings.Add(new Holding
        {
            Symbol = "A",
            ManualPriceCurrency = "   ",
            CostCurrency = "",
            Note = "  ",
            Sector = " Technology ",
        });

        var h = PortfolioStore.Normalize(p).Holdings.Single();

        Assert.Null(h.ManualPriceCurrency);
        Assert.Null(h.CostCurrency);
        Assert.Null(h.Note);
        Assert.Equal("Technology", h.Sector);   // kept, but trimmed
    }

    [Fact]
    public void Normalize_keeps_every_holding_including_the_awkward_ones()
    {
        // Repairing a file must never silently delete someone's gold bar.
        var p = new Portfolio();
        p.Holdings.Add(new Holding { Symbol = "", Name = "", Quantity = 0m });
        p.Holdings.Add(new Holding { Symbol = "FINE", Quantity = 3m });

        var result = PortfolioStore.Normalize(p);
        Assert.Equal(2, result.Holdings.Count);
    }

    [Fact]
    public async Task A_hand_edited_file_is_normalized_on_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, """
        {
          "baseCurrency": " ang ",
          "holdings": [
            { "id": "", "kind": "Crypto", "assetId": "pax-gold", "symbol": " PAXG ", "name": "",
              "quantity": 2.5, "category": "Unicorns", "manualUnitPrice": 0 },
            { "id": "", "kind": "Crypto", "assetId": "pax-gold", "symbol": "PAXG", "name": "PAX Gold",
              "quantity": 1, "category": "Commodities" }
          ]
        }
        """);

        var p = await NewStore().GetAsync();

        Assert.Equal("ANG", p.BaseCurrency);   // a live-only code, kept as written
        Assert.Equal(2, p.Holdings.Count);
        Assert.Equal(2, p.Holdings.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("PAXG", p.Holdings[0].Symbol);
        Assert.Equal("PAXG", p.Holdings[0].Name);
        Assert.Null(p.Holdings[0].Category);
        Assert.Null(p.Holdings[0].ManualUnitPrice);
        Assert.Equal("Commodities", p.Holdings[1].Category);
        // Detection still works on what was loaded, which is what the report uses.
        Assert.Equal("Commodities", Categories.Effective(p.Holdings[0]));
    }
}
