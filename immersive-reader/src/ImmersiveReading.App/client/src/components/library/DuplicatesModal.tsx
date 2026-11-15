import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { rpc } from '../../services/rpc';
import { Copy, X, Loader2, CheckCircle2, RefreshCw, Undo2 } from 'lucide-react';

// Spec 21: find likely duplicate books and let the user decide which one to keep. Nothing is removed without a click per group,
// and "remove" only removes the library entry (the EPUB file stays on disk).

interface DupBook {
  bookId: string;
  title: string;
  seriesName: string;
  seriesIndex: number | null;
  authors: string;
  isbn: string;
  language: string;
  totalChars: number;
  chapterCount: number;
  statsComputed: boolean;
  fileSize: number;
  epubFileName: string;
  folderPath: string;
  addedAtUtc: string;
  hasCover: boolean;
  progressPercent: number;
  hasStudyPack: boolean;
  hasDeckCsv: boolean;
  tagCount: number;
}
interface DupMember { book: DupBook; keepScore: number; keepReasons: string[]; bestMatchScore: number }
interface DupPair { bookA: string; bookB: string; score: number; reasons: string[] }
interface DupGroup {
  groupId: string;
  confidence: 'certain' | 'likely' | 'possible';
  score: number;
  recommendedKeepId: string;
  members: DupMember[];
  pairs: DupPair[];
}
interface DupResult { totalBooks: number; elapsedMs: number; groups: DupGroup[] }
interface RemovedBook { bookId: string; title: string; authors: string; epubFileName: string; originalFolderPath: string; keptBookId: string; removedAtUtc: string }

const PAGE = 25;

const formatSize = (bytes: number) =>
  bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;

const badgeClass: Record<string, string> = {
  certain: 'bg-rose-500/20 text-rose-300 border-rose-500/40',
  likely: 'bg-amber-500/20 text-amber-300 border-amber-500/40',
  possible: 'bg-sky-500/20 text-sky-300 border-sky-500/40',
};

const THRESHOLDS: { label: string; value: number }[] = [
  { label: 'Likely and certain', value: 70 },
  { label: 'Include possible', value: 50 },
  { label: 'Certain only', value: 90 },
];

export const DuplicatesModal: React.FC = () => {
  const { isDuplicatesModalOpen, closeDuplicatesModal, fetchBooks } = useLibraryStore();

  const [tab, setTab] = useState<'groups' | 'removed'>('groups');
  const [minScore, setMinScore] = useState(70);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<DupResult | null>(null);
  const [groups, setGroups] = useState<DupGroup[]>([]);
  const [keep, setKeep] = useState<Record<string, string>>({});
  const [busyGroup, setBusyGroup] = useState<string | null>(null);
  const [mergeData, setMergeData] = useState(true);
  const [shown, setShown] = useState(PAGE);
  const [removedBooks, setRemovedBooks] = useState<RemovedBook[]>([]);
  const [resolvedCount, setResolvedCount] = useState(0);
  const [massBusy, setMassBusy] = useState(false);
  const [massMessage, setMassMessage] = useState<string | null>(null);

  const search = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await rpc.call<DupResult>('library.findDuplicates', { minScore });
      setResult(res);
      setGroups(res.groups);
      setKeep(Object.fromEntries(res.groups.map((g) => [g.groupId, g.recommendedKeepId])));
      setShown(PAGE);
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setLoading(false);
    }
  }, [minScore]);

  const loadRemoved = useCallback(async () => {
    try {
      setRemovedBooks(await rpc.call<RemovedBook[]>('library.listRemovedBooks', {}));
    } catch (e: any) {
      setError(e?.message || String(e));
    }
  }, []);

  useEffect(() => {
    if (!isDuplicatesModalOpen) return;
    setResolvedCount(0);
    setTab('groups');
    search();
    loadRemoved();
  }, [isDuplicatesModalOpen, search, loadRemoved]);

  useEffect(() => {
    if (!isDuplicatesModalOpen) return undefined;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !busyGroup) closeDuplicatesModal();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isDuplicatesModalOpen, busyGroup, closeDuplicatesModal]);

  const resolve = async (group: DupGroup) => {
    const keepId = keep[group.groupId] ?? group.recommendedKeepId;
    const removeIds = group.members.map((m) => m.book.bookId).filter((id) => id !== keepId);
    const keepBook = group.members.find((m) => m.book.bookId === keepId)!.book;
    if (
      !confirm(
        `Keep "${keepBook.title}" and remove ${removeIds.length} other book(s) from the library?\n\n` +
          'The EPUB files stay on disk and a re-scan will not import the removed ones again.' +
          (mergeData ? '\nTags, custom fields and missing details are copied to the kept book.' : '')
      )
    ) {
      return;
    }
    setBusyGroup(group.groupId);
    setError(null);
    try {
      await rpc.call('library.resolveDuplicates', { keepBookId: keepId, removeBookIds: removeIds, mergeData });
      setGroups((gs) => gs.filter((g) => g.groupId !== group.groupId));
      setResolvedCount((n) => n + removeIds.length);
      await fetchBooks();
      loadRemoved();
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setBusyGroup(null);
    }
  };

  const dismiss = async (group: DupGroup) => {
    setBusyGroup(group.groupId);
    setError(null);
    try {
      await rpc.call('library.dismissDuplicates', { bookIds: group.members.map((m) => m.book.bookId) });
      setGroups((gs) => gs.filter((g) => g.groupId !== group.groupId));
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setBusyGroup(null);
    }
  };

  // Mass clear: every group that is currently listed keeps its recommended book, except groups where the user chose another copy
  // by hand: that choice is respected. Nothing else changes: EPUB files stay on disk, same merge option as a single group.
  const massClear = async () => {
    const targets = groups.map((g) => {
      const keepId = keep[g.groupId] ?? g.recommendedKeepId;
      return { group: g, keepId, manual: keepId !== g.recommendedKeepId };
    });
    if (targets.length === 0) return;
    const byConfidence = (c: string) => targets.filter((t) => t.group.confidence === c).length;
    const books = targets.reduce((n, t) => n + t.group.members.length - 1, 0);
    const manual = targets.filter((t) => t.manual).length;
    const lines = [
      `Clear ${targets.length} duplicate group(s), keeping the recommended book in each and removing ${books} other book(s) from the library?`,
      '',
      `certain: ${byConfidence('certain')}, likely: ${byConfidence('likely')}, possible: ${byConfidence('possible')}`,
      ...(manual > 0 ? [`${manual} group(s) where you picked a different copy keep your pick.`] : []),
      ...(byConfidence('possible') > 0 ? ['', 'Some groups are only "possible" matches. Consider reviewing those first (or switch the list to "Likely and certain").'] : []),
      '',
      'EPUB files stay on disk and a re-scan will not import the removed ones again.' + (mergeData ? ' Tags, decks and missing details are merged into the kept books.' : ''),
    ];
    if (!confirm(lines.join('\n'))) return;

    setMassBusy(true);
    setMassMessage(null);
    setError(null);
    try {
      const res = await rpc.call<{ groupsResolved: number; removed: number; resolvedKeepBookIds: string[]; failed: { keepBookId: string; error: string }[] }>(
        'library.resolveDuplicatesBatch',
        {
          mergeData,
          groups: targets.map((t) => ({
            keepBookId: t.keepId,
            removeBookIds: t.group.members.map((m) => m.book.bookId).filter((id) => id !== t.keepId),
          })),
        }
      );
      const done = new Set(res.resolvedKeepBookIds);
      const doneGroups = new Set(targets.filter((t) => done.has(t.keepId)).map((t) => t.group.groupId));
      setGroups((gs) => gs.filter((g) => !doneGroups.has(g.groupId)));
      setResolvedCount((n) => n + res.removed);
      setMassMessage(
        `Cleared ${res.groupsResolved} group(s), removed ${res.removed} book(s).` +
          (res.failed.length > 0 ? ` ${res.failed.length} group(s) could not be cleared (${res.failed[0].error}); search again.` : '')
      );
      await fetchBooks();
      loadRemoved();
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setMassBusy(false);
    }
  };

  const forget = async (bookId: string) => {
    try {
      await rpc.call('library.forgetRemovedBook', { bookId });
      setRemovedBooks((r) => r.filter((b) => b.bookId !== bookId));
    } catch (e: any) {
      setError(e?.message || String(e));
    }
  };

  const visible = useMemo(() => groups.slice(0, shown), [groups, shown]);

  if (!isDuplicatesModalOpen) return null;

  return (
    <div
      className="modal-backdrop fixed inset-0 bg-black/75 backdrop-blur-sm flex items-center justify-center z-50 p-4"
      onClick={() => !busyGroup && closeDuplicatesModal()}
    >
      <div
        className="bg-[var(--bg-surface)] border border-[var(--border-color)] modal-surface rounded-[var(--radius-modal)] shadow-2xl flex flex-col w-[1100px] max-w-[96vw] max-h-[92vh] overflow-hidden"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-[var(--border-color)] bg-black/20">
          <div className="flex items-center space-x-2.5">
            <div className="p-1.5 rounded-lg bg-[var(--accent-color)]/20 text-[var(--accent-text)] border border-[var(--accent-color)]/30">
              <Copy className="w-5 h-5" />
            </div>
            <div>
              <h3 className="text-sm font-bold text-[var(--text-primary)]">Find duplicate books</h3>
              <p className="text-[11px] text-[var(--text-muted)]">
                Compared by title, volume, author, series, ISBN and length. You decide what to keep; EPUB files are never deleted.
              </p>
            </div>
          </div>
          <button onClick={closeDuplicatesModal} disabled={!!busyGroup} className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]" title="Close (Esc)">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="flex items-center justify-between gap-3 px-5 py-2.5 border-b border-[var(--border-color)] text-xs">
          <div className="flex items-center gap-2">
            <button className={`px-3 py-1 rounded border ${tab === 'groups' ? 'border-[var(--accent-color)] bg-[var(--accent-color)]/15' : 'border-[var(--border-color)]'}`} onClick={() => setTab('groups')}>
              Duplicates{result ? ` (${groups.length})` : ''}
            </button>
            <button className={`px-3 py-1 rounded border ${tab === 'removed' ? 'border-[var(--accent-color)] bg-[var(--accent-color)]/15' : 'border-[var(--border-color)]'}`} onClick={() => setTab('removed')}>
              Removed duplicates ({removedBooks.length})
            </button>
          </div>
          {tab === 'groups' && (
            <div className="flex items-center gap-3">
              <select
                className="bg-[var(--bg-primary)] border border-[var(--border-color)] rounded px-2 py-1"
                value={minScore}
                onChange={(e) => setMinScore(parseInt(e.target.value, 10))}
              >
                {THRESHOLDS.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
              </select>
              <label className="flex items-center gap-1.5 text-[var(--text-muted)]" title="Copy tags, custom fields (e.g. the vocabulary deck) and missing details to the kept book">
                <input type="checkbox" checked={mergeData} onChange={(e) => setMergeData(e.target.checked)} /> Merge tags and details into the kept book
              </label>
              <button
                className="flex items-center gap-1 px-2.5 py-1 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold disabled:opacity-50"
                onClick={massClear}
                disabled={massBusy || loading || !!busyGroup || groups.length === 0}
                title="Keep the recommended book in every listed group and remove the others from the library (files stay on disk)"
              >
                {massBusy ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : null} Mass clear keeping recommended ({groups.length})
              </button>
              <button className="flex items-center gap-1 px-2.5 py-1 rounded border border-[var(--border-color)] hover:border-[var(--accent-color)]" onClick={search} disabled={loading || massBusy}>
                <RefreshCw className={`w-3.5 h-3.5 ${loading ? 'animate-spin' : ''}`} /> Search again
              </button>
            </div>
          )}
        </div>

        <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs text-[var(--text-primary)]">
          {error && <div className="p-2.5 rounded border border-rose-500/40 bg-rose-950/30 text-rose-300">{error}</div>}
          {massMessage && <div className="p-2.5 rounded border border-emerald-500/40 bg-emerald-950/30 text-emerald-300">{massMessage}</div>}

          {tab === 'groups' && (
            <>
              {loading && !result && (
                <div className="flex items-center gap-2 text-[var(--text-muted)]"><Loader2 className="w-4 h-4 animate-spin" /> Comparing your library...</div>
              )}
              {result && (
                <div className="text-[var(--text-muted)]">
                  {result.totalBooks.toLocaleString()} books compared in {result.elapsedMs} ms: {groups.length} group(s) to review
                  {resolvedCount > 0 && <span className="text-emerald-400"> · {resolvedCount} removed in this session</span>}
                </div>
              )}
              {result && groups.length === 0 && !loading && (
                <div className="flex items-center gap-2 text-emerald-400 py-8 justify-center">
                  <CheckCircle2 className="w-5 h-5" /> No duplicates to review{minScore > 50 ? ' at this level. Try "Include possible".' : '.'}
                </div>
              )}

              {visible.map((group) => {
                const keepId = keep[group.groupId] ?? group.recommendedKeepId;
                const reasons = Array.from(new Set(group.pairs.flatMap((p) => p.reasons))).slice(0, 4);
                const busy = busyGroup === group.groupId;
                return (
                  <div key={group.groupId} className="rounded-lg border border-[var(--border-color)] bg-black/20">
                    <div className="flex items-center justify-between px-3.5 py-2 border-b border-[var(--border-color)]">
                      <div className="flex items-center gap-2 flex-wrap">
                        <span className={`px-2 py-0.5 rounded border text-[10px] font-bold uppercase ${badgeClass[group.confidence]}`}>{group.confidence}</span>
                        <span className="text-[var(--text-muted)]">{group.members.length} books · match {group.score}</span>
                        <span className="text-[var(--text-muted)]">· {reasons.join(' · ')}</span>
                      </div>
                    </div>
                    <table className="w-full">
                      <thead>
                        <tr className="text-[10px] uppercase text-[var(--text-muted)] text-left">
                          <th className="px-3 py-1.5 w-12">Keep</th>
                          <th className="px-2 py-1.5">Book</th>
                          <th className="px-2 py-1.5">Series</th>
                          <th className="px-2 py-1.5">Length</th>
                          <th className="px-2 py-1.5">File</th>
                          <th className="px-2 py-1.5">You have</th>
                        </tr>
                      </thead>
                      <tbody>
                        {group.members.map((m) => {
                          const b = m.book;
                          const isKeep = b.bookId === keepId;
                          return (
                            <tr key={b.bookId} className={`border-t border-[var(--border-color)] align-top cursor-pointer ${isKeep ? 'bg-emerald-950/20' : ''}`} onClick={() => setKeep((k) => ({ ...k, [group.groupId]: b.bookId }))}>
                              <td className="px-3 py-2"><input type="radio" name={`keep-${group.groupId}`} checked={isKeep} onChange={() => setKeep((k) => ({ ...k, [group.groupId]: b.bookId }))} /></td>
                              <td className="px-2 py-2">
                                <div className="flex gap-2.5">
                                  {b.hasCover ? (
                                    <img src={`ir-app://covers/${b.bookId}.jpg`} alt="" className="w-10 h-14 object-cover rounded shrink-0" />
                                  ) : (
                                    <div className="w-10 h-14 rounded bg-[var(--bg-primary)] shrink-0" />
                                  )}
                                  <div className="min-w-0">
                                    <div className="font-semibold truncate max-w-[300px]" title={b.title}>{b.title}</div>
                                    <div className="text-[var(--text-muted)] truncate max-w-[300px]">{b.authors || 'unknown author'}</div>
                                    {b.bookId === group.recommendedKeepId && (
                                      <div className="text-emerald-400 text-[10px] mt-0.5" title={m.keepReasons.join(', ')}>
                                        recommended{m.keepReasons.length > 0 ? `: ${m.keepReasons.slice(0, 2).join(', ')}` : ''}
                                      </div>
                                    )}
                                  </div>
                                </div>
                              </td>
                              <td className="px-2 py-2 text-[var(--text-muted)]">{b.seriesName ? `${b.seriesName}${b.seriesIndex != null ? ` #${b.seriesIndex}` : ''}` : '—'}</td>
                              <td className="px-2 py-2 text-[var(--text-muted)]">
                                {b.statsComputed ? `${b.totalChars.toLocaleString()} chars` : '—'}
                                <div>{b.chapterCount} ch.</div>
                              </td>
                              <td className="px-2 py-2 text-[var(--text-muted)]">
                                <div className="truncate max-w-[220px]" title={`${b.folderPath}\\${b.epubFileName}`}>{b.epubFileName}</div>
                                <div className="truncate max-w-[220px]" title={b.folderPath}>{b.folderPath}</div>
                                <div>{formatSize(b.fileSize)}</div>
                              </td>
                              <td className="px-2 py-2 text-[var(--text-muted)] space-y-0.5">
                                {b.progressPercent > 0 && <div className="text-amber-300">{Math.round(b.progressPercent)}% read</div>}
                                {b.hasStudyPack && <div>study pack</div>}
                                {b.hasDeckCsv && <div>vocabulary deck</div>}
                                {b.tagCount > 0 && <div>{b.tagCount} tag(s)</div>}
                                {b.progressPercent <= 0 && !b.hasStudyPack && !b.hasDeckCsv && b.tagCount === 0 && <div>—</div>}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                    <div className="flex items-center justify-end gap-2 px-3.5 py-2 border-t border-[var(--border-color)]">
                      <button className="px-3 py-1.5 rounded border border-[var(--border-color)] hover:border-[var(--text-primary)]" disabled={busy} onClick={() => setGroups((gs) => gs.filter((g) => g.groupId !== group.groupId))} title="Hide for now; it will be offered again next time">
                        Skip
                      </button>
                      <button className="px-3 py-1.5 rounded border border-[var(--border-color)] hover:border-[var(--text-primary)]" disabled={busy} onClick={() => dismiss(group)} title="Remember that these are different books">
                        Not duplicates
                      </button>
                      <button className="px-3 py-1.5 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold disabled:opacity-50" disabled={busy} onClick={() => resolve(group)}>
                        {busy ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : 'Keep selected, remove others'}
                      </button>
                    </div>
                  </div>
                );
              })}

              {groups.length > shown && (
                <div className="flex justify-center">
                  <button className="px-4 py-1.5 rounded border border-[var(--border-color)] hover:border-[var(--accent-color)]" onClick={() => setShown((n) => n + PAGE)}>
                    Show {Math.min(PAGE, groups.length - shown)} more ({groups.length - shown} left)
                  </button>
                </div>
              )}
            </>
          )}

          {tab === 'removed' && (
            <>
              <p className="text-[var(--text-muted)]">
                Books you removed as duplicates. Their files are still on disk and are skipped when you scan a folder. Allow import again to bring one back with the next scan.
              </p>
              {removedBooks.length === 0 ? (
                <div className="py-8 text-center text-[var(--text-muted)]">Nothing removed yet.</div>
              ) : (
                <table className="w-full">
                  <thead><tr className="text-[10px] uppercase text-[var(--text-muted)] text-left"><th className="px-2 py-1.5">Book</th><th className="px-2 py-1.5">File</th><th className="px-2 py-1.5">Removed</th><th /></tr></thead>
                  <tbody>
                    {removedBooks.map((b) => (
                      <tr key={b.bookId} className="border-t border-[var(--border-color)]">
                        <td className="px-2 py-2"><div className="font-semibold">{b.title}</div><div className="text-[var(--text-muted)]">{b.authors}</div></td>
                        <td className="px-2 py-2 text-[var(--text-muted)]"><div>{b.epubFileName}</div><div className="truncate max-w-[300px]" title={b.originalFolderPath}>{b.originalFolderPath}</div></td>
                        <td className="px-2 py-2 text-[var(--text-muted)]">{new Date(b.removedAtUtc).toLocaleString()}</td>
                        <td className="px-2 py-2 text-right">
                          <button className="inline-flex items-center gap-1 px-2.5 py-1 rounded border border-[var(--border-color)] hover:border-[var(--accent-color)]" onClick={() => forget(b.bookId)}>
                            <Undo2 className="w-3.5 h-3.5" /> Allow import again
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
};
