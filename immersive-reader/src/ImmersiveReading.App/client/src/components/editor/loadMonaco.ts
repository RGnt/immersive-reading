// Monaco is large (several MB), so it is loaded only when an editor is first opened (dynamic import, its own chunks).
// It must work offline inside the desktop window: nothing is fetched from a CDN, and the web workers are bundled inline
// (blob workers), because the app runs from file:// where a worker file URL would be refused.
import type * as Monaco from 'monaco-editor/editor/editor.api';

export type MonacoApi = typeof Monaco;

/** The language id of prompt templates: markdown-ish text with the template syntax ({{name}}, {{#if}}, {{#each}}). */
export const PROMPT_LANGUAGE = 'irprompt';

/** CSS custom properties of the app that a user stylesheet can rely on (offered as completions). */
export const APP_CSS_VARIABLES = [
  '--bg-primary', '--bg-surface', '--text-primary', '--text-muted', '--border-color', '--accent-color', '--ruby-color',
  '--ui-font-family', '--reader-font', '--reader-font-size', '--reader-line-height', '--reader-column-width',
  '--bg-app', '--bg-panel', '--bg-card', '--bg-hover', '--bg-input', '--text-main', '--accent', '--accent-hover',
  '--danger', '--success', '--warning',
];

let pending: Promise<MonacoApi> | null = null;

export function loadMonaco(): Promise<MonacoApi> {
  pending ??= (async () => {
    // Not the package's all-in-one entry: that registers every language service (TypeScript, HTML, JSON ...) and their workers,
    // tens of MB of code this app never uses. The editor core, its features, the CSS language and the CSS worker are enough.
    const [monaco, editorWorker, cssWorker] = await Promise.all([
      import('monaco-editor/editor/editor.api'),
      import('monaco-editor/editor/editor.worker?worker&inline'),
      import('monaco-editor/language/css/css.worker?worker&inline'),
      import('monaco-editor/features/register.all'),
      import('monaco-editor/languages/definitions/css/register'),
      import('monaco-editor/languages/features/css/register'),
    ]);
    (self as unknown as { MonacoEnvironment: Monaco.Environment }).MonacoEnvironment = {
      getWorker(_workerId: string, label: string) {
        return label === 'css' || label === 'scss' || label === 'less' ? new cssWorker.default() : new editorWorker.default();
      },
    };
    (window as unknown as { __irMonaco: MonacoApi }).__irMonaco = monaco;      // test hook: --verify-ui drives the real editors
    registerPromptLanguage(monaco);
    registerCompletions(monaco);
    return monaco;
  })();
  return pending;
}

function registerPromptLanguage(monaco: MonacoApi) {
  monaco.languages.register({ id: PROMPT_LANGUAGE, aliases: ['Prompt template'], extensions: ['.prompt.md'] });
  monaco.languages.setMonarchTokensProvider(PROMPT_LANGUAGE, {
    defaultToken: '',
    tokenizer: {
      root: [
        [/^---\s*$/, { token: 'meta.separator', next: '@header' }],
        { include: '@body' },
      ],
      header: [
        [/^---\s*$/, { token: 'meta.separator', next: '@pop' }],
        [/^(\s*-\s*)([A-Za-z_][A-Za-z0-9_]*)/, ['delimiter', 'variable.name']],
        [/\b(required|untrusted)\b/, 'keyword'],
        [/\b(default|enum|type)(=)/, ['keyword', 'delimiter']],
        [/^[A-Za-z_]+(?=:)/, 'type.identifier'],
        [/\|/, 'delimiter'],
      ],
      body: [
        [/\{\{\s*(?:#if|#each|\/if|\/each)\b/, { token: 'keyword.control', next: '@tag' }],
        [/\{\{\s*this\s*\}\}/, 'variable.predefined'],
        [/\{\{/, { token: 'variable', next: '@tag' }],
        [/^#{1,6}\s.*$/, 'markup.heading'],
        [/<<<[A-Z_]+|[A-Z_]+>>>/, 'comment'],
      ],
      tag: [
        [/[A-Za-z_][A-Za-z0-9_]*/, 'variable.name'],
        [/\}\}/, { token: 'variable', next: '@pop' }],
        [/\s+/, ''],
      ],
    },
  });
  monaco.languages.setLanguageConfiguration(PROMPT_LANGUAGE, {
    brackets: [['{{', '}}']],
    autoClosingPairs: [{ open: '{{', close: '}}' }, { open: '(', close: ')' }, { open: '"', close: '"' }],
    surroundingPairs: [{ open: '{{', close: '}}' }],
  });
}

/** Variable names offered after "{{": set by the editor that is currently open (a prompt knows its own variables). */
export const promptCompletionState: { variables: { name: string; type: string; untrusted: boolean; required: boolean }[] } = { variables: [] };

function registerCompletions(monaco: MonacoApi) {
  monaco.languages.registerCompletionItemProvider('css', {
    triggerCharacters: ['-', '('],
    provideCompletionItems(model, position) {
      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      return {
        suggestions: APP_CSS_VARIABLES.map((name) => ({
          label: name,
          kind: monaco.languages.CompletionItemKind.Variable,
          insertText: name,
          detail: 'Immersive Reading theme variable',
          range,
        })),
      };
    },
  });

  monaco.languages.registerCompletionItemProvider(PROMPT_LANGUAGE, {
    triggerCharacters: ['{', '#', ' '],
    provideCompletionItems(model, position) {
      const before = model.getValueInRange(new monaco.Range(position.lineNumber, 1, position.lineNumber, position.column));
      const open = before.lastIndexOf('{{');
      if (open < 0 || before.indexOf('}}', open) >= 0) return { suggestions: [] };
      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      const names = promptCompletionState.variables.map((v) => ({
        label: v.name,
        kind: monaco.languages.CompletionItemKind.Variable,
        insertText: v.name,
        detail: `${v.type}${v.required ? ', required' : ''}${v.untrusted ? ', untrusted (fenced when inserted)' : ''}`,
        range,
      }));
      const keywords = ['#if', '#each', '/if', '/each', 'this'].map((k) => ({
        label: k, kind: monaco.languages.CompletionItemKind.Keyword, insertText: k, range,
      }));
      return { suggestions: [...names, ...keywords] };
    },
  });
}

/** Light or dark Monaco base for the current app colours (the app themes are CSS variables, not classes). */
export function applyAppTheme(monaco: MonacoApi) {
  const css = getComputedStyle(document.documentElement);
  const read = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback;
  const bg = read('--bg-input', read('--bg-primary', '#0f172a'));
  const fg = read('--text-primary', '#f8fafc');
  const dark = luminance(bg) < 0.5;
  monaco.editor.defineTheme('ir-app', {
    base: dark ? 'vs-dark' : 'vs',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': toHex(bg),
      'editor.foreground': toHex(fg),
      'editorLineNumber.foreground': toHex(read('--text-muted', '#94a3b8')),
      'editorCursor.foreground': toHex(read('--accent-color', '#38bdf8')),
      'editor.lineHighlightBackground': dark ? '#ffffff0d' : '#0000000d',
    },
  });
  monaco.editor.setTheme('ir-app');
}

function toHex(color: string): string {
  const c = document.createElement('canvas').getContext('2d');
  if (!c) return color;
  c.fillStyle = '#000';
  c.fillStyle = color;
  const v = c.fillStyle;                                   // normalised: #rrggbb
  return /^#[0-9a-f]{6}$/i.test(v) ? v : '#000000';
}

function luminance(color: string): number {
  const hex = toHex(color);
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
