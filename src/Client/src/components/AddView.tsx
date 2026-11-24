import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../api';
import { AssetIcon, Badge, Card, Icon, Spinner } from './ui';
import { CategoryPicker } from './CategoryPicker';
import { kindLabel, moneyAuto, parseAmount, quantity } from '../format';
import type { AddHoldingRequest, CategoryMeta, Meta, SearchItem } from '../types';

// Used only for the moment before /api/meta has answered.
const FALLBACK_CATEGORIES: CategoryMeta[] = [
  { name: 'Cash', detect: 'cash' }, { name: 'Crypto', detect: 'crypto' },
  { name: 'Stocks', detect: 'stocks' }, { name: 'ETFs & Funds', detect: 'funds' },
  { name: 'Commodities', detect: 'commodities' }, { name: 'Indices', detect: 'indices' },
  { name: 'Other', detect: 'other' },
].map((c) => ({ ...c, auto: true }));

type Step =
  | { name: 'search' }
  | { name: 'amount'; item: SearchItem }
  | { name: 'manual' };

interface Props {
  meta: Meta | null;
  base: string;
  onAdded: (req: AddHoldingRequest) => Promise<boolean>;
}

export function AddView({ meta, base, onAdded }: Props) {
  const [step, setStep] = useState<Step>({ name: 'search' });
  const categories = meta?.categories ?? FALLBACK_CATEGORIES;

  const back = () => setStep({ name: 'search' });

  return step.name === 'search' ? (
    <SearchStep onPick={(item) => setStep({ name: 'amount', item })} onManual={() => setStep({ name: 'manual' })} />
  ) : step.name === 'amount' ? (
    <AmountStep item={step.item} categories={categories} base={base} onBack={back} onAdded={onAdded} />
  ) : (
    <ManualStep categories={categories} base={base} onBack={back} onAdded={onAdded} />
  );
}

// ===========================================================================
// Step 1 - find the asset
// ===========================================================================

function SearchStep({
  onPick,
  onManual,
}: {
  onPick: (item: SearchItem) => void;
  onManual: () => void;
}) {
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<SearchItem[]>([]);
  // The query the items on screen belong to. Comparing it with what is in the
  // box is what tells us the list is stale - which is true the instant a key is
  // pressed, not 280ms later when the debounce fires.
  const [itemsFor, setItemsFor] = useState('');
  const [degraded, setDegraded] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [cursor, setCursor] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => { inputRef.current?.focus(); }, []);

  // Debounced so typing a word does not become seven requests, and aborted so
  // a slow earlier query cannot overwrite the results of a later one.
  useEffect(() => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    const timer = window.setTimeout(async () => {
      setLoading(true);
      setError(null);
      try {
        const res = await api.search(query.trim(), 12, controller.signal);
        setItems(res.items);
        setItemsFor(query.trim());
        setDegraded(res.degraded);
        setCursor(0);
      } catch (e) {
        if ((e as Error).name === 'AbortError') return;
        setError(e instanceof Error ? e.message : 'Search failed');
        setItems([]);
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }, query.trim().length === 0 ? 60 : 280);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [query]);

  const busy = loading || itemsFor !== query.trim();

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (items.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setCursor((c) => Math.min(c + 1, items.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((c) => Math.max(c - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); onPick(items[cursor]); }
    else if (e.key === 'Escape') { setQuery(''); }
  };

  return (
    <>
      <div className="search">
        <span className="search__icon">{busy ? <Spinner /> : <Icon name="search" size={18} />}</span>
        <input
          ref={inputRef}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder="Search coins, stocks, funds, currencies…"
          aria-label="Search for an asset"
          autoComplete="off"
          autoCorrect="off"
          spellCheck={false}
          enterKeyHint="search"
        />
        {query.length > 0 && (
          <button className="btn btn--ghost btn--icon" onClick={() => setQuery('')} aria-label="Clear search">
            <Icon name="x" size={16} />
          </button>
        )}
      </div>

      {error && (
        <div className="notice notice--err">
          <Icon name="alert" size={16} />
          <div><strong>Search failed.</strong><div>{error}</div></div>
        </div>
      )}

      {degraded.map((d) => (
        <div className="notice notice--warn" key={d}>
          <Icon name="info" size={16} />
          <div>{d}</div>
        </div>
      ))}

      {items.length > 0 ? (
        <Card className="card--flush">
          {/* The previous query's results stay on screen while the new one is in
              flight so the list does not collapse on every keystroke - but they
              are dimmed and marked busy, so they cannot be read as current. */}
          <div className="results" role="listbox" aria-label="Search results" aria-busy={busy} data-busy={busy}>
            {items.map((it, i) => <ResultRow key={`${it.kind}:${it.id}`} item={it} active={i === cursor} onPick={() => onPick(it)} onHover={() => setCursor(i)} />)}
          </div>
        </Card>
      ) : !busy && query.trim().length > 0 && !error ? (
        <Card>
          <div className="empty">
            <div className="empty__title">No match for “{query.trim()}”</div>
            <p>
              You can still track it. Add it by hand and enter your own price per
              unit — useful for physical metal, a private deal, a delisted
              ticker, or anything the free sources do not cover.
            </p>
            <button className="btn btn--primary" onClick={onManual}>
              <Icon name="plus" size={17} /> Add “{query.trim()}” manually
            </button>
          </div>
        </Card>
      ) : null}

      <button className="btn btn--ghost btn--block" onClick={onManual}>
        <Icon name="edit" size={16} /> Can’t find it? Add it manually
      </button>

      <p className="small muted center" style={{ margin: 0, lineHeight: 1.6 }}>
        The same ticker can be two different assets — <strong>PAXG</strong> is both a
        tokenised gold coin and a listed company. Pick the row that matches;
        the app remembers which provider it came from.
      </p>
    </>
  );
}

function ResultRow({
  item,
  active,
  onPick,
  onHover,
}: {
  item: SearchItem;
  active: boolean;
  onPick: () => void;
  onHover: () => void;
}) {
  return (
    <button className="result" data-active={active} onClick={onPick} onMouseEnter={onHover} role="option" aria-selected={active}>
      <AssetIcon symbol={item.symbol} name={item.name} iconUrl={item.iconUrl} kind={item.kind} />
      <span style={{ minWidth: 0 }}>
        <span className="result__sym">{item.symbol}</span>
        <span className="result__name">{item.name}</span>
      </span>
      <span style={{ minWidth: 0 }}>
        <span className="result__price num">{item.priceUsd != null ? moneyAuto(item.priceUsd, 'USD') : '—'}</span>
        <span className="result__meta">
          {item.exchange ?? (item.rank ? `Rank #${item.rank}` : kindLabel[item.kind])}
        </span>
      </span>
      <span className="result__tags">
        <Badge>{kindLabel[item.kind]}</Badge>
        <Badge tone="accent">{item.category}</Badge>
        {item.sector && <Badge>{item.sector}</Badge>}
        {item.change24hPct != null && (
          <Badge tone={item.change24hPct >= 0 ? 'pos' : 'neg'}>
            {item.change24hPct >= 0 ? '+' : ''}{item.change24hPct.toFixed(1)}%
          </Badge>
        )}
      </span>
    </button>
  );
}

// ===========================================================================
// Step 2 - how much of it
// ===========================================================================

function AmountStep({
  item,
  categories,
  base,
  onBack,
  onAdded,
}: {
  item: SearchItem;
  categories: CategoryMeta[];
  base: string;
  onBack: () => void;
  onAdded: (req: AddHoldingRequest) => Promise<boolean>;
}) {
  const [qtyRaw, setQtyRaw] = useState('');
  const [category, setCategory] = useState<string | null>(null); // null = auto
  const [costRaw, setCostRaw] = useState('');
  const [costCurrency, setCostCurrency] = useState(base);
  const [note, setNote] = useState('');
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const qtyRef = useRef<HTMLInputElement>(null);

  useEffect(() => { qtyRef.current?.focus(); }, []);

  const qty = parseAmount(qtyRaw);
  const cost = parseAmount(costRaw);
  const effectiveCategory = category ?? item.category;

  const estimate = useMemo(() => {
    if (qty == null || item.priceUsd == null) return null;
    return qty * item.priceUsd;
  }, [qty, item.priceUsd]);

  const submit = useCallback(async () => {
    setErr(null);
    if (qty == null || qty === 0) { setErr('Enter how much you hold.'); return; }
    setBusy(true);
    const ok = await onAdded({
      kind: item.kind,
      assetId: item.id,
      symbol: item.symbol,
      name: item.name,
      quantity: qty,
      category,
      iconUrl: item.iconUrl ?? null,
      exchange: item.exchange ?? null,
      sector: item.sector ?? null,
      industry: item.industry ?? null,
      unitCost: cost ?? null,
      costCurrency: cost != null ? costCurrency : null,
      note: note.trim() || null,
    });
    setBusy(false);
    if (ok) {
      setQtyRaw(''); setCostRaw(''); setNote(''); setCategory(null);
      onBack();
    }
  }, [qty, item, category, cost, costCurrency, note, onAdded, onBack]);

  return (
    <>
      <div className="row" style={{ alignItems: 'center' }}>
        <button className="btn btn--ghost btn--icon" onClick={onBack} aria-label="Back to search">
          <Icon name="back" size={18} />
        </button>
        <h2 style={{ margin: 0, fontSize: 17, fontWeight: 650 }}>How much do you hold?</h2>
      </div>

      <Card>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <AssetIcon symbol={item.symbol} name={item.name} iconUrl={item.iconUrl} kind={item.kind} size="lg" />
          <div style={{ minWidth: 0 }}>
            <div style={{ fontWeight: 680, fontSize: 16 }}>{item.symbol}</div>
            <div className="small muted" style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{item.name}</div>
            <div style={{ display: 'flex', gap: 6, marginTop: 6, flexWrap: 'wrap' }}>
              <Badge>{kindLabel[item.kind]}</Badge>
              {item.exchange && <Badge>{item.exchange}</Badge>}
              {item.sector && <Badge>{item.sector}</Badge>}
            </div>
          </div>
          <span className="grow" />
          {item.priceUsd != null && (
            <div style={{ textAlign: 'right' }}>
              <div className="num" style={{ fontWeight: 650 }}>{moneyAuto(item.priceUsd, 'USD')}</div>
              <div className="small muted">per unit</div>
            </div>
          )}
        </div>
      </Card>

      <Card>
        <div className="field">
          <label className="field__label" htmlFor="qty">Amount held ({item.symbol})</label>
          <input
            id="qty"
            ref={qtyRef}
            className="input input--amount num"
            value={qtyRaw}
            onChange={(e) => setQtyRaw(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }}
            placeholder="0.00"
            inputMode="decimal"
            autoComplete="off"
          />
          <span className="field__hint">
            Shares, whole coins, fractions of a coin, or currency units.
            {estimate != null && <> Estimated value <strong className="num">{moneyAuto(estimate, 'USD')}</strong>.</>}
          </span>
        </div>
      </Card>

      <Card>
        <CategoryPicker categories={categories} value={category} autoValue={item.category} onChange={setCategory} />
      </Card>

      <button className="btn btn--ghost btn--block" onClick={() => setShowAdvanced((s) => !s)}>
        <Icon name={showAdvanced ? 'up' : 'down'} size={15} />
        {showAdvanced ? 'Hide' : 'Cost basis and note (optional)'}
      </button>

      {showAdvanced && (
        <Card>
          <div className="stack">
            <div className="row">
              <div className="field">
                <label className="field__label" htmlFor="cost">Paid per unit</label>
                <input id="cost" className="input num" value={costRaw} onChange={(e) => setCostRaw(e.target.value)}
                       placeholder="0.00" inputMode="decimal" autoComplete="off" />
              </div>
              <div className="field" style={{ maxWidth: 120 }}>
                <label className="field__label" htmlFor="costcur">In</label>
                <input id="costcur" className="input" value={costCurrency} onChange={(e) => setCostCurrency(e.target.value.toUpperCase())}
                       maxLength={8} autoComplete="off" />
              </div>
            </div>
            <div className="field">
              <label className="field__label" htmlFor="note">Note</label>
              <input id="note" className="input" value={note} onChange={(e) => setNote(e.target.value)}
                     placeholder="e.g. bought in March, cold wallet" maxLength={140} autoComplete="off" />
              <span className="field__hint">Adding a cost basis turns on gain/loss in the reports.</span>
            </div>
          </div>
        </Card>
      )}

      {err && <div className="notice notice--err"><Icon name="alert" size={16} /><div>{err}</div></div>}

      <button className="btn btn--primary btn--block" onClick={() => void submit()} disabled={busy}>
        {busy ? <Spinner /> : <Icon name="check" size={17} />}
        {busy ? 'Adding…' : `Add ${qty != null ? `${quantity(qty)} ${item.symbol}` : 'to portfolio'}`}
      </button>

      <p className="small muted center" style={{ margin: 0 }}>
        Category will be <strong>{effectiveCategory}</strong> unless you pick another.
      </p>
    </>
  );
}

// ===========================================================================
// Step 3 - the escape hatch for anything not listed
// ===========================================================================

function ManualStep({
  categories,
  base,
  onBack,
  onAdded,
}: {
  categories: CategoryMeta[];
  base: string;
  onBack: () => void;
  onAdded: (req: AddHoldingRequest) => Promise<boolean>;
}) {
  const [name, setName] = useState('');
  const [symbol, setSymbol] = useState('');
  const [qtyRaw, setQtyRaw] = useState('');
  const [priceRaw, setPriceRaw] = useState('');
  const [priceCurrency, setPriceCurrency] = useState(base);
  const [category, setCategory] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const qty = parseAmount(qtyRaw);
  const price = parseAmount(priceRaw);

  // Whatever the user now calls the catch-all that owns the "other" key.
  const manualDefault = categories.find((c) => c.detect === 'other')?.name
    ?? categories.find((c) => c.name === 'Other')?.name
    ?? 'Other';

  const submit = async () => {
    setErr(null);
    if (!name.trim()) { setErr('Give it a name so you recognise it later.'); return; }
    if (qty == null || qty === 0) { setErr('Enter how much you hold.'); return; }
    if (price == null || price <= 0) { setErr('Enter the price per unit — this is the conversion rate the app will use.'); return; }

    setBusy(true);
    const ok = await onAdded({
      kind: 'other',
      assetId: (symbol.trim() || name.trim()).toUpperCase(),
      symbol: (symbol.trim() || name.trim()).toUpperCase().slice(0, 24),
      name: name.trim(),
      quantity: qty,
      category,
      manualUnitPrice: price,
      manualPriceCurrency: priceCurrency.trim().toUpperCase() || base,
      note: note.trim() || null,
    });
    setBusy(false);
    if (ok) { setName(''); setSymbol(''); setQtyRaw(''); setPriceRaw(''); setNote(''); setCategory(null); onBack(); }
  };

  return (
    <>
      <div className="row" style={{ alignItems: 'center' }}>
        <button className="btn btn--ghost btn--icon" onClick={onBack} aria-label="Back to search">
          <Icon name="back" size={18} />
        </button>
        <h2 style={{ margin: 0, fontSize: 17, fontWeight: 650 }}>Add it manually</h2>
      </div>

      <div className="notice">
        <Icon name="info" size={16} />
        <div>
          For anything the free sources do not list: physical metal, a private
          deal, a delisted ticker, a foreign bank balance. You supply the price
          per unit and the app converts it like any other holding. It never
          updates by itself — edit it whenever the value changes.
        </div>
      </div>

      <Card>
        <div className="stack">
          <div className="field">
            <label className="field__label" htmlFor="m-name">Name</label>
            <input id="m-name" className="input" value={name} onChange={(e) => setName(e.target.value)}
                   placeholder="Gold bar 100g" maxLength={60} autoComplete="off" />
          </div>
          <div className="row">
            <div className="field">
              <label className="field__label" htmlFor="m-sym">Label (optional)</label>
              <input id="m-sym" className="input" value={symbol} onChange={(e) => setSymbol(e.target.value.toUpperCase())}
                     placeholder="XAU" maxLength={24} autoComplete="off" />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="m-qty">Amount</label>
              <input id="m-qty" className="input num" value={qtyRaw} onChange={(e) => setQtyRaw(e.target.value)}
                     placeholder="1" inputMode="decimal" autoComplete="off" />
            </div>
          </div>
          <div className="row">
            <div className="field">
              <label className="field__label" htmlFor="m-price">Price per unit</label>
              <input id="m-price" className="input num" value={priceRaw} onChange={(e) => setPriceRaw(e.target.value)}
                     placeholder="0.00" inputMode="decimal" autoComplete="off" />
            </div>
            <div className="field" style={{ maxWidth: 120 }}>
              <label className="field__label" htmlFor="m-cur">In</label>
              <input id="m-cur" className="input" value={priceCurrency}
                     onChange={(e) => setPriceCurrency(e.target.value.toUpperCase())} maxLength={8} autoComplete="off" />
            </div>
          </div>
          <div className="field">
            <label className="field__label" htmlFor="m-note">Note</label>
            <input id="m-note" className="input" value={note} onChange={(e) => setNote(e.target.value)}
                   placeholder="Kept in a safe" maxLength={140} autoComplete="off" />
          </div>
        </div>
      </Card>

      <Card>
        <CategoryPicker
          categories={categories}
          value={category}
          autoValue={manualDefault}
          onChange={setCategory}
          hint="Nothing here is priced by a provider, so detection has nothing to go on: pick the category this belongs in."
        />
      </Card>

      {qty != null && price != null && (
        <p className="small muted center" style={{ margin: 0 }}>
          Value: <strong className="num">{moneyAuto(qty * price, priceCurrency || base)}</strong>
        </p>
      )}

      {err && <div className="notice notice--err"><Icon name="alert" size={16} /><div>{err}</div></div>}

      <button className="btn btn--primary btn--block" onClick={() => void submit()} disabled={busy}>
        {busy ? <Spinner /> : <Icon name="check" size={17} />}
        {busy ? 'Adding…' : 'Add to portfolio'}
      </button>
    </>
  );
}
