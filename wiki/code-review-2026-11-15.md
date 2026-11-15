# Code review, 2026-11-15 (whole project, focus on the uncommitted work)

**Nothing was changed in the code during this review.** A whole-series knowledge build was running in the desktop app, so the review only read files, ran the test suites (Release configuration, scratch output) and wrote this page. Every item below is a proposal; none is applied yet.

Baseline verified at the start of the review: `knowledge-graph` 225 tests OK; `enrichement-extractor` 162 OK; .NET 15 + 48 + 222 + 175 = **460** OK (an earlier message said 457; that sum was wrong).

## How much was read

| Area | Depth |
| :--- | :--- |
| `knowledge-graph/` (`llm.py`, `builder.py`, first half of `store.py` and `ops.py`) | read line by line |
| `CharacterKnowledge` plugin (`CharacterKnowledgePlugin.cs`, `KnowledgeBuildRunner.cs`, `SeriesResolver.cs`, first 200 lines of `KnowledgeDb.cs`) | read line by line |
| Deck enrichment (`DeckEnricher.cs`, `OpenAiChatClient.cs`, plugin job code) | read line by line |
| Bulk edit and duplicates (`BookMetadataService` bulk code, `DuplicateService` by pattern) | bulk edit read; duplicates scanned by pattern only |
| `AppSchemeHandler`, `PluginHostManager.OpenPluginAssetStream`, reader iframe, HTML injection | read the relevant parts |
| `enrichement-extractor`, AiCompanion, PosHighlighter, Dictionaries importers, most of the React client, `ReaderRpcModule` | **not read this pass** (pattern scans only, previous review stands) |

Findings marked *(by reading)* were not reproduced with a running program. Findings marked *(measured)* were.

## What changed since the last review (2026-11-14)

All of it is uncommitted (see finding 17).

* **Story pass, spec 22:** events and item holdings as a second builder pass, `replaces` / `replaced_by` links, schema v2 migration, Events and Items views, 225 Python tests.
* **Library bulk edit, spec 23:** `BulkNumbering`, exact preview, select-all-matching, sidebar tag-filter fix (the filter never worked: the client sent `tags`, the server reads `tagIds`).
* **Library duplicates, spec 21**, **Character Knowledge plugin, spec 20**, **Deck Builder plugin and LLM enrichment, spec 16**, JitenMoe split into its own plugin project.
* **Deck enrichment live progress:** the status endpoint now reports the running job's counters; a model answer that contributes nothing adds a warning.

## Findings, by priority

### P1: can lose work, hide data or be exploited

**1. A knowledge base is keyed by the series *name*, so renaming a series in the new bulk edit orphans it** *(by reading)*
`SeriesResolver.KeyForSeries` is `"series-" + SHA1(name.Trim().ToLowerInvariant())`. After a bulk edit that changes the series name the plugin looks in a new, empty folder: the finished graph looks gone, the old folder stays on disk and is never deleted (`DeleteKnowledge` targets the new key). Renumbering volumes flags `OrderChanged` (that part is handled). Do not bulk-edit the series of books whose knowledge is being built right now.
Fix options: remember the key per book in the plugin data (`bookId -> seriesKey`) and reuse it when any volume is found; or warn in the bulk edit dialog when a selected book has a knowledge base; or offer "move knowledge to the new series name".

**2. Chapter HTML from an EPUB is rendered unsandboxed and without a Content-Security-Policy** *(by reading, not exploited)*
`ChapterIframe.tsx` uses `<iframe srcDoc=…>` with no `sandbox` attribute, so the book's HTML runs **same-origin with the app**, which owns the JSON-RPC bridge. Nothing in `ImmersiveReading.Enrichment` strips `<script>` or `on*=` attributes (`EpubUrlRewriter` even rewrites `script src`, which means scripts are kept), and `index.html` has no CSP. A hostile EPUB could call RPC methods (change settings, read the stored API keys, clear the library). For your own books the risk is small; for downloaded ones it is real.
Fix: strip `script`, `iframe`, `object`, `embed`, `on*` attributes and `javascript:` URLs in the composer; add a CSP meta (`script-src` limited to the injected reader script via a nonce); long term, sandbox the iframe (`allow-scripts` without `allow-same-origin`) and talk to the host with `postMessage`. Add a test that feeds a chapter with each vector and checks the output.

**3. Plugin asset path: a rooted second argument escapes the plugin folder** *(by reading)*
`PluginHostManager.OpenPluginAssetStream` does `Path.Combine(externalDir, sanitized)`; `Path.Combine("dir", "C:/x")` returns `C:/x`. The scheme handler rejects `..` but not drive-qualified segments (`ir-app://plugins/<id>/C:/…`), so for an *external* plugin folder any readable file can be served. Only reachable from code running in the app (see finding 2), so it is a defence-in-depth gap, not a remote hole.
Fix: `Path.GetFullPath`, then require the result to start with the plugin folder plus a separator (the helper `IsUnderRoot` already exists in `AppSchemeHandler`); reject segments containing `:`.

**4. One dropped connection aborts a whole-series build** *(by reading)*
`llm.py` `_request`: a refused connection is `fatal=True` and stops the run; a reset or `IncompleteRead` while reading the body (`response.read()` is inside the `try`, but `ConnectionResetError` / `http.client.IncompleteRead` are not `URLError`) is not caught at all and ends the process with a traceback. A local proxy such as FreeToken restarting, or a laptop sleeping, hits exactly this, hours into a run. The build is resumable, but you only notice when you come back.
Fix: treat connection refused, reset, remote disconnect and incomplete read as *retryable* with the existing backoff, and only call it fatal after a longer window (for example 5 minutes of failures); catch `OSError` and `http.client.HTTPException` around `read()`. Add tests with a fake server that drops the connection.

**5. The UI can show "no knowledge" while a build holds the database lock** *(by reading)*
`KnowledgeDb` returns empty results on any `SqliteException`, including "database is locked". The Python side uses the default rollback journal and one long transaction per snapshot generation, so for the duration of `generate_snapshots` at the end of a build (and during each chapter commit) readers time out after 5 s and the plugin reports an empty graph or "not created yet".
Fix: `PRAGMA journal_mode=WAL` and `busy_timeout` in `Store._open` (readers then never block on the writer; the `-wal` / `-shm` files must be handled by the "delete knowledge folder" code, which already deletes the whole directory, and by any backup that copies only `knowledge.db`); in `KnowledgeDb` return a distinct "busy" state instead of an empty one on `SQLITE_BUSY`.

### P2: wrong or slow in realistic use

**6. Enrichment prompt grows without bound and the context limit is not adaptive** *(by reading)*
`DeckEnricher.BuildUserPrompt` sends every known pattern (`KNOWN_PATTERNS`) and every function word with every chunk, so the prompt grows through the book; with a server that really accepts 8,241 tokens, late chunks fail. `OpenAiChatClient` classifies the "prompt too long" HTTP 400 as fatal, so the first oversized chunk ends the run, while the Python client already learns the real limit, shrinks the snapshot and splits the text. The default `LlmMaxChunkChars` is 60,000 and `LlmContextTokens` defaults to 0 (unknown), so the default settings cannot work against such a server.
Fix: port the Python approach (parse "N tokens > M maximum", split the chunk, cap `KNOWN_PATTERNS` to the most frequent or most recent 100), and default `LlmContextTokens` from the first such error.

**7. Enrichment results are saved only when the run ends** *(by reading)*
Cancelling or a failure at chunk 20 of 28 discards everything. Save after each chunk (`EnrichmentStore` partial result keyed by book stamp and chunk index) and resume from the first unfinished chunk, as the knowledge builder does.

**8. `_index_sightings` repeats work for every name and chapter** *(by reading, complexity)*
`builder.py` calls `haystack.casefold()` inside the loop over names and chapters; with N names and C chapters that is N × C casefolds of a whole chapter per newly built chapter. Fine for 27 chapters, painful for a 300-chapter series. Cache the case-folded text next to `_normalized`, and use an index (first occurrence per name) instead of rescanning from chapter 1.

**9. Bulk edit marks every touched book as "metadata edited", even for a tag-only or folder-only change** *(by reading; pre-existing SQL, now much easier to trigger)*
`UPDATE books … is_metadata_edited = 1` runs for each book whatever the patch contains. If the importer or "reset to OPF" treats that flag as "do not refresh", a bulk tag operation silently pins the metadata of thousands of books. Set the flag only when a metadata column really changes (compare in SQL with `WHERE … IS NOT`).

**10. Bulk edit scales badly for "select all N"** *(by reading)*
`PreviewBulkUpdate` runs one `SELECT` per book; `BulkUpdateBooks` rebuilds the full-text row of each book inside one transaction. For 100,000 selected books that is a long write lock that blocks the importer and every other writer. Load all rows in one query (the numbering inputs already are), cap the preview, rebuild FTS only when searchable columns changed, and ask for confirmation above a few thousand books.

**11. `GROUP BY series_name` is case-sensitive, the resolver is not** *(by reading)*
`ListSeriesNames` lists "Saga" and "saga" separately; `SeriesResolver.ResolveForSeries` and the knowledge key treat them as one series. Group by `series_name COLLATE NOCASE` and show one name.

**12. `KnowledgeBuildRunner.Handle` can crash the desktop app** *(by reading)*
It runs on the process's output event (a thread-pool thread) and uses `GetInt32()` on numbers from the builder's JSON. A number that is not an `int32` (a float, a very large count) throws `FormatException`, and an unhandled exception on that thread ends the host process. Use `TryGetInt32` and wrap the handler body in a catch that logs.

**13. `EstimateBuild` and `StartBuild` export the whole book text on the RPC thread** *(by reading)*
Both rebuild the full text JSON of the series each time (estimate is called whenever the panel opens). Cache the export by `(seriesKey, upto, chapter hashes)` and run it off the RPC thread. `StartBuild` also checks "a build is already running" and then starts without a lock; two near-simultaneous calls can both start.

**14. Removing a book or clearing the library leaves plugin data behind** *(by reading)*
`DeleteBookInTransaction` and `ClearLibrary` touch only the library database. The knowledge folder (`volumes.json`, `knowledge.db`), deck enrichment results and any POS cache for that book stay on disk, and a built volume of a series can reference a book that no longer exists. Add a plugin hook (`OnBooksRemoved(ids)`) or at least list orphaned data in settings with a "clean up" button.

### P3: maintainability

**15. Retry state is carried in untyped places.** `DeckLlmException` uses `ex.Data["retryable"|"fatal"]`; `llm.py` sets `error.retryable` dynamically on `LlmError` (not declared in `__init__`). Declare typed fields (`IsRetryable`, `IsFatal`) in both.

**16. Three LLM clients with three copies of the same lessons.** `llm.py`, `DeckBuilder/OpenAiChatClient.cs` and the AiCompanion provider each implement retry, JSON-mode fallback and error text. Only the Python one learns the real context limit. Pull the shared behaviour (request, retry, error classification, limit parsing) into one C# class used by both plugins, and keep the Python and C# test cases in step.

**17. 113 files are uncommitted, and `knowledge-graph/`, both new plugins and specs 15 to 23 are untracked.** One bad `git clean` or disk problem loses the whole body of work, and there is no way to bisect it. Commit in logical steps (library duplicates; library bulk edit; plugin host and JitenMoe split; Deck Builder; Character Knowledge; knowledge-graph; docs). This is the most important housekeeping item. I did not commit anything because you did not ask.

**Boundary audit (same day, after the review):** layering and separation of concerns were checked against project references, `using` namespaces, SQL placement and Python imports. One violation was fixed (`ConfigRpcModule` used a concrete plugin; `LlmEndpointTester` moved to Core). The rules are now enforced by tests; the two remaining debts (SQL in four RPC modules, AI companion tables owned by Library migrations) are allowlisted there with ceilings. See `dotnet-architecture.md` section 1.

**18. Other smaller items**
* `Store.set_chapter_state` / `set_story_state` call `commit()` themselves, which would silently end a surrounding `transaction()`; they are only called outside one today. Make them join the current transaction or document it.
* `CharacterKnowledgePlugin` and `DeckBuilderPlugin` each define their own `Str` / `Int` / `Bool` payload helpers; `Int` uses `GetInt32()` and throws on a non-integer. Share one `RpcParams` helper that uses `TryGetInt32`.
* `AGENTS.md` repeats the "Running Tests" section (second copy is missing the `knowledge-graph` command and starts with a stray fragment). Remove the duplicate.
* `ReaderRpcModule` (764 lines) and `DeckBuilderPlugin` (647 lines) are still the largest classes; the open findings of `dotnet-code-review.md` (CSV parsing, bare `catch` blocks, no logging abstraction) still stand.
* Bare `catch { }` remain in `BookMetadataService.ClearLibrary` (cover deletion), `StudyPackRpcModule:155`, `AtomicFile:21`, `PythonChapterPosTagger` (two `proc.Kill`). All are best effort; add a comment each.

## Possibilities for improvement (not defects)

* **Measure the extraction.** There is no quality yardstick for the story pass or the character pass. Keep 3 to 5 real chapters with a hand-written expected list (events, who took part, which items, who wears what) and a script that scores precision and recall for a given model and prompt. Without it, a prompt change or a different model cannot be judged.
* **Merge duplicates the model creates.** Entities are matched by exact normalised name only. Monsters and items created by the story pass across chapters ("ゴブリン", "ゴブリンたち", "ゴブリンの群れ") become separate entities. A review list of "probably the same" candidates (shared substring, same kind, near in the story) with one-click merge would clean the graph; merging must keep `src_key` semantics so rollback still works.
* **Cost of the second pass.** The story pass doubles the number of requests. A cheap filter (skip chapters with no battle, item or place words, found by the deterministic scanner), or combining both passes for short chapters when the real context allows it, would cut that.
* **Show build health in the panel.** Per chapter: parts, retries, rejected ops with reasons (they are in `ops_log`). The rejection reasons are the fastest way to see whether a prompt or a model is the problem.
* **Visible "as of" safety.** The reader-position gating is correct by construction in the store; add a debug view in the plugin that shows which snapshot row served the current answer, so a spoiler report can be checked in seconds.
* **Series-first identity.** Findings 1, 11 and 14 share a root cause: a series is only a name string. A `series` table (id, name, aliases) would make renames, merges and plugin data cleanup deterministic.
* **Streaming progress for enrichment** (tokens received, current request age), so a slow server is not mistaken for a stuck one.

## Reviewed and found fine

* API keys travel only through the environment (`KNOWLEDGE_API_KEY`), never the command line or the config file; the config loader rejects an `api_key` entry.
* Spoiler gating: the story pass reads the gate as of the end of the chapter it is processing, valid holdings are returned open (no future end), cards naming unmet entities are blocked by the scanner; tests cover each.
* Every model operation is validated (verbatim evidence, known ids, `Reject` reasons stored); a failed or truncated answer is never applied; rollback of one pass does not touch the other.
* All SQL read in the bulk edit code and in the duplicate service's helpers is parameterised; table names formatted into SQL in `KnowledgeDb` are constants.
* Duplicate removal deletes library entries only, never EPUB files.
* `AppSchemeHandler` rejects `..` after unescaping and checks roots with a trailing separator.
