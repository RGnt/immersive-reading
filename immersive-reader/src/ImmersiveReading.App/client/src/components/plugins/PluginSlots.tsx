import React from 'react';
import { usePluginStore } from '../../stores/usePluginStore';

export class PluginErrorBoundary extends React.Component<
  { pluginId: string; children: React.ReactNode },
  { error: Error | null }
> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  render() {
    if (this.state.error) {
      return (
        <div className="p-4 m-2 rounded-lg bg-rose-950/60 border border-rose-500/40 text-rose-200 text-xs">
          <p className="font-semibold mb-1">Plugin "{this.props.pluginId}" encountered an error:</p>
          <pre className="whitespace-pre-wrap opacity-80">{this.state.error.message}</pre>
        </div>
      );
    }
    return this.props.children;
  }
}

// 1. Reader Toolbar Slot (Mounted inside ReaderTopBar.tsx)
export const ReaderToolbarPluginSlot: React.FC<{
  onNavigateView?: (viewId: string, params?: Record<string, any>) => void;
}> = ({ onNavigateView }) => {
  const toolbarActions = usePluginStore((s) => s.toolbarActions);
  const drawers = usePluginStore((s) => s.drawers);
  const activeDrawerId = usePluginStore((s) => s.activeDrawerId);
  const toggleDrawer = usePluginStore((s) => s.toggleDrawer);
  const openModal = usePluginStore((s) => s.openModal);
  const plugins = usePluginStore((s) => s.plugins);

  return (
    <div className="flex items-center gap-1">
      {/* Drawer Toggle Buttons */}
      {drawers.map((d) => {
        const Icon = d.icon;
        const isOpen = activeDrawerId === d.id;
        return (
          <button
            key={d.id}
            title={d.title}
            onClick={() => toggleDrawer(d.id)}
            className={`p-2 rounded-md transition-colors cursor-pointer ${
              isOpen ? 'bg-indigo-600 text-white' : 'hover:bg-white/10 text-white/80'
            }`}
          >
            <Icon className="w-4 h-4" />
          </button>
        );
      })}

      {/* Custom Toolbar Action Buttons */}
      {toolbarActions.map((act) => {
        const Icon = act.icon;
        return (
          <button
            key={act.id}
            title={act.label}
            onClick={() =>
              act.onClick({
                openModal: (opts) => openModal({ ...opts, pluginId: act.pluginId }),
                toggleDrawer: (id) => toggleDrawer(id),
                navigateToView: onNavigateView || (() => {}),
                getPluginConfig: () => (plugins[act.pluginId]?.config ?? {}) as any,
              })
            }
            className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-xs font-medium hover:bg-white/10 text-white/80 cursor-pointer"
          >
            <Icon className="w-4 h-4" />
            <span>{act.label}</span>
          </button>
        );
      })}
    </div>
  );
};

// 2. Reader Active Drawer Slot (Mounted inside ReaderView.tsx)
export const ReaderDrawerPluginSlot: React.FC = () => {
  const drawers = usePluginStore((s) => s.drawers);
  const activeDrawerId = usePluginStore((s) => s.activeDrawerId);
  const toggleDrawer = usePluginStore((s) => s.toggleDrawer);

  const activeDrawer = drawers.find((d) => d.id === activeDrawerId);
  if (!activeDrawer) return null;

  const Component = activeDrawer.component;
  const width = activeDrawer.defaultSize ?? 380;

  return (
    <div style={{ width }} className="h-full shrink-0 border-l border-white/10 bg-[var(--bg-secondary)]">
      <PluginErrorBoundary pluginId={activeDrawer.pluginId}>
        <Component onClose={() => toggleDrawer(null)} />
      </PluginErrorBoundary>
    </div>
  );
};

// 3. Global Plugin Modal Host (Mounted in App.tsx)
export const PluginModalHost: React.FC = () => {
  const activeModal = usePluginStore((s) => s.activeModal);
  const closeModal = usePluginStore((s) => s.closeModal);

  if (!activeModal) return null;
  const Component = activeModal.component;

  const sizeClasses = {
    sm: 'max-w-md',
    md: 'max-w-lg',
    lg: 'max-w-2xl',
    xl: 'max-w-4xl',
  };
  const maxWidth = sizeClasses[activeModal.size ?? 'lg'] || 'max-w-2xl';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-xs p-4">
      <div className={`w-full ${maxWidth} rounded-xl bg-[var(--bg-secondary,#18181b)] border border-white/15 shadow-2xl overflow-hidden`}>
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-white/10">
          <h3 className="font-semibold text-base text-white">{activeModal.title}</h3>
          <button onClick={closeModal} className="p-1 rounded hover:bg-white/10 text-white/70 hover:text-white cursor-pointer">
            ✕
          </button>
        </div>
        <div className="p-5 max-h-[80vh] overflow-y-auto">
          <PluginErrorBoundary pluginId={activeModal.pluginId}>
            <Component onClose={closeModal} />
          </PluginErrorBoundary>
        </div>
      </div>
    </div>
  );
};
