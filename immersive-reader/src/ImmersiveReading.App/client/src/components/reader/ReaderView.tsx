import React from 'react';
import { ReaderTopBar } from './ReaderTopBar';
import { ChapterIframe } from './ChapterIframe';
import { DictionaryPopover } from './DictionaryPopover';
import { ReaderSearchBar } from './ReaderSearchBar';
import { BookmarksDrawer } from './BookmarksDrawer';
import { ShortcutsModal } from './ShortcutsModal';
import { ReaderToast } from './ReaderToast';
import { ChapterStudyPanel } from './ChapterStudyPanel';
import { ReaderDrawerPluginSlot } from '../plugins/PluginSlots';

export const ReaderView: React.FC = () => {
  return (
    <div className="reader-view flex-1 flex flex-col h-full overflow-hidden bg-[var(--bg-primary)] relative">
      <ReaderTopBar />
      <div className="flex-1 relative w-full h-full overflow-hidden flex flex-row">
        <div className="flex-1 relative h-full overflow-hidden">
          <ChapterIframe />
          <ReaderSearchBar />
          <DictionaryPopover />
          <BookmarksDrawer />
          <ShortcutsModal />
          <ReaderToast />
        </div>
        <ChapterStudyPanel />
        <ReaderDrawerPluginSlot />
      </div>
    </div>
  );
};

