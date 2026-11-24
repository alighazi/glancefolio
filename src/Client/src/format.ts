import type { AssetKind } from './types';

// Intl throws a RangeError on currency codes it does not recognise, and this
// app's base currency is whatever the user typed - so every formatter here
// degrades to a plain number plus a code suffix instead of crashing.
const fmtCache = new Map<string, Intl.NumberFormat | null>();

function currencyFormatter(code: string, digits: number): Intl.NumberFormat | null {
  const key = `${code}|${digits}`;
  const cached = fmtCache.get(key);
  if (cached !== undefined) return cached;
  let f: Intl.NumberFormat | null = null;
  try {
    f = new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: code,
      minimumFractionDigits: digits,
      maximumFractionDigits: digits,
    });
  } catch {
    f = null;
  }
  fmtCache.set(key, f);
  return f;
}

function usable(v: number | null | undefined): v is number {
  return typeof v === 'number' && Number.isFinite(v);
}

/** Money with a fixed number of decimals. */
export function money(v: number | null | undefined, code: string, digits = 2): string {
  if (!usable(v)) return '—';
  const f = currencyFormatter(code, digits);
  if (f) return f.format(v);
  return `${v.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${code}`;
}

/**
 * Money with decimals chosen from the magnitude, so a whole portfolio value and
 * a fraction-of-a-cent unit price are both readable without a sea of zeros.
 */
export function moneyAuto(v: number | null | undefined, code: string): string {
  if (!usable(v)) return '—';
  const a = Math.abs(v);
  const digits = a >= 1000 ? 2 : a >= 1 ? 2 : a >= 0.01 ? 4 : a > 0 ? 8 : 2;
  return money(v, code, digits);
}

/** A holding amount: coins, shares or currency units. No currency symbol. */
export function quantity(v: number | null | undefined): string {
  if (!usable(v)) return '—';
  const a = Math.abs(v);
  const digits = a >= 1000 ? 2 : a >= 1 ? 4 : a > 0 ? 8 : 2;
  return v.toLocaleString(undefined, { maximumFractionDigits: digits });
}

/** Compact money for tight spaces: $1.2M, $4.3k. */
export function moneyCompact(v: number | null | undefined, code: string): string {
  if (!usable(v)) return '—';
  try {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: code,
      notation: 'compact',
      maximumFractionDigits: 1,
    }).format(v);
  } catch {
    return money(v, code, 0);
  }
}

export function signedPct(v: number | null | undefined, digits = 2): string {
  if (!usable(v)) return '—';
  return `${v > 0 ? '+' : ''}${v.toFixed(digits)}%`;
}

export function signedMoney(v: number | null | undefined, code: string): string {
  if (!usable(v)) return '—';
  const s = moneyAuto(Math.abs(v), code);
  return v > 0 ? `+${s}` : v < 0 ? `−${s}` : s;
}

export function timeAgo(iso: string | null | undefined): string {
  if (!iso) return '—';
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '—';
  const secs = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (secs < 10) return 'just now';
  if (secs < 60) return `${secs}s ago`;
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 7) return `${days}d ago`;
  return new Date(then).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export function bytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export const kindLabel: Record<AssetKind, string> = {
  crypto: 'Crypto',
  stock: 'Stock',
  etf: 'Fund',
  index: 'Index',
  future: 'Future',
  currency: 'Currency',
  other: 'Manual',
};

/** Two-letter monogram for assets with no logo source (equities, cash). */
export function monogram(symbol: string, name?: string): string {
  const s = (symbol || name || '?').trim();
  if (!s) return '?';
  const letters = s.replace(/[^A-Za-z0-9]/g, '');
  if (letters.length === 0) return s.slice(0, 2).toUpperCase();
  if (letters.length <= 3) return letters.toUpperCase();
  return (letters[0] + letters[1]).toUpperCase();
}

/** Parses what a human typed into an amount field: "1,5", "0.001", " 2 ". */
export function parseAmount(raw: string): number | null {
  const t = raw.trim().replace(/\s/g, '');
  if (!t) return null;
  // Treat a comma as the decimal separator when there is no dot, so both
  // "1,5" and "1.5" work regardless of keyboard locale.
  const normalised = t.includes('.') ? t.replace(/,/g, '') : t.replace(',', '.');
  if (!/^-?\d*\.?\d*$/.test(normalised)) return null;
  const n = Number(normalised);
  return Number.isFinite(n) ? n : null;
}
