import { create } from 'zustand';
import { rpc } from '../services/rpc';
import type {
  PluginDescriptorDto,
  PluginViewRegistration,
  PluginDrawerRegistration,
  PluginToolbarActionRegistration,
  PluginBookTabRegistration,
  PluginSettingsPanelRegistration,
  PluginUiContext,
} from '../sdk/types';
import type { ComponentType } from 'react';
import { loadPluginModule, loadPluginStyle } from '../services/pluginModuleLoader';

type ScopedItem<T> = T & { pluginId: string };

export interface ActiveModalState {
  id: string;
  pluginId: string;
  title: string;
  size?: 'sm' | 'md' | 'lg' | 'xl';
  component: ComponentType<{ onClose: () => void }>;
}

interface PluginStoreState {
  plugins: Record<string, PluginDescriptorDto>;
  loadedBundles: Record<string, boolean>;

  // Registered UI Slot Items
  views: ScopedItem<PluginViewRegistration>[];
  drawers: ScopedItem<PluginDrawerRegistration>[];
  toolbarActions: ScopedItem<PluginToolbarActionRegistration>[];
  bookTabs: ScopedItem<PluginBookTabRegistration>[];
  customSettingsPanels: Record<string, PluginSettingsPanelRegistration>;

  /** Live CSS overrides pushed by plugins into the reader iframe: pluginId -> styleId -> css. */
  readerStyles: Record<string, Record<string, string>>;
  /** Last UI load failure per plugin (shown on its Settings card); absent when the UI loaded fine. */
  uiErrors: Record<string, string>;

  // Active UI State
  activeDrawerId: string | null;
  activeModal: ActiveModalState | null;

  // Actions
  initializePlugins: () => Promise<void>;
  setPluginEnabled: (pluginId: string, enabled: boolean) => Promise<void>;
  updatePluginConfig: (pluginId: string, partialConfig: Record<string, any>) => Promise<void>;
  resetPluginConfig: (pluginId: string) => Promise<void>;
  toggleDrawer: (drawerId: string | null) => void;
  openModal: (modal: ActiveModalState) => void;
  closeModal: () => void;
  setReaderStyle: (pluginId: string, styleId: string, css: string | null) => void;
  loadPluginUiBundle: (pluginId: string) => Promise<void>;
  unloadPluginUiRegistrations: (pluginId: string) => void;
}

export const usePluginStore = create<PluginStoreState>((set, get) => ({
  plugins: {},
  loadedBundles: {},
  views: [],
  drawers: [],
  toolbarActions: [],
  bookTabs: [],
  customSettingsPanels: {},
  readerStyles: {},
  uiErrors: {},
  activeDrawerId: null,
  activeModal: null,

  initializePlugins: async () => {
    try {
      const res = await rpc.call<{ plugins: any[] }>('extensions.list', {});
      const map: Record<string, PluginDescriptorDto> = {};

      for (const p of res.plugins || []) {
        if (p.manifest) {
          map[p.manifest.id] = p;
        }
      }
      set({ plugins: map });

      // Dynamically import UI bundles for all currently enabled plugins
      for (const p of Object.values(map)) {
        if (p.state === 'Enabled' && (p.manifest.uiEntry || p.manifest.styleEntry)) {
          await get().loadPluginUiBundle(p.manifest.id);
        }
      }

      // Listen for live state & config push events from C#
      rpc.on('extensions.stateChanged', async (evt: any) => {
        const { pluginId, state, requiresRestart, uiBundleUrl } = evt;
        set((s) => ({
          plugins: {
            ...s.plugins,
            [pluginId]: {
              ...s.plugins[pluginId],
              state,
              requiresRestart,
              uiBundleUrl: uiBundleUrl ?? s.plugins[pluginId]?.uiBundleUrl,
            },
          },
        }));

        const manifest = get().plugins[pluginId]?.manifest;
        if (state === 'Enabled' && (manifest?.uiEntry || manifest?.styleEntry)) {
          await get().loadPluginUiBundle(pluginId);
        } else if (state === 'Disabled') {
          get().unloadPluginUiRegistrations(pluginId);
        }
      });

      rpc.on('extensions.configChanged', (evt: any) => {
        const { pluginId, config } = evt;
        set((s) => ({
          plugins: {
            ...s.plugins,
            [pluginId]: {
              ...s.plugins[pluginId],
              config: { ...s.plugins[pluginId]?.config, ...config },
            },
          },
        }));
      });
    } catch (err) {
      console.error('[PluginStore] Failed to initialize plugins:', err);
    }
  },

  loadPluginUiBundle: async (pluginId) => {
    const manifest = get().plugins[pluginId]?.manifest;
    if (!manifest) return;
    const setUiError = (message: string | null) =>
      set((s) => {
        const next = { ...s.uiErrors };
        if (message === null) delete next[pluginId];
        else next[pluginId] = message;
        return { uiErrors: next };
      });

    // Prevent duplicate registrations before re-activating
    get().unloadPluginUiRegistrations(pluginId);

    const ctx: PluginUiContext = {
      pluginId,
      registerView: (view) =>
        set((s) => ({ views: [...s.views, { ...view, pluginId }] })),
      registerReaderDrawer: (drawer) =>
        set((s) => ({ drawers: [...s.drawers, { ...drawer, pluginId }] })),
      registerReaderToolbarAction: (action) =>
        set((s) => ({ toolbarActions: [...s.toolbarActions, { ...action, pluginId }] })),
      registerBookInfoTab: (tab) =>
        set((s) => ({ bookTabs: [...s.bookTabs, { ...tab, pluginId }] })),
      registerCustomSettingsPanel: (panel) =>
        set((s) => ({
          customSettingsPanels: { ...s.customSettingsPanels, [pluginId]: panel },
        })),
    };

    try {
      if (manifest.styleEntry) {
        const css = await loadPluginStyle(pluginId, manifest.styleEntry);
        const el = document.createElement('style');
        el.setAttribute('data-ir-plugin', pluginId);
        el.textContent = css;
        document.head.appendChild(el);
      }
      if (manifest.uiEntry) {
        const module = await loadPluginModule(pluginId, manifest.uiEntry);
        if (typeof module.activate === 'function') {
          (module.activate as (c: PluginUiContext) => void)(ctx);
        }
      }
      set((s) => ({ loadedBundles: { ...s.loadedBundles, [pluginId]: true } }));
      setUiError(null);
    } catch (err) {
      get().unloadPluginUiRegistrations(pluginId); // never leave a half-activated plugin behind
      const message = err instanceof Error ? err.message : String(err);
      console.error(`[PluginHost] Failed to load UI for '${pluginId}':`, err);
      setUiError(message);
    }
  },

  setReaderStyle: (pluginId, styleId, css) =>
    set((s) => {
      const forPlugin = { ...(s.readerStyles[pluginId] ?? {}) };
      if (css === null) delete forPlugin[styleId];
      else forPlugin[styleId] = css;
      return { readerStyles: { ...s.readerStyles, [pluginId]: forPlugin } };
    }),

  unloadPluginUiRegistrations: (pluginId) => {
    set((s) => {
      const nextCustomPanels = { ...s.customSettingsPanels };
      delete nextCustomPanels[pluginId];
      // A disabled/unloaded plugin must not leave live style overrides behind.
      const nextReaderStyles = { ...s.readerStyles };
      delete nextReaderStyles[pluginId];
      document.querySelectorAll(`style[data-ir-plugin="${pluginId}"]`).forEach((el) => el.remove());

      const isActiveDrawerFromPlugin = s.drawers.some(
        (d) => d.id === s.activeDrawerId && d.pluginId === pluginId
      );

      return {
        views: s.views.filter((v) => v.pluginId !== pluginId),
        drawers: s.drawers.filter((d) => d.pluginId !== pluginId),
        toolbarActions: s.toolbarActions.filter((a) => a.pluginId !== pluginId),
        bookTabs: s.bookTabs.filter((t) => t.pluginId !== pluginId),
        customSettingsPanels: nextCustomPanels,
        readerStyles: nextReaderStyles,
        activeDrawerId: isActiveDrawerFromPlugin ? null : s.activeDrawerId,
        activeModal: s.activeModal?.pluginId === pluginId ? null : s.activeModal,
      };
    });
  },

  setPluginEnabled: async (pluginId, enabled) => {
    await rpc.call('extensions.setEnabled', { pluginId, enabled });
  },

  updatePluginConfig: async (pluginId, partialConfig) => {
    await rpc.call('extensions.updateConfig', { pluginId, config: partialConfig });
  },

  resetPluginConfig: async (pluginId) => {
    await rpc.call('extensions.resetConfig', { pluginId });
  },

  toggleDrawer: (drawerId) =>
    set((s) => ({ activeDrawerId: s.activeDrawerId === drawerId ? null : drawerId })),

  openModal: (modal) => set({ activeModal: modal }),
  closeModal: () => set({ activeModal: null }),
}));
