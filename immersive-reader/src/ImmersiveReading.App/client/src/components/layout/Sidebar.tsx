import React from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import type { FolderNode } from '../../types/api';
import { Folder, FolderOpen, Tag, BookCopy, RefreshCw } from 'lucide-react';

export const Sidebar: React.FC = () => {
  const { folders, activeFolderId, selectFolder, tags, selectedTagIds, toggleTag, totalBooks, fetchBooks } = useLibraryStore();

  const renderFolderItem = (node: FolderNode, depth = 0) => {
    const isSelected = activeFolderId === node.folderId;
    return (
      <div key={node.folderId} className="folder-item">
        <button
          onClick={() => selectFolder(node.folderId)}
          style={{ paddingLeft: `${depth * 12 + 8}px` }}
          className={`w-full flex items-center justify-between py-1.5 pr-2 rounded text-xs transition-colors ${
            isSelected
              ? 'bg-[var(--accent-color)] text-black font-semibold'
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
    <aside className="sidebar w-64 shrink-0 bg-[var(--bg-primary)] border-r border-[var(--border-color)] flex flex-col h-full select-none text-sm">
      <div className="p-3 border-b border-[var(--border-color)] flex items-center justify-between">
        <span className="text-xs font-semibold uppercase tracking-wider text-[var(--text-muted)]">Library</span>
        <button
          onClick={() => fetchBooks()}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)]"
          title="Refresh Books"
        >
          <RefreshCw className="w-3.5 h-3.5" />
        </button>
      </div>

      <div className="flex-1 overflow-y-auto p-2 space-y-4">
        <div>
          <button
            onClick={() => selectFolder(null)}
            className={`w-full flex items-center justify-between px-2 py-1.5 rounded text-xs font-medium transition-colors ${
              activeFolderId === null
                ? 'bg-[var(--accent-color)] text-black font-semibold'
                : 'text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]'
            }`}
          >
            <div className="flex items-center space-x-2">
              <BookCopy className="w-3.5 h-3.5" />
              <span>All Books</span>
            </div>
            <span className="text-[10px]">{totalBooks}</span>
          </button>
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
                        ? 'border-[var(--accent-color)] bg-[var(--accent-color)] text-black font-semibold'
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
