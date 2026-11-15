import React, { useState } from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { Bookmark, X, Plus, Trash2, ArrowUpRight } from 'lucide-react';

export const BookmarksDrawer: React.FC = () => {
  const {
    isBookmarksOpen,
    toggleBookmarks,
    bookmarks,
    addBookmark,
    removeBookmark,
    loadChapter,
    currentChapterIndex,
    chapters,
  } = useReaderStore();

  const [noteInput, setNoteInput] = useState('');
  const [showAddForm, setShowAddForm] = useState(false);
  const [selection, setSelection] = useState('');

  if (!isBookmarksOpen) return null;

  const currentChapter = chapters[currentChapterIndex] || {
    title: `Chapter ${currentChapterIndex + 1}`,
  };

  const handleCreateBookmark = async (e: React.FormEvent) => {
    e.preventDefault();
    await addBookmark(noteInput.trim(), selection);
    setNoteInput('');
    setSelection('');
    setShowAddForm(false);
  };

  return (
    <div className="fixed inset-y-0 right-0 w-80 bg-[var(--bg-surface)] border-l border-[var(--border-color)] shadow-2xl flex flex-col z-40 select-none animate-in slide-in-from-right duration-200">
      <div className="p-3 border-b border-[var(--border-color)] flex items-center justify-between bg-[var(--bg-primary)]">
        <div className="flex items-center space-x-2">
          <Bookmark className="w-4 h-4 text-[var(--accent-text)]" />
          <span className="text-xs font-bold text-[var(--text-primary)]">
            Bookmarks ({bookmarks.length})
          </span>
        </div>
        <button
          onClick={toggleBookmarks}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]"
        >
          <X className="w-4 h-4" />
        </button>
      </div>

      <div className="p-3 border-b border-[var(--border-color)] bg-black/10">
        {!showAddForm ? (
          <button
            onClick={() => {
              // the words selected in the book page (if any) become the bookmark's line
              const frame = document.getElementById('reader-frame') as HTMLIFrameElement | null;
              setSelection((frame?.contentWindow?.getSelection()?.toString() ?? '').trim());
              setShowAddForm(true);
            }}
            className="w-full py-1.5 px-3 rounded bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold text-xs flex items-center justify-center space-x-1.5 hover:opacity-90 shadow-sm"
          >
            <Plus className="w-3.5 h-3.5" />
            <span>Bookmark here</span>
          </button>
        ) : (
          <form onSubmit={handleCreateBookmark} className="space-y-2">
            <div className="text-[11px] text-[var(--text-muted)]" data-testid="bookmark-form-hint">
              {selection
                ? <>Selected text: <b className="text-[var(--text-primary)]">{selection.length > 60 ? selection.slice(0, 60) + '…' : selection}</b></>
                : <>{currentChapter.title}. Select words in the text first to bookmark them.</>}
            </div>
            <input
              type="text"
              value={noteInput}
              onChange={(e) => setNoteInput(e.target.value)}
              placeholder="Optional personal note..."
              autoFocus
              className="w-full px-2.5 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
            />
            <div className="flex items-center space-x-1.5 justify-end">
              <button
                type="button"
                onClick={() => setShowAddForm(false)}
                className="px-2 py-0.5 rounded text-[11px] text-[var(--text-muted)] hover:text-[var(--text-primary)]"
              >
                Cancel
              </button>
              <button
                type="submit"
                className="px-2.5 py-1 rounded bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold text-xs hover:opacity-90"
              >
                Save
              </button>
            </div>
          </form>
        )}
      </div>

      <div className="flex-1 overflow-y-auto p-3 space-y-2">
        {bookmarks.length === 0 ? (
          <div className="p-6 text-center text-xs text-[var(--text-muted)]">
            No bookmarks saved for this book yet. Click above to save your position!
          </div>
        ) : (
          bookmarks.map((bm) => (
            <div
              key={bm.bookmarkId}
              className="p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)]/70 hover:border-[var(--accent-color)]/60 text-xs transition-colors group space-y-1.5"
            >
              <div className="flex items-baseline justify-between">
                <span className="bookmark-label font-bold text-[var(--text-primary)] truncate max-w-[200px]" title={bm.selectedText || bm.label}>
                  {bm.label || bm.selectedText || `Ch${bm.chapterIndex + 1}`}
                </span>
                <span className="text-[10px] text-[var(--text-muted)] font-mono">
                  {new Date(bm.createdAtUtc).toLocaleDateString()}
                </span>
              </div>

              {bm.userNote && (
                <p className="text-[11px] text-[var(--text-muted)] italic line-clamp-2">
                  "{bm.userNote}"
                </p>
              )}

              <div className="pt-1 flex items-center justify-between border-t border-[var(--border-color)]/30">
                <button
                  onClick={() => {
                    loadChapter(bm.spineHref, bm.chapterIndex);
                    toggleBookmarks();
                  }}
                  className="flex items-center space-x-1 text-[11px] text-[var(--accent-text)] hover:underline font-medium"
                >
                  <span>Jump to chapter</span>
                  <ArrowUpRight className="w-3 h-3" />
                </button>

                <button
                  onClick={() => removeBookmark(bm.bookmarkId)}
                  className="p-1 rounded text-[var(--text-muted)] hover:text-red-400 hover:bg-[var(--bg-surface)] transition-colors"
                  title="Delete bookmark"
                >
                  <Trash2 className="w-3.5 h-3.5" />
                </button>
              </div>
            </div>
          ))
        )}
      </div>
    </div>
  );
};

