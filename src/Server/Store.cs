using System.Text.Json;

namespace PortfolioWeb;

/// <summary>
/// Single-file portfolio persistence.
///
/// One portfolio, one file, one writer at a time: a semaphore guards the
/// load-modify-save cycle so two concurrent edits cannot lose one of them, and
/// writes go to a temp file that is then moved into place, so a crash mid-write
/// leaves the previous portfolio intact rather than a truncated one. The prior
/// version is kept next to it as `.bak` because this file is the user's only
/// copy of their holdings.
/// </summary>
public sealed class PortfolioStore
{
    /// <summary>Enough for anyone's real taxonomy, few enough that the chip row
    /// and the report stay usable.</summary>
    public const int MaxCategories = 40;
    public const int MaxCategoryNameLength = 40;

    private readonly string _path;
    private readonly ILogger<PortfolioStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Portfolio? _current;

    public PortfolioStore(string path, ILogger<PortfolioStore> log)
    {
        _path = path;
        _log = log;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>Absolute path of the portfolio file (the property is not named
    /// <c>Path</c> because that would shadow <see cref="System.IO.Path"/> here).</summary>
    public string FilePath => _path;

    public async Task<Portfolio> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _current ??= await ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Portfolio> UpdateAsync(Func<Portfolio, Portfolio> mutate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _current ??= await ReadAsync(ct).ConfigureAwait(false);
            var next = Normalize(mutate(current));
            next.UpdatedUtc = DateTime.UtcNow;
            await WriteAsync(next, ct).ConfigureAwait(false);
            _current = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Portfolio> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _log.LogInformation("no portfolio file at {Path}; starting empty", _path);
            return Normalize(new Portfolio());
        }
        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<Portfolio>(stream, Json.Options, ct).ConfigureAwait(false);
            return Normalize(loaded ?? new Portfolio());
        }
        catch (Exception ex)
        {
            // A corrupt file must not take the app down. Keep the bytes, start
            // clean, and tell the operator where the damage is.
            _log.LogError(ex, "could not read {Path}; starting empty and leaving the file untouched", _path);
            return Normalize(new Portfolio());
        }
    }

    private async Task WriteAsync(Portfolio p, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(p, Json.Options);
        var tmp = _path + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not keep a .bak of the portfolio");
        }
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Defensive cleanup applied on every load and save: unique ids, a usable
    /// base currency, categories from the known taxonomy only, and no NaN or
    /// infinite quantities (which JSON can carry in from a hand-edited file).
    /// </summary>
    public static Portfolio Normalize(Portfolio p)
    {
        p.BaseCurrency = string.IsNullOrWhiteSpace(p.BaseCurrency)
            ? "USD"
            : p.BaseCurrency.Trim().ToUpperInvariant();

        // Deliberately not checked against the currency list here: the codes the
        // app can convert depend on the live FX payload (~166), which is wider
        // than the built-in table (107), so clamping on load would silently
        // rewrite a perfectly good choice like ANG into USD. A code nothing can
        // convert is handled where it is visible - Valuation.Build shows the
        // values in USD and puts the reason in Problems - and the API rejects
        // unknown codes on the way in.

        // Categories first: what counts as a known category for a holding
        // depends on the user's list, not on the built-in defaults.
        p.Categories = NormalizeCategories(p.Categories);
        var categories = p.Categories;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<Holding>(p.Holdings.Count);

        foreach (var h in p.Holdings)
        {
            if (string.IsNullOrWhiteSpace(h.Id) || !seen.Add(h.Id))
            {
                h.Id = Holding.NewId();
                seen.Add(h.Id);
            }
            // decimal cannot hold NaN or infinity - an out-of-range number in a
            // hand-edited file fails deserialisation instead, which ReadAsync
            // already handles by starting clean.
            if (h.ManualUnitPrice is { } mp && mp <= 0) h.ManualUnitPrice = null;
            if (h.UnitCost is { } uc && uc < 0) h.UnitCost = null;

            h.Symbol = (h.Symbol ?? "").Trim();
            h.Name = string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name.Trim();
            h.AssetId = string.IsNullOrWhiteSpace(h.AssetId) ? h.Symbol : h.AssetId.Trim();
            if (string.IsNullOrWhiteSpace(h.ManualPriceCurrency)) h.ManualPriceCurrency = null;
            if (string.IsNullOrWhiteSpace(h.CostCurrency)) h.CostCurrency = null;

            // These are all displayed verbatim, so a hand-edited file with
            // padding would show up as misaligned rows in the UI.
            h.Exchange = NullIfBlank(h.Exchange);
            h.Sector = NullIfBlank(h.Sector);
            h.Industry = NullIfBlank(h.Industry);
            h.Note = NullIfBlank(h.Note);
            h.IconUrl = NullIfBlank(h.IconUrl);

            // A name that is not in the list would silently split the report, so
            // clear it and let detection choose. A known one is stored in its
            // canonical spelling, so " cash " and "Cash" group together.
            h.Category = CanonicalCategory(h.Category, categories);

            kept.Add(h);
        }

        p.Holdings = kept;
        return p;
    }

    /// <summary>
    /// Repairs the user's category list: seeds the defaults into a portfolio
    /// that predates them, trims, drops blanks and duplicates, forgets detection
    /// keys that are not real, and keeps two categories from receiving the same
    /// detected assets. The list is capped so a hand-edited file cannot make the
    /// chip row or the report unusable.
    /// </summary>
    public static List<CategoryDef> NormalizeCategories(List<CategoryDef>? incoming)
    {
        if (incoming is null || incoming.Count == 0) return Categories.DefaultList();

        var result = new List<CategoryDef>(incoming.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in incoming)
        {
            if (result.Count >= MaxCategories) break;
            var name = raw?.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > MaxCategoryNameLength) continue;
            if (!names.Add(name)) continue;

            var detect = raw!.Detect?.Trim();
            if (detect is { Length: > 0 })
            {
                detect = detect.ToLowerInvariant();
                // A key that is not one detection produces is a typo, and a key
                // already claimed would split the same assets across two groups.
                if (!Categories.DetectKeys.Contains(detect, StringComparer.Ordinal) || !keys.Add(detect))
                    detect = null;
            }
            else
            {
                detect = null;
            }

            result.Add(new CategoryDef { Name = name, Detect = detect });
        }

        // An empty list would leave every holding uncategorised, so fall back to
        // the defaults rather than to nothing.
        return result.Count > 0 ? result : Categories.DefaultList();
    }

    /// <summary>The taxonomy spelling of a category, or null if it is not one.</summary>
    private static string? CanonicalCategory(string? value, IReadOnlyList<CategoryDef> list)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return list.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
