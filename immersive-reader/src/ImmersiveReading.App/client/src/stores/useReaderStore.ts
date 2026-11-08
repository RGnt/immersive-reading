import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { BookDetail, ChapterSummary, BookmarkItem } from '../types/api';

export interface TermSense {
  dictionaryTitle: string;
  tags?: string[];
  glosses: string[];
}

export interface KanjiInfo {
  character: string;
  primaryMeanings: string[];
  onReadings: string[];
  kunReadings: string[];
  strokeCount?: number;
  grade?: string;
  primarySource?: string;
  kanjiVgStrokes?: string[];
}

export interface LookupTerm {
  expression: string;
  reading: string;
  readingMatchStatus?: string;
  inflectionChain?: string[];
  frequencies?: { dictionaryTitle: string; frequencyText: string }[];
  senses: TermSense[];
  userStatus?: number;
  _minedCardId?: string;
  _ankiSynced?: boolean;
}

export interface LookupQueryResult {
  terms: LookupTerm[];
  kanji: KanjiInfo[];
  sentenceText?: string;
  sentenceRuby?: string;
}

interface PopoverState {
  isOpen: boolean;
  activeTokenId: number | null;
  result: LookupQueryResult | null;
  anchorRect: { left: number; top: number; width?: number; height?: number } | null;
  isLoading: boolean;
  activeTab: 'words' | 'kanji';
}

interface ReaderState {
  isActive: boolean;
  bookDetail: BookDetail | null;
  chapters: ChapterSummary[];
  currentChapterIndex: number;
  currentSpineHref: string;
  chapterHtml: string;
  writingMode: 'vertical-rl' | 'horizontal-tb';
  furiganaMode: 'AllKanji' | 'Uncommon' | 'None';
  fontSizePx: number;
  popover: PopoverState;

  // Search state
  isSearchOpen: boolean;
  searchQuery: string;
  searchMatchCount: number;
  searchCurrentIndex: number;

  // Bookmark state
  bookmarks: BookmarkItem[];
  isBookmarksOpen: boolean;

  // Shortcuts & Toast state
  isShortcutsOpen: boolean;
  toastMessage: string | null;

  // Actions
  openBook: (bookId: string, initialHref?: string) => Promise<void>;
  loadChapter: (spineHref: string, chapterIndex?: number, preloadedHtml?: string) => Promise<void>;
  closeReader: () => void;
  nextChapter: () => Promise<void>;
  prevChapter: () => Promise<void>;
  setFuriganaMode: (mode: 'AllKanji' | 'Uncommon' | 'None') => void;
  cycleFuriganaMode: () => void;
  toggleWritingMode: () => void;
  adjustFontSize: (deltaPx: number) => void;
  handleTokenClick: (tokenId: number, rect: { left: number; top: number; width?: number; height?: number }, overrideChapterHref?: string, overrideBookId?: string) => Promise<void>;
  closePopover: () => void;
  setPopoverTab: (tab: 'words' | 'kanji') => void;
  mineFlashcard: (selectedTermIndex?: number, forceAnkiSync?: boolean) => Promise<any>;
  updateWordStatus: (termIndex: number, statusInt: number) => Promise<void>;

  // Search & Bookmark Actions
  openSearch: () => void;
  closeSearch: () => void;
  setSearchQuery: (query: string) => void;
  setSearchResults: (count: number, current: number) => void;
  toggleBookmarks: () => void;
  loadBookmarks: () => Promise<void>;
  addBookmark: (note?: string, selectedText?: string) => Promise<void>;
  removeBookmark: (bookmarkId: string) => Promise<void>;

  // Shortcuts & Toast Actions
  toggleShortcuts: (force?: boolean) => void;
  showToast: (message: string) => void;
}

export const useReaderStore = create<ReaderState>((set, get) => ({
  isActive: false,
  bookDetail: null,
  chapters: [],
  currentChapterIndex: 0,
  currentSpineHref: '',
  chapterHtml: '',
  writingMode: 'vertical-rl',
  furiganaMode: 'AllKanji',
  fontSizePx: 22,
  popover: {
    isOpen: false,
    activeTokenId: null,
    result: null,
    anchorRect: null,
    isLoading: false,
    activeTab: 'words',
  },
  isSearchOpen: false,
  searchQuery: '',
  searchMatchCount: 0,
  searchCurrentIndex: 0,
  bookmarks: [],
  isBookmarksOpen: false,
  isShortcutsOpen: false,
  toastMessage: null,

  openBook: async (bookId: string, initialHref?: string) => {
    try {
      const params: Record<string, unknown> = { bookId };
      if (initialHref) params.spineHref = initialHref;

      const session = await rpc.call<any>('reader.openBook', params);
      if (!session || !session.book) return;

      const chapters = session.book.chapters || [];
      const spineHref = session.initialSpineHref || (chapters.length > 0 ? chapters[0].spineHref : '');
      const chapterIndex = session.initialChapterIndex || 0;
      const initialHtml = session.initialChapterHtml || '';

      set({
        isActive: true,
        bookDetail: session.book,
        chapters,
        currentChapterIndex: chapterIndex,
        currentSpineHref: spineHref,
        chapterHtml: initialHtml,
        writingMode: session.book.summary.writingMode === 'horizontal-tb' ? 'horizontal-tb' : 'vertical-rl',
      });

      if (!initialHtml && spineHref) {
        await get().loadChapter(spineHref, chapterIndex);
      }
      await get().loadBookmarks();
    } catch (err) {
      console.error('Failed to open book in reader:', err);
    }
  },

  loadChapter: async (spineHref: string, chapterIndex?: number, preloadedHtml?: string) => {
    const { chapters, bookDetail } = get();
    if (!bookDetail) return;

    let index = chapterIndex;
    if (index === undefined) {
      index = chapters.findIndex((c) => c.spineHref === spineHref);
      if (index === -1) index = 0;
    }

    let html = preloadedHtml;
    if (!html) {
      try {
        const res = await rpc.call<any>('reader.getChapterContent', {
          bookId: bookDetail.summary.bookId,
          spineHref,
        });
        html = res?.html || '';
      } catch (err) {
        console.error('Failed to load chapter content:', err);
      }
    }

    set({
      currentSpineHref: spineHref,
      currentChapterIndex: index,
      chapterHtml: html || '',
      popover: { ...get().popover, isOpen: false },
    });
  },

  closeReader: () => {
    set({
      isActive: false,
      bookDetail: null,
      chapterHtml: '',
      popover: { ...get().popover, isOpen: false },
    });
  },

  nextChapter: async () => {
    const { chapters, currentChapterIndex } = get();
    if (currentChapterIndex < chapters.length - 1) {
      const nextIdx = currentChapterIndex + 1;
      await get().loadChapter(chapters[nextIdx].spineHref, nextIdx);
    }
  },

  prevChapter: async () => {
    const { chapters, currentChapterIndex } = get();
    if (currentChapterIndex > 0) {
      const prevIdx = currentChapterIndex - 1;
      await get().loadChapter(chapters[prevIdx].spineHref, prevIdx);
    }
  },

  setFuriganaMode: (furiganaMode) => {
    set({ furiganaMode });
  },

  cycleFuriganaMode: () => {
    const modes: ('AllKanji' | 'Uncommon' | 'None')[] = ['AllKanji', 'Uncommon', 'None'];
    const curIdx = modes.indexOf(get().furiganaMode);
    const nextMode = modes[(curIdx + 1) % modes.length];
    set({ furiganaMode: nextMode });
    get().showToast(`Furigana: ${nextMode}`);
  },

  toggleWritingMode: () => {
    const current = get().writingMode;
    set({ writingMode: current === 'vertical-rl' ? 'horizontal-tb' : 'vertical-rl' });
  },

  adjustFontSize: (deltaPx) => {
    set((s) => ({ fontSizePx: Math.max(14, Math.min(48, s.fontSizePx + deltaPx)) }));
  },

  handleTokenClick: async (tokenId, rect, overrideChapterHref?: string, overrideBookId?: string) => {
    const { bookDetail, currentSpineHref } = get();
    const effectiveBookId = overrideBookId || bookDetail?.summary.bookId;
    const effectiveHref = overrideChapterHref || currentSpineHref;
    if (!effectiveBookId || !effectiveHref) return;

    set({
      popover: {
        isOpen: true,
        activeTokenId: tokenId,
        result: null,
        anchorRect: rect,
        isLoading: true,
        activeTab: 'words',
      },
    });

    try {
      const result = await rpc.call<LookupQueryResult>('lookup.query', {
        bookId: effectiveBookId,
        chapterHref: effectiveHref,
        tokenId,
      });

      if (!result || !result.terms || result.terms.length === 0) {
        get().closePopover();
        return;
      }

      set({
        popover: {
          isOpen: true,
          activeTokenId: tokenId,
          result,
          anchorRect: rect,
          isLoading: false,
          activeTab: 'words',
        },
      });
    } catch (err) {
      console.error('Failed to lookup token:', err);
      get().closePopover();
    }
  },

  closePopover: () => {
    set((s) => ({ popover: { ...s.popover, isOpen: false, activeTokenId: null } }));
  },

  setPopoverTab: (activeTab) => {
    set((s) => ({ popover: { ...s.popover, activeTab } }));
  },

  mineFlashcard: async (selectedTermIndex = 0, forceAnkiSync = false) => {
    const { bookDetail, chapters, currentChapterIndex, popover } = get();
    if (!bookDetail || !popover.result || !popover.result.terms[selectedTermIndex]) return null;

    const term = popover.result.terms[selectedTermIndex];
    const ch = chapters[currentChapterIndex] || { chapterIndex: currentChapterIndex, title: `Chapter ${currentChapterIndex + 1}` };
    const firstSense = term.senses?.[0]?.glosses?.join('; ') || term.expression;
    const sentenceText = popover.result.sentenceText || term.expression;
    const sentenceRuby = popover.result.sentenceRuby || sentenceText;

    try {
      const res = await rpc.call<any>('flashcards.mine', {
        bookId: bookDetail.summary.bookId,
        bookTitle: bookDetail.summary.title,
        chapterNumber: (ch.chapterIndex || 0) + 1,
        chapterTitle: ch.title || `Chapter ${currentChapterIndex + 1}`,
        word: term.expression,
        reading: term.reading || '',
        meaningGloss: firstSense,
        sentenceText,
        sentenceRuby,
        syncToAnki: forceAnkiSync,
      });

      term._minedCardId = res?.card?.id || 'mined';
      if (res?.anki?.success) {
        term._ankiSynced = true;
      }
      set((s) => ({ popover: { ...s.popover } }));
      return res;
    } catch (err) {
      console.error('Failed to mine flashcard:', err);
      return null;
    }
  },

  updateWordStatus: async (termIndex, statusInt) => {
    const { popover } = get();
    if (!popover.result || !popover.result.terms[termIndex]) return;
    const term = popover.result.terms[termIndex];

    try {
      await rpc.call('vocab.setStatus', {
        language: 'ja',
        lemma: term.expression,
        reading: term.reading,
        status: statusInt,
      });

      term.userStatus = statusInt;
      set((s) => ({ popover: { ...s.popover } }));
    } catch (err) {
      console.error('Failed to set vocabulary status:', err);
    }
  },

  openSearch: () => set({ isSearchOpen: true }),
  closeSearch: () =>
    set({ isSearchOpen: false, searchQuery: '', searchMatchCount: 0, searchCurrentIndex: 0 }),
  setSearchQuery: (searchQuery: string) => set({ searchQuery }),
  setSearchResults: (searchMatchCount: number, searchCurrentIndex: number) =>
    set({ searchMatchCount, searchCurrentIndex }),

  toggleBookmarks: () => set((s) => ({ isBookmarksOpen: !s.isBookmarksOpen })),
  loadBookmarks: async () => {
    const { bookDetail } = get();
    if (!bookDetail) return;
    try {
      const res = await rpc.call<any>('reader.getBookmarks', { bookId: bookDetail.summary.bookId });
      set({ bookmarks: res?.bookmarks || [] });
    } catch (err) {
      console.error('Failed to load bookmarks:', err);
    }
  },
  addBookmark: async (note = '', selectedText) => {
    const { bookDetail, currentChapterIndex, currentSpineHref, chapters } = get();
    if (!bookDetail) return;
    const ch = chapters[currentChapterIndex] || { title: `Chapter ${currentChapterIndex + 1}` };
    const text = selectedText || ch.title || `Chapter ${currentChapterIndex + 1}`;
    try {
      const bm = await rpc.call<BookmarkItem>('reader.addBookmark', {
        bookId: bookDetail.summary.bookId,
        chapterIndex: currentChapterIndex,
        spineHref: currentSpineHref,
        selectedText: text,
        userNote: note,
      });
      if (bm) {
        set((s) => ({ bookmarks: [...s.bookmarks, bm] }));
      }
    } catch (err) {
      console.error('Failed to add bookmark:', err);
    }
  },
  removeBookmark: async (bookmarkId: string) => {
    try {
      await rpc.call('reader.removeBookmark', { bookmarkId });
      set((s) => ({ bookmarks: s.bookmarks.filter((b) => b.bookmarkId !== bookmarkId) }));
    } catch (err) {
      console.error('Failed to remove bookmark:', err);
    }
  },

  toggleShortcuts: (force) => {
    set((s) => ({ isShortcutsOpen: force !== undefined ? force : !s.isShortcutsOpen }));
  },

  showToast: (message: string) => {
    set({ toastMessage: message });
    if ((window as any).__toastTimer) clearTimeout((window as any).__toastTimer);
    (window as any).__toastTimer = setTimeout(() => {
      set({ toastMessage: null });
    }, 1500);
  },
}));
