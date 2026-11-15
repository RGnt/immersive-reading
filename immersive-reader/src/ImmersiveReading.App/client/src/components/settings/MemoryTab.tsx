import { useCallback, useEffect, useState } from 'react';
import { AlertCircle, Loader2, Plus, Trash2 } from 'lucide-react';
import { rpc } from '../../services/rpc';

interface Fact {
  id: string;
  sourceId: string;
  text: string;
  subject: string;
  origin: string;
  createdUtc: string;
  sharedWith: string[];
}

function whereFrom(origin: string): string {
  if (origin.startsWith('chat:')) return 'told in a conversation';
  if (origin === 'settings') return 'added here';
  return origin || 'unknown';
}

/** Settings → Memory: everything the characters know about you. Every fact can be seen and really deleted. */
export function MemoryTab() {
  const [facts, setFacts] = useState<Fact[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const result = await rpc.call<{ available: boolean; facts: Fact[] }>('memory.list', {});
      setAvailable(result.available);
      setFacts(result.facts);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const add = async () => {
    if (!draft.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await rpc.call<{ ok: boolean; error: string | null }>('memory.add', { text: draft });
      if (result.ok) { setDraft(''); await load(); } else setError(result.error);
    } finally {
      setBusy(false);
    }
  };

  const forget = async (id: string) => {
    await rpc.call('memory.forget', { id });
    await load();
  };

  const forgetAll = async () => {
    if (!facts?.length || !confirm(`Delete all ${facts.length} facts? The characters will no longer know any of them.`)) return;
    await rpc.call('memory.forgetAll', {});
    await load();
  };

  if (!facts && !error) return <div className="text-xs text-[var(--text-muted)] flex items-center gap-1.5"><Loader2 className="w-3.5 h-3.5 animate-spin" /> Loading…</div>;

  return (
    <div className="space-y-4 text-xs max-w-3xl" data-testid="memory-tab">
      <p className="text-[var(--text-muted)]">
        Facts you state about yourself while talking to a character are kept <b>on this computer</b> and given to the characters in later
        conversations. When a fact is used, it is sent to your model endpoint like the rest of the chat. Turn the whole thing off, or limit
        a fact to one character, in Plugins → AI Reading Companion.
      </p>

      {!available && <div className="text-amber-400 flex items-center gap-1.5"><AlertCircle className="w-3.5 h-3.5" /> The memory file could not be opened, so nothing can be remembered.</div>}
      {error && <div className="text-red-400 flex items-center gap-1.5"><AlertCircle className="w-3.5 h-3.5" /> {error}</div>}

      <div className="flex gap-2">
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void add(); }}
          placeholder="Tell the characters something about you, e.g. My sister is called Yuna"
          className="flex-1 px-2 py-1.5 rounded bg-[var(--bg-input)] border border-[var(--border-color)] text-[var(--text-primary)]"
          maxLength={500}
        />
        <button onClick={() => void add()} disabled={busy || !draft.trim()} className="flex items-center gap-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold disabled:opacity-40">
          <Plus className="w-3.5 h-3.5" /> Add
        </button>
      </div>

      {facts && facts.length === 0 ? (
        <div className="text-[var(--text-muted)]">Nothing yet. Say something about yourself in a conversation and it will show up here.</div>
      ) : (
        <ul className="divide-y divide-[var(--border-color)] border border-[var(--border-color)] rounded" data-testid="memory-facts">
          {facts?.map((fact) => (
            <li key={fact.id} className="flex items-start justify-between gap-3 px-3 py-2">
              <div className="min-w-0">
                <div className="text-[var(--text-primary)] break-words">{fact.text}</div>
                <div className="text-[var(--text-muted)] mt-0.5">
                  {whereFrom(fact.origin)}
                  {fact.createdUtc && ` · ${new Date(fact.createdUtc).toLocaleDateString()}`}
                  {fact.sharedWith.length > 0 && ' · only one character knows this'}
                </div>
              </div>
              <button onClick={() => void forget(fact.id)} title="Forget this" className="shrink-0 p-1 rounded text-[var(--text-muted)] hover:text-red-400">
                <Trash2 className="w-3.5 h-3.5" />
              </button>
            </li>
          ))}
        </ul>
      )}

      {!!facts?.length && (
        <button onClick={() => void forgetAll()} className="px-3 py-1.5 rounded border border-[var(--border-color)] text-[var(--text-muted)] hover:text-red-400">
          Forget everything
        </button>
      )}
    </div>
  );
}
