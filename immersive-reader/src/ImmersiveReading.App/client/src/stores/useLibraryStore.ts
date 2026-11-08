import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { BookRecord, FolderNode, TagRecord, CustomFieldDefinition, BookDetail } from '../types/api';

interface LibraryState {
  folders: FolderNode[];
  tags: TagRecord[];
  customFields: CustomFieldDefinition[];
  activeFolderId: string | null;
  selectedTagIds: string[];
  searchQuery: string;
  books: BookRecord[];
  totalBooks: number;
  offset: number;
  limit: number;
  viewMode: 'grid' | 'table' | 'series';
  isLoading: boolean;
  activeBookDetail: BookDetail | null;
  isInspectorOpen: boolean;

  // Multi-selection state
  selectedBookIds: string[];

  // Actions
  loadBootstrap: () => Promise<void>;
  fetchBooks: (newOffset?: number) => Promise<void>;
  selectFolder: (folderId: string | null) => void;
  toggleTag: (tagId: string) => void;
  setSearchQuery: (query: string) => void;
  setViewMode: (mode: 'grid' | 'table' | 'series') => void;
  inspectBook: (bookId: string) => Promise<void>;
  closeInspector: () => void;

  // Bulk actions
  toggleSelectBook: (bookId: string) => void;
  selectAllOnPage: () => void;
  clearSelection: () => void;
  bulkMoveBooks: (targetFolderId: string | null) => Promise<void>;
  bulkAddTags: (tagIds: string[]) => Promise<void>;
  bulkDeleteSelected: () => Promise<void>;
  bulkUpdateBooks: (patch: {
    folderId?: string | null;
    clearFolderId?: boolean;
    seriesName?: string;
    autoNumberVolumes?: boolean;
    authors?: string;
    publisher?: string;
    addTagIds?: string[];
    removeTagIds?: string[];
  }) => Promise<void>;
}

export const useLibraryStore = create<LibraryState>((set, get) => ({
  folders: [],
  tags: [],
  customFields: [],
  activeFolderId: null,
  selectedTagIds: [],
  searchQuery: '',
  books: [],
  totalBooks: 0,
  offset: 0,
  limit: 100,
  viewMode: 'grid',
  isLoading: false,
  activeBookDetail: null,
  isInspectorOpen: false,
  selectedBookIds: [],

  loadBootstrap: async () => {
    set({ isLoading: true });
    try {
      const bootstrap = await rpc.call<any>('library.getBootstrapState');
      if (bootstrap) {
        set({
          folders: bootstrap.folders || [],
        });
      }
      await get().fetchBooks(0);
    } catch (err) {
      console.error('Failed to load bootstrap state:', err);
    } finally {
      set({ isLoading: false });
    }
  },

  fetchBooks: async (newOffset?: number) => {
    const { activeFolderId, selectedTagIds, searchQuery, limit } = get();
    const currentOffset = newOffset !== undefined ? newOffset : get().offset;

    set({ isLoading: true, offset: currentOffset });
    try {
      const res = await rpc.call<any>('library.listBooks', {
        folderId: activeFolderId || undefined,
        tags: selectedTagIds.length > 0 ? selectedTagIds : undefined,
        search: searchQuery.trim() || undefined,
        offset: currentOffset,
        limit,
      });

      if (res) {
        set({
          books: res.books || res.items || [],
          totalBooks: res.totalCount !== undefined ? res.totalCount : (res.books?.length || 0),
        });
      }
    } catch (err) {
      console.error('Failed to fetch books:', err);
    } finally {
      set({ isLoading: false });
    }
  },

  selectFolder: (folderId: string | null) => {
    set({ activeFolderId: folderId, offset: 0, selectedBookIds: [] });
    get().fetchBooks(0);
  },

  toggleTag: (tagId: string) => {
    const current = get().selectedTagIds;
    const next = current.includes(tagId) ? current.filter((id) => id !== tagId) : [...current, tagId];
    set({ selectedTagIds: next, offset: 0, selectedBookIds: [] });
    get().fetchBooks(0);
  },

  setSearchQuery: (query: string) => {
    set({ searchQuery: query, offset: 0, selectedBookIds: [] });
    get().fetchBooks(0);
  },

  setViewMode: (viewMode: 'grid' | 'table' | 'series') => {
    set({ viewMode });
  },

  inspectBook: async (bookId: string) => {
    try {
      const detail = await rpc.call<BookDetail>('library.getBookDetail', { bookId });
      set({ activeBookDetail: detail, isInspectorOpen: true });
    } catch (err) {
      console.error('Failed to inspect book:', err);
    }
  },

  closeInspector: () => {
    set({ activeBookDetail: null, isInspectorOpen: false });
  },

  toggleSelectBook: (bookId: string) => {
    const current = get().selectedBookIds;
    const next = current.includes(bookId) ? current.filter((id) => id !== bookId) : [...current, bookId];
    set({ selectedBookIds: next });
  },

  selectAllOnPage: () => {
    const pageBookIds = get().books.map((b) => b.bookId);
    const current = get().selectedBookIds;
    const allSelected = pageBookIds.every((id) => current.includes(id));
    if (allSelected) {
      set({ selectedBookIds: current.filter((id) => !pageBookIds.includes(id)) });
    } else {
      const combined = Array.from(new Set([...current, ...pageBookIds]));
      set({ selectedBookIds: combined });
    }
  },

  clearSelection: () => {
    set({ selectedBookIds: [] });
  },

  bulkMoveBooks: async (targetFolderId: string | null) => {
    const ids = get().selectedBookIds;
    if (ids.length === 0) return;

    try {
      await rpc.call('library.bulkUpdateBooks', {
        bookIds: ids,
        folderId: targetFolderId || null,
        clearFolderId: !targetFolderId,
      });
      set({ selectedBookIds: [] });
      await get().loadBootstrap();
    } catch (err) {
      console.error('Failed to bulk move books:', err);
      throw err;
    }
  },

  bulkAddTags: async (tagIds: string[]) => {
    const ids = get().selectedBookIds;
    if (ids.length === 0 || tagIds.length === 0) return;

    try {
      await rpc.call('library.bulkUpdateBooks', {
        bookIds: ids,
        addTagIds: tagIds,
      });
      set({ selectedBookIds: [] });
      await get().fetchBooks();
    } catch (err) {
      console.error('Failed to bulk add tags:', err);
      throw err;
    }
  },

  bulkDeleteSelected: async () => {
    const ids = get().selectedBookIds;
    if (ids.length === 0) return;

    try {
      await rpc.call('library.bulkDeleteBooks', { bookIds: ids });
      set({ selectedBookIds: [] });
      await get().loadBootstrap();
    } catch (err) {
      console.error('Failed to bulk delete books:', err);
      throw err;
    }
  },

  bulkUpdateBooks: async (patch) => {
    const ids = get().selectedBookIds;
    if (ids.length === 0) return;

    try {
      await rpc.call('library.bulkUpdateBooks', {
        bookIds: ids,
        ...patch,
      });
      set({ selectedBookIds: [] });
      await get().loadBootstrap();
    } catch (err) {
      console.error('Failed to bulk update books:', err);
      throw err;
    }
  },
}));
