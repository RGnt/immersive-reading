import React from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { Keyboard, X } from 'lucide-react';

export const ShortcutsModal: React.FC = () => {
  const { isShortcutsOpen, toggleShortcuts } = useReaderStore();

  if (!isShortcutsOpen) return null;

  return (
    <div
      className="fixed inset-0 z-50 bg-black/70 flex items-center justify-center p-4 backdrop-blur-sm animate-in fade-in duration-150 select-none"
      onClick={() => toggleShortcuts(false)}
    >
      <div
        className="shortcuts-dialog"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="shortcuts-dialog-header">
          <div className="flex items-center space-x-2">
            <Keyboard className="w-5 h-5 text-[var(--accent-color)]" />
            <h2 className="text-base font-bold text-[var(--text-primary)]">
              Keyboard Shortcuts & Vim Motions
            </h2>
          </div>
          <button
            onClick={() => toggleShortcuts(false)}
            className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)]"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="shortcuts-grid">
          {/* Navigation & Scrolling */}
          <div className="shortcut-group">
            <div className="shortcut-group-title">Navigation & Scrolling</div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Scroll Down / Up</span>
              <div className="shortcut-keys">
                <kbd>j</kbd> <kbd>k</kbd> or <kbd>↓</kbd> <kbd>↑</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Page Forward (Vertical)</span>
              <div className="shortcut-keys">
                <kbd>h</kbd> or <kbd>←</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Page Backward (Vertical)</span>
              <div className="shortcut-keys">
                <kbd>l</kbd> or <kbd>→</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Full Page Scroll</span>
              <div className="shortcut-keys">
                <kbd>Space</kbd> / <kbd>⇧ Space</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Half Page Scroll</span>
              <div className="shortcut-keys">
                <kbd>Ctrl+d</kbd> / <kbd>Ctrl+u</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Jump to Chapter Start</span>
              <div className="shortcut-keys">
                <kbd>gg</kbd> or <kbd>Home</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Jump to Chapter End</span>
              <div className="shortcut-keys">
                <kbd>G</kbd> or <kbd>End</kbd>
              </div>
            </div>
          </div>

          {/* Word Cursor & Dictionary */}
          <div className="shortcut-group">
            <div className="shortcut-group-title">Word Cursor & Dictionary</div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Move to Next Word</span>
              <div className="shortcut-keys">
                <kbd>w</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Move to Previous Word</span>
              <div className="shortcut-keys">
                <kbd>b</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Look Up Highlighted Word</span>
              <div className="shortcut-keys">
                <kbd>Enter</kbd> <kbd>e</kbd> or <kbd>d</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Mark Learning / Known</span>
              <div className="shortcut-keys">
                <kbd>1</kbd> / <kbd>2</kbd> (in popover)
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Mine 1-Click Flashcard</span>
              <div className="shortcut-keys">
                <kbd>m</kbd> (in popover)
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Dismiss Popover / Cursor</span>
              <div className="shortcut-keys">
                <kbd>Esc</kbd>
              </div>
            </div>
          </div>

          {/* Reader Display & Chapters */}
          <div className="shortcut-group">
            <div className="shortcut-group-title">Display & Chapters</div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Cycle Furigana Mode</span>
              <div className="shortcut-keys">
                <kbd>f</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Toggle Vertical / Horizontal</span>
              <div className="shortcut-keys">
                <kbd>v</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Font Size Larger / Smaller</span>
              <div className="shortcut-keys">
                <kbd>+</kbd> / <kbd>-</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Reset Font Size (22px)</span>
              <div className="shortcut-keys">
                <kbd>0</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Next / Previous Chapter</span>
              <div className="shortcut-keys">
                <kbd>]</kbd> / <kbd>[</kbd> or <kbd>n</kbd> / <kbd>p</kbd>
              </div>
            </div>
          </div>

          {/* Panels & Help */}
          <div className="shortcut-group">
            <div className="shortcut-group-title">Panels & System</div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Find in Chapter</span>
              <div className="shortcut-keys">
                <kbd>Ctrl+f</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Bookmarks Drawer</span>
              <div className="shortcut-keys">
                <kbd>Ctrl+b</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">AI Companion (Quiz / Chat)</span>
              <div className="shortcut-keys">
                <kbd>a</kbd> or <kbd>c</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Study Pack Master Sheets</span>
              <div className="shortcut-keys">
                <kbd>s</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Preferences & Settings</span>
              <div className="shortcut-keys">
                <kbd>Ctrl+,</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Close Reader / Back to Library</span>
              <div className="shortcut-keys">
                <kbd>q</kbd> or <kbd>Esc</kbd>
              </div>
            </div>
            <div className="shortcut-row">
              <span className="text-[var(--text-primary)]">Show This Help Cheatsheet</span>
              <div className="shortcut-keys">
                <kbd>?</kbd>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};

