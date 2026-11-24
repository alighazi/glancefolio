// Renders the real React app in jsdom against the real API and drives it the
// way a thumb would: tap Add, type a ticker, pick a result, enter an amount.
//
// There is no browser in this sandbox, so this is the only way to prove the
// bundle actually mounts, that the search-first flow works end to end, and that
// the client only ever asks for relative URLs (which is what lets the API and
// the UI share one origin behind the preview proxy).
//
//   npm run test:ui            (builds the bundle, then runs this)
//   node uismoke/run.mjs http://127.0.0.1:5000

import { JSDOM, VirtualConsole } from 'jsdom';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const BUNDLE = path.join(HERE, '..', 'dist', 'uismoke', 'bundle.mjs');
const ORIGIN = process.argv[2] ?? 'http://127.0.0.1:5000';
const PAGE_ORIGIN = 'https://5000-sandbox.e2b.app';   // what a browser really sees
const NOT_FOUND = 'qqzznotfound';

const failures = [];
const requestedUrls = [];
const consoleErrors = [];

function check(label, ok, detail = '') {
  console.log(`[${ok ? 'ok  ' : 'FAIL'}] ${label}${detail ? ` -- ${detail}` : ''}`);
  if (!ok) failures.push(label);
}

// ------------------------------------------------------------------- environment

const virtualConsole = new VirtualConsole();
virtualConsole.on('jsdomError', (e) => consoleErrors.push(`jsdomError: ${e.message}`));
virtualConsole.on('error', (...args) => consoleErrors.push(args.join(' ')));

const dom = new JSDOM(`<!doctype html><html><body><div id="root"></div></body></html>`, {
  url: `${PAGE_ORIGIN}/`,
  pretendToBeVisual: true,
  virtualConsole,
});
const { window } = dom;

globalThis.window = window;
globalThis.document = window.document;
globalThis.navigator = window.navigator;
for (const key of ['HTMLElement', 'HTMLInputElement', 'Element', 'Node', 'Event', 'CustomEvent',
                   'KeyboardEvent', 'MouseEvent', 'SVGElement', 'getComputedStyle',
                   'requestAnimationFrame', 'cancelAnimationFrame']) {
  globalThis[key] = window[key];
}
window.addEventListener('error', (e) => consoleErrors.push(`window.onerror: ${e.message}`));

// React logs through the bundle's own `console`, which in node is the real one
// rather than the jsdom virtual console - so hook that too, or a duplicate-key
// warning sails straight past the check at the end.
for (const level of ['error', 'warn']) {
  const original = console[level].bind(console);
  console[level] = (...args) => {
    const line = args.map((a) => (a instanceof Error ? a.stack : String(a))).join(' ');
    consoleErrors.push(`console.${level}: ${line.split('\n')[0]}`);
    original(...args);
  };
}
window.addEventListener('unhandledrejection', (e) => consoleErrors.push(`unhandled: ${e.reason}`));

// Relative URL -> the real server. Anything absolute is a violation: browser
// code must never name a host, or it breaks behind the preview proxy.
const nodeFetch = globalThis.fetch;
globalThis.fetch = async (input, init) => {
  const raw = typeof input === 'string' ? input : input.url;
  requestedUrls.push(raw);
  if (/^https?:\/\//i.test(raw)) throw new Error(`client requested an absolute URL: ${raw}`);
  return nodeFetch(new URL(raw, ORIGIN).toString(), init);
};
window.fetch = globalThis.fetch;

// ------------------------------------------------------------------ dom helpers

const $ = (sel) => window.document.querySelector(sel);
const $$ = (sel) => [...window.document.querySelectorAll(sel)];
const text = () => window.document.body.textContent ?? '';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Accepts a sync or async predicate; polls until it returns something truthy. */
async function waitFor(label, predicate, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    let value = null;
    try { value = await predicate(); } catch { value = null; }
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${label}`);
    await sleep(50);
  }
}

/** React ignores a plain `input.value = x`, so go through the native setter. */
function typeInto(el, value) {
  if (!el) throw new Error('typeInto: no element');
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  setter.call(el, value);
  el.dispatchEvent(new window.Event('input', { bubbles: true }));
}

const click = (el) => {
  if (!el) throw new Error('click: no element');
  el.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
};
const settle = (ms = 150) => sleep(ms);
const byText = (sel, re) => $$(sel).find((e) => re.test(e.textContent ?? ''));
const api = async (p) => nodeFetch(`${ORIGIN}${p}`).then((r) => r.json());

// ------------------------------------------------------------------------ run

async function main() {
  console.log(`server:      ${ORIGIN}\npage origin: ${PAGE_ORIGIN}\n`);

  const health = await api('/api/health');
  check('API is up', health.status === 'ok', JSON.stringify(health));

  // Seed a known portfolio rather than inheriting whatever the last run left
  // behind: an aborted run must not change what the next one asserts. This is
  // also the state the app is left in afterwards, so it doubles as demo data.
  //
  // The category list is reset first, because a holding's category only means
  // something relative to it: seeding "Commodities" into a portfolio whose list
  // was left renamed would have the server clear it on the way in.
  const meta0 = await api('/api/meta');
  const defaultCategories = meta0.defaultCategories.map((c) => c.name);
  await nodeFetch(`${ORIGIN}/api/categories`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ categories: defaultCategories.map((name) => ({ name })) }),
  }).then((r) => {
    if (!r.ok) throw new Error(`could not reset the categories: HTTP ${r.status}`);
    return r.json();
  });

  const seed = {
    baseCurrency: 'USD',
    holdings: [
      { kind: 'crypto', assetId: 'pax-gold', symbol: 'PAXG', name: 'PAX Gold', quantity: 2.5 },
      { kind: 'crypto', assetId: 'bitcoin', symbol: 'BTC', name: 'Bitcoin', quantity: 0.25 },
      { kind: 'stock', assetId: 'AAPL', symbol: 'AAPL', name: 'Apple Inc.', quantity: 10,
        exchange: 'NASDAQ', sector: 'Technology', industry: 'Consumer Electronics',
        unitCost: 150, costCurrency: 'USD' },
      { kind: 'etf', assetId: 'SPY', symbol: 'SPY', name: 'SPDR S&P 500 ETF Trust', quantity: 5,
        sector: 'Financial Services' },
      { kind: 'currency', assetId: 'EUR', symbol: 'EUR', name: 'Euro', quantity: 1000 },
      { kind: 'other', assetId: 'GOLDBAR', symbol: 'GOLDBAR', name: 'Gold bar 100g', quantity: 1,
        category: 'Commodities', manualUnitPrice: 3600, manualPriceCurrency: 'USD', note: 'in the safe' },
    ],
    updatedUtc: new Date().toISOString(),
  };
  const seeded = await nodeFetch(`${ORIGIN}/api/portfolio`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(seed),
  }).then((r) => {
    if (!r.ok) throw new Error(`could not seed the portfolio: HTTP ${r.status}`);
    return r.json();
  });
  const before = seeded.holdings.length;
  console.log(`seeded ${before} holdings, base ${seeded.baseCurrency}, ${defaultCategories.length} categories\n`);

  await import(BUNDLE);

  // ---- shell -------------------------------------------------------------
  await waitFor('the app to mount', () => $('.topbar'));
  check('top bar rendered', $('.topbar__title')?.textContent === 'Portfolio', $('.topbar__title')?.textContent);
  check('bottom nav has four tabs', $$('.nav__item').length === 4, `${$$('.nav__item').length}`);
  check('nav has the raised add button', Boolean($('.nav__add .nav__plus')));

  await waitFor('the shell to render', () => (/Total value/.test(text()) ? true : null));
  check('the hero shows a shimmer, not a bare ellipsis, while prices load',
        Boolean($('.hero__total .skeleton')) || /[\d.,]/.test($('.hero__total')?.textContent ?? ''),
        $('.hero__total')?.textContent?.trim());
  await waitFor('the first report to land',
    () => ($('.hero__total') && !$('.hero__total .skeleton') && /\d/.test($('.hero__total').textContent) ? true : null));
  check('no "cannot reach the API" notice', !/Cannot reach the API/.test(text()));
  check('hero shows a total', ($('.hero__total')?.textContent ?? '').trim().length > 1, $('.hero__total')?.textContent);
  check('top bar shows a holding count and an updated time',
        /\d+ holdings? · updated/.test($('.topbar__sub')?.textContent ?? ''), $('.topbar__sub')?.textContent);

  {
    await waitFor('the holdings list', () => ($$('.holding').length > 0 ? true : null));
    check('one row per holding', $$('.holding').length === before, `${$$('.holding').length} vs ${before}`);
    check('allocation card rendered', Boolean($('.mix__seg')) && Boolean($('.alloc__row')));
    check('every row shows a value and a category',
          $$('.holding').every((h) => (h.querySelector('.holding__val')?.textContent ?? '').trim().length > 0
                                   && h.querySelector('.holding__tags .badge')));
    check('every row has an icon slot', $$('.holding .icon').length === before);
  }

  // ---- reports tab -------------------------------------------------------
  click($$('.nav__item')[1]);
  await settle();
  await waitFor('the reports view', () => (/By category/.test(text()) ? true : null));
  const categoryRows = $$('.table tbody tr');
  check('category table has rows', categoryRows.length > 0, `${categoryRows.length}`);
  check('category rows carry value and weight columns', categoryRows.every((r) => r.querySelectorAll('td').length >= 3));
  check('positions list rendered', $$('.holding').length === before, `${$$('.holding').length}`);
  check('report discloses that values are indicative', /indicative/i.test(text()));

  // The donut replaced the stacked bar here: it has to show the same shares,
  // add up to a whole ring, and survive a tap.
  {
    const segs = $$('.pie__seg');
    const items = $$('.pie__item');
    const groups = (await api('/api/report')).byCategory;
    check('the category donut is drawn', segs.length > 0, `${segs.length} slices`);
    check('one slice and one legend row per category',
          segs.length === items.length && items.length === groups.length,
          `${segs.length} slices / ${items.length} rows / ${groups.length} groups`);
    check('the slices follow the report order',
          items.map((i) => i.querySelector('.pie__item-name')?.textContent).join('|') === groups.map((g) => g.name).join('|'),
          items.map((i) => i.querySelector('.pie__item-name')?.textContent).join('|'));
    // pathLength is normalised to 100, so a dash is a percentage of the ring
    // and the offset is where the slice starts.
    const arcs = segs.map((sg) => {
      const dash = parseFloat((sg.getAttribute('stroke-dasharray') ?? '0').split(' ')[0]);
      const start = -parseFloat(sg.getAttribute('stroke-dashoffset') ?? '0');
      return { dash, start };
    });
    // A group with no value gets no slice, so compare against what is drawn.
    const drawn = groups.filter((g) => g.valueBase > 0);
    const totalVal = drawn.reduce((a, g) => a + g.valueBase, 0);
    const share = (g) => (g.valueBase / totalVal) * 100;
    check('every slice is a real arc', arcs.every((a) => a.dash > 0), arcs.map((a) => a.dash.toFixed(2)).join(' '));
    check('each arc is as wide as its share of the total',
          arcs.length === drawn.length && arcs.every((a, i) => Math.abs(a.dash - share(drawn[i])) < 1.5),
          arcs.map((a, i) => `${a.dash.toFixed(1)}~${share(drawn[i] ?? { valueBase: 0 }).toFixed(1)}`).join(' '));
    check('the arcs tile the ring clockwise without overlapping',
          Math.abs(arcs[0].start) < 1e-6
          && arcs.every((a, i) => i === 0 || a.start >= arcs[i - 1].start + arcs[i - 1].dash - 1e-6)
          && arcs.at(-1).start + arcs.at(-1).dash <= 100.001,
          arcs.map((a) => `${a.start.toFixed(1)}+${a.dash.toFixed(1)}`).join(' '));
    check('the gaps between arcs are separators, not missing value',
          arcs.reduce((sum, a) => sum + a.dash, 0) >= 100 - 1.5 * arcs.length,
          `${arcs.reduce((sum, a) => sum + a.dash, 0).toFixed(1)} of the ring drawn`);
    check('the slices are coloured by category', segs.every((sg) => (sg.getAttribute('stroke') ?? '').length > 3),
          segs[0]?.getAttribute('stroke'));
    check('the ring is described for a screen reader',
          /Allocation by category/.test($('.pie__chart svg')?.getAttribute('aria-label') ?? ''),
          $('.pie__chart svg')?.getAttribute('aria-label')?.slice(0, 70));
    check('the centre starts on the portfolio total', /Total/.test($('.pie__centre')?.textContent ?? ''),
          $('.pie__centre')?.textContent?.replace(/\s+/g, ' ').trim());
    check('the legend carries a value and a weight per row',
          items.every((i) => (i.querySelector('.pie__item-val')?.textContent ?? '').length > 1
                          && /%$/.test((i.querySelector('.pie__item-pct')?.textContent ?? '').trim())),
          items[0]?.textContent?.replace(/\s+/g, ' ').trim());

    click(segs[0]);
    await settle();
    check('tapping a slice puts that category in the middle',
          ($('.pie__centre-label')?.textContent ?? '') === groups[0].name
          && ($('.pie__centre')?.textContent ?? '').includes('%'),
          $('.pie__centre')?.textContent?.replace(/\s+/g, ' ').trim());
    check('the other slices dim so the selection reads',
          $$('.pie__seg').filter((sg) => sg.dataset.dim === 'true').length === segs.length - 1);
    click(items[0]);
    await settle();
    check('tapping the same slice again returns to the total',
          /Total/.test($('.pie__centre')?.textContent ?? ''), $('.pie__centre')?.textContent?.trim());
  }

  // ---- the search-first flow --------------------------------------------
  click($('.nav__add'));
  await settle();
  await waitFor('the search step', () => $('.search input'));
  check('Add opens on search, not on a form', Boolean($('.search input')));
  check('search field is focused', window.document.activeElement === $('.search input'));
  check('the manual escape hatch is offered up front', /manually/i.test(text()));

  // An empty query browses the top coins, so results are already on screen
  // before anything is typed. Wait for the list to actually match the query,
  // and check that the stale list is dimmed while the new one is in flight.
  typeInto($('.search input'), 'paxg');
  check('the previous results are dimmed the moment the query changes',
        $('.results')?.dataset.busy === 'true' || $$('.result').length === 0,
        `busy=${$('.results')?.dataset.busy}`);
  await waitFor('results matching paxg', () => (/PAX Gold/i.test(text()) ? true : null));
  await waitFor('the busy flag to clear', () => ($('.results')?.dataset.busy === 'false' ? true : null));
  await settle(150);
  const results = $$('.result');
  check('the results now match the typed query', /PAXG/i.test(results[0].textContent ?? ''),
        results[0].textContent?.replace(/\s+/g, ' ').slice(0, 60));
  check('autocomplete returned several matches', results.length >= 2, `${results.length}`);
  check('every result has an icon slot', $$('.result .icon').length === results.length);
  check('the coin result carries a real logo', Boolean($('.result .icon img')),
        $('.result .icon img')?.getAttribute('src')?.slice(0, 55));
  check('results show a name, not just a ticker', /PAX Gold/i.test(text()));
  check('results show a price', ($('.result__price')?.textContent ?? '').length > 1, $('.result__price')?.textContent);
  check('results show the detected category', /Commodities/.test($('.result__tags')?.textContent ?? ''),
        $('.result__tags')?.textContent);
  check('results say what kind of asset each is', /Crypto|Stock|Fund/i.test(results[0].textContent ?? ''),
        results[0].textContent?.replace(/\s+/g, ' ').slice(0, 60));
  check('a collision is not collapsed: both PAXG assets are offered',
        /Pine Tree|Stock/i.test(results.slice(1).map((r) => r.textContent).join(' ')));
  check('exactly one result is marked selected for the keyboard',
        results.filter((r) => r.dataset.active === 'true').length === 1);
  check('results are buttons a thumb can hit', results.every((r) => r.tagName === 'BUTTON'));

  click(results[0]);
  await waitFor('the amount step', () => $('#qty'));
  check('picking a result opens the amount step', Boolean($('#qty')));
  check('the chosen asset is shown large with its logo', Boolean($('.icon--lg')));
  check('the chosen asset keeps its price per unit', /per unit/.test(text()));
  check('amount field is focused', window.document.activeElement === $('#qty'));
  check('category defaults to Auto and names the detected value', /Auto · Commodities/.test(text()),
        $('.chips .chip')?.textContent);

  typeInto($('#qty'), '2.5');
  await settle();
  check('typing an amount shows an estimated value', /Estimated value/.test(text()),
        $('.field__hint')?.textContent?.trim().slice(0, 60));
  check('the add button names the amount', /Add 2\.5 PAXG/.test($('.btn--primary')?.textContent ?? ''),
        $('.btn--primary')?.textContent?.trim());
  check('cost basis is collapsed until asked for', !$('#cost'));
  click(byText('.btn--ghost', /Cost basis/));
  await settle();
  check('cost basis expands on request', Boolean($('#cost')) && Boolean($('#note')));
  typeInto($('#cost'), '3000');
  await settle();

  click($('.btn--primary'));
  const toast = await waitFor('the added toast', () => (/added/.test($('.toast')?.textContent ?? '') ? $('.toast') : null));
  check('adding confirms with a toast', /PAXG added/.test(toast.textContent), toast.textContent);
  await waitFor('the portfolio tab to come back', () => ($('.hero__total') ? true : null));
  const grown = await waitFor('the new holding to persist', async () => {
    const p = await api('/api/portfolio');
    return p.holdings.length === before + 1 ? p : null;
  });
  const added = grown.holdings[grown.holdings.length - 1];
  check('it kept the provider id, not the bare ticker', added.assetId === 'pax-gold', added.assetId);
  check('it kept the quantity', String(added.quantity) === '2.5', String(added.quantity));
  check('it kept the cost basis', String(added.unitCost) === '3000', String(added.unitCost));
  check('it kept the logo', typeof added.iconUrl === 'string' && added.iconUrl.startsWith('http'), String(added.iconUrl).slice(0, 50));
  check('category was left to auto-detection', added.category == null, String(added.category));
  check('the report priced it as the coin, not the $10 stock',
        await waitFor('a paxg report row', async () => {
          const r = await api('/api/report');
          const row = r.holdings.find((h) => h.symbol === 'PAXG' && h.kind === 'crypto');
          return row && row.unitPriceBase > 3000 ? row : null;
        }).then((row) => row.unitPriceBase > 3000),
        'unit price above 3000');

  await waitFor('the list to show it', () => ($$('.holding').length === before + 1 ? true : null));
  check('the holdings list grew by one row', $$('.holding').length === before + 1, `${$$('.holding').length}`);

  // ---- edit in a sheet ---------------------------------------------------
  const addedRow = await waitFor('the added row in the list', () => {
    const rows = $$('.holding');
    const last = rows[rows.length - 1];
    return last && last.textContent.includes(added.symbol) ? last : null;
  });
  click(addedRow);
  await waitFor('the edit sheet', () => $('.sheet'));
  check('tapping a holding opens a bottom sheet', Boolean($('.sheet')) && Boolean($('.scrim')));
  check('the sheet shows what it is worth', ($('.sheet .num')?.textContent ?? '').length > 1);
  const originalQty = $('#edit-qty')?.value ?? '';
  check('the sheet pre-fills the amount', originalQty.length > 0, originalQty);
  check('the sheet offers the category chips', $$('.sheet .chip').length >= 10, `${$$('.sheet .chip').length}`);
  check('save is disabled until something changes', $('.sheet .btn--primary')?.disabled === true,
        $('.sheet .btn--primary')?.textContent?.trim());
  const sheetText = $('.sheet')?.textContent ?? '';
  const editedQty = String(Number(originalQty || '1') + 1);
  typeInto($('#edit-qty'), editedQty);
  await settle();
  check('editing enables save', $('.sheet .btn--primary')?.disabled === false,
        $('.sheet .btn--primary')?.textContent?.trim());
  check('the sheet had shown the holding identity', /PAX|Gold/i.test(sheetText));
  click($('.sheet .btn--primary'));
  await waitFor('the sheet to close', () => ($('.sheet') ? null : true));
  check('the sheet closed after saving', !$('.sheet'));
  await waitFor('the edit to persist', async () => {
    const p = await api('/api/portfolio');
    return p.holdings.some((h) => h.id === added.id && String(h.quantity) === editedQty) ? p : null;
  });
  check(`quantity ${originalQty} -> ${editedQty} was saved`, true);

  // delete it again, with the two-tap confirm
  const rows2 = $$('.holding');
  click(rows2[rows2.length - 1]);
  await waitFor('the edit sheet again', () => $('.sheet'));
  const del = byText('.sheet .btn', /Delete holding/);
  check('delete is offered', Boolean(del));
  click(del);
  await settle();
  check('delete needs a second tap', /Tap again to delete/.test(del.textContent), del.textContent);
  click(del);
  await waitFor('the sheet to close after delete', () => ($('.sheet') ? null : true));
  await waitFor('the holding to be gone', async () => {
    const p = await api('/api/portfolio');
    return p.holdings.length === before ? p : null;
  });
  check('the holding was removed server-side', true);

  // ---- manual fallback ---------------------------------------------------
  click($('.nav__add'));
  await settle();
  typeInto($('.search input'), NOT_FOUND);
  await waitFor('the no-match state', () => (/No match for/.test(text()) ? true : null));
  check('a miss says so plainly', /No match for/.test($('.empty__title')?.textContent ?? ''), $('.empty__title')?.textContent);
  check('a miss explains the manual route', /by hand|your own price/i.test($('.empty')?.textContent ?? ''));
  const manualBtn = byText('.btn', new RegExp(`Add “${NOT_FOUND}” manually`));
  check('the empty state has a one-tap manual button', Boolean(manualBtn), manualBtn?.textContent?.trim());
  click(manualBtn);
  await waitFor('the manual form', () => $('#m-name'));
  check('the manual form explains what it is for', /physical metal|delisted|do not list/i.test(text()));
  check('the manual form asks for a price per unit', Boolean($('#m-price')));
  check('the manual form asks for a currency for that price', Boolean($('#m-cur')));

  typeInto($('#m-name'), 'Smoke test gold bar');
  typeInto($('#m-sym'), 'SMOKEBAR');
  typeInto($('#m-qty'), '2');
  typeInto($('#m-price'), '3600');
  await settle();
  check('the manual form shows the value as you type', /7,?200/.test(text()),
        $$('.small.muted').map((e) => e.textContent).join(' ').slice(-50));
  check('the manual form pre-fills the base currency', ($('#m-cur')?.value ?? '').length === 3, $('#m-cur')?.value);

  click($('.btn--primary'));
  await waitFor('the manual toast', () => (/added/.test($('.toast')?.textContent ?? '') ? $('.toast') : null));
  const manualSaved = await waitFor('the manual holding to persist', async () => {
    const p = await api('/api/portfolio');
    return p.holdings.find((h) => h.symbol === 'SMOKEBAR') ?? null;
  });
  check('manual holding saved with its own rate', String(manualSaved.manualUnitPrice) === '3600',
        JSON.stringify({ q: manualSaved.quantity, p: manualSaved.manualUnitPrice, c: manualSaved.manualPriceCurrency }));
  check('manual holding is kind=other', manualSaved.kind === 'other', manualSaved.kind);

  await waitFor('the manual row to appear', () => (byText('.holding', /SMOKEBAR/) ? true : null));
  await settle(300);
  const manualRow = byText('.holding', /SMOKEBAR/);
  check('the manual row is badged as manual', /Manual/.test(manualRow?.textContent ?? ''),
        manualRow?.textContent?.replace(/\s+/g, ' ').slice(0, 70));
  check('the manual row is valued from the supplied rate', /7,?200/.test(manualRow?.textContent ?? ''));

  click(manualRow);
  await waitFor('the sheet for the manual holding', () => $('.sheet'));
  check('the manual sheet edits the rate, not a cost basis', Boolean($('#edit-price')) && !$('#edit-cost'));
  const del2 = byText('.sheet .btn', /Delete holding/);
  click(del2); await settle(); click(del2);
  await waitFor('the manual holding to be deleted', async () => {
    const p = await api('/api/portfolio');
    return p.holdings.length === before ? p : null;
  });
  check('portfolio restored to its starting size', true);

  // ---- settings ----------------------------------------------------------
  click($$('.nav__item')[3]);
  await settle();
  await waitFor('the settings view', () => (/Base currency/.test(text()) ? true : null));
  check('settings offers base-currency chips', $$('.chip').length >= 8, `${$$('.chip').length}`);
  const exportHref = $('a[download]')?.getAttribute('href') ?? '';
  check('settings shows the export link', exportHref.length > 0, exportHref);
  check('the export link is relative, so it works through the proxy', exportHref.startsWith('/api/'), exportHref);
  check('settings shows the import button', Boolean(byText('.btn', /Import from file/)));
  check('settings has a hidden file input for import', Boolean($('input[type=file]')));
  await waitFor('the quota table', () => ($$('.table tbody tr').length >= 3 ? true : null));
  check('settings shows per-host quota counters', $$('.table tbody tr').length >= 3,
        $$('.table thead th').map((t) => t.textContent).join('/'));
  check('settings names the sources', /CoinGecko/.test(text()) && /Yahoo/.test(text()) && /er-api/.test(text()));
  check('settings shows the cache size', /Disk cache:/.test(text()),
        byText('.field__hint', /Disk cache/)?.textContent?.trim());

  // ---- the category editor ------------------------------------------------
  // Renaming has to carry the holdings, deleting has to release them, and the
  // app has to say which of the two happened. Left as the last step in Settings
  // so the list is back to its defaults before the run ends.
  {
    const defaults = defaultCategories;
    const rows = $$('.catrow');
    const rowFor = (name) => $$('.catrow').find((r) => r.querySelector('input.catrow__name')?.value === name);
    const saveBtn = () => byText('.btn--primary', /Save categories|Saved/);

    check('settings lists every category', rows.length === defaults.length, `${rows.length} rows`);
    check('a row says what fills it automatically', /auto · crypto/.test(rowFor('Crypto')?.textContent ?? ''),
          rowFor('Crypto')?.textContent?.replace(/\s+/g, ' ').trim());
    check('a hand-picked category says it is never chosen for you',
          /hand-picked/.test(rowFor('Real Estate')?.textContent ?? ''),
          rowFor('Real Estate')?.textContent?.replace(/\s+/g, ' ').trim());
    check('each row carries the colour the report uses',
          (rowFor('Crypto')?.querySelector('.catrow__swatch')?.getAttribute('style') ?? '').includes('background'),
          rowFor('Crypto')?.querySelector('.catrow__swatch')?.getAttribute('style'));
    check('save is asleep until something changes', saveBtn()?.disabled === true, saveBtn()?.textContent?.trim());
    check('the default list offers nothing to restore', !byText('.btn', /Restore defaults/));

    // Both a blank name and a duplicate are caught in the browser, before the
    // server has to refuse them.
    const cryptoInput = rowFor('Crypto').querySelector('input.catrow__name');
    typeInto(cryptoInput, '   ');
    await settle();
    check('a blank name is refused before it is sent',
          saveBtn()?.disabled === true && /needs a name/.test(text()),
          byText('.notice--err', /name/)?.textContent?.trim());

    typeInto(cryptoInput, 'Cash');
    await settle();
    check('a duplicate name is refused before it is sent',
          saveBtn()?.disabled === true && /appears twice/.test(text()),
          byText('.notice--err', /twice/)?.textContent?.trim());

    typeInto(cryptoInput, 'Uncategorised');
    await settle();
    check('the reserved fallback name is refused too',
          saveBtn()?.disabled === true && /reserved/.test(text()),
          byText('.notice--err', /reserved/)?.textContent?.trim());

    typeInto(cryptoInput, 'Digital assets');
    await settle();

    check('the renamed row shows that the old name is going away',
          $$('.catrow').some((r) => r.className.includes('catrow--gone')),
          $$('.catrow').map((r) => r.className).join(' '));
    check('save wakes up once the list differs', saveBtn()?.disabled === false, saveBtn()?.textContent?.trim());

    click(saveBtn());
    const renamedMeta = await waitFor('the rename to reach the server', async () => {
      const m = await api('/api/meta');
      return m.categories.some((c) => c.name === 'Digital assets') ? m : null;
    });
    const renamedCat = renamedMeta.categories.find((c) => c.name === 'Digital assets');
    check('a rename keeps the detection binding', renamedCat?.detect === 'crypto' && renamedCat?.auto === true,
          JSON.stringify(renamedCat));
    await waitFor('the rename toast', () => (/Categories saved/.test($('.toast')?.textContent ?? '') ? true : null));
    check('the toast reports the save, and only counts holdings that moved',
          /Categories saved/.test($('.toast')?.textContent ?? '')
          && !/moved with a rename/.test($('.toast')?.textContent ?? ''),
          $('.toast')?.textContent?.trim());
    check('the editor says what a rename does to the holdings in it',
          /filed under it follows/i.test(text()),
          byText('.small.muted', /filed under it follows/i)?.textContent?.replace(/\s+/g, ' ').trim().slice(0, 90));
    check('the editor offers to restore the defaults now', Boolean(byText('.btn', /Restore defaults/)));

    const renamedReport = await waitFor('the report to follow the rename', async () => {
      const r = await api('/api/report');
      return r.byCategory.some((g) => g.name === 'Digital assets') ? r : null;
    });
    check('coins are still filed where they were, under the new name',
          renamedReport.holdings.find((h) => h.symbol === 'BTC')?.category === 'Digital assets',
          JSON.stringify(renamedReport.byCategory.map((g) => [g.name, g.count])));

    // Add one of your own: nothing may be filed there behind your back.
    click(byText('.btn', /^\s*Add\s*$/));
    await settle();
    const fresh = $$('.catrow input.catrow__name').find((i) => i.value === '');
    check('Add appends an empty row and focuses it', Boolean(fresh), `${$$('.catrow').length} rows`);
    typeInto(fresh, 'Pension');
    await settle();
    check('a new row is marked hand-picked', /hand-picked/.test(fresh.closest('.catrow')?.textContent ?? ''),
          fresh.closest('.catrow')?.textContent?.replace(/\s+/g, ' ').trim());
    click(saveBtn());
    const addedMeta = await waitFor('the new category to reach the server', async () => {
      const m = await api('/api/meta');
      return m.categories.some((c) => c.name === 'Pension') ? m : null;
    });
    check('a category you added is never filled in automatically',
          addedMeta.categories.find((c) => c.name === 'Pension')?.auto === false,
          JSON.stringify(addedMeta.categories.find((c) => c.name === 'Pension')));

    // Delete the one the coins were detected into, and watch the app say so
    // rather than quietly moving them somewhere else.
    const gone = $$('.catrow').find((r) => r.querySelector('input.catrow__name')?.value === 'Digital assets');
    click(gone.querySelector('.iconbtn--danger'));
    await settle();
    check('delete removes the row at once', !$$('.catrow input.catrow__name').some((i) => i.value === 'Digital assets'));
    click(saveBtn());
    const deletedMeta = await waitFor('the deletion to reach the server', async () => {
      const m = await api('/api/meta');
      return m.categories.every((c) => c.name !== 'Digital assets') ? m : null;
    });
    check('the list is one shorter', deletedMeta.categories.length === defaults.length, `${deletedMeta.categories.length}`);
    const freedReport = await waitFor('the freed holdings to show up as uncategorised', async () => {
      const r = await api('/api/report');
      return r.byCategory.some((g) => g.name === 'Uncategorised') ? r : null;
    });
    check('a deleted category leaves its holdings uncategorised, not misfiled',
          freedReport.holdings.find((h) => h.symbol === 'BTC')?.category === 'Uncategorised',
          JSON.stringify(freedReport.byCategory.map((g) => [g.name, g.count])));
    check('the report says why', /matches no category/.test(freedReport.problems.join(' ')), freedReport.problems.join(' | '));

    click($$('.nav__item')[1]);
    await settle();
    await waitFor('the warning to reach the reports tab', () => (/matches no category/.test(text()) ? true : null));
    check('the reports tab surfaces the problem', /matches no category/.test($('.notice--warn')?.textContent ?? ''),
          $('.notice--warn')?.textContent?.replace(/\s+/g, ' ').trim().slice(0, 90));
    check('Uncategorised gets a slice of its own',
          $$('.pie__item-name').some((n) => n.textContent === 'Uncategorised'),
          $$('.pie__item-name').map((n) => n.textContent).join('|'));

    // Put it all back the way the app ships.
    click($$('.nav__item')[3]);
    await settle();
    await waitFor('the settings view again', () => ($$('.catrow').length > 0 ? true : null));
    click(byText('.btn', /Restore defaults/));
    await settle();
    check('restore fills the built-in list back in',
          $$('.catrow input.catrow__name').map((i) => i.value).join('|') === defaults.join('|'),
          $$('.catrow input.catrow__name').map((i) => i.value).join('|'));
    click(saveBtn());
    await waitFor('the defaults to be back on the server', async () => {
      const m = await api('/api/meta');
      return m.categories.map((c) => c.name).join('|') === defaults.join('|') ? m : null;
    });
    check('the default list is back, bindings and all', true);
    const finalReport = await waitFor('the report to be clean again', async () => {
      const r = await api('/api/report');
      return r.problems.length === 0 ? r : null;
    });
    check('every holding is detected again',
          finalReport.holdings.find((h) => h.symbol === 'BTC')?.category === 'Crypto'
          && finalReport.holdings.find((h) => h.symbol === 'GOLDBAR')?.category === 'Commodities',
          JSON.stringify(finalReport.holdings.map((h) => [h.symbol, h.category])));
  }

  // Switch the base currency through the UI and watch the hero change.
  const usdReport = await api('/api/report');
  click(byText('.chip', /^EUR$/));
  await waitFor('the base currency to change', async () => {
    const p = await api('/api/portfolio');
    return p.baseCurrency === 'EUR' ? p : null;
  });
  await settle(800);
  click($$('.nav__item')[0]);
  await settle();
  await waitFor('the hero to show euros', () => (/€/.test($('.hero__total')?.textContent ?? '') ? true : null));
  const eurReport = await api('/api/report');
  check('the hero re-rendered in EUR', /€/.test($('.hero__total')?.textContent ?? ''), $('.hero__total')?.textContent?.trim());
  check('the EUR total is the USD total converted',
        Math.abs(eurReport.totalBase - usdReport.totalBase * 0.91) < usdReport.totalBase * 0.05,
        `${usdReport.totalBase.toFixed(2)} USD -> ${eurReport.totalBase.toFixed(2)} EUR`);

  click($$('.nav__item')[3]);
  await settle();
  click(byText('.chip', /^USD$/));
  await waitFor('the base currency to go back to USD', async () => {
    const p = await api('/api/portfolio');
    return p.baseCurrency === 'USD' ? p : null;
  });
  check('base currency restored to USD', true);

  // ---- empty state, and putting the portfolio back -----------------------
  // The first-run screen is what a new user sees, so it is worth rendering:
  // clear everything through the UI, check the empty state, then restore.
  const snapshot = await api('/api/portfolio');
  const clearBtn = byText('.btn--danger', /Delete every holding/);
  check('settings offers a clear-all action', Boolean(clearBtn), clearBtn?.textContent?.trim());
  click(clearBtn);
  await settle();
  check('clear-all needs a second tap', /Tap again to delete all/.test(clearBtn.textContent),
        clearBtn.textContent.trim());
  click(clearBtn);
  await waitFor('the portfolio to be emptied', async () => {
    const p = await api('/api/portfolio');
    return p.holdings.length === 0 ? p : null;
  });
  check('clear-all deleted every holding server-side', true);
  // Clearing happens on the Settings tab; the empty state lives on Portfolio.
  click($$('.nav__item')[0]);
  await settle();
  await waitFor('the empty state', () => (/Nothing here yet/.test(text()) ? true : null));
  check('the empty portfolio shows a first-run screen', /Nothing here yet/.test(text()));
  check('the empty state explains the flow', /Search for a coin, stock, fund, index or currency/i.test(text()));
  check('the empty state has one obvious next step', Boolean(byText('.btn--primary', /Add your first holding/)));
  check('the empty hero still reads as a total, not a gap', /\$?0/.test($('.hero__total')?.textContent ?? ''),
        $('.hero__total')?.textContent?.trim());
  check('the top bar says zero holdings', /0 holdings/.test($('.topbar__sub')?.textContent ?? ''),
        $('.topbar__sub')?.textContent);

  // Restore through the import path, which is the same PUT the file upload uses.
  const restored = await nodeFetch(`${ORIGIN}/api/portfolio`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(snapshot),
  }).then((r) => r.json());
  check('the portfolio was restored from the snapshot', restored.holdings.length === snapshot.holdings.length,
        `${restored.holdings.length} of ${snapshot.holdings.length}`);
  // The snapshot went in behind the app's back, so ask it to re-read: the
  // top bar's refresh button reloads the portfolio as well as the prices.
  click($('.topbar .btn--icon'));
  await waitFor('the list to come back', () => ($$('.holding').length === snapshot.holdings.length ? true : null));
  check('Refresh picks up an out-of-band change to the file',
        $$('.holding').length === snapshot.holdings.length, `${$$('.holding').length} rows`);

  // ---- hygiene -----------------------------------------------------------
  const absolute = requestedUrls.filter((u) => /^https?:/i.test(u));
  check('every client request used a relative URL', absolute.length === 0,
        `${requestedUrls.length} requests; absolute: ${absolute.join(', ') || 'none'}`);
  const endpoints = [...new Set(requestedUrls.map((u) => u.split('?')[0]))].sort();
  check('the client talked to the API', endpoints.some((u) => u.startsWith('/api/')), endpoints.join(' '));
  check('no uncaught errors or console errors', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '));
}

main()
  .catch((e) => { failures.push(`aborted: ${e.message}`); console.log(`\n[FAIL] aborted -- ${e.stack}`); })
  .finally(() => {
    console.log(`\n${failures.length === 0 ? 'UI SMOKE TEST PASSED' : `${failures.length} UI FAILURES:\n  - ${failures.join('\n  - ')}`}`);
    if (consoleErrors.length) console.log(`console errors:\n  - ${consoleErrors.slice(0, 5).join('\n  - ')}`);
    process.exit(failures.length === 0 ? 0 : 1);
  });
