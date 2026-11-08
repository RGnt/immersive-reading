import React from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { Bookmark, X, Loader2 } from 'lucide-react';

export const DictionaryPopover: React.FC = () => {
  const { popover, closePopover, setPopoverTab, mineFlashcard, updateWordStatus } = useReaderStore();

  if (!popover.isOpen) return null;

  const { result, anchorRect, isLoading, activeTab } = popover;

  const terms = result?.terms || [];
  const primaryTerm = terms[0];
  const kanjiList = result?.kanji || [];

  const top = anchorRect ? Math.min(window.innerHeight - 380, Math.max(10, anchorRect.top - 40)) : 100;
  const left = anchorRect ? Math.min(window.innerWidth - 400, Math.max(10, (anchorRect.left || 100) + 40)) : 100;

  return (
    <div
      id="dict-popover"
      className="fixed z-50 w-96 max-h-[480px] bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-lg shadow-2xl flex flex-col text-xs select-none animate-in fade-in zoom-in-95 duration-150"
      style={{ top: `${top}px`, left: `${left}px` }}
    >
      <div className="p-2.5 border-b border-[var(--border-color)] flex items-center justify-between bg-[var(--bg-primary)] rounded-t-lg">
        {primaryTerm ? (
          <div className="flex items-baseline space-x-2 truncate">
            <span className="dict-headword text-base font-bold text-[var(--accent-color)] truncate">
              {primaryTerm.expression}
            </span>
            {primaryTerm.reading && (
              <span className="dict-reading text-xs text-[var(--text-muted)] truncate">
                [{primaryTerm.reading}]
              </span>
            )}
            {primaryTerm.inflectionChain && primaryTerm.inflectionChain.length > 0 && (
              <span className="text-[10px] px-1 rounded bg-[var(--bg-surface)] text-[var(--text-muted)] border border-[var(--border-color)]">
                {primaryTerm.inflectionChain.join(' ← ')}
              </span>
            )}
          </div>
        ) : (
          <span className="text-xs text-[var(--text-muted)]">Looking up...</span>
        )}

        <button
          onClick={closePopover}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]"
        >
          <X className="w-3.5 h-3.5" />
        </button>
      </div>

      {isLoading ? (
        <div className="p-8 flex items-center justify-center space-x-2 text-[var(--text-muted)]">
          <Loader2 className="w-4 h-4 animate-spin text-[var(--accent-color)]" />
          <span>Searching dictionaries...</span>
        </div>
      ) : terms.length === 0 ? (
        <div className="p-6 text-center text-[var(--text-muted)]">No dictionary entries found for this word</div>
      ) : (
        <>
          <div className="flex border-b border-[var(--border-color)] bg-[var(--bg-primary)] px-2 text-[11px]">
            <button
              onClick={() => setPopoverTab('words')}
              className={`py-1.5 px-3 border-b-2 font-medium transition-colors ${
                activeTab === 'words'
                  ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                  : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
              }`}
            >
              Words ({terms.length})
            </button>
            {kanjiList.length > 0 && (
              <button
                onClick={() => setPopoverTab('kanji')}
                className={`py-1.5 px-3 border-b-2 font-medium transition-colors ${
                  activeTab === 'kanji'
                    ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                    : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
                }`}
              >
                Kanji ({kanjiList.length})
              </button>
            )}
          </div>

          <div className="flex-1 overflow-y-auto p-3 space-y-3">
            {activeTab === 'words' && (
              <div className="space-y-3">
                {terms.map((t, idx) => (
                  <div key={idx} className="p-2 rounded bg-[var(--bg-primary)] border border-[var(--border-color)]/60 space-y-2">
                    <div className="flex items-center justify-between text-xs">
                      <div className="flex items-baseline space-x-1.5">
                        <span className="font-bold text-[var(--text-primary)]">{t.expression}</span>
                        {t.reading && <span className="text-[11px] text-[var(--text-muted)]">[{t.reading}]</span>}
                      </div>

                      <div className="flex items-center space-x-1">
                        {[
                          { id: 1, label: 'Learn' },
                          { id: 2, label: 'Known' },
                        ].map((btn) => (
                          <button
                            key={btn.id}
                            onClick={() => updateWordStatus(idx, btn.id)}
                            className={`px-1.5 py-0.5 rounded text-[10px] border transition-colors ${
                              t.userStatus === btn.id
                                ? 'bg-[var(--accent-color)] text-black font-bold border-[var(--accent-color)]'
                                : 'text-[var(--text-muted)] border-[var(--border-color)] hover:text-[var(--text-primary)]'
                            }`}
                          >
                            {btn.label}
                          </button>
                        ))}
                      </div>
                    </div>

                    <div className="space-y-1">
                      {t.senses?.map((s, sIdx) => (
                        <div key={sIdx} className="text-[11px] leading-snug">
                          <span className="text-[10px] text-[var(--accent-color)] opacity-75 font-semibold mr-1">
                            {s.dictionaryTitle}
                          </span>
                          <span className="text-[var(--text-primary)]">{s.glosses?.join('; ')}</span>
                        </div>
                      ))}
                    </div>

                    <div className="pt-1 flex items-center justify-between border-t border-[var(--border-color)]/30">
                      <button
                        onClick={() => mineFlashcard(idx)}
                        className={`flex items-center space-x-1 px-2 py-0.5 rounded text-[10px] font-semibold border transition-colors ${
                          t._minedCardId
                            ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/40'
                            : 'bg-emerald-600/20 text-emerald-400 border-emerald-500/40 hover:bg-emerald-600/30'
                        }`}
                      >
                        <Bookmark className="w-3 h-3" />
                        <span>{t._minedCardId ? '✓ Mined' : '+ Mine Card'}</span>
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}

            {activeTab === 'kanji' && (
              <div className="space-y-2">
                {kanjiList.map((k, idx) => (
                  <div key={idx} className="p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] flex space-x-3">
                    <span className="text-2xl font-serif text-[var(--accent-color)] shrink-0">{k.character}</span>
                    <div className="flex-1 text-[11px] space-y-0.5">
                      <div className="text-[var(--text-primary)] font-semibold">{k.primaryMeanings?.join(', ')}</div>
                      <div className="text-[10px] text-[var(--text-muted)]">
                        ON: {k.onReadings?.join('、') || '—'} • KUN: {k.kunReadings?.join('、') || '—'}
                      </div>
                      <div className="text-[10px] text-[var(--text-muted)]">
                        Strokes: {k.strokeCount ?? '—'} • Grade: {k.grade || '—'}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
};
