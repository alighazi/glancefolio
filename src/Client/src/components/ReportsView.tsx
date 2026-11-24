import { Badge, Card, Empty, Icon, Spinner } from './ui';
import { PieChart } from './PieChart';
import { money, moneyAuto, quantity, signedMoney, signedPct, timeAgo } from '../format';
import { categoryColor, categoryVars } from '../palette';
import type { ValuationReport } from '../types';

interface Props {
  report: ValuationReport | null;
  booting: boolean;
  pricing: boolean;
  base: string;
  onRefresh: () => void;
}

export function ReportsView({ report, booting, pricing, base, onRefresh }: Props) {
  if (booting) {
    return (
      <div className="stack">
        <div className="skeleton" style={{ height: 128, borderRadius: 20 }} />
        <div className="skeleton" style={{ height: 240, borderRadius: 20 }} />
      </div>
    );
  }

  if (!report || report.holdings.length === 0) {
    return (
      <Card>
        <Empty title="No report yet">
          Add a holding and the category breakdown, weights and gain/loss appear here.
        </Empty>
      </Card>
    );
  }

  const { byCategory, bySector, holdings, totalBase } = report;
  const hasCost = holdings.some((h) => h.costBase != null);

  return (
    <>
      <Card
        title="Summary"
        action={
          <button className="btn btn--ghost btn--sm" onClick={onRefresh} disabled={pricing}>
            {pricing ? <Spinner /> : <Icon name="refresh" size={14} />} {pricing ? 'Working…' : 'Refresh'}
          </button>
        }
      >
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap' }}>
          <span className="num" style={{ fontSize: 30, fontWeight: 700, letterSpacing: '-0.02em' }}>
            {money(totalBase, base)}
          </span>
          <span className="small muted">in {base}</span>
        </div>
        <div className="hero__meta" style={{ marginTop: 10 }}>
          {hasCost && report.totalGainPct != null && (
            <span className={`delta ${report.totalGainPct > 0 ? 'delta--pos' : report.totalGainPct < 0 ? 'delta--neg' : 'delta--flat'} num`}>
              <Icon name={report.totalGainPct >= 0 ? 'up' : 'down'} size={13} strokeWidth={2.2} />
              {signedMoney(report.totalGainBase, base)} ({signedPct(report.totalGainPct)})
            </span>
          )}
          <span>{report.pricedCount} of {holdings.length} priced</span>
          <span>as of {timeAgo(report.asOfUtc)}</span>
        </div>
        {!hasCost && (
          <p className="field__hint" style={{ margin: '12px 0 0' }}>
            Add a “paid per unit” figure to a holding to unlock gain and loss.
          </p>
        )}
      </Card>

      {report.problems.length > 0 && (
        <div className="notice notice--warn">
          <Icon name="alert" size={16} />
          <div>
            <strong>Not everything could be priced</strong>
            <ul>{report.problems.slice(0, 6).map((p) => <li key={p}>{p}</li>)}</ul>
          </div>
        </div>
      )}

      <Card title="By category">
        <PieChart groups={byCategory} total={totalBase} currency={base} />
        <table className="table" style={{ marginTop: 16 }}>
          <thead>
            <tr>
              <th>Category</th>
              <th className="r">Value</th>
              <th className="r">Weight</th>
              {hasCost && <th className="r">Gain</th>}
            </tr>
          </thead>
          <tbody>
            {byCategory.map((g) => (
              <tr key={g.name}>
                <td>
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                    <span style={{ width: 9, height: 9, borderRadius: 3, background: categoryColor(g.name), flex: 'none' }} />
                    <span>
                      {g.name}
                      <span className="small muted" style={{ marginLeft: 6 }}>{g.count}</span>
                    </span>
                  </span>
                </td>
                <td className="r num">{money(g.valueBase, base)}</td>
                <td className="r num">{g.weightPct.toFixed(1)}%</td>
                {hasCost && (
                  <td className={`r num ${g.gainPct == null ? 'muted' : g.gainPct > 0 ? 'pos' : g.gainPct < 0 ? 'neg' : ''}`}>
                    {g.gainPct == null ? '—' : signedPct(g.gainPct)}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </Card>

      {bySector.length > 0 && (
        <Card title="By sector">
          <div className="alloc">
            {bySector.slice(0, 8).map((g) => (
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
          <p className="field__hint" style={{ margin: '12px 0 0' }}>
            Only listed equities carry a sector, so these weights are of the whole
            portfolio and will not add up to 100%.
          </p>
        </Card>
      )}

      <Card title="Positions" className="card--flush">
        <div className="list">
          {holdings.map((h) => (
            <div className="holding" key={h.id} style={{ cursor: 'default' }}>
              <span className="icon" data-kind={h.kind} aria-hidden="true">
                <span style={{ width: 9, height: 9, borderRadius: 3, background: categoryColor(h.category) }} />
              </span>
              <span style={{ minWidth: 0 }}>
                <span className="holding__sym">
                  {h.symbol}
                  {h.manual && <Badge>Manual</Badge>}
                  {h.stale && <Badge tone="warn">Stale</Badge>}
                </span>
                <span className="holding__name">
                  {quantity(h.quantity)} @ {h.unitPriceBase != null ? moneyAuto(h.unitPriceBase, base) : '—'}
                </span>
              </span>
              <span style={{ minWidth: 0 }}>
                <span className="holding__val num">{h.valueBase != null ? money(h.valueBase, base) : '—'}</span>
                <span className="holding__sub num">{h.weightPct.toFixed(1)}%</span>
              </span>
              <span className="holding__tags">
                <Badge tone="accent">{h.category}</Badge>
                {h.gainPct != null && (
                  <Badge tone={h.gainPct > 0 ? 'pos' : h.gainPct < 0 ? 'neg' : 'plain'}>
                    {signedMoney(h.gainBase, base)} · {signedPct(h.gainPct)}
                  </Badge>
                )}
                {h.error && <Badge tone="warn" title={h.error}>{h.error}</Badge>}
              </span>
            </div>
          ))}
        </div>
      </Card>

      <p className="small muted center" style={{ margin: 0, lineHeight: 1.6 }}>
        Values are indicative, not execution quotes. Prices are cached to stay
        inside the free tiers — press Refresh to force a new fetch.
      </p>
    </>
  );
}
