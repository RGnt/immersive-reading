import React from 'react';
import { ReaderTopBar } from './ReaderTopBar';
import { ChapterIframe } from './ChapterIframe';
import { DictionaryPopover } from './DictionaryPopover';

export const ReaderView: React.FC = () => {
  return (
    <div className="reader-view flex-1 flex flex-col h-full overflow-hidden bg-[var(--bg-primary)]">
      <ReaderTopBar />
      <div className="flex-1 relative w-full h-full overflow-hidden">
        <ChapterIframe />
        <DictionaryPopover />
      </div>
    </div>
  );
};

