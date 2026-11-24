import { useState } from 'react';
import { money, moneyCompact } from '../format';
import { categoryColor } from '../palette';
import type { GroupTotal } from '../types';

const R = 38;          // ring radius in viewBox units
const STROKE = 13;     // resting thickness
const STROKE_ON = 15;  // hovered / selected, still inside the viewBox

/**
 * Allocation donut, drawn as inline SVG with no chart library.
 *
 * Each slice is one circle whose stroke is dashed to the slice's share, with
 * `pathLength` normalised to 100 so the maths is in percent and independent of
 * the radius. Tap a slice or a legend row to put that category in the middle;
 * tap again to go back to the total.
 *
 * Fractions come from the values rather than the server's rounded weights, so
 * the ring always closes exactly.
 */
export function PieChart({
  groups,
  total,
  currency,
}: {
  groups: GroupTotal[];
  total: number;
  currency: string;
}) {
  const [selected, setSelected] = useState<string | null>(null);

  const drawn = groups.filter((g) => g.valueBase > 0);
  const sum = drawn.reduce((acc, g) => acc + g.valueBase, 0);
  if (drawn.length === 0 || sum <= 0) return null;

  const active = selected ? drawn.find((g) => g.name === selected) ?? null : null;

  let start = 0;
  const slices = drawn.map((g) => {
    const pct = (g.valueBase / sum) * 100;
    // A hairline gap reads as separate slices; a big one would eat the small
    // allocations, so it scales down with the slice.
    const gap = pct > 4 ? 1.1 : pct > 1.2 ? 0.5 : 0;
    const dash = Math.max(pct - gap, 0.35);
    const slice = { group: g, pct, dash, offset: -start };
    start += pct;
    return slice;
  });

  const summary = drawn
    .map((g) => `${g.name} ${((g.valueBase / sum) * 100).toFixed(1)}%`)
    .join(', ');

  const toggle = (name: string) => setSelected((s) => (s === name ? null : name));

  return (
    <div className="pie">
      <div className="pie__chart">
        <svg viewBox="0 0 100 100" role="img" aria-label={`Allocation by category: ${summary}`}>
          <g transform="rotate(-90 50 50)">
            {slices.map(({ group, dash, offset }) => {
              const on = active?.name === group.name;
              return (
                <circle
                  key={group.name}
                  className="pie__seg"
                  cx="50"
                  cy="50"
                  r={R}
                  pathLength={100}
                  stroke={categoryColor(group.name)}
                  strokeWidth={on ? STROKE_ON : STROKE}
                  strokeDasharray={`${dash} ${100 - dash}`}
                  strokeDashoffset={offset}
                  data-sel={on}
                  data-dim={active !== null && !on}
                  onClick={() => toggle(group.name)}
                  role="button"
                  tabIndex={-1}
                  aria-label={`${group.name}: ${money(group.valueBase, currency)}`}
                >
                  <title>{`${group.name} — ${money(group.valueBase, currency)} (${((group.valueBase / sum) * 100).toFixed(1)}%)`}</title>
                </circle>
              );
            })}
          </g>
        </svg>
        <div className="pie__centre">
          {active ? (
            <>
              <span className="pie__centre-label">{active.name}</span>
              <span className="pie__centre-value num">{money(active.valueBase, currency)}</span>
              <span className="pie__centre-sub num">
                {((active.valueBase / sum) * 100).toFixed(1)}% · {active.count}{' '}
                {active.count === 1 ? 'holding' : 'holdings'}
              </span>
            </>
          ) : (
            <>
              <span className="pie__centre-label">Total</span>
              <span className="pie__centre-value num">{moneyCompact(total, currency)}</span>
              <span className="pie__centre-sub">{drawn.length} categories</span>
            </>
          )}
        </div>
      </div>

      <div className="pie__legend">
        {drawn.map((g) => (
          <button
            key={g.name}
            className="pie__item"
            data-sel={active?.name === g.name}
            onClick={() => toggle(g.name)}
            aria-pressed={active?.name === g.name}
          >
            <span className="pie__item-swatch" style={{ background: categoryColor(g.name) }} />
            <span className="pie__item-name">{g.name}</span>
            <span className="pie__item-count">{g.count}</span>
            <span className="pie__item-val num">{money(g.valueBase, currency)}</span>
            <span className="pie__item-pct num">{((g.valueBase / sum) * 100).toFixed(1)}%</span>
          </button>
        ))}
      </div>
    </div>
  );
}
