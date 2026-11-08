import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { StudyPackOverview } from '../types/api';

interface StudyPackState {
  isOpen: boolean;
  activeBookId: string | null;
  overview: StudyPackOverview | null;
  isLoading: boolean;
  selectedVocabSetIndex: number;

  openDrawer: (bookId: string) => Promise<void>;
  closeDrawer: () => void;
  loadOverview: (bookId: string) => Promise<void>;
  selectVocabSet: (index: number) => void;
}

export const useStudyPackStore = create<StudyPackState>((set, get) => ({
  isOpen: false,
  activeBookId: null,
  overview: null,
  isLoading: false,
  selectedVocabSetIndex: 0,

  openDrawer: async (bookId: string) => {
    set({ isOpen: true, activeBookId: bookId, selectedVocabSetIndex: 0 });
    await get().loadOverview(bookId);
  },

  closeDrawer: () => {
    set({ isOpen: false, activeBookId: null, overview: null });
  },

  loadOverview: async (bookId: string) => {
    set({ isLoading: true });
    try {
      const res = await rpc.call<any>('studypack.getOverview', { bookId });
      set({
        overview: res?.overview || res || null,
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
}));
