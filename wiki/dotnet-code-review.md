# .NET Code Review Log (`immersive-reader/`)

Last review: 2026-11-14. Build: 0 warnings / 0 errors before and after. **203 tests pass** (Core 87, Enrichment 36, Dictionaries 15, App 65).
Scan command (run from `immersive-reader/src`) to spot regressions:
`grep -rEn 'async void|\.Result\b|\.Wait\(\)|new HttpClient|catch *\{ *\}' --include=*.cs .`

## Fixed in this pass

| Where | Problem | Fix |
| :--- | :--- | :--- |
| `ConfigRpcModule.CheckPythonVersion` | `ReadToEnd()` ran *before* `WaitForExit(3000)`, so a hung interpreter blocked forever and the timeout was dead code. `Kill()` swallowed everything. | Read both streams async, then `WaitForExit`; `Kill(entireProcessTree: true)`, catching only `InvalidOperationException` |
| `DictionaryRpcModule.DownloadAndInstallAsync` | New `HttpClient` per dictionary download | `static readonly HttpClient DownloadClient` |
| `ReaderRpcModule` (6 sites) and `StudyPackRpcModule` | Bare `catch { }` hid DB/IO/JSON failures | Narrowed to `SqliteException` / `IOException` / `JsonException`, logged via `Trace.TraceWarning` |
| `ReaderRpcModule.OpenBook` | The "pick first story chapter" lambda chain was duplicated | `FindFirstStoryChapter(IReadOnlyList<BookChapterRecord>)` |

## Open findings (by priority)

1. **`ReaderRpcModule` is a ~750-line god class**: RPC handlers, raw SQL, CSV parsing (`LoadCsvDeckForBook`), dictionary enrichment and kanji queries. Extract a `ReaderInsightsService` (Library or Enrichment) and keep the module as thin param-parsing and dispatch. Move the `reading_progress` and bookmark SQL to a repository beside `BookQueryService`. Keep `RpcContractTests` green, since result keys must not change.
2. **CSV deck parsing uses `line.Split(',')`**, which breaks on quoted commas. Use a small RFC 4180 reader when this code is touched.
3. **No logging abstraction**, only `Trace`. When two or more further places need logs, add `Microsoft.Extensions.Logging` through DI instead of growing ad-hoc `Trace` calls.
4. **Remaining unfiltered `catch` blocks** (about 25): `AppSchemeHandler:155`, `PluginDatabaseContext:102`, `PluginHostManager:267`, `MigrationRunner:99`, `FrontMatterClassifier:103` and others. Audit each. Swallow only if truly best-effort, and say why in a comment.
5. **`JitenMoeService`** builds its own fallback `HttpClient`, and the static `CreateHttpClient()` bakes the API key into default headers at creation (a key change needs a new client). Prefer one injected client and apply the key per request, as `CreateRequest` already does.
6. **Static pass-through wrappers in `JitenMoeService`** (`ParseDeck`, `GetGenreName`, ...) only forward to `JitenResponseParser`. Remove them once callers migrate.
7. **Magic strings**: RPC method names and `"horizontal-tb"` / `"vertical-rl"` are repeated literals. Introduce constants if a typo ever bites.
8. Consider `TreatWarningsAsErrors` and `AnalysisLevel=latest-recommended` while the baseline stays at 0 warnings.

## Reviewed and found fine
No `async void`. No `.Result` / `.Wait()` outside startup and shutdown. SQL is parameterized everywhere inspected. Only `DateTime.UtcNow` is used. No `lock(this)`.

Scope note: this pass scanned the whole `src/` tree with the grep patterns above and read the App RPC modules and `AppServices` closely. Library, Enrichment, Dictionaries and the plugins were scanned by pattern only, not read line by line.
