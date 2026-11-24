"""End-to-end exercise of the portfolio API against the live server.

Covers: add (listed + manual), auto-categorisation, FX-pair currency routing,
patch, base-currency switch, report math, refresh, export/import, delete.
"""
import json
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5000"
FAILURES = []


def call(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(
        BASE + path, data=data, method=method,
        headers={"Content-Type": "application/json"} if data else {},
    )
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=90) as r:
            text = r.read().decode()
            ms = int((time.time() - t0) * 1000)
            return r.status, (json.loads(text) if text else None), ms
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        try:
            return e.code, json.loads(text), int((time.time() - t0) * 1000)
        except json.JSONDecodeError:
            return e.code, text, int((time.time() - t0) * 1000)


def check(label, cond, detail=""):
    mark = "ok  " if cond else "FAIL"
    print(f"[{mark}] {label}{(' -- ' + str(detail)) if detail else ''}")
    if not cond:
        FAILURES.append(label)


# ---------------------------------------------------------------- start clean
st, _, _ = call("GET", "/api/portfolio")
if st == 200:
    for h in call("GET", "/api/portfolio")[1]["holdings"]:
        call("DELETE", f"/api/holdings/{h['id']}")
# Reset the base currency too: a previous run leaves it on EUR, and every
# assertion below is written against USD.
st, res, _ = call("PATCH", "/api/portfolio/base-currency", dict(baseCurrency="USD"))
check("reset base currency to USD", st == 200 and res["baseCurrency"] == "USD", f"HTTP {st}")
# ...and the category list, for the same reason: a holding's category only means
# something relative to the list, and the seed below uses the built-in names.
st, meta0, _ = call("GET", "/api/meta")
DEFAULT_CATEGORIES = [c["name"] for c in meta0["defaultCategories"]]
st, res, _ = call("PUT", "/api/categories",
                  dict(categories=[dict(name=n) for n in DEFAULT_CATEGORIES]))
check("reset the category list to the defaults",
      st == 200 and [c["name"] for c in res["categories"]] == DEFAULT_CATEGORIES,
      f"HTTP {st}: {str(res)[:120]}")
print(f"portfolio cleared, base currency USD, {len(DEFAULT_CATEGORIES)} default categories")

ADDS = [
    # iconUrl comes from a real search hit so the round-trip is genuine
    dict(kind="crypto", assetId="pax-gold", symbol="PAXG", name="PAX Gold", quantity=2.5,
         iconUrl=next((it.get("iconUrl") for it in call("GET", "/api/search?q=pax%20gold")[1]["items"]
                       if it["id"] == "pax-gold"), None)),
    dict(kind="crypto", assetId="bitcoin", symbol="BTC", name="Bitcoin", quantity=0.5),
    dict(kind="crypto", assetId="tether", symbol="USDT", name="Tether", quantity=1000),
    dict(kind="stock", assetId="AAPL", symbol="AAPL", name="Apple Inc.", quantity=10,
         exchange="NASDAQ", sector="Technology", industry="Consumer Electronics", unitCost=150.0, costCurrency="USD"),
    dict(kind="etf", assetId="SPY", symbol="SPY", name="SPDR S&P 500 ETF Trust", quantity=5, sector="Financial Services"),
    dict(kind="index", assetId="^GSPC", symbol="^GSPC", name="S&P 500", quantity=1),
    dict(kind="future", assetId="GC=F", symbol="GC=F", name="Gold Futures", quantity=1),
    dict(kind="currency", assetId="EUR", symbol="EUR", name="Euro", quantity=1000),
    # The regression case: an ISO-looking currency holding whose real identity
    # is a Yahoo FX pair. Must be priced via Yahoo, not the er-api table.
    dict(kind="currency", assetId="EURUSD=X", symbol="EURUSD=X", name="EUR/USD", quantity=500),
    dict(kind="currency", assetId="IRR", symbol="IRR", name="Iranian Rial", quantity=50000000),
    # Manual / unpriced by any provider.
    dict(kind="other", assetId="GOLDBAR100", symbol="GOLDBAR100", name="Gold bar 100g", quantity=1,
         manualUnitPrice=3600.0, manualPriceCurrency="USD", category="Commodities", note="in the safe"),
]

ids = {}
for a in ADDS:
    st, res, ms = call("POST", "/api/holdings", a)
    check(f"POST {a['symbol']}", st == 201 and res and "holding" in res,
          f"HTTP {st} {ms}ms" if st == 201 else f"HTTP {st} {ms}ms {str(res)[:200]}")
    if st == 201:
        ids[a["symbol"]] = res["holding"]["id"]
        print(f"       -> id={res['holding']['id'][:8]} category={res['holding'].get('category')!r} (auto) {ms}ms")

# ------------------------------------------------------------ report (USD)
st, rep, ms = call("GET", "/api/report")
check("GET /api/report", st == 200, f"HTTP {st} {ms}ms")
print(f"\n=== report in {rep['baseCurrency']} ({ms}ms) ===")
assert rep["baseCurrency"] == "USD", f"expected the report in USD, got {rep['baseCurrency']}"
print(f"total      {rep['totalBase']:>16,.2f}")
print(f"priced/unpriced/stale  {rep['pricedCount']}/{rep['unpricedCount']}/{rep['staleCount']}")
for p in rep["problems"]:
    print(f"  ! {p}")
print("\nholdings:")
for h in sorted(rep["holdings"], key=lambda x: -(x["valueBase"] or 0)):
    print(f"  {h['symbol']:11s} {h['category']:14s} qty={h['quantity']:>12,.4f} "
          f"unit={h['unitPriceBase'] if h['unitPriceBase'] is None else round(h['unitPriceBase'], 6):>14} "
          f"value={h['valueBase'] if h['valueBase'] is None else round(h['valueBase'], 2):>14} "
          f"w={h['weightPct']:>6.2f}% {'MANUAL' if h['manual'] else '      '} "
          f"{'STALE' if h['stale'] else '     '} {h.get('error') or ''}")
print("\nby category:")
for g in rep["byCategory"]:
    print(f"  {g['name']:14s} {g['valueBase']:>16,.2f}  {g['weightPct']:>6.2f}%  n={g['count']}")
print("\nby sector:")
for g in rep["bySector"]:
    print(f"  {g['name']:22s} {g['valueBase']:>14,.2f}  {g['weightPct']:>6.2f}%  n={g['count']}")

# ------------------------------------------------------------ auto-categorise
cat = {h["symbol"]: h["category"] for h in rep["holdings"]}
check("auto: pax-gold -> Commodities", cat.get("PAXG") == "Commodities", cat.get("PAXG"))
check("auto: tether (stablecoin) -> Cash", cat.get("USDT") == "Cash", cat.get("USDT"))
check("auto: bitcoin -> Crypto", cat.get("BTC") == "Crypto", cat.get("BTC"))
check("auto: AAPL -> Stocks", cat.get("AAPL") == "Stocks", cat.get("AAPL"))
check("auto: SPY -> ETFs & Funds", cat.get("SPY") == "ETFs & Funds", cat.get("SPY"))
check("auto: ^GSPC -> Indices", cat.get("^GSPC") == "Indices", cat.get("^GSPC"))
check("auto: GC=F -> Commodities", cat.get("GC=F") == "Commodities", cat.get("GC=F"))
check("auto: EUR -> Cash", cat.get("EUR") == "Cash", cat.get("EUR"))
check("manual override kept (Gold bar -> Commodities)", cat.get("GOLDBAR100") == "Commodities", cat.get("GOLDBAR100"))

# ------------------------------------------------------- the PAXG regression
vals = {h["symbol"]: h for h in rep["holdings"]}
paxg = vals.get("PAXG") or {}
check("PAXG priced as the coin (>$3000, not the $9.86 stock)",
      (paxg.get("unitPriceBase") or 0) > 3000, paxg.get("unitPriceBase"))
check("PAXG value == qty * unit (2.5 units)",
      paxg.get("valueBase") is not None and abs(paxg["valueBase"] - 2.5 * paxg["unitPriceBase"]) < 0.01)
fx = vals.get("EURUSD=X") or {}
check("EURUSD=X currency holding priced via Yahoo (~1.0-1.3 USD)",
      fx.get("unitPriceBase") is not None and 0.5 < fx["unitPriceBase"] < 2.0, fx.get("unitPriceBase"))
check("EURUSD=X has no pricing error", not fx.get("error"), fx.get("error"))
irr = vals.get("IRR") or {}
check("IRR priced (tiny USD per rial)", irr.get("unitPriceBase") is not None and 0 < irr["unitPriceBase"] < 1e-4,
      irr.get("unitPriceBase"))
gold = vals.get("GOLDBAR100") or {}
check("manual holding valued at 3600 and flagged manual",
      gold.get("valueBase") == 3600.0 and gold.get("manual") is True, gold)

# ------------------------------------------------------------- report math
tot = round(sum(h["valueBase"] for h in rep["holdings"] if h["valueBase"] is not None), 2)
check("totalBase == sum of holding values", abs(tot - rep["totalBase"]) < 0.01, f"{tot} vs {rep['totalBase']}")
wt = round(sum(g["weightPct"] for g in rep["byCategory"]), 1)
check("category weights sum to ~100%", abs(wt - 100.0) <= 0.2, wt)
cv = round(sum(g["valueBase"] for g in rep["byCategory"]), 2)
check("category values sum to total", abs(cv - rep["totalBase"]) < 0.05, f"{cv} vs {rep['totalBase']}")
check("gain/loss computed for AAPL (cost 150/unit)",
      vals["AAPL"].get("gainBase") is not None and vals["AAPL"]["gainBase"] != 0,
      f"gainBase={vals['AAPL'].get('gainBase')} costBase={vals['AAPL'].get('costBase')} "
      f"valueBase={vals['AAPL'].get('valueBase')} base={rep['baseCurrency']}")
check("cost basis survived the round trip in the stored holding",
      any(h["symbol"] == "AAPL" and h.get("unitCost") == 150.0 and h.get("costCurrency") == "USD"
          for h in call("GET", "/api/portfolio")[1]["holdings"]),
      [h for h in call("GET", "/api/portfolio")[1]["holdings"] if h["symbol"] == "AAPL"])
check("holdings without cost have no gainPct",
      vals["BTC"].get("gainPct") is None, vals["BTC"].get("gainPct"))

# ------------------------------------------------------------- patch + base
st, res, ms = call("PATCH", f"/api/holdings/{ids['AAPL']}", dict(quantity=20, category="Stocks", note="doubled"))
check("PATCH quantity 10 -> 20", st == 200 and res["holding"]["quantity"] == 20, f"HTTP {st} {ms}ms")
check("PATCH keeps symbol/assetId", st == 200 and res["holding"]["assetId"] == "AAPL")
st, res, ms = call("PATCH", f"/api/holdings/{ids['GOLDBAR100']}",
                   dict(manualUnitPrice=3750.0, manualPriceCurrency="USD"))
check("PATCH manual price 3600 -> 3750", st == 200 and res["holding"]["manualUnitPrice"] == 3750.0, f"HTTP {st}")

st, p, ms = call("PATCH", "/api/portfolio/base-currency", dict(baseCurrency="EUR"))
check("switch base currency to EUR", st == 200 and p["baseCurrency"] == "EUR", f"HTTP {st}")
st, rep_eur, ms = call("GET", "/api/report")
check("report in EUR", st == 200 and rep_eur["baseCurrency"] == "EUR")
eur_per_unit = (vals["EUR"]["unitPriceBase"] or 0)  # USD per EUR from the USD report
print(f"\nEUR total {rep_eur['totalBase']:,.2f} vs USD {rep['totalBase']:,.2f} "
      f"(ratio {rep['totalBase'] / rep_eur['totalBase'] if rep_eur['totalBase'] else 0:.5f}; "
      f"USD-per-EUR from FX {eur_per_unit:.6f})")
ratio = rep["totalBase"] / rep_eur["totalBase"] if rep_eur["totalBase"] else 0
check("EUR total is the USD total scaled by ~1/1.09", 1.0 < ratio < 1.35, ratio)
check("EUR-denominated holding is worth ~1 EUR per unit in EUR base",
      abs([h for h in rep_eur["holdings"] if h["symbol"] == "EUR"][0]["unitPriceBase"] - 1.0) < 0.001,
      [h for h in rep_eur["holdings"] if h["symbol"] == "EUR"][0]["unitPriceBase"])

# ------------------------------------------------------------------ refresh
st, res, ms = call("POST", "/api/refresh")
check("POST /api/refresh", st == 200 and "report" in res, f"HTTP {st} {ms}ms")
if st == 200:
    print(f"       cleared {res['clearedCacheEntries']} cache entries, rebuilt in {ms}ms, "
          f"total now {res['report']['totalBase']:,.2f} EUR")
    check("refresh keeps every holding priced", res["report"]["unpricedCount"] == 0, res["report"]["problems"])
    check("refresh keeps the total stable (same session)",
          abs(res["report"]["totalBase"] - rep_eur["totalBase"]) < rep_eur["totalBase"] * 0.05,
          f"{rep_eur['totalBase']:,.2f} -> {res['report']['totalBase']:,.2f}")

# ------------------------------------------------------- export / import
st, exp, ms = call("GET", "/api/portfolio/export")
check("GET /api/portfolio/export", st == 200 and isinstance(exp, dict) and len(exp["holdings"]) == len(ADDS),
      f"HTTP {st} {ms}ms n={len(exp['holdings']) if isinstance(exp, dict) else '?'}")
n_before = len(exp["holdings"])
st, p2, ms = call("PUT", "/api/portfolio", exp)
check("PUT /api/portfolio (round-trip import)", st == 200 and len(p2["holdings"]) == n_before, f"HTTP {st}")
check("import preserves base currency", p2["baseCurrency"] == "EUR" if st == 200 else False)
check("import preserves manual price",
      any(h["symbol"] == "GOLDBAR100" and h["manualUnitPrice"] == 3750.0 for h in p2["holdings"]) if st == 200 else False)

# ------------------------------------------------------------------ validation
bad = [
    ("empty symbol", dict(kind="stock", symbol="", quantity=1, assetId="X")),
    ("zero quantity", dict(kind="stock", symbol="AAPL", quantity=0)),
    ("negative quantity", dict(kind="stock", symbol="AAPL", quantity=-5)),
    ("absurd quantity", dict(kind="stock", symbol="AAPL", quantity=1e15)),
    ("listed without assetId", dict(kind="stock", symbol="AAPL", quantity=1)),
    ("manual without price", dict(kind="other", symbol="THING", name="Thing", quantity=1)),
    ("unknown category", dict(kind="crypto", assetId="bitcoin", symbol="BTC", quantity=1, category="Unicorns")),
    ("negative cost", dict(kind="stock", assetId="AAPL", symbol="AAPL", quantity=1, unitCost=-3)),
]
for label, body in bad:
    st, res, _ = call("POST", "/api/holdings", body)
    check(f"rejects {label}", st == 400, f"HTTP {st}: {res}")
st, res, _ = call("PATCH", "/api/holdings/does-not-exist", dict(quantity=1))
check("PATCH unknown id -> 404", st == 404, f"HTTP {st}")
st, res, _ = call("DELETE", "/api/holdings/does-not-exist")
check("DELETE unknown id -> 404", st == 404, f"HTTP {st}")
# A code the live FX payload carries but the built-in table does not must be
# accepted: rejecting it would block 59 real currencies the UI offers as chips.
for code in ("ANG", "AOA", "BWP", "MNT"):
    st, res, _ = call("PATCH", "/api/portfolio/base-currency", dict(baseCurrency=code))
    check(f"base currency {code} (live-only code) accepted", st == 200 and res["baseCurrency"] == code,
          f"HTTP {st}: {str(res)[:120]}")
for code in ("ZZZ", "XAU", "BTC", "NOTACURRENCY"):
    st, res, _ = call("PATCH", "/api/portfolio/base-currency", dict(baseCurrency=code))
    check(f"base currency {code} rejected", st == 400, f"HTTP {st}: {str(res)[:120]}")
st, res, _ = call("PATCH", "/api/portfolio/base-currency", dict(baseCurrency=""))
check("empty base currency rejected", st == 400, f"HTTP {st}")
st, _, _ = call("PATCH", "/api/portfolio/base-currency", dict(baseCurrency="EUR"))
check("base currency back to EUR", st == 200)

# PATCH now validates too, so a client bug cannot corrupt the file.
st, res, _ = call("PATCH", f"/api/holdings/{ids['AAPL']}", dict(quantity=0))
check("PATCH rejects zero quantity", st == 400, f"HTTP {st}: {res}")
st, res, _ = call("PATCH", f"/api/holdings/{ids['AAPL']}", dict(category="Unicorns"))
check("PATCH rejects an unknown category", st == 400, f"HTTP {st}: {res}")
st, res, _ = call("PATCH", f"/api/holdings/{ids['AAPL']}", dict(unitCost=-5))
check("PATCH rejects a negative cost", st == 400, f"HTTP {st}: {res}")
st, res, _ = call("PATCH", f"/api/holdings/{ids['GOLDBAR100']}", dict(manualPriceCurrency="ZZZ"))
check("PATCH rejects an unknown price currency", st == 400, f"HTTP {st}: {res}")
st, res, _ = call("PATCH", f"/api/holdings/{ids['GOLDBAR100']}", dict(manualPriceCurrency="ANG"))
check("PATCH accepts a live-only price currency", st == 200, f"HTTP {st}: {str(res)[:120]}")
st, res, _ = call("PATCH", f"/api/holdings/{ids['GOLDBAR100']}", dict(manualPriceCurrency="USD"))
check("PATCH back to USD", st == 200)

# A whole-portfolio replace must be validated as well.
st, bad, _ = call("GET", "/api/portfolio/export")
tampered = json.loads(json.dumps(bad))
tampered["baseCurrency"] = "ZZZ"
st, res, _ = call("PUT", "/api/portfolio", tampered)
check("PUT rejects an unconvertible base currency", st == 400, f"HTTP {st}: {str(res)[:120]}")
tampered = json.loads(json.dumps(bad))
tampered["holdings"][0]["symbol"] = ""
tampered["holdings"][0]["name"] = ""
st, res, _ = call("PUT", "/api/portfolio", tampered)
check("PUT rejects a holding with no identity", st == 400, f"HTTP {st}: {str(res)[:120]}")
st, res, _ = call("PUT", "/api/portfolio", bad)
check("PUT still accepts the untouched export", st == 200, f"HTTP {st}")

# ------------------------------------------------------- editing categories
# The list is user data: renaming must carry the holdings filed under the old
# name, and deleting one must send them back to detection rather than leave them
# pointing at a category that no longer exists.
rep_before = call("GET", "/api/report")[1]

# Put the gold bar's hand pick back explicitly: the rename below is asserted by
# how many holdings travel with it, so the starting state has to be exact.
st, res, _ = call("PATCH", f"/api/holdings/{ids['GOLDBAR100']}", dict(category="Commodities"))
check("the gold bar starts hand-filed under Commodities", st == 200, f"HTTP {st}: {str(res)[:120]}")

st, meta, _ = call("GET", "/api/meta")
check("meta exposes the category list", st == 200 and isinstance(meta.get("categories"), list)
      and len(meta["categories"]) == len(meta["defaultCategories"]), f"HTTP {st}")
if st == 200:
    names = [c["name"] for c in meta["categories"]]
    check("meta categories are objects with an auto flag",
          all({"name", "auto"} <= set(c) for c in meta["categories"]), meta["categories"][:2])
    check("meta says which categories are hand-picked only",
          [c["name"] for c in meta["categories"] if not c["auto"]] == ["Bonds", "Real Estate", "Collectibles"],
          [c["name"] for c in meta["categories"] if not c["auto"]])
    check("meta carries the detection key the UI needs",
          next(c for c in meta["categories"] if c["name"] == "Crypto").get("detect") == "crypto")
    check("meta offers the defaults to restore", [c["name"] for c in meta["defaultCategories"]] == names)
    check("meta states the limits", meta["maxCategories"] == 40 and meta["maxCategoryNameLength"] == 40)

st, res, _ = call("PUT", "/api/categories", dict(categories=[
    dict(name="Cash"), dict(name="Digital assets", renamedFrom="Crypto"), dict(name="Stocks"),
    dict(name="ETFs & Funds"), dict(name="Hard assets", renamedFrom="Commodities"), dict(name="Indices"),
    dict(name="Bonds"), dict(name="Real Estate"), dict(name="Collectibles"), dict(name="Other"),
]))
check("PUT renames two categories", st == 200 and [c["name"] for c in res["categories"]][1] == "Digital assets",
      f"HTTP {st}: {str(res)[:160]}")
if st == 200:
    check("the hand-picked gold bar travelled with its category",
          res["renamed"] == 1
          and next(h for h in res["portfolio"]["holdings"] if h["symbol"] == "GOLDBAR100")["category"] == "Hard assets",
          f"renamed={res['renamed']}")
    check("a rename keeps the detection binding",
          next(c for c in res["categories"] if c["name"] == "Digital assets")["auto"] is True
          and next(c for c in res["categories"] if c["name"] == "Digital assets").get("detect") == "crypto")

st, rep2, _ = call("GET", "/api/report")
cat2 = {h["symbol"]: h["category"] for h in rep2["holdings"]} if st == 200 else {}
check("renamed categories still receive what they always did",
      cat2.get("BTC") == "Digital assets" and cat2.get("PAXG") == "Hard assets"
      and cat2.get("GC=F") == "Hard assets" and cat2.get("AAPL") == "Stocks", cat2)
check("a rename does not disturb the totals",
      st == 200 and abs(rep2["totalBase"] - rep_before["totalBase"]) < 0.01,
      f"{rep2.get('totalBase')} vs {rep_before['totalBase']}")
check("the report lists groups in the user's order",
      st == 200 and [g["name"] for g in rep2["byCategory"]][:2] == ["Cash", "Digital assets"],
      [g["name"] for g in rep2.get("byCategory", [])])

# Deleting the category coins were detected into leaves them with nowhere to go.
st, res, _ = call("PUT", "/api/categories", dict(categories=[
    dict(name="Cash"), dict(name="Stocks"), dict(name="ETFs & Funds"), dict(name="Hard assets"),
    dict(name="Indices"), dict(name="Bonds"), dict(name="Real Estate"), dict(name="Collectibles"),
    dict(name="Pension"), dict(name="Other"),
]))
check("PUT deletes a category and adds one", st == 200 and res["released"] == 0, f"HTTP {st}: {str(res)[:160]}")
if st == 200:
    check("a category you add is never filled in automatically",
          next(c for c in res["categories"] if c["name"] == "Pension")["auto"] is False)
    check("a new category named after a default inherits its binding",
          next(c for c in res["categories"] if c["name"] == "Hard assets").get("detect") == "commodities")

st, rep3, _ = call("GET", "/api/report")
cat3 = {h["symbol"]: h["category"] for h in rep3["holdings"]} if st == 200 else {}
check("holdings whose category was deleted are reported, not misfiled",
      cat3.get("BTC") == "Uncategorised" and cat3.get("PAXG") == "Hard assets", cat3)
check("the report says how many holdings match no category",
      st == 200 and any("1 holding matches no category" in p for p in rep3["problems"]), rep3.get("problems"))
check("Uncategorised is a real group with the right count",
      st == 200 and next(g for g in rep3["byCategory"] if g["name"] == "Uncategorised")["count"] == 1)

# A hand pick into the new category is the only way anything lands there.
st, res, _ = call("PATCH", f"/api/holdings/{ids['AAPL']}", dict(category="Pension"))
check("PATCH into a category you added", st == 200
      and next(h for h in res["portfolio"]["holdings"] if h["symbol"] == "AAPL")["category"] == "Pension",
      f"HTTP {st}: {str(res)[:160]}")
st, rep4, _ = call("GET", "/api/report")
check("the report shows the hand pick next to what detection said",
      st == 200 and next(h for h in rep4["holdings"] if h["symbol"] == "AAPL")["category"] == "Pension"
      and next(h for h in rep4["holdings"] if h["symbol"] == "AAPL")["detectedCategory"] == "Stocks")
st, res, _ = call("POST", "/api/holdings", dict(kind="crypto", assetId="ethereum", symbol="ETH",
                                                 name="Ethereum", quantity=1, category="Cellar"))
check("a category that is not in the list is rejected by name",
      st == 400 and "Pension" in str(res.get("error", "")) and "Cellar" in str(res.get("error", "")),
      f"HTTP {st}: {str(res)[:160]}")

for label, body in [
    ("an empty list", dict(categories=[])),
    ("no list at all", dict()),
    ("a blank name", dict(categories=[dict(name="   ")])),
    ("a name over 40 characters", dict(categories=[dict(name="x" * 41)])),
    ("two categories with the same name", dict(categories=[dict(name="Cash"), dict(name="cash")])),
    ("the reserved fallback name", dict(categories=[dict(name="Uncategorised")])),
    ("more than 40 categories", dict(categories=[dict(name=f"c{i}") for i in range(41)])),
]:
    st, res, _ = call("PUT", "/api/categories", body)
    check(f"PUT rejects {label}", st == 400, f"HTTP {st}: {str(res)[:160]}")

# Put the defaults back, so the sections that follow (and the next run) see the
# list they were written against.
st, res, _ = call("PUT", "/api/categories",
                  dict(categories=[dict(name=n) for n in DEFAULT_CATEGORIES]))
check("PUT restores the default list", st == 200 and len(res["categories"]) == len(DEFAULT_CATEGORIES),
      f"HTTP {st}: {str(res)[:160]}")
if st == 200:
    check("restoring defaults releases the two picks filed under deleted names", res["released"] == 2,
          f"released={res['released']}")
st, res, _ = call("PATCH", f"/api/holdings/{ids['GOLDBAR100']}", dict(category="Commodities"))
check("the gold bar is hand-filed under Commodities again", st == 200
      and next(h for h in res["portfolio"]["holdings"] if h["symbol"] == "GOLDBAR100")["category"] == "Commodities",
      f"HTTP {st}: {str(res)[:160]}")
st, rep5, _ = call("GET", "/api/report")
cat5 = {h["symbol"]: h["category"] for h in rep5["holdings"]} if st == 200 else {}
check("everything is detected as before",
      cat5.get("BTC") == "Crypto" and cat5.get("PAXG") == "Commodities" and cat5.get("AAPL") == "Stocks"
      and cat5.get("GOLDBAR100") == "Commodities", cat5)
check("no uncategorised holdings are left", st == 200 and not rep5["problems"], rep5.get("problems"))

# ------------------------------------------------------------------ delete
st, res, ms = call("DELETE", f"/api/holdings/{ids['BTC']}")
check("DELETE holding", st == 200 and res["removedId"] == ids["BTC"], f"HTTP {st}")
check("DELETE removes it from the portfolio",
      all(h["id"] != ids["BTC"] for h in res["portfolio"]["holdings"]) if st == 200 else False)

# ------------------------------------------------------------------ diagnostics
st, d, ms = call("GET", "/api/diagnostics")
check("GET /api/diagnostics", st == 200, f"HTTP {st}")
if st == 200:
    print("\n=== diagnostics ===")
    print(f"cache: {d['cache']['files']} files, {d['cache']['bytes']} bytes")
    print(f"portfolio file: {d['portfolioFile']}")
    for h in d["hosts"]:
        print(f"  {h['host']:26s} calls={h['requests']:>3} hits={h['cacheHits']:>3} "
              f"stale={h['staleServes']:>2} err={h['errors']:>2} throttled={h['throttled']:>2} "
              f"budgetBlocked={h['budgetBlocked']}")
    check("no host hit its daily budget", all(h["budgetBlocked"] == 0 for h in d["hosts"]))

print("\n" + ("ALL API CHECKS PASSED" if not FAILURES else f"{len(FAILURES)} FAILURES: {FAILURES}"))
sys.exit(1 if FAILURES else 0)
