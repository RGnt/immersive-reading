import { create } from 'zustand';
import { ensureContrast, isLightColor, mixRgb, parseHex, readableOn, paletteOverrides, statusColors, toHex } from '../styles/themeColors';
import { rpc } from '../services/rpc';
import type { AppConfig, LibrarySettings } from '../types/api';

export type SettingsTab = 'general' | 'library' | 'appearance' | 'reader' | 'llm' | 'dictionaries' | 'prompts' | 'customCss' | 'memory';

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
  applyDerivedThemeColors(config);
}

/**
 * Colours that depend on the theme's own colours, so text stays readable in any theme (light ones, and custom ones, included):
 * muted and accent-coloured text are pulled towards the main text colour until they reach 4.5:1, the text of an accent button is
 * black or white by the accent's brightness, and the pale Tailwind status shades become darker ones on a light theme.
 */
export function applyDerivedThemeColors(config: AppConfig) {
  const root = document.documentElement;
  const cs = getComputedStyle(root);
  const a = config.appearance;
  const read = (value: string | undefined, variable: string) => (value && parseHex(value) ? value : cs.getPropertyValue(variable).trim());
  const bgPrimary = read(a?.bgPrimary, '--bg-primary');
  const bgSurface = read(a?.bgSurface, '--bg-surface');
  const textPrimary = read(a?.textPrimary, '--text-primary');
  const textMuted = read(a?.textMuted, '--text-muted');
  const accent = read(a?.accentColor, '--accent-color');
  if (![bgPrimary, bgSurface, textPrimary].every((c) => parseHex(c))) return;

  const light = isLightColor(bgPrimary);
  root.dataset.themeMode = light ? 'light' : 'dark';
  // also on the darker panels (bg-black/5 ...) that sit on the surface
  const panel = toHex(mixRgb(parseHex(bgSurface)!, [0, 0, 0], 0.08));
  root.style.setProperty('--text-muted', ensureContrast(textMuted, [bgPrimary, bgSurface, panel], textPrimary, 4.5));
  // accent-coloured text also sits on tinted bars (bg-black/10) and selected rows (a 15 to 20% wash of the accent itself)
  const tint = parseHex(accent) ? toHex(mixRgb(parseHex(bgSurface)!, parseHex(accent)!, 0.2)) : bgSurface;
  const darkerBar = toHex(mixRgb(parseHex(bgSurface)!, [0, 0, 0], 0.12));
  root.style.setProperty('--accent-text', ensureContrast(accent, [bgPrimary, bgSurface, panel, tint, darkerBar], textPrimary, 4.5));
  root.style.setProperty('--accent-contrast', readableOn(accent));
  for (const [name, value] of Object.entries(statusColors(light))) root.style.setProperty(name, value);
  for (const [name, value] of paletteOverrides(light)) {
    if (value) root.style.setProperty(name, value); else root.style.removeProperty(name);
  }
}
