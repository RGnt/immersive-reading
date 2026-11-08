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
  loadPluginUiBundle: (pluginId: string, bundleUrl: string) => Promise<void>;
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
        if (p.state === 'Enabled' && p.uiBundleUrl) {
          await get().loadPluginUiBundle(p.manifest.id, p.uiBundleUrl);
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

        if (state === 'Enabled' && uiBundleUrl) {
          await get().loadPluginUiBundle(pluginId, uiBundleUrl);
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

  loadPluginUiBundle: async (pluginId, bundleUrl) => {
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
      const module = await import(/* @vite-ignore */ `${bundleUrl}?t=${Date.now()}`);
      if (typeof module.activate === 'function') {
        module.activate(ctx);
      }
      set((s) => ({ loadedBundles: { ...s.loadedBundles, [pluginId]: true } }));
    } catch (err) {
      console.error(`[PluginHost] Failed to load UI bundle for '${pluginId}':`, err);
    }
  },

  unloadPluginUiRegistrations: (pluginId) => {
    set((s) => {
      const nextCustomPanels = { ...s.customSettingsPanels };
      delete nextCustomPanels[pluginId];

      const isActiveDrawerFromPlugin = s.drawers.some(
        (d) => d.id === s.activeDrawerId && d.pluginId === pluginId
      );

      return {
        views: s.views.filter((v) => v.pluginId !== pluginId),
        drawers: s.drawers.filter((d) => d.pluginId !== pluginId),
        toolbarActions: s.toolbarActions.filter((a) => a.pluginId !== pluginId),
        bookTabs: s.bookTabs.filter((t) => t.pluginId !== pluginId),
        customSettingsPanels: nextCustomPanels,
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
