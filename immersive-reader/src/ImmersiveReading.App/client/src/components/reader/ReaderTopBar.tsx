import React from 'react';
import { useReaderStore } from '../../stores/useReaderStore';
import { useStudyPackStore } from '../../stores/useStudyPackStore';
import { ReaderToolbarPluginSlot } from '../plugins/PluginSlots';
import {
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  Sparkles,
  BookOpen,
  AlignVerticalSpaceAround,
  AlignHorizontalSpaceAround,
  Search,
  Bookmark,
  HelpCircle,
} from 'lucide-react';

export const ReaderTopBar: React.FC = () => {
  const {
    bookDetail,
    chapters,
    currentChapterIndex,
    loadChapter,
    closeReader,
    nextChapter,
    prevChapter,
    furiganaMode,
    setFuriganaMode,
    writingMode,
    toggleWritingMode,
    adjustFontSize,
    fontSizePx,
    openSearch,
    toggleBookmarks,
    bookmarks,
    toggleShortcuts,
    isChapterStudyOpen,
    toggleChapterStudy,
  } = useReaderStore();

  const openStudyPack = useStudyPackStore((s) => s.openDrawer);

  if (!bookDetail) return null;

  return (
    <div className="reader-topbar flex items-center justify-between px-3 py-1.5 border-b border-[var(--border-color)] bg-[var(--bg-surface)] select-none text-xs">
      <div className="flex items-center space-x-2 min-w-0">
        <button
          onClick={closeReader}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"
          title="Back to Library"
        >
          <ArrowLeft className="w-4 h-4" />
        </button>

        <span className="font-semibold text-[var(--text-primary)] truncate max-w-xs" title={bookDetail.summary.title}>
          {bookDetail.summary.title}
        </span>
      </div>

      <div className="flex items-center space-x-1.5">
        <button
          onClick={prevChapter}
          disabled={currentChapterIndex <= 0}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] disabled:opacity-30 disabled:cursor-not-allowed"
          title="Previous Chapter"
        >
          <ChevronLeft className="w-4 h-4" />
        </button>

        <select
          value={currentChapterIndex}
          onChange={(e) => {
            const idx = Number(e.target.value);
            if (chapters[idx]) {
              loadChapter(chapters[idx].spineHref, idx);
            }
          }}
          className="bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-primary)] rounded px-2 py-1 text-xs focus:outline-none focus:border-[var(--accent-color)] max-w-[200px] truncate"
        >
          {chapters.map((ch, idx) => (
            <option key={ch.spineHref || idx} value={idx}>
              {ch.title || `Chapter ${idx + 1}`}
            </option>
          ))}
        </select>

        <button
          onClick={nextChapter}
          disabled={currentChapterIndex >= chapters.length - 1}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] disabled:opacity-30 disabled:cursor-not-allowed"
          title="Next Chapter"
        >
          <ChevronRight className="w-4 h-4" />
        </button>
      </div>

      <div className="flex items-center space-x-2">
        <div className="flex items-center space-x-1 bg-[var(--bg-primary)] border border-[var(--border-color)] rounded p-0.5">
          <button
            onClick={() => adjustFontSize(-2)}
            className="px-1.5 py-0.5 text-[11px] font-bold text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            title="Decrease font size"
          >
            A-
          </button>
          <span className="text-[10px] text-[var(--text-muted)] px-1">{fontSizePx}px</span>
          <button
            onClick={() => adjustFontSize(2)}
            className="px-1.5 py-0.5 text-[11px] font-bold text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            title="Increase font size"
          >
            A+
          </button>
        </div>

        <select
          value={furiganaMode}
          onChange={(e) => setFuriganaMode(e.target.value as any)}
          className="bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-primary)] rounded px-2 py-1 text-xs focus:outline-none"
          title="Furigana Display Mode"
        >
          <option value="AllKanji">All Furigana</option>
          <option value="Uncommon">Uncommon Only</option>
          <option value="AuthorOnly">Author Only</option>
          <option value="Hover">Hover Only</option>
          <option value="None">Hide Furigana</option>
        </select>

        <button
          onClick={toggleWritingMode}
          className="p-1.5 rounded border border-[var(--border-color)] bg-[var(--bg-primary)] text-[var(--text-muted)] hover:text-[var(--text-primary)]"
          title={`Switch Writing Mode (current: ${writingMode})`}
        >
          {writingMode === 'vertical-rl' ? (
            <AlignVerticalSpaceAround className="w-3.5 h-3.5" />
          ) : (
            <AlignHorizontalSpaceAround className="w-3.5 h-3.5" />
          )}
        </button>

        {/* IN-READER SEARCH BUTTON */}
        <button
          onClick={openSearch}
          className="flex items-center space-x-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:border-[var(--accent-color)]"
          title="Find in chapter (Ctrl+F)"
        >
          <Search className="w-3.5 h-3.5" />
          <span className="hidden sm:inline">Find</span>
        </button>

        {/* BOOKMARKS BUTTON */}
        <button
          onClick={toggleBookmarks}
          className="flex items-center space-x-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:border-[var(--accent-color)] relative"
          title="Bookmarks & Saved Positions"
        >
          <Bookmark className="w-3.5 h-3.5 text-amber-400" />
          <span className="hidden sm:inline">Marks</span>
          {bookmarks.length > 0 && (
            <span className="text-[9px] px-1 rounded-full bg-amber-500/20 text-amber-300 font-bold ml-0.5">
              {bookmarks.length}
            </span>
          )}
        </button>

        <button
          onClick={toggleChapterStudy}
          className={`flex items-center space-x-1 px-2 py-1 rounded border text-xs transition-colors ${
            isChapterStudyOpen
              ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/50 shadow-sm'
              : 'bg-[var(--bg-primary)] border-[var(--border-color)] text-emerald-400 hover:border-emerald-400'
          }`}
          title="Toggle Chapter Vocabulary, Kanji & Grammar Panel"
        >
          <BookOpen className="w-3.5 h-3.5" />
          <span>Chapter Study</span>
        </button>

        <button
          onClick={() => openStudyPack(bookDetail.summary.bookId)}
          className="flex items-center space-x-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-emerald-400 hover:border-emerald-400"
          title="Study Pack Drawer"
        >
          <Sparkles className="w-3.5 h-3.5" />
          <span>Full Pack</span>
        </button>

        <ReaderToolbarPluginSlot />

        {/* SHORTCUTS HELP BUTTON */}
        <button
          onClick={() => toggleShortcuts()}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] border border-transparent hover:border-[var(--border-color)]"
          title="Keyboard Shortcuts & Vim Motions (?)"
        >
          <HelpCircle className="w-4 h-4" />
        </button>
      </div>
    </div>
  );
};
