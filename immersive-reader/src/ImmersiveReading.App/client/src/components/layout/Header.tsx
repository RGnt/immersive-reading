import React from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { useConfigStore } from '../../stores/useConfigStore';
import { useReaderStore } from '../../stores/useReaderStore';
import { Search, Settings, BookOpen, LayoutGrid, List, Library } from 'lucide-react';

export const Header: React.FC = () => {
  const { searchQuery, setSearchQuery, viewMode, setViewMode, totalBooks } = useLibraryStore();
  const { openSettings } = useConfigStore();
  const isReaderActive = useReaderStore((s) => s.isActive);
  const closeReader = useReaderStore((s) => s.closeReader);

  return (
    <header className="header shrink-0 flex items-center justify-between px-4 py-2 border-b border-[var(--border-color)] bg-[var(--bg-surface)] select-none">
      <div className="flex items-center space-x-3">
        <button
          onClick={closeReader}
          className="flex items-center space-x-2 font-bold text-lg text-[var(--accent-color)] hover:opacity-90"
          title="Back to Library"
        >
          <BookOpen className="w-6 h-6" />
          <span>Immersive Reading</span>
        </button>
        {isReaderActive && (
          <button
            onClick={closeReader}
            className="text-xs px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-muted)] hover:text-[var(--text-primary)]"
          >
            ← Library ({totalBooks} books)
          </button>
        )}
      </div>

      {!isReaderActive && (
        <div className="flex-1 max-w-md mx-6">
          <div className="relative">
            <Search className="absolute left-3 top-2.5 w-4 h-4 text-[var(--text-muted)]" />
            <input
              type="text"
              id="global-search-input"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              placeholder="Search by title, author, or series..."
              className="w-full pl-9 pr-4 py-1.5 rounded-md bg-[var(--bg-primary)] border border-[var(--border-color)] text-sm text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
            />
          </div>
        </div>
      )}

      <div className="flex items-center space-x-2">
        {!isReaderActive && (
          <div className="flex items-center bg-[var(--bg-primary)] rounded border border-[var(--border-color)] p-0.5">
            <button
              onClick={() => setViewMode('grid')}
              className={`p-1.5 rounded ${viewMode === 'grid' ? 'bg-[var(--accent-color)] text-black' : 'text-[var(--text-muted)] hover:text-[var(--text-primary)]'}`}
              title="Grid View"
            >
              <LayoutGrid className="w-4 h-4" />
            </button>
            <button
              onClick={() => setViewMode('table')}
              className={`p-1.5 rounded ${viewMode === 'table' ? 'bg-[var(--accent-color)] text-black' : 'text-[var(--text-muted)] hover:text-[var(--text-primary)]'}`}
              title="Table View"
            >
              <List className="w-4 h-4" />
            </button>
            <button
              onClick={() => setViewMode('series')}
              className={`p-1.5 rounded ${viewMode === 'series' ? 'bg-[var(--accent-color)] text-black' : 'text-[var(--text-muted)] hover:text-[var(--text-primary)]'}`}
              title="Series View"
            >
              <Library className="w-4 h-4" />
            </button>
          </div>
        )}

        <button
          onClick={() => openSettings('general')}
          id="btn-open-settings"
          className="p-1.5 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"
          title="Settings"
        >
          <Settings className="w-5 h-5" />
        </button>
      </div>
    </header>
  );
};

