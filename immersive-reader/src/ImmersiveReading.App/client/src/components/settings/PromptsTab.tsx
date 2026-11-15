import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertCircle, Check, Loader2, RotateCcw, Save } from 'lucide-react';
import { rpc } from '../../services/rpc';
import { CodeEditor, type EditorMarker } from '../editor/CodeEditor';
import { PROMPT_LANGUAGE, promptCompletionState } from '../editor/loadMonaco';

export interface PromptVariable {
  name: string;
  required: boolean;
  untrusted: boolean;
  type: string;
  default: string | null;
  enum: string[];
}

export interface PromptDto {
  name: string;
  owner: string;
  version: number;
  purpose: string;
  response: string;
  variables: PromptVariable[];
  builtInText: string;
  /** The text in force, or the user's saved file when it is not valid. */
  text: string;
  usingOverride: boolean;
  overrideError: string | null;
}

interface CheckResult {
  ok: boolean;
  error: string | null;
  line: number;
  warnings: string[];
}

const OWNER_LABELS: Record<string, string> = {
  characterKnowledge: 'Character Knowledge (reads the book and builds the graph)',
};

/**
 * Settings → Prompts. Every prompt the program sends to a model, in an editor. An edited copy is checked by the same rules as the
 * built-in (it must keep the variables the program fills in), saved as a separate file, and the built-in comes back with one click.
 */
export function PromptsTab() {
  const [prompts, setPrompts] = useState<PromptDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [check, setCheck] = useState<CheckResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const checkTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const load = useCallback(async () => {
    try {
      const result = await rpc.call<{ prompts: PromptDto[] }>('prompts.list', {});
      setPrompts(result.prompts);
      setSelected((current) => current && result.prompts.some((p) => p.name === current) ? current : result.prompts[0]?.name ?? null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const prompt = useMemo(() => prompts?.find((p) => p.name === selected) ?? null, [prompts, selected]);
  const draft = prompt ? drafts[prompt.name] ?? prompt.text : '';
  const dirty = !!prompt && draft !== prompt.text;

  useEffect(() => {
    promptCompletionState.variables = prompt?.variables ?? [];
  }, [prompt]);

  // validate while typing (the server applies the real rules, this only asks)
  useEffect(() => {
    clearTimeout(checkTimer.current);
    if (!prompt) { setCheck(null); return; }
    const name = prompt.name;
    checkTimer.current = setTimeout(async () => {
      try {
        setCheck(await rpc.call<CheckResult>('prompts.check', { name, text: draft }));
      } catch (e) {
        setCheck({ ok: false, error: e instanceof Error ? e.message : String(e), line: 0, warnings: [] });
      }
    }, 350);
    return () => clearTimeout(checkTimer.current);
  }, [prompt, draft]);

  const markers: EditorMarker[] = useMemo(() => {
    const list: EditorMarker[] = [];
    if (check && !check.ok && check.error) list.push({ message: check.error, line: check.line, severity: 'error' });
    return list;
  }, [check]);

  const replace = (updated: PromptDto) => setPrompts((all) => all?.map((p) => (p.name === updated.name ? updated : p)) ?? null);

  const save = async () => {
    if (!prompt || busy) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await rpc.call<{ ok: boolean; error: string | null; line: number; prompt: PromptDto | null }>('prompts.save', { name: prompt.name, text: draft });
      if (result.ok && result.prompt) {
        replace(result.prompt);
        setDrafts(({ [prompt.name]: _gone, ...rest }) => rest);
        setMessage('Saved. The next request uses this text.');
      } else {
        setCheck({ ok: false, error: result.error, line: result.line, warnings: [] });
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const resetToBuiltIn = async () => {
    if (!prompt || busy) return;
    if (prompt.usingOverride || prompt.overrideError) {
      if (!confirm(`Go back to the built-in text of '${prompt.name}'? Your edited copy is deleted.`)) return;
    }
    setBusy(true);
    setMessage(null);
    try {
      const result = await rpc.call<{ prompt: PromptDto }>('prompts.reset', { name: prompt.name });
      replace(result.prompt);
      setDrafts(({ [prompt.name]: _gone, ...rest }) => rest);
      setMessage('The built-in text is in use again.');
    } finally {
      setBusy(false);
    }
  };

  if (error) return <div className="text-xs text-red-400 flex items-center gap-1.5"><AlertCircle className="w-3.5 h-3.5" /> {error}</div>;
  if (!prompts) return <div className="text-xs text-[var(--text-muted)] flex items-center gap-1.5"><Loader2 className="w-3.5 h-3.5 animate-spin" /> Loading prompts…</div>;
  if (prompts.length === 0) {
    return (
      <div className="text-xs text-[var(--text-muted)] max-w-xl space-y-2">
        <p>No prompts are registered yet. A feature adds its prompts when it is enabled.</p>
        <p>The Character Knowledge prompts appear once its <b>knowledge-graph</b> folder is found (Plugins → Character Knowledge).</p>
      </div>
    );
  }

  const owners = [...new Set(prompts.map((p) => p.owner))];
  const edited = !!prompt && (prompt.usingOverride || !!prompt.overrideError);

  return (
    <div className="flex gap-4 text-xs" data-testid="prompts-tab">
      <div className="w-60 shrink-0 space-y-3">
        {owners.map((owner) => (
          <div key={owner}>
            <div className="text-[10px] uppercase tracking-wide text-[var(--text-muted)] mb-1">{OWNER_LABELS[owner] ?? owner}</div>
            <div className="space-y-0.5">
              {prompts.filter((p) => p.owner === owner).map((p) => (
                <button
                  key={p.name}
                  data-testid={`prompt-item-${p.name}`}
                  onClick={() => { setSelected(p.name); setMessage(null); }}
                  className={`w-full text-left px-2 py-1.5 rounded flex items-center justify-between gap-2 ${
                    p.name === selected ? 'bg-[var(--accent-color)]/15 text-[var(--accent-text)]' : 'hover:bg-[var(--hover-bg)] text-[var(--text-primary)]'
                  }`}
                >
                  <span className="truncate">{p.name}</span>
                  {p.overrideError
                    ? <span className="text-[10px] text-amber-400 shrink-0">not used</span>
                    : (drafts[p.name] !== undefined && drafts[p.name] !== p.text) ? <span className="text-[10px] text-[var(--text-muted)] shrink-0">unsaved</span>
                    : p.usingOverride ? <span className="text-[10px] text-[var(--accent-text)] shrink-0">edited</span> : null}
                </button>
              ))}
            </div>
          </div>
        ))}
      </div>

      {prompt && (
        <div className="flex-1 min-w-0 space-y-3">
          <div>
            <div className="font-semibold text-sm text-[var(--text-primary)]">{prompt.name} <span className="font-normal text-[var(--text-muted)]">version {prompt.version}</span></div>
            {prompt.purpose && <div className="text-[var(--text-muted)] mt-0.5">{prompt.purpose}</div>}
          </div>

          <div className="flex flex-wrap gap-1.5" data-testid="prompt-variables">
            {prompt.variables.map((v) => (
              <span
                key={v.name}
                title={`${v.type}${v.required ? ', required' : ''}${v.default !== null ? `, default "${v.default}"` : ''}${v.enum.length ? `, one of ${v.enum.join('/')}` : ''}${v.untrusted ? '. Text from a book or from you: it is fenced, so it cannot give the model instructions.' : ''}`}
                className={`px-1.5 py-0.5 rounded border font-mono ${v.untrusted ? 'border-amber-500/50 text-amber-300' : 'border-[var(--border-color)] text-[var(--text-muted)]'}`}
              >
                {`{{${v.type === 'list' ? '#each ' : ''}${v.name}}}`}
              </span>
            ))}
          </div>

          {prompt.overrideError && (
            <div className="p-2 rounded border border-amber-500/40 bg-amber-500/10 text-amber-300 flex gap-1.5">
              <AlertCircle className="w-3.5 h-3.5 shrink-0 mt-0.5" />
              <span>Your saved copy is <b>not in use</b> (the built-in is): {prompt.overrideError}</span>
            </div>
          )}

          <CodeEditor
            documentKey={`prompt:${prompt.name}`}
            value={draft}
            language={PROMPT_LANGUAGE}
            markers={markers}
            height={380}
            onChange={(text) => setDrafts((d) => (d[prompt.name] === text || (d[prompt.name] === undefined && text === prompt.text) ? d : { ...d, [prompt.name]: text }))}
            onSave={() => void save()}
          />

          <div className="min-h-[1.25rem]" data-testid="prompt-status">
            {check && !check.ok && <span className="text-red-400 flex items-center gap-1.5"><AlertCircle className="w-3.5 h-3.5" /> {check.line > 0 ? `Line ${check.line}: ` : ''}{check.error}</span>}
            {check?.ok && check.warnings.map((w) => <div key={w} className="text-amber-400">{w}</div>)}
            {check?.ok && check.warnings.length === 0 && dirty && <span className="text-emerald-400 flex items-center gap-1.5"><Check className="w-3.5 h-3.5" /> Valid. Not saved yet.</span>}
            {message && !dirty && <span className="text-emerald-400">{message}</span>}
          </div>

          <div className="flex items-center gap-2">
            <button
              onClick={() => void save()}
              disabled={busy || !dirty || (check ? !check.ok : false)}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold disabled:opacity-40"
            >
              <Save className="w-3.5 h-3.5" /> Save (Ctrl+S)
            </button>
            <button
              onClick={() => setDrafts(({ [prompt.name]: _gone, ...rest }) => rest)}
              disabled={!dirty}
              className="px-3 py-1.5 rounded border border-[var(--border-color)] text-[var(--text-muted)] hover:text-[var(--text-primary)] disabled:opacity-40"
            >
              Discard changes
            </button>
            <button
              onClick={() => void resetToBuiltIn()}
              disabled={busy || !edited}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded border border-[var(--border-color)] text-[var(--text-muted)] hover:text-red-400 disabled:opacity-40"
              title="Delete your copy and use the built-in text"
            >
              <RotateCcw className="w-3.5 h-3.5" /> Use built-in text
            </button>
          </div>

          <p className="text-[var(--text-muted)] max-w-2xl">
            Keep every <code>{'{{variable}}'}</code> the header declares: the program fills them in and refuses a copy that drops one.
            Variables marked untrusted hold text from a book or from you and stay fenced. A copy is used from the next request on; a build
            that is already running keeps the prompts it started with.
          </p>
        </div>
      )}
    </div>
  );
}
