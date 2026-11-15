import { useEffect, useState } from 'react';
import { AlertCircle, Check, Save } from 'lucide-react';
import { CodeEditor } from '../editor/CodeEditor';
import { APP_CSS_VARIABLES } from '../editor/loadMonaco';
import { useAppearanceStore } from '../../stores/useAppearanceStore';

/** Shown in the editor while the stylesheet is empty. Everything is commented out: it only teaches what is possible. */
export const STARTER_CSS = `/* Your own stylesheet. It is applied on top of the app's styles, in the app window and inside the reader.
   Try a rule, the window changes while you type; Save (Ctrl+S) keeps it.

   If something goes wrong and you cannot reach Settings, press Ctrl+Alt+Shift+C to switch this stylesheet off. */

/* Colours and fonts: change the theme variables for everything at once */
/* :root { --accent-color: #f472b6; --reader-font-size: 26px; --reader-column-width: 760px; } */

/* Aim at one window: html.ir-app is the app, html.ir-reader is the book page */
/* html.ir-reader body { padding-inline: 3rem; } */
/* (inside the reader the app sets font, size and colours with !important: add !important to override them) */

/* Corners: dialogs, cards and controls each have a token (0px = square) */
/* :root { --radius-modal: 8px; --radius-card: 6px; --radius-control: 4px; } */

/* Move or hide a region (the data-ir attributes stay stable between versions) */
/* [data-ir="sidebar"] { width: 200px; } */
/* [data-ir="header"] { display: none; } */
`;

const HOOKS: [string, string][] = [
  ['html.ir-app', 'the app window'],
  ['html.ir-reader', 'inside the book page (the chapter frame)'],
  ['[data-ir="app"]', 'the whole app shell'],
  ['[data-ir="header"]', 'the top bar'],
  ['[data-ir="sidebar"]', 'the library sidebar'],
  ['[data-ir="library"]', 'the book grid'],
  ['[data-ir="book-card"]', 'one book in the gallery'],
  ['[data-ir="reader"]', 'the reader view'],
  ['[data-ir="chapter-frame"]', 'the frame that holds the book page'],
];

/** Settings → Custom CSS: one stylesheet the user owns, edited in Monaco with a live preview. */
export function CustomCssTab() {
  const { css, enabled, maxBytes, loaded, save, setEnabled, setPreview } = useAppearanceStore();
  const [draft, setDraft] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => { if (!loaded) void useAppearanceStore.getState().load(); }, [loaded]);

  // leaving the tab without saving puts the saved stylesheet back
  useEffect(() => () => useAppearanceStore.getState().setPreview(null), []);

  const text = draft ?? (css.trim() ? css : STARTER_CSS);
  const dirty = draft !== null && draft !== css;
  const tooBig = new TextEncoder().encode(text).length > maxBytes;

  const edit = (value: string) => {
    setSaved(false);
    setDraft(value === css ? null : value);
    setPreview(value === css ? null : value);
  };

  const doSave = async () => {
    if (saving || tooBig) return;
    setSaving(true);
    setError(null);
    try {
      await save(draft ?? css, true);
      setDraft(null);
      setSaved(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-3 text-xs" data-testid="custom-css-tab">
      <div className="flex items-center justify-between gap-4">
        <label className="flex items-center gap-2 text-[var(--text-primary)]">
          <input type="checkbox" checked={enabled} onChange={(e) => void setEnabled(e.target.checked)} />
          <span>Use my stylesheet</span>
        </label>
        <span className="text-[var(--text-muted)]">Switch off from anywhere: Ctrl+Alt+Shift+C</span>
      </div>

      <CodeEditor documentKey="user.css" value={text} language="css" height={380} onChange={edit} onSave={() => void doSave()} />

      <div className="min-h-[1.25rem]">
        {error && <span className="text-red-400 flex items-center gap-1.5"><AlertCircle className="w-3.5 h-3.5" /> {error}</span>}
        {tooBig && <span className="text-red-400">The stylesheet is larger than {Math.round(maxBytes / 1024)} KB.</span>}
        {dirty && !error && <span className="text-[var(--text-muted)]">Previewing. Not saved yet.</span>}
        {saved && !dirty && <span className="text-emerald-400 flex items-center gap-1.5"><Check className="w-3.5 h-3.5" /> Saved.</span>}
      </div>

      <div className="flex items-center gap-2">
        <button
          onClick={() => void doSave()}
          disabled={saving || !dirty || tooBig}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold disabled:opacity-40"
        >
          <Save className="w-3.5 h-3.5" /> Save (Ctrl+S)
        </button>
        <button
          onClick={() => { setDraft(null); setPreview(null); }}
          disabled={!dirty}
          className="px-3 py-1.5 rounded border border-[var(--border-color)] text-[var(--text-muted)] hover:text-[var(--text-primary)] disabled:opacity-40"
        >
          Discard changes
        </button>
        <button
          onClick={() => { if (confirm('Delete your stylesheet?')) void save('', enabled).then(() => setDraft(null)); }}
          disabled={!css.trim()}
          className="px-3 py-1.5 rounded border border-[var(--border-color)] text-[var(--text-muted)] hover:text-red-400 disabled:opacity-40"
        >
          Delete stylesheet
        </button>
      </div>

      <div className="grid grid-cols-2 gap-4 max-w-3xl text-[var(--text-muted)]">
        <div>
          <div className="font-semibold text-[var(--text-primary)] mb-1">Places you can aim at</div>
          <ul className="space-y-0.5">
            {HOOKS.map(([selector, what]) => <li key={selector}><code className="text-[var(--accent-text)]">{selector}</code> {what}</li>)}
          </ul>
        </div>
        <div>
          <div className="font-semibold text-[var(--text-primary)] mb-1">Theme variables (suggested while you type)</div>
          <div className="font-mono leading-relaxed">{APP_CSS_VARIABLES.slice(0, 12).join('  ')} …</div>
          <p className="mt-2">Class names inside the app can change between versions; the <code>data-ir</code> attributes and the variables are kept stable.</p>
        </div>
      </div>
    </div>
  );
}
