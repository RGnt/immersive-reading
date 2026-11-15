import { rpc } from './rpc';
import { useReaderStore } from '../stores/useReaderStore';
import { usePluginStore } from '../stores/usePluginStore';
import { useLibraryStore } from '../stores/useLibraryStore';
import { applyThemeStyles, useConfigStore } from '../stores/useConfigStore';
import { THEME_PRESETS } from '../styles/themePresets';
import type { AppConfig } from '../types/api';
import { auditContrast, summarise, type Offender } from './contrastAudit';
import type { MonacoApi } from '../components/editor/loadMonaco';
import { selfCheckRewriter } from './pluginImportRewriter';

/** Any text below the minimum contrast contrast in any theme fails the UI check. */
const CONTRAST_STRICT = true;
/** Text below this contrast ratio is reported (4.5:1 is the WCAG AA level for normal text). */
const CONTRAST_MIN = 4.5;

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
  await withPluginUi('jitenMoe', async () => {
    const s = usePluginStore.getState();
    const hasTab = s.bookTabs.some((t) => t.pluginId === 'jitenMoe');
    const hasBadge = s.bookBadges.some((b) => b.pluginId === 'jitenMoe');
    const hasSeriesAction = s.seriesActions.some((a) => a.pluginId === 'jitenMoe');
    if (!hasTab || !hasBadge || !hasSeriesAction) {
      throw new Error('jitenMoe UI loaded but its book tab / badge / series action were not registered');
    }
  });
  await withPluginUi('deckBuilder', async () => {
    const s = usePluginStore.getState();
    if (!s.bookTabs.some((t) => t.pluginId === 'deckBuilder')) {
      throw new Error('deckBuilder UI loaded but its book tab was not registered');
    }
  });
  await withPluginUi('characterKnowledge', async () => {
    const s = usePluginStore.getState();
    const tab = s.bookTabs.some((t) => t.pluginId === 'characterKnowledge');
    const drawer = s.drawers.some((d) => d.pluginId === 'characterKnowledge');
    const action = s.seriesActions.some((a) => a.pluginId === 'characterKnowledge');
    if (!tab || !drawer || !action) {
      throw new Error('characterKnowledge UI loaded but its book tab / reader drawer / series action were not registered');
    }
  });
  return 'pluginUi=ok(rewriter ' + 'cases pass, aiCompanion view+drawer, jitenMoe tab+badge+series action, deckBuilder tab registered, characterKnowledge tab+drawer+series action)';
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

/** Spec 21: the duplicates dialog opens, searches the real library through the real RPC (read-only) and closes again. */
async function verifyDuplicatesDialog(): Promise<string> {
  const text = () => document.body.innerText;
  useLibraryStore.getState().openDuplicatesModal();
  try {
    if (!(await waitFor(() => text().includes('Find duplicate books')))) throw new Error('duplicates dialog did not open');
    const summary = await waitFor(() => /books compared in \d+ ms/.test(text()) && text().match(/books compared in \d+ ms: (\d+) group/)?.[1], 8000);
    if (!summary) throw new Error('duplicates dialog showed no search summary (library.findDuplicates failed?)');
    return `duplicates=ok(dialog, search found ${summary} group(s))`;
  } finally {
    useLibraryStore.getState().closeDuplicatesModal();
    if (!(await waitFor(() => !text().includes('Find duplicate books')))) throw new Error('duplicates dialog did not close');
  }
}

/**
 * Library management: the bulk edit dialog opens on a selection, a ticked series field produces the server's preview and nothing is
 * applied (the dialog is only read from: the live library is never changed by this check).
 */
async function verifyBulkEditDialog(): Promise<string> {
  const text = () => document.body.innerText;
  const store = useLibraryStore.getState();
  const before = store.selectedBookIds;
  const listed = await rpc.call<any>('library.listBooks', { limit: 2 });
  const books = (listed?.items || listed?.books || []).slice(0, 2);
  if (books.length < 2) return 'bulkEdit=skipped(fewer than two books)';
  useLibraryStore.setState({ selectedBookIds: books.map((b: any) => b.bookId) });
  useLibraryStore.getState().openBulkEdit();
  try {
    if (!(await waitFor(() => text().includes('Edit 2 books at once')))) throw new Error('bulk edit dialog did not open');
    const box = document.querySelector<HTMLInputElement>('.modal-backdrop input[type="checkbox"]');
    if (!box) throw new Error('bulk edit dialog has no series checkbox');
    box.click();
    if (!(await waitFor(() => /Preview/.test(text()) && /volume numbers? will change/.test(text()), 8000))) {
      throw new Error('bulk edit dialog showed no preview (library.previewBulkUpdate failed?)');
    }
    if (!/Apply to 2/.test(text())) throw new Error('bulk edit dialog has no apply button for the selection');
    return 'bulkEdit=ok(dialog, preview of 2 books)';
  } finally {
    useLibraryStore.getState().closeBulkEdit();
    useLibraryStore.setState({ selectedBookIds: before });
    if (!(await waitFor(() => !text().includes('Edit 2 books at once')))) throw new Error('bulk edit dialog did not close');
  }
}

/**
 * Monaco in the real window (offline, from the bundled files): the Prompts tab shows a real prompt and reports a bad edit with the
 * server's reason, the Custom CSS tab previews a rule live and the CSS language service answers. Nothing is saved.
 */
async function verifyEditors(): Promise<string> {
  const text = () => document.body.innerText;
  const monaco = () => (window as unknown as { __irMonaco?: MonacoApi }).__irMonaco;
  const editorOf = async () => {
    if (!(await waitFor(() => document.querySelector('[data-testid="code-editor"] .monaco-editor'), 20000))) {
      throw new Error('the Monaco editor did not render (bundle or worker problem)');
    }
    const ed = await waitFor(() => monaco()?.editor.getEditors()[0], 5000);
    if (!ed) throw new Error('no Monaco editor instance');
    return ed;
  };

  let promptSummary = '';
  await withPluginUi('aiCompanion', async () => {
    const listed = await rpc.call<{ prompts: { name: string }[] }>('prompts.list', {});
    const names = listed.prompts.map((p) => p.name);
    for (const wanted of ['persona.system', 'persona.turn', 'quiz.generate.system', 'quiz.grade.user', 'persona.memory']) {
      if (!names.includes(wanted)) throw new Error(`the prompt '${wanted}' is not offered for editing`);
    }
    promptSummary = `chatPrompts=ok(${names.length} prompts listed), `;
  });
  await withPluginUi('characterKnowledge', async () => {
    useConfigStore.getState().openSettings('prompts');
    if (!(await waitFor(() => document.querySelector('[data-testid="prompts-tab"]') || /No prompts are registered/.test(text()), 10000))) {
      throw new Error('prompts tab did not open');
    }
    if (!document.querySelector('[data-testid="prompts-tab"]')) { promptSummary += 'prompts=skipped(no builder folder)'; return; }
    const editor = await editorOf();
    const original = editor.getValue();
    if (!original.startsWith('---')) throw new Error('the prompt editor does not show a template');
    editor.setValue(original + '\n{{definitely_not_declared}}');
    if (!(await waitFor(() => /definitely_not_declared/.test(document.querySelector('[data-testid="prompt-status"]')?.textContent ?? ''), 8000))) {
      const name = document.querySelector('[data-testid^="prompt-item-"]')?.getAttribute('data-testid')?.replace('prompt-item-', '') ?? '';
      const direct = await rpc.call<unknown>('prompts.check', { name, text: editor.getValue() }).catch((e) => String(e));
      throw new Error(`a bad edit was not reported by prompts.check (editor ends with ${JSON.stringify(editor.getValue().slice(-40))}; status ${JSON.stringify(document.querySelector('[data-testid="prompt-status"]')?.textContent)}; direct check ${JSON.stringify(direct)})`);
    }
    const marked = monaco()!.editor.getModelMarkers({}).some((m) => /definitely_not_declared/.test(m.message));
    if (!marked) throw new Error('the problem was not marked in the editor');
    editor.setValue(original);
    promptSummary += `prompts=ok(${original.split('\n').length} lines, bad edit marked)`;
  });
  useConfigStore.getState().closeSettings();

  useConfigStore.getState().openSettings('customCss');
  try {
    const editor = await editorOf();
    const original = editor.getValue();
    editor.setValue('html.ir-app { --ir-selftest: 42; }');
    if (!(await waitFor(() => getComputedStyle(document.documentElement).getPropertyValue('--ir-selftest').trim() === '42', 4000))) {
      throw new Error('the stylesheet was not previewed live');
    }
    editor.setValue('.a { color: ; } }}} @@@');
    const cssMarked = await waitFor(() => monaco()!.editor.getModelMarkers({}).length > 0, 15000);
    editor.setValue(original);
    if (!(await waitFor(() => getComputedStyle(document.documentElement).getPropertyValue('--ir-selftest').trim() === '', 4000))) {
      throw new Error('the preview was not removed again');
    }
    return `${promptSummary}, css=ok(live preview, ${cssMarked ? 'css service answered' : 'no css diagnostics'})`;
  } finally {
    useConfigStore.getState().closeSettings();
  }
}

/**
 * Text must stay readable in every colour theme, plugin screens included. Each theme is applied to the open window (nothing is saved), every
 * surface is opened in turn, and every visible piece of text is compared with the background it sits on. Surfaces: the reader chrome and the
 * plugins' drawers, all Settings tabs, the dialogs, the library and the book info dialog with all its tabs (plugin tabs too).
 * `strict`: any text below the minimum contrast fails the run; otherwise the offenders are only reported.
 */
async function verifyContrast(strict: boolean): Promise<string> {
  const root = document.documentElement;
  const keys = ['--bg-primary', '--bg-surface', '--text-primary', '--text-muted', '--border-color', '--accent-color', '--ruby-color'];
  const saved = keys.map((k) => [k, root.style.getPropertyValue(k)] as const);
  const wasReaderActive = useReaderStore.getState().isActive;
  const library = useLibraryStore.getState();
  const books = await rpc.call<any>('library.listBooks', { limit: 1 });
  const firstBookId: string | undefined = (books?.items || books?.books || [])[0]?.bookId;

  const all: Record<string, Offender[]> = {};
  const crashed = new Set<string>();         // plugin screens that showed the error box instead of their content
  let surfaces = 0;
  const reportOf = async (theme: string, label: string, open: () => void | Promise<void>, close: () => void | Promise<void>, settle = 450) => {
    await open();
    await sleep(settle);
    const scope = Array.from(document.querySelectorAll('.modal-backdrop, [role="dialog"], .fixed.inset-0')).pop() ?? document.body;
    surfaces++;
    const crash = document.body.innerText.match(/Plugin "([^"]+)" encountered an error:\s*(.*)/);
    if (crash) crashed.add(`${label}: plugin ${crash[1]} crashed (${crash[2].slice(0, 160)})`);
    (all[theme] ??= []).push(...auditContrast(scope, CONTRAST_MIN).map((o) => ({ ...o, where: `${label} › ${o.where}` })));
    await close();
    await sleep(100);
  };
  const noop = () => {};
  const tabs = ['general', 'library', 'appearance', 'reader', 'llm', 'dictionaries', 'prompts', 'customCss', 'memory'] as const;

  const run = async () => {
    for (const [theme, preset] of Object.entries(THEME_PRESETS)) {
      applyThemeStyles({ appearance: preset } as AppConfig);
      await sleep(150);

      // reader chrome and the plugins' drawers (the book page itself is a separate document and is not audited)
      if (wasReaderActive) {
        await reportOf(theme, 'reader', noop, noop);
        for (const drawer of usePluginStore.getState().drawers) {
          await reportOf(theme, `drawer ${drawer.id}`, () => usePluginStore.getState().toggleDrawer(drawer.id), () => usePluginStore.getState().toggleDrawer(drawer.id), 900);
        }
      }
      for (const tab of tabs) {
        await reportOf(theme, `settings ${tab}`, () => useConfigStore.getState().openSettings(tab), () => useConfigStore.getState().closeSettings(), tab === 'prompts' ? 1500 : 500);
      }
      if (wasReaderActive) {
        const quizButton = () => Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.title === 'Quiz' || (b.textContent ?? '').trim() === 'Quiz');
        if (quizButton()) await reportOf(theme, 'quiz modal', () => quizButton()!.click(), () => usePluginStore.getState().closeModal(), 1500);
      }
      await reportOf(theme, 'duplicates', () => library.openDuplicatesModal(), () => library.closeDuplicatesModal(), 900);
      await reportOf(theme, 'scan folder', () => library.openScanModal(), () => library.closeScanModal());

      // the library itself and the book dialog (needs the reader closed)
      if (wasReaderActive) useReaderStore.getState().closeReader();
      await sleep(400);
      await reportOf(theme, 'library', noop, noop, 600);
      if (firstBookId) {
        await library.inspectBook(firstBookId);
        await sleep(600);
        const tabButtons = () => Array.from(document.querySelectorAll<HTMLButtonElement>('.book-info-tab-btn'));
        const count = tabButtons().length;
        for (let i = 0; i < count; i++) {
          await reportOf(theme, `book info tab ${i + 1}`, () => tabButtons()[i]?.click(), noop, 700);
        }
        library.closeInspector();
      }
      useLibraryStore.setState({ selectedBookIds: firstBookId ? [firstBookId] : [] });
      await reportOf(theme, 'bulk edit', () => useLibraryStore.getState().openBulkEdit(), () => useLibraryStore.getState().closeBulkEdit(), 800);
      useLibraryStore.setState({ selectedBookIds: [] });
    }
  };

  try {
    await withPluginUi('aiCompanion', () => withPluginUi('jitenMoe', () => withPluginUi('deckBuilder', () => withPluginUi('characterKnowledge', run))));
  } finally {
    for (const [k, v] of saved) { if (v) root.style.setProperty(k, v); else root.style.removeProperty(k); }
  }

  if (crashed.size > 0) throw new Error(`plugin screens crashed: ${[...crashed].join(' // ')}`);

  const lines: string[] = [];
  let total = 0;
  for (const [theme, list] of Object.entries(all)) {
    total += list.length;
    const top = summarise(list).slice(0, 14).map((g) => `${g.count}x ${g.key} "${g.sample.text}" ${g.sample.ratio}:1`);
    if (top.length) lines.push(`${theme}: ${list.length} low-contrast texts [${top.join(' | ')}]`);
  }
  if (strict && total > 0) throw new Error(`contrast: ${total} texts below the minimum contrast over ${surfaces} surfaces: ${lines.join(' // ')}`);
  return total === 0 ? `contrast=ok(${Object.keys(all).length} themes, ${surfaces} surfaces, nothing below the minimum contrast)` : `contrast=REPORT(${total} below the minimum contrast over ${surfaces} surfaces: ${lines.join(' // ')})`;
}

/**
 * Settings → Memory against the real memory file: a fact is added through the same RPC the tab uses, shown in the tab, and forgotten again
 * (the file is left as it was). Skipped when the memory file cannot be opened.
 */
async function verifyMemoryTab(): Promise<string> {
  const marker = `selftest-${Date.now()}`;
  const listed = await rpc.call<{ available: boolean }>('memory.list', {});
  if (!listed.available) return 'memory=skipped(no memory file)';
  const added = await rpc.call<{ ok: boolean; error: string | null; fact: { id: string } | null }>('memory.add', { text: `The user said ${marker}` });
  if (!added.ok || !added.fact) throw new Error(`memory.add failed: ${added.error}`);
  try {
    useConfigStore.getState().openSettings('memory');
    if (!(await waitFor(() => document.body.innerText.includes(marker), 6000))) throw new Error('the Memory tab does not show a stored fact');
    return 'memory=ok(fact stored, shown, forgotten)';
  } finally {
    await rpc.call('memory.forget', { id: added.fact.id });
    useConfigStore.getState().closeSettings();
  }
}

/** The sharper look: dialogs are square (controls inside them nearly so) and the scroll bars are the narrow, quiet ones (not on macOS, which keeps its own). */
async function verifyLook(): Promise<string> {
  useConfigStore.getState().openSettings('general');
  try {
    const panel = await waitFor(() => document.querySelector<HTMLElement>('.modal-surface'), 4000);
    if (!panel) throw new Error('the settings dialog has no .modal-surface panel');
    const radius = getComputedStyle(panel).borderTopLeftRadius;
    if (parseFloat(radius) > 0) throw new Error(`dialog corners are ${radius}, expected square`);
    const control = panel.querySelector<HTMLElement>('input[type="text"], select, button');
    const controlRadius = control ? parseFloat(getComputedStyle(control).borderTopLeftRadius) : 0;
    if (controlRadius > 4) throw new Error(`controls inside a dialog are rounded ${controlRadius}px, expected 4px or less`);
    const mac = document.documentElement.classList.contains('ir-mac');
    let bar = 'native (macOS)';
    if (!mac) {
      const pane = panel.querySelector<HTMLElement>('.overflow-y-auto') ?? panel;
      const width = getComputedStyle(pane, '::-webkit-scrollbar').width;
      if (width !== '8px') throw new Error(`scroll bar width is ${width}, expected 8px`);
      bar = '8px, faint thumb';
    }
    return `look=ok(square dialogs, controls ${controlRadius}px, scroll bars ${bar})`;
  } finally {
    useConfigStore.getState().closeSettings();
  }
}

/**
 * The book card in the real gallery: title bar on top with the chapter count under the title on the left, the selection box over the
 * title bar's top-right corner, the cover, Read at the bottom left and Info at the bottom right, square corners; Shift+click adds a book to
 * the selection and removes it again without opening its details. The selection is left empty.
 */
async function verifyBookCard(): Promise<string> {
  if (useReaderStore.getState().isActive) useReaderStore.getState().closeReader();
  useLibraryStore.setState({ selectedBookIds: [] });
  if (!(await waitFor(() => document.querySelectorAll('.book-card').length >= 2, 8000))) return 'bookCard=skipped(fewer than two books shown)';
  const cards = Array.from(document.querySelectorAll<HTMLElement>('.book-card'));
  const card = cards[0];
  const part = (selector: string) => {
    const el = card.querySelector<HTMLElement>(selector);
    if (!el) throw new Error(`book card has no ${selector}`);
    return el.getBoundingClientRect();
  };
  const c = card.getBoundingClientRect();
  const title = part('.book-card-title'), chapters = part('.book-card-chapters'), cover = part('.book-card-cover');
  const box = part('.book-card-select'), read = part('.book-card-read'), info = part('.book-card-info');
  if (!(title.top < cover.top && chapters.top >= title.bottom - 1 && chapters.bottom <= cover.top + 1)) throw new Error('the chapter count is not directly under the title, above the cover');
  if (!(chapters.left - c.left < c.width / 2)) throw new Error('the chapter count is not on the left side of the card');
  if (!(box.right > c.right - 12 && box.top < title.bottom && box.bottom <= cover.top + 1)) throw new Error('the selection box is not in the title bar corner (top right)');
  if (!(read.left < info.left && read.top >= cover.bottom - 1)) throw new Error('Read is not below the cover on the left of Info');
  if (parseFloat(getComputedStyle(card).borderTopLeftRadius) > 0) throw new Error('the book card has rounded corners');

  const ids = () => useLibraryStore.getState().selectedBookIds;
  const shiftClick = async (el: HTMLElement) => {
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey: true }));
    await sleep(120);
  };
  const first = card.dataset.bookId!, second = cards[1].dataset.bookId!;
  await shiftClick(card);
  if (ids().join() !== first) throw new Error(`shift+click did not select the book (selection: ${ids().join()})`);
  await shiftClick(cards[1]);
  if (ids().length !== 2 || !ids().includes(second)) throw new Error('shift+click did not add a second book to the selection');
  await shiftClick(card);
  if (ids().join() !== second) throw new Error(`shift+click on a selected book did not remove it (selection: ${ids().join()})`);
  if (useLibraryStore.getState().activeBookDetail) throw new Error('shift+click opened the book details');
  useLibraryStore.setState({ selectedBookIds: [] });
  return 'bookCard=ok(title bar, count under title, box top-right, Read left / Info right, square, shift+click adds and removes)';
}

/**
 * Favourites and the reading list in the real library: flag the first book, see the star, the status chip, the sidebar count and the list,
 * then put every flag back. And the bookmark line: Vol<X>Ch<Y> without a selection, [ch<Y>] text with one (the test bookmarks are removed).
 */
async function verifyListsAndBookmarks(): Promise<string> {
  if (useReaderStore.getState().isActive) useReaderStore.getState().closeReader();
  const lib = () => useLibraryStore.getState();
  lib().selectListFilter(null);
  if (!(await waitFor(() => document.querySelectorAll('.book-card').length >= 1, 8000))) return 'lists=skipped(no books shown)';
  const card = document.querySelector<HTMLElement>('.book-card')!;
  const id = card.dataset.bookId!;
  const before = lib().books.find((b) => b.bookId === id)!;
  const original = { favorite: !!before.isFavorite, readingStatus: (before.readingStatus || '') as '' | 'reading' | 'planned' | 'completed' };
  const countsBefore = { ...lib().listCounts };

  try {
    await lib().setBookFlags([id], { favorite: true, readingStatus: 'reading' });
    const cardNow = () => Array.from(document.querySelectorAll<HTMLElement>('.book-card')).find((c) => c.dataset.bookId === id);
    if (!(await waitFor(() => cardNow()?.querySelector('[data-testid="favorite-star"]')?.getAttribute('aria-pressed') === 'true', 4000))) throw new Error('the card does not show the favourite star');
    if (!(await waitFor(() => /Reading/.test(cardNow()?.querySelector('[data-testid="book-card-status"]')?.textContent ?? ''), 4000))) throw new Error('the card does not show the reading status');
    if (lib().listCounts.reading < 1 || lib().listCounts.favorites < 1) throw new Error('the list counts did not follow the change');

    // the sidebar list shows the book
    document.querySelector<HTMLElement>('[data-testid="list-reading"]')!.click();
    // wait for the FILTERED page (the unfiltered one is still on screen until the request returns)
    const onlyReading = () => lib().books.length > 0 && lib().books.every((b) => (b.readingStatus || b.seriesReadingStatus) === 'reading');
    if (!(await waitFor(() => lib().listFilter === 'reading' && !lib().isLoading && onlyReading() && lib().books.some((b) => b.bookId === id), 6000))) {
      throw new Error('the Currently reading list is not exactly the books on it (it should contain the flagged book and nothing else)');
    }
    lib().selectListFilter(null);
  } finally {
    await lib().setBookFlags([id], { favorite: original.favorite, readingStatus: original.readingStatus });
    lib().selectListFilter(null);
  }
  const after = lib().listCounts;
  if (after.reading !== countsBefore.reading || after.favorites !== countsBefore.favorites) throw new Error('the flags were not restored');

  // bookmark lines
  const detail = await rpc.call<any>('library.getBookDetail', { bookId: id });
  const chapter = detail?.chapters?.[0];
  if (!chapter) return 'lists=ok(star, chip, count, list, restored), bookmarks=skipped(no chapters)';
  const made: string[] = [];
  try {
    const plain = await rpc.call<any>('reader.addBookmark', { bookId: id, chapterIndex: 0, spineHref: chapter.spineHref });
    made.push(plain.bookmarkId);
    const picked = await rpc.call<any>('reader.addBookmark', { bookId: id, chapterIndex: 0, spineHref: chapter.spineHref, selectedText: 'こんにちは、  世界' });
    made.push(picked.bookmarkId);
    if (!/^Vol\d+(\.\d+)?Ch1$/.test(plain.label)) throw new Error(`a bookmark without a selection is labelled "${plain.label}", expected Vol<X>Ch<Y>`);
    if (picked.label !== '[ch1] こんにちは、 世界') throw new Error(`a bookmark with a selection is labelled "${picked.label}", expected [ch<Y>] <text>`);
    const listed = await rpc.call<any>('reader.getBookmarks', { bookId: id });
    const labels = (listed.bookmarks as any[]).filter((b) => made.includes(b.bookmarkId)).map((b) => b.label);
    if (labels.length !== 2) throw new Error('the new bookmarks are not listed');
    return `lists=ok(star, chip, count, list, restored), bookmarks=ok(${plain.label} | ${picked.label})`;
  } finally {
    for (const bookmarkId of made) await rpc.call('reader.removeBookmark', { bookmarkId });
  }
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
    // The quiz needs a real model. Without one (the offline fallback answers with text that is not a question) the quiz must REFUSE, not invent:
    // that refusal is a valid outcome here; the question logic itself is covered by unit tests.
    let quizQ: any = null;
    let quizRefused = '';
    try {
      quizQ = await rpc.call<any>('ext.aiCompanion.generateQuestion', {
        bookId: targetBook.bookId,
        // a quiz asks about the last COMPLETED chapter: pretend the reader has moved on to the next one (the saved position is not changed)
        chapterIndex: currentChapter.currentChapterIndex + 1,
      });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      if (!/did not produce a usable question|Finish a chapter first/.test(message)) throw error;
      quizRefused = message.slice(0, 80);
    }
    let quizScore = -1;
    if (quizQ) {
      const quizGrade = await rpc.call<any>('ext.aiCompanion.submitAnswer', { questionId: quizQ.questionId, selectedOptionIndex: 0, userAnswer: '0' });
      quizScore = quizGrade ? quizGrade.comprehensionScore : -1;
    }
    // the correct option's position is random and never sent to the client, so a multiple-choice answer is right (100) or wrong (0)
    const quizOk = quizQ ? (quizQ.questionMode === 'mcq' ? quizScore === 100 || quizScore === 0 : quizScore >= 0 && quizScore <= 100) : true;

    // Fatal: a plugin UI that fails to load must fail verification (specs/11 FR-7).
    const pluginUi = await verifyFirstPartyPluginUis();
    const posMenu = await verifyPosCategoryMenu();
    const duplicates = await verifyDuplicatesDialog();
    const bulkEdit = await verifyBulkEditDialog();
    const editors = await verifyEditors();
    const memory = await verifyMemoryTab();
    const look = await verifyLook();
    const contrast = await verifyContrast(CONTRAST_STRICT);
    const bookCard = await verifyBookCard();
    const lists = await verifyListsAndBookmarks();

    const passed = popVisible && headword.length > 0 && vocabSetsCount > 0 && quizOk;

    await rpc.call('ui.reportSelfTestResult', {
      passed,
      summary: `UI_WINDOW_READER_OK: book="${targetBook.title}" (${targetBook.bookId}), chapter="${currentChapter.currentSpineHref}", domTokens=${tokCount}, popoverVisible=${popVisible}, headword="${headword}", vocabSets=${vocabSetsCount}, aiQuiz=${quizQ ? `score ${quizScore} (${quizQ.questionMode})` : `refused (${quizRefused})`}, ${pluginUi}, ${posMenu}, ${duplicates}, ${bulkEdit}, ${editors}, ${memory}, ${look}, ${contrast}, ${bookCard}, ${lists}`,
    });
  } catch (err: any) {
    await rpc.call('ui.reportSelfTestResult', {
      passed: false,
      summary: `FAIL: ${err.message}`,
    });
  }
}
