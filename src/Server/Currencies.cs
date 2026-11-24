namespace PortfolioWeb;

/// <summary>
/// ISO 4217 codes and their English names, bundled so that searching for
/// "euro", "yen" or "dollar" costs zero API calls and works offline.
///
/// The live FX table (open.er-api.com) is the authority for which codes are
/// actually tradeable and what they are worth; this file only supplies names
/// for search and display. Anything in the FX table but missing here is still
/// searchable by its code.
/// </summary>
public static class Currencies
{
    public static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USD"] = "US Dollar",
            ["EUR"] = "Euro",
            ["GBP"] = "British Pound",
            ["JPY"] = "Japanese Yen",
            ["CHF"] = "Swiss Franc",
            ["CAD"] = "Canadian Dollar",
            ["AUD"] = "Australian Dollar",
            ["NZD"] = "New Zealand Dollar",
            ["CNY"] = "Chinese Yuan Renminbi",
            ["CNH"] = "Chinese Yuan (offshore)",
            ["HKD"] = "Hong Kong Dollar",
            ["SGD"] = "Singapore Dollar",
            ["SEK"] = "Swedish Krona",
            ["NOK"] = "Norwegian Krone",
            ["DKK"] = "Danish Krone",
            ["ISK"] = "Icelandic Krona",
            ["PLN"] = "Polish Zloty",
            ["CZK"] = "Czech Koruna",
            ["HUF"] = "Hungarian Forint",
            ["RON"] = "Romanian Leu",
            ["BGN"] = "Bulgarian Lev",
            ["HRK"] = "Croatian Kuna",
            ["RSD"] = "Serbian Dinar",
            ["RUB"] = "Russian Ruble",
            ["UAH"] = "Ukrainian Hryvnia",
            ["TRY"] = "Turkish Lira",
            ["ILS"] = "Israeli New Shekel",
            ["SAR"] = "Saudi Riyal",
            ["AED"] = "UAE Dirham",
            ["QAR"] = "Qatari Riyal",
            ["KWD"] = "Kuwaiti Dinar",
            ["BHD"] = "Bahraini Dinar",
            ["OMR"] = "Omani Rial",
            ["JOD"] = "Jordanian Dinar",
            ["LBP"] = "Lebanese Pound",
            ["EGP"] = "Egyptian Pound",
            ["MAD"] = "Moroccan Dirham",
            ["DZD"] = "Algerian Dinar",
            ["TND"] = "Tunisian Dinar",
            ["ZAR"] = "South African Rand",
            ["NGN"] = "Nigerian Naira",
            ["GHS"] = "Ghanaian Cedi",
            ["KES"] = "Kenyan Shilling",
            ["TZS"] = "Tanzanian Shilling",
            ["UGX"] = "Ugandan Shilling",
            ["ETB"] = "Ethiopian Birr",
            ["MUR"] = "Mauritian Rupee",
            ["XOF"] = "West African CFA Franc",
            ["XAF"] = "Central African CFA Franc",
            ["INR"] = "Indian Rupee",
            ["PKR"] = "Pakistani Rupee",
            ["BDT"] = "Bangladeshi Taka",
            ["LKR"] = "Sri Lankan Rupee",
            ["NPR"] = "Nepalese Rupee",
            ["KRW"] = "South Korean Won",
            ["TWD"] = "New Taiwan Dollar",
            ["THB"] = "Thai Baht",
            ["MYR"] = "Malaysian Ringgit",
            ["IDR"] = "Indonesian Rupiah",
            ["PHP"] = "Philippine Peso",
            ["VND"] = "Vietnamese Dong",
            ["MMK"] = "Myanmar Kyat",
            ["KHR"] = "Cambodian Riel",
            ["LAK"] = "Lao Kip",
            ["MNT"] = "Mongolian Tugrik",
            ["BRL"] = "Brazilian Real",
            ["MXN"] = "Mexican Peso",
            ["ARS"] = "Argentine Peso",
            ["CLP"] = "Chilean Peso",
            ["COP"] = "Colombian Peso",
            ["PEN"] = "Peruvian Sol",
            ["UYU"] = "Uruguayan Peso",
            ["PYG"] = "Paraguayan Guarani",
            ["BOB"] = "Bolivian Boliviano",
            ["CRC"] = "Costa Rican Colon",
            ["GTQ"] = "Guatemalan Quetzal",
            ["HNL"] = "Honduran Lempira",
            ["NIO"] = "Nicaraguan Cordoba",
            ["PAB"] = "Panamanian Balboa",
            ["DOP"] = "Dominican Peso",
            ["CUP"] = "Cuban Peso",
            ["JMD"] = "Jamaican Dollar",
            ["TTD"] = "Trinidad and Tobago Dollar",
            ["BSD"] = "Bahamian Dollar",
            ["BBD"] = "Barbados Dollar",
            ["XCD"] = "East Caribbean Dollar",
            ["IRR"] = "Iranian Rial",
            ["IQD"] = "Iraqi Dinar",
            ["SYP"] = "Syrian Pound",
            ["YER"] = "Yemeni Rial",
            ["AFN"] = "Afghan Afghani",
            ["AMD"] = "Armenian Dram",
            ["GEL"] = "Georgian Lari",
            ["AZN"] = "Azerbaijani Manat",
            ["KZT"] = "Kazakhstani Tenge",
            ["UZS"] = "Uzbekistani Som",
            ["KGS"] = "Kyrgyzstani Som",
            ["TJS"] = "Tajikistani Somoni",
            ["TMT"] = "Turkmenistani Manat",
            ["BYN"] = "Belarusian Ruble",
            ["MDL"] = "Moldovan Leu",
            ["ALL"] = "Albanian Lek",
            ["MKD"] = "Macedonian Denar",
            ["BAM"] = "Bosnia-Herzegovina Convertible Mark",
            ["MVR"] = "Maldivian Rufiyaa",
            ["BND"] = "Brunei Dollar",
            // XAU/XAG/XPT/XPD (the ISO metal codes) are deliberately absent:
            // no free source here prices them, and offering a result that can
            // never be valued is worse than not offering it. Gold and silver
            // are reachable as GC=F / SI=F futures, as tokenised metal (XAUT,
            // PAXG), or as a manual holding.
            ["XDR"] = "IMF Special Drawing Rights",
        };

    /// <summary>Name for a code, falling back to the code itself.</summary>
    public static string NameOf(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var c = code.Trim().ToUpperInvariant();
        return Names.TryGetValue(c, out var n) && n.Length > 0 ? n : c;
    }

    /// <summary>
    /// Codes whose name or code matches the query. Matching is local and free,
    /// so this can run on every keystroke.
    /// </summary>
    public static IEnumerable<string> Search(string query, int take)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim().ToUpperInvariant();

        // Exact code first, then codes starting with the query, then names.
        var exact = Names.Keys.Where(k => Names[k].Length > 0 && k == q);
        var prefix = Names.Keys.Where(k => Names[k].Length > 0 && k.StartsWith(q, StringComparison.Ordinal) && k != q);
        var byName = Names
            .Where(kv => kv.Value.Length > 0 && kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Length)
            .Select(kv => kv.Key)
            .Where(k => k != q && !k.StartsWith(q, StringComparison.Ordinal));

        return exact.Concat(prefix).Concat(byName).Distinct().Take(take);
    }
}
