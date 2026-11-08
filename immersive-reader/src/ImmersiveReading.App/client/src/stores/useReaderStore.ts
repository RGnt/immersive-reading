import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { BookDetail, ChapterSummary } from '../types/api';

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

  // Actions
  openBook: (bookId: string, initialHref?: string) => Promise<void>;
  loadChapter: (spineHref: string, chapterIndex?: number, preloadedHtml?: string) => Promise<void>;
  closeReader: () => void;
  nextChapter: () => Promise<void>;
  prevChapter: () => Promise<void>;
  setFuriganaMode: (mode: 'AllKanji' | 'Uncommon' | 'None') => void;
  toggleWritingMode: () => void;
  adjustFontSize: (deltaPx: number) => void;
  handleTokenClick: (tokenId: number, rect: { left: number; top: number; width?: number; height?: number }) => Promise<void>;
  closePopover: () => void;
  setPopoverTab: (tab: 'words' | 'kanji') => void;
  mineFlashcard: (selectedTermIndex?: number) => Promise<any>;
  updateWordStatus: (termIndex: number, statusInt: number) => Promise<void>;
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

  toggleWritingMode: () => {
    const current = get().writingMode;
    set({ writingMode: current === 'vertical-rl' ? 'horizontal-tb' : 'vertical-rl' });
  },

  adjustFontSize: (deltaPx) => {
    set((s) => ({ fontSizePx: Math.max(14, Math.min(48, s.fontSizePx + deltaPx)) }));
  },

  handleTokenClick: async (tokenId, rect) => {
    const { bookDetail, currentSpineHref } = get();
    if (!bookDetail) return;

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
        bookId: bookDetail.summary.bookId,
        chapterHref: currentSpineHref,
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

  mineFlashcard: async (selectedTermIndex = 0) => {
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
      });

      term._minedCardId = res?.card?.id || 'mined';
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
}));
