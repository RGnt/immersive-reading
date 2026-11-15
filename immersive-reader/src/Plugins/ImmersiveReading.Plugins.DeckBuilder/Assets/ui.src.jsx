// Deck Builder plugin UI. Source for Assets/ui.js (build with `npm run build:plugins` in client/).
// The deck-building method is adapted from jiten.moe (https://github.com/Sirush/Jiten, Apache-2.0); see NOTICE-Jiten.md.
import React, { useState, useEffect, useRef } from 'react';
import { Layers, Loader2, Hammer, BarChart3, Sparkles } from 'lucide-react';
import { usePluginRpc, usePluginConfig, refreshLibrary } from '@ir/sdk';

const PLUGIN_ID = 'deckBuilder';
const errMsg = (e) => (e && e.message) || String(e);
const fmt = (n) => (n ?? 0).toLocaleString();

function Status({ status }) {
  if (!status) return null;
  const cls = status.type === 'success' ? 'db-success' : status.type === 'error' ? 'db-error' : '';
  return <div className={`db-status ${cls}`}>{status.text}</div>;
}

function Stat({ label, value }) {
  return (
    <div className="db-stat">
      <b>{value}</b>
      <span>{label}</span>
    </div>
  );
}

// Example sentences mark the target with **word**.
function Example({ text }) {
  if (!text) return <span className="db-muted">—</span>;
  const parts = text.split('**');
  return (
    <span className="db-ex">
      {parts.map((p, i) => (i % 2 === 1 ? <b key={i}>{p}</b> : <React.Fragment key={i}>{p}</React.Fragment>))}
    </span>
  );
}

// ---------------------------------------------------------------- LLM enrichment panel

function EnrichmentPanel({ bookId, call, onChanged }) {
  const [est, setEst] = useState(null);
  const [st, setSt] = useState(null);
  const [error, setError] = useState(null);
  const [open, setOpen] = useState(false);

  const refresh = async () => {
    const s = await call('getEnrichmentStatus', { bookId, limit: 40 });
    setSt(s);
    return s;
  };

  useEffect(() => {
    setEst(null);
    setSt(null);
    setError(null);
    (async () => {
      try {
        setEst(await call('estimateEnrichment', { bookId }));
        await refresh();
      } catch (e) {
        setError(errMsg(e));
      }
    })();
  }, [bookId]);

  useEffect(() => {
    if (st?.state !== 'running') return undefined;
    const timer = setInterval(async () => {
      try {
        const s = await refresh();
        if (s.state !== 'running') onChanged?.();
      } catch {
        /* keep polling: a transient RPC failure must not stop the progress display */
      }
    }, 1500);
    return () => clearInterval(timer);
  }, [st?.state]);

  const start = async () => {
    setError(null);
    try {
      await call('startEnrichment', { bookId });
      await refresh();
    } catch (e) {
      setError(errMsg(e));
    }
  };
  const cancel = async () => {
    try {
      await call('cancelEnrichment', { bookId });
      await refresh();
    } catch (e) {
      setError(errMsg(e));
    }
  };

  const running = st?.state === 'running';
  const done = st && (st.state === 'done' || st.grammarPointCount > 0 || st.functionWordNoteCount > 0);
  const tokensK = est ? Math.max(1, Math.round(est.approxInputTokens / 1000)) : 0;

  return (
    <div className="db-box">
      <div className="db-row db-between">
        <div className="db-row">
          <span className="db-title">✨ Improve the deck with an LLM</span>
          <span className="db-tag">optional</span>
        </div>
        {est && (est.configured
          ? <span className="db-muted">{est.model || 'loaded model'} @ {est.endpointHost || 'endpoint'} ({est.source === 'plugin' ? 'plugin endpoint' : 'Settings → AI'})</span>
          : <span className="db-warn">no LLM configured</span>)}
      </div>
      <div className="db-muted">
        Reads the book chapter by chapter and writes (1) the <b>grammar used in each chapter</b> and (2) a <b>usage note for
        every particle and auxiliary</b>. Particles and auxiliaries are already marked in the deck from the tokenizer, so they print
        in their own section, apart from the vocabulary. Every example is a real sentence of the book: anything the model cites that
        cannot be found verbatim is discarded. Story sentences are never translated.
      </div>

      {est && !est.configured && (
        <div className="db-status db-error">Set an LLM endpoint in this plugin's settings (Settings → Installed Plugins → Deck Builder → LLM endpoint, for example your FreeToken URL) or the endpoint URL, model and key in Settings → AI first.</div>
      )}
      {est && (
        <div className="db-muted">
          {est.chapters} chapters · {fmt(est.characters)} characters → <b>{est.requests}</b> request{est.requests === 1 ? '' : 's'}, about{' '}
          <b>{fmt(tokensK)}k</b> input tokens in total. This is the only step that sends book text to the model, and only when you press the button.
          {est.existing && <> Last run: {est.existing.grammarPoints} grammar points, {est.existing.functionWordNotes} function words ({est.existing.model}).</>}
        </div>
      )}

      <div className="db-row">
        {!running ? (
          <button className="db-btn db-primary" onClick={start} disabled={!est?.configured}>
            <Sparkles size={13} /> {done ? 'Run again' : 'Improve deck with LLM'}
          </button>
        ) : (
          <button className="db-btn" onClick={cancel}>Cancel</button>
        )}
        {running && (
          <span className="db-row db-muted">
            <Loader2 size={13} className="animate-spin" /> {st.done} / {st.total} requests{st.chapter ? ` · ${st.chapter}` : ''}
          </span>
        )}
      </div>
      {running && (
        <div className="db-muted">
          So far: <b>{st.grammarPointCount}</b> grammar points · <b>{st.functionWordNoteCount}</b> particles / auxiliaries with usage notes
          {st.chunksFailed > 0 && <span className="db-warn"> · {st.chunksFailed} request{st.chunksFailed === 1 ? '' : 's'} failed</span>}
          {st.warningCount > 0 && <span className="db-warn"> · {st.warningCount} warning{st.warningCount === 1 ? '' : 's'}</span>}
          {st.warnings?.length > 0 && <div className="db-warn">Latest: {st.warnings[st.warnings.length - 1]}</div>}
        </div>
      )}

      {st?.state === 'failed' && <div className="db-status db-error">Enrichment failed: {st.error}</div>}
      {done && !running && st.warningCount > 0 && st.grammarPointCount === 0 && st.functionWordNoteCount === 0 && (
        <div className="db-status db-error">The run finished but produced nothing usable. Latest warning: {st.warnings?.[0] || 'see the warnings below'}</div>
      )}
      {st?.state === 'cancelled' && <div className="db-status">Cancelled. Nothing was saved from this run.</div>}
      {error && <div className="db-status db-error">{error}</div>}

      {done && !running && (
        <div className="db-inset">
          <div className="db-row db-between">
            <span><b>{st.grammarPointCount}</b> grammar points · <b>{st.functionWordNoteCount}</b> particles / auxiliaries with usage notes</span>
            <button className="db-btn" onClick={() => setOpen((o) => !o)}>{open ? 'Hide' : 'Show'} results</button>
          </div>
          {st.warningCount > 0 && (
            <div className="db-warn" style={{ marginTop: 6 }}>
              {st.warningCount} item{st.warningCount === 1 ? '' : 's'} skipped (unverifiable examples or failed requests){st.warnings?.length ? `: ${st.warnings[0]}` : ''}
            </div>
          )}
          {open && (
            <>
              <div className="db-scroll" style={{ marginTop: 8 }}>
                <table className="db-table">
                  <thead><tr><th>Pattern</th><th>Meaning</th><th>Chapter</th><th>Example</th></tr></thead>
                  <tbody>
                    {(st.points || []).map((g) => (
                      <tr key={g.id}>
                        <td><b>{g.pattern}</b><div className="db-muted">{g.category}</div></td>
                        <td>{g.meaning}<div className="db-muted">{g.explanation}</div></td>
                        <td className="db-muted">{g.chapter}</td>
                        <td>{g.examples?.[0] ? <Example text={g.examples[0].text.replace(g.examples[0].surface, `**${g.examples[0].surface}**`)} /> : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="db-scroll" style={{ marginTop: 8 }}>
                <table className="db-table">
                  <thead><tr><th>Particle / auxiliary</th><th>Uses in this book</th></tr></thead>
                  <tbody>
                    {(st.functionUses || []).map((f) => (
                      <tr key={f.word}>
                        <td><b>{f.word}</b></td>
                        <td>{f.uses.map((u, i) => <div key={i}><b>{u.function}</b>: {u.note}</div>)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
          <div className="db-muted" style={{ marginTop: 6 }}>
            The results are saved. “Build &amp; attach” writes them as <code>deck-grammar.json</code> next to the deck; the study pack then adds
            the grammar to the grammar master and the usage notes to the particles &amp; function words sheet.
            Explanations are model output: review them before relying on them.
          </div>
        </div>
      )}
    </div>
  );
}

function DeckBuilderTab({ bookId, book }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const { config } = usePluginConfig(PLUGIN_ID);
  const hasDeck = !!book?.hasDeckCsv;

  const [order, setOrder] = useState(config?.order || 'chronological');
  const [selection, setSelection] = useState('full');
  const [selectionValue, setSelectionValue] = useState(1000);
  const [excludeKana, setExcludeKana] = useState(!!config?.excludeKana);
  const [excludeExamples, setExcludeExamples] = useState(!!config?.excludeExampleSentences);
  const [contentOnly, setContentOnly] = useState(!!config?.contentWordsOnly);
  const [format, setFormat] = useState('csv');
  const [excludeFunction, setExcludeFunction] = useState(!!config?.excludeFunctionWords);
  const [previewTick, setPreviewTick] = useState(0);

  const [stats, setStats] = useState(null);
  const [preview, setPreview] = useState(null);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState(null);
  const [confirmOverwrite, setConfirmOverwrite] = useState(false);
  const analyzedFor = useRef(null);

  const valueNeeded = selection !== 'full';
  const valueLabel = selection === 'topN' ? 'Number of words' : selection === 'targetCoverage' ? 'Coverage %' : 'Min. occurrences';

  const params = () => ({
    bookId,
    order,
    selection,
    selectionValue: valueNeeded ? Number(selectionValue) || 0 : 0,
    excludeKana,
    excludeExampleSentences: excludeExamples,
    excludeFunctionWords: excludeFunction,
    includeGrammar: true,
    contentWordsOnly: contentOnly,
    format,
  });

  useEffect(() => {
    setStats(null);
    setPreview(null);
    setStatus(null);
    setConfirmOverwrite(false);
    analyzedFor.current = null;
  }, [bookId]);

  // Re-analyse when "content words only" changes (it changes which words exist); the rest only re-filters the cached build.
  useEffect(() => {
    if (analyzedFor.current !== null && analyzedFor.current !== contentOnly) {
      setStats(null);
      setPreview(null);
    }
  }, [contentOnly]);

  // Live preview of the selection once the book has been analysed.
  useEffect(() => {
    if (!stats) return;
    let cancelled = false;
    const t = setTimeout(async () => {
      try {
        const res = await call('previewDeck', { ...params(), limit: 60 });
        if (!cancelled) setPreview(res);
      } catch (err) {
        if (!cancelled) setStatus({ type: 'error', text: `Preview failed: ${errMsg(err)}` });
      }
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(t);
    };
  }, [stats, order, selection, selectionValue, excludeKana, excludeExamples, excludeFunction, format, previewTick]);

  const analyze = async () => {
    setBusy(true);
    setStatus({ text: 'Reading the book and counting words. Long novels can take up to a minute...' });
    try {
      const res = await call('analyzeBook', { bookId, contentWordsOnly: contentOnly, refresh: true });
      setStats(res.stats);
      analyzedFor.current = contentOnly;
      setStatus(null);
    } catch (err) {
      setStatus({ type: 'error', text: `Analysis failed: ${errMsg(err)}` });
    } finally {
      setBusy(false);
    }
  };

  const build = async (overwrite) => {
    setBusy(true);
    setStatus({ text: 'Writing the deck next to the book...' });
    try {
      const res = await call('exportDeck', { ...params(), overwrite });
      if (res.exists && !res.ok) {
        setConfirmOverwrite(true);
        setStatus({ type: 'error', text: `${res.path} already exists.` });
        return;
      }
      setConfirmOverwrite(false);
      setStatus({
        type: 'success',
        text: `Wrote ${res.fileName}: ${fmt(res.wordCount)} words (${Math.round((res.fileSizeBytes || 0) / 1024)} KB) to the book folder.`,
      });
      await refreshLibrary(bookId);
    } catch (err) {
      setStatus({ type: 'error', text: `Build failed: ${errMsg(err)}` });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="db-root">
      <div className="db-box">
        <div className="db-row db-between">
          <div className="db-row">
            <span className="db-title">🧱 Build a vocabulary deck from this book</span>
            <span className="db-tag">local</span>
          </div>
          <span className="db-muted">{hasDeck ? <span className="db-ok">✓ {book.deckCsvFileName || 'deck.csv'} attached</span> : <span className="db-warn">⚠ no deck yet</span>}</span>
        </div>
        <div className="db-muted">
          Counts every word of the book (dictionary form, in order of first appearance), looks each one up in your
          imported dictionaries and writes <code>deck.csv</code> next to the book. Nothing is sent over the network.
        </div>

        <div className="db-row db-wrap" style={{ alignItems: 'flex-end' }}>
          <div className="db-field">
            <label>Word order</label>
            <select className="db-select" value={order} onChange={(e) => setOrder(e.target.value)}>
              <option value="chronological">Chronological (first appearance)</option>
              <option value="deckFrequency">Deck frequency (most used first)</option>
              <option value="globalFrequency">Global frequency (most common first)</option>
            </select>
          </div>
          <div className="db-field">
            <label>Which words</label>
            <select className="db-select" value={selection} onChange={(e) => setSelection(e.target.value)}>
              <option value="full">Full deck</option>
              <option value="topN">Top N words</option>
              <option value="targetCoverage">Target coverage</option>
              <option value="minOccurrences">Minimum occurrences</option>
            </select>
          </div>
          {valueNeeded && (
            <div className="db-field">
              <label>{valueLabel}</label>
              <input className="db-input" type="number" min="1" value={selectionValue} onChange={(e) => setSelectionValue(e.target.value)} />
            </div>
          )}
          <div className="db-field">
            <label>Format</label>
            <select className="db-select" value={format} onChange={(e) => setFormat(e.target.value)}>
              <option value="csv">CSV (deck.csv)</option>
              <option value="txt">Word list (TXT)</option>
              <option value="txtRepeated">Word list, one line per occurrence</option>
            </select>
          </div>
        </div>

        <div className="db-row db-wrap" style={{ gap: 16 }}>
          <label className="db-row"><input type="checkbox" checked={excludeKana} onChange={(e) => setExcludeKana(e.target.checked)} /> Exclude kana-only words</label>
          <label className="db-row"><input type="checkbox" checked={excludeExamples} onChange={(e) => setExcludeExamples(e.target.checked)} /> No example sentences</label>
          <label className="db-row"><input type="checkbox" checked={contentOnly} onChange={(e) => setContentOnly(e.target.checked)} /> Content words only</label>
          <label className="db-row" title="By default particles and auxiliaries stay in the deck, marked in the Kind column, and print in their own section"><input type="checkbox" checked={excludeFunction} onChange={(e) => setExcludeFunction(e.target.checked)} /> Leave particles &amp; auxiliaries out</label>
        </div>

        <div className="db-row">
          <button className="db-btn" onClick={analyze} disabled={busy}>
            {busy && !stats ? <Loader2 size={13} className="animate-spin" /> : <BarChart3 size={13} />} {stats ? 'Re-analyze book' : 'Analyze book'}
          </button>
          <button className="db-btn db-primary" onClick={() => build(false)} disabled={busy || !stats}>
            {busy && stats ? <Loader2 size={13} className="animate-spin" /> : <Hammer size={13} />} Build &amp; attach {format === 'csv' ? 'deck.csv' : 'deck.txt'}
          </button>
          {confirmOverwrite && (
            <button className="db-btn" onClick={() => build(true)} disabled={busy}>Replace existing file</button>
          )}
        </div>

        <Status status={status} />
      </div>

      <EnrichmentPanel bookId={bookId} call={call} onChanged={() => setPreviewTick((t) => t + 1)} />

      {stats && (
        <div className="db-box">
          <div className="db-stats">
            <Stat label="Characters" value={fmt(stats.characterCount)} />
            <Stat label="Words (total)" value={fmt(stats.wordCount)} />
            <Stat label="Unique words" value={fmt(stats.uniqueWordCount)} />
            <Stat label="Seen once" value={fmt(stats.uniqueWordUsedOnceCount)} />
            <Stat label="Unique kanji" value={fmt(stats.uniqueKanjiCount)} />
            <Stat label="Sentences" value={fmt(stats.sentenceCount)} />
            <Stat label="Dialogue" value={`${stats.dialoguePercentage}%`} />
            <Stat label="Not in dictionary" value={fmt(stats.unresolvedWordCount)} />
            <Stat label="Particles" value={fmt(stats.particleCount)} />
            <Stat label="Auxiliaries" value={fmt(stats.auxiliaryCount)} />
          </div>
          {stats.unresolvedWordCount > 0 && stats.unresolvedWordCount === stats.uniqueWordCount && (
            <div className="db-status db-error">
              No word matched your dictionaries, so definitions will be empty. Import a JMdict-based Yomitan dictionary in Settings first.
            </div>
          )}
        </div>
      )}

      {preview && (
        <div className="db-box">
          <div className="db-row db-between">
            <span className="db-title">Selected: {fmt(preview.selectedCount)} words</span>
            <span className="db-muted">
              covers {preview.totalOccurrences ? ((preview.selectedOccurrences / preview.totalOccurrences) * 100).toFixed(1) : 0}% of all word occurrences
            </span>
          </div>
          <div className="db-scroll">
            <table className="db-table">
              <thead><tr><th>Word</th><th>Kind</th><th>Reading</th><th>×</th><th>Rank</th><th>Definition / usage</th><th>Example</th></tr></thead>
              <tbody>
                {preview.words.map((w, i) => (
                  <tr key={i}>
                    <td className={w.resolved ? '' : 'db-unresolved'}>{w.word}</td>
                    <td>{w.kind && w.kind !== 'vocabulary' ? <span className="db-kind">{w.kind}</span> : <span className="db-muted">word</span>}</td>
                    <td>{w.reading || '—'}</td>
                    <td>{w.occurrences}</td>
                    <td>{w.frequencyRank || '—'}</td>
                    <td className="db-muted">{w.usageNote ? <><b>{w.usageNote}</b><div>{w.definitions}</div></> : (w.definitions || '—')}</td>
                    <td><Example text={w.example} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="db-muted">Showing the first {preview.words.length} of {fmt(preview.selectedCount)}.</div>
        </div>
      )}

      <div className="db-attrib">
        The way decks are built here is adapted from{' '}
        <a href="https://github.com/Sirush/Jiten" target="_blank" rel="noreferrer">jiten.moe (Jiten, Apache-2.0)</a>:
        sentence-by-sentence parsing, dictionary-form de-duplication in first-appearance order, the example-sentence
        selection passes, and the deck.csv column format. This plugin is not affiliated with jiten.moe. See
        NOTICE-Jiten.md in the plugin folder.
      </div>
    </div>
  );
}

export function activate(ctx) {
  ctx.registerBookInfoTab({ id: 'deck-builder', title: 'Deck Builder', icon: Layers, component: DeckBuilderTab });
}
