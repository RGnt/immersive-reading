import * as React from 'react';
import * as ReactDOM from 'react-dom';
import * as JsxRuntime from 'react/jsx-runtime';
import * as LucideReact from 'lucide-react';
import * as HostSdk from './index';
import { rpc } from '../services/rpc';

export function exposeHostRuntime() {
  if (typeof window !== 'undefined') {
    (window as any).__IR_RUNTIME__ = {
      React,
      ReactDOM,
      JsxRuntime,
      LucideReact,
      HostSdk,
    };
    (window as any).irBridge = {
      onEvent: (event: string, handler: (data: any) => void) => rpc.on(event, handler),
      offEvent: () => {},
    };
  }
}
