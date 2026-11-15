import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { BookRecord, FolderNode, TagRecord, CustomFieldDefinition, BookDetail, ListFilter, ReadingListCounts, ReadingStatus } from '../types/api';

/** The query fields of a list: favourites, or one reading list (a book counts through its series too). */
export function listFilterQuery(filter: ListFilter | null): { favorite?: boolean; readingStatus?: string } {
  if (filter === 'favorites') return { favorite: true };
  return filter ? { readingStatus: filter } : {};
}

/** What a bulk edit may change. A field that is left out stays as it is; an empty string clears it. Mirrors BulkBookUpdatePatch (C#). */
export interface BulkPatch {
  folderId?: string | null;
  clearFolderId?: boolean;
  seriesName?: string;
  /** selection | title | filename | fromTitle; leave out to keep the volume numbers. */
  numberingMode?: 'selection' | 'title' | 'filename' | 'fromTitle';
  autoNumberStart?: number;
  autoNumberStep?: number;
  clearSeriesIndex?: boolean;
  autoNumberVolumes?: boolean;
  authors?: string;
  publisher?: string;
  imprint?: string;
  language?: string;
  addTagIds?: string[];
  removeTagIds?: string[];
}

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

  // Scanning state
  isScanModalOpen: boolean;
  isDuplicatesModalOpen: boolean;
  isBulkEditOpen: boolean;
  defaultScanDir: string;
  openBulkEdit: () => void;
  closeBulkEdit: () => void;
  openScanModal: () => void;
  openDuplicatesModal: () => void;
  closeDuplicatesModal: () => void;
  closeScanModal: () => void;

  /** Which list the gallery shows (favourites or a reading list), or null for the whole library. */
  listFilter: ListFilter | null;
  listCounts: ReadingListCounts;
  selectListFilter: (filter: ListFilter | null) => void;
  loadListCounts: () => Promise<void>;
  /** Favourite and reading list of books (all selected ones, for example). Absent = unchanged; a status of '' clears it. */
  setBookFlags: (bookIds: string[], flags: { favorite?: boolean; readingStatus?: ReadingStatus }) => Promise<void>;
  setSeriesFlags: (seriesName: string, flags: { favorite?: boolean; readingStatus?: ReadingStatus }) => Promise<void>;

  selectedSeriesDrillDown: string | null;
  setSelectedSeriesDrillDown: (seriesName: string | null) => void;

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
  /** Selects every book the current filters match (all pages), not only the visible one. Returns how many were selected. */
  selectAllMatching: () => Promise<number>;
  bulkMoveBooks: (targetFolderId: string | null) => Promise<void>;
  bulkAddTags: (tagIds: string[]) => Promise<void>;
  bulkDeleteSelected: () => Promise<void>;
  bulkUpdateBooks: (patch: BulkPatch) => Promise<void>;
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
  isScanModalOpen: false,
  isDuplicatesModalOpen: false,
  isBulkEditOpen: false,
  defaultScanDir: '',
  selectedSeriesDrillDown: null,
  listFilter: null,
  listCounts: { favorites: 0, reading: 0, planned: 0, completed: 0 },

  selectListFilter: (filter) => {
    set({ listFilter: filter, activeFolderId: null, offset: 0, selectedBookIds: [] });
    get().fetchBooks(0);
  },

  loadListCounts: async () => {
    try {
      const counts = await rpc.call<ReadingListCounts>('library.getReadingListCounts', {});
      if (counts) set({ listCounts: counts });
    } catch (err) {
      console.error('Failed to load the reading list counts:', err);
    }
  },

  setBookFlags: async (bookIds, flags) => {
    if (bookIds.length === 0) return;
    const res = await rpc.call<{ counts?: ReadingListCounts }>('library.setBookFlags', { bookIds, ...flags });
    if (res?.counts) set({ listCounts: res.counts });
    await get().fetchBooks();
    const open = get().activeBookDetail;
    if (open && bookIds.includes(open.summary.bookId)) await get().inspectBook(open.summary.bookId);
  },

  setSeriesFlags: async (seriesName, flags) => {
    const res = await rpc.call<{ counts?: ReadingListCounts }>('library.setSeriesFlags', { seriesName, ...flags });
    if (res?.counts) set({ listCounts: res.counts });
    await get().fetchBooks();
    const open = get().activeBookDetail;
    if (open?.summary.seriesName === seriesName) await get().inspectBook(open.summary.bookId);
  },

  openBulkEdit: () => set({ isBulkEditOpen: true }),
  closeBulkEdit: () => set({ isBulkEditOpen: false }),
  openScanModal: () => set({ isScanModalOpen: true }),
  openDuplicatesModal: () => set({ isDuplicatesModalOpen: true }),
  closeDuplicatesModal: () => set({ isDuplicatesModalOpen: false }),
  closeScanModal: () => set({ isScanModalOpen: false }),


  setSelectedSeriesDrillDown: (seriesName: string | null) => set({ selectedSeriesDrillDown: seriesName }),

  loadBootstrap: async () => {
    set({ isLoading: true });
    try {
      const bootstrap = await rpc.call<any>('library.getBootstrapState');
      if (bootstrap) {
        set({
          folders: bootstrap.folders || [],
          defaultScanDir: bootstrap.defaultScanDir || '',
        });
      }
      await get().fetchBooks(0);
      void get().loadListCounts();
    } catch (err) {
      console.error('Failed to load bootstrap state:', err);
    } finally {
      set({ isLoading: false });
    }
  },

  fetchBooks: async (newOffset?: number) => {
    const { activeFolderId, selectedTagIds, searchQuery, limit, listFilter } = get();
    const currentOffset = newOffset !== undefined ? newOffset : get().offset;

    set({ isLoading: true, offset: currentOffset });
    try {
      const res = await rpc.call<any>('library.listBooks', {
        folderId: activeFolderId || undefined,
        tagIds: selectedTagIds.length > 0 ? selectedTagIds : undefined,
        search: searchQuery.trim() || undefined,
        ...listFilterQuery(listFilter),
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
    set({ activeFolderId: folderId, listFilter: null, offset: 0, selectedBookIds: [] });
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

  selectAllMatching: async () => {
    const { activeFolderId, selectedTagIds, searchQuery, listFilter } = get();
    const res = await rpc.call<{ bookIds: string[]; total: number }>('library.listBookIds', {
      folderId: activeFolderId || undefined,
      tagIds: selectedTagIds.length > 0 ? selectedTagIds : undefined,
      search: searchQuery.trim() || undefined,
      ...listFilterQuery(listFilter),
    });
    const ids = res?.bookIds ?? [];
    set({ selectedBookIds: ids });
    return ids.length;
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
