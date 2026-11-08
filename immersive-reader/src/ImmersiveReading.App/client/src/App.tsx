import React, { useEffect } from 'react';
import { Header } from './components/layout/Header';
import { Sidebar } from './components/layout/Sidebar';
import { LibraryGrid } from './components/library/LibraryGrid';
import { ReaderView } from './components/reader/ReaderView';
import { BookInfoModal } from './components/library/BookInfoModal';
import { ScanFolderModal } from './components/library/ScanFolderModal';
import { SeriesJitenModal } from './components/library/SeriesJitenModal';
import { AiCompanionDrawer } from './components/companion/AiCompanionDrawer';
import { StudyPackDrawer } from './components/studypack/StudyPackDrawer';
import { SettingsModal } from './components/settings/SettingsModal';
import { PluginModalHost } from './components/plugins/PluginSlots';
import { useLibraryStore } from './stores/useLibraryStore';
import { useConfigStore } from './stores/useConfigStore';
import { useReaderStore } from './stores/useReaderStore';
import { usePluginStore } from './stores/usePluginStore';
import { rpc } from './services/rpc';
import { runReactReaderSelfTest } from './services/selfTestRunner';
import { useKeyboardShortcuts } from './hooks/useKeyboardShortcuts';

export const App: React.FC = () => {
  useKeyboardShortcuts();
  const loadBootstrap = useLibraryStore((s) => s.loadBootstrap);
  const loadConfig = useConfigStore((s) => s.loadConfig);
  const initializePlugins = usePluginStore((s) => s.initializePlugins);
  const isReaderActive = useReaderStore((s) => s.isActive);

  useEffect(() => {
    loadBootstrap();
    loadConfig();
    initializePlugins();

    // Listen for Photino self-test event
    const unsubscribeSelfTest = rpc.on('ui.runReaderSelfTest', () => {
      runReactReaderSelfTest();
    });

    return () => {
      unsubscribeSelfTest();
    };
  }, [loadBootstrap, loadConfig, initializePlugins]);

  return (
    <div className="app-shell flex flex-col h-screen w-screen overflow-hidden bg-[var(--bg-primary)] text-[var(--text-primary)]">
      <Header />
      <div className="flex-1 flex overflow-hidden relative">
        {isReaderActive ? (
          <ReaderView />
        ) : (
          <>
            <Sidebar />
            <LibraryGrid />
          </>
        )}
      </div>

      <BookInfoModal />
      <ScanFolderModal />
      <SeriesJitenModal />
      <AiCompanionDrawer />
      <StudyPackDrawer />
      <SettingsModal />
      <PluginModalHost />
    </div>
  );
};

export default App;
