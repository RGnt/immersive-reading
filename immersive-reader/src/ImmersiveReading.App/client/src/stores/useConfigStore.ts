import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type { AppConfig, LibrarySettings } from '../types/api';

export type SettingsTab = 'general' | 'library' | 'appearance' | 'reader' | 'llm' | 'dictionaries';

interface ConfigState {
  config: AppConfig | null;
  isSettingsOpen: boolean;
  activeTab: SettingsTab;

  loadConfig: () => Promise<void>;
  saveConfig: (updated: AppConfig) => Promise<void>;
  updateLibrarySettings: (patch: Partial<LibrarySettings>) => Promise<void>;
  resetDefaults: (category?: string) => Promise<void>;
  openSettings: (tab?: SettingsTab) => void;
  closeSettings: () => void;
  setActiveTab: (tab: SettingsTab) => void;
}

export const useConfigStore = create<ConfigState>((set, get) => ({
  config: null,
  isSettingsOpen: false,
  activeTab: 'general',

  loadConfig: async () => {
    try {
      const res = await rpc.call<any>('config.getAll');
      const config = res?.config || res;
      if (config) {
        // Ensure library settings defaults if missing
        if (!config.library) {
          config.library = {
            primaryGrouping: 'series',
            sortWithinGrouping: 'series_and_volume',
            enableShelves: true,
            excludedFolderIds: [],
            excludedTagIds: [],
            hideCompleted: false,
            defaultViewMode: 'grid',
          };
        }
        set({ config });
        applyThemeStyles(config);
      }
    } catch (err) {
      console.error('Failed to load config:', err);
    }
  },

  saveConfig: async (updated: AppConfig) => {
    try {
      await rpc.call('config.update', { config: updated });
      set({ config: updated });
      applyThemeStyles(updated);
    } catch (err) {
      console.error('Failed to save config:', err);
    }
  },

  updateLibrarySettings: async (patch: Partial<LibrarySettings>) => {
    const current = get().config;
    if (!current) return;

    const updatedLibrary: LibrarySettings = {
      ...current.library,
      ...patch,
    };

    const updatedConfig: AppConfig = {
      ...current,
      library: updatedLibrary,
    };

    set({ config: updatedConfig });

    try {
      await rpc.call('config.update', { config: updatedConfig });
    } catch (err) {
      console.error('Failed to persist library settings:', err);
    }
  },

  resetDefaults: async (category?: string) => {
    try {
      const res = await rpc.call<any>('config.resetDefaults', { category: category || undefined });
      const config = res?.config || res;
      if (config) {
        set({ config });
        applyThemeStyles(config);
      } else {
        await get().loadConfig();
      }
    } catch (err) {
      console.error('Failed to reset config defaults:', err);
    }
  },

  openSettings: (tab = 'general') => {
    set({ isSettingsOpen: true, activeTab: tab });
  },

  closeSettings: () => {
    set({ isSettingsOpen: false });
  },

  setActiveTab: (activeTab) => {
    set({ activeTab });
  },
}));

export function applyThemeStyles(config: AppConfig) {
  if (!config?.appearance) return;
  const root = document.documentElement;
  const a = config.appearance;

  if (a.bgPrimary) root.style.setProperty('--bg-primary', a.bgPrimary);
  if (a.bgSurface) root.style.setProperty('--bg-surface', a.bgSurface);
  if (a.textPrimary) root.style.setProperty('--text-primary', a.textPrimary);
  if (a.textMuted) root.style.setProperty('--text-muted', a.textMuted);
  if (a.borderColor) root.style.setProperty('--border-color', a.borderColor);
  if (a.accentColor) root.style.setProperty('--accent-color', a.accentColor);
  if (a.rubyColor) root.style.setProperty('--ruby-color', a.rubyColor);
  if (a.uiFontFamily) root.style.setProperty('--ui-font-family', a.uiFontFamily);
}
