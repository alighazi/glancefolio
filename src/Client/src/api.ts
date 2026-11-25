import type {
  AddHoldingRequest,
  CategoryEdit,
  Diagnostics,
  Holding,
  Meta,
  Portfolio,
  PutCategoriesResult,
  SearchResponse,
  UpdateHoldingRequest,
  User,
  ValuationReport,
} from './types';

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = 'ApiError';
  }
}

const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(path, init);
  } catch {
    // A network failure and a server failure need different messages: one means
    // "the app is not running", the other means "this request was refused".
    throw new ApiError('Cannot reach the server — is it running?', 0);
  }

  const text = await res.text();
  let body: unknown = null;
  if (text.length > 0) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }

  if (!res.ok) {
    const detail =
      body && typeof body === 'object' && 'error' in body
        ? String((body as { error: unknown }).error)
        : typeof body === 'string' && body.length > 0
          ? body
          : `Request failed (HTTP ${res.status})`;
    throw new ApiError(detail, res.status);
  }

  return body as T;
}

export interface HoldingMutationResult {
  holding: Holding;
  portfolio: Portfolio;
}

export const api = {
  me(): Promise<User> {
    return req<User>('/api/auth/me', { credentials: 'same-origin' });
  },

  login(email: string, password: string): Promise<User> {
    return req<User>('/api/auth/login', { method: 'POST', headers: JSON_HEADERS, credentials: 'same-origin', body: JSON.stringify({ email, password }) });
  },

  register(email: string, password: string): Promise<User> {
    return req<User>('/api/auth/register', { method: 'POST', headers: JSON_HEADERS, credentials: 'same-origin', body: JSON.stringify({ email, password }) });
  },

  logout(): Promise<void> {
    return req<void>('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
  },

  search(q: string, take = 12, signal?: AbortSignal): Promise<SearchResponse> {
    const url = `/api/search?q=${encodeURIComponent(q)}&take=${take}`;
    return req<SearchResponse>(url, { signal });
  },

  meta(): Promise<Meta> {
    return req<Meta>('/api/meta');
  },

  portfolio(): Promise<Portfolio> {
    return req<Portfolio>('/api/portfolio');
  },

  report(): Promise<ValuationReport> {
    return req<ValuationReport>('/api/report');
  },

  /** Forces a re-fetch of prices, then returns the rebuilt report. */
  refresh(): Promise<{ clearedCacheEntries: number; report: ValuationReport }> {
    return req('/api/refresh', { method: 'POST' });
  },

  addHolding(body: AddHoldingRequest): Promise<HoldingMutationResult> {
    return req('/api/holdings', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(body) });
  },

  updateHolding(id: string, patch: UpdateHoldingRequest): Promise<HoldingMutationResult> {
    return req(`/api/holdings/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: JSON_HEADERS,
      body: JSON.stringify(patch),
    });
  },

  deleteHolding(id: string): Promise<{ removedId: string; portfolio: Portfolio }> {
    return req(`/api/holdings/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },

  /** Replaces the whole category list: order, renames and deletions at once. */
  putCategories(categories: CategoryEdit[]): Promise<PutCategoriesResult> {
    return req('/api/categories', {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({ categories }),
    });
  },

  setBaseCurrency(baseCurrency: string): Promise<Portfolio> {
    return req('/api/portfolio/base-currency', {
      method: 'PATCH',
      headers: JSON_HEADERS,
      body: JSON.stringify({ baseCurrency }),
    });
  },

  replacePortfolio(portfolio: Portfolio): Promise<Portfolio> {
    return req('/api/portfolio', {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify(portfolio),
    });
  },

  diagnostics(): Promise<Diagnostics> {
    return req<Diagnostics>('/api/diagnostics');
  },
};

/** The export endpoint streams a file, so it is a link rather than a fetch. */
export const exportUrl = '/api/portfolio/export';
