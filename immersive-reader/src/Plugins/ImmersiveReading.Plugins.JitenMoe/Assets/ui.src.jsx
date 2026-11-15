// jiten.moe plugin UI. Source for Assets/ui.js (build with `npm run build:plugins` in client/).
import React, { useState, useEffect } from 'react';
import { Globe, Sparkles, Download, Search, Loader2, ExternalLink } from 'lucide-react';
import { usePluginRpc, usePluginConfig, refreshLibrary } from '@ir/sdk';

const PLUGIN_ID = 'jitenMoe';
const URL_FIELD = 'jiten_moe_url';
const DIFF_FIELD = 'jiten_difficulty';

const cfValue = (v) => (v == null ? '' : typeof v === 'string' ? v : v.effectiveValueText ?? v.valueText ?? '');
const deckIdFromUrl = (url) => {
  const m = url && String(url).match(/(\d+)(?!.*\d)/);
  return m ? parseInt(m[1], 10) : null;
};
const diffClass = (d) => (d < 3.5 ? 'jm-diff-easy' : d < 6 ? 'jm-diff-med' : d < 8 ? 'jm-diff-hard' : 'jm-diff-master');
const errMsg = (e) => (e && e.message) || String(e);

// ---------------------------------------------------------------- badge (cards, rows, series)

function DifficultyBadge({ books, context }) {
  let diff = null;
  for (const b of books) {
    const raw = cfValue(b.customFields?.[DIFF_FIELD]);
    const n = raw ? parseFloat(raw) : NaN;
    if (!isNaN(n)) {
      diff = n;
      break;
    }
  }
  if (diff === null) return context === 'row' ? <span className="jm-muted" style={{ opacity: 0.35 }}>—</span> : null;
  return (
    <span className={`jm-badge jm-${context} ${diffClass(diff)}`} title={`jiten.moe difficulty: ${diff.toFixed(2)} / 10`}>
      ★ {diff.toFixed(1)}
    </span>
  );
}

// ---------------------------------------------------------------- shared bits

function Status({ status }) {
  if (!status) return null;
  return <div className={`jm-status ${status.type === 'success' ? 'jm-success' : status.type === 'error' ? 'jm-error' : ''}`}>{status.text}</div>;
}

function SearchResult({ item, busy, onDownload, onApply, rpc }) {
  const [vocab, setVocab] = useState(null);
  const [vocabOpen, setVocabOpen] = useState(false);
  const [subs, setSubs] = useState(null);
  const [subsOpen, setSubsOpen] = useState(false);

  const toggleVocab = async () => {
    setVocabOpen((o) => !o);
    if (!vocab) {
      try {
        const res = await rpc('getVocabularyPreview', { deckId: item.deckId, limit: 15 });
        setVocab(res?.words || []);
      } catch {
        setVocab([]);
      }
    }
  };
  const toggleSubs = async () => {
    setSubsOpen((o) => !o);
    if (!subs) {
      try {
        const res = await rpc('getDeckDetail', { deckId: item.deckId });
        setSubs(res?.subDecks || []);
      } catch {
        setSubs([]);
      }
    }
  };

  return (
    <div className="jm-result">
      <div className="jm-row-flex" style={{ alignItems: 'flex-start' }}>
        {item.coverUrl && <img className="jm-cover" src={item.coverUrl} alt="" onError={(e) => (e.target.style.display = 'none')} />}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="jm-title" style={{ fontSize: 12 }}>{item.originalTitle}</div>
          <div className="jm-muted">{[item.romajiTitle, item.englishTitle].filter(Boolean).join(' • ')}</div>
          <div className="jm-row-flex jm-wrap jm-muted" style={{ fontSize: 10, margin: '6px 0' }}>
            {item.difficulty != null && <span className={`jm-badge ${diffClass(item.difficulty)}`}>★ {item.difficulty.toFixed(1)}/10</span>}
            <span>Words: {(item.wordCount || 0).toLocaleString()}</span>
            <span>Chars: {(item.characterCount || 0).toLocaleString()}</span>
            {item.childrenDeckCount > 0 && <span className="jm-link">{item.childrenDeckCount} Volumes</span>}
          </div>
          <div className="jm-row-flex jm-wrap">
            <button className="jm-btn jm-primary" disabled={busy} onClick={() => onDownload(item.deckId, item)}>
              <Download size={12} /> Download Deck CSV
            </button>
            <button className="jm-btn" onClick={() => onApply(item)}>Apply Metadata</button>
            <button className={`jm-btn ${vocabOpen ? 'jm-active' : ''}`} onClick={toggleVocab}>📖 Vocab Preview</button>
            {item.childrenDeckCount > 0 && (
              <button className={`jm-btn ${subsOpen ? 'jm-active' : ''}`} onClick={toggleSubs}>▼ Volumes ({item.childrenDeckCount})</button>
            )}
            {item.jitenUrl && (
              <a className="jm-link" href={item.jitenUrl} target="_blank" rel="noreferrer">Jiten <ExternalLink size={10} /></a>
            )}
          </div>
        </div>
      </div>

      {vocabOpen && (
        <div className="jm-inset">
          {!vocab ? <Loader2 size={14} className="animate-spin" /> : vocab.length === 0 ? (
            <span className="jm-muted">No vocabulary preview available for this deck.</span>
          ) : (
            <div className="jm-scroll">
              <table className="jm-table">
                <thead><tr><th>Word</th><th>Reading</th><th>Meaning</th></tr></thead>
                <tbody>
                  {vocab.map((w, i) => (
                    <tr key={i}>
                      <td>{w.reading || '—'}</td>
                      <td className="jm-mono">{w.readingFurigana || '—'}</td>
                      <td className="jm-muted">{w.mainDefinition || '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {subsOpen && (
        <div className="jm-inset">
          {!subs ? <Loader2 size={14} className="animate-spin" /> : (
            <div className="jm-grid2">
              {subs.map((s) => (
                <div key={s.deckId} className="jm-result jm-row-flex jm-between" style={{ flexDirection: 'row', padding: 8 }}>
                  <div style={{ minWidth: 0 }}>
                    <div className="jm-title" style={{ fontSize: 11 }}>{s.originalTitle || `Volume #${s.deckId}`}</div>
                    <div className="jm-muted" style={{ fontSize: 10 }}>
                      {s.difficulty ? `★ ${s.difficulty.toFixed(1)} • ` : ''}{(s.characterCount || 0).toLocaleString()} chars
                    </div>
                  </div>
                  <button className="jm-btn jm-primary" disabled={busy} onClick={() => onDownload(s.deckId, s)}>Apply Vol</button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- book info tab

function BookTab({ bookId, book }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const { config } = usePluginConfig(PLUGIN_ID);
  const summary = book?.summary || {};
  const jitenUrl = cfValue(summary.customFields?.[URL_FIELD]);
  const hasDeck = !!book?.hasDeckCsv;

  const [mediaType, setMediaType] = useState(config?.defaultMediaType ?? 4);
  const [query, setQuery] = useState(summary.seriesName || summary.title || '');
  const [results, setResults] = useState([]);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState(null);

  useEffect(() => {
    setQuery(summary.seriesName || summary.title || '');
    setResults([]);
    setStatus(null);
  }, [bookId]);

  const search = async (e) => {
    e?.preventDefault();
    if (!query.trim()) return;
    setSearching(true);
    setStatus(null);
    try {
      const res = await call('search', { query: query.trim(), mediaType: mediaType === 0 ? undefined : mediaType });
      setResults(res?.items || []);
      if (!res?.items?.length) setStatus({ text: `No direct matches found on jiten.moe for "${query.trim()}".` });
    } catch (err) {
      setStatus({ type: 'error', text: `Search failed: ${errMsg(err)}` });
    } finally {
      setSearching(false);
    }
  };

  const apply = async (deck) => {
    try {
      await call('applyMetadata', {
        bookId,
        jitenUrl: deck.jitenUrl || `https://jiten.moe/decks/media/${deck.deckId}`,
        difficulty: deck.difficulty ?? undefined,
        description: deck.description ?? undefined,
        tags: [...(deck.genres || []), ...(deck.tags || []).map((t) => t.name)],
      });
      setStatus({ type: 'success', text: `Applied jiten.moe deck #${deck.deckId} (difficulty ${deck.difficulty ?? '—'}).` });
      await refreshLibrary(bookId);
    } catch (err) {
      setStatus({ type: 'error', text: `Apply failed: ${errMsg(err)}` });
    }
  };

  const download = async (deckId, deckMeta) => {
    const id = deckId ?? deckIdFromUrl(jitenUrl);
    if (!id) {
      setStatus({ type: 'error', text: 'No valid jiten.moe deck id. Search for a deck below.' });
      return;
    }
    setBusy(true);
    setStatus({ text: 'Downloading deck CSV from jiten.moe and attaching it to the book folder...' });
    try {
      const res = await call('downloadDeckCsv', { bookId, deckId: id });
      if (deckMeta) await apply(deckMeta);
      setStatus({ type: 'success', text: `Downloaded and attached deck.csv (${Math.round((res?.fileSizeBytes || 0) / 1024)} KB).` });
      await refreshLibrary(bookId);
    } catch (err) {
      setStatus({ type: 'error', text: `Download failed: ${errMsg(err)}` });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="jm-root">
      <div className="jm-card-box">
        <div className="jm-row-flex jm-between">
          <div className="jm-row-flex">
            <span className="jm-title">🌐 jiten.moe Media Integration</span>
            <span className="jm-tag">jiten.moe</span>
          </div>
          {jitenUrl && <a className="jm-link" href={jitenUrl} target="_blank" rel="noreferrer">Open Deck <ExternalLink size={11} /></a>}
        </div>

        <div className="jm-inset jm-row-flex jm-between">
          <div>
            <div className="jm-title" style={{ fontSize: 12 }}>
              Vocabulary Deck:{' '}
              {hasDeck ? <span className="jm-ok">✓ {book.deckCsvFileName || 'deck.csv'} attached</span> : <span className="jm-warn">⚠ no deck.csv in book folder</span>}
            </div>
            <div className="jm-muted">
              {hasDeck
                ? 'The enrichment pipeline and study packs use this deck for vocabulary and pitch data.'
                : 'Download the vocabulary deck to enable frequency data, definitions and pitch accent.'}
            </div>
          </div>
          {deckIdFromUrl(jitenUrl) ? (
            <button className="jm-btn jm-primary" disabled={busy} onClick={() => download()}>
              {busy ? <Loader2 size={13} className="animate-spin" /> : <Download size={13} />}
              {hasDeck ? 'Re-download CSV deck' : '1-click download & attach deck'}
            </button>
          ) : (
            !hasDeck && <span className="jm-muted"><i>Search below to 1-click install</i></span>
          )}
        </div>

        <form className="jm-row-flex" onSubmit={search}>
          <select className="jm-select" value={mediaType} onChange={(e) => setMediaType(parseInt(e.target.value, 10))}>
            <option value={4}>Novels / Light Novels</option>
            <option value={0}>All Formats</option>
            <option value={1}>Manga</option>
            <option value={2}>Visual Novels</option>
            <option value={3}>Anime</option>
          </select>
          <input className="jm-input" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search series or book title on jiten.moe..." />
          <button type="submit" className="jm-btn jm-primary" disabled={searching}>
            <Search size={13} /> {searching ? 'Searching...' : 'Search'}
          </button>
        </form>

        <Status status={status} />

        {results.length > 0 && (
          <div className="jm-results">
            {results.map((item) => (
              <SearchResult key={item.deckId} item={item} busy={busy} onDownload={download} onApply={apply} rpc={call} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- series matcher modal

function SeriesMatcher({ seriesName, onClose }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const [loading, setLoading] = useState(false);
  const [match, setMatch] = useState(null);
  const [query, setQuery] = useState(seriesName);
  const [searching, setSearching] = useState(false);
  const [results, setResults] = useState([]);
  const [importTags, setImportTags] = useState(true);
  const [updateSynopsis, setUpdateSynopsis] = useState(true);
  const [working, setWorking] = useState(false);
  const [status, setStatus] = useState(null);

  const load = async (deckId) => {
    setLoading(true);
    setStatus(null);
    try {
      const res = await call('matchSeries', { seriesName, deckId });
      if (res?.found) setMatch(res);
      else {
        setMatch(null);
        setStatus({ text: res?.message || `No matching franchise found on jiten.moe for "${seriesName}". Try searching.` });
      }
    } catch (err) {
      setStatus({ type: 'error', text: `Failed to match on jiten.moe: ${errMsg(err)}` });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, [seriesName]);

  const search = async () => {
    if (!query.trim()) return;
    setSearching(true);
    try {
      const res = await call('search', { query: query.trim() });
      setResults(res?.items || []);
      if (!res?.items?.length) setStatus({ text: `No results found on jiten.moe for "${query.trim()}".` });
    } catch (err) {
      setStatus({ type: 'error', text: `Search failed: ${errMsg(err)}` });
    } finally {
      setSearching(false);
    }
  };

  const mappings = match?.mappings || [];
  const parent = match?.parentDeck;

  const applyMapping = async () => {
    setWorking(true);
    try {
      const res = await call('applySeriesMapping', { mappings, tags: importTags ? match.availableTags || [] : [], updateSynopsis });
      setStatus({ type: 'success', text: `Updated ${res?.updatedCount ?? mappings.length} volumes with jiten.moe metadata.` });
      await refreshLibrary();
    } catch (err) {
      setStatus({ type: 'error', text: `Failed to apply metadata: ${errMsg(err)}` });
    } finally {
      setWorking(false);
    }
  };

  const downloadAll = async () => {
    const downloads = mappings.filter((m) => m.matchedDeckId && m.bookId).map((m) => ({ bookId: m.bookId, deckId: m.matchedDeckId }));
    if (downloads.length === 0) {
      setStatus({ type: 'error', text: 'No mapped sub-decks with valid deck ids to download.' });
      return;
    }
    setWorking(true);
    setStatus({ text: `Downloading ${downloads.length} deck CSV files from jiten.moe...` });
    try {
      const res = await call('downloadSeriesDecksCsv', { downloads });
      const failed = (res?.results || []).filter((r) => r.error);
      setStatus({
        type: failed.length ? 'error' : 'success',
        text: `Downloaded ${res?.downloadedCount ?? 0} deck.csv files${failed.length ? `; ${failed.length} failed (${failed[0].error})` : ''}.`,
      });
      await refreshLibrary();
    } catch (err) {
      setStatus({ type: 'error', text: `Download failed: ${errMsg(err)}` });
    } finally {
      setWorking(false);
    }
  };

  return (
    <div className="jm-root" style={{ maxWidth: 'none' }}>
      <div className="jm-row-flex">
        <input
          className="jm-input"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && search()}
          placeholder="Search series or novel title on jiten.moe..."
        />
        <button className="jm-btn jm-primary" onClick={search} disabled={searching}>
          {searching ? <Loader2 size={13} className="animate-spin" /> : <Search size={13} />} Search
        </button>
      </div>

      <Status status={status} />

      {results.length > 0 && (
        <div className="jm-inset">
          <div className="jm-muted" style={{ marginBottom: 6 }}>Search results ({results.length}). Click one to match this series against it:</div>
          <div className="jm-grid2">
            {results.map((item) => (
              <div
                key={item.deckId}
                className="jm-result jm-row-flex"
                style={{ flexDirection: 'row', cursor: 'pointer', padding: 8 }}
                onClick={() => {
                  setResults([]);
                  load(item.deckId);
                }}
              >
                {item.coverUrl && <img className="jm-cover" style={{ width: 32, height: 44 }} src={item.coverUrl} alt="" />}
                <div style={{ minWidth: 0 }}>
                  <div className="jm-title" style={{ fontSize: 11 }}>{item.originalTitle || item.romajiTitle || item.englishTitle}</div>
                  <div className="jm-muted" style={{ fontSize: 10 }}>Deck #{item.deckId}{item.difficulty ? ` • ★ ${item.difficulty.toFixed(1)}` : ''}</div>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {loading ? (
        <div className="jm-row-flex jm-muted" style={{ justifyContent: 'center', padding: 40 }}>
          <Loader2 size={18} className="animate-spin" /> Querying jiten.moe franchise and sub-decks...
        </div>
      ) : parent ? (
        <>
          <div className="jm-card-box" style={{ flexDirection: 'row', alignItems: 'flex-start' }}>
            {parent.coverUrl && <img className="jm-cover jm-cover-lg" src={parent.coverUrl} alt="" />}
            <div style={{ flex: 1, minWidth: 0 }}>
              <div className="jm-row-flex jm-between">
                <span className="jm-title">{parent.originalTitle || parent.romajiTitle || seriesName}</span>
                {parent.jitenUrl && <a className="jm-link" href={parent.jitenUrl} target="_blank" rel="noreferrer">View on jiten.moe <ExternalLink size={11} /></a>}
              </div>
              {parent.romajiTitle && parent.romajiTitle !== parent.originalTitle && <div className="jm-muted"><i>{parent.romajiTitle}</i></div>}
              <div className="jm-row-flex jm-wrap" style={{ marginTop: 6 }}>
                {parent.difficulty != null && <span className={`jm-badge ${diffClass(parent.difficulty)}`}>★ {parent.difficulty.toFixed(2)}</span>}
                <span className="jm-muted">{match.subDecksCount || 0} volume sub-decks</span>
                <span className="jm-muted">{mappings.filter((m) => m.matchedDeckId).length}/{mappings.length} local volumes matched</span>
              </div>
              {match.availableTags?.length > 0 && (
                <div className="jm-row-flex jm-wrap" style={{ marginTop: 6, gap: 4 }}>
                  {match.availableTags.slice(0, 8).map((t) => <span key={t} className="jm-chip">{t}</span>)}
                  {match.availableTags.length > 8 && <span className="jm-muted">+{match.availableTags.length - 8} more</span>}
                </div>
              )}
            </div>
          </div>

          <div style={{ border: '1px solid var(--border-color)', borderRadius: 8, overflow: 'hidden', maxHeight: '22rem', overflowY: 'auto' }}>
            <table className="jm-table">
              <thead>
                <tr><th>Vol #</th><th>Local book</th><th>Matched jiten.moe sub-deck</th><th>Diff</th><th>Chars</th><th>Status</th></tr>
              </thead>
              <tbody>
                {mappings.map((m, i) => (
                  <tr key={m.bookId || i}>
                    <td className="jm-mono">#{m.seriesIndex ?? i + 1}</td>
                    <td title={m.title}>{m.title}</td>
                    <td className="jm-muted">{m.matchedTitle || <i>No sub-deck match</i>}</td>
                    <td>{m.difficulty ? <span className="jm-warn">★ {m.difficulty.toFixed(1)}</span> : '—'}</td>
                    <td className="jm-muted">{m.characterCount ? m.characterCount.toLocaleString() : '—'}</td>
                    <td>{m.matchedDeckId ? <span className="jm-pill-ok">✓ Mapped</span> : <span className="jm-pill-miss">Unmatched</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="jm-inset jm-row-flex jm-between">
            <label className="jm-row-flex"><input type="checkbox" checked={importTags} onChange={(e) => setImportTags(e.target.checked)} /> Import jiten.moe tags and genres as book tags</label>
            <label className="jm-row-flex"><input type="checkbox" checked={updateSynopsis} onChange={(e) => setUpdateSynopsis(e.target.checked)} /> Fill missing descriptions from the jiten.moe synopsis</label>
          </div>

          <div className="jm-row-flex" style={{ justifyContent: 'flex-end' }}>
            <button className="jm-btn" onClick={onClose}>Close</button>
            <button className="jm-btn" disabled={working} onClick={downloadAll}>
              {working ? <Loader2 size={13} className="animate-spin" /> : <Download size={13} />} Download all decks (.csv)
            </button>
            <button className="jm-btn jm-primary" disabled={working} onClick={applyMapping}>
              {working ? <Loader2 size={13} className="animate-spin" /> : <Sparkles size={13} />} Apply mapping &amp; sync
            </button>
          </div>
        </>
      ) : (
        <div className="jm-muted" style={{ textAlign: 'center', padding: 32 }}>
          No direct jiten.moe match for “{seriesName}”. Use the search bar to look the franchise up by romaji or English title.
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- activation

export function activate(ctx) {
  ctx.registerBookInfoTab({ id: 'jiten', title: 'jiten.moe', icon: Globe, component: BookTab });
  ctx.registerBookBadge({ id: 'difficulty', component: DifficultyBadge });
  ctx.registerSeriesAction({
    id: 'match-series',
    label: 'Match on jiten.moe',
    title: 'Match this series against jiten.moe',
    icon: Sparkles,
    onClick: ({ seriesName, openModal }) =>
      openModal({
        id: 'jiten-series-matcher',
        title: `jiten.moe series matcher: ${seriesName}`,
        size: 'xl',
        component: ({ onClose }) => <SeriesMatcher seriesName={seriesName} onClose={onClose} />,
      }),
  });
}
