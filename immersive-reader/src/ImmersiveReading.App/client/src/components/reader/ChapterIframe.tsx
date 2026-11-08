import React, { useEffect, useRef, useCallback } from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { useConfigStore } from '../../stores/useConfigStore';

export const ChapterIframe: React.FC = () => {
  const {
    bookDetail,
    currentSpineHref,
    chapterHtml,
    handleTokenClick,
    closePopover,
    loadChapter,
    furiganaMode,
    writingMode,
    fontSizePx,
  } = useReaderStore();
  const config = useConfigStore((s) => s.config);
  const iframeRef = useRef<HTMLIFrameElement>(null);

  const applyReaderThemeToIframe = useCallback(() => {
    const iframe = iframeRef.current;
    if (!iframe) return;
    try {
      const doc = iframe.contentDocument;
      if (!doc || !doc.documentElement) return;
      const rRoot = doc.documentElement;

      const bgPrimary = config?.appearance?.bgPrimary || '#0f172a';
      const textPrimary = config?.appearance?.textPrimary || '#f8fafc';
      const rubyColor = config?.appearance?.rubyColor || '#e2e8f0';
      const readerFont = config?.reader?.fontFamily || '"Noto Serif JP", "Yu Mincho", serif';
      const fontSize = fontSizePx || config?.reader?.fontSizePx || 22;
      const lineHeight = config?.reader?.lineHeight || 1.85;

      rRoot.style.setProperty('--bg-primary', bgPrimary);
      rRoot.style.setProperty('--text-primary', textPrimary);
      rRoot.style.setProperty('--ruby-color', rubyColor);
      rRoot.style.setProperty('--reader-font', readerFont);
      rRoot.style.setProperty('--reader-font-size', `${fontSize}px`);
      rRoot.style.setProperty('--reader-line-height', String(lineHeight));

      let styleEl = doc.getElementById('ir-theme-injected-overrides') as HTMLStyleElement | null;
      if (!styleEl) {
        styleEl = doc.createElement('style');
        styleEl.id = 'ir-theme-injected-overrides';
        doc.head?.appendChild(styleEl);
      }
      styleEl.textContent = `
        html, body {
          background-color: ${bgPrimary} !important;
          color: ${textPrimary} !important;
          font-family: ${readerFont} !important;
          font-size: ${fontSize}px !important;
          line-height: ${lineHeight} !important;
        }
        p, div, span, a, h1, h2, h3, h4, h5, h6 {
          color: inherit;
        }
        .ir-tok {
          color: ${textPrimary} !important;
        }
        rt {
          color: ${rubyColor} !important;
        }
      `;

      if (doc.body) {
        doc.body.style.backgroundColor = bgPrimary;
        doc.body.style.color = textPrimary;
      }
    } catch {
      // Cross-origin guard
    }
  }, [config, fontSizePx]);

  useEffect(() => {
    const handleMessage = (e: MessageEvent) => {
      if (!e.data || typeof e.data !== 'object') return;

      if (e.data.type === 'ir-token-click') {
        const { tokenId, clientRect } = e.data;
        const iframeRect = iframeRef.current?.getBoundingClientRect();
        if (iframeRect && clientRect) {
          const absoluteRect = {
            left: iframeRect.left + clientRect.left,
            top: iframeRect.top + clientRect.top,
            bottom: iframeRect.top + clientRect.bottom,
            right: iframeRect.left + clientRect.right,
            width: clientRect.width,
            height: clientRect.height,
          };
          handleTokenClick(tokenId, absoluteRect);
        } else {
          handleTokenClick(tokenId, clientRect || { left: 100, top: 100 });
        }
      } else if (e.data.type === 'ir-clear-lookup') {
        closePopover();
      } else if (e.data.type === 'ir-navigate-href') {
        const { spineHref } = e.data;
        if (spineHref) {
          loadChapter(spineHref);
        }
      } else if (e.data.type === 'ir-search-results') {
        const { count, current } = e.data;
        useReaderStore.getState().setSearchResults(count || 0, current || 0);
      } else if (e.data.type === 'ir-open-search') {
        useReaderStore.getState().openSearch();
      }
    };

    window.addEventListener('message', handleMessage);
    return () => window.removeEventListener('message', handleMessage);
  }, [handleTokenClick, closePopover, loadChapter]);

  // Sync mode changes & theme to iframe
  useEffect(() => {
    applyReaderThemeToIframe();
    const cw = iframeRef.current?.contentWindow;
    if (cw) {
      cw.postMessage({ type: 'ir-set-furigana-mode', mode: furiganaMode }, '*');
      cw.postMessage({ type: 'ir-set-writing-mode', mode: writingMode }, '*');
    }
  }, [furiganaMode, writingMode, currentSpineHref, chapterHtml, applyReaderThemeToIframe]);

  if (!bookDetail || !currentSpineHref) {
    return (
      <div className="flex-1 flex items-center justify-center bg-[var(--bg-primary)] text-[var(--text-muted)]">
        Select a chapter to begin reading
      </div>
    );
  }

  const fallbackSrc = !chapterHtml && currentSpineHref
    ? `ir-book://${bookDetail.summary.bookId}/${currentSpineHref}`
    : undefined;

  return (
    <div className="flex-1 relative w-full h-full overflow-hidden bg-[var(--bg-primary)]">
      <iframe
        key={`${bookDetail.summary.bookId}-${currentSpineHref}`}
        ref={iframeRef}
        id="reader-frame"
        srcDoc={chapterHtml || undefined}
        src={fallbackSrc}
        className="w-full h-full border-0 select-text"
        title="EPUB Chapter View"
        onLoad={() => {
          applyReaderThemeToIframe();
          const cw = iframeRef.current?.contentWindow;
          if (cw) {
            cw.postMessage({ type: 'ir-set-furigana-mode', mode: furiganaMode }, '*');
            cw.postMessage({ type: 'ir-set-writing-mode', mode: writingMode }, '*');
            if (writingMode === 'vertical-rl') {
              cw.scrollTo({ left: 10000000, top: 0, behavior: 'instant' as ScrollBehavior });
              try {
                const doc = iframeRef.current?.contentDocument;
                if (doc?.documentElement) {
                  doc.documentElement.scrollLeft = doc.documentElement.scrollWidth;
                }
              } catch {
                // Guard
              }
            }
          }
        }}
      />
    </div>
  );
};
