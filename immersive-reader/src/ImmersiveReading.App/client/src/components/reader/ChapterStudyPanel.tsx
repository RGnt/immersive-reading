import React, { useState, useMemo } from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { useStudyPackStore } from '../../stores/useStudyPackStore';
import {
  X,
  Search,
  BookOpen,
  Languages,
  Sparkles,
  BookMarked,
  Loader2,
  ExternalLink,
} from 'lucide-react';

export const ChapterStudyPanel: React.FC = () => {
  const {
    isChapterStudyOpen,
    closeChapterStudy,
    chapterInsights,
    isLoadingChapterInsights,
    currentChapterIndex,
    chapters,
  } = useReaderStore();

  const [activeTab, setActiveTab] = useState<'vocab' | 'kanji' | 'grammar'>('vocab');
  const [filterQuery, setFilterQuery] = useState('');

  const currentChapter = chapters[currentChapterIndex] || {
    title: `Chapter ${currentChapterIndex + 1}`,
  };

  const vocabList = chapterInsights?.vocabulary || [];
  const kanjiList = chapterInsights?.kanji || [];
  const grammarList = chapterInsights?.grammar || [];

  const filteredVocab = useMemo(() => {
    if (!filterQuery.trim()) return vocabList;
    const q = filterQuery.toLowerCase().trim();
    return vocabList.filter(
      (v) =>
        v.word.toLowerCase().includes(q) ||
        v.lemma.toLowerCase().includes(q) ||
        (v.reading && v.reading.toLowerCase().includes(q)) ||
        (v.definition && v.definition.toLowerCase().includes(q))
    );
  }, [vocabList, filterQuery]);

  const filteredKanji = useMemo(() => {
    if (!filterQuery.trim()) return kanjiList;
    const q = filterQuery.toLowerCase().trim();
    return kanjiList.filter(
      (k) =>
        k.character.includes(q) ||
        (k.codepointHex && k.codepointHex.toLowerCase().includes(q)) ||
        (k.words && k.words.some((w) => w.toLowerCase().includes(q))) ||
        (k.meanings && k.meanings.some((m) => m.toLowerCase().includes(q)))
    );
  }, [kanjiList, filterQuery]);

  const filteredGrammar = useMemo(() => {
    if (!filterQuery.trim()) return grammarList;
    const q = filterQuery.toLowerCase().trim();
    return grammarList.filter(
      (g) =>
        g.label.toLowerCase().includes(q) ||
        g.meaning.toLowerCase().includes(q) ||
        (g.examples && g.examples.some((ex) => ex.toLowerCase().includes(q)))
    );
  }, [grammarList, filterQuery]);

  if (!isChapterStudyOpen) return null;

  return (
    <div
      className="chapter-study-panel w-96 h-full border-l border-[var(--border-color)] bg-[var(--bg-surface)] flex flex-col z-20 shadow-2xl transition-all select-none animate-in slide-in-from-right duration-200"
      style={{ minWidth: '340px', maxWidth: '420px' }}
    >
      {/* HEADER */}
      <div className="flex items-center justify-between p-3 border-b border-[var(--border-color)] bg-black/10">
        <div className="flex items-center space-x-2 min-w-0">
          <BookOpen className="w-4 h-4 text-emerald-400 shrink-0" />
          <div className="min-w-0">
            <h3 className="font-semibold text-xs text-[var(--text-primary)] truncate">
              Chapter {currentChapterIndex + 1} Insights
            </h3>
            <p className="text-[10px] text-[var(--text-muted)] truncate">
              {currentChapter.title || `Chapter ${currentChapterIndex + 1}`}
            </p>
          </div>
        </div>
        <button
          onClick={closeChapterStudy}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] transition-colors"
          title="Close Chapter Panel (Esc)"
        >
          <X className="w-4 h-4" />
        </button>
      </div>

      {/* TABS HEADER */}
      <div className="flex items-center border-b border-[var(--border-color)] bg-[var(--bg-primary)] text-xs">
        <button
          onClick={() => {
            setActiveTab('vocab');
            setFilterQuery('');
          }}
          className={`flex-1 py-2 px-2 text-center font-medium border-b-2 flex items-center justify-center space-x-1.5 transition-colors ${
            activeTab === 'vocab'
              ? 'border-emerald-500 text-emerald-400 font-semibold bg-emerald-500/5'
              : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
          }`}
        >
          <Languages className="w-3.5 h-3.5" />
          <span>Vocab ({vocabList.length})</span>
        </button>

        <button
          onClick={() => {
            setActiveTab('kanji');
            setFilterQuery('');
          }}
          className={`flex-1 py-2 px-2 text-center font-medium border-b-2 flex items-center justify-center space-x-1.5 transition-colors ${
            activeTab === 'kanji'
              ? 'border-teal-500 text-teal-400 font-semibold bg-teal-500/5'
              : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
          }`}
        >
          <Sparkles className="w-3.5 h-3.5" />
          <span>Kanji ({kanjiList.length})</span>
        </button>

        <button
          onClick={() => {
            setActiveTab('grammar');
            setFilterQuery('');
          }}
          className={`flex-1 py-2 px-2 text-center font-medium border-b-2 flex items-center justify-center space-x-1.5 transition-colors ${
            activeTab === 'grammar'
              ? 'border-indigo-500 text-indigo-400 font-semibold bg-indigo-500/5'
              : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
          }`}
        >
          <BookMarked className="w-3.5 h-3.5" />
          <span>Grammar ({grammarList.length})</span>
        </button>
      </div>

      {/* SEARCH / FILTER BAR */}
      <div className="p-2 border-b border-[var(--border-color)] bg-[var(--bg-surface)]">
        <div className="relative">
          <Search className="w-3.5 h-3.5 absolute left-2.5 top-2 text-[var(--text-muted)]" />
          <input
            type="text"
            value={filterQuery}
            onChange={(e) => setFilterQuery(e.target.value)}
            placeholder={`Filter ${activeTab} in this chapter...`}
            className="w-full pl-8 pr-3 py-1 bg-[var(--bg-primary)] border border-[var(--border-color)] rounded text-xs text-[var(--text-primary)] placeholder-[var(--text-muted)] focus:outline-none focus:border-[var(--accent-color)]"
          />
          {filterQuery && (
            <button
              onClick={() => setFilterQuery('')}
              className="absolute right-2 top-1.5 text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            >
              ✕
            </button>
          )}
        </div>
      </div>

      {/* BODY CONTENT */}
      <div className="flex-1 overflow-y-auto p-2.5 space-y-2 select-text">
        {isLoadingChapterInsights ? (
          <div className="h-64 flex flex-col items-center justify-center space-y-2 text-[var(--text-muted)] select-none">
            <Loader2 className="w-6 h-6 animate-spin text-emerald-400" />
            <span className="text-xs">Extracting chapter insights...</span>
          </div>
        ) : activeTab === 'vocab' ? (
          filteredVocab.length === 0 ? (
            <div className="p-8 text-center text-xs text-[var(--text-muted)] select-none">
              {vocabList.length === 0
                ? 'No vocabulary extracted for this chapter yet.'
                : 'No matching vocabulary found.'}
            </div>
          ) : (
            filteredVocab.map((item, idx) => (
              <div
                key={idx}
                className="p-2.5 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-emerald-500/50 transition-colors space-y-1.5 group"
              >
                <div className="flex items-start justify-between">
                  <div>
                    <div className="flex items-baseline space-x-1.5">
                      <span className="font-bold text-sm text-[var(--text-primary)] font-japanese">
                        {item.word || item.lemma}
                      </span>
                      {item.reading && item.reading !== item.word && (
                        <span className="text-xs text-emerald-400 font-mono">
                          【{item.reading}】
                        </span>
                      )}
                    </div>
                    {item.lemma && item.lemma !== item.word && (
                      <div className="text-[10px] text-[var(--text-muted)]">
                        Base: {item.lemma}
                      </div>
                    )}
                  </div>

                  <div className="flex items-center space-x-1 shrink-0">
                    {item.partOfSpeech && (
                      <span className="text-[9px] px-1.5 py-0.5 rounded bg-[var(--bg-surface)] border border-[var(--border-color)] text-[var(--text-muted)] font-medium">
                        {item.partOfSpeech}
                      </span>
                    )}
                    <span className="text-[9px] font-bold px-1.5 py-0.5 rounded bg-emerald-500/10 text-emerald-300 border border-emerald-500/30">
                      ×{item.occurrences}
                    </span>
                  </div>
                </div>

                {item.definition && (
                  <p className="text-[11px] text-[var(--text-primary)] leading-snug line-clamp-2">
                    {item.definition}
                  </p>
                )}

                {item.sampleSentence && (
                  <div className="text-[10px] text-[var(--text-muted)] bg-black/20 p-1.5 rounded border border-white/5 font-japanese leading-relaxed italic">
                    "{item.sampleSentence}"
                  </div>
                )}
              </div>
            ))
          )
        ) : activeTab === 'kanji' ? (
          filteredKanji.length === 0 ? (
            <div className="p-8 text-center text-xs text-[var(--text-muted)] select-none">
              {kanjiList.length === 0
                ? 'No kanji extracted for this chapter.'
                : 'No matching kanji found.'}
            </div>
          ) : (
            <div className="grid grid-cols-2 gap-2">
              {filteredKanji.map((item, idx) => (
                <div
                  key={idx}
                  className="p-2.5 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-teal-500/50 transition-colors flex flex-col justify-between space-y-1.5"
                >
                  <div className="flex items-start justify-between">
                    <span className="text-2xl font-bold text-teal-400 font-japanese leading-none">
                      {item.character}
                    </span>
                    <div className="flex items-center space-x-1">
                      {item.firstAppearance && (
                        <span className="text-[9px] font-semibold px-1 py-0.5 rounded bg-amber-500/10 text-amber-300 border border-amber-500/30" title="First appearance in this book">
                          NEW
                        </span>
                      )}
                      <span className="text-[9px] font-bold px-1.5 py-0.5 rounded bg-teal-500/10 text-teal-300 border border-teal-500/30">
                        ×{item.occurrences}
                      </span>
                    </div>
                  </div>

                  <div className="flex items-center space-x-2 text-[10px] text-[var(--text-muted)] font-mono">
                    <span>U+{item.codepointHex || '0000'}</span>
                    {item.strokeCount && item.strokeCount > 0 ? (
                      <span>• {item.strokeCount} strokes</span>
                    ) : null}
                  </div>

                  {item.meanings && item.meanings.length > 0 && (
                    <div className="text-[11px] text-[var(--text-primary)] font-medium line-clamp-2">
                      {item.meanings.join(', ')}
                    </div>
                  )}

                  {((item.onyomi && item.onyomi.length > 0) || (item.kunyomi && item.kunyomi.length > 0)) && (
                    <div className="text-[10px] space-y-0.5 pt-1 border-t border-[var(--border-color)]">
                      {item.onyomi && item.onyomi.length > 0 && (
                        <div className="text-teal-300/80 truncate">
                          <span className="text-[9px] text-[var(--text-muted)] mr-1">音:</span>
                          {item.onyomi.join('、')}
                        </div>
                      )}
                      {item.kunyomi && item.kunyomi.length > 0 && (
                        <div className="text-teal-200/80 truncate">
                          <span className="text-[9px] text-[var(--text-muted)] mr-1">訓:</span>
                          {item.kunyomi.join('、')}
                        </div>
                      )}
                    </div>
                  )}

                  {item.words && item.words.length > 0 && (
                    <div className="pt-1 border-t border-[var(--border-color)]">
                      <span className="text-[9px] text-[var(--text-muted)] block mb-0.5">
                        In chapter:
                      </span>
                      <div className="flex flex-wrap gap-1">
                        {item.words.map((w, wIdx) => (
                          <span
                            key={wIdx}
                            className="text-[10px] px-1 py-0.5 rounded bg-[var(--bg-surface)] text-[var(--text-primary)] font-japanese"
                          >
                            {w}
                          </span>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )
        ) : (
          filteredGrammar.length === 0 ? (
            <div className="p-8 text-center text-xs text-[var(--text-muted)] select-none">
              {grammarList.length === 0
                ? 'No grammar patterns detected in this chapter.'
                : 'No matching grammar found.'}
            </div>
          ) : (
            filteredGrammar.map((item, idx) => (
              <div
                key={idx}
                className="p-2.5 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-indigo-500/50 transition-colors space-y-1.5"
              >
                <div className="flex items-center justify-between">
                  <span className="font-bold text-xs text-indigo-400 font-japanese">
                    {item.label}
                  </span>
                  <span className="text-[9px] font-bold px-1.5 py-0.5 rounded bg-indigo-500/10 text-indigo-300 border border-indigo-500/30">
                    ×{item.occurrences}
                  </span>
                </div>

                {item.meaning && (
                  <p className="text-[11px] text-[var(--text-primary)] leading-snug">
                    {item.meaning}
                  </p>
                )}

                {item.examples && item.examples.length > 0 && (
                  <div className="text-[10px] text-[var(--text-muted)] bg-black/20 p-1.5 rounded border border-white/5 font-japanese leading-relaxed italic">
                    "{item.examples[0]}"
                  </div>
                )}
              </div>
            ))
          )
        )}
      </div>

      {/* FOOTER */}
      <div className="p-2.5 border-t border-[var(--border-color)] bg-black/10 flex items-center justify-between text-[10px] text-[var(--text-muted)]">
        <span>Chapter {currentChapterIndex + 1} of {chapters.length}</span>
        <button
          onClick={() => {
            const { bookDetail } = useReaderStore.getState();
            if (bookDetail) useStudyPackStore.getState().openDrawer(bookDetail.summary.bookId);
          }}
          className="flex items-center space-x-1 text-emerald-400 hover:underline"
        >
          <span>Open Full Study Pack</span>
          <ExternalLink className="w-3 h-3" />
        </button>
      </div>
    </div>
  );
};
