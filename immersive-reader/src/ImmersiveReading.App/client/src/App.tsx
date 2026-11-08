import React, { useEffect } from 'react';
import { Header } from './components/layout/Header';
import { Sidebar } from './components/layout/Sidebar';
import { LibraryGrid } from './components/library/LibraryGrid';
import { ReaderView } from './components/reader/ReaderView';
import { BookInfoModal } from './components/library/BookInfoModal';
import { AiCompanionDrawer } from './components/companion/AiCompanionDrawer';
import { StudyPackDrawer } from './components/studypack/StudyPackDrawer';
import { SettingsModal } from './components/settings/SettingsModal';
import { useLibraryStore } from './stores/useLibraryStore';
import { useConfigStore } from './stores/useConfigStore';
import { useReaderStore } from './stores/useReaderStore';
import { rpc } from './services/rpc';
import { runReactReaderSelfTest } from './services/selfTestRunner';

export const App: React.FC = () => {
  const loadBootstrap = useLibraryStore((s) => s.loadBootstrap);
  const loadConfig = useConfigStore((s) => s.loadConfig);
  const isReaderActive = useReaderStore((s) => s.isActive);

  useEffect(() => {
    loadBootstrap();
    loadConfig();

    // Listen for Photino self-test event
    const unsubscribeSelfTest = rpc.on('ui.runReaderSelfTest', () => {
      runReactReaderSelfTest();
    });

    return () => {
      unsubscribeSelfTest();
    };
  }, [loadBootstrap, loadConfig]);

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
      <AiCompanionDrawer />
      <StudyPackDrawer />
      <SettingsModal />
    </div>
  );
};

export default App;
