import React, { useEffect, useCallback } from 'react';
import { rpc } from '../services/rpc';
import { useReaderStore } from '../stores/useReaderStore';
import { usePluginStore } from '../stores/usePluginStore';

export * from './types';

// 1. Scoped RPC Hook for Plugins
export function usePluginRpc(pluginId: string) {
  const call = useCallback(
    <TResult = any, TPayload = Record<string, any>>(
      actionName: string,
      payload?: TPayload
    ): Promise<TResult> => {
      return rpc.call<TResult>(`ext.${pluginId}.${actionName}`, payload ?? {});
    },
    [pluginId]
  );

  const useEvent = useCallback(
    <TPayload = any>(eventName: string, handler: (payload: TPayload) => void) => {
      useEffect(() => {
        const fullEvent = `ext.${pluginId}.${eventName}`;
        return rpc.on(fullEvent, handler);
      }, [pluginId, eventName, handler]);
    },
    [pluginId]
  );

  return { call, useEvent };
}

// 2. Reactive Plugin Configuration Hook
export function usePluginConfig<T extends Record<string, any>>(pluginId: string) {
  const plugin = usePluginStore((s) => s.plugins[pluginId]);
  const updatePluginConfig = usePluginStore((s) => s.updatePluginConfig);
  const resetPluginConfig = usePluginStore((s) => s.resetPluginConfig);

  return {
    config: (plugin?.config ?? {}) as T,
    updateConfig: (partial: Partial<T>) => updatePluginConfig(pluginId, partial),
    resetConfig: () => resetPluginConfig(pluginId),
  };
}

// 2b. Live reader style injection. Replaces (or with `null` removes) <style id=styleId> in the open
// chapter without reloading it. Using the same id the server injected replaces that style; pass '' to
// blank a style. `null` forgets the override (the DOM style stays until the next chapter load).
export function setReaderStyle(pluginId: string, styleId: string, css: string | null): void {
  usePluginStore.getState().setReaderStyle(pluginId, styleId, css);
}

// 3. Active Reader Context Hook (Zero-Spoiler Safe)
export function useActiveBook() {
  return useReaderStore((s) => ({
    bookId: s.bookDetail?.summary.bookId ?? '',
    chapterIndex: s.currentChapterIndex,
    spineHref: s.currentSpineHref,
    viewportTokenId: 0,
    language: s.bookDetail?.summary.language ?? 'ja',
  }));
}

// 4. Interactive Linguistic Building Block (<EnrichedText />)
export const EnrichedText: React.FC<{
  html: string;
  className?: string;
}> = ({ html, className = '' }) => {
  const handleTokenClick = useReaderStore((s) => s.handleTokenClick);

  const handleClick = (e: React.MouseEvent<HTMLDivElement>) => {
    const target = (e.target as HTMLElement).closest('.ir-tok') as HTMLElement | null;
    if (!target) return;

    const tokIdStr = target.getAttribute('data-tok-id');
    const chHref = target.getAttribute('data-ch-href') || undefined;
    const bookId = target.getAttribute('data-book-id') || undefined;
    const rect = target.getBoundingClientRect();

    if (tokIdStr !== null) {
      const tokenId = parseInt(tokIdStr, 10);
      handleTokenClick(tokenId, rect, chHref, bookId);
    }
  };

  return (
    <div
      className={`ir-enriched-surface leading-relaxed select-text ${className}`}
      onClick={handleClick}
      dangerouslySetInnerHTML={{ __html: html }}
    />
  );
};

// 5. Layout & Shell Primitives
export const PluginPageLayout: React.FC<{
  title: string;
  subtitle?: string;
  actions?: React.ReactNode;
  children: React.ReactNode;
}> = ({ title, subtitle, actions, children }) => (
  <div className="flex flex-col h-full w-full bg-[var(--bg-primary)] text-[var(--text-primary)]">
    <header className="flex items-center justify-between px-6 py-4 border-b border-white/10">
      <div>
        <h1 className="text-xl font-semibold">{title}</h1>
        {subtitle && <p className="text-sm text-[var(--text-secondary)]">{subtitle}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </header>
    <main className="flex-1 overflow-y-auto p-6">{children}</main>
  </div>
);

export const PluginDrawerShell: React.FC<{
  title: string;
  onClose: () => void;
  headerActions?: React.ReactNode;
  footer?: React.ReactNode;
  children: React.ReactNode;
}> = ({ title, onClose, headerActions, footer, children }) => (
  <aside className="flex flex-col h-full w-full bg-[var(--bg-secondary)] text-[var(--text-primary)] border-l border-white/10 shadow-xl">
    <div className="flex items-center justify-between px-4 py-3 border-b border-white/10">
      <h2 className="font-medium text-sm tracking-wide uppercase opacity-80">{title}</h2>
      <div className="flex items-center gap-1.5">
        {headerActions}
        <button
          onClick={onClose}
          className="p-1 rounded hover:bg-white/10 text-xs opacity-70 hover:opacity-100"
        >
          ✕
        </button>
      </div>
    </div>
    <div className="flex-1 overflow-y-auto p-4">{children}</div>
    {footer && <div className="p-3 border-t border-white/10 bg-[var(--bg-primary)]">{footer}</div>}
  </aside>
);

export const Button: React.FC<
  React.ButtonHTMLAttributes<HTMLButtonElement> & {
    variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
    size?: 'sm' | 'md';
  }
> = ({ variant = 'primary', size = 'md', className = '', children, ...props }) => {
  const base = 'inline-flex items-center justify-center rounded-md font-medium transition-colors disabled:opacity-50 cursor-pointer';
  const sizes = size === 'sm' ? 'px-2.5 py-1.5 text-xs' : 'px-4 py-2 text-sm';
  const variants = {
    primary: 'bg-indigo-600 hover:bg-indigo-500 text-white',
    secondary: 'bg-white/10 hover:bg-white/15 text-[var(--text-primary)]',
    ghost: 'hover:bg-white/10 text-[var(--text-secondary)]',
    danger: 'bg-rose-600 hover:bg-rose-500 text-white',
  };
  return (
    <button className={`${base} ${sizes} ${variants[variant]} ${className}`} {...props}>
      {children}
    </button>
  );
};
