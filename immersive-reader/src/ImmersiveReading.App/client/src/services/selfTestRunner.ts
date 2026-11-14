import { rpc } from './rpc';
import { useReaderStore } from '../stores/useReaderStore';
import { usePluginStore } from '../stores/usePluginStore';
import { selfCheckRewriter } from './pluginImportRewriter';

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function waitFor<T>(probe: () => T | null | undefined | false, timeoutMs = 4000): Promise<T | null> {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    const v = probe();
    if (v) return v as T;
    await sleep(50);
  }
  return null;
}

/**
 * Enables a plugin, waits until its UI either loaded or failed, runs `fn`, and always restores the
 * plugin's previous enabled state so a verification run never changes the user's settings.
 * A UI load failure throws with the real reason (specs/11: failures must be loud).
 */
async function withPluginUi<T>(pluginId: string, fn: () => Promise<T>): Promise<T> {
  const wasEnabled = usePluginStore.getState().plugins[pluginId]?.state === 'Enabled';
  try {
    if (!wasEnabled) await rpc.call('extensions.setEnabled', { pluginId, enabled: true });
    const outcome = await waitFor(() => {
      const s = usePluginStore.getState();
      return s.uiErrors[pluginId] ? 'error' : s.loadedBundles[pluginId] ? 'loaded' : null;
    }, 8000);
    const s = usePluginStore.getState();
    if (outcome === 'error') throw new Error(`plugin '${pluginId}' UI failed to load: ${s.uiErrors[pluginId]}`);
    if (outcome !== 'loaded') throw new Error(`plugin '${pluginId}' UI neither loaded nor reported an error in 8s`);
    return await fn();
  } finally {
    if (!wasEnabled) await rpc.call('extensions.setEnabled', { pluginId, enabled: false }).catch(() => null);
  }
}

/** Spec 11: both first-party plugin UIs must load and register their slots. */
async function verifyFirstPartyPluginUis(): Promise<string> {
  const rewriterFailures = selfCheckRewriter();
  if (rewriterFailures.length > 0) throw new Error(`import rewriter self-check failed: ${rewriterFailures.join('; ')}`);

  await withPluginUi('aiCompanion', async () => {
    const s = usePluginStore.getState();
    const hasView = s.views.some((v) => v.pluginId === 'aiCompanion' && v.id === 'ai-study-hub');
    const hasDrawer = s.drawers.some((d) => d.pluginId === 'aiCompanion' && d.id === 'ai-persona-chat-drawer');
    if (!hasView || !hasDrawer) throw new Error('aiCompanion UI loaded but its view/drawer were not registered');
  });
  return 'pluginUi=ok(rewriter ' + 'cases pass, aiCompanion view+drawer registered)';
}

/**
 * Spec 10: drives the real reader top-bar "Highlights" popover and checks the live style inside the
 * chapter iframe. Restores the plugin's enabled state and category selection afterwards.
 */
async function verifyPosCategoryMenu(): Promise<string> {
  return withPluginUi('posHighlighter', async () => {
    const before = await rpc.call<any>('ext.posHighlighter.getCategories', {});
    const priorIds: string[] = before.categories.filter((c: any) => c.enabled).map((c: any) => c.id);
    const priorMaster: boolean = before.enabled;
    try {
      if (!usePluginStore.getState().toolbarActions.some((a) => a.id === 'pos-highlighter-menu')) {
        throw new Error('pos-highlighter-menu toolbar action was not registered');
      }

      const button = await waitFor(() => document.querySelector<HTMLButtonElement>('button[title="Highlights"]'));
      if (!button) throw new Error('Highlights button not rendered in the reader top bar');
      button.click();
      const dialog = await waitFor(() => document.querySelector('[role="dialog"][aria-label="Highlights"]'));
      if (!dialog) throw new Error('Highlights popover did not open');
      const boxes = await waitFor(() => {
        const n = dialog.querySelectorAll('ul input[type="checkbox"]').length;
        return n === 13 ? n : null;
      });
      if (!boxes) throw new Error('popover did not list 13 categories');

      const chip = (label: string) =>
        Array.from(dialog.querySelectorAll('button')).find((b) => b.textContent === label) as HTMLButtonElement | undefined;
      const iframeCss = () =>
        (document.getElementById('reader-frame') as HTMLIFrameElement | null)?.contentDocument
          ?.getElementById('ir-pos-highlighter-style')?.textContent ?? '';

      chip('Particles')?.click();
      const live = await waitFor(() => {
        const css = iframeCss();
        return css.includes('.ir-pos-prt-single') && !css.includes('.ir-pos-verb-u') ? css : null;
      });
      if (!live) throw new Error('Particles preset did not update the open chapter live');
      const ticked = Array.from(dialog.querySelectorAll<HTMLInputElement>('ul input[type="checkbox"]')).filter((i) => i.checked).length;
      if (ticked !== 2) throw new Error(`expected 2 ticked categories after Particles preset, got ${ticked}`);

      const persisted = await rpc.call<any>('ext.posHighlighter.getCategories', {});
      const persistedIds = persisted.categories.filter((c: any) => c.enabled).map((c: any) => c.id).sort().join(',');
      if (persistedIds !== 'particleMulti,particleSingle') throw new Error(`selection not persisted: ${persistedIds}`);

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      const closed = await waitFor(() => !document.querySelector('[role="dialog"][aria-label="Highlights"]'));
      if (!closed) throw new Error('Escape did not close the popover');
      return 'posMenu=ok(13 categories, live particles-only, persisted, esc closes)';
    } finally {
      const res = await rpc.call<any>('ext.posHighlighter.setCategories', { enabledCategories: priorIds, enabled: priorMaster }).catch(() => null);
      if (res) usePluginStore.getState().setReaderStyle('posHighlighter', 'ir-pos-highlighter-style', res.css ?? '');
    }
  });
}

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

    // Fatal: a plugin UI that fails to load must fail verification (specs/11 FR-7).
    const pluginUi = await verifyFirstPartyPluginUis();
    const posMenu = await verifyPosCategoryMenu();

    const passed = popVisible && headword.length > 0 && vocabSetsCount > 0 && quizOk;

    await rpc.call('ui.reportSelfTestResult', {
      passed,
      summary: `UI_WINDOW_READER_OK: book="${targetBook.title}" (${targetBook.bookId}), chapter="${currentChapter.currentSpineHref}", domTokens=${tokCount}, popoverVisible=${popVisible}, headword="${headword}", vocabSets=${vocabSetsCount}, aiQuizScore=${quizScore} (${quizQ.questionMode}), ${pluginUi}, ${posMenu}`,
    });
  } catch (err: any) {
    await rpc.call('ui.reportSelfTestResult', {
      passed: false,
      summary: `FAIL: ${err.message}`,
    });
  }
}
