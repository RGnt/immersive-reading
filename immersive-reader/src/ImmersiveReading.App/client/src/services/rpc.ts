// Photino JSON-RPC 2.0 Client with Push Event Subscriptions

interface RpcRequest {
  id: string;
  method: string;
  params: Record<string, unknown>;
}

export interface RpcResponse<T = unknown> {
  id: string;
  result?: T;
  error?: {
    code: number;
    message: string;
    data?: unknown;
  };
}

export interface RpcEvent<T = unknown> {
  event: string;
  data: T;
}

type PushEventListener<T = any> = (data: T) => void;

class RpcClient {
  private idCounter = 0;
  private pendingCalls = new Map<string, { resolve: (val: any) => void; reject: (err: any) => void }>();
  private eventListeners = new Map<string, Set<PushEventListener>>();

  constructor() {
    this.initPhotinoReceiver();
  }

  private initPhotinoReceiver() {
    if (typeof window === 'undefined') return;

    // Photino registerWebMessageReceivedHandler bridge
    const ext = (window as any).external;
    if (ext && typeof ext.receiveMessage === 'function') {
      ext.receiveMessage((rawMessage: string) => {
        this.dispatchIncomingMessage(rawMessage);
      });
    } else {
      // In standalone browser (Vite dev server without Photino), listen to message event if any
      window.addEventListener('message', (e) => {
        if (typeof e.data === 'string' && (e.data.startsWith('{') || e.data.startsWith('['))) {
          this.dispatchIncomingMessage(e.data);
        }
      });
    }
  }

  private dispatchIncomingMessage(raw: string) {
    try {
      const parsed = JSON.parse(raw);
      // Check if it's a push event
      if (parsed.event && typeof parsed.event === 'string') {
        const listeners = this.eventListeners.get(parsed.event);
        if (listeners) {
          listeners.forEach((listener) => {
            try {
              listener(parsed.data);
            } catch (err) {
              console.error(`Error in event listener for ${parsed.event}:`, err);
            }
          });
        }
        return;
      }

      // Check if it's a response to a call
      if (parsed.id) {
        const entry = this.pendingCalls.get(parsed.id);
        if (entry) {
          this.pendingCalls.delete(parsed.id);
          if (parsed.error) {
            entry.reject(new Error(parsed.error.message || `RPC Error code ${parsed.error.code}`));
          } else {
            entry.resolve(parsed.result);
          }
        }
      }
    } catch (err) {
      console.error('Failed to parse incoming RPC message:', err, raw);
    }
  }

  public call<T = unknown>(method: string, params: Record<string, unknown> = {}): Promise<T> {
    const id = `req-${++this.idCounter}`;
    const payload: RpcRequest = { id, method, params };
    const raw = JSON.stringify(payload);

    return new Promise<T>((resolve, reject) => {
      this.pendingCalls.set(id, { resolve, reject });

      const ext = (window as any).external;
      if (ext && typeof ext.sendMessage === 'function') {
        ext.sendMessage(raw);
      } else {
        console.warn(`[RPC Mock/Dev] call ${method}:`, params);
        // Timeout reject if running outside Photino without backend
        setTimeout(() => {
          if (this.pendingCalls.has(id)) {
            this.pendingCalls.delete(id);
            reject(new Error(`RPC timeout (no Photino host responding for ${method})`));
          }
        }, 5000);
      }
    });
  }

  public on<T = unknown>(event: string, listener: PushEventListener<T>): () => void {
    if (!this.eventListeners.has(event)) {
      this.eventListeners.set(event, new Set());
    }
    this.eventListeners.get(event)!.add(listener);

    return () => {
      this.eventListeners.get(event)?.delete(listener);
    };
  }
}

export const rpc = new RpcClient();
