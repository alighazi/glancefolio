# Portfolio — a mobile-first mixed-asset tracker

Track cash, crypto, stocks, funds, indices, futures and things no provider has
ever heard of in one place, valued in the currency you actually think in.

**.NET 10 backend · React 18 + TypeScript + Vite front end · no API keys, no
accounts, no paid services, no NuGet or npm runtime dependencies beyond React.**

```
┌─────────────────────────────┐          ┌──────────────────────────────┐
│  React SPA (mobile-first)   │  same    │  ASP.NET Core minimal API    │
│  bottom tab bar, sheets,    │─ origin ─│  search · prices · valuation │
│  search-first add flow      │  /api/*  │  disk cache · token buckets  │
└─────────────────────────────┘          └──────────┬───────────────────┘
                                                    │ cached, rate-limited
                                     ┌──────────────┼──────────────┐
                                     ▼              ▼              ▼
                               CoinGecko      Yahoo Finance    open.er-api
                              (coins, logos)  (stocks, ETFs,   (166 currency
                                               indices, FX)     rates vs USD)
```

---

## Running it

```bash
cd web
source env.sh                        # puts the .NET SDK on PATH if needed
dotnet build
dotnet run --project src/Server      # serves API + UI on http://0.0.0.0:5000
```

The client is built into `src/Server/wwwroot`, so one process serves both and
there is no CORS, no second port and no proxy to configure. To work on the UI
with hot reload:

```bash
cd src/Client
npm install
npm run dev          # Vite on :5173, /api proxied to :5000
npm run build        # tsc --noEmit, then bundle into ../Server/wwwroot
```

Environment: `PORT` / `ASPNETCORE_URLS` choose the port, `--data <dir>` (or
`PORTFOLIO_DATA`) chooses where `portfolio.json` and the cache live
(default `src/Server/data`).

---

## How you add a holding

The point of the app is that you never type a ticker into a column and hope.

1. **Search.** Tap **+**, start typing. Results come back as a list with a logo,
   the full name, the exchange, the sector, a live price, the 24h change and the
   category it will land in. An empty box browses the top coins by market cap, so
   there is something to look at before you know what you want.
2. **Pick.** Tap the row you meant. This is the step that matters: `PAXG` is
   genuinely two assets — a tokenised gold coin worth ~$3,600 and a Nasdaq shell
   company worth ~$10 — and both are offered, neither collapsed into the other.
   The app keeps the *provider's* identifier for whichever you pick, so the
   choice you make here is the choice used for pricing later. Nothing is
   re-guessed from the letters.
3. **Amount.** Enter how much you hold — shares, whole coins, 0.0004 BTC, or
   1,000 EUR. The estimated value updates as you type. Cost basis and a note are
   one tap further down if you want gain/loss.
4. **Repeat.** Adding drops you back on the search field, focused, ready for the
   next one.

**If it cannot be found**, the miss says so and offers *Add "…" manually*: name,
amount, and **the price per unit you supply yourself**, in any currency. That
covers physical metal, a private deal, a delisted ticker, a foreign bank
balance. Manual holdings are badged as such, never pretend to update, and are
converted through the same FX table as everything else.

**Categories** are chosen for you from the provider's own classification — an
ETF is a fund because Yahoo says `ETF`, not because of the shape of the symbol.
Two overrides win first: tokenised metals (`PAXG`, `XAUT`, `KAU`, `PMGT`…) go to
**Commodities** rather than Crypto, and stablecoins (`USDT`, `USDC`, `DAI`,
`PYUSD`…) go to **Cash**. You can override any of it per holding; the chip row
defaults to *Auto · Commodities* so you can see what was detected. Real Estate,
Bonds and Collectibles are never auto-detected — no provider can know you own a
flat.

---

## Your categories, your report

The ten defaults are a starting point, not a schema. `Settings → Categories`
edits the list itself: rename, reorder, add your own, delete one, or put the
defaults back.

The part that is easy to get wrong is what a rename *means*, so detection is
bound to a stable key (`crypto`, `commodities`, `cash`…) rather than to the
display name:

| You do | What happens |
|---|---|
| Rename **Crypto** → **Digital assets** | The key travels with it, so new coins still land there, and every holding hand-filed under the old name is moved across. The response says how many. |
| Add **Pension** | It receives nothing automatically — a bucket the app knows nothing about is only ever filled by hand, and the row is marked `hand-picked`. |
| Add a category named **Commodities** after deleting it | It inherits that default's key, so it starts receiving metals and futures again. |
| Delete **Crypto** | Coins have nowhere to be filed. They are **not** quietly folded into *Other*: they appear in an `Uncategorised` group, the report says *"1 holding matches no category"*, and the row keeps its value. |
| Reorder the list | The report, the allocation bar and the donut follow your order. Anything unmatched is appended alphabetically. |

Limits: 40 categories, 40 characters each, no duplicates, no blank names, and
`Uncategorised` is reserved. All of it is validated in the browser *and* at the
door, so a hand-edited `portfolio.json` is repaired on load rather than trusted.

**Reports** now leads with a donut: one arc per category, sized from the values
(so the ring always closes exactly), coloured by the same hash the rest of the
app uses. Tap an arc or a legend row to put that category's value, share and
holding count in the middle; tap again for the total. It is inline SVG arcs with
no chart library: React and ReactDOM are still the only runtime dependencies the
client has.

---

## Not running out of quota

Everything here is a free tier, so the design rule is that a repeat request must
cost nothing and a burst must wait rather than get the app banned.

| Provider | Used for | Requests made | Cache TTL | Serve-stale window | Rate limit |
|---|---|---|---|---|---|
| `open.er-api.com` | 166 currency rates vs USD | 1 per 6h, whatever you hold | 6 h | 10 days | 20/min, burst 2 |
| `api.coingecko.com` | **top 250 coins in one call** — search index, logos, ranks, prices, 24h change | 1 per 5 min covers crypto search *and* crypto prices | 5 min | 36 h | 6/min, burst 1 |
| `api.coingecko.com/search` | coins outside the top 250 | only when the local index returns < 3 hits and the query is ≥ 2 chars | 30 min | — | 6/min, burst 1, **600 ms wait cap** |
| `api.coingecko.com/simple/price` | batched coin prices | 1 per distinct id-set per minute | 60 s | — | batched, never per-coin |
| `query1.finance.yahoo.com` | stocks, ETFs, indices, futures, FX pairs | 1 per symbol per minute, sequential | 60 s | 30 h | 40/min, burst 5 |
| currency search | 166 codes | **zero** — the ISO table is local | — | — | free per keystroke |

The mechanics behind that table:

- **Disk cache that survives restarts.** One JSON file per key, named with a
  readable slug plus a hash (`crypto_top250-4F2A….json`), so `ls` tells you what
  you have and a corrupt file is dropped rather than trusted. A measured run of
  the full API suite: **er-api 0 network calls / 31 cache hits**, CoinGecko
  5 calls / 17 hits, Yahoo 22 calls / 37 hits.
- **Stale-while-revalidate.** Past its TTL an entry is served *instantly* and
  refreshed in the background (single-flight, so ten concurrent requests cause
  one fetch). A report that took 3.8 s cold answers in **1 ms** warm, with the
  stale rows badged so you can see which they are.
- **Nothing is ever blanked by a failure.** If a refresh fails, the last good
  value is served and flagged `Stale`. If there is no good value, the row stays
  with its quantity and category and an error line, and the total is computed
  from what is known. A provider outage degrades the report; it does not empty it.
- **Per-host token buckets** with a daily budget of 2,000 requests per host.
  Interactive search passes a **600 ms wait cap**: when the bucket is starved,
  autocomplete comes back with "crypto degraded" in ~160 ms instead of hanging
  for 5.6 s (measured, before and after).
- **Refresh is a button, not a timer.** `POST /api/refresh` clears the price
  prefixes and rebuilds — the difference between a quota that lasts and one that
  does not.

`Settings → Quota & cache` shows the live counters per host: calls, cache hits,
stale serves, errors, throttles, budget blocks, and the size of the cache.

---

## Money maths

Every provider is reduced to **USD per unit**, then one FX multiplication gets
you to your base currency:

- er-api publishes *units of X per 1 USD*, so `usd_per_unit = 1 / rates[X]`.
- A Tokyo listing quoted in JPY goes through the table rather than being treated
  as dollars.
- A cost basis recorded in another currency is converted too, so gain/loss is
  not silently mixing currencies.

Rounding is `MidpointRounding.AwayFromZero`: money 2 dp, weights 2 dp, unit
prices 6 dp (so an Iranian rial does not round to zero). Weights are computed
against the priced total and do not change when you switch base currency.
Gain/loss is only computed over holdings that have **both** a value and a cost —
otherwise a missing cost basis reads as a 100% gain. `Valuation.Build` is pure
and synchronous (the timestamp is injected), which is why 600 lines of it are
unit-testable without a network.

---

## API

| Method | Path | What it does |
|---|---|---|
| GET | `/api/health` | liveness, data directory, version |
| GET | `/api/search?q=&take=` | merged, ranked results from all three sources |
| GET | `/api/portfolio` | holdings + base currency |
| PUT | `/api/portfolio` | replace the whole portfolio (validated) |
| GET | `/api/portfolio/export` | download `portfolio-YYYYMMDD-HHMM.json` |
| POST | `/api/portfolio/import` | import that file back |
| PATCH | `/api/portfolio/base-currency` | switch the reporting currency |
| POST | `/api/holdings` | add one (201 with the holding and the portfolio) |
| PATCH | `/api/holdings/{id}` | change amount, category, cost, manual price, note |
| DELETE | `/api/holdings/{id}` | remove one |
| GET | `/api/report` | the valuation: rows, category totals, sector totals |
| POST | `/api/refresh` | clear price caches, re-fetch, rebuild |
| PUT | `/api/categories` | replace the list: renames carry holdings, deletions release them |
| GET | `/api/meta` | your categories (+ the defaults to restore), limits, 166 currencies, FX freshness |
| GET | `/api/diagnostics` | per-host quota counters, cache size |

`PUT /api/categories` takes `{categories:[{name, renamedFrom?}]}` — the whole
list, in your order — and answers `{categories, renamed, released, portfolio}`.
`renamedFrom` is what tells a rename apart from a delete plus an add, and it is
the reason holdings do not get orphaned by an edit. `GET /api/meta` returns each
category as `{name, auto, detect}`, so the UI can label the *Auto* choice and
mark the hand-picked ones without duplicating the detection rules.

Search ranking: an exact symbol **or exact name** match first, then symbol
prefix, then name prefix, then prominence (market-cap rank). Cross-provider
collisions are never merged.

Validation is at the door, not in the UI: empty or 24+-character symbols, zero or
absurd quantities, manual holdings without a price, listed holdings without a
provider id, unknown categories, negative cost bases, and currencies the app
cannot convert are all `400` with a sentence you could show a human. The
currency check uses the codes the **live** FX payload carries (166), not just the
built-in table (107) — validating against the smaller list rejected `ANG`, `AOA`
and 57 other real currencies.

---

## Data

One file, `src/Server/data/portfolio.json`. Writes go to a temp file that is
then moved into place, so a crash mid-write cannot truncate it, and the previous
version is kept next to it as `.bak`. A semaphore serialises the
load-modify-save cycle, so two concurrent edits cannot lose one.

Loading repairs rather than rejects: duplicate or missing ids get fresh ones, an
unknown category is cleared so detection can choose, a zero or negative manual
price is dropped, strings are trimmed. The category list is repaired *first*,
because a holding's category only means something relative to it: a portfolio
written before categories were editable is seeded with the defaults, and a
hand-edited list loses its blanks, duplicates, over-long names and made-up
detection keys (two categories may not claim the same key) before anything else
is looked at. A file that cannot be parsed at all starts
empty **and is left untouched on disk** so you can look at it. A base currency is
never rewritten on load — the report shows USD and says why instead.

---

## Tests

```bash
./run-tests.sh        # all three suites, in the order that leaves demo data behind
```

| Suite | What it covers | Result |
|---|---|---|
| `dotnet test` (156 tests) | category detection and its two overrides, **a user-edited category list** (renames keeping their binding, deletions reporting `Uncategorised`, `NormalizeCategories` repair, group order from your list), `YahooKinds` mapping, FX inversion and cross rates, disk-cache TTL/stale/atomicity/`Clear(prefix)`/`Prune`, token-bucket burst-and-wait, `Valuation.Build` money maths, weights, gain/loss, unpriced rows, `PortfolioStore.Normalize`, corrupt-file recovery, concurrent writes | **156 passed** |
| `tests/api_e2e.py` (116 checks) | every endpoint against the **live** server and **live** providers: an 11-holding mixed portfolio, auto-categorisation, the PAXG regression, FX-pair currency routing, report arithmetic (totals, weights summing to 100, category sums), base-currency switching, refresh, export/import round-trip, **renaming/deleting/adding categories and what that does to the holdings**, and 27 rejection cases | **all passed** |
| `src/Client/uismoke/run.mjs` (140 checks) | the **real React bundle** rendered in jsdom against the real API, tapped through: search → autocomplete with logo, name, price, category → pick → amount → add → toast; the collision not being collapsed; the manual fallback; the edit sheet; two-tap delete; **the donut (arc widths against the report's shares, tiling without overlap, tap-to-focus, screen-reader label)**; **the category editor (rename → save → holdings follow → delete → `Uncategorised` → restore defaults)**; base-currency switch re-rendering the hero; the empty first-run screen; quota table; and an assertion that **every** request the client made was relative | **140 passed, no console errors** |

There is no browser in this sandbox, so the UI suite drives React in jsdom and
hooks `console.error` — which is how a duplicate-key warning in the currency
chips got found and fixed rather than shipped.

Both live suites reset the state they assert against before they start — the
portfolio, the base currency and now the category list — because a suite that
inherits whatever the last run left behind is not testing anything. Deliberately
renaming categories and then running either suite still passes, and `./run-tests.sh`
finishes with the six-holding demo portfolio and the built-in list in place.

---

## Honest limits

- **er-api updates about once a day.** FX is not tick data; the app shows when
  the table was last updated and flags a stale one.
- **Yahoo's endpoints are unofficial.** They can change or 429 without notice.
  The batch quote endpoint needs a crumb and returns 401, so prices come from
  the per-symbol chart endpoint, fetched sequentially and cached hard.
- **`XAU`/`XAG`/`XPT`/`XPD` are not currencies you can pick.** Yahoo has no
  `XAUUSD=X` and er-api does not carry them, so offering them would let you
  choose a base currency nothing can convert to. Gold is reachable as `GC=F`,
  as `PAXG`/`XAUT`, or as a manual holding.
- **Crypto outside the top 250** needs the remote search endpoint, which is the
  most rate-limited call in the app; it degrades to "crypto unavailable" rather
  than blocking the rest of the results.
- **Equities have no free logo source**, so they get a kind-tinted monogram
  (`AA`, `SPY`). Coins get real artwork from CoinGecko.
- Values are indicative, not execution quotes.

---

## Layout

```
web/
├── env.sh                      toolchain PATH for the .NET SDK in ~/.local
├── run-tests.sh                all three suites
├── Portfolio.sln
├── src/Server/                 ASP.NET Core, no external packages
│   ├── Program.cs              endpoints, DI, validation, cache warm-up
│   ├── Providers.cs            HTTP client, FX, CoinGecko, Yahoo, kind mapping
│   ├── Caching.cs              disk cache, token bucket, quota meter
│   ├── Search.cs               categories + the merged, ranked search
│   ├── Prices.cs               quote resolution + Valuation.Build
│   ├── Store.cs                atomic single-file persistence + Normalize
│   ├── Models.cs, Json.cs, Currencies.cs
│   ├── data/                   portfolio.json, portfolio.json.bak, cache/
│   └── wwwroot/                built client (generated)
├── src/Client/                 React 18 + TS strict + Vite
│   ├── src/App.tsx             shell, data layer, toasts, tab bar
│   ├── src/components/         AddView (the 3-step flow), PortfolioView,
│   │                           ReportsView, SettingsView, HoldingSheet,
│   │                           CategoryEditor, CategoryPicker, PieChart, ui.tsx
│   ├── src/styles.css          mobile-first design system, no framework
│   ├── src/api.ts, types.ts, format.ts, palette.ts
│   └── uismoke/                jsdom driver for the UI suite
└── tests/
    ├── Server.Tests/           xunit, 156 tests
    └── api_e2e.py              live end-to-end API checks
```
