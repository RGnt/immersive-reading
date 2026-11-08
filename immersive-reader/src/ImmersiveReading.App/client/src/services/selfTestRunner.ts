import { rpc } from './rpc';
import { useReaderStore } from '../stores/useReaderStore';

export async function runReactReaderSelfTest(): Promise<void> {
  try {
    // 1. Search for book with Japanese text chapters (e.g. クーク)
    const searchRes = await rpc.call<any>('library.listBooks', { search: 'クーク', limit: 5 });
    const candidates = searchRes?.items || searchRes?.books || [];

    if (candidates.length === 0) {
      await rpc.call('ui.reportSelfTestResult', {
        passed: false,
        summary: 'FAIL: No books found in library during React self-test',
      });
      return;
    }

    const targetBook = candidates[0];
    const readerStore = useReaderStore.getState();

    // 2. Open book in Reader store
    await readerStore.openBook(targetBook.bookId);

    async function waitForTokens(timeoutMs = 3000): Promise<number> {
      const start = Date.now();
      while (Date.now() - start < timeoutMs) {
        const frame = document.getElementById('reader-frame') as HTMLIFrameElement | null;
        const doc = frame?.contentDocument;
        const count = doc ? doc.querySelectorAll('.ir-tok').length : 0;
        if (count > 0) return count;
        await new Promise((r) => setTimeout(r, 100));
      }
      return 0;
    }

    let tokCount = await waitForTokens(2000);

    // Advance through chapters if cover image had 0 tokens
    const chapters = useReaderStore.getState().chapters;
    for (let i = 0; i < chapters.length && tokCount === 0; i++) {
      if (!chapters[i].isExcluded && i !== useReaderStore.getState().currentChapterIndex) {
        await readerStore.loadChapter(chapters[i].spineHref, i);
        tokCount = await waitForTokens(2000);
      }
    }

    if (tokCount === 0) {
      await rpc.call('ui.reportSelfTestResult', {
        passed: false,
        summary: `FAIL: 0 .ir-tok elements rendered inside iframe for book ${targetBook.bookId}`,
      });
      return;
    }

    // 3. Trigger token click lookup for token 0
    await readerStore.handleTokenClick(0, { left: 120, top: 120, width: 40, height: 25 });
    await new Promise((r) => setTimeout(r, 200));

    const popover = document.getElementById('dict-popover');
    const popVisible = !!popover;
    const headword = document.querySelector('.dict-headword')?.textContent || '';

    // 4. Verify Phase 4 Study Pack overview & 1-click flashcard mining from popover
    const spRes = await rpc.call<any>('studypack.getOverview', { bookId: 'book-4e0541b184c4ace9' });
    const vocabSetsCount = spRes?.overview?.vocabularySets?.length || spRes?.vocabularySets?.length || 0;
    await readerStore.mineFlashcard(0);

    // 5. Verify Phase 5 AI Comprehension Quiz
    const currentChapter = useReaderStore.getState();
    const quizQ = await rpc.call<any>('ext.aiCompanion.generateQuestion', {
      bookId: targetBook.bookId,
      chapterIndex: currentChapter.currentChapterIndex,
      spineHref: currentChapter.currentSpineHref,
      viewportTokenId: 15,
    });

    const quizGrade = await rpc.call<any>('ext.aiCompanion.submitAnswer', {
      questionId: quizQ.questionId,
      selectedOptionIndex: 0,
      userAnswer: '0',
    });

    const quizScore = quizGrade ? quizGrade.comprehensionScore : -1;
    const quizOk = quizQ.questionMode === 'mcq' ? quizScore === 100 : quizScore > 0 && quizScore <= 100;

    const passed = popVisible && headword.length > 0 && vocabSetsCount > 0 && quizOk;

    await rpc.call('ui.reportSelfTestResult', {
      passed,
      summary: `UI_WINDOW_READER_OK: book="${targetBook.title}" (${targetBook.bookId}), chapter="${currentChapter.currentSpineHref}", domTokens=${tokCount}, popoverVisible=${popVisible}, headword="${headword}", vocabSets=${vocabSetsCount}, aiQuizScore=${quizScore} (${quizQ.questionMode})`,
    });
  } catch (err: any) {
    await rpc.call('ui.reportSelfTestResult', {
      passed: false,
      summary: `FAIL: ${err.message}`,
    });
  }
}
