import React, { useState } from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { FolderInput, Tag, Trash2, X, CheckSquare, ChevronDown, PencilLine, Star } from 'lucide-react';
import type { ReadingStatus } from '../../types/api';
import { STATUS_LABELS } from './ReadingFlags';

export const BulkActionBar: React.FC = () => {
  const {
    selectedBookIds,
    clearSelection,
    folders,
    tags,
    bulkMoveBooks,
    bulkAddTags,
    bulkDeleteSelected,
    totalBooks,
    selectAllMatching,
    openBulkEdit,
    setBookFlags,
    books,
  } = useLibraryStore();

  const [showFolderDropdown, setShowFolderDropdown] = useState(false);
  const [showTagDropdown, setShowTagDropdown] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);

  if (selectedBookIds.length === 0) return null;

  const count = selectedBookIds.length;

  // the list shows one page; "select all" reaches every book the current filters match
  const handleSelectAll = async () => {
    setIsProcessing(true);
    try {
      await selectAllMatching();
    } catch (err: any) {
      alert(`Select all failed: ${err.message || String(err)}`);
    } finally {
      setIsProcessing(false);
    }
  };

  // favourite all selected books, or take them all off when they already are (judged on the books loaded on this page)
  const selectedOnPage = books.filter((b) => selectedBookIds.includes(b.bookId));
  const allFavorite = selectedOnPage.length > 0 && selectedOnPage.every((b) => b.isFavorite);

  const handleFlags = async (flags: { favorite?: boolean; readingStatus?: ReadingStatus }) => {
    setIsProcessing(true);
    try {
      await setBookFlags(selectedBookIds, flags);
    } catch (err: any) {
      alert(`Could not update the lists: ${err.message || String(err)}`);
    } finally {
      setIsProcessing(false);
    }
  };

  const handleMove = async (folderId: string | null) => {
    setIsProcessing(true);
    try {
      await bulkMoveBooks(folderId);
      setShowFolderDropdown(false);
    } catch (err: any) {
      alert(`Move failed: ${err.message || String(err)}`);
    } finally {
      setIsProcessing(false);
    }
  };

  const handleAddTag = async (tagId: string) => {
    setIsProcessing(true);
    try {
      await bulkAddTags([tagId]);
      setShowTagDropdown(false);
    } catch (err: any) {
      alert(`Add tag failed: ${err.message || String(err)}`);
    } finally {
      setIsProcessing(false);
    }
  };

  const handleDelete = async () => {
    if (!confirm(`Are you sure you want to permanently delete ${count} selected books from your library?`)) {
      return;
    }
    setIsProcessing(true);
    try {
      await bulkDeleteSelected();
    } catch (err: any) {
      alert(`Delete failed: ${err.message || String(err)}`);
    } finally {
      setIsProcessing(false);
    }
  };

  return (
    <div className="fixed bottom-6 left-1/2 -translate-x-1/2 z-40 bg-[var(--bg-surface)] border border-[var(--accent-color)]/60 rounded-full shadow-2xl px-5 py-2.5 flex items-center space-x-4 text-xs animate-in slide-in-from-bottom duration-200 backdrop-blur-md">
      <div className="flex items-center space-x-2 font-bold text-[var(--accent-text)] border-r border-[var(--border-color)] pr-3">
        <CheckSquare className="w-4 h-4" />
        <span>{count} selected</span>
        {totalBooks > count && (
          <button
            type="button"
            disabled={isProcessing}
            onClick={handleSelectAll}
            className="font-medium text-[var(--text-muted)] underline decoration-dotted hover:text-[var(--accent-text)] disabled:opacity-50"
            title="Select every book the current filters match, on all pages"
          >
            select all {totalBooks}
          </button>
        )}
      </div>

      <div className="flex items-center space-x-2">
        {/* MOVE TO FOLDER DROPDOWN */}
        <div className="relative">
          <button
            type="button"
            disabled={isProcessing}
            onClick={() => {
              setShowFolderDropdown(!showFolderDropdown);
              setShowTagDropdown(false);
            }}
            className="flex items-center space-x-1.5 px-3 py-1.5 rounded-full bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-[var(--text-primary)] font-medium transition-colors disabled:opacity-50"
          >
            <FolderInput className="w-3.5 h-3.5 text-amber-400" />
            <span>Move to...</span>
            <ChevronDown className="w-3 h-3 text-[var(--text-muted)]" />
          </button>

          {showFolderDropdown && (
            <div className="absolute bottom-full mb-2 left-0 w-56 bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-lg shadow-xl overflow-hidden py-1 max-h-60 overflow-y-auto">
              <button
                type="button"
                onClick={() => handleMove(null)}
                className="w-full text-left px-3 py-1.5 hover:bg-[var(--bg-primary)] text-xs text-[var(--text-muted)] truncate"
              >
                📥 (Uncategorized / Root)
              </button>
              {folders.map((f) => (
                <button
                  key={f.folderId}
                  type="button"
                  onClick={() => handleMove(f.folderId)}
                  className="w-full text-left px-3 py-1.5 hover:bg-[var(--bg-primary)] text-xs text-[var(--text-primary)] truncate flex items-center space-x-1.5"
                >
                  <span>📁</span>
                  <span className="truncate">{f.name}</span>
                </button>
              ))}
            </div>
          )}
        </div>

        {/* ADD TAG DROPDOWN */}
        {tags.length > 0 && (
          <div className="relative">
            <button
              type="button"
              disabled={isProcessing}
              onClick={() => {
                setShowTagDropdown(!showTagDropdown);
                setShowFolderDropdown(false);
              }}
              className="flex items-center space-x-1.5 px-3 py-1.5 rounded-full bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-[var(--text-primary)] font-medium transition-colors disabled:opacity-50"
            >
              <Tag className="w-3.5 h-3.5 text-pink-400" />
              <span>Add Tag...</span>
              <ChevronDown className="w-3 h-3 text-[var(--text-muted)]" />
            </button>

            {showTagDropdown && (
              <div className="absolute bottom-full mb-2 left-0 w-48 bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-lg shadow-xl overflow-hidden py-1 max-h-60 overflow-y-auto">
                {tags.map((t) => (
                  <button
                    key={t.tagId}
                    type="button"
                    onClick={() => handleAddTag(t.tagId)}
                    className="w-full text-left px-3 py-1.5 hover:bg-[var(--bg-primary)] text-xs text-[var(--text-primary)] truncate flex items-center space-x-1.5"
                  >
                    <span className="w-2.5 h-2.5 rounded-full shrink-0" style={{ backgroundColor: t.colorHex || '#3b82f6' }} />
                    <span className="truncate">{t.name}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
        )}

        {/* EDIT METADATA (series, volume numbers, author, publisher, tags ...) */}
        <button
          type="button"
          data-testid="bulk-favorite"
          onClick={() => void handleFlags({ favorite: !allFavorite })}
          disabled={isProcessing}
          className="flex items-center space-x-1.5 px-3 py-1.5 rounded-full bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-[var(--text-primary)] font-medium transition-colors disabled:opacity-50"
          title={allFavorite ? 'Remove all selected books from favourites' : 'Add all selected books to favourites'}
        >
          <Star className="w-3.5 h-3.5 text-[var(--accent-text)]" fill={allFavorite ? 'currentColor' : 'none'} />
          <span>{allFavorite ? 'Unfavourite' : 'Favourite'}</span>
        </button>

        <select
          data-testid="bulk-reading-list"
          value=""
          disabled={isProcessing}
          onChange={(e) => { if (e.target.value !== '') void handleFlags({ readingStatus: (e.target.value === 'none' ? '' : e.target.value) as ReadingStatus }); }}
          className="px-3 py-1.5 rounded-full bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-primary)] font-medium disabled:opacity-50"
          title="Put all selected books on a reading list"
        >
          <option value="">Reading list…</option>
          {(Object.keys(STATUS_LABELS) as Exclude<ReadingStatus, ''>[]).map((st) => <option key={st} value={st}>{STATUS_LABELS[st]}</option>)}
          <option value="none">Remove from lists</option>
        </select>

        <button
          type="button"
          disabled={isProcessing}
          onClick={openBulkEdit}
          className="flex items-center space-x-1.5 px-3 py-1.5 rounded-full bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-[var(--text-primary)] font-medium transition-colors disabled:opacity-50"
          title="Change the series, volume numbers, author, publisher or tags of all selected books at once"
        >
          <PencilLine className="w-3.5 h-3.5 text-sky-400" />
          <span>Edit...</span>
        </button>

        {/* DELETE BUTTON */}
        <button
          type="button"
          disabled={isProcessing}
          onClick={handleDelete}
          className="flex items-center space-x-1 px-3 py-1.5 rounded-full bg-red-950/60 border border-red-800 text-red-300 hover:bg-red-900/60 transition-colors disabled:opacity-50 font-medium"
          title="Delete selected books"
        >
          <Trash2 className="w-3.5 h-3.5" />
          <span>Delete</span>
        </button>
      </div>

      <button
        type="button"
        onClick={clearSelection}
        className="p-1 rounded-full text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] ml-1"
        title="Clear Selection"
      >
        <X className="w-4 h-4" />
      </button>
    </div>
  );
};

