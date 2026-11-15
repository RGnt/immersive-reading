import React, { useEffect, useMemo, useState } from 'react';
import { useLibraryStore, type BulkPatch } from '../../stores/useLibraryStore';
import { rpc } from '../../services/rpc';
import { PencilLine, X, Loader2, ArrowRight, AlertTriangle } from 'lucide-react';

// Library management: edit the metadata of every selected book at once (series, volume numbers, author, publisher, ...).
// A field you do not tick is left alone. A ticked field with an empty value clears it. The preview is computed by the same
// code that applies the edit, so what it shows is what gets written.

type NumberingChoice = 'keep' | 'title' | 'filename' | 'selection' | 'fromTitle' | 'remove';

interface PreviewRow {
  bookId: string;
  title: string;
  fileName: string;
  currentSeries: string;
  currentIndex: number | null;
  newSeries: string;
  newIndex: number | null;
}
interface Preview {
  bookCount: number;
  seriesChanges: number;
  indexChanges: number;
  withoutNumber: number;
  rows: PreviewRow[];
}
interface SeriesName { name: string; count: number }

const NUMBERING_LABEL: Record<NumberingChoice, string> = {
  keep: 'Leave the volume numbers as they are',
  title: 'Number 1, 2, 3 … in title order (Vol 2 before Vol 10)',
  filename: 'Number 1, 2, 3 … in file name order',
  selection: 'Number 1, 2, 3 … in the order the books were selected',
  fromTitle: 'Use the number written in each title (第3巻, 3, III, 上/中/下 …)',
  remove: 'Remove the volume numbers',
};

const FIELDS = [
  { key: 'authors', label: 'Author', placeholder: 'e.g. 川原 礫' },
  { key: 'publisher', label: 'Publisher', placeholder: 'e.g. KADOKAWA' },
  { key: 'imprint', label: 'Imprint', placeholder: 'e.g. 電撃文庫' },
  { key: 'language', label: 'Language code', placeholder: 'ja, en …' },
] as const;
type FieldKey = (typeof FIELDS)[number]['key'];

const fmt = (n: number | null) => (n === null || n === undefined ? '—' : String(n));

export const BulkEditModal: React.FC = () => {
  const { isBulkEditOpen, closeBulkEdit, selectedBookIds, tags, bulkUpdateBooks } = useLibraryStore();
  const count = selectedBookIds.length;

  const [seriesOn, setSeriesOn] = useState(false);
  const [series, setSeries] = useState('');
  const [numbering, setNumbering] = useState<NumberingChoice>('keep');
  const [start, setStart] = useState(1);
  const [step, setStep] = useState(1);
  const [fields, setFields] = useState<Record<FieldKey, { on: boolean; value: string }>>({
    authors: { on: false, value: '' },
    publisher: { on: false, value: '' },
    imprint: { on: false, value: '' },
    language: { on: false, value: '' },
  });
  const [tagChoice, setTagChoice] = useState<Record<string, 'add' | 'remove'>>({});
  const [seriesNames, setSeriesNames] = useState<SeriesName[]>([]);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // start fresh every time the dialog opens
  useEffect(() => {
    if (!isBulkEditOpen) return;
    setSeriesOn(false); setSeries(''); setNumbering('keep'); setStart(1); setStep(1);
    setFields({ authors: { on: false, value: '' }, publisher: { on: false, value: '' }, imprint: { on: false, value: '' }, language: { on: false, value: '' } });
    setTagChoice({}); setPreview(null); setError(null);
    rpc.call<SeriesName[]>('library.listSeriesNames').then((r) => setSeriesNames(r || [])).catch(() => setSeriesNames([]));
  }, [isBulkEditOpen]);

  useEffect(() => {
    if (!isBulkEditOpen) return undefined;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !busy) closeBulkEdit(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isBulkEditOpen, busy, closeBulkEdit]);

  const patch = useMemo<BulkPatch>(() => {
    const p: BulkPatch = {};
    if (seriesOn) p.seriesName = series.trim();
    if (numbering === 'remove') p.clearSeriesIndex = true;
    else if (numbering !== 'keep') {
      p.numberingMode = numbering;
      p.autoNumberStart = Number.isFinite(start) ? start : 1;
      p.autoNumberStep = Number.isFinite(step) && step !== 0 ? step : 1;
    }
    for (const f of FIELDS) if (fields[f.key].on) p[f.key] = fields[f.key].value.trim();
    const add = Object.entries(tagChoice).filter(([, v]) => v === 'add').map(([id]) => id);
    const remove = Object.entries(tagChoice).filter(([, v]) => v === 'remove').map(([id]) => id);
    if (add.length) p.addTagIds = add;
    if (remove.length) p.removeTagIds = remove;
    return p;
  }, [seriesOn, series, numbering, start, step, fields, tagChoice]);

  const touchesSeries = seriesOn || numbering !== 'keep';
  const hasChange = Object.keys(patch).length > 0;

  // preview of the series / volume part (debounced; the server computes it with the code that applies it)
  useEffect(() => {
    if (!isBulkEditOpen || !touchesSeries || count === 0) { setPreview(null); return undefined; }
    let cancelled = false;
    setPreviewing(true);
    const t = setTimeout(() => {
      rpc.call<Preview>('library.previewBulkUpdate', { bookIds: selectedBookIds, ...patch })
        .then((r) => { if (!cancelled) { setPreview(r); setError(null); } })
        .catch((e) => { if (!cancelled) setError(e?.message || String(e)); })
        .finally(() => { if (!cancelled) setPreviewing(false); });
    }, 250);
    return () => { cancelled = true; clearTimeout(t); };
  }, [isBulkEditOpen, touchesSeries, count, JSON.stringify(patch)]);

  if (!isBulkEditOpen) return null;

  const cycleTag = (tagId: string) => setTagChoice((c) => {
    const next = { ...c };
    if (!c[tagId]) next[tagId] = 'add';
    else if (c[tagId] === 'add') next[tagId] = 'remove';
    else delete next[tagId];
    return next;
  });

  const apply = async () => {
    setBusy(true);
    setError(null);
    try {
      await bulkUpdateBooks(patch);      // clears the selection and reloads the list
      closeBulkEdit();
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setBusy(false);
    }
  };

  const clearsSeries = seriesOn && series.trim() === '';
  const setField = (key: FieldKey, next: Partial<{ on: boolean; value: string }>) => setFields((f) => ({ ...f, [key]: { ...f[key], ...next } }));

  const sample = preview?.rows.slice(0, 12) ?? [];

  return (
    <div className="modal-backdrop fixed inset-0 bg-black/75 backdrop-blur-sm flex items-center justify-center z-50 p-4" onClick={() => !busy && closeBulkEdit()}>
      <div className="modal-surface bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-[var(--radius-modal)] shadow-2xl flex flex-col w-[760px] max-w-[96vw] max-h-[92vh] overflow-hidden" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-[var(--border-color)] bg-black/20">
          <div className="flex items-center space-x-2.5">
            <div className="p-1.5 rounded-lg bg-[var(--accent-color)]/20 text-[var(--accent-text)] border border-[var(--accent-color)]/30"><PencilLine className="w-5 h-5" /></div>
            <div>
              <h3 className="text-sm font-bold text-[var(--text-primary)]">Edit {count} book{count === 1 ? '' : 's'} at once</h3>
              <p className="text-[11px] text-[var(--text-muted)]">Tick what should change. Everything else stays as it is.</p>
            </div>
          </div>
          <button type="button" onClick={closeBulkEdit} disabled={busy} className="p-1.5 rounded-lg text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"><X className="w-4 h-4" /></button>
        </div>

        <div className="overflow-y-auto p-5 space-y-5 text-xs">
          {/* SERIES + VOLUMES */}
          <section className="space-y-2.5">
            <label className="flex items-center space-x-2 font-bold text-[var(--text-primary)]">
              <input type="checkbox" checked={seriesOn} onChange={(e) => { setSeriesOn(e.target.checked); if (e.target.checked && numbering === 'keep') setNumbering('title'); }} />
              <span>Series</span>
            </label>
            <input
              list="bulk-series-names"
              disabled={!seriesOn}
              value={series}
              onChange={(e) => setSeries(e.target.value)}
              placeholder="Series name (pick an existing one or type a new one)"
              className="w-full bg-[var(--bg-primary)] border border-[var(--border-color)] rounded-lg px-3 py-2 text-[var(--text-primary)] disabled:opacity-40"
            />
            <datalist id="bulk-series-names">
              {seriesNames.map((s) => <option key={s.name} value={s.name}>{s.count} book{s.count === 1 ? '' : 's'}</option>)}
            </datalist>
            {clearsSeries && <div className="flex items-center space-x-1.5 text-amber-300"><AlertTriangle className="w-3.5 h-3.5" /><span>The series name will be cleared on all {count} books.</span></div>}

            <div className="pl-1 space-y-1.5">
              <div className="text-[var(--text-muted)] font-semibold">Volume numbers</div>
              <select value={numbering} onChange={(e) => setNumbering(e.target.value as NumberingChoice)} className="w-full bg-[var(--bg-primary)] border border-[var(--border-color)] rounded-lg px-3 py-2 text-[var(--text-primary)]">
                {(Object.keys(NUMBERING_LABEL) as NumberingChoice[]).map((k) => <option key={k} value={k}>{NUMBERING_LABEL[k]}</option>)}
              </select>
              {(numbering === 'title' || numbering === 'filename' || numbering === 'selection') && (
                <div className="flex items-center space-x-3 text-[var(--text-muted)]">
                  <label className="flex items-center space-x-1.5"><span>start at</span>
                    <input type="number" step="any" value={start} onChange={(e) => setStart(parseFloat(e.target.value))} className="w-20 bg-[var(--bg-primary)] border border-[var(--border-color)] rounded px-2 py-1 text-[var(--text-primary)]" /></label>
                  <label className="flex items-center space-x-1.5"><span>step</span>
                    <input type="number" step="any" value={step} onChange={(e) => setStep(parseFloat(e.target.value))} className="w-20 bg-[var(--bg-primary)] border border-[var(--border-color)] rounded px-2 py-1 text-[var(--text-primary)]" /></label>
                </div>
              )}
              {numbering === 'fromTitle' && <div className="text-[var(--text-muted)]">Books whose title has no number keep the number they have now.</div>}
            </div>

            {touchesSeries && (
              <div className="rounded-lg border border-[var(--border-color)] bg-black/20 p-3 space-y-2">
                <div className="flex items-center justify-between">
                  <span className="font-semibold text-[var(--text-primary)]">Preview</span>
                  {previewing && <Loader2 className="w-3.5 h-3.5 animate-spin text-[var(--text-muted)]" />}
                </div>
                {preview && (
                  <>
                    <div className="text-[var(--text-muted)]">
                      {preview.seriesChanges} series name{preview.seriesChanges === 1 ? '' : 's'} and {preview.indexChanges} volume number{preview.indexChanges === 1 ? '' : 's'} will change
                      {preview.withoutNumber > 0 ? `; ${preview.withoutNumber} title${preview.withoutNumber === 1 ? ' has' : 's have'} no number to use` : ''}.
                    </div>
                    <table className="w-full text-[11px]">
                      <thead><tr className="text-left text-[var(--text-muted)]"><th className="py-1 pr-2">Book</th><th className="pr-2">Now</th><th /><th>After</th></tr></thead>
                      <tbody>
                        {sample.map((r) => (
                          <tr key={r.bookId} className="border-t border-[var(--border-color)]">
                            <td className="py-1 pr-2 max-w-[260px] truncate" title={r.fileName}>{r.title}</td>
                            <td className="pr-2 text-[var(--text-muted)]">{r.currentSeries || '—'} · {fmt(r.currentIndex)}</td>
                            <td><ArrowRight className="w-3 h-3 text-[var(--text-muted)]" /></td>
                            <td className="text-[var(--text-primary)]">{r.newSeries || '—'} · <b>{fmt(r.newIndex)}</b></td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                    {preview.bookCount > sample.length && <div className="text-[var(--text-muted)]">… and {preview.bookCount - sample.length} more.</div>}
                  </>
                )}
              </div>
            )}
          </section>

          {/* OTHER FIELDS */}
          <section className="grid grid-cols-2 gap-3">
            {FIELDS.map((f) => (
              <div key={f.key} className="space-y-1">
                <label className="flex items-center space-x-2 font-bold text-[var(--text-primary)]">
                  <input type="checkbox" checked={fields[f.key].on} onChange={(e) => setField(f.key, { on: e.target.checked })} />
                  <span>{f.label}</span>
                </label>
                <input
                  disabled={!fields[f.key].on}
                  value={fields[f.key].value}
                  onChange={(e) => setField(f.key, { value: e.target.value })}
                  placeholder={f.placeholder}
                  className="w-full bg-[var(--bg-primary)] border border-[var(--border-color)] rounded-lg px-3 py-2 text-[var(--text-primary)] disabled:opacity-40"
                />
                {fields[f.key].on && fields[f.key].value.trim() === '' && <div className="text-amber-300">Will be cleared.</div>}
              </div>
            ))}
          </section>

          {/* TAGS */}
          {tags.length > 0 && (
            <section className="space-y-2">
              <div className="font-bold text-[var(--text-primary)]">Tags <span className="font-normal text-[var(--text-muted)]">(click: add → remove → leave)</span></div>
              <div className="flex flex-wrap gap-1.5">
                {tags.map((t) => {
                  const choice = tagChoice[t.tagId];
                  return (
                    <button
                      key={t.tagId}
                      type="button"
                      onClick={() => cycleTag(t.tagId)}
                      className={`px-2.5 py-1 rounded-full border text-[11px] font-medium transition-colors ${choice === 'add' ? 'border-emerald-500 bg-emerald-950/50 text-emerald-300' : choice === 'remove' ? 'border-red-500 bg-red-950/50 text-red-300 line-through' : 'border-[var(--border-color)] text-[var(--text-muted)] hover:border-[var(--accent-color)]'}`}
                    >
                      {choice === 'add' ? '+ ' : choice === 'remove' ? '− ' : ''}{t.name}
                    </button>
                  );
                })}
              </div>
            </section>
          )}

          {error && <div className="text-red-300">{error}</div>}
        </div>

        <div className="flex items-center justify-between px-5 py-3 border-t border-[var(--border-color)] bg-black/20">
          <span className="text-[11px] text-[var(--text-muted)]">{hasChange ? `Applies to ${count} book${count === 1 ? '' : 's'}.` : 'Nothing is ticked yet.'}</span>
          <div className="flex items-center space-x-2">
            <button type="button" onClick={closeBulkEdit} disabled={busy} className="px-3.5 py-1.5 rounded-lg border border-[var(--border-color)] text-[var(--text-primary)] hover:border-[var(--accent-color)] text-xs font-medium">Cancel</button>
            <button type="button" onClick={apply} disabled={busy || !hasChange || count === 0} className="px-4 py-1.5 rounded-lg bg-[var(--accent-color)] text-[var(--accent-contrast)] text-xs font-bold disabled:opacity-40 flex items-center space-x-1.5">
              {busy && <Loader2 className="w-3.5 h-3.5 animate-spin" />}<span>Apply to {count}</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};
