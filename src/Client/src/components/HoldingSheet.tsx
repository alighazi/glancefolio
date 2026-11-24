import { useEffect, useMemo, useState } from 'react';
import { AssetIcon, Badge, Icon, Sheet, Spinner } from './ui';
import { CategoryPicker } from './CategoryPicker';
import { kindLabel, money, moneyAuto, parseAmount, quantity, timeAgo } from '../format';
import type { Holding, HoldingValuation, Meta, UpdateHoldingRequest } from '../types';

interface Props {
  holding: Holding;
  meta: Meta | null;
  base: string;
  valuation?: HoldingValuation;
  onClose: () => void;
  onSave: (id: string, patch: UpdateHoldingRequest) => Promise<boolean>;
  onDelete: (id: string) => Promise<boolean>;
}

/**
 * One holding: what it is, what it is worth, and the five things a person ever
 * wants to change about it — amount, category, cost basis, manual price, note.
 */
export function HoldingSheet({ holding, meta, base, valuation, onClose, onSave, onDelete }: Props) {
  const categories = meta?.categories ?? [];
  const isManual = holding.kind === 'other';

  const [qtyRaw, setQtyRaw] = useState(String(holding.quantity));
  const [category, setCategory] = useState(holding.category ?? null);
  const [costRaw, setCostRaw] = useState(holding.unitCost != null ? String(holding.unitCost) : '');
  const [costCurrency, setCostCurrency] = useState(holding.costCurrency ?? base);
  const [priceRaw, setPriceRaw] = useState(holding.manualUnitPrice != null ? String(holding.manualUnitPrice) : '');
  const [priceCurrency, setPriceCurrency] = useState(holding.manualPriceCurrency ?? base);
  const [note, setNote] = useState(holding.note ?? '');
  // What detection alone says about this holding, used as the "Auto" label.
  // The server computes it; guessing it here would duplicate the rules and drift.
  const autoCategory = valuation?.detectedCategory;

  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);

  // Re-opening the sheet for a different holding must not keep the old draft.
  useEffect(() => {
    setQtyRaw(String(holding.quantity));
    setCategory(holding.category ?? null);
    setCostRaw(holding.unitCost != null ? String(holding.unitCost) : '');
    setCostCurrency(holding.costCurrency ?? base);
    setPriceRaw(holding.manualUnitPrice != null ? String(holding.manualUnitPrice) : '');
    setPriceCurrency(holding.manualPriceCurrency ?? base);
    setNote(holding.note ?? '');
    setConfirmDelete(false);
    setErr(null);
  }, [holding, base]);

  const dirty = useMemo(() => {
    const qty = parseAmount(qtyRaw);
    return (
      qty != null && qty !== holding.quantity ||
      category !== (holding.category ?? null) ||
      parseAmount(costRaw) !== (holding.unitCost ?? null) ||
      costCurrency.toUpperCase() !== (holding.costCurrency ?? base).toUpperCase() ||
      parseAmount(priceRaw) !== (holding.manualUnitPrice ?? null) ||
      priceCurrency.toUpperCase() !== (holding.manualPriceCurrency ?? base).toUpperCase() ||
      note.trim() !== (holding.note ?? '')
    );
  }, [qtyRaw, holding, category, costRaw, costCurrency, priceRaw, priceCurrency, note, base]);

  const save = async () => {
    setErr(null);
    const qty = parseAmount(qtyRaw);
    if (qty == null || qty === 0) { setErr('Amount must be a number greater than zero.'); return; }

    const patch: UpdateHoldingRequest = { quantity: qty, category, note: note.trim() || null };
    const cost = parseAmount(costRaw);
    patch.unitCost = cost;
    patch.costCurrency = cost != null ? costCurrency.trim().toUpperCase() : null;

    if (isManual || holding.manualUnitPrice != null) {
      const price = parseAmount(priceRaw);
      if (price == null || price <= 0) { setErr('A manual holding needs a price per unit.'); return; }
      patch.manualUnitPrice = price;
      patch.manualPriceCurrency = priceCurrency.trim().toUpperCase();
    }

    setBusy(true);
    await onSave(holding.id, patch);
    setBusy(false);
  };

  const remove = async () => {
    setBusy(true);
    await onDelete(holding.id);
    setBusy(false);
  };

  return (
    <Sheet title={holding.name || holding.symbol} sub={`${kindLabel[holding.kind]} · added ${timeAgo(holding.addedUtc)}`} onClose={onClose} labelledBy="holding-title">
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, margin: '4px 0 16px' }}>
        <AssetIcon symbol={holding.symbol} name={holding.name} iconUrl={holding.iconUrl} kind={holding.kind} size="lg" />
        <div style={{ minWidth: 0 }}>
          <div style={{ fontWeight: 700, fontSize: 18 }} className="num">
            {valuation?.valueBase != null ? money(valuation.valueBase, base) : '—'}
          </div>
          <div className="small muted">
            {quantity(holding.quantity)} {holding.symbol} @ {valuation?.unitPriceBase != null ? moneyAuto(valuation.unitPriceBase, base) : 'no price'}
          </div>
        </div>
      </div>

      <div className="chips" style={{ marginBottom: 16 }}>
        <Badge tone="accent">{valuation?.category ?? holding.category ?? 'Uncategorised'}</Badge>
        {holding.exchange && <Badge>{holding.exchange}</Badge>}
        {holding.sector && <Badge>{holding.sector}</Badge>}
        {holding.industry && <Badge>{holding.industry}</Badge>}
        {holding.assetId && !isManual && <Badge title={holding.assetId}>{holding.assetId.length > 16 ? `${holding.assetId.slice(0, 15)}…` : holding.assetId}</Badge>}
        {valuation?.stale && <Badge tone="warn">Stale price</Badge>}
        {valuation?.gainPct != null && (
          <Badge tone={valuation.gainPct > 0 ? 'pos' : valuation.gainPct < 0 ? 'neg' : 'plain'}>
            {valuation.gainBase != null ? `${money(valuation.gainBase, base)} · ` : ''}
            {valuation.gainPct > 0 ? '+' : ''}{valuation.gainPct.toFixed(2)}%
          </Badge>
        )}
      </div>

      {valuation?.error && (
        <div className="notice notice--warn">
          <Icon name="alert" size={16} />
          <div><strong>No live price.</strong><div>{valuation.error}</div></div>
        </div>
      )}

      <div className="stack">
        <div className="field">
          <label className="field__label" htmlFor="edit-qty">Amount held</label>
          <input id="edit-qty" className="input num" value={qtyRaw} onChange={(e) => setQtyRaw(e.target.value)}
                 inputMode="decimal" autoComplete="off" />
        </div>

        <div className="field">
          <CategoryPicker
            categories={categories}
            value={category}
            autoValue={autoCategory}
            onChange={setCategory}
            hint={
              category && autoCategory && category !== autoCategory
                ? <>Hand-picked: automatic detection would file this under {autoCategory}.</>
                : <>Detection follows the asset type the provider reports. Clear it to let the app decide again.</>
            }
          />
        </div>

        {!isManual && holding.manualUnitPrice == null ? (
          <div className="field">
            <label className="field__label" htmlFor="edit-cost">Paid per unit (cost basis)</label>
            <div className="row">
              <input id="edit-cost" className="input num" value={costRaw} onChange={(e) => setCostRaw(e.target.value)}
                     placeholder="0.00" inputMode="decimal" autoComplete="off" />
              <input className="input" style={{ maxWidth: 96 }} value={costCurrency}
                     onChange={(e) => setCostCurrency(e.target.value.toUpperCase())} maxLength={8} autoComplete="off"
                     aria-label="Cost currency" />
            </div>
            <span className="field__hint">Leave empty if you do not track cost.</span>
          </div>
        ) : (
          <div className="field">
            <label className="field__label" htmlFor="edit-price">Price per unit (manual)</label>
            <div className="row">
              <input id="edit-price" className="input num" value={priceRaw} onChange={(e) => setPriceRaw(e.target.value)}
                     placeholder="0.00" inputMode="decimal" autoComplete="off" />
              <input className="input" style={{ maxWidth: 96 }} value={priceCurrency}
                     onChange={(e) => setPriceCurrency(e.target.value.toUpperCase())} maxLength={8} autoComplete="off"
                     aria-label="Price currency" />
            </div>
            <span className="field__hint">
              This is the conversion rate for this holding. It never changes by itself.
            </span>
          </div>
        )}

        <div className="field">
          <label className="field__label" htmlFor="edit-note">Note</label>
          <input id="edit-note" className="input" value={note} onChange={(e) => setNote(e.target.value)}
                 placeholder="Where it is, when you bought it…" maxLength={140} autoComplete="off" />
        </div>
      </div>

      {err && <div className="notice notice--err"><Icon name="alert" size={16} /><div>{err}</div></div>}

      <div className="stack" style={{ marginTop: 4 }}>
        <button className="btn btn--primary btn--block" onClick={() => void save()} disabled={busy || !dirty}>
          {busy ? <Spinner /> : <Icon name="check" size={17} />}
          {busy ? 'Saving…' : dirty ? 'Save changes' : 'No changes'}
        </button>
        <button className="btn btn--danger btn--block" onClick={() => (confirmDelete ? void remove() : setConfirmDelete(true))} disabled={busy}>
          <Icon name="trash" size={16} />
          {confirmDelete ? `Tap again to delete ${holding.symbol}` : 'Delete holding'}
        </button>
        <button className="btn btn--ghost btn--block" onClick={onClose}>Close</button>
      </div>
    </Sheet>
  );
}
