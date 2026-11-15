# Troubleshooting & Lessons Learned (check here BEFORE debugging)

**Rule:** when you fix a non-trivial bug or learn a gotcha, add an entry here using the template. Newest first. Search with `grep -in <keyword> wiki/troubleshooting-and-lessons.md`.

```
### Knowledge graph: the model server advertises 262,144 tokens but enforces ~8k per request
- Why it mattered: the first full-volume build died on chapter 12 with HTTP 400 `prompt is too long: 8630 tokens > 8241 maximum (prompt + generation)`, and earlier answers were cut at about 6,000 tokens, although `/v1/models` reports `context_length: 262144`.
- Cause: the FreeToken instance runs with a small KV-cache budget; the advertised model length is not the per-request budget.
- Fix / how: `knowledge-graph` never trusts the advertised size. It parses the real limit from the error, shrinks the snapshot (3 levels), then splits the text; later parts are sized to a quarter of the learned context; a cut-off answer is also split. Nothing of a failed or truncated answer is applied. Config `context_tokens` skips the discovery.
- Prevent: `tests/test_adaptive.py`. For the user: raising the KV cache budget in FreeToken lets whole chapters go in one request.

### Knowledge graph: reasoning models burn the output budget on hidden thinking
- Why it mattered: with default settings the first real request returned an empty `content`, 5,992 tokens of `reasoning_content` and `finish_reason: length`.
- Cause: Qwen3.x thinks by default; the output cap is shared by reasoning and answer.
- Fix / how: extraction sends `chat_template_kwargs: {"enable_thinking": false}` by default (config `thinking`); `finish_reason == "length"` is reported with the hint instead of "invalid JSON".
- Prevent: `tests/test_llm.py` (`finish_reason` cases).

### Knowledge graph scanner blocked 36% of snapshots on ordinary words
- Why it mattered: names such as `クマ` (bear, on every page) or `ギル` (inside `ギルド`) became entity names later, so the scanner treated them as secrets and blocked 40 of 110 snapshots.
- Cause: "hidden" was defined only by when the entity was created, not by whether the reader had already read the word; matching was a bare substring.
- Fix / how: `names.seen_key` (first occurrence in the book text, indexed by the builder) and script-boundary word matching (`evidence.find_word`, honorifics allowed). Names without an indexed sighting still use the strict rule. A character without a snapshot at the reader's chapter falls back to its latest earlier snapshot.
- Prevent: `tests/test_adaptive.py` (sightings, boundaries), `tests/test_scanner_profile.py` (canary tests unchanged and green).

### Knowledge graph: one rejected `add_entity` cascaded into 16 rejected operations
- Why it mattered: a chapter told from Fina's point of view re-created Fina as a "new" entity; the duplicate-name rule rejected it and every operation using that ref failed (0 applied that chapter).
- Cause: rejecting is the wrong answer when the model simply re-introduced someone it was shown.
- Fix / how: `ops._reuse_entity` binds the ref to the existing entity (new names become aliases with evidence); an entity the model was not shown (created in the same chapter by an earlier answer) is still rejected.
- Prevent: `tests/test_ops.py::ReuseTests`.

### Knowledge graph: quote verification, strict where it matters
- Why it mattered: real answers quote with different edge punctuation (`「…ユナ様」` vs `…ユナ様。`), use 3-character name quotes, or paraphrase one of several quotes.
- Fix / how: edge brackets and sentence-end marks are ignored (the body must match exactly); an entity's quote may be as short as its name but must contain it and the name must occur in the text; an operation with at least one verified quote is kept and positioned at the end of the text the model was shown (conservative).
- Prevent: `tests/test_position_evidence.py`, `tests/test_ops.py::EvidenceStrictnessTests`.

### LLM deck enrichment: why not the AI companion's provider, and why the plugin (not Python) calls the model
- Why it mattered: asking an LLM for grammar and particle notes risked (a) silently getting canned text, (b) invented example sentences, (c) breaking the "no LLM / no network in `enrichement-extractor`" invariant.
- Cause: `OpenAiCompatibleLlmProvider` falls back to a fake quiz answer on any failure when `EnableOfflineHeuristicFallback` is on; the Python pipeline must stay offline.
- Fix / how: the DeckBuilder plugin has its own `OpenAiChatClient` (no fallback, real HTTP errors, retries) fed from Settings → AI; the model answers with sentence ids + exact `surface`, the plugin maps them to real book sentences and drops anything unverifiable; Python only reads `deck-grammar.json` and verifies the examples again. Particle/auxiliary marking is deterministic from MeCab POS, not LLM output. Spec 16.
- Prevent: `DeckEnrichmentTests` (hallucinated example dropped, fatal errors not retried, no fake answer without an endpoint), `test_deck_function_words.py` (invented sentence never reaches the study data).

### Prompt JSON lists were `\uXXXX`-escaped
- Why it mattered: `KNOWN_PATTERNS` / `FUNCTION_WORDS` reached the model as `\u3066...`, wasting tokens and weakening pattern reuse.
- Cause: `JsonSerializer.Serialize` escapes non-ASCII by default.
- Fix / how: `DeckEnricher.PromptJson` uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. Caught by `Enricher_SamePatternInLaterChapter_AddsExamplesAndKeepsFirstChapter`.
- Prevent: serialize any text that goes *into a prompt* with the relaxed encoder.

### System.Text.Json did not restore a get-only `List<>` property
- Why it mattered: a saved enrichment would reload with empty `Examples`, dropping every example from the next export.
- Cause: STJ does not populate read-only collection properties on deserialization.
- Fix / how: `GrammarPoint.Examples` is `{ get; set; }`; `DeckEnrichmentTests.Store_RoundTripsIncludingExamples_AndIgnoresAChangedBook` guards it.
- Prevent: persisted models need settable collections and a round-trip test.

### Plugin singleton only implements IAsyncDisposable: container Dispose throws
- Why it mattered: `AppServices.Dispose()` threw `InvalidOperationException` on shutdown (and failed `AppServicesContainerTests`) after adding the `JitenMoe` plugin.
- Cause: the plugin is registered as a DI singleton in its module; `ServiceProvider.Dispose()` (sync) refuses a service that implements only `IAsyncDisposable`.
- Fix / how: `JitenMoePlugin` and `DeckBuilderPlugin` implement `IDisposable` too (same as `PosHighlighter`).
- Prevent: `AppServicesContainerTests.Create_LoadsAllPluginsPolymorphicallyFromPluginCatalog` asserts every first-party plugin loads and disposes.

### Moving a feature out of core into a plugin (jiten.moe, spec 15)
- Why it mattered: the library UI and `LibraryRpcDispatcher` had jiten-specific code (modal, badges, buttons, RPC module); it could not be switched off or reused.
- Cause: the feature predated the plugin system.
- Fix / how: backend moved to `src/Plugins/ImmersiveReading.Plugins.JitenMoe` (RPC `library.*Jiten*` became `ext.jitenMoe.*`), host got generic slots (Book Info tab, book badge, series action, `refreshLibrary`, `IPluginHostContext.Library`). The `jiten_moe_url` / `jiten_difficulty` custom-field *definitions* deliberately stay in `LibraryDatabase` (library data, used by Phase 1 inheritance/filter tests and by existing user libraries).
- Prevent: new library features add a slot, never a hard-coded button. Plugin CSS must ship in its own `ui.css` (host Tailwind does not scan plugin sources).

### Deck Builder: `AnalyzedToken.DictionaryReading` is only the stem for conjugated verbs
- Why it mattered: homograph choice and the unresolved-word reading for conjugated forms (食べた) looked wrong.
- Cause: the MeCab adapter merges verb + auxiliaries but keeps `DictionaryReading` of the first morpheme (`たべ`, not `たべる`).
- Fix / how: `DeckParser` treats it as a *prefix* hint (`reading_norm.StartsWith`) when choosing between dictionary rows and only uses it as the word's reading when `surface == lemma`.
- Prevent: `DeckBuilderTests.RealMeCab_ConjugatedVerbsResolveToTheirDictionaryForm_AndSymbolsAreSkipped`.

### <short symptom>
- Why it mattered: <user-visible impact / why a fix was needed>
- Cause: <root cause, verified>
- Fix / how: <what changed, files, commit>
- Prevent: <test, rule, or wiki page>
```

---

### Image-only books (manga, scanned books) in the library (fixed at import: spec 13)
- Why it mattered: image EPUBs have no text, so tokenizing, lookups, vocabulary and study packs are meaningless, yet they imported as hundreds of "chapters" (one chapter held 266 image files).
- Cause: import had no notion of content type. Detection was also non-obvious: the share of image pages is a poor signal because picture books and diagram-heavy books sit at 50-90% (547 of 2,223 books).
- Fix / how: at import, profile each spine document; image-only means >= 90% image pages AND < 80 visible characters per document (clean gap in the real data between 60 and 100). Scans skip and warn; single import throws `UnsupportedBookException`; nothing is written. 55 existing library books match (listed by `--verify-chapters`, untouched).
- Prevent: `BookContentProfileTests` (boundaries 89/90% and 70/100 chars, light novels, fixed-layout-with-text), `ImageOnlyImportTests`. When changing thresholds, re-run the library analysis (`--verify-chapters`) and check the gap.
- Method note: the thresholds came from analyzing every EPUB in the real library first (a throwaway Python script over the zips, 18 s), then confirmed that the C# detector flags the identical 55 books.

### Chapters: too many, split around images (fixed: spec 12)
- Why it mattered: 39% of all chapter records in the library (34,278 of 88,460) were generated `(Part N)` / `(Illustration)` entries; books showed chapters like *text, (Illustration), (Part 2), (Illustration), (Part 3)*.
- Cause: `ChapterListBuilder` emitted one chapter per **spine file**; files without their own ToC entry (split files like `part0005_split_001.html`, illustration pages) became continuation records.
- Fix / how: one chapter per ToC entry with member spine files, rendered as one merged document; legacy books upgrade lazily on first open. Details: [dotnet-architecture.md](dotnet-architecture.md) section 8, [spec 12](../specs/12-chapter-grouping-spec.md).
- Prevent: `ChapterGroupingTests`, `ChapterModelUpgradeTests`, `ChapterGroupingRenderTests`, and `--verify-chapters` (dry run over the whole library; expect 0 coverage mismatches).
- Method note: measure against the real library first (`library.db` is plain SQLite; open it read-only) before deciding a design. A dry-run CLI mode that reports before/after totals found a data shape (image-only books with 100+ files per chapter) that the synthetic tests never would.

### Illustration chapters shrank to 1/6 of the window with a bottom scrollbar (fixed: spec 14)
- Why it mattered: after chapter grouping, "口絵 (Illustrations)" (6 full-page SVG/image files) showed six small pictures side by side in vertical books; horizontal books showed them in the 800 px text column.
- Cause: the merged document lays parts out like text. In `vertical-rl` the block axis is horizontal, so each part (`<section class="ir-spine-part">`) becomes a column; six pages of ~483 px sat in one 3,382 px row. The older per-file render did not have this because each illustration was its own chapter. My first hypotheses (wrapper breaking percentage heights, the reader font-size override) were wrong; measuring the real chapter through the real pipeline showed the row layout.
- Fix / how: the composer adds `ir-image-part` to parts that are illustration pages (image element, at most 50 visible chars, same rule as spec 13); `ir-reader-injected.css` sizes those to exactly one window page in both modes with viewport-unit custom properties (`--ir-page-w/h`, not percentages because publisher wrappers like `div.main` have no definite size). Details: [spec 14](../specs/14-image-page-layout-spec.md).
- Prevent: `ChapterGroupingRenderTests` (marking rules) and `ProgramSchemeTests.ReaderCss_SizesIllustrationPartsToOnePage_InBothWritingModes` (CSS contract). When touching merged-chapter layout, test with a chapter of 6 illustration files, not 2.
- Method note: reproduce with the app's own pipeline (a throwaway test that imports the real EPUB and dumps the `BookSchemeHandler` output), then measure element boxes with headless Edge (`--enable-logging=stderr --v=0` and `console.log` in an injected script gives numbers when `--dump-dom` prints nothing). Headless Edge paints vertical-rl pages with scrolled content blank, so use box measurements there. In a test harness, never string-replace a mode class over HTML that already contains the CSS text.

### Library schema changes silently did nothing (`LibraryMigrations` is dead code)
- Why it mattered: spec 12 planned "migration v4" for new columns; it would have been a no-op for the existing database.
- Cause: `LibraryMigrations.All` is not applied anywhere. The library schema only runs `CREATE TABLE IF NOT EXISTS` (`LibraryDatabase.SchemaSql`), which never alters an existing table. (The earlier version of dotnet-architecture.md wrongly said to "add a migration".)
- Fix / how: `EnsureColumn` helper in `LibraryDatabase.InitializeSchema` (checks `PRAGMA table_info`, then `ALTER TABLE ... ADD COLUMN`), plus the column in `SchemaSql` for fresh databases.
- Prevent: when adding a library column, update both places; add a test that opens a DB without it and calls `InitializeSchema`.

### Plugin UI bundles could not load (fixed: spec 11)
- Why it mattered: no plugin UI (POS "Highlights" menu, AiCompanion drawer/page) could ever appear in the reader, and nothing said why.
- Cause: (1) the SPA runs from `file://` (`MainWindowHost` calls `window.Load(dist/index.html)`), where the WebView blocks every `fetch()`/`import()` of an `ir-app://` URL, including the SDK shims. Loading the SPA through `ir-app://app/index.html` instead gives a **blank window** (module scripts from a custom-scheme origin don't load; `window.Load(string)` also treats the argument as a file path, use `Load(Uri)`). (2) AiCompanion's embedded `ui.js` was **raw JSX**, which no browser can run, so it could never have loaded. (3) Load errors were swallowed into `console.error`.
- Fix / how: plugin code is fetched over JSON-RPC (`extensions.getAsset`) and run as `blob:` modules (blob import works from `file://`); bare imports are rewritten to blob shims generated from `window.__IR_RUNTIME__`; JSX plugins are compiled by `npm run build:plugins`; errors are stored per plugin and shown on the plugin's Settings card; `--verify-ui` now fails if a first-party plugin UI doesn't load.
- Prevent: [plugin-ui-authoring.md](plugin-ui-authoring.md). Guard tests: `FirstPartyPluginUi_OnlyImportsSpecifiersTheHostProvides`, `FirstPartyPluginUi_IsPlainJavaScript_NotRawJsx`, and the `pluginUi=` / `posMenu=` fields of `--verify-ui`.
- Method note: a hypothesis about WebView behavior is cheap to test inside `--verify-ui` (probe `fetch`/`import` in the self-test and read the summary). Do that before designing around an assumption.

### (Superseded by spec 11) Hand-maintained SDK shims / inert toolbar button
- Why it mattered: the POS "POS Colors" toolbar button did nothing, and a plugin `ui.js` can fail to load with no visible error.
- Cause: (1) plugin `ui.js` imports resolve through the import map to hard-coded shims in `AppSchemeHandler.GetSdkShim` (`lucide-react.js`, `host-sdk.js`). Importing a name the shim does not list (e.g. `Palette`, `setReaderStyle`) is an ES-module link error that kills the whole bundle. (2) `ToolbarActionContext` never contained `rpc`/`toast`, yet the old `ui.js` destructured them, so even a loaded button threw inside a try/catch.
- Fix / how (at the time): added names to the shims and a scoped `rpc` to `ToolbarActionContext`.
- Prevent: obsolete. The `ir-app://sdk` shims and `GetSdkShim` were removed; shim exports are now derived from the runtime object's keys. The scoped `rpc` in `ToolbarActionContext` stays.

### `dotnet build` fails: file locked by `ImmersiveReading.App`
- Why it mattered: build/test could not copy plugin DLLs while the app was running.
- Cause: a running `ImmersiveReading.App` process holds `bin/Debug/net10.0/*.dll`.
- Fix / how: either close the app, or build/test into the git-ignored `temp/` folder inside the repo: `dotnet test ImmersiveReading.sln -p:OutDir='K:\immersive-reading\temp\out\' -p:BuildReactClient=false`.
- Prevent: the OutDir **must stay inside the repo**. `PythonEnvironmentResolver.ResolveExtractorDir` walks up from `AppContext.BaseDirectory` to find `enrichement-extractor`, so an OutDir elsewhere (e.g. the OS temp dir) makes 3 Python-dependent tests fail with a null path.

### POS highlighting is CSS-only (what changes when)
- Facts that avoid re-investigating: the stage always writes token classes and `data-ir-pos`; only the injected `<style id="ir-pos-highlighter-style">` decides what is visible. `ChapterEnrichmentCache` stores only the token map, so every chapter load re-enriches with the *current* settings. The reader iframe is re-created per chapter (`key=bookId-spineHref`). Category selection (spec 10) therefore needs no cache invalidation; live updates just replace that `<style>` in the open iframe via `setReaderStyle`.

### Python version probe hangs the Settings UI
- Why it mattered: the "detect Python" action could block forever on a stuck interpreter.
- Cause: `ConfigRpcModule.CheckPythonVersion` called `StandardOutput.ReadToEnd()` before `WaitForExit(3000)`. `ReadToEnd` blocks until the process exits, so the timeout never fired.
- Fix / how: `ReadToEndAsync` on both streams, then `WaitForExit`, then `Kill(entireProcessTree: true)` on timeout.
- Prevent: with `RedirectStandardOutput`, never read synchronously before a timed wait. See [dotnet-code-review.md](dotnet-code-review.md).

### Silent failures from bare `catch { }`
- Why it mattered: dictionary enrichment, kanji lookup and study-pack flag updates failed with no trace.
- Cause: blanket catches in `ReaderRpcModule` and `StudyPackRpcModule`.
- Fix / how: catch `SqliteException` / `IOException` / `JsonException` and `Trace.TraceWarning`.
- Prevent: the grep command in [dotnet-code-review.md](dotnet-code-review.md).

### Dictionary preset downloads 404 (KANJIDIC, JMnedict, KireiCake)
- Cause / fix: URLs in `DictionaryRpcModule.Presets` went stale; fixed in commit `41a064c` (`git show 41a064c` for the URLs).
- Prevent: when a preset 404s, check the upstream release URL first. The `kirei*` preset id match is prefix-based by design.

### Reader text rendering / furigana toggle desync across Japanese EPUBs
- Commits: `b5d6d3a`, `1ee83e7` (`git show <sha>` for details). Touches `BookSchemeHandler`, `AngleSharpDomEnricher`, `ir-reader-bridge.js` and the injected CSS served via `ir-app://assets/`.
- Start debugging with `dotnet run --project immersive-reader/src/ImmersiveReading.App -- --verify-ui` (`ReaderVerification.cs`), which checks assets, enrichment and lookup end to end.

### Environment gotchas
- Python tests run from `enrichement-extractor` with `.venv\Scripts\python.exe`. .NET tests run from `immersive-reader` (`dotnet test ImmersiveReading.sln`).
- `ImmersiveReading.App.csproj` runs `npm run build` before build **only if `wwwroot/dist/index.html` is missing**. Pass `-p:BuildReactClient=false` to skip the Vite build.
- Some App paths are relative to `AppContext.BaseDirectory` (`.library-data/`, e.g. dictionary downloads), so running from `bin/` can use a different data dir than the repo's `immersive-reader/.library-data`.
- Bash tool on Windows: a long multi-heredoc command (or one mixing `'''` Python strings with regex backslashes) can fail to parse and write nothing, or silently mangle `\b` into a backspace. Check `git status`, and write files with the Write tool, then run a small script file.

### Plugin `settings.schema.json` with an invalid JSON escape breaks app start-up
- Symptom: `AppServicesContainerTests.Create_LoadsAllPluginsPolymorphicallyFromPluginCatalog` fails with `'.' is an invalid escapable character within a JSON string` thrown from `PluginConfigContext.LoadInitial`.
- Cause: a description containing a Windows path (`knowledge-graph\.venv`) was written into `settings.schema.json` unescaped; one bad embedded schema stops every plugin from loading.
- Fix / prevent: no backslashes in schema text; after creating or editing a plugin's `settings.*.json`, run `python -c "import json; json.load(open(...))"` on both files. The container test is the guard.

### Character knowledge plugin: where the safety lives (spec 20)
- Why: the reader position only selects a precomputed snapshot; all gating is in the Python builder (`gate.py`, spec 18). C# must never query facts, names or events directly, otherwise two implementations of the spoiler rule would drift.
- Do: `KnowledgeDb.ListCharacters/GetProfile` read `profile_snapshots` with "latest boundary at or before (volume, chapter)". Keep the C# schema assumptions in sync through `specs/fixtures/knowledge-schema.sql` (a Python test guards the fixture).
- Do: the API key goes only through the `KNOWLEDGE_API_KEY` environment variable of the child process (test `Runner_DefaultStartInfo_NeverPutsTheKeyOnTheCommandLine`); the volume numbering of a build is stored in `volumes.json`, so adding volumes never renumbers built ones.

### Duplicate detection merged whole series into one group (spec 21)
- Symptom: on a real library, a "duplicate group" listed 12 different volumes of one series (and the 上/中/下 parts of one novel, and volume 14 with 14.5).
- Cause: synthetic tests only had `Title 3` style volumes. Real titles carry the volume attached (`教典2`, `件５`), inside (`賢者２ ～サブ～`), as roman numerals (`... VI`, `ホライゾンII<上>`), as kanji numerals (`～十一`, `～弐`), as `14.5`, `#EX`, `その２`; some tools stamp one `dc:identifier` on every volume; the series index is often wrong (14.5 stored as 14). The fuzzy-title rule (Dice 0.88) and the identifier rule then chained different volumes.
- Fix / how: richer volume extraction (spec 21 section 3.1), a number signature that must match for the fuzzy rules, strong (title) versus weak (file name, series index) volume evidence, an identifier only counts with a similar title and when used by at most 5 books, and group joins are refused when they would contain two known volumes.
- Prevent: `DuplicateDetectorTests` carries real titles as vectors. When changing a rule, copy the real library DB to a scratch folder and diff the groups; do not trust synthetic tests alone. Keep removing **only** the library entry (files stay) until the user explicitly asks for file deletion.

### Test book ids are hashes: do not assert on creation order
- Symptom: a test passed in Debug and failed in Release (`Collections differ`).
- Cause: `book_id` derives from the file hash (and the OPF test identifier is a fresh GUID), so ids are random per run; the test compared an ordered list of ids to an expected list in creation order.
- Fix / prevent: compare ids as sets (`Order(StringComparer.Ordinal)` on both sides).

### Building while the app is running
- Symptom: `MSB3021 Unable to copy file ... because it is being used by another process` for `ImmersiveReading.App\bin\Debug`.
- Cause: the desktop app (for example a long knowledge build) is running from that folder.
- Fix: do not kill it. Build and test with `-c Release` (a different output folder), or `dotnet build src/ImmersiveReading.App -o <scratch>` for a compile check. `--verify-ui` also works with `dotnet run -c Release`.

### The particle master PDF and the LLM grammar did not reach every printable (spec 16)
- Symptom: after "Improve deck with LLM", the study pack drawer showed no particle sheet, and the per-chapter printouts (kanji and vocabulary only) had no grammar at all; the grammar master printed the explanation without saying an LLM wrote it and with a single example.
- Cause: `StudyPackOverviewBuilder` had a fixed list of three master PDFs and classified every other `chapter-*.pdf` as kanji or vocabulary; the Python side wrote grammar and particles only as master sheets.
- Fix / how: per-chapter grammar and particle booklets (only for chapters with entries), provenance line and up to 3 verbatim examples on LLM cards in print and HTML, the drawer lists `function-words-master.pdf` and the new booklet kinds, `pdf_validation` and `publication.py` know the new names.
- Prevent: when a new printable is added, update **both** sides in one change: `render_outputs`, `pdf_validation` (exact file set), `publication.py` (recognised names), `StudyPackOverviewBuilder` (drawer) and its test. `StudyPackBookletTests` and `ChapterBookletTests` cover it.

### A holding that ends later was shown with its end (story cards, spec 22)
- Symptom: an item card at chapter 3 said the sword was held by nobody (and its history said it would be sold in chapter 4).
- Cause: the gate returned holdings *valid at P* with their real `to_key`. "Valid at P" includes holdings that end after P, so the end (a later fact) was copied into the card and the "current holder" lookup (`to is None`) failed.
- Fix / prevent: `Gate.view` returns valid holdings **open** (no end, no reason); only holdings that ended before P carry an end. Test `StoryCardTests.test_items_follow_their_holders_and_keep_their_history`. Any new "interval" data (a holding, a relation, an attribute) must hide its own end from a reader who is still inside it.

### A second pass needs its own rollback (story pass, spec 22)
- Why: the story pass must be redoable (failed chapter, changed text, prompt tuning) without touching the character data that took the longest to build.
- Do: every row a pass creates is tagged `origin` ('c' or 's', also on `evidence` and `ops_log`); `Store.rollback_story_from` deletes only story rows and **recomputes** single-valued attribute closures (a plain "reopen what chapter N closed" would also reopen values that character rows closed). `rollback_from` (characters) still takes everything of those chapters with it.
- Prevent: a new kind of derived fact needs its tag, its rollback and a test that rolls back a mixed store (`RollbackTests`).

### Names the story pass creates need a first-sighting index (spec 22)
- Symptom: the quest card of chapter 1 ("ゴブリン討伐の依頼") was blocked by the spoiler scanner because the monster entity ゴブリン is only created in chapter 2.
- Cause: the scanner treats a name as hidden until its entity is visible, unless the word has already occurred in the text read (`seen_key`). `_index_sightings` ran only after the character pass.
- Fix: run it after every story chapter too. Test `SpoilerLeakTests.test_a_word_the_reader_already_read_is_not_a_secret` (the loot saga contains exactly this case).

### A store that refuses to open must not stay locked (Windows)
- Symptom: a test of "unsupported schema version" failed on cleanup with `PermissionError: ... being used by another process`.
- Cause: `Store.__init__` raised after `sqlite3.connect`, leaving the connection (and the file handle) open; Windows cannot delete an open file.
- Fix: `Store.__init__` closes the connection when opening fails.

### Python heredocs through the shell tool
- Symptom: `unexpected EOF while looking for matching` quote errors when a heredoc contains many quotes, backslashes and Japanese text; edit scripts silently did nothing.
- Fix: write the script with the file tool (scratchpad) and run it, or make the change with the edit tool. Always `assert old in text` before replacing, so a miss fails loudly.

### `verify` demanded an exact position, but later is allowed (knowledge graph)
- Symptom: `verify` on a copy of the real knowledge database reported `events v0251: position is not the end of its latest evidence (I-K1)`.
- Cause: when one of an operation's quotes cannot be verified, the builder deliberately positions the fact at the end of the text the model was shown (later = safer). The check compared with `!=`.
- Fix: only an **earlier** position is a violation (it would show a fact before the reader has seen its support): `pos < max(evidence end)`. Test `test_verify_accepts_a_fact_positioned_later_than_its_evidence`.
- Do: run the new builder code on a **copy** of the real database (SQLite backup API, never the live file) after any schema or gate change; it found this and proved the migration on 27 real chapters.

### The sidebar tag filter was never applied (bulk edit, spec 23)
- Symptom: found while writing "select all N matching": filtering the library by a tag still listed every book.
- Cause: the client sent `tags: [...]` but `BookListQuery` binds `TagIds`; the unknown JSON property is silently ignored, so the query had no tag condition. Nothing failed, so nobody noticed.
- Fix: the client sends `tagIds`; test `LibraryBulkEditRpcTests.TheTagFilterTheSidebarSends_…` (it fails with `tags`).
- Prevent: when a UI filter "does nothing", check the property name against the C# record; `RpcRequest.Bind` ignores unknown properties. A new RPC contract test should send the exact payload the UI sends.

### The importer already guesses series and volumes (bulk edit, spec 23)
- Symptom: tests that expected a fresh book to have no series failed (`"Saga 10"` came back as series "Saga", volume 10).
- Cause: `MetadataMerger` falls back to title and file name hints for the series name and index.
- Do: in tests, assert against the state you set up yourself, not against "empty". In the product this is why a bulk edit shows a *now* column in the preview: the library is full of guesses, some wrong.

### Preview and apply must be one code path (bulk edit, spec 23)
- Why: a "preview" computed separately from the update drifts (different sort, different rounding) and the user trusts the preview. `BulkNumbering.Compute` is called by `PreviewBulkUpdate` and by `BulkUpdateBooks`; `Preview_ShowsExactlyWhatTheEditWritesAndWritesNothing` compares them row by row.

### Deck enrichment "did nothing": the counters were not live (spec 16)
- Symptom: during a run the status polled by the UI always said `grammarPointCount: 0, functionWordNoteCount: 0, warningCount: 0`, chapter after chapter, so the run looked like it changed nothing.
- Cause: `GetEnrichmentStatus` read the counts from `job.Result ?? stored`. `job.Result` is assigned only when the whole run ends and there is no stored result on a first run, so every running status was zeros. A chunk whose answer was valid but unusable (all examples unverifiable, empty arrays) was also silent, so zeros could not be told apart from failure.
- Fix: `EnrichmentProgress` carries live counters (and the last warnings) and the job exposes them while it runs; a chunk that contributed nothing adds a warning that says what the answer contained. Tests `Enricher_ProgressCarriesLiveCounters_…`, `Enricher_AnAnswerWhoseExamplesCannotBeVerified_…`.
- Prevent: a progress display must read the state the worker is updating, never the end-of-run result. Do not let "valid answer, nothing accepted" pass without a trace.

### Layer rules were only prose, and one was already broken (architecture guards, 2026-11-15)
- Symptom: `ConfigRpcModule` (App) used `LlmEndpointTester` from the AiCompanion plugin, although the rule says only `App/Composition` may name a concrete plugin. Nobody noticed because every project reference was legal at the csproj level.
- Cause: the dependency rules lived only in `wiki/dotnet-architecture.md`; nothing compared them with the code.
- Fix: `LlmEndpointTester` moved to `Core.Llm` (App and the plugin both use it); `ArchitectureTests` (.NET) and `test_architecture.py` in both Python projects read the project files and import statements and fail on a violation. Remaining debts are allowlisted with a ceiling (raw SQL in RPC modules, AI companion tables in Library migrations) so they can shrink but not grow.
- Prevent: a new module in `knowledge-graph` needs an entry in `ALLOWED` (decide its layer); a new SQL statement in an RPC module fails the test; when a rule changes, change the test and the doc together.

### Other features must reach a graph through a contract in Core, and gating must be part of it (knowledge hub, spec 24)
- Why: plugins reference only Core, so persona chat could not use the Character Knowledge plugin's graph without breaking the layering rule, and a naive "get everything" API would let any new consumer leak spoilers.
- Decision: `IKnowledgeHub` in `Core.Knowledge`; a source declares `Gating`; the hub refuses a `StoryPosition` source a query without a viewpoint before calling it, and the story source refuses again. Test: `StorySource_RefusesAQueryWithoutAViewpoint_EvenThroughTheHub`.
- Prevent: a new source must declare its gating honestly; a source that returns different data for different reader positions is `StoryPosition`, never `None`.

### Monaco in the desktop window (spec 25)
- The default `@monaco-editor/react` loader fetches Monaco from a CDN: unusable offline. Import Monaco from the package and bundle the workers inline (`monaco-editor/editor/editor.worker?worker&inline`, `.../language/css/css.worker?worker&inline`): the app runs from `file://`, where a worker file URL is refused. Package subpaths are exported without `esm/vs` (`monaco-editor/editor/editor.api`, not `monaco-editor/esm/vs/editor/editor.api`).
- Do not import the package's all-in-one entry: it registers every language service and worker (about 30 MB of build output). Use `editor.api` + `features/register.all` + the CSS definition and feature registration.
- A controlled editor must push a prop into the editor only when the prop **changed**. Pushing whenever prop and text differ lets a re-render with a stale prop overwrite what the user just typed. The real-window test (`--verify-ui`, `prompts=ok(...)`) found it; a jsdom test cannot run Monaco.

### Shell heredocs eat backslash-n (again)
- Python or TypeScript written through a shell heredoc turned `\n` into a real newline inside string literals, giving "unterminated string" errors. Write source files with the Write tool, or fix the literal with the Edit tool; never embed `\n` in code sent through a heredoc.

### A prompt override must keep the built-in's contract (spec 19 PT-5, spec 25)
- Why: the program fills in the variables it knows; a copy that drops one fails every request, and a copy that turns an `untrusted` variable into a trusted one removes the fence around book or user text.
- Rule in `PromptCatalog.Validate`: same name, version not lower, every built-in variable still declared with the same type and `untrusted` kept, no new required variable. A copy that fails is shown with its reason and never used.

### Migrating a prompt from a C# string to a template (spec 25)
- Keep the instructions word for word and prove it with a test that compares the rendered text with the old string; change only what is deliberate (here: book text and user words became fenced untrusted variables).
- Do not mark a variable `required` just because it is normally filled: a required variable that is empty makes the request fail ("required variable is empty"), where the old code sent an empty section. Use `required` only where an empty value is a bug.
- Fencing neutralises the **closing marker of the same variable** only (PT-4). Text pasted into one fenced block that contains another block's closing marker is not changed; the model sees it inside the first block.
- A service that can run without a host needs a built-ins-only catalog (`PromptCatalog("")`) as its default, otherwise tests and tools that build it directly have no prompts.

### Light themes: measure contrast instead of guessing (spec 25, section 4b)
- Symptom: text hard to read on the light themes, especially in plugin screens. About 280 hard-coded colours (`text-white`, `text-emerald-400`, `bg-black/20`, `bg-red-950/40`, `border-white/10`, `#fda4af` in plugin CSS) were designed for a dark surface.
- Method that worked: a contrast audit in the real window (every theme, every surface, every visible text, effective background stacked from ancestors) failed the build at 4.5:1. Fixes were then systematic: tokens derived from the theme, Tailwind's palette variables remapped for light themes, light-mode overrides for black/white alpha classes, light tints in place of dark tinted panels.
- Parsing colours: read the pixel from a 1x1 canvas. Parsing `getComputedStyle` text fails on `oklch()`/`color-mix()` (Tailwind v4) and reports black on black.
- The audit must skip text that is invisible by design (opacity 0 hover controls) and disabled controls, or it drowns the real findings.
- A CSS variable nobody defines (`--bg-secondary`, used with a dark fallback) is a silent bug on light themes: define aliases in `variables.css`.
- Custom `::-webkit-scrollbar` styling turns macOS overlay bars into permanent ones: apply it only when not on macOS (`html.ir-mac`).

### React error #185 in every plugin drawer that read the open book (SDK `useActiveBook`)
- Symptom: opening the AI persona drawer, the quiz drawer/modal or the Character Knowledge drawer showed "Plugin ... encountered an error: Minified React error #185" (maximum update depth exceeded).
- Cause: `useActiveBook()` returned `useReaderStore((s) => ({ ... }))`. With Zustand 5 on React 19 a selector that builds a new object on every call is a new snapshot every time, so the component re-renders without end.
- Fix: wrap the selector in `useShallow` (`zustand/react/shallow`). Search for the pattern with `grep -rn "Store((s) => ({"`.
- Found and now guarded by `--verify-ui`: the contrast audit opens every plugin drawer and the quiz modal, and fails the run when a plugin error box ("encountered an error:") appears on any surface. A boundary that shows the error makes a crash look like an empty screen to every other check.
- Second bug behind the same drawer: it sent no persona id, the server fell back to the id "default", no such persona exists, and the failure was swallowed by a `console.error`. The drawer now asks `listPersonas` for the book's character and shows send errors in the panel. Rule: a UI call that can fail must show the failure, not only log it.
- Open: `navigateToView` (the quiz's "dedicated page" mode and the registered `ai-study-hub` view) is a no-op in the host, because nothing renders plugin views.

### The library schema is `LibraryDatabase.SchemaSql` + `EnsureColumn`, not `LibraryMigrations` (spec 26)
- `LibraryMigrations.All` is never applied (a comment in `LibraryDatabase.InitializeSchema` says so). A new table goes into `SchemaSql` (`CREATE TABLE IF NOT EXISTS`), a new column into an `EnsureColumn(...)` call. I first wrote a migration and the first test failed with "no such table".

### Gating on a completed chapter: the saved reading position belongs to the CURRENT chapter (spec 26)
- `ReaderContextService.ResolveCurrentPosition` fills any missing override (chapter, spine, viewport token) from `reading_progress`. Asking for an earlier chapter without a viewport token therefore caps that chapter at the *current* chapter's token id. `GetLastCompletedChapterAsync` passes all overrides and `int.MaxValue` as the viewport so the whole completed chapter is returned.

### A model's "evidence" and what the retry note teaches it (spec 26)
- A rejection reason that tells the model to "copy it exactly" was copied as the next evidenceQuote. Retry notes are short statements of what was wrong ("evidenceQuote was not found in the passage"), never instructions in quotable form, and the final error lists every attempt's reason.
- The in-app `--verify-ui` quiz step used the AI companion's offline heuristic (Settings → AI pointed at a server that was not running): its "answer" echoed the first line of the prompt. A check that needs a real model must accept "no usable model" as an outcome and leave the logic to unit tests; the quiz now refuses instead of showing that text.

### Multiple choice: the model picks position 1, so the program shuffles (spec 26)
- Models almost always make option 1 correct. The validator shuffles the options and the correct index is never sent to the client; client checks for "the right option" must accept right or wrong, not a fixed score.

### One rule for the model-server address: `Core.Llm.LlmEndpoint` (llama-server on :8091)
- **Symptom.** Settings → AI → Test connection: `http://localhost:8091/v1` answered 404, `http://localhost:8091` answered 415 (wrong media type).
- **Cause.** Every client read the typed address differently. The tester only replaced `/chat/completions` with `/models`, so the bare host was asked at `GET /` (415) and a `/v1` base at `GET /v1` (404); the companion provider POSTed to the raw typed address (`POST /v1` 404, `POST /` 404) and then hid the HTTP error behind the offline heuristic fallback, so the app looked "working" with nonsense answers. llama-server measured: GET `/v1/models` and `/models` 200, GET `/` 415, `/v1` 404, POST `/chat/completions` and `/v1/chat/completions` 200.
- **Fix.** `LlmEndpoint.ChatCompletionsUrl` / `ModelsUrls` accept host, base or full chat URL (bare host gets `/v1`). Tester, companion provider and the Python `OpenAiClient._base` use it; the deck builder keeps its "an exact non-/v1 URL is never rewritten" rule and only adds `/v1` for a bare host. The tester tries each candidate and lists what it tried ("<url> answered HTTP 404"). The provider retries without `response_format` on a 400 naming it, throws `LlmEndpointException` with the real status and body on any HTTP error, and uses the offline fallback only when the server is unreachable and the setting is on.
- **Prevent.** Never build an LLM URL by hand; `LlmEndpointTests` runs a fake llama-server through every address form.

### Settings → AI of the companion was never read by the companion (persona chat answered the same canned line)
- **Symptom.** Every persona chat message got "うん、いっしょに…を見に行こうね！" and no request reached the server. The AI tab held `http://127.0.0.1:8091/v1` (`.library-data/config/plugins/aiCompanion.json`).
- **Cause.** Two settings stores: the plugin's own panel (`aiCompanion.json`: endpointUrl, apiKey, modelId) and the app-wide `llm` category in `library.db` (default Ollama `:11434`, saved as such). `OpenAiCompatibleLlmProvider` was built only from the app-wide one. Ollama was not running, so the offline heuristic answered, and the persona chat showed it as the character's reply. CharacterKnowledge and DeckBuilder already prefer their own endpoint and fall back to the app-wide one; the companion did not.
- **Fix.** `AiCompanionPlugin.OnEnableAsync` reads `CompanionEndpointSettings` and calls `provider.UseEndpointOverride` (also on config change); the app-wide settings still supply timeout and the fallback switch, and survive an override. Persona chat now throws `LlmEndpointException` when the answer comes from the offline stand-in (`OfflineModelId`), as the quiz already did.
- **Prevent.** A plugin that has its own endpoint fields must feed them to its provider (test: `The_companions_own_endpoint_wins_over_the_app_wide_one…`). Anything an offline stand-in produced must never be shown as a model's answer.

### The narrator of a first-person book became a character called "わたし" (knowledge graph)
- **Symptom.** In the finished くまクマ熊ベア graph the protagonist ユナ (e0010) and an entity named わたし (e0001) were separate; e0001 had 2,392 event roles, 539 relations and wrong aliases (カガリ, 狐). 俺 and わし were characters too.
- **Cause.** The prompts never said what to do with pronouns. The model "created" わたし in each chapter; `_reuse_entity` bound the ref to the first one by name, so it kept growing, and the narrator was never linked to the named character.
- **Fix.** `pronouns.py`, guards in `ops.py`, the `narrator` attribute and snapshot hint, prompt rule 8 (version 2), one retry when an answer still names a character by a pronoun (`builder._without_pronoun_names`). See spec 18, section 11.
- **Prevent.** No guessing: a pronoun is never auto-mapped to the narrator (俺 inside quotes is another person). Existing graphs keep the old entity until rebuilt from where it first appears; pronoun-only characters are no longer offered to the model or accepted as targets.
