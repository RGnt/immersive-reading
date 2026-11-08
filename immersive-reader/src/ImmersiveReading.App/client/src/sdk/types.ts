import type { ComponentType } from 'react';
import type { LucideIcon } from 'lucide-react';

export type DrawerLocation = 'right' | 'bottom';

export interface PluginManifestDto {
  id: string;
  name: string;
  version: string;
  author: string;
  description: string;
  reloadBehavior: 'Hot' | 'RestartRequired';
  uiEntry?: string | null;
  styleEntry?: string | null;
}

export type PluginRuntimeState =
  | 'Disabled'
  | 'Enabled'
  | 'PendingEnable'
  | 'PendingDisable'
  | 'Faulted';

export interface PluginDescriptorDto {
  manifest: PluginManifestDto;
  state: PluginRuntimeState;
  requiresRestart: boolean;
  lastError?: string | null;
  uiBundleUrl?: string | null;
  styleBundleUrl?: string | null;
  config: Record<string, any>;
  schema?: JsonSchemaObject | null;
}

// --- UI Slot Descriptors ---

export interface PluginViewRegistration {
  id: string;
  title: string;
  icon: LucideIcon;
  component: ComponentType<{ viewParams?: Record<string, any> }>;
}

export interface PluginDrawerRegistration {
  id: string;
  title: string;
  icon: LucideIcon;
  location?: DrawerLocation;
  defaultSize?: number;
  component: ComponentType<{ onClose: () => void }>;
}

export interface ToolbarActionContext {
  openModal: (opts: {
    id: string;
    title: string;
    size?: 'sm' | 'md' | 'lg' | 'xl';
    component: ComponentType<{ onClose: () => void }>;
  }) => void;
  toggleDrawer: (drawerId: string) => void;
  navigateToView: (viewId: string, params?: Record<string, any>) => void;
  getPluginConfig: <T = Record<string, any>>() => T;
}

export interface PluginToolbarActionRegistration {
  id: string;
  label: string;
  icon: LucideIcon;
  onClick: (ctx: ToolbarActionContext) => void;
}

export interface PluginBookTabRegistration {
  id: string;
  title: string;
  icon: LucideIcon;
  component: ComponentType<{ bookId: string }>;
}

export interface PluginSettingsPanelRegistration {
  component: ComponentType<{
    config: Record<string, any>;
    updateConfig: (partial: Record<string, any>) => Promise<void>;
    resetConfig: () => Promise<void>;
  }>;
}

export interface PluginUiContext {
  readonly pluginId: string;
  registerView(view: PluginViewRegistration): void;
  registerReaderDrawer(drawer: PluginDrawerRegistration): void;
  registerReaderToolbarAction(action: PluginToolbarActionRegistration): void;
  registerBookInfoTab(tab: PluginBookTabRegistration): void;
  registerCustomSettingsPanel(panel: PluginSettingsPanelRegistration): void;
}

export interface JsonSchemaProperty {
  type: 'string' | 'number' | 'integer' | 'boolean';
  title: string;
  description?: string;
  default?: any;
  enum?: string[];
  enumLabels?: string[];
  minimum?: number;
  maximum?: number;
  format?: 'password' | 'uri' | 'textarea';
  'x-rpc-action'?: {
    label: string;
    actionName: string;
  };
}

export interface JsonSchemaObject {
  type: 'object';
  properties: Record<string, JsonSchemaProperty>;
  required?: string[];
}
