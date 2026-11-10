import { useEffect, useRef } from 'react';
import { useReaderStore } from '../stores/useReaderStore';
import { usePluginStore } from '../stores/usePluginStore';
import { useStudyPackStore } from '../stores/useStudyPackStore';
import { useConfigStore } from '../stores/useConfigStore';

export const sendVimMotion = (payload: any) => {
  const iframe = document.getElementById('reader-frame') as HTMLIFrameElement | null;
  if (iframe?.contentWindow) {
    iframe.contentWindow.postMessage({ type: 'ir-vim-motion', ...payload }, '*');
  }
};

export const useKeyboardShortcuts = () => {
  const lastVimKeyChar = useRef('');
  const lastVimKeyTime = useRef(0);

  useEffect(() => {
    const handleKey = (e: {
      key: string;
      code?: string;
      ctrlKey?: boolean;
      metaKey?: boolean;
      shiftKey?: boolean;
      altKey?: boolean;
      preventDefault?: () => void;
    }) => {
      const reader = useReaderStore.getState();
      const plugin = usePluginStore.getState();
      const studyPack = useStudyPackStore.getState();
      const config = useConfigStore.getState();

      const activeEl = document.activeElement;
      const isInput =
        activeEl?.tagName === 'INPUT' ||
        activeEl?.tagName === 'TEXTAREA' ||
        activeEl?.tagName === 'SELECT';

      // Global Settings shortcut: Ctrl+, or Cmd+,
      if ((e.ctrlKey || e.metaKey) && e.key === ',') {
        e.preventDefault?.();
        config.openSettings();
        return true;
      }

      // If user is typing in an input/textarea/select
      if (isInput) {
        if (e.key === 'Escape') {
          (activeEl as HTMLElement).blur();
          if (reader.isSearchOpen) reader.closeSearch();
          return true;
        }
        return false;
      }

      // If reader is NOT active, only global library shortcuts apply
      if (!reader.isActive) {
        return false;
      }

      const key = e.key;

      // Global reader search: Ctrl+F or Cmd+F
      if ((e.ctrlKey || e.metaKey) && (key === 'f' || key === 'F')) {
        e.preventDefault?.();
        reader.openSearch();
        return true;
      }

      // Modal / Drawer Dismissals via Escape
      if (key === 'Escape') {
        e.preventDefault?.();
        if (reader.isShortcutsOpen) {
          reader.toggleShortcuts(false);
          return true;
        }
        if (reader.isSearchOpen) {
          sendVimMotion({ action: 'ir-search-clear' });
          reader.closeSearch();
          return true;
        }
        if (reader.popover.isOpen) {
          reader.closePopover();
          return true;
        }
        if (reader.isBookmarksOpen) {
          reader.toggleBookmarks();
          return true;
        }
        if (plugin.activeDrawerId) {
          plugin.toggleDrawer(null);
          return true;
        }
        if (plugin.activeModal) {
          plugin.closeModal();
          return true;
        }
        if (studyPack.isOpen) {
          studyPack.closeDrawer();
          return true;
        }
        if (config.isSettingsOpen) {
          config.closeSettings();
          return true;
        }

        sendVimMotion({ action: 'clear-word-cursor' });
        reader.closeReader();
        return true;
      }

      // Shortcuts Modal Toggle via '?'
      if (key === '?' && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault?.();
        reader.toggleShortcuts();
        return true;
      }

      // If Shortcuts modal is currently open, block other keys except Escape and ?
      if (reader.isShortcutsOpen) {
        return false;
      }

      // If Dictionary Popover is open, handle status updates and flashcard mining
      if (reader.popover.isOpen) {
        if (key === '1') {
          e.preventDefault?.();
          reader.updateWordStatus(0, 1);
          reader.showToast('Marked: Learning (1)');
          return true;
        }
        if (key === '2') {
          e.preventDefault?.();
          reader.updateWordStatus(0, 2);
          reader.showToast('Marked: Known (2)');
          return true;
        }
        if (key === 'm' || key === 'M') {
          e.preventDefault?.();
          reader.mineFlashcard(0);
          reader.showToast('Mined flashcard!');
          return true;
        }
      }

      // Double 'g' sequence for jump to chapter start
      if (key === 'g' && !e.ctrlKey && !e.metaKey && !e.altKey) {
        const now = Date.now();
        if (lastVimKeyChar.current === 'g' && now - lastVimKeyTime.current < 500) {
          lastVimKeyChar.current = '';
          sendVimMotion({ action: 'scroll-to', target: 'start' });
          reader.showToast('Jump to Chapter Start (gg)');
          return true;
        }
        lastVimKeyChar.current = 'g';
        lastVimKeyTime.current = now;
        return true;
      }
      lastVimKeyChar.current = '';

      switch (key) {
        // Vertical step scroll
        case 'j':
        case 'ArrowDown':
          e.preventDefault?.();
          sendVimMotion({ action: 'scroll-step', direction: 'down' });
          return true;

        case 'k':
        case 'ArrowUp':
          e.preventDefault?.();
          sendVimMotion({ action: 'scroll-step', direction: 'up' });
          return true;

        // Horizontal / Page navigation
        case 'h':
        case 'ArrowLeft':
          e.preventDefault?.();
          if (reader.writingMode === 'vertical-rl') {
            sendVimMotion({ action: 'scroll-page', forward: true });
            reader.showToast('Page Forward (h)');
          } else {
            sendVimMotion({ action: 'scroll-step', direction: 'left' });
          }
          return true;

        case 'l':
        case 'ArrowRight':
          e.preventDefault?.();
          if (reader.writingMode === 'vertical-rl') {
            sendVimMotion({ action: 'scroll-page', forward: false });
            reader.showToast('Page Backward (l)');
          } else {
            sendVimMotion({ action: 'scroll-step', direction: 'right' });
          }
          return true;

        // Full Page Scrolling
        case ' ':
          e.preventDefault?.();
          if (e.shiftKey) {
            sendVimMotion({ action: 'scroll-page', forward: false });
            reader.showToast('Page Backward (Shift+Space)');
          } else {
            sendVimMotion({ action: 'scroll-page', forward: true });
            reader.showToast('Page Forward (Space)');
          }
          return true;

        case 'PageDown':
          e.preventDefault?.();
          sendVimMotion({ action: 'scroll-page', forward: true });
          return true;

        case 'PageUp':
          e.preventDefault?.();
          sendVimMotion({ action: 'scroll-page', forward: false });
          return true;

        // Half-page motions and word lookup
        case 'd':
          e.preventDefault?.();
          if (e.ctrlKey || e.metaKey) {
            sendVimMotion({ action: 'scroll-half-page', forward: true });
            reader.showToast('Half Page Down (Ctrl+d)');
            return true;
          }
          sendVimMotion({ action: 'lookup-current-word' });
          return true;

        case 'u':
          if (e.ctrlKey || e.metaKey) {
            e.preventDefault?.();
            sendVimMotion({ action: 'scroll-half-page', forward: false });
            reader.showToast('Half Page Up (Ctrl+u)');
            return true;
          }
          return false;

        // Jump to Start / End
        case 'G':
        case 'End':
          e.preventDefault?.();
          sendVimMotion({ action: 'scroll-to', target: 'end' });
          reader.showToast('Jump to Chapter End (G)');
          return true;

        case 'Home':
          e.preventDefault?.();
          sendVimMotion({ action: 'scroll-to', target: 'start' });
          reader.showToast('Jump to Chapter Start (Home)');
          return true;

        // Word Cursor Navigation (w = next, b = prev, e/Enter = lookup)
        case 'w':
          e.preventDefault?.();
          sendVimMotion({ action: 'next-word' });
          return true;

        case 'b':
          e.preventDefault?.();
          if (e.ctrlKey || e.metaKey) {
            reader.toggleBookmarks();
            return true;
          }
          sendVimMotion({ action: 'prev-word' });
          return true;

        case 'e':
        case 'Enter':
          e.preventDefault?.();
          sendVimMotion({ action: 'lookup-current-word' });
          return true;

        // Chapter Navigation
        case ']':
        case 'n':
          e.preventDefault?.();
          reader.nextChapter();
          return true;

        case '[':
        case 'p':
          e.preventDefault?.();
          reader.prevChapter();
          return true;

        // Display Controls
        case 'f':
          e.preventDefault?.();
          reader.cycleFuriganaMode();
          return true;

        case 'v':
          e.preventDefault?.();
          reader.toggleWritingMode();
          reader.showToast(
            `Writing Mode: ${reader.writingMode === 'vertical-rl' ? 'Horizontal 横書き' : 'Vertical 縦書き'}`
          );
          return true;

        case '+':
        case '=':
          e.preventDefault?.();
          reader.adjustFontSize(2);
          sendVimMotion({ action: 'adjust-font-size', delta: 2 });
          reader.showToast(`Font Size: ${reader.fontSizePx + 2}px`);
          return true;

        case '-':
        case '_':
          e.preventDefault?.();
          reader.adjustFontSize(-2);
          sendVimMotion({ action: 'adjust-font-size', delta: -2 });
          reader.showToast(`Font Size: ${reader.fontSizePx - 2}px`);
          return true;

        case '0':
          e.preventDefault?.();
          reader.adjustFontSize(22 - reader.fontSizePx);
          sendVimMotion({ action: 'adjust-font-size', delta: 0 });
          reader.showToast('Font Size Reset (22px)');
          return true;

        // Plugin Drawers
        case 'a':
        case 'c':
          e.preventDefault?.();
          if (plugin.activeDrawerId) {
            plugin.toggleDrawer(null);
          } else if (plugin.drawers.length > 0) {
            plugin.toggleDrawer(plugin.drawers[0].id);
          }
          return true;

        case 's':
          e.preventDefault?.();
          if (studyPack.isOpen) studyPack.closeDrawer();
          else if (reader.bookDetail) studyPack.openDrawer(reader.bookDetail.summary.bookId);
          return true;

        case 'q':
          e.preventDefault?.();
          reader.closeReader();
          return true;

        default:
          return false;
      }
    };

    const handleWindowKeyDown = (e: KeyboardEvent) => {
      handleKey(e);
    };

    const handleWindowMessage = (e: MessageEvent) => {
      const data = e.data;
      if (!data || typeof data !== 'object') return;

      if (data.type === 'ir-reader-keydown') {
        handleKey(data);
      } else if (data.type === 'ir-word-focused') {
        if (data.surface) {
          useReaderStore.getState().showToast(`Word: ${data.surface}`);
        }
      }
    };

    window.addEventListener('keydown', handleWindowKeyDown);
    window.addEventListener('message', handleWindowMessage);

    return () => {
      window.removeEventListener('keydown', handleWindowKeyDown);
      window.removeEventListener('message', handleWindowMessage);
    };
  }, []);
};

