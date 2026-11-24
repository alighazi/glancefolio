import { AssetIcon, Badge, Card, Empty, Icon } from './ui';
import { kindLabel, money, moneyAuto, quantity, signedMoney, signedPct, timeAgo } from '../format';
import { categoryColor, categoryVars } from '../palette';
import type { Holding, Portfolio, ValuationReport } from '../types';

interface Props {
  booting: boolean;
  portfolio: Portfolio | null;
  report: ValuationReport | null;
  pricing: boolean;
  base: string;
  onOpen: (h: Holding) => void;
  onAdd: () => void;
  onRefresh: () => void;
}

export function PortfolioView({ booting, portfolio, report, pricing, base, onOpen, onAdd, onRefresh }: Props) {
  if (booting) return <PortfolioSkeleton />;

  const holdings = portfolio?.holdings ?? [];

  if (holdings.length === 0) {
    return (
      <>
        <Card className="hero">
          <div className="hero__label">Total value</div>
          <div className="hero__total num">{money(0, base)}</div>
          <div className="hero__meta">
            <span>Base currency {base}</span>
          </div>
        </Card>
        <Card>
          <Empty
            title="Nothing here yet"
            action={
              <button className="btn btn--primary" onClick={onAdd}>
                <Icon name="plus" size={17} /> Add your first holding
              </button>
            }
          >
            Search for a coin, stock, fund, index or currency, pick it from the
            list, and enter how much you hold. Anything you cannot find can be
            added by hand with your own price.
          </Empty>
        </Card>
      </>
    );
  }

  const total = report?.totalBase ?? 0;
  const gain = report?.totalGainBase ?? null;
  const gainPct = report?.totalGainPct ?? null;
  const groups = report?.byCategory ?? [];
  const rows = report?.holdings ?? [];
  const byId = new Map(holdings.map((h) => [h.id, h]));
  const problems = report?.problems ?? [];

  return (
    <>
      <Card className="hero">
        <div className="hero__label">Total value</div>
        <div className="hero__total num" style={{ opacity: pricing && !report ? 0.5 : 1 }}>
          {report
            ? money(total, base)
            // The portfolio loads fast but the first price fetch does not, so
            // show a shimmer rather than a bare ellipsis that looks broken.
            : <span className="skeleton" style={{ display: 'inline-block', width: '62%', height: '0.85em', verticalAlign: 'baseline' }} aria-hidden="true" />}
        </div>
        <div className="hero__meta">
          {gainPct !== null && gainPct !== undefined && (
            <span className={`delta ${gainPct > 0 ? 'delta--pos' : gainPct < 0 ? 'delta--neg' : 'delta--flat'} num`}>
              <Icon name={gainPct >= 0 ? 'up' : 'down'} size={13} strokeWidth={2.2} />
              {signedMoney(gain, base)} ({signedPct(gainPct)})
            </span>
          )}
          <span>in {base}</span>
          {report && (
            <span>
              {report.pricedCount} priced
              {report.unpricedCount > 0 ? ` · ${report.unpricedCount} unpriced` : ''}
              {report.staleCount > 0 ? ` · ${report.staleCount} stale` : ''}
            </span>
          )}
          {report && <span title={new Date(report.asOfUtc).toLocaleString()}>as of {timeAgo(report.asOfUtc)}</span>}
        </div>
      </Card>

      {problems.length > 0 && (
        <div className="notice notice--warn">
          <Icon name="alert" size={16} />
          <div>
            <strong>{problems.length === 1 ? 'One thing needs attention' : `${problems.length} things need attention`}</strong>
            <ul>
              {problems.slice(0, 4).map((p) => <li key={p}>{p}</li>)}
              {problems.length > 4 && <li>…and {problems.length - 4} more</li>}
            </ul>
          </div>
        </div>
      )}

      {groups.length > 0 && (
        <Card title="Allocation">
          <div className="mix" role="img" aria-label={groups.map((g) => `${g.name} ${g.weightPct.toFixed(1)}%`).join(', ')}>
            {groups.map((g) => (
              <span
                key={g.name}
                className="mix__seg"
                style={{ width: `${Math.max(g.weightPct, 1.5)}%`, background: categoryColor(g.name) }}
              />
            ))}
          </div>
          <div className="alloc" style={{ marginTop: 14 }}>
            {groups.slice(0, 5).map((g) => (
              <div className="alloc__row" key={g.name}>
                <span className="alloc__name">{g.name}</span>
                <span className="alloc__val num">{money(g.valueBase, base)}</span>
                <span className="alloc__track" style={categoryVars(g.name)}>
                  <span className="alloc__fill" style={{ width: `${Math.min(100, Math.max(g.weightPct, 0.5))}%` }} />
                </span>
                <span className="alloc__pct num">{g.weightPct.toFixed(1)}% · {g.count}</span>
              </div>
            ))}
          </div>
        </Card>
      )}

      <Card
        title={`Holdings (${holdings.length})`}
        className="card--flush"
        action={
          <button className="btn btn--ghost btn--sm" onClick={onRefresh} disabled={pricing}>
            <Icon name="refresh" size={14} /> {pricing ? 'Pricing…' : 'Refresh'}
          </button>
        }
      >
        <div className="list">
          {holdings.map((h) => {
            const v = rows.find((r) => r.id === h.id);
            return (
              <button key={h.id} className="holding" onClick={() => onOpen(h)}>
                <AssetIcon symbol={h.symbol} name={h.name} iconUrl={h.iconUrl} kind={h.kind} />
                <span style={{ minWidth: 0 }}>
                  <span className="holding__sym">
                    {h.symbol}
                    {v?.manual && <Badge>Manual</Badge>}
                    {v?.stale && <Badge tone="warn">Stale</Badge>}
                  </span>
                  <span className="holding__name">{h.name || kindLabel[h.kind]}</span>
                </span>
                <span style={{ minWidth: 0 }}>
                  <span className="holding__val num">
                    {v?.valueBase != null ? money(v.valueBase, base) : '—'}
                  </span>
                  <span className="holding__sub num">
                    {quantity(h.quantity)} @ {v?.unitPriceBase != null ? moneyAuto(v.unitPriceBase, base) : '—'}
                  </span>
                </span>
                <span className="holding__tags">
                  <Badge tone="accent">{v?.category ?? h.category ?? '—'}</Badge>
                  {v?.weightPct ? <Badge>{v.weightPct.toFixed(1)}%</Badge> : null}
                  {v?.gainPct != null && (
                    <Badge tone={v.gainPct > 0 ? 'pos' : v.gainPct < 0 ? 'neg' : 'plain'}>
                      {signedPct(v.gainPct)}
                    </Badge>
                  )}
                  {v?.error && <Badge tone="warn" title={v.error}>No price</Badge>}
                </span>
              </button>
            );
          })}
        </div>
      </Card>

      <button className="btn btn--block" onClick={onAdd}>
        <Icon name="plus" size={17} /> Add another holding
      </button>

      <p className="small muted center" style={{ margin: 0 }}>
        {byId.size} tracked · prices from free public sources, cached
      </p>
    </>
  );
}

function PortfolioSkeleton() {
  return (
    <>
      <div className="skeleton" style={{ height: 128, borderRadius: 20 }} />
      <div className="skeleton" style={{ height: 190, borderRadius: 20 }} />
      <div className="stack">
        {[0, 1, 2].map((i) => <div key={i} className="skeleton" style={{ height: 62, borderRadius: 14 }} />)}
      </div>
    </>
  );
}
