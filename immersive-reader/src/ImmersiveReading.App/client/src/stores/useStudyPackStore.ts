import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { StudyPackOverview } from '../types/api';

interface StudyPackState {
  isOpen: boolean;
  activeBookId: string | null;
  overview: StudyPackOverview | null;
  minedCards: any[];
  isLoading: boolean;
  isSyncingAnki: boolean;
  selectedVocabSetIndex: number;

  openDrawer: (bookId: string) => Promise<void>;
  closeDrawer: () => void;
  loadOverview: (bookId: string) => Promise<void>;
  selectVocabSet: (index: number) => void;
  syncAllToAnki: () => Promise<any>;
}

export const useStudyPackStore = create<StudyPackState>((set, get) => ({
  isOpen: false,
  activeBookId: null,
  overview: null,
  minedCards: [],
  isLoading: false,
  isSyncingAnki: false,
  selectedVocabSetIndex: 0,

  openDrawer: async (bookId: string) => {
    set({ isOpen: true, activeBookId: bookId, selectedVocabSetIndex: 0 });
    await get().loadOverview(bookId);
  },

  closeDrawer: () => {
    set({ isOpen: false, activeBookId: null, overview: null, minedCards: [] });
  },

  loadOverview: async (bookId: string) => {
    set({ isLoading: true });
    try {
      const res = await rpc.call<any>('studypack.getOverview', { bookId });
      set({
        overview: res?.overview || res || null,
        minedCards: res?.minedCards || [],
        isLoading: false,
      });
    } catch (err) {
      console.error('Failed to load study pack overview:', err);
      set({ isLoading: false });
    }
  },

  selectVocabSet: (selectedVocabSetIndex: number) => {
    set({ selectedVocabSetIndex });
  },

  syncAllToAnki: async () => {
    const { activeBookId } = get();
    if (!activeBookId) return null;
    set({ isSyncingAnki: true });
    try {
      const res = await rpc.call<any>('anki.sync', { bookId: activeBookId });
      return res;
    } finally {
      set({ isSyncingAnki: false });
    }
  },
}));
