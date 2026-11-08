import React from 'react';
import type { BookRecord } from '../../types/api';
import { useReaderStore } from '../../stores/useReaderStore';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { BookMarked, Sparkles } from 'lucide-react';

interface BookCardProps {
  book: BookRecord;
}

export const BookCard: React.FC<BookCardProps> = ({ book }) => {
  const openBook = useReaderStore((s) => s.openBook);
  const inspectBook = useLibraryStore((s) => s.inspectBook);

  const coverUrl = book.coverCachePath
    ? `ir-app://covers/${book.bookId}.jpg`
    : '';

  return (
    <div
      className="book-card group relative bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-lg overflow-hidden flex flex-col hover:border-[var(--accent-color)] transition-all cursor-pointer shadow-sm hover:shadow-md"
      onClick={() => inspectBook(book.bookId)}
      onDoubleClick={(e) => {
        e.stopPropagation();
        openBook(book.bookId);
      }}
      title={`${book.title} - ${book.authors}\nDouble-click to read`}
    >
      <div className="relative aspect-[2/3] w-full bg-[var(--bg-primary)] overflow-hidden flex items-center justify-center">
        {coverUrl ? (
          <img
            src={coverUrl}
            alt={book.title}
            loading="lazy"
            className="w-full h-full object-cover transition-transform group-hover:scale-105 duration-200"
            onError={(e) => {
              (e.target as HTMLElement).style.display = 'none';
            }}
          />
        ) : (
          <BookMarked className="w-12 h-12 text-[var(--text-muted)] opacity-40" />
        )}

        {book.hasStudyPack && (
          <div
            className="absolute top-1.5 right-1.5 bg-emerald-600/90 text-white p-1 rounded shadow-sm text-[10px] font-bold flex items-center space-x-0.5"
            title="Study Pack Ready (Kanji/Vocab Master PDFs)"
          >
            <Sparkles className="w-3 h-3" />
            <span>STUDY</span>
          </div>
        )}

        <div className="absolute inset-x-0 bottom-0 bg-black/40 h-1">
          <div
            className="bg-[var(--accent-color)] h-full transition-all"
            style={{ width: `${Math.min(100, Math.max(0, book.overallCompletion || 0))}%` }}
          />
        </div>
      </div>

      <div className="p-2.5 flex-1 flex flex-col justify-between text-left">
        <div>
          <h4 className="text-xs font-semibold text-[var(--text-primary)] line-clamp-2 leading-snug group-hover:text-[var(--accent-color)]">
            {book.title}
          </h4>
          <p className="text-[11px] text-[var(--text-muted)] mt-1 truncate">{book.authors || 'Unknown Author'}</p>
        </div>

        <div className="mt-2 flex items-center justify-between pt-1 border-t border-[var(--border-color)]/50 text-[10px] text-[var(--text-muted)]">
          <span>{book.chapterCount} ch</span>
          <div className="flex items-center space-x-2">
            <button
              onClick={(e) => {
                e.stopPropagation();
                inspectBook(book.bookId);
              }}
              className="text-[var(--text-muted)] hover:text-[var(--text-primary)] font-medium"
              title="Book Details & Metadata"
            >
              Info ℹ️
            </button>
            <button
              onClick={(e) => {
                e.stopPropagation();
                openBook(book.bookId);
              }}
              className="text-[var(--accent-color)] hover:underline font-semibold"
            >
              Read →
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};
