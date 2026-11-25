import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError, type HoldingMutationResult } from './api';
import { Icon, Toast, type IconName, type ToastState } from './components/ui';
import { AddView } from './components/AddView';
import { HoldingSheet } from './components/HoldingSheet';
import { PortfolioView } from './components/PortfolioView';
import { ReportsView } from './components/ReportsView';
import { SettingsView } from './components/SettingsView';
import { AuthView } from './components/AuthView';
import { timeAgo } from './format';
import type { AddHoldingRequest, CategoryEdit, Holding, Meta, Portfolio, UpdateHoldingRequest, User, ValuationReport } from './types';

type Tab = 'portfolio' | 'reports' | 'add' | 'settings';

const TABS: { id: Tab; label: string; icon: IconName }[] = [
  { id: 'portfolio', label: 'Portfolio', icon: 'wallet' },
  { id: 'reports', label: 'Reports', icon: 'chart' },
  { id: 'add', label: 'Add', icon: 'plus' },
  { id: 'settings', label: 'Settings', icon: 'gear' },
];

export default function App() {
  const [tab, setTab] = useState<Tab>('portfolio');
  const [user, setUser] = useState<User | null>(null);
  const [portfolio, setPortfolio] = useState<Portfolio | null>(null);
  const [report, setReport] = useState<ValuationReport | null>(null);
  const [meta, setMeta] = useState<Meta | null>(null);
  const [booting, setBooting] = useState(true);
  const [bootError, setBootError] = useState<string | null>(null);
  const [pricing, setPricing] = useState(false);
  const [toast, setToast] = useState<ToastState | null>(null);
  const [editing, setEditing] = useState<Holding | null>(null);
  const toastSeq = useRef(0);
  const toastTimer = useRef<number | null>(null);

  const notify = useCallback((text: string, tone: ToastState['tone'] = 'plain') => {
    toastSeq.current += 1;
    const id = toastSeq.current;
    setToast({ id, text, tone });
    if (toastTimer.current) window.clearTimeout(toastTimer.current);
    toastTimer.current = window.setTimeout(() => {
      setToast((t) => (t && t.id === id ? null : t));
    }, 3600);
  }, []);

  useEffect(() => () => { if (toastTimer.current) window.clearTimeout(toastTimer.current); }, []);

  const message = (e: unknown): string =>
    e instanceof ApiError ? e.message : e instanceof Error ? e.message : 'Something went wrong';

  /**
   * Portfolio and metadata arrive first (fast, cached), then the report — which
   * may need live prices and can take a couple of seconds. Splitting them keeps
   * the shell usable instead of showing one big spinner.
   */
  const loadReport = useCallback(async (force = false) => {
    setPricing(true);
    try {
      // The portfolio is re-read alongside the report. It is a local file, so
      // this costs nothing, and it means Refresh also picks up an out-of-band
      // change - a hand-edited portfolio.json, or an import in another tab.
      const [p, r] = await Promise.all([
        api.portfolio(),
        force ? api.refresh().then((x) => x.report) : api.report(),
      ]);
      setPortfolio(p);
      setReport(r);
      return r;
    } catch (e) {
      notify(message(e), 'err');
      return null;
    } finally {
      setPricing(false);
    }
  }, [notify]);

  const bootstrap = useCallback(async () => {
    setBooting(true);
    setBootError(null);
    try {
      const signedIn = await api.me();
      setUser(signedIn);
      const [m, p] = await Promise.all([api.meta(), api.portfolio()]);
      setMeta(m);
      setPortfolio(p);
      setBooting(false);
      await loadReport();
    } catch (e) {
      setBooting(false);
      if (e instanceof ApiError && e.status === 401) {
        setUser(null);
        setBootError(null);
      } else {
        setBootError(message(e));
      }
    }
  }, [loadReport]);

  useEffect(() => { void bootstrap(); }, [bootstrap]);

  const applyMutation = useCallback(async (result: HoldingMutationResult) => {
    setPortfolio(result.portfolio);
    await loadReport();
  }, [loadReport]);

  const addHolding = useCallback(async (req: AddHoldingRequest) => {
    try {
      const result = await api.addHolding(req);
      await applyMutation(result);
      notify(`${result.holding.symbol} added`, 'ok');
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [applyMutation, notify]);

  const updateHolding = useCallback(async (id: string, patch: UpdateHoldingRequest) => {
    try {
      const result = await api.updateHolding(id, patch);
      setEditing(null);
      await applyMutation(result);
      notify('Updated', 'ok');
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [applyMutation, notify]);

  const deleteHolding = useCallback(async (id: string) => {
    try {
      const result = await api.deleteHolding(id);
      setEditing(null);
      setPortfolio(result.portfolio);
      await loadReport();
      notify('Removed', 'ok');
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [loadReport, notify]);

  const setBaseCurrency = useCallback(async (code: string) => {
    try {
      const p = await api.setBaseCurrency(code);
      setPortfolio(p);
      await loadReport();
      notify(`Base currency: ${code}`, 'ok');
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [loadReport, notify]);

  const importPortfolio = useCallback(async (incoming: Portfolio) => {
    try {
      const p = await api.replacePortfolio(incoming);
      setPortfolio(p);
      await loadReport();
      notify(`Imported ${p.holdings.length} holdings`, 'ok');
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [loadReport, notify]);

  // One PUT with an empty list: deleting N holdings one request at a time would
  // be N chances to end up with a half-empty portfolio.
  const clearAll = useCallback(async () => {
    if (!portfolio) return false;
    try {
      const p = await api.replacePortfolio({ ...portfolio, holdings: [] });
      setPortfolio(p);
      await loadReport();
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [portfolio, loadReport, notify]);

  // The whole list goes up in one request. A rename has to carry its holdings
  // and a delete has to release them, so N separate calls would be N chances to
  // end up with holdings filed under a category that no longer exists.
  const saveCategories = useCallback(async (edits: CategoryEdit[]) => {
    try {
      const result = await api.putCategories(edits);
      setMeta((m) => (m ? { ...m, categories: result.categories } : m));
      setPortfolio(result.portfolio);
      await loadReport();
      const moved = [
        result.renamed ? `${result.renamed} moved with a rename` : null,
        result.released ? `${result.released} back to auto-detection` : null,
      ].filter(Boolean);
      notify(moved.length ? `Categories saved — ${moved.join(', ')}` : 'Categories saved', 'ok');
      return true;
    } catch (e) {
      notify(message(e), 'err');
      return false;
    }
  }, [loadReport, notify]);

  const signOut = async () => {
    try {
      await api.logout();
      setUser(null);
      setPortfolio(null);
      setReport(null);
      setMeta(null);
      setTab('portfolio');
    } catch (e) {
      notify(message(e), 'err');
    }
  };

  if (!booting && !bootError && !user) {
    return <AuthView onAuthenticated={(signedIn) => { setUser(signedIn); void bootstrap(); }} />;
  }

  const holdings = portfolio?.holdings ?? [];
  const base = report?.baseCurrency ?? portfolio?.baseCurrency ?? 'USD';
  const subtitle = booting
    ? 'Loading…'
    : bootError
      ? 'Server unreachable'
      : `${holdings.length} ${holdings.length === 1 ? 'holding' : 'holdings'} · updated ${timeAgo(report?.asOfUtc ?? portfolio?.updatedUtc)}`;

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <div className="topbar__title">Glancefolio</div>
          <div className="topbar__sub">{subtitle}</div>
        </div>
        <span className="topbar__spacer" />
        {pricing && <span className="spinner" title="Fetching prices" />}
        {user && <button className="btn btn--ghost btn--sm auth-signout" onClick={() => void signOut()} title={`Signed in as ${user.email}`}>Sign out</button>}
        <button
          className="btn btn--ghost btn--icon"
          onClick={() => void loadReport(true)}
          disabled={pricing || booting || Boolean(bootError)}
          aria-label="Refresh prices now"
          title="Refresh prices now"
        >
          <Icon name="refresh" />
        </button>
      </header>

      <main className="view">
        {bootError ? (
          <div className="notice notice--err">
            <Icon name="alert" size={16} />
            <div>
              <strong>Cannot reach the API.</strong>
              <div>{bootError}</div>
              <button className="btn btn--sm" style={{ marginTop: 10 }} onClick={() => void bootstrap()}>
                <Icon name="refresh" size={15} /> Try again
              </button>
            </div>
          </div>
        ) : tab === 'portfolio' ? (
          <PortfolioView
            booting={booting}
            portfolio={portfolio}
            report={report}
            pricing={pricing}
            base={base}
            onOpen={(h) => setEditing(h)}
            onAdd={() => setTab('add')}
            onRefresh={() => void loadReport(true)}
          />
        ) : tab === 'reports' ? (
          <ReportsView report={report} booting={booting} pricing={pricing} base={base} onRefresh={() => void loadReport(true)} />
        ) : tab === 'add' ? (
          <AddView meta={meta} base={base} onAdded={async (req) => {
            const ok = await addHolding(req);
            if (ok) setTab('portfolio');
            return ok;
          }} />
        ) : (
          <SettingsView
            portfolio={portfolio}
            meta={meta}
            base={base}
            onBaseCurrency={setBaseCurrency}
            onCategories={saveCategories}
            onImport={importPortfolio}
            onClear={clearAll}
            onRefresh={() => void loadReport(true)}
            notify={notify}
          />
        )}
      </main>

      <nav className="nav" aria-label="Primary">
        {TABS.map((t) => {
          const active = tab === t.id;
          const isAdd = t.id === 'add';
          return (
            <button
              key={t.id}
              className={isAdd ? 'nav__item nav__add' : 'nav__item'}
              aria-current={active ? 'page' : undefined}
              onClick={() => setTab(t.id)}
            >
              {isAdd ? (
                <span className="nav__plus"><Icon name="plus" size={20} strokeWidth={2.2} /></span>
              ) : (
                <>
                  <Icon name={t.icon} size={20} />
                  <span className="nav__label">{t.label}</span>
                </>
              )}
            </button>
          );
        })}
      </nav>

      {editing && (
        <HoldingSheet
          holding={editing}
          meta={meta}
          base={base}
          valuation={report?.holdings.find((h) => h.id === editing.id)}
          onClose={() => setEditing(null)}
          onSave={updateHolding}
          onDelete={deleteHolding}
        />
      )}

      <Toast toast={toast} />
    </div>
  );
}
