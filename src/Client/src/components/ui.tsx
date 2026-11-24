import { useEffect, useState, type ReactNode } from 'react';
import { monogram } from '../format';
import type { AssetKind } from '../types';

// --------------------------------------------------------------------- icons

const paths = {
  search: (<><circle cx="11" cy="11" r="7" /><path d="m20.5 20.5-4-4" /></>),
  plus: (<path d="M12 5v14M5 12h14" />),
  wallet: (<><path d="M3 7.5A2.5 2.5 0 0 1 5.5 5H18a1 1 0 0 1 1 1v2" /><path d="M3 7.5V17a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-6a1 1 0 0 0-1-1H5.5A2.5 2.5 0 0 1 3 7.5Z" /><circle cx="16.5" cy="14" r="1.1" fill="currentColor" stroke="none" /></>),
  chart: (<><path d="M4 20V4" /><path d="M4 20h16" /><rect x="7.5" y="12" width="3" height="5" rx="1" /><rect x="13" y="8" width="3" height="9" rx="1" /></>),
  gear: (<><circle cx="12" cy="12" r="3" /><path d="M12 3.5v2M12 18.5v2M20.5 12h-2M5.5 12h-2M18 6l-1.4 1.4M7.4 16.6 6 18M18 18l-1.4-1.4M7.4 7.4 6 6" /></>),
  back: (<path d="M15 5l-7 7 7 7" />),
  check: (<path d="m5 12.5 4.5 4.5L19 7" />),
  refresh: (<><path d="M20 11a8 8 0 0 0-13.7-5.1L4 8" /><path d="M4 4v4h4" /><path d="M4 13a8 8 0 0 0 13.7 5.1L20 16" /><path d="M20 20v-4h-4" /></>),
  trash: (<><path d="M4 7h16" /><path d="M9 7V5.5A1.5 1.5 0 0 1 10.5 4h3A1.5 1.5 0 0 1 15 5.5V7" /><path d="M6.5 7 7.4 19a1.6 1.6 0 0 0 1.6 1.5h6a1.6 1.6 0 0 0 1.6-1.5L17.5 7" /><path d="M10.5 11v5.5M13.5 11v5.5" /></>),
  alert: (<><path d="M12 4.8 3.6 19h16.8L12 4.8Z" /><path d="M12 10v4" /><circle cx="12" cy="16.6" r="0.9" fill="currentColor" stroke="none" /></>),
  info: (<><circle cx="12" cy="12" r="8.5" /><path d="M12 11v5.5" /><circle cx="12" cy="8" r="0.9" fill="currentColor" stroke="none" /></>),
  x: (<path d="M6 6l12 12M18 6 6 18" />),
  down: (<><path d="M12 4v13" /><path d="m6.5 12 5.5 5.5L17.5 12" /></>),
  up: (<><path d="M12 20V7" /><path d="m6.5 12 5.5-5.5L17.5 12" /></>),
  sparkle: (<><path d="M12 3.5 13.7 9l5.5 1.7-5.5 1.7L12 18l-1.7-5.6L4.8 10.7 10.3 9 12 3.5Z" /><path d="M18.5 16.5 19.2 19l2.3.8-2.3.8-.7 2.4" /></>),
  download: (<><path d="M12 4v10" /><path d="m8 10.5 4 4 4-4" /><path d="M4.5 19h15" /></>),
  upload: (<><path d="M12 20V9" /><path d="m8 12.5 4-4 4 4" /><path d="M4.5 4.5h15" /></>),
  edit: (<><path d="M4.5 19.5h4L19 9a2.1 2.1 0 0 0-3-3L5.5 16.5l-1 3Z" /><path d="m14.5 7 2.5 2.5" /></>),
  arrow: (<path d="M5 12h13m-5-5 5 5-5 5" />),
  clock: (<><circle cx="12" cy="12" r="8.5" /><path d="M12 7.5V12l3 2" /></>),
  layers: (<><path d="m12 3.5 8.5 4.5L12 12.5 3.5 8 12 3.5Z" /><path d="m4 12.5 8 4.2 8-4.2" /><path d="m4 16.8 8 4.2 8-4.2" /></>),
} as const satisfies Record<string, ReactNode>;

export type IconName = keyof typeof paths;

export function Icon({ name, size = 18, strokeWidth = 1.7 }: { name: IconName; size?: number; strokeWidth?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={strokeWidth}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {paths[name]}
    </svg>
  );
}

// -------------------------------------------------------------------- pieces

export function Card({
  title,
  action,
  children,
  className = '',
}: {
  title?: string;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`card ${className}`}>
      {(title || action) && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: title ? 12 : 0 }}>
          {title && <h2 className="card__title" style={{ margin: 0 }}>{title}</h2>}
          <span className="grow" />
          {action}
        </div>
      )}
      {children}
    </section>
  );
}

type Tone = 'plain' | 'accent' | 'pos' | 'neg' | 'warn';

export function Badge({ children, tone = 'plain', title }: { children: ReactNode; tone?: Tone; title?: string }) {
  const cls = tone === 'plain' ? 'badge' : `badge badge--${tone}`;
  return <span className={cls} title={title}>{children}</span>;
}

export function Spinner({ large = false }: { large?: boolean }) {
  return <span className={large ? 'spinner spinner--lg' : 'spinner'} role="status" aria-label="Loading" />;
}

export function Empty({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="empty">
      <div className="empty__title">{title}</div>
      {children && <p>{children}</p>}
      {action}
    </div>
  );
}

/**
 * Asset logo. Coins have real artwork from CoinGecko; equities and cash have
 * no free logo source, so they fall back to a kind-tinted monogram — which
 * still tells you what you are looking at rather than showing a grey box.
 */
export function AssetIcon({
  symbol,
  name,
  iconUrl,
  kind,
  size = 'md',
}: {
  symbol: string;
  name?: string;
  iconUrl?: string | null;
  kind: AssetKind;
  size?: 'sm' | 'md' | 'lg';
}) {
  const [broken, setBroken] = useState(false);
  const cls = size === 'md' ? 'icon' : `icon icon--${size}`;
  const useImage = Boolean(iconUrl) && !broken;

  return (
    <span className={cls} data-kind={kind} aria-hidden="true">
      {useImage ? (
        <img
          src={iconUrl!}
          alt=""
          loading="lazy"
          decoding="async"
          referrerPolicy="no-referrer"
          onError={() => setBroken(true)}
        />
      ) : (
        monogram(symbol, name)
      )}
    </span>
  );
}

/** Bottom sheet on phones, and still usable as a modal on wide screens. */
export function Sheet({
  title,
  sub,
  onClose,
  children,
  labelledBy,
}: {
  title: string;
  sub?: string;
  onClose: () => void;
  children: ReactNode;
  labelledBy?: string;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prev;
    };
  }, [onClose]);

  return (
    <>
      <div className="scrim" onClick={onClose} aria-hidden="true" />
      <div className="sheet" role="dialog" aria-modal="true" aria-labelledby={labelledBy}>
        <div className="sheet__grip" aria-hidden="true" />
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 12 }}>
          <div className="grow">
            <h3 className="sheet__title" id={labelledBy}>{title}</h3>
            {sub && <p className="sheet__sub">{sub}</p>}
          </div>
          <button className="btn btn--ghost btn--icon" onClick={onClose} aria-label="Close">
            <Icon name="x" />
          </button>
        </div>
        {children}
      </div>
    </>
  );
}

export interface ToastState {
  id: number;
  text: string;
  tone: 'plain' | 'ok' | 'err';
}

export function Toast({ toast }: { toast: ToastState | null }) {
  if (!toast) return null;
  const cls = toast.tone === 'plain' ? 'toast' : `toast toast--${toast.tone}`;
  return (
    <div className={cls} role="status" aria-live="polite">
      {toast.text}
    </div>
  );
}
