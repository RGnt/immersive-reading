// Character Knowledge plugin UI. Source for Assets/ui.js (build with `npm run build:plugins` in client/).
// Surfaces: Book Info tab (set up, build, browse characters / events / items), series action (build a whole series), reader drawer
// (characters, events and items as of the chapter being read). All gating happens in the builder; this UI only asks
// "as of chapter N" and shows what comes back.
import React, { useState, useEffect, useRef, useCallback } from 'react';
import { Users, Network, Loader2, Play, Square, RefreshCw, Trash2, CheckCircle2, AlertTriangle, Shield, Swords, Package, MapPin } from 'lucide-react';
import { usePluginRpc, useActiveBook, PluginDrawerShell } from '@ir/sdk';

const PLUGIN_ID = 'characterKnowledge';
const errMsg = (e) => (e && e.message) || String(e);

// What happened (builder: EVENT_KINDS) and where an item is worn (builder: SLOTS).
const KINDS = [
  ['battle', 'Battle'], ['hunt', 'Hunt'], ['dungeon', 'Dungeon'], ['quest', 'Quest'], ['item', 'Item'], ['growth', 'Growth'],
  ['travel', 'Travel'], ['meeting', 'Meeting'], ['group', 'Group'], ['revelation', 'Revelation'], ['death', 'Death'], ['other', 'Other'],
];
const KIND_LABEL = Object.fromEntries(KINDS);
const SLOT_LABEL = { weapon: 'Weapon', shield: 'Shield', head: 'Head', body: 'Body', hands: 'Hands', legs: 'Legs', feet: 'Feet', cloak: 'Cloak', accessory: 'Accessory', other: 'Other' };
const SLOT_ORDER = Object.keys(SLOT_LABEL);
const REASON_LABEL = { replaced: 'replaced by newer gear', transferred: 'given to someone else', changed: 'carried instead / worn instead', lost: 'lost or gone' };

/** "ch. 4", or "vol 1 ch. 4" when it is not the volume being read. Chapters are 0-based in the data. */
const where = (pos, asOf) => `${asOf && pos.volume === asOf.volume ? '' : `vol ${pos.volume} `}ch. ${pos.chapter + 1}`;

/** Polls getStatus while a build runs (every 1.5 s) and once on mount / when the key changes. */
function useKnowledgeStatus(target) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const [status, setStatus] = useState(null);
  const [error, setError] = useState(null);
  const key = JSON.stringify(target);
  const alive = useRef(true);

  const reload = useCallback(async () => {
    try {
      const s = await call('getStatus', target);
      if (alive.current) { setStatus(s); setError(null); }
      return s;
    } catch (e) {
      if (alive.current) setError(errMsg(e));
      return null;
    }
  }, [key]);

  useEffect(() => {
    alive.current = true;
    reload();
    return () => { alive.current = false; };
  }, [reload]);

  const running = status?.job?.state === 'running';
  useEffect(() => {
    if (!running) return undefined;
    const t = setInterval(reload, 1500);
    return () => clearInterval(t);
  }, [running, reload]);

  return { status, error, reload, call };
}

function Progress({ job }) {
  const pct = job.total > 0 ? Math.min(100, Math.round((job.done / job.total) * 100)) : 0;
  return (
    <div className="ck-inset">
      <div className="ck-row ck-between">
        <span className="ck-title">
          {job.state === 'running' && <Loader2 size={13} className="animate-spin" />} {job.state === 'running' ? (job.phase === 'story' ? 'Reading the book again for events and items…' : 'Reading the book…') : job.state === 'done' ? 'Finished' : job.state === 'cancelled' ? 'Cancelled' : 'Stopped'}
        </span>
        <span className="ck-muted">{job.done} / {job.total} steps{job.skipped ? ` (${job.skipped} already up to date)` : ''}</span>
      </div>
      <div className="ck-bar"><div className="ck-bar-fill" style={{ width: `${pct}%` }} /></div>
      {job.state === 'running' && job.currentChapter >= 0 && (
        <div className="ck-muted">Volume {job.currentVolume}, chapter {job.currentChapter + 1}{job.currentTitle ? `: ${job.currentTitle}` : ''}</div>
      )}
      <div className="ck-muted">
        {job.opsApplied} facts stored, {job.opsRejected} rejected
        {job.storyOpsApplied || job.storyOpsRejected ? `; events and items: ${job.storyOpsApplied} stored, ${job.storyOpsRejected} rejected` : ''}
        {job.splits ? `, ${job.splits} chapters split` : ''}
        {job.contextTokensLearned ? `; server limit ${job.contextTokensLearned} tokens` : ''}{job.model ? `; model ${job.model}` : ''}
      </div>
      {job.error && job.state !== 'done' && <div className="ck-err">{job.error}</div>}
      {job.log?.length > 0 && (
        <details>
          <summary className="ck-muted">Details</summary>
          <pre className="ck-log">{job.log.join('\n')}</pre>
        </details>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- profile view

function TraitList({ traits }) {
  const entries = Object.entries(traits || {});
  if (entries.length === 0) return <div className="ck-muted">Nothing is known yet.</div>;
  return (
    <dl className="ck-traits">
      {entries.map(([k, values]) => (
        <React.Fragment key={k}>
          <dt>{k.replace(/_/g, ' ')}</dt>
          <dd>{Array.isArray(values) ? values.join('; ') : String(values)}</dd>
        </React.Fragment>
      ))}
    </dl>
  );
}

function ProfileView({ profile }) {
  if (!profile) return null;
  const p = profile.profile || {};
  return (
    <div className="ck-profile">
      <div className="ck-title" style={{ fontSize: 15 }}>{profile.name}</div>
      {p.aliases?.length > 0 && <div className="ck-muted">Also known as: {p.aliases.join('、')}</div>}
      <TraitList traits={p.traits} />
      {p.relationships?.length > 0 && (
        <>
          <div className="ck-sub">Relationships</div>
          <ul className="ck-list">
            {p.relationships.map((r, i) => <li key={i}><b>{r.other}</b> <span className="ck-muted">({r.type})</span> {r.summary}</li>)}
          </ul>
        </>
      )}
      {(p.equipment?.length > 0 || p.belongings?.length > 0) && (
        <>
          <div className="ck-sub">Equipment</div>
          <dl className="ck-traits">
            {(p.equipment || []).map((e, i) => (
              <React.Fragment key={`${e.slot}:${i}`}>
                <dt>{SLOT_LABEL[e.slot] || e.slot}</dt>
                <dd>{e.item}{e.replaces ? <span className="ck-tag ck-tag-new">replaces {e.replaces}</span> : null}</dd>
              </React.Fragment>
            ))}
            {p.belongings?.length > 0 && <><dt>carrying</dt><dd>{p.belongings.join('、')}</dd></>}
          </dl>
        </>
      )}
      {p.former_gear?.length > 0 && (
        <div className="ck-muted">Formerly: {p.former_gear.map((g) => `${g.item} (${(SLOT_LABEL[g.slot] || g.slot).toLowerCase()}, ${g.replaced_by ? `replaced by ${g.replaced_by}, ` : ''}until ch. ${g.until_chapter + 1})`).join('; ')}</div>
      )}
      {p.timeline?.length > 0 && (
        <>
          <div className="ck-sub">What has happened</div>
          <ul className="ck-list">
            {p.timeline.map((t, i) => (
              <li key={i}>
                <span className="ck-muted">ch. {(t.chapter ?? 0) + 1}</span>{' '}
                {t.kind && <span className={`ck-kind ck-kind-${t.kind}`}>{KIND_LABEL[t.kind] || t.kind}</span>} {t.summary}
              </li>
            ))}
          </ul>
        </>
      )}
      {p.speech_samples?.length > 0 && (
        <>
          <div className="ck-sub">How they speak</div>
          <ul className="ck-list">{p.speech_samples.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </>
      )}
    </div>
  );
}

function SpoilerNote({ asOf }) {
  return (
    <div className="ck-note">
      <Shield size={12} /> Only what a reader knows before chapter {asOf.chapter + 1}{asOf.volume > 1 ? ` of volume ${asOf.volume}` : ''}. The current chapter is hidden until you have read it.
    </div>
  );
}

/** Character list + profile for one book as of a chapter. Used by the tab and by the reader drawer. */
function Browser({ bookId, chapterIndex, compact }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const [state, setState] = useState({ loading: true });
  const [selected, setSelected] = useState(null);
  const [profile, setProfile] = useState(null);
  const [filter, setFilter] = useState('');

  useEffect(() => {
    let cancelled = false;
    setState({ loading: true });
    setProfile(null);
    call('listCharacters', { bookId, chapterIndex })
      .then((r) => { if (!cancelled) setState({ asOf: r.asOf, characters: r.characters || [] }); })
      .catch((e) => { if (!cancelled) setState({ error: errMsg(e) }); });
    return () => { cancelled = true; };
  }, [bookId, chapterIndex]);

  useEffect(() => {
    if (!selected) { setProfile(null); return undefined; }
    let cancelled = false;
    call('getProfile', { bookId, chapterIndex, characterId: selected })
      .then((r) => { if (!cancelled) setProfile(r.profile); })
      .catch(() => { if (!cancelled) setProfile(null); });
    return () => { cancelled = true; };
  }, [selected, bookId, chapterIndex, state.asOf?.chapter]);

  if (state.loading) return <div className="ck-muted"><Loader2 size={13} className="animate-spin" /> Loading…</div>;
  if (state.error) return <div className="ck-err">{state.error}</div>;
  const q = filter.trim().toLowerCase();
  const shown = state.characters.filter((c) => !q || c.name.toLowerCase().includes(q));

  return (
    <div className="ck-col">
      <SpoilerNote asOf={state.asOf} />
      {state.characters.length === 0 ? (
        <div className="ck-muted">No characters are known at this point of the story (or this part has not been built yet).</div>
      ) : (
        <div className={compact ? 'ck-col' : 'ck-split'}>
          <div className="ck-col" style={{ minWidth: 0 }}>
            <input className="ck-input" placeholder="Filter characters…" value={filter} onChange={(e) => setFilter(e.target.value)} />
            <div className="ck-chars">
              {shown.map((c) => (
                <button key={c.characterId} className={`ck-char ${selected === c.characterId ? 'ck-active' : ''}`} onClick={() => setSelected(c.characterId === selected ? null : c.characterId)}>
                  {c.name}
                </button>
              ))}
            </div>
          </div>
          <div style={{ minWidth: 0 }}>
            {selected ? <ProfileView profile={profile} /> : <div className="ck-muted">Select a character.</div>}
          </div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- events and items

function Chip({ kind, onClick, title, children }) {
  return <button type="button" className={`ck-chip ck-chip-${kind}`} onClick={onClick} title={title} disabled={!onClick}>{children}</button>;
}

/** Events newest first; children (parts of a larger happening) are listed oldest first under their parent. */
function nestEvents(events) {
  const ids = new Set(events.map((e) => e.id));
  const kids = new Map();
  const roots = [];
  for (const e of events) {
    if (e.parent && ids.has(e.parent)) {
      if (!kids.has(e.parent)) kids.set(e.parent, []);
      kids.get(e.parent).push(e);
    } else roots.push(e);
  }
  for (const list of kids.values()) list.reverse();
  return { roots, kids };
}

function EventNode({ event, kids, asOf, onCharacter, onItem, nested }) {
  const children = kids.get(event.id) || [];
  return (
    <div className={nested ? 'ck-event ck-nested' : 'ck-event'}>
      <div className="ck-row ck-wrap">
        {event.typed && <span className={`ck-kind ck-kind-${event.kind}`}>{KIND_LABEL[event.kind] || event.kind}</span>}
        <b>{event.title || event.summary}</b>
        <span className="ck-muted">{where(event, asOf)}</span>
      </div>
      {event.title && event.summary && event.summary !== event.title && <div>{event.summary}</div>}
      {(event.location || event.participants.length > 0) && (
        <div className="ck-row ck-wrap">
          {event.location && <Chip kind="location" title="Where it happened"><MapPin size={10} /> {event.location.name}</Chip>}
          {event.participants.map((p) => (
            <Chip
              key={`${p.id}:${p.role}`}
              kind={p.kind}
              title={p.kind === 'character' ? 'Show only their events' : p.kind === 'item' ? 'Show this item' : p.kind}
              onClick={p.kind === 'character' ? () => onCharacter(p.id, p.name) : p.kind === 'item' ? () => onItem(p.id) : undefined}
            >
              {p.name}{p.role ? <span className="ck-muted"> · {p.role}</span> : null}
            </Chip>
          ))}
        </div>
      )}
      {children.map((c) => <EventNode key={c.id} event={c} kids={kids} asOf={asOf} onCharacter={onCharacter} onItem={onItem} nested />)}
    </div>
  );
}

/** What has happened so far (as of a chapter): typed events with the characters, monsters, items and places involved. */
function EventsView({ bookId, chapterIndex, onItem }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const [characters, setCharacters] = useState([]);
  const [who, setWho] = useState({ id: '', name: '' });
  const [kind, setKind] = useState('');
  const [text, setText] = useState('');
  const [state, setState] = useState({ loading: true });

  useEffect(() => { setWho({ id: '', name: '' }); }, [bookId]);
  useEffect(() => {
    let cancelled = false;
    call('listCharacters', { bookId, chapterIndex }).then((r) => { if (!cancelled) setCharacters(r.characters || []); }).catch(() => { if (!cancelled) setCharacters([]); });
    return () => { cancelled = true; };
  }, [bookId, chapterIndex]);
  useEffect(() => {
    let cancelled = false;
    setState((s) => ({ ...s, loading: true }));
    call('listEvents', { bookId, chapterIndex, characterId: who.id || undefined, kind: kind || undefined, limit: 500 })
      .then((r) => { if (!cancelled) setState({ asOf: r.asOf, total: r.total, events: r.events || [] }); })
      .catch((e) => { if (!cancelled) setState({ error: errMsg(e) }); });
    return () => { cancelled = true; };
  }, [bookId, chapterIndex, who.id, kind]);

  if (state.error) return <div className="ck-err">{state.error}</div>;
  if (!state.asOf) return <div className="ck-muted"><Loader2 size={13} className="animate-spin" /> Loading…</div>;

  const q = text.trim().toLowerCase();
  const matches = (e) => !q || [e.title, e.summary, e.location?.name, ...e.participants.map((p) => p.name)].some((v) => v && v.toLowerCase().includes(q));
  const events = state.events.filter(matches);
  const { roots, kids } = nestEvents(events);
  const known = characters.some((c) => c.characterId === who.id);

  return (
    <div className="ck-col">
      <SpoilerNote asOf={state.asOf} />
      <div className="ck-row ck-wrap">
        <select className="ck-select" value={who.id} onChange={(e) => setWho({ id: e.target.value, name: characters.find((c) => c.characterId === e.target.value)?.name || '' })} title="Only events this character took part in">
          <option value="">Everyone</option>
          {who.id && !known && <option value={who.id}>{who.name}</option>}
          {characters.map((c) => <option key={c.characterId} value={c.characterId}>{c.name}</option>)}
        </select>
        <select className="ck-select" value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="">All kinds</option>
          {KINDS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
        </select>
        <input className="ck-input" style={{ flex: 1 }} placeholder="Search events…" value={text} onChange={(e) => setText(e.target.value)} />
      </div>
      {state.total === 0 ? (
        <div className="ck-muted">No events are known at this point of the story{who.id || kind ? ' for this filter' : ''} (or the events of this part have not been built yet).</div>
      ) : (
        <>
          <div className="ck-muted">
            {events.length === state.total ? `${state.total} events` : `${events.length} of ${state.total} events shown`}
            {state.total > state.events.length ? ` (the newest ${state.events.length} were loaded)` : ''}, newest first
          </div>
          <div className="ck-col">
            {roots.map((e) => <EventNode key={e.id} event={e} kids={kids} asOf={state.asOf} onCharacter={(id, name) => setWho({ id, name })} onItem={onItem} />)}
          </div>
        </>
      )}
    </div>
  );
}

function ItemRow({ item, asOf, open, onToggle, showHolder, badge }) {
  const d = item.description || {};
  const ref = useRef(null);
  useEffect(() => { if (open && ref.current?.scrollIntoView) ref.current.scrollIntoView({ block: 'nearest' }); }, [open]);
  return (
    <div className="ck-item" ref={ref}>
      <button type="button" className="ck-item-head" onClick={onToggle}>
        {badge && <span className="ck-kind ck-kind-slot">{badge}</span>}
        <b>{item.name}</b>
        {item.replaces && <span className="ck-tag ck-tag-new" title="Newer gear: it took this slot over">replaces {item.replaces.name}</span>}
        {item.replaced_by && <span className="ck-tag ck-tag-old" title="Older gear: it is no longer used">replaced by {item.replaced_by.name}</span>}
        {showHolder && <span className="ck-muted">{item.holder ? `${item.holder.name}${item.state === 'equipped' ? ` (${(SLOT_LABEL[item.slot] || item.slot).toLowerCase()})` : ''}` : 'nobody has it now'}</span>}
      </button>
      {(d.appearance || d.ability || d.note || d.status) && (
        <div className="ck-muted">{[...(d.appearance || []), ...(d.ability || []), ...(d.note || []), ...(d.status || [])].join(' · ')}</div>
      )}
      {open && (
        <ul className="ck-list">
          {item.history.map((h, i) => (
            <li key={i}>
              <b>{h.holder.name}</b>: {h.state === 'equipped' ? `worn (${(SLOT_LABEL[h.slot] || h.slot).toLowerCase()})` : 'carried'}, {where(h.from, asOf)}
              {h.to ? ` to ${where(h.to, asOf)}${h.reason ? ` (${h.reason === 'replaced' && h.replaced_by ? `replaced by ${h.replaced_by.name}` : REASON_LABEL[h.reason] || h.reason})` : ''}` : ' until now'}
              {h.replaces ? <span className="ck-tag ck-tag-new">replaced {h.replaces.name}</span> : null}
            </li>
          ))}
          {item.history.length === 0 && <li className="ck-muted">Nobody is known to have had it yet.</li>}
        </ul>
      )}
    </div>
  );
}

/** Items the characters have as of a chapter: grouped by who has them (worn gear by slot first), or all items. */
function ItemsView({ bookId, chapterIndex, focus }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const [state, setState] = useState({ loading: true });
  const [mode, setMode] = useState('holder');
  const [text, setText] = useState('');
  const [open, setOpen] = useState(() => new Set());

  useEffect(() => {
    let cancelled = false;
    setState({ loading: true });
    call('listItems', { bookId, chapterIndex })
      .then((r) => { if (!cancelled) setState({ asOf: r.asOf, items: r.items || [] }); })
      .catch((e) => { if (!cancelled) setState({ error: errMsg(e) }); });
    return () => { cancelled = true; };
  }, [bookId, chapterIndex]);
  useEffect(() => {
    if (!focus) return;
    setMode('all');
    setText('');
    setOpen((o) => new Set(o).add(focus));
  }, [focus]);

  if (state.error) return <div className="ck-err">{state.error}</div>;
  if (!state.asOf) return <div className="ck-muted"><Loader2 size={13} className="animate-spin" /> Loading…</div>;

  const toggle = (id) => setOpen((o) => { const n = new Set(o); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const q = text.trim().toLowerCase();
  const items = state.items.filter((i) => !q || i.name.toLowerCase().includes(q) || i.holder?.name.toLowerCase().includes(q));

  // group by holder: people with the most items first, then those nobody holds
  const groups = new Map();
  for (const item of items) {
    const key = item.holder ? item.holder.id : '';
    if (!groups.has(key)) groups.set(key, { name: item.holder ? item.holder.name : 'Nobody has them now', items: [] });
    groups.get(key).items.push(item);
  }
  const slotRank = (i) => (i.state === 'equipped' ? SLOT_ORDER.indexOf(i.slot) : 100);
  const ordered = [...groups.entries()].sort(([ka, a], [kb, b]) => (ka === '') - (kb === '') || b.items.length - a.items.length || a.name.localeCompare(b.name));
  for (const [, g] of ordered) g.items.sort((a, b) => slotRank(a) - slotRank(b) || a.name.localeCompare(b.name));

  return (
    <div className="ck-col">
      <SpoilerNote asOf={state.asOf} />
      <div className="ck-row ck-wrap">
        <button className={`ck-btn ${mode === 'holder' ? 'ck-active' : ''}`} onClick={() => setMode('holder')}>By character</button>
        <button className={`ck-btn ${mode === 'all' ? 'ck-active' : ''}`} onClick={() => setMode('all')}>All items</button>
        <input className="ck-input" style={{ flex: 1 }} placeholder="Search items or people…" value={text} onChange={(e) => setText(e.target.value)} />
      </div>
      {state.items.length === 0 ? (
        <div className="ck-muted">No items are known at this point of the story (or the items of this part have not been built yet).</div>
      ) : mode === 'all' ? (
        <div className="ck-col">
          {items.map((i) => <ItemRow key={i.id} item={i} asOf={state.asOf} open={open.has(i.id)} onToggle={() => toggle(i.id)} showHolder />)}
        </div>
      ) : (
        <div className="ck-col">
          {ordered.map(([key, g]) => (
            <div key={key || 'nobody'} className="ck-inset">
              <div className="ck-title">{g.name} <span className="ck-muted">({g.items.length})</span></div>
              {g.items.map((i) => (
                <ItemRow key={i.id} item={i} asOf={state.asOf} open={open.has(i.id)} onToggle={() => toggle(i.id)}
                  badge={i.state === 'equipped' ? SLOT_LABEL[i.slot] || i.slot : i.holder ? 'carrying' : ''} />
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- builder panel

function BuildPanel({ target, scopeChoice, bookId }) {
  const { status, error, reload, call } = useKnowledgeStatus(target);
  const [scope, setScope] = useState(scopeChoice ? 'upToVolume' : 'series');
  const [estimate, setEstimate] = useState(null);
  const [endpoint, setEndpoint] = useState(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState(null);

  useEffect(() => { setEstimate(null); }, [scope, JSON.stringify(target)]);

  const withScope = { ...target, scope };
  const run = async (fn) => {
    setBusy(true);
    setMessage(null);
    try { await fn(); } catch (e) { setMessage({ error: true, text: errMsg(e) }); } finally { setBusy(false); }
  };

  const doEstimate = () => run(async () => setEstimate(await call('estimateBuild', withScope)));
  const doTest = () => run(async () => setEndpoint(await call('testEndpoint', {})));
  const doStart = (rebuild) => run(async () => {
    const r = await call('startBuild', { ...withScope, rebuild });
    if (!r?.started && r?.error) setMessage({ error: true, text: r.error });
    await reload();
  });
  const doCancel = () => run(async () => { await call('cancelBuild', target); await reload(); });
  const doDelete = () => run(async () => {
    if (!window.confirm('Delete the knowledge built for this book/series? It can be rebuilt, but that takes a while.')) return;
    await call('deleteKnowledge', target);
    setEstimate(null);
    await reload();
  });

  if (error && !status) return <div className="ck-err">{error}</div>;
  if (!status) return <div className="ck-muted"><Loader2 size={13} className="animate-spin" /> Loading…</div>;

  const job = status.job;
  const running = job?.state === 'running';
  const builtTotal = status.series.volumes.reduce((n, v) => n + v.built, 0);
  const chapterTotal = status.series.volumes.reduce((n, v) => n + v.chapterCount, 0);
  const withStory = !!status.knowledge.extractStory;
  const storyTotal = status.series.volumes.reduce((n, v) => n + (v.storyBuilt || 0), 0);
  const addStoryOnly = withStory && builtTotal > 0 && builtTotal >= chapterTotal && storyTotal < chapterTotal;

  return (
    <div className="ck-col">
      {!status.builder.found && (
        <div className="ck-warnbox"><AlertTriangle size={14} /> <div><b>The builder is not set up.</b><pre className="ck-log">{status.builder.problem}</pre></div></div>
      )}

      {status.series.orderChanged && (
        <div className="ck-warnbox"><AlertTriangle size={14} /> The volumes of this series are ordered differently than when the knowledge was built. Rebuild so the spoiler boundaries match the new order.</div>
      )}

      <div className="ck-inset">
        <div className="ck-row ck-between ck-wrap">
          <div>
            <div className="ck-title">{status.series.title}</div>
            <div className="ck-muted">
              {builtTotal} of {chapterTotal} chapters built across {status.series.volumes.length} volume{status.series.volumes.length === 1 ? '' : 's'}
              {status.knowledge.exists ? `; ${status.knowledge.counts.entities} entities, ${status.knowledge.counts.relations} relationships, ${status.knowledge.counts.events} events` : ''}
              {withStory ? `; events and items: ${storyTotal} of ${chapterTotal} chapters${status.knowledge.hasStory ? ` (${status.knowledge.counts.eventCards} events, ${status.knowledge.counts.itemCards} items)` : ''}` : ''}
            </div>
          </div>
          <div className="ck-muted ck-right">
            Model: {status.llm.configured ? `${status.llm.model || 'server default'} @ ${status.llm.endpoint}` : <span className="ck-warn">not configured</span>}
            {status.llm.usesSettingsAi && ' (Settings → AI)'}
          </div>
        </div>
        {status.series.volumes.length > 1 && (
          <table className="ck-table">
            <thead><tr><th>Vol</th><th>Book</th><th>Characters</th>{withStory && <th>Events and items</th>}<th></th></tr></thead>
            <tbody>
              {status.series.volumes.map((v) => (
                <tr key={v.bookId} className={v.bookId === bookId ? 'ck-current' : ''}>
                  <td className="ck-mono">{v.volume}</td>
                  <td>{v.title}</td>
                  <td>{v.built}/{v.chapterCount}{v.failed ? <span className="ck-err"> ({v.failed} failed)</span> : null}</td>
                  {withStory && <td>{v.storyBuilt}/{v.chapterCount}{v.storyFailed ? <span className="ck-err"> ({v.storyFailed} failed)</span> : null}</td>}
                  <td>{v.built === v.chapterCount && v.chapterCount > 0 && (!withStory || v.storyBuilt === v.chapterCount) ? <CheckCircle2 size={13} className="ck-ok" /> : null}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="ck-inset ck-col">
        <div className="ck-title">Build the knowledge{withStory ? ' (characters, then events and items)' : ' of the characters'}</div>
        <div className="ck-muted">
          The book text is sent chapter by chapter to <b>{status.llm.endpoint || 'the configured model'}</b> (once per book; finished chapters are kept and only changed ones are redone).
          {withStory
            ? ' After the characters, a second pass records what happened (battles, hunts, dungeons, drops, quests) and who has which item. It can be added to a book whose characters are already built. It can be switched off in the plugin settings.'
            : ' Events and items are switched off in the plugin settings.'}
        </div>
        {scopeChoice && (
          <div className="ck-row ck-wrap">
            <label className="ck-row"><input type="radio" checked={scope === 'upToVolume'} onChange={() => setScope('upToVolume')} /> This volume{status.currentVolume > 1 ? ' and the ones before it' : ''}</label>
            <label className="ck-row"><input type="radio" checked={scope === 'series'} onChange={() => setScope('series')} /> The whole series</label>
          </div>
        )}
        <div className="ck-row ck-wrap">
          <button className="ck-btn" disabled={busy || running} onClick={doTest}>Test connection</button>
          <button className="ck-btn" disabled={busy || running} onClick={doEstimate}>Estimate</button>
          {running ? (
            <button className="ck-btn ck-danger" disabled={busy} onClick={doCancel}><Square size={12} /> Cancel</button>
          ) : (
            <>
              <button className="ck-btn ck-primary" disabled={busy || !status.builder.found || !status.llm.configured} onClick={() => doStart(false)}>
                <Play size={12} /> {addStoryOnly ? 'Add events and items' : builtTotal > 0 ? 'Build / continue' : 'Build'}
              </button>
              {builtTotal > 0 && (
                <button className="ck-btn" disabled={busy || !status.builder.found || !status.llm.configured} onClick={() => doStart(true)} title="Discard what was built and start over">
                  <RefreshCw size={12} /> Rebuild
                </button>
              )}
              {status.knowledge.exists && <button className="ck-btn" disabled={busy} onClick={doDelete}><Trash2 size={12} /> Delete</button>}
            </>
          )}
        </div>

        {endpoint && (endpoint.ok
          ? <div className="ck-ok">Connected in {endpoint.latencyMs} ms{endpoint.model ? ` (${endpoint.model})` : ''}.{endpoint.advertisedContext ? ` Advertised context: ${endpoint.advertisedContext.toLocaleString()} tokens. ${endpoint.note}` : ''}</div>
          : <div className="ck-err">{endpoint.error}</div>)}

        {estimate && (
          <div className="ck-muted">
            {estimate.toBuild} of {estimate.chapters} chapters to build ({estimate.alreadyBuilt} up to date), about {estimate.characters.toLocaleString()} characters of text.
            Roughly {estimate.requests}{estimate.contextKnown ? '' : `–${estimate.requestsIfSmallContext}`} model requests; local models take about 10 seconds per request.
            {estimate.extractStory && ` Events and items: ${estimate.storyToBuild} chapters (${estimate.storyAlreadyBuilt} up to date), roughly ${estimate.storyRequests} more requests.`}
          </div>
        )}
        {message && <div className={message.error ? 'ck-err' : 'ck-ok'}>{message.text}</div>}
      </div>

      {job && <Progress job={job} />}

      {status.knowledge.failedStoryChapters?.length > 0 && !running && (
        <div className="ck-warnbox"><AlertTriangle size={14} /> <div>
          The events and items of {status.knowledge.failedStoryChapters.length} chapter(s) failed and will be retried by Build / continue (the characters are not affected):
          <ul className="ck-list">{status.knowledge.failedStoryChapters.slice(0, 5).map((c) => <li key={`${c.volume}:${c.chapter}`}>vol {c.volume} ch. {c.chapter + 1}: {c.error}</li>)}</ul>
        </div></div>
      )}

      {status.knowledge.failedChapters?.length > 0 && !running && (
        <div className="ck-warnbox"><AlertTriangle size={14} /> <div>
          {status.knowledge.failedChapters.length} chapter(s) failed and will be retried by Build / continue:
          <ul className="ck-list">{status.knowledge.failedChapters.slice(0, 5).map((c) => <li key={`${c.volume}:${c.chapter}`}>vol {c.volume} ch. {c.chapter + 1}: {c.error}</li>)}</ul>
        </div></div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- book info tab

function BookTab({ bookId, book }) {
  const [view, setView] = useState('browse');
  const [focusItem, setFocusItem] = useState(null);
  const [chapter, setChapter] = useState(null);
  const { call } = usePluginRpc(PLUGIN_ID);
  const [reading, setReading] = useState(null);
  const chapters = (book?.chapters || []).filter((c) => !c.isExcluded);

  useEffect(() => {
    call('getStatus', { bookId }).then((s) => setReading(s.readingChapterIndex ?? 0)).catch(() => setReading(0));
    setChapter(null);
  }, [bookId]);

  const asOf = chapter ?? reading ?? 0;
  return (
    <div className="ck-root">
      <div className="ck-row ck-tabs">
        <button className={`ck-btn ${view === 'browse' ? 'ck-active' : ''}`} onClick={() => setView('browse')}><Users size={12} /> Characters</button>
        <button className={`ck-btn ${view === 'events' ? 'ck-active' : ''}`} onClick={() => setView('events')}><Swords size={12} /> Events</button>
        <button className={`ck-btn ${view === 'items' ? 'ck-active' : ''}`} onClick={() => setView('items')}><Package size={12} /> Items</button>
        <button className={`ck-btn ${view === 'build' ? 'ck-active' : ''}`} onClick={() => setView('build')}><Network size={12} /> Build</button>
      </div>
      {view === 'build' ? (
        <BuildPanel target={{ bookId }} scopeChoice bookId={bookId} />
      ) : (
        <div className="ck-col">
          <div className="ck-row ck-wrap">
            <span className="ck-muted">{view === 'events' ? 'Events' : view === 'items' ? 'Items' : 'Characters'} as of the start of</span>
            <select className="ck-select" value={asOf} onChange={(e) => setChapter(parseInt(e.target.value, 10))}>
              {chapters.length > 0
                ? chapters.map((c) => <option key={c.chapterIndex} value={c.chapterIndex}>{c.chapterIndex + 1}. {c.title}</option>)
                : <option value={asOf}>chapter {asOf + 1}</option>}
            </select>
            {reading != null && chapter != null && chapter !== reading && (
              <button className="ck-btn" onClick={() => setChapter(null)}>Back to where I am (ch. {reading + 1})</button>
            )}
          </div>
          {view === 'events' ? <EventsView bookId={bookId} chapterIndex={asOf} onItem={(id) => { setFocusItem(id); setView('items'); }} />
            : view === 'items' ? <ItemsView bookId={bookId} chapterIndex={asOf} focus={focusItem} />
              : <Browser bookId={bookId} chapterIndex={asOf} />}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- reader drawer

function CharactersDrawer({ onClose }) {
  const { bookId, chapterIndex } = useActiveBook();
  const [tab, setTab] = useState('characters');
  const [focusItem, setFocusItem] = useState(null);
  return (
    <PluginDrawerShell title="Story" onClose={onClose}>
      <div className="ck-root">
        {bookId ? (
          <>
            <div className="ck-row ck-tabs ck-wrap">
              <button className={`ck-btn ${tab === 'characters' ? 'ck-active' : ''}`} onClick={() => setTab('characters')}><Users size={12} /> Characters</button>
              <button className={`ck-btn ${tab === 'events' ? 'ck-active' : ''}`} onClick={() => setTab('events')}><Swords size={12} /> Events</button>
              <button className={`ck-btn ${tab === 'items' ? 'ck-active' : ''}`} onClick={() => setTab('items')}><Package size={12} /> Items</button>
            </div>
            {tab === 'events' ? <EventsView bookId={bookId} chapterIndex={chapterIndex} onItem={(id) => { setFocusItem(id); setTab('items'); }} />
              : tab === 'items' ? <ItemsView bookId={bookId} chapterIndex={chapterIndex} focus={focusItem} />
                : <Browser bookId={bookId} chapterIndex={chapterIndex} compact />}
          </>
        ) : <div className="ck-muted">Open a book to see its characters, events and items.</div>}
      </div>
    </PluginDrawerShell>
  );
}

// ---------------------------------------------------------------- activation

export function activate(ctx) {
  ctx.registerBookInfoTab({ id: 'characters', title: 'Characters', icon: Users, component: BookTab });
  ctx.registerReaderDrawer({
    id: 'characters-drawer',
    title: 'Story',
    icon: Users,
    location: 'right',
    defaultSize: 380,
    component: CharactersDrawer,
  });
  ctx.registerSeriesAction({
    id: 'build-character-knowledge',
    label: 'Characters',
    title: 'Build the character knowledge of this series',
    icon: Network,
    onClick: ({ seriesName, openModal }) =>
      openModal({
        id: 'character-knowledge-series',
        title: `Character knowledge: ${seriesName}`,
        size: 'xl',
        component: () => <div className="ck-root" style={{ maxWidth: 'none' }}><BuildPanel target={{ seriesName }} /></div>,
      }),
  });
}
