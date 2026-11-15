import React from 'react';
import type { BookRecord } from '../../types/api';
import { useReaderStore } from '../../stores/useReaderStore';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { BookMarked, Sparkles } from 'lucide-react';
import { BookBadgeSlot } from '../plugins/PluginSlots';
import { FavoriteStar, STATUS_LABELS, STATUS_SHORT } from './ReadingFlags';

interface BookCardProps {
  book: BookRecord;
}

/**
 * One book in the gallery. Top to bottom: the title bar (title, then the chapter count on the left, the author on the right; the selection
 * box floats over its top-right corner), the cover, and a footer with Read (a button in the accent colour) on the left and Info (a link) on the right.
 * Shift + click adds the book to the selection, or removes it when it is already selected. A plain click opens the details.
 */
export const BookCard: React.FC<BookCardProps> = ({ book }) => {
  const openBook = useReaderStore((s) => s.openBook);
  const inspectBook = useLibraryStore((s) => s.inspectBook);
  const selectedBookIds = useLibraryStore((s) => s.selectedBookIds);
  const toggleSelectBook = useLibraryStore((s) => s.toggleSelectBook);
  const setBookFlags = useLibraryStore((s) => s.setBookFlags);

  const isSelected = selectedBookIds.includes(book.bookId);
  const anySelected = selectedBookIds.length > 0;
  const status = (book.readingStatus || book.seriesReadingStatus || '') as '' | 'reading' | 'planned' | 'completed';

  const coverUrl = book.coverCachePath
    ? `ir-app://covers/${book.bookId}.jpg`
    : '';

  return (
    <div
      data-ir="book-card"
      data-book-id={book.bookId}
      className={`book-card group relative bg-[var(--bg-surface)] border rounded-[var(--radius-card)] overflow-hidden flex flex-col transition-all cursor-pointer select-none shadow-sm hover:shadow-md ${
        isSelected
          ? 'border-[var(--accent-color)] ring-2 ring-[var(--accent-color)]/40'
          : 'border-[var(--border-color)] hover:border-[var(--accent-color)]'
      }`}
      onMouseDown={(e) => {
        if (e.shiftKey) e.preventDefault();             // no text range selection when shift-clicking
      }}
      onClick={(e) => {
        if (e.shiftKey) {
          e.preventDefault();
          toggleSelectBook(book.bookId);
          return;
        }
        inspectBook(book.bookId);
      }}
      onDoubleClick={(e) => {
        if (e.shiftKey) return;
        e.stopPropagation();
        openBook(book.bookId);
      }}
      title={`${book.title} - ${book.authors}\nClick for details, double-click to read, Shift+click to ${isSelected ? 'deselect' : 'select'}`}
    >
      {/* TITLE BAR */}
      <div className="book-card-header relative px-2.5 pt-2 pb-1.5 pr-9 text-left">
        <h4 className="book-card-title text-xs font-semibold text-[var(--text-primary)] line-clamp-2 min-h-[2.1rem] leading-snug group-hover:text-[var(--accent-text)]">
          {book.title}
        </h4>
        <div className="book-card-meta mt-0.5 flex items-center justify-between gap-2 text-[10px] text-[var(--text-muted)]">
          <span className="flex items-center gap-1 shrink-0">
            <span className="book-card-chapters">{book.chapterCount} ch</span>
            <FavoriteStar
              own={!!book.isFavorite}
              inherited={!!book.seriesIsFavorite}
              size={13}
              onToggle={() => void setBookFlags([book.bookId], { favorite: !book.isFavorite })}
            />
          </span>
          <span className="book-card-author truncate">{book.authors || 'Unknown Author'}</span>
        </div>

        {/* SELECTION BOX: floats over the title bar's top-right corner */}
        <div
          onClick={(e) => {
            e.stopPropagation();
            toggleSelectBook(book.bookId);
          }}
          className={`book-card-select absolute top-1.5 right-1.5 z-10 w-5 h-5 border flex items-center justify-center transition-all cursor-pointer ${
            isSelected
              ? 'bg-[var(--accent-color)] border-[var(--accent-color)] text-[var(--accent-contrast)]'
              : `bg-[var(--bg-surface)] border-[var(--text-muted)] text-transparent hover:border-[var(--accent-color)] ${
                  anySelected ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'
                }`
          }`}
          title={isSelected ? 'Deselect book (Shift+click the card)' : 'Select book (Shift+click the card)'}
        >
          <span className="text-[11px] font-bold">✓</span>
        </div>
      </div>

      {/* COVER */}
      <div className="book-card-cover relative aspect-[2/3] w-full bg-[var(--bg-primary)] overflow-hidden flex items-center justify-center">
        {coverUrl ? (
          <img
            src={coverUrl}
            alt={book.title}
            loading="lazy"
            draggable={false}
            className="w-full h-full object-cover transition-transform group-hover:scale-105 duration-200"
            onError={(e) => {
              (e.target as HTMLElement).style.display = 'none';
            }}
          />
        ) : (
          <BookMarked className="w-12 h-12 text-[var(--text-muted)] opacity-40" />
        )}

        {status && (
          <div
            data-testid="book-card-status"
            className="absolute top-1.5 left-1.5 px-1.5 py-0.5 bg-[var(--bg-surface)] text-[var(--text-primary)] border border-[var(--border-color)] text-[10px] font-semibold shadow-sm"
            title={`${STATUS_LABELS[status]}${book.readingStatus ? '' : ' (from the series)'}`}
          >
            {STATUS_SHORT[status]}
          </div>
        )}

        {book.hasStudyPack && (
          <div
            className="absolute top-1.5 right-1.5 bg-emerald-700 text-white p-1 shadow-sm text-[10px] font-bold flex items-center space-x-0.5"
            title="Study Pack Ready (Kanji/Vocab Master PDFs)"
          >
            <Sparkles className="w-3 h-3" />
            <span>STUDY</span>
          </div>
        )}

        <div className="absolute bottom-2 left-1.5 z-10 flex gap-1">
          <BookBadgeSlot books={[book]} context="card" />
        </div>

        <div className="absolute inset-x-0 bottom-0 bg-black/40 h-1">
          <div
            className="bg-[var(--accent-color)] h-full transition-all"
            style={{ width: `${Math.min(100, Math.max(0, book.overallCompletion || 0))}%` }}
          />
        </div>
      </div>

      {/* FOOTER: Read on the left, Info on the right */}
      <div className="book-card-footer flex items-center justify-between gap-2 px-2.5 py-2 border-t border-[var(--border-color)]/50">
        <button
          onClick={(e) => {
            e.stopPropagation();
            openBook(book.bookId);
          }}
          className="book-card-read px-4 py-1.5 bg-[var(--accent-color)] text-[var(--accent-contrast)] text-xs font-semibold shadow-sm hover:opacity-90 transition-opacity"
        >
          Read →
        </button>
        <button
          onClick={(e) => {
            e.stopPropagation();
            inspectBook(book.bookId);
          }}
          className="book-card-info px-1 py-1.5 text-xs font-medium text-[var(--accent-text)] underline underline-offset-4 decoration-1 hover:decoration-2"
          title="Book Details & Metadata"
        >
          Info ℹ️
        </button>
      </div>
    </div>
  );
};
