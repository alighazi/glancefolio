import { useCallback, useEffect, useRef, useState } from 'react';
import { api, exportUrl } from '../api';
import { Badge, Card, Icon, Spinner } from './ui';
import { CategoryEditor } from './CategoryEditor';
import { bytes, timeAgo } from '../format';
import type { CategoryEdit, Diagnostics, Meta, Portfolio } from '../types';
import type { ToastState } from './ui';

interface Props {
  portfolio: Portfolio | null;
  meta: Meta | null;
  base: string;
  onBaseCurrency: (code: string) => Promise<boolean>;
  onCategories: (edits: CategoryEdit[]) => Promise<boolean>;
  onImport: (p: Portfolio) => Promise<boolean>;
  onClear: () => Promise<boolean>;
  onRefresh: () => void;
  notify: (text: string, tone?: ToastState['tone']) => void;
}

const COMMON_BASES = ['USD', 'EUR', 'GBP', 'CHF', 'JPY', 'CAD', 'AUD', 'IRR', 'TRY', 'AED', 'INR', 'CNY'];

export function SettingsView({ portfolio, meta, base, onBaseCurrency, onCategories, onImport, onClear, onRefresh, notify }: Props) {
  const [draft, setDraft] = useState(base);
  const [saving, setSaving] = useState(false);
  const [diag, setDiag] = useState<Diagnostics | null>(null);
  const [diagBusy, setDiagBusy] = useState(false);
  const [showAllCurrencies, setShowAllCurrencies] = useState(false);
  const [importBusy, setImportBusy] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearBusy, setClearBusy] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => { setDraft(base); }, [base]);

  const loadDiag = useCallback(async () => {
    setDiagBusy(true);
    try {
      setDiag(await api.diagnostics());
    } catch (e) {
      notify(e instanceof Error ? e.message : 'Could not load diagnostics', 'err');
    } finally {
      setDiagBusy(false);
    }
  }, [notify]);

  useEffect(() => { void loadDiag(); }, [loadDiag]);

  const saveBase = async () => {
    const code = draft.trim().toUpperCase();
    if (!code) return;
    setSaving(true);
    await onBaseCurrency(code);
    setSaving(false);
  };

  const onPickFile = async (file: File) => {
    setImportBusy(true);
    setImportError(null);
    try {
      const text = await file.text();
      const parsed = JSON.parse(text) as Portfolio;
      if (!parsed || typeof parsed !== 'object' || !Array.isArray(parsed.holdings)) {
        throw new Error('That file does not look like an exported portfolio.');
      }
      const ok = await onImport(parsed);
      if (ok) notify(`Imported ${parsed.holdings.length} holdings from ${file.name}`, 'ok');
    } catch (e) {
      setImportError(e instanceof Error ? e.message : 'Import failed');
    } finally {
      setImportBusy(false);
      if (fileRef.current) fileRef.current.value = '';
    }
  };

  const clearAll = async () => {
    setClearBusy(true);
    const ok = await onClear();
    setClearBusy(false);
    setConfirmClear(false);
    if (ok) notify('Every holding deleted', 'ok');
  };

  const all = meta?.currencies ?? [];
  // The current base is always offered, but it may already be one of the common
  // ones - dedupe, or React gets two children with the same key.
  const codes = showAllCurrencies
    ? all.map((c) => c.code)
    : [...new Set([...COMMON_BASES.filter((c) => all.some((x) => x.code === c)), base])];
  const nameOf = new Map(all.map((c) => [c.code, c.name]));
  const shown = codes.map((code) => ({ code, name: nameOf.get(code) ?? code }));

  return (
    <>
      <Card title="Base currency">
        <div className="field">
          <label className="field__label" htmlFor="base-input">Report everything in</label>
          <div className="row">
            <input
              id="base-input"
              className="input"
              style={{ maxWidth: 120, textTransform: 'uppercase' }}
              value={draft}
              maxLength={8}
              onChange={(e) => setDraft(e.target.value.toUpperCase())}
              onKeyDown={(e) => { if (e.key === 'Enter') void saveBase(); }}
              autoComplete="off"
            />
            <button className="btn btn--primary" onClick={() => void saveBase()} disabled={saving || draft.trim().toUpperCase() === base}>
              {saving ? <Spinner /> : <Icon name="check" size={16} />} Apply
            </button>
          </div>
          <span className="field__hint">
            FX from open.er-api.com — {all.length} codes, updated {timeAgo(meta?.fxAsOf)}
            {meta?.fxStale ? ' (cached copy in use)' : ''}.
          </span>
        </div>

        <div className="chips" style={{ marginTop: 12 }}>
          {shown.map(({ code, name }) => (
            <button
              key={code}
              className="chip"
              aria-pressed={code === base}
              title={name}
              onClick={() => { setDraft(code); void onBaseCurrency(code); }}
            >
              {code}
            </button>
          ))}
        </div>
        {all.length > COMMON_BASES.length && (
          <button className="btn btn--ghost btn--sm" style={{ marginTop: 10 }} onClick={() => setShowAllCurrencies((v) => !v)}>
            <Icon name={showAllCurrencies ? 'up' : 'down'} size={14} />
            {showAllCurrencies ? 'Show fewer' : `All ${all.length} currencies`}
          </button>
        )}
        {meta && !meta.fxAvailable && (
          <div className="notice notice--warn" style={{ marginTop: 12 }}>
            <Icon name="alert" size={16} />
            <div>{meta.fxError ?? 'Exchange rates are unavailable — values stay in USD.'}</div>
          </div>
        )}
      </Card>

      <Card title="Categories">
        {meta ? (
          <CategoryEditor
            categories={meta.categories}
            defaults={meta.defaultCategories}
            maxCategories={meta.maxCategories}
            maxNameLength={meta.maxCategoryNameLength}
            onSave={onCategories}
          />
        ) : (
          <div style={{ padding: '18px 0' }}><Spinner large /></div>
        )}
      </Card>

      <Card title="Your data">
        <p className="small muted" style={{ marginTop: 0, lineHeight: 1.6 }}>
          Everything lives in one JSON file on the server. Export it as a backup,
          or import it to move a portfolio between machines.
        </p>
        <div className="stack">
          <a className="btn btn--block" href={exportUrl} download>
            <Icon name="download" size={16} /> Export portfolio.json
          </a>
          <button className="btn btn--block" onClick={() => fileRef.current?.click()} disabled={importBusy}>
            {importBusy ? <Spinner /> : <Icon name="upload" size={16} />}
            {importBusy ? 'Importing…' : 'Import from file'}
          </button>
          <input
            ref={fileRef}
            type="file"
            accept="application/json,.json"
            hidden
            onChange={(e) => { const f = e.target.files?.[0]; if (f) void onPickFile(f); }}
          />
        </div>
        {importError && (
          <div className="notice notice--err" style={{ marginTop: 12 }}>
            <Icon name="alert" size={16} /><div>{importError}</div>
          </div>
        )}
        <div className="field__hint" style={{ marginTop: 12 }}>
          File: <code>{diag?.portfolioFile ?? '—'}</code>
        </div>

        <hr className="hr" />
        <button
          className="btn btn--danger btn--block"
          disabled={clearBusy || (portfolio?.holdings.length ?? 0) === 0}
          onClick={() => {
            if (confirmClear) void clearAll();
            else setConfirmClear(true);
          }}
        >
          <Icon name="trash" size={16} />
          {confirmClear
            ? `Tap again to delete all ${portfolio?.holdings.length ?? 0}`
            : 'Delete every holding'}
        </button>
        <p className="field__hint" style={{ marginTop: 8, marginBottom: 0 }}>
          Export first if you might want this back — the server keeps one
          automatic <code>.bak</code> of the previous save, and that is all.
        </p>
      </Card>

      <Card
        title="Quota &amp; cache"
        action={
          <button className="btn btn--ghost btn--sm" onClick={() => { void loadDiag(); onRefresh(); }} disabled={diagBusy}>
            {diagBusy ? <Spinner /> : <Icon name="refresh" size={14} />} Reload
          </button>
        }
      >
        <p className="small muted" style={{ marginTop: 0, lineHeight: 1.6 }}>
          Free providers have hard limits, so requests are cached on disk and
          rate-limited per host. A cache hit costs no quota; a stale serve means
          the last good value was reused because a refresh failed.
        </p>

        {diag && (
          <>
            <table className="table">
              <thead>
                <tr>
                  <th>Host</th>
                  <th className="r">Calls</th>
                  <th className="r">Cache hits</th>
                  <th className="r">Stale</th>
                  <th className="r">Errors</th>
                  <th className="r">Throttled</th>
                </tr>
              </thead>
              <tbody>
                {diag.hosts.map((h) => (
                  <tr key={h.host}>
                    <td>
                      <span style={{ fontWeight: 560 }}>{h.host}</span>
                      {h.budgetBlocked > 0 && (
                        <span style={{ display: 'block' }}><Badge tone="warn">daily budget hit</Badge></span>
                      )}
                    </td>
                    <td className="r num">{h.requests}</td>
                    <td className="r num">{h.cacheHits}</td>
                    <td className="r num">{h.staleServes}</td>
                    <td className={`r num ${h.errors > 0 ? 'neg' : 'muted'}`}>{h.errors}</td>
                    <td className={`r num ${h.throttled > 0 ? 'warn' : 'muted'}`}>{h.throttled}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="field__hint" style={{ margin: '12px 0 0' }}>
              Disk cache: {diag.cache.files} entries, {bytes(diag.cache.bytes)} · checked {timeAgo(diag.time)}
            </p>
          </>
        )}
        {!diag && diagBusy && <div style={{ padding: '18px 0' }}><Spinner large /></div>}
      </Card>

      <Card title="Sources">
        <ul className="small muted" style={{ margin: 0, paddingLeft: 18, lineHeight: 1.9 }}>
          <li><strong>CoinGecko</strong> — crypto search, icons, 24h change, prices (top 250 in one call)</li>
          <li><strong>Yahoo Finance</strong> — stocks, ETFs, indices, futures, FX pairs</li>
          <li><strong>open.er-api.com</strong> — 166 currency rates against USD</li>
        </ul>
        <p className="field__hint" style={{ margin: '12px 0 0' }}>
          No API keys are needed. All three are free tiers, so the app never
          requests more than it needs and always keeps the last good value.
        </p>
      </Card>

      <p className="small muted center" style={{ margin: 0, lineHeight: 1.6 }}>
        Portfolio · .NET backend + React front end · {portfolio?.holdings.length ?? 0} holdings
      </p>
    </>
  );
}
