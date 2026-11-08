import React, { useState, useRef } from 'react';
import { useStudyPackStore } from '../../stores/useStudyPackStore';
import { rpc } from '../../services/rpc';
import { X, Sparkles, FileText, Loader2, Zap, Bookmark, Check, Download, Upload } from 'lucide-react';

export const StudyPackDrawer: React.FC = () => {
  const {
    isOpen,
    closeDrawer,
    overview,
    minedCards,
    isLoading,
    isSyncingAnki,
    selectedVocabSetIndex,
    selectVocabSet,
    syncAllToAnki,
  } = useStudyPackStore();

  const [syncMessage, setSyncMessage] = useState<string | null>(null);
  const [isExporting, setIsExporting] = useState(false);
  const [isImporting, setIsImporting] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  if (!isOpen) return null;

  const vocabSets = (overview?.vocabularySets as any[]) || [];
  const activeSet = vocabSets[selectedVocabSetIndex];

  const handleExportTransfer = async () => {
    if (!overview?.bookId) return;
    setIsExporting(true);
    try {
      const res = await rpc.call<any>('studypack.getLearningMaterial', { bookId: overview.bookId });
      if (res?.learningMaterial) {
        const jsonStr = JSON.stringify(res.learningMaterial, null, 2);
        const blob = new Blob([jsonStr], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${overview.bookId}_learning_material.json`;
        a.click();
        URL.revokeObjectURL(url);
      } else {
        alert('Learning material transfer JSON not found for this book yet. Generate the study pack first.');
      }
    } catch (err: any) {
      alert(`Export failed: ${err.message || String(err)}`);
    } finally {
      setIsExporting(false);
    }
  };

  const handleImportTransferFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file || !overview?.bookId) return;
    setIsImporting(true);
    try {
      const text = await file.text();
      await rpc.call('studypack.importTransfer', {
        bookId: overview.bookId,
        transferJson: text,
        generatePdfs: true,
      });
      useStudyPackStore.getState().openDrawer(overview.bookId);
      alert('Successfully imported learning material and generated PDFs!');
    } catch (err: any) {
      alert(`Import failed: ${err.message || String(err)}`);
    } finally {
      setIsImporting(false);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  };

  const handleSyncAnki = async () => {
    setSyncMessage(null);
    try {
      const res = await syncAllToAnki();
      if (res?.message) {
        setSyncMessage(res.message);
      } else if (res?.success) {
        setSyncMessage(`Successfully synced ${res.addedCount} cards to Anki!`);
      }
    } catch (err: any) {
      setSyncMessage(`Anki sync failed: ${err.message || String(err)}`);
    }
  };

  const allPdfs: any[] = [
    ...(overview?.masterPdfs || []),
    ...((overview as any)?.chapterPdfs || [])
  ];

  return (
    <div
      id="studypack-drawer"
      className="fixed inset-y-0 right-0 w-96 bg-[var(--bg-surface)] border-l border-[var(--border-color)] shadow-2xl flex flex-col z-40 select-none animate-in slide-in-from-right duration-200"
    >
      <div className="p-3 border-b border-[var(--border-color)] flex items-center justify-between bg-[var(--bg-primary)]">
        <div className="flex items-center space-x-2">
          <Sparkles className="w-4 h-4 text-emerald-400" />
          <span className="text-xs font-bold text-[var(--text-primary)]">Study Pack & Master Sheets</span>
        </div>
        <button
          onClick={closeDrawer}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)] cursor-pointer"
        >
          <X className="w-4 h-4" />
        </button>
      </div>

      <div className="flex-1 overflow-y-auto p-4 space-y-4">
        {isLoading ? (
          <div className="h-64 flex flex-col items-center justify-center space-y-2 text-[var(--text-muted)]">
            <Loader2 className="w-6 h-6 animate-spin text-[var(--accent-color)]" />
            <span className="text-xs">Loading study pack...</span>
          </div>
        ) : (!overview || (!overview.available && allPdfs.length === 0 && vocabSets.length === 0)) ? (
          <div className="p-6 text-center text-xs text-[var(--text-muted)]">No study pack published for this book yet</div>
        ) : (
          <>
            {allPdfs.length > 0 && (
              <div>
                <h4 className="text-[11px] font-bold uppercase text-[var(--text-muted)] tracking-wider mb-2">
                  Printable A4 Study PDFs ({allPdfs.length})
                </h4>
                <div className="grid grid-cols-1 gap-1.5">
                  {allPdfs.map((pdf: any, idx: number) => {
                    const pdfName = pdf.title || pdf.fileName || pdf.pdfName || 'Study PDF';
                    const fullPath = pdf.fullPath || '';
                    return (
                      <div
                        key={idx}
                        className="flex items-center justify-between p-2 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-xs text-[var(--text-primary)] group"
                      >
                        <div className="flex items-center space-x-2 truncate">
                          <FileText className="w-4 h-4 text-emerald-400 shrink-0" />
                          <span className="truncate">{pdfName}</span>
                        </div>
                        {fullPath ? (
                          <button
                            type="button"
                            onClick={() => rpc.call('studypack.openPdf', { fullPath })}
                            className="text-[10px] text-[var(--accent-color)] hover:underline font-semibold shrink-0 cursor-pointer"
                          >
                            Open PDF ↗
                          </button>
                        ) : (
                          <span className="text-[10px] text-[var(--text-muted)]">Ready</span>
                        )}
                      </div>
                    );
                  })}
                </div>
              </div>
            )}

            {/* TRANSFER FORMAT & ACTIONS */}
            <div className="p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[11px] font-bold uppercase text-[var(--text-muted)] tracking-wider">
                  Transfer Format (JSON)
                </span>
                <span className="text-[9px] px-1.5 py-0.2 rounded bg-emerald-500/10 text-emerald-400 font-mono">
                  v1.0.0
                </span>
              </div>
              <p className="text-[10px] text-[var(--text-muted)] leading-relaxed">
                Open transfer format with chapter vocabulary, kanji, KanjiVG stroke diagrams, and grammar.
              </p>
              <div className="flex items-center space-x-2 pt-1">
                <button
                  type="button"
                  onClick={handleExportTransfer}
                  disabled={isExporting}
                  className="flex-1 flex items-center justify-center space-x-1 py-1 px-2 rounded bg-[var(--bg-surface)] hover:bg-[var(--border-color)] border border-[var(--border-color)] text-[11px] text-[var(--text-primary)] font-medium cursor-pointer transition-colors"
                >
                  {isExporting ? <Loader2 className="w-3 h-3 animate-spin" /> : <Download className="w-3 h-3 text-emerald-400" />}
                  <span>Export JSON</span>
                </button>

                <button
                  type="button"
                  onClick={() => fileInputRef.current?.click()}
                  disabled={isImporting}
                  className="flex-1 flex items-center justify-center space-x-1 py-1 px-2 rounded bg-[var(--bg-surface)] hover:bg-[var(--border-color)] border border-[var(--border-color)] text-[11px] text-[var(--text-primary)] font-medium cursor-pointer transition-colors"
                >
                  {isImporting ? <Loader2 className="w-3 h-3 animate-spin" /> : <Upload className="w-3 h-3 text-teal-400" />}
                  <span>Import JSON</span>
                </button>
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".json"
                  className="hidden"
                  onChange={handleImportTransferFile}
                />
              </div>
            </div>

            <div>
              <div className="flex items-center justify-between mb-2">
                <h4 className="text-[11px] font-bold uppercase text-[var(--text-muted)] tracking-wider">
                  Vocabulary Sets ({vocabSets.length})
                </h4>
                {vocabSets.length > 0 && (
                  <select
                    value={selectedVocabSetIndex}
                    onChange={(e) => selectVocabSet(Number(e.target.value))}
                    className="bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-primary)] rounded px-2 py-0.5 text-[11px] focus:outline-none"
                  >
                    {vocabSets.map((vs: any, idx: number) => {
                      const title = vs.setTitle || (vs.number ? `Set ${vs.number} (${vs.size || vs.words?.length || 0} words)` : `Set ${idx + 1}`);
                      return (
                        <option key={vs.setId || vs.id || idx} value={idx}>
                          {title}
                        </option>
                      );
                    })}
                  </select>
                )}
              </div>

              {activeSet && (
                <div className="p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] space-y-2 max-h-72 overflow-y-auto">
                  <div className="font-semibold text-xs text-[var(--accent-color)]">
                    {activeSet.setTitle || (activeSet.number ? `Set ${activeSet.number}` : `Set ${selectedVocabSetIndex + 1}`)}
                  </div>
                  <div className="space-y-1.5 divide-y divide-[var(--border-color)]/40">
                    {activeSet.words?.map((w: any, idx: number) => {
                      const wordText = w.lemma || w.word || w.headword;
                      const readingText = w.reading;
                      const defText = w.definition || (w.sampleSentences && w.sampleSentences[0]) || '';
                      const count = w.occurrencesCount || w.bookOccurrences || 1;
                      return (
                        <div key={idx} className="pt-1.5 first:pt-0 text-xs">
                          <div className="flex items-baseline space-x-2">
                            <span className="font-bold text-[var(--text-primary)]">{wordText}</span>
                            {readingText && readingText !== wordText && (
                              <span className="text-[10px] text-[var(--text-muted)]">[{readingText}]</span>
                            )}
                            <span className="text-[10px] opacity-75 text-[var(--text-muted)] ml-auto">
                              {count}×
                            </span>
                          </div>
                          {defText && <p className="text-[11px] text-[var(--text-muted)] mt-0.5 line-clamp-2">{defText}</p>}
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>

            {/* MINED FLASHCARDS & ANKI DIRECT SYNC */}
            <div>
              <div className="flex items-center justify-between mb-2">
                <div className="flex items-center space-x-1.5">
                  <Bookmark className="w-3.5 h-3.5 text-emerald-400" />
                  <h4 className="text-[11px] font-bold uppercase text-[var(--text-muted)] tracking-wider">
                    Mined Cards ({minedCards.length})
                  </h4>
                </div>
                {minedCards.length > 0 && (
                  <button
                    onClick={handleSyncAnki}
                    disabled={isSyncingAnki}
                    className="flex items-center space-x-1 px-2.5 py-1 rounded bg-amber-500/20 hover:bg-amber-500/30 border border-amber-500/40 text-[11px] font-bold text-amber-300 disabled:opacity-50 transition-colors"
                  >
                    {isSyncingAnki ? (
                      <Loader2 className="w-3 h-3 animate-spin" />
                    ) : (
                      <Zap className="w-3 h-3 text-amber-400" />
                    )}
                    <span>Sync to Anki</span>
                  </button>
                )}
              </div>

              {syncMessage && (
                <div className="p-2 mb-2 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 text-xs flex items-center space-x-1.5">
                  <Check className="w-3.5 h-3.5 shrink-0" />
                  <span>{syncMessage}</span>
                </div>
              )}

              {minedCards.length === 0 ? (
                <div className="p-4 text-center rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-[11px] text-[var(--text-muted)]">
                  No cards mined yet. Highlight words while reading to create 1-click cards!
                </div>
              ) : (
                <div className="space-y-1.5 max-h-56 overflow-y-auto">
                  {minedCards.map((c: any) => (
                    <div
                      key={c.id}
                      className="p-2 rounded bg-[var(--bg-primary)] border border-[var(--border-color)]/60 text-xs"
                    >
                      <div className="flex items-baseline space-x-2">
                        <span className="font-bold text-[var(--text-primary)]">{c.prompt}</span>
                        {c.reading && <span className="text-[10px] text-[var(--text-muted)]">[{c.reading}]</span>}
                        <span className="text-[9px] px-1 py-0.2 rounded bg-black/40 text-[var(--text-muted)] ml-auto font-mono">
                          {c.chapterTitle || `Ch ${c.chapterNumber}`}
                        </span>
                      </div>
                      <p className="text-[11px] text-[var(--text-muted)] mt-0.5 line-clamp-1">{c.meaning}</p>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
};
