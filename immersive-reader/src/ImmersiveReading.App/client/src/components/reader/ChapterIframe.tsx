import React, { useEffect, useRef } from 'react';
import { useReaderStore } from '../../stores/useReaderStore';

export const ChapterIframe: React.FC = () => {
  const { bookDetail, currentSpineHref, chapterHtml, handleTokenClick, closePopover, loadChapter, furiganaMode, writingMode } =
    useReaderStore();
  const iframeRef = useRef<HTMLIFrameElement>(null);

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
      }
    };

    window.addEventListener('message', handleMessage);
    return () => window.removeEventListener('message', handleMessage);
  }, [handleTokenClick, closePopover, loadChapter]);

  // Sync mode changes to iframe
  useEffect(() => {
    const cw = iframeRef.current?.contentWindow;
    if (cw) {
      cw.postMessage({ type: 'ir-set-furigana-mode', mode: furiganaMode }, '*');
      cw.postMessage({ type: 'ir-set-writing-mode', mode: writingMode }, '*');
    }
  }, [furiganaMode, writingMode, currentSpineHref, chapterHtml]);

  if (!bookDetail || !currentSpineHref) {
    return (
      <div className="flex-1 flex items-center justify-center bg-[var(--bg-primary)] text-[var(--text-muted)]">
        Select a chapter to begin reading
      </div>
    );
  }

  return (
    <div className="flex-1 relative w-full h-full overflow-hidden bg-[var(--bg-primary)]">
      <iframe
        ref={iframeRef}
        id="reader-frame"
        srcDoc={chapterHtml}
        className="w-full h-full border-0 select-text"
        title="EPUB Chapter View"
        onLoad={() => {
          const cw = iframeRef.current?.contentWindow;
          if (cw) {
            cw.postMessage({ type: 'ir-set-furigana-mode', mode: furiganaMode }, '*');
            cw.postMessage({ type: 'ir-set-writing-mode', mode: writingMode }, '*');
          }
        }}
      />
    </div>
  );
};
