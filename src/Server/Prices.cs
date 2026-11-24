namespace PortfolioWeb;

// ===========================================================================
// Prices
// ===========================================================================

/// <summary>
/// Resolves every holding to one number: USD per unit.
///
/// Order of preference is also order of cost. Crypto prices come out of the
/// cached top-250 index first, so a portfolio of well-known coins needs no
/// price request at all; only ids outside the index go to /simple/price, and
/// they go in a single batched call. Currencies come from the one cached FX
/// table. Equities are the only per-symbol cost, because Yahoo's batch quote
/// endpoint is crumb-gated and answers 401 without a session.
/// </summary>
public sealed class PriceService
{
    private readonly FxService _fx;
    private readonly CryptoService _crypto;
    private readonly YahooService _yahoo;
    private readonly ILogger<PriceService> _log;

    public PriceService(FxService fx, CryptoService crypto, YahooService yahoo, ILogger<PriceService> log)
    {
        _fx = fx;
        _crypto = crypto;
        _yahoo = yahoo;
        _log = log;
    }

    /// <summary>Quotes keyed by holding id.</summary>
    public async Task<QuoteSet> GetQuotesAsync(IReadOnlyList<Holding> holdings, CancellationToken ct = default)
    {
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        var problems = new List<string>();
        if (holdings.Count == 0) return new QuoteSet(quotes, null, false, problems);

        var fx = await _fx.GetTableAsync(ct).ConfigureAwait(false);
        if (!fx.Available)
            problems.Add(fx.Error ?? "FX table unavailable");

        // ---- manual holdings: the user is the price provider ---------------
        foreach (var h in holdings.Where(h => h.IsManual))
        {
            quotes[h.Id] = ManualQuote(h, fx);
        }

        // ---- currencies ----------------------------------------------------
        foreach (var h in holdings.Where(h => !h.IsManual && h.Kind == AssetKind.Currency))
        {
            var usd = fx.UsdPerUnit(h.AssetId);
            if (usd is > 0)
            {
                quotes[h.Id] = new PriceQuote
                {
                    AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = usd, Source = "er-api",
                    AsOfUtc = fx.AsOfUtc, Stale = fx.Stale
                };
                continue;
            }

            // Yahoo's own search offers FX pairs as ids like "EURUSD=X", which
            // the ISO table will never contain. Those still price fine, just
            // through the other provider.
            if (h.AssetId.Contains('=') || h.AssetId.Contains('-'))
            {
                quotes[h.Id] = await YahooQuoteAsync(h, fx, ct).ConfigureAwait(false);
                continue;
            }

            quotes[h.Id] = Failed(h, fx.Error is not null
                ? $"FX table unavailable ({fx.Error})"
                : $"no FX rate for {h.AssetId}");
        }

        // ---- crypto: cached index first, batched call for the rest ---------
        var cryptoHoldings = holdings.Where(h => !h.IsManual && h.Kind == AssetKind.Crypto).ToList();

        var misses = new List<Holding>();
        if (cryptoHoldings.Count > 0)
        {
            var index = await _crypto.GetTopIndexAsync(ct).ConfigureAwait(false);
            var byId = index
                .Where(c => c.CurrentPrice is > 0)
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var h in cryptoHoldings)
            {
                if (byId.TryGetValue(h.AssetId, out var coin))
                {
                    quotes[h.Id] = new PriceQuote
                    {
                        AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = coin.CurrentPrice,
                        Source = "coingecko-index", AsOfUtc = DateTimeOffset.UtcNow
                    };
                }
                else
                {
                    misses.Add(h);
                }
            }

            // Yahoo-listed crypto pairs (id looks like "PAXG-USD") are not in
            // the CoinGecko index; route them to Yahoo with the equities.
            var yahooCoins = misses.Where(h => h.AssetId.Contains('-')).ToList();
            misses = misses.Where(h => !h.AssetId.Contains('-')).ToList();

            if (misses.Count > 0)
            {
                var prices = await _crypto.GetPricesAsync(misses.Select(h => h.AssetId), ct).ConfigureAwait(false);
                foreach (var h in misses)
                {
                    quotes[h.Id] = prices.TryGetValue(h.AssetId, out var p) && p > 0
                        ? new PriceQuote
                        {
                            AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = p,
                            Source = "coingecko", AsOfUtc = DateTimeOffset.UtcNow
                        }
                        : Failed(h, $"CoinGecko has no USD price for \"{h.AssetId}\"");
                }
            }

            foreach (var h in yahooCoins)
            {
                quotes[h.Id] = await YahooQuoteAsync(h, fx, ct).ConfigureAwait(false);
            }
        }

        // ---- equities, ETFs, indices, futures ------------------------------
        var yahooHoldings = holdings.Where(h =>
            !h.IsManual &&
            (h.Kind is AssetKind.Stock or AssetKind.Etf or AssetKind.Index or AssetKind.Future)).ToList();

        // Sequentially, on purpose: the token bucket spaces these out, and a
        // parallel burst is exactly what makes an unofficial endpoint 429.
        foreach (var h in yahooHoldings)
        {
            quotes[h.Id] = await YahooQuoteAsync(h, fx, ct).ConfigureAwait(false);
        }

        // Anything left over (an unrecognised kind) gets an honest error rather
        // than silently valuing at zero.
        foreach (var h in holdings.Where(h => !quotes.ContainsKey(h.Id)))
        {
            quotes[h.Id] = Failed(h, $"no price source for kind \"{h.Kind}\"");
        }

        foreach (var kv in quotes.Where(kv => kv.Value.Error is not null))
            problems.Add($"{SymbolOf(holdings, kv.Key)}: {kv.Value.Error}");

        return new QuoteSet(quotes, fx.AsOfUtc, fx.Stale, problems);
    }

    private static string SymbolOf(IReadOnlyList<Holding> holdings, string id) =>
        holdings.FirstOrDefault(h => h.Id == id)?.Symbol ?? id;

    private async Task<PriceQuote> YahooQuoteAsync(Holding h, FxTable fx, CancellationToken ct)
    {
        var q = await _yahoo.QuoteAsync(h.AssetId, ct).ConfigureAwait(false);
        if (!q.Ok)
            return Failed(h, q.Error ?? "no quote");

        // A Tokyo listing is quoted in JPY, so put it through the FX table
        // rather than pretending the number is already dollars.
        var cur = q.Currency;
        decimal usd;
        if (string.Equals(cur, "USD", StringComparison.OrdinalIgnoreCase))
        {
            usd = q.Price;
        }
        else
        {
            var per = fx.UsdPerUnit(cur);
            if (per is null)
                return Failed(h, $"quoted in {cur} with no FX path to USD");
            usd = q.Price * per.Value;
        }

        return new PriceQuote
        {
            AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = usd, Source = "yahoo",
            AsOfUtc = q.AsOfUtc, Stale = q.Stale, Error = q.Error
        };
    }

    private static PriceQuote ManualQuote(Holding h, FxTable fx)
    {
        var price = h.ManualUnitPrice;
        if (price is null or <= 0)
            return Failed(h, "manual holding has no price per unit");

        var cur = string.IsNullOrWhiteSpace(h.ManualPriceCurrency) ? "USD" : h.ManualPriceCurrency.Trim().ToUpperInvariant();
        if (string.Equals(cur, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return new PriceQuote
            {
                AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = price, Source = "manual",
                AsOfUtc = DateTimeOffset.UtcNow
            };
        }

        var per = fx.UsdPerUnit(cur);
        if (per is null)
            return Failed(h, $"manual price is in {cur}, which has no FX path to USD");

        return new PriceQuote
        {
            AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = price.Value * per.Value,
            Source = "manual", AsOfUtc = DateTimeOffset.UtcNow
        };
    }

    private static PriceQuote Failed(Holding h, string error) => new()
    {
        AssetId = h.AssetId, Kind = h.Kind, UsdPerUnit = null, Source = "none", Error = error
    };
}

/// <summary>Quotes for one valuation pass, plus the FX context they used.</summary>
public sealed record QuoteSet(
    IReadOnlyDictionary<string, PriceQuote> ByHoldingId,
    DateTimeOffset? FxAsOfUtc,
    bool FxStale,
    IReadOnlyList<string> Problems);

// ===========================================================================
// Valuation / reporting
// ===========================================================================

/// <summary>
/// Turns holdings + quotes into the report: per-holding values in the base
/// currency, category totals with weights and gain/loss, and a secondary
/// sector breakdown for the part of the portfolio that has one.
///
/// Pure and synchronous, so it is unit-testable without a network.
/// </summary>
public static class Valuation
{
    private const int Money = 2;
    private const int Weight = 2;

    /// <param name="asOfUtc">
    /// Report timestamp. Defaults to now; a test passes a fixed value so Build
    /// stays deterministic and does not read the clock itself.
    /// </param>
    public static ValuationReport Build(Portfolio portfolio, QuoteSet quotes, FxTable fx,
                                        DateTimeOffset? asOfUtc = null)
    {
        var baseCur = string.IsNullOrWhiteSpace(portfolio.BaseCurrency)
            ? "USD"
            : portfolio.BaseCurrency.Trim().ToUpperInvariant();

        // The user's own list, in their order. Empty means a portfolio written
        // before categories were editable, and falls back to the defaults.
        var taxonomy = portfolio.Categories is { Count: > 0 }
            ? portfolio.Categories
            : Categories.Defaults;
        var order = taxonomy.Select(c => c.Name).ToList();

        // USD -> base multiplier. When the base is unknown to the FX table we
        // fall back to 1:00 rather than blanking the whole report, and say so.
        var problems = quotes.Problems.ToList();
        var usdToBase = baseCur == "USD" ? 1m : fx.Rate("USD", baseCur);
        if (usdToBase is null or <= 0)
        {
            problems.Add($"no FX path from USD to {baseCur}; values are shown in USD");
            usdToBase = 1m;
        }

        var rows = new List<HoldingValuation>();
        foreach (var h in portfolio.Holdings)
        {
            quotes.ByHoldingId.TryGetValue(h.Id, out var quote);
            var category = Categories.Effective(h, taxonomy);

            decimal? unitBase = null;
            decimal? valueBase = null;
            if (quote?.UsdPerUnit is > 0)
            {
                unitBase = Round(quote.UsdPerUnit.Value * usdToBase.Value, 6);
                valueBase = Round(h.Quantity * unitBase.Value, Money);
            }

            decimal? costBase = null;
            if (h.UnitCost is > 0)
            {
                var costCur = string.IsNullOrWhiteSpace(h.CostCurrency) ? baseCur : h.CostCurrency.Trim().ToUpperInvariant();
                var toBase = string.Equals(costCur, baseCur, StringComparison.OrdinalIgnoreCase) ? 1m : fx.Rate(costCur, baseCur);
                if (toBase is > 0) costBase = Round(h.Quantity * h.UnitCost.Value * toBase.Value, Money);
                else problems.Add($"{h.Symbol}: cost basis is in {costCur}, which has no FX path to {baseCur}");
            }

            decimal? gain = null, gainPct = null;
            if (valueBase is not null && costBase is not null)
            {
                gain = Round(valueBase.Value - costBase.Value, Money);
                if (costBase.Value != 0) gainPct = Round(gain.Value / costBase.Value * 100m, Weight);
            }

            rows.Add(new HoldingValuation
            {
                Id = h.Id,
                Symbol = string.IsNullOrWhiteSpace(h.Symbol) ? h.Name : h.Symbol,
                Name = h.Name,
                Kind = h.Kind,
                IconUrl = h.IconUrl,
                Category = category,
                DetectedCategory = Categories.Detected(h, taxonomy),
                Sector = NullIfEmpty(h.Sector),
                Quantity = h.Quantity,
                UnitPriceBase = unitBase,
                ValueBase = valueBase,
                CostBase = costBase,
                GainBase = gain,
                GainPct = gainPct,
                Manual = h.IsManual,
                Stale = quote?.Stale ?? false,
                AsOfUtc = quote?.AsOfUtc,
                Error = quote?.Error
            });
        }

        var total = Round(rows.Where(r => r.ValueBase is not null).Sum(r => r.ValueBase!.Value), Money);

        // Weights only make sense against a positive total.
        if (total > 0)
        {
            rows = rows
                .Select(r => r.ValueBase is null
                    ? r
                    : r with { WeightPct = Round(r.ValueBase.Value / total * 100m, Weight) })
                .ToList();
        }

        var byCategory = Group(rows, r => r.Category, total, order);
        // Sectors are free text from the provider, never the user's categories,
        // so they get no preferred order and sort alphabetically.
        var bySector = Group(rows.Where(r => !string.IsNullOrWhiteSpace(r.Sector)), r => r.Sector!, total, []);

        var uncategorised = rows.Count(r => r.Category == Categories.Uncategorised);
        if (uncategorised > 0)
        {
            problems.Add(uncategorised == 1
                ? "1 holding matches no category - the category it was detected into has been deleted"
                : $"{uncategorised} holdings match no category - the category they were detected into has been deleted");
        }

        var priced = rows.Count(r => r.ValueBase is not null);
        var cost = rows.Where(r => r.CostBase is not null).Sum(r => r.CostBase!.Value);
        decimal? totalCost = rows.Any(r => r.CostBase is not null) ? Round(cost, Money) : null;
        decimal? totalGain = null, totalGainPct = null;
        if (totalCost is not null && totalCost > 0)
        {
            // Gain is only meaningful over holdings that have both a value and
            // a cost, otherwise a missing quote looks like a loss.
            var comparable = rows.Where(r => r.ValueBase is not null && r.CostBase is not null);
            var v = comparable.Sum(r => r.ValueBase!.Value);
            var c = comparable.Sum(r => r.CostBase!.Value);
            totalGain = Round(v - c, Money);
            if (c != 0) totalGainPct = Round((v - c) / c * 100m, Weight);
        }

        return new ValuationReport
        {
            BaseCurrency = baseCur,
            TotalBase = total,
            TotalCostBase = totalCost,
            TotalGainBase = totalGain,
            TotalGainPct = totalGainPct,
            ByCategory = byCategory,
            BySector = bySector,
            Holdings = rows.OrderByDescending(r => r.ValueBase ?? 0m).ToList(),
            Problems = problems.Distinct(StringComparer.Ordinal).ToList(),
            AsOfUtc = asOfUtc ?? DateTimeOffset.UtcNow,
            PricedCount = priced,
            UnpricedCount = rows.Count - priced,
            StaleCount = rows.Count(r => r.Stale)
        };
    }

    /// <summary>
    /// Groups in the canonical category order (so the report does not reshuffle
    /// between refreshes), with empty groups omitted and any group not in the
    /// taxonomy appended alphabetically.
    /// </summary>
    private static List<GroupTotal> Group(IEnumerable<HoldingValuation> rows,
                                          Func<HoldingValuation, string> key, decimal total,
                                          IReadOnlyList<string> order)
    {
        var groups = rows
            .GroupBy(key)
            .Select(g =>
            {
                var value = Round(g.Sum(r => r.ValueBase ?? 0m), Money);
                var costRows = g.Where(r => r.CostBase is not null && r.ValueBase is not null).ToList();
                decimal? cost = costRows.Count > 0 ? Round(costRows.Sum(r => r.CostBase!.Value), Money) : null;
                decimal? gain = cost is not null ? Round(costRows.Sum(r => r.ValueBase!.Value) - cost.Value, Money) : null;
                decimal? gainPct = cost is > 0 ? Round(gain!.Value / cost.Value * 100m, Weight) : null;

                return new GroupTotal
                {
                    Name = g.Key,
                    ValueBase = value,
                    WeightPct = total > 0 ? Round(value / total * 100m, Weight) : 0m,
                    Count = g.Count(),
                    CostBase = cost,
                    GainBase = gain,
                    GainPct = gainPct
                };
            })
            .ToList();

        var ordered = new List<GroupTotal>();
        foreach (var name in order)
        {
            var hit = groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) ordered.Add(hit);
        }
        ordered.AddRange(groups
            .Where(g => !order.Any(c => string.Equals(c, g.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g.Name, StringComparer.Ordinal));

        return ordered.Where(g => g.Count > 0).ToList();
    }

    private static decimal Round(decimal v, int decimals) => Math.Round(v, decimals, MidpointRounding.AwayFromZero);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
