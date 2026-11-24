import type { CSSProperties } from 'react';

// Category colours, shared by the allocation bar, the category list and the
// report so a category is the same colour everywhere in the app.

const GRADIENTS: Record<string, [string, string]> = {
  Cash: ['#34d399', '#10b981'],
  Crypto: ['#f7931a', '#fbbf24'],
  Stocks: ['#7c5cff', '#5b8cff'],
  'ETFs & Funds': ['#22d3ee', '#38bdf8'],
  Commodities: ['#fbbf24', '#f97316'],
  Indices: ['#a78bfa', '#60a5fa'],
  Bonds: ['#94a3b8', '#64748b'],
  'Real Estate': ['#f472b6', '#ec4899'],
  Collectibles: ['#fb7185', '#f43f5e'],
  Other: ['#64748b', '#475569'],
};

/**
 * Categories are editable, so most names will not be in the table above. Those
 * get a hue from the golden angle keyed on the name: stable between renders,
 * and spread far enough apart that two custom categories next to each other in
 * a pie chart are not the same colour. Four hand-picked fallbacks would run out
 * the moment someone added a fifth bucket.
 */
const GOLDEN_ANGLE = 137.508;

function hashName(name: string): number {
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) >>> 0;
  return hash;
}

function fromHue(hue: number): [string, string] {
  return [`hsl(${hue.toFixed(1)} 72% 64%)`, `hsl(${((hue + 18) % 360).toFixed(1)} 70% 50%)`];
}

export function categoryGradient(name: string): [string, string] {
  const known = GRADIENTS[name];
  if (known) return known;
  return fromHue((hashName(name) * GOLDEN_ANGLE) % 360);
}

/** CSS custom properties for the `.alloc__fill` gradient. */
export function categoryVars(name: string): CSSProperties {
  const [c1, c2] = categoryGradient(name);
  return { ['--c1' as string]: c1, ['--c2' as string]: c2 } as CSSProperties;
}

export function categoryColor(name: string): string {
  return categoryGradient(name)[0];
}
