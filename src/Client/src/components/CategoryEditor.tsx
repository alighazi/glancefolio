import { useEffect, useMemo, useRef, useState } from 'react';
import { Icon, Spinner } from './ui';
import { categoryColor } from '../palette';
import type { CategoryEdit, CategoryMeta } from '../types';

/**
 * One row of the editor.
 *
 * `from` is the name this category has on the server right now, or null for a
 * row that does not exist yet. It is what turns a rename into a rename: the
 * server moves every holding filed under `from` across to `name`. Without it,
 * editing a name would look like a delete plus an add and the holdings would
 * silently fall back to auto-detection.
 */
interface Row {
  key: number;
  name: string;
  from: string | null;
  detect: string | null;
}

const RESERVED = 'Uncategorised';

export function CategoryEditor({
  categories,
  defaults,
  maxCategories,
  maxNameLength,
  onSave,
}: {
  categories: CategoryMeta[];
  defaults: CategoryMeta[];
  maxCategories: number;
  maxNameLength: number;
  onSave: (edits: CategoryEdit[]) => Promise<boolean>;
}) {
  const saved = useMemo(
    () => categories.map((c, i) => ({ key: i, name: c.name, from: c.name, detect: c.detect ?? null })),
    [categories],
  );

  const [rows, setRows] = useState<Row[]>(saved);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const nextKey = useRef(categories.length);
  const listRef = useRef<HTMLDivElement>(null);

  // A save anywhere else in the app (an import, a restore) replaces the list, so
  // the editor follows it rather than showing a stale draft.
  useEffect(() => {
    setRows(saved);
    setError(null);
    nextKey.current = saved.length;
  }, [saved]);

  const patch = (key: number, next: Partial<Row>) =>
    setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...next } : r)));

  const move = (index: number, delta: number) =>
    setRows((rs) => {
      const to = index + delta;
      if (to < 0 || to >= rs.length) return rs;
      const next = [...rs];
      [next[index], next[to]] = [next[to], next[index]];
      return next;
    });

  const add = () => {
    if (rows.length >= maxCategories) {
      setError(`The list holds ${maxCategories} categories at most.`);
      return;
    }
    const key = nextKey.current++;
    setRows((rs) => [...rs, { key, name: '', from: null, detect: null }]);
    setError(null);
    // Focus the fresh field once it has rendered.
    requestAnimationFrame(() => {
      const inputs = listRef.current?.querySelectorAll<HTMLInputElement>('input.catrow__name');
      inputs?.[inputs.length - 1]?.focus();
    });
  };

  const restoreDefaults = () => {
    // Anything already in the list keeps its server-side name, so a category the
    // user renamed travels back instead of being dropped and re-detected.
    setRows(
      defaults.map((d, i) => {
        const lower = d.name.toLowerCase();
        const existing = rows.find((r) => r.from?.toLowerCase() === lower) ?? rows.find((r) => r.name.trim().toLowerCase() === lower);
        return { key: i, name: d.name, from: existing?.from ?? null, detect: d.detect ?? null };
      }),
    );
    nextKey.current = defaults.length;
    setError(null);
  };

  const trimmed = rows.map((r) => r.name.trim());

  const problem = useMemo(() => {
    if (rows.length === 0) return 'Keep at least one category.';
    if (rows.length > maxCategories) return `The list holds ${maxCategories} categories at most.`;
    for (const name of trimmed) {
      if (!name) return 'A category needs a name.';
      if (name.length > maxNameLength) return `“${name.slice(0, 24)}…” is longer than ${maxNameLength} characters.`;
      if (name.toLowerCase() === RESERVED.toLowerCase())
        return `“${RESERVED}” is reserved — it is where holdings go when nothing matches.`;
    }
    const dupe = trimmed.find((n, i) => trimmed.findIndex((m) => m.toLowerCase() === n.toLowerCase()) !== i);
    if (dupe) return `“${dupe}” appears twice.`;
    return null;
  }, [rows.length, trimmed, maxCategories, maxNameLength]);

  const changed =
    rows.length !== saved.length ||
    rows.some((r, i) => r.name.trim() !== saved[i].name || r.from !== saved[i].from);

  const save = async () => {
    if (problem || !changed) return;
    setBusy(true);
    setError(null);
    const ok = await onSave(rows.map((r) => ({ name: r.name.trim(), renamedFrom: r.from })));
    setBusy(false);
    if (!ok) setError('The server rejected that list — nothing was changed.');
  };

  const isDefaultList =
    saved.length === defaults.length && saved.every((r, i) => r.name === defaults[i].name);

  return (
    <div>
      <p className="small muted" style={{ marginTop: 0, lineHeight: 1.6 }}>
        Categories group the report. Most are filled in automatically from the
        asset type the provider reports; rename one and everything filed under it
        follows, and delete one to send those holdings back to automatic
        detection.
      </p>

      <div className="catlist" ref={listRef}>
        {rows.map((r, i) => {
          const name = r.name.trim();
          const gone = r.from !== null && name && name.toLowerCase() !== r.from.toLowerCase();
          const fresh = r.from === null;
          return (
            <div key={r.key} className={`catrow${gone ? ' catrow--gone' : ''}${fresh ? ' catrow--new' : ''}`}>
              <span
                className="catrow__swatch"
                style={{ background: name ? categoryColor(name) : 'var(--surface-3)' }}
                aria-hidden="true"
              />
              <input
                className="catrow__name"
                value={r.name}
                maxLength={maxNameLength + 8}
                placeholder="New category"
                aria-label={r.from ? `Rename ${r.from}` : 'New category name'}
                onChange={(e) => patch(r.key, { name: e.target.value })}
                onKeyDown={(e) => { if (e.key === 'Enter') void save(); }}
                autoComplete="off"
              />
              {r.detect ? (
                <span className="catrow__auto" title={`Filled in automatically from ${r.detect}`}>
                  auto · {r.detect}
                </span>
              ) : (
                <span className="catrow__auto" style={{ color: 'var(--text-3)', background: 'var(--surface-3)' }} title="Nothing is ever filed here automatically">
                  hand-picked
                </span>
              )}
              <button className="iconbtn" aria-label={`Move ${name || 'this'} up`} disabled={i === 0 || busy} onClick={() => move(i, -1)}>
                <Icon name="up" size={15} />
              </button>
              <button className="iconbtn" aria-label={`Move ${name || 'this'} down`} disabled={i === rows.length - 1 || busy} onClick={() => move(i, 1)}>
                <Icon name="down" size={15} />
              </button>
              <button
                className="iconbtn iconbtn--danger"
                aria-label={`Delete ${name || 'this category'}`}
                disabled={rows.length <= 1 || busy}
                onClick={() => setRows((rs) => rs.filter((x) => x.key !== r.key))}
              >
                <Icon name="trash" size={15} />
              </button>
            </div>
          );
        })}
      </div>

      {(problem || error) && (
        <div className="notice notice--err" style={{ marginTop: 12 }}>
          <Icon name="alert" size={16} />
          <div>{error ?? problem}</div>
        </div>
      )}

      <div className="row" style={{ marginTop: 14, gap: 8, flexWrap: 'wrap' }}>
        <button className="btn btn--primary" onClick={() => void save()} disabled={busy || Boolean(problem) || !changed}>
          {busy ? <Spinner /> : <Icon name="check" size={16} />}
          {changed ? 'Save categories' : 'Saved'}
        </button>
        <button className="btn btn--ghost" onClick={add} disabled={busy || rows.length >= maxCategories}>
          <Icon name="plus" size={16} /> Add
        </button>
        <span className="grow" />
        {!isDefaultList && (
          <button className="btn btn--ghost btn--sm" onClick={restoreDefaults} disabled={busy}>
            <Icon name="refresh" size={14} /> Restore defaults
          </button>
        )}
      </div>
      <p className="field__hint" style={{ marginTop: 10, marginBottom: 0 }}>
        Order sets the order of the report. Up to {maxCategories} categories,
        {maxNameLength} characters each.
      </p>
    </div>
  );
}
