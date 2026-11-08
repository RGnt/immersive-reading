import React, { useState } from 'react';
import { usePluginStore } from '../../stores/usePluginStore';
import { rpc } from '../../services/rpc';
import { Button } from '../../sdk';
import type { JsonSchemaObject, PluginDescriptorDto } from '../../sdk/types';

export const PluginSettingsTab: React.FC = () => {
  const plugins = Object.values(usePluginStore((s) => s.plugins));
  const setPluginEnabled = usePluginStore((s) => s.setPluginEnabled);
  const updatePluginConfig = usePluginStore((s) => s.updatePluginConfig);
  const resetPluginConfig = usePluginStore((s) => s.resetPluginConfig);
  const customSettingsPanels = usePluginStore((s) => s.customSettingsPanels);

  const [selectedPluginId, setSelectedPluginId] = useState<string | null>(
    plugins[0]?.manifest.id ?? null
  );

  const selectedPlugin = plugins.find((p) => p.manifest.id === selectedPluginId) || plugins[0];

  return (
    <div className="grid grid-cols-12 gap-6 h-full min-h-[400px]">
      {/* Left Column: Plugin List & Enable/Disable Toggles */}
      <div className="col-span-5 space-y-3 border-r border-white/10 pr-4">
        <h3 className="text-xs font-semibold uppercase tracking-wider text-white/70">
          Installed Plugins ({plugins.length})
        </h3>
        {plugins.length === 0 ? (
          <p className="text-xs text-white/50 py-4">No plugins installed.</p>
        ) : (
          plugins.map((p) => {
            const isEnabled = p.state === 'Enabled' || p.state === 'PendingEnable';
            const isSelected = p.manifest.id === (selectedPlugin?.manifest.id);

            return (
              <div
                key={p.manifest.id}
                onClick={() => setSelectedPluginId(p.manifest.id)}
                className={`p-3.5 rounded-lg border cursor-pointer transition-colors ${
                  isSelected
                    ? 'border-indigo-500 bg-indigo-500/10'
                    : 'border-white/10 bg-white/5 hover:bg-white/10'
                }`}
              >
                <div className="flex items-center justify-between">
                  <div>
                    <span className="font-medium text-sm text-white">{p.manifest.name}</span>
                    <span className="ml-2 text-xs text-white/50">v{p.manifest.version}</span>
                  </div>
                  <input
                    type="checkbox"
                    checked={isEnabled}
                    onChange={(e) => {
                      e.stopPropagation();
                      setPluginEnabled(p.manifest.id, e.target.checked);
                    }}
                    className="h-4 w-4 accent-indigo-600 cursor-pointer"
                  />
                </div>
                <p className="text-xs text-white/70 mt-1 line-clamp-2">{p.manifest.description}</p>
                {p.requiresRestart && (
                  <div className="mt-2 inline-block px-2 py-0.5 rounded text-[11px] bg-amber-500/20 text-amber-300 border border-amber-500/30">
                    Restart application to apply change
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>

      {/* Right Column: Selected Plugin Configuration */}
      <div className="col-span-7 overflow-y-auto pr-1">
        {selectedPlugin ? (
          <PluginConfigurationPane
            plugin={selectedPlugin}
            customPanel={customSettingsPanels[selectedPlugin.manifest.id]}
            onUpdate={(partial) => updatePluginConfig(selectedPlugin.manifest.id, partial)}
            onReset={() => resetPluginConfig(selectedPlugin.manifest.id)}
          />
        ) : (
          <p className="text-sm text-white/60">Select a plugin to configure its settings.</p>
        )}
      </div>
    </div>
  );
};

const PluginConfigurationPane: React.FC<{
  plugin: PluginDescriptorDto;
  customPanel?: { component: React.ComponentType<any> };
  onUpdate: (partial: Record<string, any>) => Promise<void>;
  onReset: () => Promise<void>;
}> = ({ plugin, customPanel, onUpdate, onReset }) => {
  const [actionStatus, setActionStatus] = useState<Record<string, string>>({});

  if (customPanel) {
    const CustomComp = customPanel.component;
    return <CustomComp config={plugin.config} updateConfig={onUpdate} resetConfig={onReset} />;
  }

  const schema = plugin.schema as JsonSchemaObject | undefined;
  if (!schema || !schema.properties || Object.keys(schema.properties).length === 0) {
    return (
      <div className="space-y-4">
        <h4 className="font-semibold text-base text-white">{plugin.manifest.name}</h4>
        <p className="text-sm text-white/60">This plugin has no configurable settings.</p>
      </div>
    );
  }

  const runSchemaRpcAction = async (actionName: string) => {
    setActionStatus((s) => ({ ...s, [actionName]: 'Running...' }));
    try {
      const res = await rpc.call<any>(
        `ext.${plugin.manifest.id}.${actionName}`,
        plugin.config
      );
      setActionStatus((s) => ({
        ...s,
        [actionName]: res?.message ?? `OK (${res?.latencyMs ?? 0}ms)`,
      }));
    } catch (err: any) {
      setActionStatus((s) => ({ ...s, [actionName]: `Error: ${err.message}` }));
    }
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between border-b border-white/10 pb-3">
        <h4 className="font-semibold text-base text-white">{plugin.manifest.name} Settings</h4>
        <Button variant="ghost" size="sm" onClick={onReset}>
          Reset to Defaults
        </Button>
      </div>

      <div className="space-y-4">
        {Object.entries(schema.properties).map(([key, prop]) => {
          const val = plugin.config?.[key] ?? prop.default;

          return (
            <div key={key} className="space-y-1.5">
              <label className="block text-xs font-medium text-white/90">{prop.title}</label>
              {prop.description && (
                <p className="text-[11px] text-white/60">{prop.description}</p>
              )}

              {prop.enum ? (
                <select
                  value={String(val ?? '')}
                  onChange={(e) => onUpdate({ [key]: e.target.value })}
                  className="w-full px-3 py-1.5 rounded bg-black/40 border border-white/15 text-sm text-white"
                >
                  {prop.enum.map((opt, idx) => (
                    <option key={opt} value={opt} className="bg-zinc-900 text-white">
                      {prop.enumLabels?.[idx] ?? opt}
                    </option>
                  ))}
                </select>
              ) : prop.type === 'boolean' ? (
                <label className="flex items-center gap-2 cursor-pointer pt-1">
                  <input
                    type="checkbox"
                    checked={Boolean(val)}
                    onChange={(e) => onUpdate({ [key]: e.target.checked })}
                    className="h-4 w-4 accent-indigo-600 rounded cursor-pointer"
                  />
                  <span className="text-xs text-white/80">Enabled</span>
                </label>
              ) : prop.format === 'color' ? (
                <div className="flex items-center gap-2.5">
                  <input
                    type="color"
                    value={val || '#ffffff'}
                    onChange={(e) => onUpdate({ [key]: e.target.value })}
                    className="w-9 h-8 rounded border border-white/20 bg-transparent cursor-pointer p-0.5"
                    title="Click to pick a color"
                  />
                  <input
                    type="text"
                    value={val ?? ''}
                    onChange={(e) => onUpdate({ [key]: e.target.value })}
                    className="flex-1 px-3 py-1.5 rounded bg-black/40 border border-white/15 text-sm text-white font-mono"
                    placeholder="#rrggbb"
                  />
                </div>
              ) : (
                <input
                  type={
                    prop.format === 'password'
                      ? 'password'
                      : prop.type === 'number' || prop.type === 'integer'
                      ? 'number'
                      : 'text'
                  }
                  value={val ?? ''}
                  onChange={(e) =>
                    onUpdate({
                      [key]:
                        prop.type === 'number' || prop.type === 'integer'
                          ? Number(e.target.value)
                          : e.target.value,
                    })
                  }
                  className="w-full px-3 py-1.5 rounded bg-black/40 border border-white/15 text-sm text-white"
                />
              )}

              {prop['x-rpc-action'] && (
                <div className="flex items-center gap-3 pt-1">
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => runSchemaRpcAction(prop['x-rpc-action']!.actionName)}
                  >
                    {prop['x-rpc-action'].label}
                  </Button>
                  {actionStatus[prop['x-rpc-action'].actionName] && (
                    <span className="text-xs text-white/80">
                      {actionStatus[prop['x-rpc-action'].actionName]}
                    </span>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};
