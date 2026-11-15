# Spec 25: Text editing with Monaco: editable prompts, a user stylesheet, and the characters' memory view

Status: **implemented** (Monaco editor component, Settings → Prompts, Settings → Custom CSS, Settings → Memory; C# prompt engine and catalog; `prompts_dir` in the builder; 20+ C# tests, 2 Python tests, `--verify-ui` drives the real editors and the real memory file). Builds on [spec 19](19-prompt-templates-and-conversation-coach-spec.md) (prompt templates), [spec 24](24-knowledge-hub-and-user-memory-spec.md) (memory).

## 1. Goal

Give the user a proper text editor in the app and use it first for three things: the **prompts** the program sends to models, **one stylesheet of their own** for the whole interface and the reader, and later **notes**. The same editor component serves all of them.

## 2. The editor (`components/editor`)

* **Monaco 0.57**, loaded **on demand** (its own chunks; the main bundle does not contain it). Offline: nothing comes from a CDN; the two web workers (editor, CSS) are bundled **inline** (blob workers), because the app runs from `file://` where a worker file URL is refused. Verified in the real window by `--verify-ui` (`prompts=ok…, css=ok(live preview, css service answered)`).
* **Lean entry, not the package's all-in-one one.** Only the editor core and its features, the CSS language and the CSS worker are imported. The full entry pulled TypeScript, HTML and JSON language services (about 30 MB of extra build output) that nothing here uses.
* `CodeEditor` props: `documentKey` (switching it swaps the text and its undo history), `value`, `language`, `onChange`, `onSave` (Ctrl+S), `markers`, `readOnly`. It follows the app theme (the themes are CSS variables on `<html>`; a `MutationObserver` re-derives the Monaco colours).
* A prop is pushed into the editor **only when it changed** since last shown. (A first version re-pushed whenever the prop and the text differed, so a re-render with a stale prop wiped what the user had just typed. Found by the real-window test.)
* New languages and completions are registered in `loadMonaco.ts`: **`irprompt`** (front matter, `{{name}}`, `{{#if}}`, `{{#each}}`, fenced markers) with variable completion taken from the open prompt, and completion of the app's theme variables in CSS.

## 3. Prompts (Settings → Prompts)

* The C# **template engine** (`Core.Prompts.PromptTemplate`) is the twin of the Python one and runs the same **33 shared vectors** (`specs/fixtures/prompt-vectors.json`). Body errors carry a line number so the editor can mark them.
* **`PromptCatalog`** (Core, one per app, `<data root>/prompts/`): owners register built-in templates (`context.RegisterPrompts`); the user's edited copy is saved as `<name>.prompt.md`. A copy is used only if it **passes validation and keeps the built-in's contract** (PT-5): same name, version not lower, every built-in variable still declared with the same type, `untrusted` never dropped (a copy cannot turn book or user text into trusted text), no new required variable (the program would not fill it). A copy that fails is **ignored, shown with the reason, never half-applied**; the built-in stays in force. Reset deletes the copy.
* RPC `prompts.list|check|save|reset`. The tab lists prompts by owner, shows the variables (untrusted ones highlighted), validates while typing, marks the error line, saves with Ctrl+S.
* **Which prompts exist (14):** the Character Knowledge builder's five (read from the builder folder, the single source) and the AI Companion's nine: `persona.system`, `persona.turn`, `persona.rewrite`, `quiz.generate.system`, `quiz.generate.user`, `quiz.grade.system`, `quiz.grade.user`, `persona.memory`, `memory.extract`. The services render through the host's catalog (`PersonaChatService.Prompts`, `ComprehensionQuizService.Prompts`); without a host they use the built-ins alone (`ChatPrompts.BuiltInsOnly`). The old C# string classes are gone. The builder receives the prompts **in force** as files in the job folder (`prompts_dir` in its config; `load_templates` in `cli.py`); a file that does not load, or a name the builder does not know, stops the build before anything is sent (exit code 2). A change applies to the next build; a running build keeps the prompts it started with.
* **Migration of the persona, quiz and grading prompts (PT-10) is done.** The persona instructions are word for word what the C# strings were (a test compares them); the quiz and grading prompts were then rewritten as version 2 ([spec 26](26-quiz-rubric-reading-lists-and-bookmarks-spec.md)). Two deliberate changes: book text, the learner's words, answers and the model's candidate reply are now **untrusted variables**, so they arrive fenced (`<<<NAME ... NAME>>>`) with one added line telling the model that fenced text is quoted material, never an instruction; and the persona's default voice line ("natural conversational tone") is now the template's own default instead of code. Persona name, role and speech style stay trusted (as before). Prompts that exist only as text sent to the Python builder are covered by `prompts_dir` above.

## 4. Custom CSS (Settings → Custom CSS)

* One file, `<data root>/appearance/user.css` (max 512 KB), plus an on/off marker. RPC `appearance.getUserCss|setUserCss|setUserCssEnabled`.
* Applied as the **last `<style>`** in the app window **and inside the chapter iframe** (the book page), so it wins at equal specificity. The editor previews the text **live while typing**; leaving without saving restores the saved file.
* **Stable hooks:** `html.ir-app` / `html.ir-reader` tell the two documents apart; `data-ir` attributes on `app`, `header`, `sidebar`, `library`, `reader`, `chapter-frame`; the theme variables (`--bg-primary`, `--accent-color`, `--reader-font-size`, …). Other class names may change between versions. Inside the book page the app sets font, size and colours with `!important`; a user rule needs `!important` to override them.
* **Way out of a broken stylesheet:** `Ctrl+Alt+Shift+C` switches the stylesheet off (and on) from anywhere; it is checked before any other shortcut. The file is never deleted by switching off.
* Remote `@import` is not blocked: the stylesheet is the user's own file.

## 4b. Readable in every theme, square dialogs, quiet scroll bars

* **Contrast guard.** `--verify-ui` applies each colour theme to the open window (nothing is saved), opens 25 surfaces in turn (reader chrome, every plugin drawer, all Settings tabs, the dialogs, the library, the book dialog with all its tabs including the plugins' tabs) and compares every visible piece of text with the background it sits on (`services/contrastAudit.ts`; disabled controls and invisible-by-design text are skipped). **Any text below 4.5:1 fails the run** (`CONTRAST_STRICT`, `CONTRAST_MIN` in `selfTestRunner.ts`). First measurement: 7,024 low-contrast texts over the four themes (about 1,700 below 3:1 in the light ones); now none below 4.5:1.
* **Theme-derived colours** (`styles/themeColors.ts`, applied by `applyThemeStyles`): muted text and accent-coloured text (`--accent-text`) are pulled towards the main text colour until they reach 4.5:1 on the surface, on darker panels and on accent washes; the text of an accent button (`--accent-contrast`) is black or white by the accent's brightness (it was always black: invisible on the e-ink theme); on a light theme the pale Tailwind status shades 200 to 400 become darker ones; `html[data-theme-mode]` says which kind of theme is on. Status text colours for plugins that cannot use Tailwind: `--danger-text`, `--success-text`, `--warning-text`, `--info-text`, `--violet-text`.
* **Rules for new UI:** no `text-white`/`text-black` on a neutral surface (use `--text-primary`, `--text-muted`), none on an accent button (use `--accent-contrast`), no `border-white/N` or `bg-white/N` (use `--border-color`, `--hover-bg`), no hex colours in plugin stylesheets (use the tokens above, `--shade` for darkening layers). White text on a solid saturated button (`bg-rose-600 text-white`) is fine.
* **Square dialogs.** Dialog panels carry `modal-surface` and `rounded-[var(--radius-modal)]` (`--radius-modal: 0px`); controls inside a dialog use `--radius-control` (2px). Circles and pills are left alone. Both tokens can be set in Custom CSS.
* **Scroll bars.** 8px wide with a transparent track and a faint, square thumb (4px visible) in the app and in the book page. **macOS (`html.ir-mac`) keeps its native overlay bars**: they are already thin and fade when idle, and a styled `::-webkit-scrollbar` would make them permanent.
* **Not covered by the audit:** dialogs opened by plugin series actions, hover states, the book page itself (a separate document), Monaco's own colours.

## 5. Memory (Settings → Memory)

Lists every fact the characters hold about the user (text, where it came from, date, whether one character only), adds, forgets one, forgets all. RPC `memory.list|add|forget|forgetAll`. The persona chat integration is described in [spec 24](24-knowledge-hub-and-user-memory-spec.md) (phase 2).

## 6. Not done

* **Notes** (the third use of the editor): no storage or UI yet; the component is ready (`language="markdown"` needs its definition imported in `loadMonaco.ts`).
* A **diff view** against the built-in prompt, per-prompt **test render** with sample values, import and export of prompt sets.
* Editing the Python builder's **system-level behaviour** beyond its prompts.
* CSS: a visual picker for regions, and a per-book stylesheet.
