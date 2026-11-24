// Mirrors the server DTOs (camelCase on the wire, enums as strings).

export type AssetKind =
  | 'crypto'
  | 'stock'
  | 'etf'
  | 'index'
  | 'future'
  | 'currency'
  | 'other';

export interface SearchItem {
  kind: AssetKind;
  id: string;          // provider-native: coingecko id | yahoo symbol | ISO code
  symbol: string;
  name: string;
  exchange?: string;
  sector?: string;
  industry?: string;
  iconUrl?: string;
  priceUsd?: number;
  change24hPct?: number;
  rank?: number;
  source: string;
  category: string;
}

export interface SearchResponse {
  items: SearchItem[];
  sources: string[];
  degraded: string[];
  fromCache: boolean;
  elapsedMs: number;
}

export interface Holding {
  id: string;
  kind: AssetKind;
  assetId: string;
  symbol: string;
  name: string;
  iconUrl?: string;
  exchange?: string;
  sector?: string;
  industry?: string;
  quantity: number;
  category?: string | null;
  unitCost?: number | null;
  costCurrency?: string | null;
  manualUnitPrice?: number | null;
  manualPriceCurrency?: string | null;
  note?: string | null;
  addedUtc: string;
}

export interface Portfolio {
  baseCurrency: string;
  holdings: Holding[];
  updatedUtc: string;
}

export interface HoldingValuation {
  id: string;
  symbol: string;
  name: string;
  kind: AssetKind;
  iconUrl?: string;
  category: string;
  /** What detection alone would say, even when `category` was picked by hand. */
  detectedCategory: string;
  sector?: string;
  quantity: number;
  unitPriceBase?: number | null;
  valueBase?: number | null;
  costBase?: number | null;
  gainBase?: number | null;
  gainPct?: number | null;
  weightPct: number;
  manual: boolean;
  stale: boolean;
  asOfUtc?: string | null;
  error?: string | null;
}

export interface GroupTotal {
  name: string;
  valueBase: number;
  weightPct: number;
  count: number;
  costBase?: number | null;
  gainBase?: number | null;
  gainPct?: number | null;
}

export interface ValuationReport {
  baseCurrency: string;
  totalBase: number;
  totalCostBase?: number | null;
  totalGainBase?: number | null;
  totalGainPct?: number | null;
  byCategory: GroupTotal[];
  bySector: GroupTotal[];
  holdings: HoldingValuation[];
  problems: string[];
  asOfUtc: string;
  pricedCount: number;
  unpricedCount: number;
  staleCount: number;
}

/** A category as the user has it. `detect` is the asset-type key that fills it
 *  automatically, so it is null for anything you add yourself and for the ones
 *  no provider can detect (real estate, bonds, collectibles); `auto` is simply
 *  `detect != null`. Keys: cash, crypto, stocks, funds, commodities, indices,
 *  other. */
export interface CategoryMeta {
  name: string;
  auto: boolean;
  /** Omitted on the wire when null, hence optional. */
  detect?: string | null;
}

/** One row of the category editor. `renamedFrom` is what tells a rename apart
 *  from a delete plus an add, so holdings travel with the name. */
export interface CategoryEdit {
  name: string;
  renamedFrom?: string | null;
}

export interface Meta {
  categories: CategoryMeta[];
  /** The built-in list, used by "Restore defaults". */
  defaultCategories: CategoryMeta[];
  maxCategories: number;
  maxCategoryNameLength: number;
  currencies: { code: string; name: string }[];
  fxAvailable: boolean;
  fxAsOf?: string | null;
  fxStale: boolean;
  fxError?: string | null;
}

export interface PutCategoriesResult {
  categories: CategoryMeta[];
  /** Holdings carried across a rename. */
  renamed: number;
  /** Holdings whose category was deleted, now back to auto-detection. */
  released: number;
  portfolio: Portfolio;
}

export interface HostStats {
  host: string;
  requests: number;
  cacheHits: number;
  staleServes: number;
  errors: number;
  throttled: number;
  budgetBlocked: number;
  lastCallUtc?: string | null;
}

export interface Diagnostics {
  hosts: HostStats[];
  cache: { files: number; bytes: number };
  portfolioFile: string;
  time: string;
}

export interface AddHoldingRequest {
  kind: AssetKind;
  assetId?: string | null;
  symbol: string;
  name?: string | null;
  quantity: number;
  category?: string | null;
  iconUrl?: string | null;
  exchange?: string | null;
  sector?: string | null;
  industry?: string | null;
  unitCost?: number | null;
  costCurrency?: string | null;
  manualUnitPrice?: number | null;
  manualPriceCurrency?: string | null;
  note?: string | null;
}

export interface UpdateHoldingRequest {
  quantity?: number | null;
  category?: string | null;
  unitCost?: number | null;
  costCurrency?: string | null;
  manualUnitPrice?: number | null;
  manualPriceCurrency?: string | null;
  note?: string | null;
  symbol?: string | null;
  name?: string | null;
}
