import React from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import type { FolderNode } from '../../types/api';
import { Folder, FolderOpen, Tag, BookCopy, RefreshCw, FolderSearch, Copy, Star, BookOpen, Clock, CheckCircle2 } from 'lucide-react';
import type { ListFilter } from '../../types/api';

export const Sidebar: React.FC = () => {
  const {
    folders,
    activeFolderId,
    selectFolder,
    tags,
    selectedTagIds,
    toggleTag,
    totalBooks,
    fetchBooks,
    openScanModal,
    openDuplicatesModal,
    listFilter,
    listCounts,
    selectListFilter,
  } = useLibraryStore();

  const lists: { id: ListFilter; label: string; count: number; icon: React.ReactNode }[] = [
    { id: 'favorites', label: 'Favourites', count: listCounts.favorites, icon: <Star className="w-3.5 h-3.5" /> },
    { id: 'reading', label: 'Currently reading', count: listCounts.reading, icon: <BookOpen className="w-3.5 h-3.5" /> },
    { id: 'planned', label: 'Planning to read', count: listCounts.planned, icon: <Clock className="w-3.5 h-3.5" /> },
    { id: 'completed', label: 'Completed', count: listCounts.completed, icon: <CheckCircle2 className="w-3.5 h-3.5" /> },
  ];

  const renderFolderItem = (node: FolderNode, depth = 0) => {
    const isSelected = activeFolderId === node.folderId;
    return (
      <div key={node.folderId} className="folder-item">
        <button
          onClick={() => selectFolder(node.folderId)}
          style={{ paddingLeft: `${depth * 12 + 8}px` }}
          className={`w-full flex items-center justify-between py-1.5 pr-2 rounded text-xs transition-colors ${
            isSelected
              ? 'bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold'
              : 'text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]'
          }`}
        >
          <div className="flex items-center space-x-2 truncate">
            {isSelected ? <FolderOpen className="w-3.5 h-3.5 shrink-0" /> : <Folder className="w-3.5 h-3.5 shrink-0" />}
            <span className="truncate">{node.name}</span>
          </div>
          <span className="text-[10px] opacity-75 shrink-0">{node.totalSubtreeBookCount}</span>
        </button>
        {node.children && node.children.length > 0 && (
          <div className="pl-1">
            {node.children.map((child) => renderFolderItem(child, depth + 1))}
          </div>
        )}
      </div>
    );
  };

  return (
    <aside data-ir="sidebar" className="sidebar w-64 shrink-0 bg-[var(--bg-primary)] border-r border-[var(--border-color)] flex flex-col h-full select-none text-sm">
      <div className="p-3 border-b border-[var(--border-color)] flex items-center justify-between">
        <span className="text-xs font-semibold uppercase tracking-wider text-[var(--text-muted)]">Library</span>
        <div className="flex items-center space-x-1.5">
          <button
            onClick={() => openScanModal()}
            className="flex items-center space-x-1 px-2 py-0.5 rounded text-[11px] font-semibold bg-[var(--accent-color)] text-[var(--accent-contrast)] hover:opacity-90 shadow-sm transition-opacity"
            title="Scan a directory for EPUB books"
          >
            <FolderSearch className="w-3 h-3" />
            <span>Scan</span>
          </button>
          <button
            onClick={() => fetchBooks()}
            className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            title="Refresh Books"
          >
            <RefreshCw className="w-3.5 h-3.5" />
          </button>
        </div>
      </div>

      <div className="flex-1 overflow-y-auto p-2 space-y-4">
        <div className="space-y-1.5">
          <button
            onClick={() => selectFolder(null)}
            className={`w-full flex items-center justify-between px-2 py-1.5 rounded text-xs font-medium transition-colors ${
              activeFolderId === null && !listFilter
                ? 'bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold'
                : 'text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]'
            }`}
          >
            <div className="flex items-center space-x-2">
              <BookCopy className="w-3.5 h-3.5" />
              <span>All Books</span>
            </div>
            <span className="text-[10px]">{totalBooks}</span>
          </button>

          <button
            onClick={() => openScanModal()}
            className="w-full flex items-center space-x-2 px-2.5 py-1.5 rounded text-xs text-sky-400 hover:text-sky-300 hover:bg-sky-950/30 border border-dashed border-sky-500/40 transition-colors font-medium"
            title="Scan a directory on disk to add books"
          >
            <FolderSearch className="w-3.5 h-3.5 text-sky-400" />
            <span>Scan Folder for Books...</span>
          </button>

          <button
            onClick={() => openDuplicatesModal()}
            className="w-full flex items-center space-x-2 px-2.5 py-1.5 rounded text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)] border border-[var(--border-color)] transition-colors font-medium"
            title="Find books that exist twice (different files of the same book) and choose which to keep"
          >
            <Copy className="w-3.5 h-3.5" />
            <span>Find Duplicates...</span>
          </button>
        </div>

        <div data-testid="sidebar-lists">
          <div className="px-2 pb-1 text-[11px] font-semibold text-[var(--text-muted)] uppercase">Lists</div>
          <div className="space-y-0.5">
            {lists.map((l) => (
              <button
                key={l.id}
                data-testid={`list-${l.id}`}
                onClick={() => selectListFilter(listFilter === l.id ? null : l.id)}
                className={`w-full flex items-center justify-between px-2 py-1.5 rounded text-xs font-medium transition-colors ${
                  listFilter === l.id
                    ? 'bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold'
                    : 'text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]'
                }`}
              >
                <span className="flex items-center space-x-2">{l.icon}<span>{l.label}</span></span>
                <span className="text-[10px]">{l.count}</span>
              </button>
            ))}
          </div>
        </div>

        {folders.length > 0 && (
          <div>
            <div className="px-2 pb-1 text-[11px] font-semibold text-[var(--text-muted)] uppercase">Folders</div>
            <div className="space-y-0.5">{folders.map((f) => renderFolderItem(f, 0))}</div>
          </div>
        )}

        {tags.length > 0 && (
          <div>
            <div className="px-2 pb-1 text-[11px] font-semibold text-[var(--text-muted)] uppercase">Tags</div>
            <div className="flex flex-wrap gap-1 px-2">
              {tags.map((tag) => {
                const isSelected = selectedTagIds.includes(tag.tagId);
                return (
                  <button
                    key={tag.tagId}
                    onClick={() => toggleTag(tag.tagId)}
                    className={`flex items-center space-x-1 px-2 py-0.5 rounded text-[11px] border transition-colors ${
                      isSelected
                        ? 'border-[var(--accent-color)] bg-[var(--accent-color)] text-[var(--accent-contrast)] font-semibold'
                        : 'border-[var(--border-color)] text-[var(--text-muted)] hover:border-[var(--text-primary)]'
                    }`}
                  >
                    <Tag className="w-2.5 h-2.5" />
                    <span>{tag.name}</span>
                  </button>
                );
              })}
            </div>
          </div>
        )}
      </div>
    </aside>
  );
};
