import React from 'react';
import { Palette } from 'lucide-react';

const PLUGIN_ID = 'posHighlighter';

export function activate(ctx) {
  // Register Reader Toolbar Action to toggle POS highlighting on/off
  ctx.registerReaderToolbarAction({
    id: 'pos-highlighter-toggle-btn',
    label: 'POS Colors',
    icon: Palette,
    onClick: async ({ rpc, toast }) => {
      try {
        const res = await rpc(`ext.${PLUGIN_ID}.toggle`, {});
        const isEnabled = res?.enabled;
        if (toast) {
          toast.info(isEnabled ? 'Grammar POS highlighting active' : 'Grammar POS highlighting turned off');
        }
      } catch (err) {
        console.error('Failed to toggle POS highlighting', err);
      }
    },
  });
}

export function deactivate() {
  // Host unregisters toolbar action on disable
}
