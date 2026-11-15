import { create } from 'zustand';
import { rpc } from '../services/rpc';

/** Id of the style element in every document the user stylesheet is applied to (the app window and each chapter iframe). */
export const USER_CSS_ELEMENT_ID = 'ir-user-css';

interface UserCssDto {
  css: string;
  enabled: boolean;
  maxBytes: number;
}

interface AppearanceState {
  /** The saved stylesheet. */
  css: string;
  enabled: boolean;
  /** The text being edited in Settings, shown live before it is saved; null when nothing is being previewed. */
  preview: string | null;
  maxBytes: number;
  loaded: boolean;
  load: () => Promise<void>;
  save: (css: string, enabled: boolean) => Promise<void>;
  setEnabled: (enabled: boolean) => Promise<void>;
  setPreview: (css: string | null) => void;
}

/** What is applied right now: the preview while editing, else the saved text; nothing when switched off. */
export function effectiveUserCss(s: Pick<AppearanceState, 'css' | 'enabled' | 'preview'>): string {
  return s.enabled || s.preview !== null ? (s.preview ?? s.css) : '';
}

/** Puts the user stylesheet into a document (replacing what was there), or removes it. Used for the app and for the reader iframe. */
export function applyUserCssTo(doc: Document, css: string) {
  doc.documentElement.classList.add(doc === document ? 'ir-app' : 'ir-reader');
  let el = doc.getElementById(USER_CSS_ELEMENT_ID) as HTMLStyleElement | null;
  if (!css.trim()) {
    el?.remove();
    return;
  }
  if (!el) {
    el = doc.createElement('style');
    el.id = USER_CSS_ELEMENT_ID;
  }
  if (el.textContent !== css) el.textContent = css;
  (doc.head ?? doc.documentElement).appendChild(el);      // last in <head>: at equal specificity it wins over the app's own styles
}

export const useAppearanceStore = create<AppearanceState>((set, get) => {
  const apply = () => applyUserCssTo(document, effectiveUserCss(get()));
  const adopt = (dto: UserCssDto) => {
    set({ css: dto.css, enabled: dto.enabled, maxBytes: dto.maxBytes });
    apply();
  };
  return {
    css: '',
    enabled: true,
    preview: null,
    maxBytes: 512 * 1024,
    loaded: false,

    load: async () => {
      try {
        adopt(await rpc.call<UserCssDto>('appearance.getUserCss', {}));
      } catch {
        // no stylesheet is better than a blocked start
      }
      set({ loaded: true });
    },

    save: async (css, enabled) => {
      adopt(await rpc.call<UserCssDto>('appearance.setUserCss', { css, enabled }));
      set({ preview: null });
      apply();
    },

    setEnabled: async (enabled) => adopt(await rpc.call<UserCssDto>('appearance.setUserCssEnabled', { enabled })),

    setPreview: (preview) => {
      set({ preview });
      apply();
    },
  };
});
