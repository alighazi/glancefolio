using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using PortfolioWeb;

// ---------------------------------------------------------------------------
// Glancefolio - ASP.NET Core minimal API
//
// Serves both the JSON API and the built single-page client from one origin, so
// the browser never needs CORS or a second port. All market data comes from
// free, key-less providers and is funnelled through ProviderClient, which
// caches on disk, spaces out calls per host and keeps a daily budget.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
});

// Data lives outside bin/ so a rebuild does not touch the user's portfolio.
var dataDir = Path.GetFullPath(
    builder.Configuration["DataDir"]
    ?? Environment.GetEnvironmentVariable("PORTFOLIO_DATA_DIR")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data"));

builder.Services.AddHttpClient("providers", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Glancefolio/1.0 (personal finance; free public endpoints)");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("Glancefolio");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "glancefolio.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build();
});
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(_ => new UserStore(Path.Combine(dataDir, "users.json")));
builder.Services.AddScoped(sp =>
{
    var userId = AuthHelpers.UserId(sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal());
    if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("An authenticated user is required.");
    var logger = sp.GetRequiredService<ILogger<PortfolioStore>>();
    return new PortfolioStore(Path.Combine(dataDir, "users", userId, "portfolio.json"), logger);
});

builder.Services.AddSingleton<QuotaMeter>();
builder.Services.AddSingleton(_ => new DiskCache(Path.Combine(dataDir, "cache"), Json.Options));
builder.Services.AddSingleton<ProviderClient>();
builder.Services.AddSingleton<FxService>();
builder.Services.AddSingleton<CryptoService>();
builder.Services.AddSingleton<YahooService>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<PriceService>();
builder.Services.AddHostedService<WarmupService>();

var app = builder.Build();

// The production host terminates TLS in front of Kestrel. Honor its forwarded
// host/scheme so secure cookies and same-origin validation use the public URL.
var forwardedHeaders = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost
};
// This app is deployed behind the host's edge proxy; forwarded headers from
// that proxy need to be accepted even though its internal addresses vary.
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

// Bind explicitly: the preview proxies to this port, and 0.0.0.0 is required
// for anything outside the sandbox to reach it.
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
app.Urls.Add($"http://0.0.0.0:{port}");

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
        !AuthHelpers.IsSameOrigin(context))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "cross-origin request rejected" });
        return;
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0"
})).AllowAnonymous();

app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var id = AuthHelpers.UserId(context.User);
    if (id is null) return Results.Unauthorized();
    var createdClaim = context.User.FindFirst("glancefolio_created_utc")?.Value;
    var createdUtc = DateTime.TryParse(createdClaim, out var created) ? created : DateTime.UtcNow;
    return Results.Ok(new PublicAccount(id, context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "", createdUtc));
}).AllowAnonymous();

app.MapPost("/api/auth/register", async (AuthRequest request, UserStore users, HttpContext context, ILogger<Program> log, CancellationToken ct) =>
{
    var email = request.Email?.Trim() ?? "";
    var password = request.Password ?? "";
    if (email.Length is < 3 or > 254 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
        return Results.Json(new { error = "enter a valid email address" }, statusCode: 400);
    if (password.Length < 12 || password.Length > 200)
        return Results.Json(new { error = "password must be at least 12 characters" }, statusCode: 400);
    var creation = await users.CreateAsync(email, password, ct);
    if (creation is null) return Results.Json(new { error = "an account with that email already exists" }, statusCode: 409);
    var account = creation.User;
    if (creation.IsFirstAccount)
    {
        var legacy = Path.Combine(dataDir, "portfolio.json");
        var target = Path.Combine(dataDir, "users", account.Id, "portfolio.json");
        if (File.Exists(legacy))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(legacy, target, overwrite: false);
                log.LogInformation("migrated the existing portfolio into the first Glancefolio account");
            }
            catch (Exception ex) { log.LogWarning(ex, "could not migrate the legacy portfolio; original file remains available for import"); }
        }
    }
    await SignIn(context, account);
    return Results.Ok(account);
}).AllowAnonymous().RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (AuthRequest request, UserStore users, HttpContext context, CancellationToken ct) =>
{
    var email = request.Email?.Trim() ?? "";
    var password = request.Password ?? "";
    if (email.Length > 254 || password.Length > 200 || password.Length == 0)
        return Results.Json(new { error = "email or password is incorrect" }, statusCode: 401);
    var account = await users.VerifyAsync(email, password, ct);
    if (account is null) return Results.Json(new { error = "email or password is incorrect" }, statusCode: 401);
    await SignIn(context, account);
    return Results.Ok(account);
}).AllowAnonymous().RequireRateLimiting("auth");

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

/// <summary>
/// Unified symbol search. Every item carries its provider-native id and kind,
/// so picking one here is what determines how it is priced later.
/// </summary>
app.MapGet("/api/search", async (SearchService search, string? q, int? take, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await search.SearchAsync(q ?? "", take ?? 12, ct));
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"search failed: {ex.Message}" }, statusCode: 502);
    }
});

app.MapGet("/api/portfolio", async (PortfolioStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAsync(ct)));

/// <summary>Whole-portfolio replace - used by import and by the editor's bulk save.</summary>
app.MapPut("/api/portfolio", async (PortfolioStore store, FxService fx, Portfolio incoming, CancellationToken ct) =>
{
    if (incoming is null) return Results.Json(new { error = "a portfolio body is required" }, statusCode: 400);
    var problem = ValidatePortfolio(incoming, fx.KnownCodes);
    if (problem is not null) return Results.Json(new { error = problem }, statusCode: 400);
    var saved = await store.UpdateAsync(_ => incoming, ct);
    return Results.Ok(saved);
});

app.MapGet("/api/meta", async (PortfolioStore store, FxService fx, CancellationToken ct) =>
{
    var table = await fx.GetTableAsync(ct);
    var portfolio = await store.GetAsync(ct);
    var codes = table.Available
        ? table.UnitsPerUsd.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
        : Currencies.Names.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    return Results.Ok(new
    {
        // `auto` is whether the app may file things here by itself. A category
        // you add starts out false: nothing is ever auto-assigned to a bucket
        // the app knows nothing about.
        categories = CategoryDto.From(portfolio.Categories),
        defaultCategories = CategoryDto.From(Categories.Defaults),
        maxCategories = PortfolioStore.MaxCategories,
        maxCategoryNameLength = PortfolioStore.MaxCategoryNameLength,
        currencies = codes.Select(c => new { code = c, name = Currencies.NameOf(c) }),
        fxAvailable = table.Available,
        fxAsOf = table.AsOfUtc,
        fxStale = table.Stale,
        fxError = table.Error
    });
});

/// <summary>
/// The report: fetches whatever prices are missing (through the cache), then
/// builds category totals, weights and gain/loss in the base currency.
/// </summary>
app.MapGet("/api/report", async (PortfolioStore store, PriceService prices, FxService fx, CancellationToken ct) =>
{
    var portfolio = await store.GetAsync(ct);
    var quotes = await prices.GetQuotesAsync(portfolio.Holdings, ct);
    var table = await fx.GetTableAsync(ct);
    return Results.Ok(Valuation.Build(portfolio, quotes, table));
});

/// <summary>
/// Force a re-fetch. Clears the *price* caches only - search and FX survive,
/// because those are the expensive-per-value ones and change slowly. This keeps
/// "refresh" a deliberate, cheap action rather than a quota leak.
/// </summary>
app.MapPost("/api/refresh", async (DiskCache cache, PortfolioStore store, PriceService prices,
                                   FxService fx, CancellationToken ct) =>
{
    var cleared = cache.Clear("yahoo:chart:");
    cleared += cache.Clear("crypto:prices:");
    cleared += cache.Clear("crypto:top250");

    var portfolio = await store.GetAsync(ct);
    var quotes = await prices.GetQuotesAsync(portfolio.Holdings, ct);
    var table = await fx.GetTableAsync(ct);
    var report = Valuation.Build(portfolio, quotes, table);
    return Results.Ok(new { clearedCacheEntries = cleared, report });
});

/// <summary>Quota and cache visibility - how many calls each provider cost.</summary>
app.MapGet("/api/diagnostics", (QuotaMeter meter, DiskCache cache, PortfolioStore store) =>
{
    var (files, bytes) = CacheSize(cache);
    return Results.Ok(new
    {
        hosts = meter.Snapshot().Select(h => new
        {
            host = h.Host,
            requests = h.Requests,
            cacheHits = h.CacheHits,
            staleServes = h.StaleServes,
            errors = h.Errors,
            throttled = h.Throttled,
            budgetBlocked = h.BudgetBlocked,
            lastCallUtc = h.LastCallUtc
        }),
        cache = new { files, bytes },
        portfolioFile = store.FilePath,
        time = DateTimeOffset.UtcNow
    });
});

// ---- holdings -------------------------------------------------------------

app.MapPost("/api/holdings", async (PortfolioStore store, FxService fx, AddHoldingRequest req, CancellationToken ct) =>
{
    var current = await store.GetAsync(ct);
    var error = Validate(req, fx.KnownCodes, current.Categories);
    if (error is not null) return Results.Json(new { error }, statusCode: 400);

    var holding = new Holding
    {
        Kind = req.Kind,
        AssetId = string.IsNullOrWhiteSpace(req.AssetId) ? req.Symbol.Trim() : req.AssetId.Trim(),
        Symbol = req.Symbol.Trim(),
        Name = string.IsNullOrWhiteSpace(req.Name) ? req.Symbol.Trim() : req.Name.Trim(),
        IconUrl = req.IconUrl,
        Exchange = req.Exchange,
        Sector = req.Sector,
        Industry = req.Industry,
        Quantity = req.Quantity,
        Category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim(),
        UnitCost = req.UnitCost,
        CostCurrency = req.CostCurrency,
        ManualUnitPrice = req.ManualUnitPrice,
        ManualPriceCurrency = req.ManualPriceCurrency,
        Note = req.Note
    };

    var portfolio = await store.UpdateAsync(p =>
    {
        p.Holdings.Add(holding);
        return p;
    }, ct);

    return Results.Created($"/api/holdings/{holding.Id}", new { holding, portfolio });
});

app.MapPatch("/api/holdings/{id}", async (PortfolioStore store, FxService fx, string id, UpdateHoldingRequest req, CancellationToken ct) =>
{
    var current = await store.GetAsync(ct);
    var problem = ValidateUpdate(req, fx.KnownCodes, current.Categories);
    if (problem is not null)
        return Results.Json(new { error = problem }, statusCode: 400);

    var found = false;
    var portfolio = await store.UpdateAsync(p =>
    {
        var h = p.Holdings.FirstOrDefault(x => x.Id == id);
        if (h is null) return p;
        found = true;

        if (req.Quantity is { } q) h.Quantity = q;
        if (req.Category is { } c) h.Category = string.IsNullOrWhiteSpace(c) ? null : c.Trim();
        if (req.UnitCost is { } uc) h.UnitCost = uc;
        if (req.CostCurrency is { } cc) h.CostCurrency = string.IsNullOrWhiteSpace(cc) ? null : cc.Trim();
        if (req.ManualUnitPrice is { } mp) h.ManualUnitPrice = mp;
        if (req.ManualPriceCurrency is { } mc) h.ManualPriceCurrency = string.IsNullOrWhiteSpace(mc) ? null : mc.Trim();
        if (req.Note is { } n) h.Note = string.IsNullOrWhiteSpace(n) ? null : n;
        if (req.Symbol is { } s && !string.IsNullOrWhiteSpace(s)) h.Symbol = s.Trim();
        if (req.Name is { } nm && !string.IsNullOrWhiteSpace(nm)) h.Name = nm.Trim();
        return p;
    }, ct);

    return found
        ? Results.Ok(new { holding = portfolio.Holdings.First(x => x.Id == id), portfolio })
        : Results.Json(new { error = $"no holding with id \"{id}\"" }, statusCode: 404);
});

app.MapDelete("/api/holdings/{id}", async (PortfolioStore store, string id, CancellationToken ct) =>
{
    var removed = false;
    var portfolio = await store.UpdateAsync(p =>
    {
        removed = p.Holdings.RemoveAll(x => x.Id == id) > 0;
        return p;
    }, ct);
    return removed
        ? Results.Ok(new { removedId = id, portfolio })
        : Results.Json(new { error = $"no holding with id \"{id}\"" }, statusCode: 404);
});

app.MapPatch("/api/portfolio/base-currency", async (PortfolioStore store, FxService fx, BaseCurrencyRequest req, CancellationToken ct) =>
{
    var code = (req?.BaseCurrency ?? "").Trim().ToUpperInvariant();
    var problem = ValidateCurrency(code, "base currency", fx.KnownCodes)
               ?? (code.Length == 0 ? "a base currency code is required" : null);
    if (problem is not null)
        return Results.Json(new { error = problem }, statusCode: 400);

    var portfolio = await store.UpdateAsync(p => { p.BaseCurrency = code; return p; }, ct);
    return Results.Ok(portfolio);
});

/// <summary>
/// Replaces the category list. Three things have to happen together, which is
/// why this is one atomic write rather than a set of CRUD endpoints: the order
/// is the report order, a rename has to carry its holdings across, and a
/// deletion has to release its holdings back to auto-detection instead of
/// leaving them pointing at a name that no longer exists.
/// </summary>
app.MapPut("/api/categories", async (PortfolioStore store, CategoryListRequest req, CancellationToken ct) =>
{
    var incoming = req?.Categories ?? [];
    var problem = ValidateCategories(incoming);
    if (problem is not null) return Results.Json(new { error = problem }, statusCode: 400);

    var renamed = 0;
    var released = 0;

    var portfolio = await store.UpdateAsync(p =>
    {
        var previous = p.Categories;
        var next = new List<CategoryDef>(incoming.Count);

        foreach (var item in incoming)
        {
            var name = (item.Name ?? "").Trim();
            var from = string.IsNullOrWhiteSpace(item.RenamedFrom) ? name : item.RenamedFrom!.Trim();

            // A rename keeps its detection binding, so "Crypto" renamed to
            // "Digital assets" still receives coins. A brand new category that
            // happens to be named after a default inherits that default's
            // binding; anything else is hand-assigned only.
            var existing = previous.FirstOrDefault(c => string.Equals(c.Name, from, StringComparison.OrdinalIgnoreCase))
                        ?? previous.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            next.Add(new CategoryDef { Name = name, Detect = existing?.Detect ?? Categories.DefaultKeyFor(name) });
        }

        foreach (var h in p.Holdings)
        {
            if (string.IsNullOrWhiteSpace(h.Category)) continue;

            var still = next.FirstOrDefault(c => string.Equals(c.Name, h.Category, StringComparison.OrdinalIgnoreCase));
            if (still is not null)
            {
                if (!string.Equals(still.Name, h.Category, StringComparison.Ordinal)) { h.Category = still.Name; renamed++; }
                continue;
            }

            var moved = incoming.FirstOrDefault(i =>
                string.Equals((i.RenamedFrom ?? "").Trim(), h.Category, StringComparison.OrdinalIgnoreCase));
            if (moved is not null) { h.Category = (moved.Name ?? "").Trim(); renamed++; continue; }

            // The category is gone. Clearing it lets detection have another go,
            // and the report says so if there is nowhere left for it to go.
            h.Category = null;
            released++;
        }

        p.Categories = next;
        return p;
    }, ct);

    return Results.Ok(new
    {
        categories = CategoryDto.From(portfolio.Categories),
        renamed,
        released,
        portfolio
    });
});

app.MapGet("/api/portfolio/export", async (PortfolioStore store, CancellationToken ct) =>
{
    var portfolio = await store.GetAsync(ct);
    var json = JsonSerializer.Serialize(portfolio, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json",
                        $"portfolio-{DateTime.UtcNow:yyyyMMdd-HHmm}.json");
});

app.MapPost("/api/portfolio/import", async (PortfolioStore store, FxService fx, Portfolio incoming, CancellationToken ct) =>
{
    if (incoming?.Holdings is null)
        return Results.Json(new { error = "imported file had no holdings" }, statusCode: 400);
    var problem = ValidatePortfolio(incoming, fx.KnownCodes);
    if (problem is not null)
        return Results.Json(new { error = problem }, statusCode: 400);
    var saved = await store.UpdateAsync(_ => incoming, ct);
    return Results.Ok(new { imported = saved.Holdings.Count, portfolio = saved });
});

// Single-page client: unknown non-API paths fall through to index.html.
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Glancefolio data directory: {DataDir}", dataDir);
app.Run();

// ---------------------------------------------------------------------------
// Request DTOs and validation
// ---------------------------------------------------------------------------

static Task SignIn(HttpContext context, PublicAccount account)
{
    var claims = new[]
    {
        new System.Security.Claims.Claim(AuthHelpers.UserIdClaim, account.Id),
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, account.Email),
        new System.Security.Claims.Claim("glancefolio_created_utc", account.CreatedUtc.ToString("O"))
    };
    var identity = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new System.Security.Claims.ClaimsPrincipal(identity));
}

static string? Validate(AddHoldingRequest req, IReadOnlySet<string> currencies, IReadOnlyList<CategoryDef> categories)
{
    if (string.IsNullOrWhiteSpace(req.Symbol))
        return "a symbol is required";
    if (req.Symbol.Trim().Length > 24)
        return "symbol is too long (24 characters max)";
    if (req.Quantity == 0)
        return "quantity cannot be zero";
    if (Math.Abs(req.Quantity) > 1_000_000_000_000m)
        return "quantity is implausibly large";

    var isManual = req.Kind == AssetKind.Other || req.ManualUnitPrice is not null;
    if (isManual)
    {
        if (req.ManualUnitPrice is null or <= 0)
            return "a manual holding needs a price per unit greater than zero";
        if (string.IsNullOrWhiteSpace(req.Name))
            return "a manual holding needs a name";
    }
    else if (string.IsNullOrWhiteSpace(req.AssetId))
    {
        return "a listed holding needs its provider id (pick it from search results)";
    }

    var categoryProblem = ValidateCategory(req.Category, categories);
    if (categoryProblem is not null) return categoryProblem;
    if (req.UnitCost is < 0) return "cost basis cannot be negative";

    return ValidateCurrency(req.CostCurrency, "cost currency", currencies)
        ?? ValidateCurrency(req.ManualPriceCurrency, "manual price currency", currencies);
}

/// <summary>
/// Same rules for a PATCH, where every field is optional: only what is present
/// is checked, and null means "leave alone".
/// </summary>
static string? ValidateUpdate(UpdateHoldingRequest req, IReadOnlySet<string> currencies, IReadOnlyList<CategoryDef> categories)
{
    if (req.Quantity is { } q)
    {
        if (q == 0) return "quantity cannot be zero";
        if (Math.Abs(q) > 1_000_000_000_000m) return "quantity is implausibly large";
    }
    if (req.UnitCost is < 0) return "cost basis cannot be negative";
    if (req.ManualUnitPrice is < 0) return "a price per unit cannot be negative";
    var categoryProblem = ValidateCategory(req.Category, categories);
    if (categoryProblem is not null) return categoryProblem;

    return ValidateCurrency(req.CostCurrency, "cost currency", currencies)
        ?? ValidateCurrency(req.ManualPriceCurrency, "manual price currency", currencies);
}

/// <summary>
/// Whole-portfolio validation for replace and import. Normalize already repairs
/// ids, categories and prices, so this only rejects what would silently produce
/// a wrong report: a base currency nothing can be converted into, or a holding
/// with no identity at all.
/// </summary>
static string? ValidatePortfolio(Portfolio p, IReadOnlySet<string> currencies)
{
    var baseProblem = ValidateCurrency(p.BaseCurrency, "base currency", currencies);
    if (baseProblem is not null) return baseProblem;

    var taxonomy = PortfolioStore.NormalizeCategories(p.Categories);

    for (var i = 0; i < p.Holdings.Count; i++)
    {
        var h = p.Holdings[i];
        if (string.IsNullOrWhiteSpace(h.Symbol) && string.IsNullOrWhiteSpace(h.Name))
            return $"holding #{i + 1} has neither a symbol nor a name";
        if (h.Quantity == 0)
            return $"holding \"{h.Symbol}\" has a quantity of zero";
        var currencyProblem = ValidateCurrency(h.CostCurrency, "cost currency", currencies)
                           ?? ValidateCurrency(h.ManualPriceCurrency, "manual price currency", currencies);
        if (currencyProblem is not null) return $"holding \"{h.Symbol}\": {currencyProblem}";

        var categoryProblem = ValidateCategory(h.Category, taxonomy);
        if (categoryProblem is not null) return $"holding \"{h.Symbol}\": {categoryProblem}";
    }
    return null;
}

/// <summary>
/// Rejects a currency the app cannot convert, at the door rather than later.
/// A holding priced in an unknown code would show a blank value with no visible
/// reason, so the caller gets told which codes exist instead. An empty value is
/// allowed: for optional fields it means "not set".
/// </summary>
/// <summary>
/// A holding's category has to be one the user actually has, or it would sit
/// outside every total in the report. Empty means "detect it for me".
/// </summary>
static string? ValidateCategory(string? category, IReadOnlyList<CategoryDef> categories)
{
    if (string.IsNullOrWhiteSpace(category)) return null;
    var list = categories is { Count: > 0 } ? categories : Categories.Defaults;
    return list.Any(c => string.Equals(c.Name, category.Trim(), StringComparison.OrdinalIgnoreCase))
        ? null
        : $"unknown category \"{category.Trim()}\" - the list has: {string.Join(", ", list.Select(c => c.Name))}";
}

/// <summary>
/// The category list itself. Names must exist, be distinct and be short enough
/// to render as a chip, and the reserved fallback is off limits because the
/// report uses that name to mean "this holding matches nothing".
/// </summary>
static string? ValidateCategories(IReadOnlyList<CategoryRequest> incoming)
{
    if (incoming.Count == 0) return "at least one category is required";
    if (incoming.Count > PortfolioStore.MaxCategories)
        return $"too many categories ({PortfolioStore.MaxCategories} is the limit)";

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < incoming.Count; i++)
    {
        var name = (incoming[i].Name ?? "").Trim();
        if (name.Length == 0) return $"category #{i + 1} has no name";
        if (name.Length > PortfolioStore.MaxCategoryNameLength)
            return $"category \"{name}\" is too long ({PortfolioStore.MaxCategoryNameLength} characters max)";
        if (string.Equals(name, Categories.Uncategorised, StringComparison.OrdinalIgnoreCase))
            return $"the name \"{Categories.Uncategorised}\" is reserved for holdings that match no category";
        if (!seen.Add(name)) return $"two categories are both called \"{name}\"";
    }
    return null;
}

static string? ValidateCurrency(string? code, string what, IReadOnlySet<string> currencies)
{
    if (string.IsNullOrWhiteSpace(code)) return null;   // absent means "not set"
    var c = code.Trim().ToUpperInvariant();
    if (c.Length is < 3 or > 8) return $"{what} must be a 3-8 character code";
    if (!currencies.Contains(c))
        return $"unknown currency \"{c}\" for {what} — {currencies.Count} codes are available (see Settings)";
    return null;
}

static (int Files, long Bytes) CacheSize(DiskCache cache) => cache.Size();

/// <summary>
/// Warms the two caches the first search and the first report both need, so the
/// opening screen is not waiting on a cold provider call. Failures are logged
/// and swallowed: an offline start must still show the portfolio.
/// </summary>
public sealed class WarmupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WarmupService> _log;

    public WarmupService(IServiceProvider services, ILogger<WarmupService> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the web server a moment to bind first.
        try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { return; }

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        try
        {
            var fx = sp.GetRequiredService<FxService>();
            var crypto = sp.GetRequiredService<CryptoService>();
            var table = await fx.GetTableAsync(stoppingToken);
            var index = await crypto.GetTopIndexAsync(stoppingToken);
            _log.LogInformation("warmup: {Fx} currencies, {Coins} coins cached",
                                table.UnitsPerUsd.Count, index.Count);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "warmup failed; the app will fetch on demand instead");
        }

        // Once a day, prune cache files nobody has touched in a week.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                var cache = sp.GetRequiredService<DiskCache>();
                var removed = cache.Prune(TimeSpan.FromDays(7));
                if (removed > 0) _log.LogInformation("pruned {Count} stale cache files", removed);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "cache prune failed");
            }
        }
    }
}

public sealed record AddHoldingRequest(
    AssetKind Kind,
    string? AssetId,
    string Symbol,
    string? Name,
    decimal Quantity,
    string? Category,
    string? IconUrl,
    string? Exchange,
    string? Sector,
    string? Industry,
    decimal? UnitCost,
    string? CostCurrency,
    decimal? ManualUnitPrice,
    string? ManualPriceCurrency,
    string? Note);

public sealed record UpdateHoldingRequest(
    decimal? Quantity,
    string? Category,
    decimal? UnitCost,
    string? CostCurrency,
    decimal? ManualUnitPrice,
    string? ManualPriceCurrency,
    string? Note,
    string? Symbol,
    string? Name);

public sealed record BaseCurrencyRequest(string? BaseCurrency);

/// <summary>
/// One row of the category editor. <c>RenamedFrom</c> is how a rename is told
/// apart from a delete plus an add, which matters because holdings filed under
/// the old name have to travel with it.
/// </summary>
public sealed record CategoryRequest(string? Name, string? RenamedFrom);

public sealed record CategoryListRequest(List<CategoryRequest>? Categories);

/// <summary>
/// A category as the client sees it. <c>Detect</c> is the asset-type key that
/// fills the category automatically; the client needs it only to name the
/// catch-all in the manual form, since a user may have renamed or dropped
/// "Other". It is never accepted back: which key a category owns is decided on
/// the server (a rename keeps it, a new entry may inherit a default's).
/// </summary>
public sealed record CategoryDto(string Name, bool Auto, string? Detect)
{
    public static CategoryDto[] From(IEnumerable<CategoryDef> list) =>
        [.. list.Select(c => new CategoryDto(c.Name, c.Detect is not null, c.Detect))];
}

/// <summary>Entry point type, so integration tests can host this app.</summary>
public partial class Program;
