import React, { useEffect, useRef } from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { Search, ChevronUp, ChevronDown, X } from 'lucide-react';

export const ReaderSearchBar: React.FC = () => {
  const {
    isSearchOpen,
    searchQuery,
    setSearchQuery,
    searchMatchCount,
    searchCurrentIndex,
    closeSearch,
  } = useReaderStore();

  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (isSearchOpen) {
      setTimeout(() => inputRef.current?.focus(), 50);
    }
  }, [isSearchOpen]);

  if (!isSearchOpen) return null;

  const sendIframeMessage = (msg: any) => {
    const iframe = document.getElementById('reader-frame') as HTMLIFrameElement | null;
    if (iframe?.contentWindow) {
      iframe.contentWindow.postMessage(msg, '*');
    }
  };

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const q = e.target.value;
    setSearchQuery(q);
    sendIframeMessage({ type: 'ir-search-query', query: q });
  };

  const handleNext = () => {
    sendIframeMessage({ type: 'ir-search-step', forward: true });
  };

  const handlePrev = () => {
    sendIframeMessage({ type: 'ir-search-step', forward: false });
  };

  const handleClose = () => {
    sendIframeMessage({ type: 'ir-search-clear' });
    closeSearch();
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      handleClose();
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (e.shiftKey) {
        handlePrev();
      } else {
        handleNext();
      }
    }
  };

  return (
    <div className="absolute top-3 right-6 z-40 bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-lg shadow-2xl p-1.5 flex items-center space-x-2 text-xs animate-in slide-in-from-top-2 duration-150 backdrop-blur-md">
      <Search className="w-3.5 h-3.5 text-[var(--accent-color)] ml-1 shrink-0" />

      <input
        ref={inputRef}
        type="text"
        value={searchQuery}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
        placeholder="Find in chapter (Ctrl+F)..."
        className="px-2 py-1 bg-[var(--bg-primary)] border border-[var(--border-color)] rounded text-xs text-[var(--text-primary)] w-52 focus:outline-none focus:border-[var(--accent-color)]"
      />

      <span className="text-[10px] text-[var(--text-muted)] font-mono px-1 select-none whitespace-nowrap">
        {searchQuery.trim().length === 0
          ? ''
          : searchMatchCount === 0
          ? '0 matches'
          : `${searchCurrentIndex} of ${searchMatchCount}`}
      </span>

      <div className="flex items-center space-x-0.5 border-l border-[var(--border-color)] pl-1">
        <button
          onClick={handlePrev}
          disabled={searchMatchCount === 0}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] disabled:opacity-30 disabled:cursor-not-allowed"
          title="Previous Match (Shift+Enter)"
        >
          <ChevronUp className="w-3.5 h-3.5" />
        </button>

        <button
          onClick={handleNext}
          disabled={searchMatchCount === 0}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] disabled:opacity-30 disabled:cursor-not-allowed"
          title="Next Match (Enter)"
        >
          <ChevronDown className="w-3.5 h-3.5" />
        </button>

        <button
          onClick={handleClose}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] ml-1"
          title="Close search (Esc)"
        >
          <X className="w-3.5 h-3.5" />
        </button>
      </div>
    </div>
  );
};

