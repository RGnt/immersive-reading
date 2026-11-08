import React, { useMemo } from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { useReaderStore } from '../../stores/useReaderStore';
import { useConfigStore } from '../../stores/useConfigStore';
import { BookCard } from './BookCard';
import { BulkActionBar } from './BulkActionBar';
import type { BookRecord } from '../../types/api';
import {
  ChevronLeft,
  ChevronRight,
  Loader2,
  BookOpen,
  Info,
  Library,
  Layers,
  ArrowUpDown,
  Filter,
  EyeOff,
  User,
  Folder,
  Tag,
  Building2,
} from 'lucide-react';

export const LibraryGrid: React.FC = () => {
  const {
    books,
    totalBooks,
    offset,
    limit,
    isLoading,
    fetchBooks,
    viewMode,
    inspectBook,
    selectedBookIds,
    toggleSelectBook,
    selectAllOnPage,
    clearSelection,
  } = useLibraryStore();
  const openBook = useReaderStore((s) => s.openBook);
  const { config, updateLibrarySettings, openSettings } = useConfigStore();

  const librarySettings = config?.library || {
    primaryGrouping: 'series',
    sortWithinGrouping: 'series_and_volume',
    enableShelves: true,
    excludedFolderIds: [],
    excludedTagIds: [],
    hideCompleted: false,
    defaultViewMode: 'grid',
  };

  const currentPage = Math.floor(offset / limit) + 1;
  const totalPages = Math.max(1, Math.ceil(totalBooks / limit));

  // 1. FILTERING: Remove excluded categories, tags, or completed books
  const filteredBooks = useMemo(() => {
    return books.filter((b) => {
      // Excluded folders / categories
      if (
        librarySettings.excludedFolderIds &&
        librarySettings.excludedFolderIds.length > 0 &&
        b.folderId &&
        librarySettings.excludedFolderIds.includes(b.folderId)
      ) {
        return false;
      }

      // Excluded tags
      if (librarySettings.excludedTagIds && librarySettings.excludedTagIds.length > 0 && b.tags) {
        const bookTagIds = Array.isArray(b.tags)
          ? b.tags.map((t: any) => (typeof t === 'string' ? t : t.tagId))
          : [];
        if (bookTagIds.some((id) => librarySettings.excludedTagIds?.includes(id))) {
          return false;
        }
      }

      // Hide completed books
      if (librarySettings.hideCompleted && (b.overallCompletion || 0) >= 100) {
        return false;
      }

      return true;
    });
  }, [books, librarySettings.excludedFolderIds, librarySettings.excludedTagIds, librarySettings.hideCompleted]);

  // 2. SORTING HELPER
  const sortBooks = (items: BookRecord[]): BookRecord[] => {
    const mode = librarySettings.sortWithinGrouping || 'series_and_volume';
    return [...items].sort((a, b) => {
      switch (mode) {
        case 'series_and_volume': {
          // Default: If series match, sort by volume / series index ascending
          if (a.seriesName && b.seriesName && a.seriesName === b.seriesName) {
            const idxA = a.seriesIndex ?? 999999;
            const idxB = b.seriesIndex ?? 999999;
            if (idxA !== idxB) return idxA - idxB;
          }
          return (a.sortTitle || a.title).localeCompare(b.sortTitle || b.title, 'ja');
        }
        case 'title':
          return (a.sortTitle || a.title).localeCompare(b.sortTitle || b.title, 'ja');
        case 'author':
          return (a.authorSort || a.authors || '').localeCompare(b.authorSort || b.authors || '', 'ja');
        case 'added':
          return (b.addedAtUtc || '').localeCompare(a.addedAtUtc || '');
        case 'custom':
          return (a.customSortOrder || 0) - (b.customSortOrder || 0);
        case 'completion':
          return (b.overallCompletion || 0) - (a.overallCompletion || 0);
        default:
          return 0;
      }
    });
  };

  // 3. GROUPING INTO SHELVES
  const shelves = useMemo(() => {
    const grouping = librarySettings.primaryGrouping || 'series';
    const groups: { id: string; title: string; subtitle?: string; books: BookRecord[]; icon: 'series' | 'author' | 'category' | 'publisher' | 'tag' | 'flat' }[] = [];
    const groupMap: Record<string, BookRecord[]> = {};

    if (grouping === 'none') {
      return [{
        id: 'all',
        title: 'All Books',
        books: sortBooks(filteredBooks),
        icon: 'flat' as const,
      }];
    }

    for (const b of filteredBooks) {
      let key = '';
      if (grouping === 'series') {
        key = b.seriesName ? b.seriesName.trim() : '__standalone__';
      } else if (grouping === 'author') {
        key = b.authors ? b.authors.trim() : 'Unknown Author';
      } else if (grouping === 'category') {
        key = b.folderPathBreadcrumb ? b.folderPathBreadcrumb.trim() : 'Uncategorized';
      } else if (grouping === 'publisher') {
        key = b.imprint || b.publisher ? (b.imprint || b.publisher)!.trim() : 'Independent / Unknown Publisher';
      } else if (grouping === 'tag') {
        const firstTag = Array.isArray(b.tags) && b.tags.length > 0
          ? (typeof b.tags[0] === 'string' ? b.tags[0] : (b.tags[0] as any).name)
          : 'Untagged';
        key = firstTag;
      }

      if (!groupMap[key]) groupMap[key] = [];
      groupMap[key].push(b);
    }

    const sortedKeys = Object.keys(groupMap).sort((a, b) => {
      if (a === '__standalone__') return 1;
      if (b === '__standalone__') return -1;
      return a.localeCompare(b, 'ja');
    });

    for (const key of sortedKeys) {
      const displayTitle = key === '__standalone__' ? 'Standalones & Single Volumes' : key;
      const iconType =
        grouping === 'series'
          ? 'series'
          : grouping === 'author'
          ? 'author'
          : grouping === 'category'
          ? 'category'
          : grouping === 'publisher'
          ? 'publisher'
          : 'tag';

      groups.push({
        id: key,
        title: displayTitle,
        books: sortBooks(groupMap[key]),
        icon: iconType,
      });
    }

    return groups;
  }, [filteredBooks, librarySettings.primaryGrouping, librarySettings.sortWithinGrouping]);

  const handlePrevPage = () => {
    if (offset > 0) {
      fetchBooks(Math.max(0, offset - limit));
    }
  };

  const handleNextPage = () => {
    if (offset + limit < totalBooks) {
      fetchBooks(offset + limit);
    }
  };

  const renderShelfIcon = (iconType: string) => {
    switch (iconType) {
      case 'author':
        return <User className="w-4 h-4 text-emerald-400 shrink-0" />;
      case 'category':
        return <Folder className="w-4 h-4 text-amber-400 shrink-0" />;
      case 'publisher':
        return <Building2 className="w-4 h-4 text-purple-400 shrink-0" />;
      case 'tag':
        return <Tag className="w-4 h-4 text-pink-400 shrink-0" />;
      case 'series':
      default:
        return <Library className="w-4 h-4 text-[var(--accent-color)] shrink-0" />;
    }
  };

  const renderContent = () => {
    if (isLoading && books.length === 0) {
      return (
        <div className="h-full flex items-center justify-center space-x-2 text-[var(--text-muted)]">
          <Loader2 className="w-5 h-5 animate-spin text-[var(--accent-color)]" />
          <span>Loading library...</span>
        </div>
      );
    }

    if (filteredBooks.length === 0) {
      return (
        <div className="h-full flex flex-col items-center justify-center text-[var(--text-muted)] p-8">
          <p className="text-base font-medium">No books match current filters</p>
          <p className="text-xs mt-1">
            {books.length > 0
              ? 'Books are hidden by active category or tag exclusion settings.'
              : 'Try adjusting your search query or folder selection.'}
          </p>
          {books.length > 0 && (
            <button
              onClick={() => openSettings('library')}
              className="mt-3 px-3 py-1.5 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 shadow-sm"
            >
              Adjust Shelf Filter Exclusions
            </button>
          )}
        </div>
      );
    }

    // TABLE VIEW (with shelf grouping headers)
    if (viewMode === 'table') {
      return (
        <div className="w-full overflow-x-auto">
          <table className="w-full text-left text-xs border-collapse">
            <thead>
              <tr className="border-b border-[var(--border-color)] text-[var(--text-muted)] uppercase text-[10px] tracking-wider bg-[var(--bg-surface)] select-none">
                <th className="py-2.5 px-3 w-8 text-center">
                  <input
                    type="checkbox"
                    checked={filteredBooks.length > 0 && selectedBookIds.length >= filteredBooks.length}
                    onChange={() => {
                      if (selectedBookIds.length >= filteredBooks.length) {
                        clearSelection();
                      } else {
                        selectAllOnPage();
                      }
                    }}
                    className="rounded border-[var(--border-color)] text-[var(--accent-color)] focus:ring-0 cursor-pointer"
                    title="Select all books"
                  />
                </th>
                <th className="py-2.5 px-3 w-32">Actions</th>
                <th className="py-2.5 px-3">Title</th>
                <th className="py-2.5 px-3">Series & Vol</th>
                <th className="py-2.5 px-3">Author(s)</th>
                <th className="py-2.5 px-3">Publisher / Imprint</th>
                <th className="py-2.5 px-3">ISBN</th>
                <th className="py-2.5 px-3">Folder</th>
              </tr>
            </thead>
            <tbody>
              {shelves.map((shelf) => (
                <React.Fragment key={shelf.id}>
                  {librarySettings.primaryGrouping !== 'none' && (
                    <tr className="bg-black/30 border-y border-[var(--border-color)]">
                      <td colSpan={8} className="py-2 px-3">
                        <div className="flex items-center space-x-2 font-bold text-xs text-[var(--text-primary)]">
                          {renderShelfIcon(shelf.icon)}
                          <span>{shelf.title}</span>
                          <span className="text-[10px] font-normal text-[var(--text-muted)]">
                            ({shelf.books.length} {shelf.books.length === 1 ? 'book' : 'books'})
                          </span>
                        </div>
                      </td>
                    </tr>
                  )}
                  {shelf.books.map((b) => (
                    <tr
                      key={b.bookId}
                      onDoubleClick={() => openBook(b.bookId)}
                      className={`hover:bg-[var(--bg-surface)] transition-colors cursor-pointer group border-b border-[var(--border-color)]/50 ${
                        selectedBookIds.includes(b.bookId) ? 'bg-[var(--accent-color)]/10' : ''
                      }`}
                    >
                      <td className="py-2 px-3 text-center shrink-0" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          checked={selectedBookIds.includes(b.bookId)}
                          onChange={() => toggleSelectBook(b.bookId)}
                          className="rounded border-[var(--border-color)] text-[var(--accent-color)] focus:ring-0 cursor-pointer"
                        />
                      </td>
                      <td className="py-2 px-3 shrink-0" onClick={(e) => e.stopPropagation()}>
                        <div className="flex items-center space-x-1.5">
                          <button
                            onClick={() => openBook(b.bookId)}
                            className="flex items-center space-x-1 px-2 py-1 rounded bg-[var(--accent-color)] text-black font-semibold text-[11px] hover:opacity-90"
                            title="Read book"
                          >
                            <BookOpen className="w-3 h-3" />
                            <span>Read</span>
                          </button>
                          <button
                            onClick={() => inspectBook(b.bookId)}
                            className="flex items-center space-x-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:border-[var(--accent-color)] text-[11px]"
                            title="Book Details & Metadata"
                          >
                            <Info className="w-3 h-3" />
                            <span>Info</span>
                          </button>
                        </div>
                      </td>
                      <td className="py-2 px-3 font-medium text-[var(--text-primary)]">
                        <div className="flex items-center space-x-1.5">
                          <span className="truncate max-w-sm">{b.title}</span>
                          {b.isMetadataEdited && (
                            <span className="text-[9px] px-1 py-0.2 rounded bg-amber-500/20 text-amber-300 border border-amber-500/30">
                              Edited
                            </span>
                          )}
                        </div>
                      </td>
                      <td className="py-2 px-3 text-[var(--text-muted)]">
                        {b.seriesName ? (
                          <span className="inline-flex items-center space-x-1">
                            <span>{b.seriesName}</span>
                            <span className="text-[10px] px-1.5 py-0.2 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--accent-color)] font-mono">
                              #{b.seriesIndex ?? 1}
                            </span>
                          </span>
                        ) : (
                          '—'
                        )}
                      </td>
                      <td className="py-2 px-3 text-[var(--text-muted)] truncate max-w-xs">{b.authors || '—'}</td>
                      <td className="py-2 px-3 text-[var(--text-muted)] truncate max-w-xs">{b.imprint || b.publisher || '—'}</td>
                      <td className="py-2 px-3 text-[var(--text-muted)] font-mono text-[11px]">{b.isbn || '—'}</td>
                      <td className="py-2 px-3 text-[var(--text-muted)]">{b.folderPathBreadcrumb || '—'}</td>
                    </tr>
                  ))}
                </React.Fragment>
              ))}
            </tbody>
          </table>
        </div>
      );
    }

    // SHELVES & GRID VIEW
    return (
      <div className="space-y-8 pb-4">
        {shelves.map((shelf) => (
          <div key={shelf.id} className="shelf-group space-y-3">
            {librarySettings.primaryGrouping !== 'none' && (
              <div className="flex items-center justify-between pb-1.5 border-b border-[var(--border-color)]">
                <div className="flex items-center space-x-2.5">
                  {renderShelfIcon(shelf.icon)}
                  <h3 className="font-bold text-sm text-[var(--text-primary)] tracking-wide">{shelf.title}</h3>
                  <span className="text-xs px-2 py-0.5 rounded-full bg-[var(--bg-surface)] border border-[var(--border-color)] text-[var(--text-muted)] font-medium">
                    {shelf.books.length} {shelf.books.length === 1 ? 'book' : 'books'}
                  </span>
                </div>
              </div>
            )}

            {/* VISUAL SHELF RACK CONTAINER */}
            <div
              className={
                librarySettings.enableShelves
                  ? 'shelf-rack relative bg-gradient-to-b from-transparent via-black/10 to-black/25 p-3 pb-5 rounded-lg border-b-4 border-amber-950/40 shadow-inner'
                  : ''
              }
            >
              <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-4">
                {shelf.books.map((book) => (
                  <BookCard key={book.bookId} book={book} />
                ))}
              </div>
              {librarySettings.enableShelves && (
                <div className="absolute inset-x-0 bottom-0 h-1.5 bg-gradient-to-r from-amber-950 via-amber-800 to-amber-950 rounded-b opacity-80" />
              )}
            </div>
          </div>
        ))}
      </div>
    );
  };

  const activeExcludedCount =
    (librarySettings.excludedFolderIds?.length || 0) + (librarySettings.excludedTagIds?.length || 0);

  return (
    <div className="flex-1 flex flex-col h-full overflow-hidden bg-[var(--bg-primary)]">
      {/* PERSISTENT SHELF GROUPING & SORTING TOOLBAR */}
      <div className="px-4 py-2 border-b border-[var(--border-color)] bg-[var(--bg-surface)] flex items-center justify-between text-xs gap-3 shrink-0 select-none flex-wrap">
        <div className="flex items-center space-x-3 flex-wrap gap-y-1">
          {/* Primary Shelf Grouping Selector */}
          <div className="flex items-center space-x-1.5">
            <Layers className="w-3.5 h-3.5 text-[var(--accent-color)]" />
            <span className="text-[11px] font-semibold text-[var(--text-muted)] uppercase">Group By:</span>
            <select
              value={librarySettings.primaryGrouping || 'series'}
              onChange={(e) => updateLibrarySettings({ primaryGrouping: e.target.value as any })}
              className="px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
            >
              <option value="series">Series Shelves</option>
              <option value="author">Author Shelves</option>
              <option value="category">Category / Folder</option>
              <option value="publisher">Publisher / Imprint</option>
              <option value="tag">Primary Tag</option>
              <option value="none">Flat (No Grouping)</option>
            </select>
          </div>

          {/* Sort Within Shelves Selector (Defaults to series & volume number) */}
          <div className="flex items-center space-x-1.5">
            <ArrowUpDown className="w-3.5 h-3.5 text-[var(--text-muted)]" />
            <span className="text-[11px] font-semibold text-[var(--text-muted)] uppercase">Sort Shelf:</span>
            <select
              value={librarySettings.sortWithinGrouping || 'series_and_volume'}
              onChange={(e) => updateLibrarySettings({ sortWithinGrouping: e.target.value as any })}
              className="px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
            >
              <option value="series_and_volume">Series & Vol # (Default)</option>
              <option value="title">Title (Kana / Alpha)</option>
              <option value="author">Author Name</option>
              <option value="added">Date Added (Newest)</option>
              <option value="custom">Custom Order</option>
              <option value="completion">Completion %</option>
            </select>
          </div>
        </div>

        {/* Quick Toggles: Visual Shelves, Hide Completed, Filter Exclusions */}
        <div className="flex items-center space-x-2">
          <button
            onClick={() => updateLibrarySettings({ enableShelves: !librarySettings.enableShelves })}
            className={`px-2 py-1 rounded text-xs border transition-colors flex items-center space-x-1 ${
              librarySettings.enableShelves
                ? 'bg-[var(--accent-color)]/15 border-[var(--accent-color)] text-[var(--accent-color)] font-medium'
                : 'bg-[var(--bg-primary)] border-[var(--border-color)] text-[var(--text-muted)]'
            }`}
            title="Toggle 3D visual wooden shelf rails"
          >
            <span>🪵 Shelves: {librarySettings.enableShelves ? 'ON' : 'OFF'}</span>
          </button>

          <button
            onClick={() => updateLibrarySettings({ hideCompleted: !librarySettings.hideCompleted })}
            className={`px-2 py-1 rounded text-xs border transition-colors flex items-center space-x-1 ${
              librarySettings.hideCompleted
                ? 'bg-amber-500/20 border-amber-500/40 text-amber-300 font-medium'
                : 'bg-[var(--bg-primary)] border-[var(--border-color)] text-[var(--text-muted)]'
            }`}
            title="Hide finished books"
          >
            <EyeOff className="w-3 h-3" />
            <span>Hide Done</span>
          </button>

          {activeExcludedCount > 0 && (
            <button
              onClick={() => openSettings('library')}
              className="px-2 py-1 rounded bg-red-950/60 border border-red-800 text-red-300 text-xs font-medium flex items-center space-x-1 hover:bg-red-900/60"
              title="Click to manage excluded categories or tags"
            >
              <Filter className="w-3 h-3" />
              <span>{activeExcludedCount} Excluded</span>
            </button>
          )}
        </div>
      </div>

      {/* SCROLLABLE SHELVES WORKSPACE */}
      <div className="flex-1 overflow-y-auto p-4">{renderContent()}</div>

      {/* FOOTER PAGINATION */}
      <div className="px-4 py-2 border-t border-[var(--border-color)] bg-[var(--bg-surface)] flex items-center justify-between text-xs text-[var(--text-muted)] select-none shrink-0">
        <div>
          Showing {filteredBooks.length > 0 ? offset + 1 : 0}–{Math.min(offset + limit, filteredBooks.length)} of{' '}
          {filteredBooks.length} books {activeExcludedCount > 0 && `(${totalBooks - filteredBooks.length} filtered)`}
        </div>
        <div className="flex items-center space-x-2">
          <button
            onClick={handlePrevPage}
            disabled={offset === 0 || isLoading}
            className="p-1 rounded border border-[var(--border-color)] hover:bg-[var(--bg-primary)] disabled:opacity-40 disabled:cursor-not-allowed"
          >
            <ChevronLeft className="w-4 h-4" />
          </button>
          <span>
            Page {currentPage} of {totalPages}
          </span>
          <button
            onClick={handleNextPage}
            disabled={offset + limit >= totalBooks || isLoading}
            className="p-1 rounded border border-[var(--border-color)] hover:bg-[var(--bg-primary)] disabled:opacity-40 disabled:cursor-not-allowed"
          >
            <ChevronRight className="w-4 h-4" />
          </button>
        </div>
      </div>

      {/* FLOATING BULK ACTIONS BAR */}
      <BulkActionBar />
    </div>
  );
};
