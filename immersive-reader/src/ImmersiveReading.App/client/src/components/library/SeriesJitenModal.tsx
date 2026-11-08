import React, { useState, useEffect } from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { rpc } from '../../services/rpc';
import {
  Sparkles,
  ExternalLink,
  Download,
  CheckCircle,
  AlertTriangle,
  X,
  Loader2,
  Search,
  BookOpen,
} from 'lucide-react';

export const SeriesJitenModal: React.FC = () => {
  const { activeSeriesJiten, closeSeriesJiten, fetchBooks } = useLibraryStore();

  const [isLoading, setIsLoading] = useState(false);
  const [matchData, setMatchData] = useState<any | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearching, setIsSearching] = useState(false);
  const [searchResults, setSearchResults] = useState<any[]>([]);

  const [importTags, setImportTags] = useState(true);
  const [updateSynopsis, setUpdateSynopsis] = useState(true);
  const [isApplying, setIsApplying] = useState(false);
  const [isDownloading, setIsDownloading] = useState(false);
  const [statusMessage, setStatusMessage] = useState<{ text: string; type: 'success' | 'error' | 'info' } | null>(null);

  useEffect(() => {
    if (!activeSeriesJiten) return;

    setSearchQuery(activeSeriesJiten);
    setSearchResults([]);
    setStatusMessage(null);
    setMatchData(null);

    loadMatch(activeSeriesJiten);
  }, [activeSeriesJiten]);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && activeSeriesJiten && !isApplying && !isDownloading) {
        closeSeriesJiten();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [activeSeriesJiten, isApplying, isDownloading, closeSeriesJiten]);

  if (!activeSeriesJiten) return null;

  const loadMatch = async (seriesName: string, requestedDeckId?: number) => {
    setIsLoading(true);
    setStatusMessage(null);
    try {
      const res = await rpc.call<any>('library.matchSeriesOnJiten', {
        seriesName,
        deckId: requestedDeckId,
      });

      if (res && res.found) {
        setMatchData(res);
      } else {
        setMatchData(null);
        setStatusMessage({
          text: res?.message || `No matching franchise found on jiten.moe for "${seriesName}". Try searching below.`,
          type: 'info',
        });
      }
    } catch (err: any) {
      console.error('Match series failed:', err);
      setStatusMessage({ text: `Failed to match on Jiten: ${err.message || String(err)}`, type: 'error' });
    } finally {
      setIsLoading(false);
    }
  };

  const handleSearch = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    const query = searchQuery.trim();
    if (!query) return;

    setIsSearching(true);
    setStatusMessage(null);
    try {
      const res = await rpc.call<any>('library.searchJitenMoe', { query });
      setSearchResults(res?.items || []);
      if ((res?.items || []).length === 0) {
        setStatusMessage({ text: `No results found on jiten.moe for "${query}".`, type: 'info' });
      }
    } catch (err: any) {
      setStatusMessage({ text: `Search failed: ${err.message || String(err)}`, type: 'error' });
    } finally {
      setIsSearching(false);
    }
  };

  const handleApplyMapping = async () => {
    if (!matchData?.mappings || matchData.mappings.length === 0) return;

    setIsApplying(true);
    setStatusMessage(null);
    try {
      const res = await rpc.call<any>('library.applySeriesJitenMapping', {
        mappings: matchData.mappings,
        tags: importTags ? matchData.availableTags || [] : [],
        updateSynopsis,
      });

      if (res?.ok) {
        setStatusMessage({
          text: `⚡ Successfully updated ${res.updatedCount || matchData.mappings.length} volumes with Jiten metadata & tags!`,
          type: 'success',
        });
        await fetchBooks();
      }
    } catch (err: any) {
      setStatusMessage({ text: `Failed to apply metadata: ${err.message || String(err)}`, type: 'error' });
    } finally {
      setIsApplying(false);
    }
  };

  const handleDownloadAllDecks = async () => {
    if (!matchData?.mappings) return;

    const validDownloads = matchData.mappings
      .filter((m: any) => m.matchedDeckId && m.bookId)
      .map((m: any) => ({ bookId: m.bookId, deckId: m.matchedDeckId }));

    if (validDownloads.length === 0) {
      setStatusMessage({ text: 'No mapped sub-decks with valid deck IDs to download.', type: 'error' });
      return;
    }

    setIsDownloading(true);
    setStatusMessage({
      text: `Downloading ${validDownloads.length} deck CSV files from jiten.moe...`,
      type: 'info',
    });

    try {
      const res = await rpc.call<any>('library.downloadSeriesDecksCsv', {
        downloads: validDownloads,
      });

      const downloadedCount = res?.downloadedCount ?? validDownloads.length;
      setStatusMessage({
        text: `📥 Downloaded and attached ${downloadedCount} deck.csv files to book directories!`,
        type: 'success',
      });
      await fetchBooks();
    } catch (err: any) {
      setStatusMessage({ text: `Download failed: ${err.message || String(err)}`, type: 'error' });
    } finally {
      setIsDownloading(false);
    }
  };

  const parent = matchData?.parentDeck;
  const mappings = matchData?.mappings || [];

  return (
    <div
      className="modal-backdrop fixed inset-0 bg-black/75 backdrop-blur-sm flex items-center justify-center z-50 p-4"
      onClick={closeSeriesJiten}
    >
      <div
        className="series-jiten-card bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-xl shadow-2xl flex flex-col w-[920px] max-w-[96vw] h-[720px] max-h-[92vh] overflow-hidden select-none animate-in fade-in zoom-in-95 duration-150"
        onClick={(e) => e.stopPropagation()}
      >
        {/* HEADER */}
        <div className="flex items-center justify-between px-5 py-3 border-b border-[var(--border-color)] bg-black/20">
          <div className="flex items-center space-x-2.5">
            <div className="p-1.5 rounded-lg bg-sky-500/20 text-sky-400 border border-sky-500/30">
              <Sparkles className="w-5 h-5" />
            </div>
            <div>
              <div className="flex items-center space-x-2">
                <h3 className="text-sm font-bold text-[var(--text-primary)]">
                  Jiten.moe Series Matcher
                </h3>
                <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-sky-600 text-white uppercase">
                  JITEN.MOE
                </span>
              </div>
              <p className="text-[11px] text-[var(--text-muted)] truncate max-w-lg">
                Series: <span className="text-[var(--text-primary)] font-semibold">{activeSeriesJiten}</span>
              </p>
            </div>
          </div>
          <button
            onClick={closeSeriesJiten}
            className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"
            title="Close (Esc)"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* STATUS BANNER */}
        {statusMessage && (
          <div
            className={`px-4 py-2 text-xs flex items-center justify-between border-b ${
              statusMessage.type === 'success'
                ? 'bg-emerald-950/60 text-emerald-300 border-emerald-800'
                : statusMessage.type === 'error'
                ? 'bg-rose-950/60 text-rose-300 border-rose-800'
                : 'bg-sky-950/60 text-sky-300 border-sky-800'
            }`}
          >
            <div className="flex items-center space-x-2">
              {statusMessage.type === 'success' ? (
                <CheckCircle className="w-4 h-4 text-emerald-400 shrink-0" />
              ) : statusMessage.type === 'error' ? (
                <AlertTriangle className="w-4 h-4 text-rose-400 shrink-0" />
              ) : (
                <Sparkles className="w-4 h-4 text-sky-400 shrink-0" />
              )}
              <span>{statusMessage.text}</span>
            </div>
            <button onClick={() => setStatusMessage(null)} className="opacity-70 hover:opacity-100">
              ✕
            </button>
          </div>
        )}

        {/* SEARCH BAR (for manual query or alternate match) */}
        <div className="px-5 py-2.5 bg-black/10 border-b border-[var(--border-color)] flex items-center space-x-2">
          <input
            type="text"
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder="Search series or novel title on Jiten.moe..."
            className="flex-1 px-3 py-1.5 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
            onKeyDown={(e) => {
              if (e.key === 'Enter') handleSearch();
            }}
          />
          <button
            type="button"
            onClick={handleSearch}
            disabled={isSearching}
            className="flex items-center space-x-1.5 px-3 py-1.5 rounded-lg bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-50"
          >
            {isSearching ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
            <span>Search</span>
          </button>
        </div>

        {/* BODY */}
        <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs text-[var(--text-primary)]">
          {/* SEARCH RESULTS DROPDOWN (if searched) */}
          {searchResults.length > 0 && (
            <div className="p-3 rounded-lg bg-black/30 border border-sky-500/30 space-y-2">
              <div className="text-[11px] font-semibold text-sky-300 uppercase">
                Search Results ({searchResults.length}) — Click to match with this franchise:
              </div>
              <div className="grid grid-cols-2 gap-2 max-h-48 overflow-y-auto">
                {searchResults.map((item) => (
                  <div
                    key={item.deckId}
                    onClick={() => {
                      setSearchResults([]);
                      loadMatch(activeSeriesJiten, item.deckId);
                    }}
                    className="p-2 rounded bg-[var(--bg-surface)] border border-[var(--border-color)] hover:border-[var(--accent-color)] cursor-pointer flex space-x-2.5 items-center transition-colors"
                  >
                    {item.coverUrl ? (
                      <img src={item.coverUrl} alt="" className="w-8 h-11 object-cover rounded shrink-0 bg-black/40" />
                    ) : (
                      <div className="w-8 h-11 rounded bg-black/40 flex items-center justify-center shrink-0 text-[10px]">
                        📖
                      </div>
                    )}
                    <div className="min-w-0 flex-1">
                      <div className="font-semibold text-xs text-[var(--text-primary)] truncate">
                        {item.originalTitle || item.romajiTitle || item.englishTitle}
                      </div>
                      <div className="text-[10px] text-[var(--text-muted)] flex items-center space-x-1.5 mt-0.5">
                        <span>Deck #{item.deckId}</span>
                        {item.difficulty && (
                          <span className="font-bold text-amber-400">★ {item.difficulty.toFixed(1)}</span>
                        )}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {isLoading ? (
            <div className="h-64 flex flex-col items-center justify-center space-y-2 text-[var(--text-muted)]">
              <Loader2 className="w-6 h-6 animate-spin text-[var(--accent-color)]" />
              <span>Querying jiten.moe franchise & sub-decks...</span>
            </div>
          ) : parent ? (
            <>
              {/* MATCHED FRANCHISE BANNER */}
              <div className="p-4 rounded-xl border border-sky-500/30 bg-sky-950/20 flex space-x-4 items-start">
                {parent.coverUrl ? (
                  <img
                    src={parent.coverUrl}
                    alt=""
                    className="w-16 h-24 object-cover rounded-lg shadow-md border border-white/10 shrink-0"
                  />
                ) : (
                  <div className="w-16 h-24 rounded-lg bg-black/40 flex items-center justify-center text-xl shrink-0">
                    📚
                  </div>
                )}

                <div className="min-w-0 flex-1 space-y-1.5">
                  <div className="flex items-center justify-between">
                    <h4 className="text-sm font-bold text-[var(--text-primary)]">
                      {parent.originalTitle || parent.romajiTitle || activeSeriesJiten}
                    </h4>
                    {parent.jitenUrl && (
                      <a
                        href={parent.jitenUrl}
                        target="_blank"
                        rel="noreferrer"
                        className="text-xs text-[var(--accent-color)] hover:underline inline-flex items-center space-x-1"
                      >
                        <span>View on Jiten</span>
                        <ExternalLink className="w-3 h-3" />
                      </a>
                    )}
                  </div>

                  {parent.romajiTitle && parent.romajiTitle !== parent.originalTitle && (
                    <div className="text-[11px] text-[var(--text-muted)] italic">{parent.romajiTitle}</div>
                  )}

                  <div className="flex items-center space-x-3 text-xs flex-wrap gap-y-1 pt-0.5">
                    {parent.difficulty !== undefined && parent.difficulty !== null && (
                      <span className="px-2 py-0.5 rounded font-bold bg-amber-500/20 text-amber-300 border border-amber-500/30">
                        ★ Diff: {parent.difficulty.toFixed(2)}
                      </span>
                    )}
                    <span className="text-[var(--text-muted)]">
                      {matchData.subDecksCount || 0} Volume Sub-Decks
                    </span>
                    <span className="text-[var(--text-muted)]">
                      {mappings.filter((m: any) => m.matchedDeckId).length}/{mappings.length} Local Volumes Correlated
                    </span>
                  </div>

                  {/* GENRES & COMMUNITY TAGS */}
                  {matchData.availableTags && matchData.availableTags.length > 0 && (
                    <div className="flex flex-wrap gap-1 pt-1.5">
                      {matchData.availableTags.slice(0, 8).map((t: string) => (
                        <span
                          key={t}
                          className="px-2 py-0.5 rounded text-[10px] bg-black/30 text-[var(--text-muted)] border border-white/5"
                        >
                          {t}
                        </span>
                      ))}
                      {matchData.availableTags.length > 8 && (
                        <span className="text-[10px] text-[var(--text-muted)] self-center">
                          +{matchData.availableTags.length - 8} more
                        </span>
                      )}
                    </div>
                  )}
                </div>
              </div>

              {/* VOLUME MAPPINGS TABLE */}
              <div className="space-y-2">
                <div className="flex items-center justify-between text-xs">
                  <span className="font-semibold text-[var(--text-primary)] uppercase tracking-wide text-[11px]">
                    Volume Correlation Matrix ({mappings.length} Books)
                  </span>
                  <span className="text-[11px] text-[var(--text-muted)]">
                    Local volume index maps directly to Jiten sub-deck
                  </span>
                </div>

                <div className="border border-[var(--border-color)] rounded-lg overflow-hidden">
                  <table className="w-full text-left text-xs border-collapse">
                    <thead>
                      <tr className="bg-black/30 border-b border-[var(--border-color)] text-[var(--text-muted)] uppercase text-[10px] tracking-wider">
                        <th className="py-2 px-3 w-16">Vol #</th>
                        <th className="py-2 px-3">Local Book Title</th>
                        <th className="py-2 px-3">Matched Jiten Sub-Deck</th>
                        <th className="py-2 px-3 w-20 text-center">Diff</th>
                        <th className="py-2 px-3 w-24 text-right">Chars</th>
                        <th className="py-2 px-3 w-24 text-center">Status</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-[var(--border-color)]/50">
                      {mappings.map((m: any, idx: number) => {
                        const isMatched = !!m.matchedDeckId;
                        return (
                          <tr key={m.bookId || idx} className="hover:bg-black/20">
                            <td className="py-2 px-3 font-mono font-semibold text-sky-400">
                              #{m.seriesIndex ?? idx + 1}
                            </td>
                            <td className="py-2 px-3 truncate max-w-xs font-medium" title={m.title}>
                              {m.title}
                            </td>
                            <td className="py-2 px-3 truncate max-w-xs text-[var(--text-muted)]">
                              {m.matchedTitle ? (
                                <span className="text-[var(--text-primary)]">{m.matchedTitle}</span>
                              ) : (
                                <span className="italic opacity-50">No sub-deck match</span>
                              )}
                            </td>
                            <td className="py-2 px-3 text-center">
                              {m.difficulty ? (
                                <span className="font-bold text-amber-300">★ {m.difficulty.toFixed(1)}</span>
                              ) : (
                                '—'
                              )}
                            </td>
                            <td className="py-2 px-3 text-right font-mono text-[11px] text-[var(--text-muted)]">
                              {m.characterCount ? m.characterCount.toLocaleString() : '—'}
                            </td>
                            <td className="py-2 px-3 text-center">
                              {isMatched ? (
                                <span className="px-1.5 py-0.5 rounded text-[10px] font-semibold bg-emerald-500/20 text-emerald-300 border border-emerald-500/30">
                                  ✓ Mapped
                                </span>
                              ) : (
                                <span className="px-1.5 py-0.5 rounded text-[10px] font-semibold bg-amber-500/20 text-amber-300 border border-amber-500/30">
                                  Unmatched
                                </span>
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </div>

              {/* OPTIONS */}
              <div className="p-3 rounded-lg bg-black/20 border border-[var(--border-color)] flex items-center justify-between text-xs">
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={importTags}
                    onChange={(e) => setImportTags(e.target.checked)}
                    className="rounded border-[var(--border-color)] text-[var(--accent-color)] focus:ring-0"
                  />
                  <span>Import Jiten tags & genres to local book tags</span>
                </label>

                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={updateSynopsis}
                    onChange={(e) => setUpdateSynopsis(e.target.checked)}
                    className="rounded border-[var(--border-color)] text-[var(--accent-color)] focus:ring-0"
                  />
                  <span>Fill missing descriptions from Jiten synopsis</span>
                </label>
              </div>
            </>
          ) : (
            <div className="h-64 flex flex-col items-center justify-center space-y-3 text-[var(--text-muted)] text-center p-6">
              <BookOpen className="w-10 h-10 opacity-30 text-[var(--text-muted)]" />
              <div>
                <p className="font-semibold text-sm text-[var(--text-primary)]">
                  No direct Jiten match for &ldquo;{activeSeriesJiten}&rdquo;
                </p>
                <p className="text-xs mt-1">
                  Use the search bar above to look up the franchise by Japanese romaji or English title.
                </p>
              </div>
            </div>
          )}
        </div>

        {/* FOOTER */}
        <div className="flex items-center justify-between px-5 py-3 border-t border-[var(--border-color)] bg-black/10">
          <div className="text-[11px] text-[var(--text-muted)]">
            Offline & Privacy: Jiten data is downloaded once and stored locally.
          </div>

          <div className="flex items-center space-x-2.5">
            <button
              type="button"
              onClick={closeSeriesJiten}
              className="px-4 py-1.5 rounded-lg border border-[var(--border-color)] hover:bg-[var(--bg-primary)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] transition-colors"
            >
              Close
            </button>

            {parent && (
              <>
                <button
                  type="button"
                  onClick={handleDownloadAllDecks}
                  disabled={isDownloading || isApplying}
                  className="flex items-center space-x-1.5 px-3.5 py-1.5 rounded-lg bg-[var(--bg-primary)] border border-sky-500/50 hover:border-sky-400 text-sky-300 font-semibold text-xs hover:bg-sky-950/30 transition-colors disabled:opacity-40"
                  title="Download CSV vocabulary decks for all volumes"
                >
                  {isDownloading ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
                  <span>📥 Download All Decks (.csv)</span>
                </button>

                <button
                  type="button"
                  onClick={handleApplyMapping}
                  disabled={isApplying || isDownloading}
                  className="flex items-center space-x-1.5 px-4 py-1.5 rounded-lg bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-40 shadow-sm transition-opacity"
                >
                  {isApplying ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Sparkles className="w-3.5 h-3.5" />}
                  <span>⚡ Apply Mapping & Sync</span>
                </button>
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};
