import type { ReactNode } from 'react';
import { Icon } from './ui';
import type { CategoryMeta } from '../types';

/**
 * The category chooser, shared by the add flow and the edit sheet.
 *
 * "Auto" is a real option, not the absence of one: it shows the name the app
 * detected so you can see what will happen if you do not choose, and a category
 * you added yourself is marked with a dot because nothing will ever be filed
 * there behind your back. `autoValue` is what the server's detection says; when
 * it is not known yet the button just reads "Auto".
 */
export function CategoryPicker({
  categories,
  value,
  autoValue,
  onChange,
  hint,
}: {
  categories: CategoryMeta[];
  value: string | null;
  autoValue?: string;
  onChange: (v: string | null) => void;
  hint?: ReactNode;
}) {
  return (
    <div>
      <div className="card__title" style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 10 }}>
        Category
        <span className="grow" />
        {value && autoValue && (
          <button
            className="btn btn--ghost btn--sm"
            onClick={() => onChange(null)}
            style={{ minHeight: 28, padding: '3px 9px' }}
          >
            <Icon name="sparkle" size={13} /> Use {autoValue}
          </button>
        )}
      </div>
      <div className="chips">
        <button className="chip" aria-pressed={value === null} onClick={() => onChange(null)}>
          <Icon name="sparkle" size={13} /> Auto{autoValue ? ` · ${autoValue}` : ''}
        </button>
        {categories.map((c) => (
          <button key={c.name} className="chip" aria-pressed={value === c.name} onClick={() => onChange(c.name)}>
            {c.name}
            {!c.auto && (
              <span className="chip__dot" title="Only ever chosen by hand" aria-label="hand-picked only" />
            )}
          </button>
        ))}
      </div>
      <p className="field__hint" style={{ marginTop: 10, marginBottom: 0 }}>
        {hint ?? (
          <>
            Detected from the asset type the provider reports. Tokenised gold lands in
            Commodities and stablecoins in Cash. A dot marks a category the app never
            fills in for you — edit the list in Settings.
          </>
        )}
      </p>
    </div>
  );
}
