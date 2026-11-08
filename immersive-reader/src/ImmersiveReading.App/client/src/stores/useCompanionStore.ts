import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { AiQuizQuestion, AiQuizSubmissionResult, PersonaChatMessage } from '../types/api';

interface CompanionState {
  isOpen: boolean;
  activeTab: 'quiz' | 'chat';
  currentQuestion: AiQuizQuestion | null;
  lastSubmission: AiQuizSubmissionResult | null;
  isGeneratingQuiz: boolean;
  isSubmittingQuiz: boolean;
  chatMessages: PersonaChatMessage[];
  isSendingChat: boolean;
  personas: { personaId: string; name: string; systemPrompt: string }[];
  selectedPersonaId: string;

  // Actions
  openCompanion: (tab?: 'quiz' | 'chat') => void;
  closeCompanion: () => void;
  setActiveTab: (tab: 'quiz' | 'chat') => void;
  loadPersonas: (bookId: string) => Promise<void>;
  generateQuestion: (params: {
    bookId: string;
    chapterIndex: number;
    spineHref: string;
    viewportTokenId: number;
  }) => Promise<AiQuizQuestion | null>;
  submitAnswer: (params: {
    questionId: string;
    selectedOptionIndex: number;
    userAnswer: string;
  }) => Promise<AiQuizSubmissionResult | null>;
  sendChatMessage: (bookId: string, content: string) => Promise<void>;
}

export const useCompanionStore = create<CompanionState>((set, get) => ({
  isOpen: false,
  activeTab: 'quiz',
  currentQuestion: null,
  lastSubmission: null,
  isGeneratingQuiz: false,
  isSubmittingQuiz: false,
  chatMessages: [],
  isSendingChat: false,
  personas: [],
  selectedPersonaId: '',

  openCompanion: (tab = 'quiz') => {
    set({ isOpen: true, activeTab: tab });
  },

  closeCompanion: () => {
    set({ isOpen: false });
  },

  setActiveTab: (activeTab) => {
    set({ activeTab });
  },

  loadPersonas: async (bookId: string) => {
    try {
      const res = await rpc.call<any>('ext.aiCompanion.listPersonas', { bookId });
      const personas = res?.personas || [];
      set({
        personas,
        selectedPersonaId: personas.length > 0 ? personas[0].personaId : '',
      });
    } catch {
      // Non-fatal
    }
  },

  generateQuestion: async (params) => {
    set({ isGeneratingQuiz: true, lastSubmission: null });
    try {
      const q = await rpc.call<AiQuizQuestion>('ext.aiCompanion.generateQuestion', params);
      set({ currentQuestion: q, isGeneratingQuiz: false });
      return q;
    } catch (err) {
      console.error('Failed to generate quiz question:', err);
      set({ isGeneratingQuiz: false });
      return null;
    }
  },

  submitAnswer: async (params) => {
    set({ isSubmittingQuiz: true });
    try {
      const result = await rpc.call<AiQuizSubmissionResult>('ext.aiCompanion.submitAnswer', params);
      set({ lastSubmission: result, isSubmittingQuiz: false });
      return result;
    } catch (err) {
      console.error('Failed to submit quiz answer:', err);
      set({ isSubmittingQuiz: false });
      return null;
    }
  },

  sendChatMessage: async (bookId: string, content: string) => {
    const { selectedPersonaId, chatMessages } = get();
    if (!content.trim()) return;

    const userMsg: PersonaChatMessage = {
      messageId: `user-${Date.now()}`,
      sender: 'user',
      personaName: 'You',
      content,
      timestamp: new Date().toLocaleTimeString(),
    };

    set({
      chatMessages: [...chatMessages, userMsg],
      isSendingChat: true,
    });

    try {
      const reply = await rpc.call<any>('ext.aiCompanion.sendChatMessage', {
        bookId,
        personaId: selectedPersonaId,
        message: content,
      });

      if (reply) {
        const personaMsg: PersonaChatMessage = {
          messageId: reply.messageId || `ai-${Date.now()}`,
          sender: 'persona',
          personaName: reply.personaName || 'Companion',
          content: reply.content || reply.reply || '',
          timestamp: new Date().toLocaleTimeString(),
          syntheticChapterHref: reply.promptSyntheticChapterHref,
        };
        set((s) => ({ chatMessages: [...s.chatMessages, personaMsg] }));
      }
    } catch (err) {
      console.error('Failed to send chat message:', err);
    } finally {
      set({ isSendingChat: false });
    }
  },
}));
