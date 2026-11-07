# 06 — Junior Developer Implementation Plan & Step-by-Step Task Backlog (Phases 2–5)

> **Audience:** Junior .NET / Full-Stack Developers  
> **Prerequisites Completed (Phase 1):**  
> - Solution [`ImmersiveReading.sln`](../immersive-reader/ImmersiveReading.sln) (`net10.0`) is located under [`immersive-reader/`](../immersive-reader) and passing all test suites.  
> - [`ImmersiveReading.Core`](../immersive-reader/src/ImmersiveReading.Core) has [`BookIdentity`](../immersive-reader/src/ImmersiveReading.Core/Epub/BookIdentity.cs), [`FilenameMetadataParser`](../immersive-reader/src/ImmersiveReading.Core/Epub/FilenameMetadataParser.cs), [`EpubMetadataExtractor`](../immersive-reader/src/ImmersiveReading.Core/Epub/EpubMetadataExtractor.cs), [`LibraryModels`](../immersive-reader/src/ImmersiveReading.Core/Library/LibraryModels.cs), [`LanguagePluginContracts`](../immersive-reader/src/ImmersiveReading.Core/Plugins/LanguagePluginContracts.cs), and [`WhitespaceLanguagePlugin`](../immersive-reader/src/ImmersiveReading.Core/Plugins/WhitespaceLanguagePlugin.cs).  
> - [`ImmersiveReading.Library`](../immersive-reader/src/ImmersiveReading.Library) has [`LibraryDatabase`](../immersive-reader/src/ImmersiveReading.Library/LibraryDatabase.cs) and [`LibraryManager`](../immersive-reader/src/ImmersiveReading.Library/LibraryManager.cs) supporting 24,000+ books, nested folders/series, manual tags, and multi-level custom fields with inheritance.  
> - [`ImmersiveReading.App`](../immersive-reader/src/ImmersiveReading.App) runs the Photino.NET Library Manager desktop app.

---

## 0. Golden Rules for Junior Developers (Read Before Coding!)

1. **Every Phase Must End with a Runnable Desktop App**:
   - Never leave `ImmersiveReading.App` broken. After finishing each task, run:
     ```powershell
     dotnet test k:\immersive-reading\immersive-reader\ImmersiveReading.sln
     dotnet run --project k:\immersive-reading\immersive-reader\src\ImmersiveReading.App -- --smoke-test
     ```
2. **Never Modify Source `.epub` Files on Disk**:
   - Always open `.epub` files with `FileMode.Open, FileAccess.Read, FileShare.Read`. All enrichment happens in memory before serving over `ir-book://`.
3. **Never Insert Whitespace Between CJK Token Spans**:
   - In Japanese/Chinese text, `<span class="ir-tok" data-tid="0">食</span><span class="ir-tok" data-tid="1">べた</span>` must have **zero spaces or newlines** between adjacent `<span>` tags. Inserting a newline or space causes the browser to render an unwanted space inside sentences!
4. **Author EPUB `<ruby>` Always Beats Tokenizer Guesses**:
   - If a book author wrote `<ruby>人気<rt>ひとけ</rt></ruby>`, the reading is `ひとけ` (`RubySource.EpubAuthor`), **not** MeCab's default `にんき`. Never tokenize inside `<rt>` or `<rp>` tags!
5. **Follow the Gherkin Acceptance Criteria (`specs/features/*.feature`)**:
   - Every task below links to the exact Gherkin scenarios in [`specs/features/`](features) that your xUnit test must verify.

---

## PHASE 2: Interactive EPUB Reader + Pre-Render AngleSharp & MeCab Enrichment

**Phase 2 Goal:** Double-clicking or clicking **"Read Book"** on any book in the Library opens the built-in Photino EPUB Reader (`ir-book://`), which intercepts chapter XHTML files, runs `AngleSharp` + `MeCab.DotNet` pre-render enrichment, and displays the chapter in `vertical-rl` or `horizontal-tb` layout with live furigana toggles and saved progress.

**Gherkin Specs Covered:**
- [03-prerender-anglesharp-enrichment-and-plugins.feature](features/03-prerender-anglesharp-enrichment-and-plugins.feature)
- [05-photino-reader-and-study-pack-bridge.feature](features/05-photino-reader-and-study-pack-bridge.feature) (Scenarios 1–4)

---

### `TASK-2.1`: Implement Kana Normalization & `OkuriganaRubyAligner`
- **Target Project:** `src/Plugins/ImmersiveReading.Plugins.Japanese`
- **Files to Create:**
  1. `src/Plugins/ImmersiveReading.Plugins.Japanese/KanaNormalizer.cs`
  2. `src/Plugins/ImmersiveReading.Plugins.Japanese/OkuriganaRubyAligner.cs`
- **Step-by-Step Instructions:**
  1. In `KanaNormalizer.cs`:
     - Write `public static string ToHiragana(ReadOnlySpan<char> input)`:
       - Loop over each `char c` in `input`.
       - If `c` is Katakana (`c >= '\u30A1' && c <= '\u30F6'`), convert it to Hiragana by subtracting `0x60`: `(char)(c - 0x60)`.
       - Copy all other characters unchanged.
     - Write `public static bool IsKanji(Rune r)`:
       - Return `true` if `r.Value` is in CJK Unified Ideographs (`0x3400..0x9FFF`, `0xF900..0xFAFF`, `0x20000..0x323AF`) or is `々` (`0x3005`), `〆` (`0x3006`), `〇` (`0x3007`).
  2. In `OkuriganaRubyAligner.cs` (implements [`IRubyAligner`](../immersive-reader/src/ImmersiveReading.Core/Plugins/LanguagePluginContracts.cs#L95-L101)):
     - Port the exact algorithm from [`enrichement-extractor/immersive/furigana.py`](../enrichement-extractor/immersive/furigana.py#L18-L51):
     - **Case A**: If `overlappingAuthorRubies` is non-empty, emit `RubyUnit` slices using the author's `SourceRubySpan` (`RubySource.EpubAuthor`) and plain text for any remaining okurigana.
     - **Case B**: If `surface` contains no kanji (`!surface.EnumerateRunes().Any(KanaNormalizer.IsKanji)`), return a single plain unit `[new RubyUnit(0, surface.Length, surface.ToString(), null, RubySource.None)]`.
     - **Case C**: Split `surface` into alternating segments of **Kanji runs** and **Kana runs** (e.g., `振り返る` $\rightarrow$ `["振" (Kanji), "り" (Kana), "返" (Kanji), "る" (Kana)]`).
     - Match the Hiragana-normalized Kana runs against `KanaNormalizer.ToHiragana(reading)` from right-to-left / recursively so each Kanji stem gets its exact reading slice (`振` $\rightarrow$ `ふ`, `り` $\rightarrow$ `null`, `返` $\rightarrow$ `かえ`, `る` $\rightarrow$ `null`).
     - If a word has an irregular jukujikun reading where internal kana cannot align (e.g. `大人` $\rightarrow$ `おとな`), return a single whole-word ruby unit `[new RubyUnit(0, surface.Length, surface.ToString(), hiraReading, RubySource.PluginGenerated)]`.
- **Definition of Done (xUnit Test):**
  - In `tests/ImmersiveReading.Enrichment.Tests/JapanesePluginTests.cs`, test:
    - `食べた` + `たべた` $\rightarrow$ `[Ruby("食", "た"), Plain("べた")]`
    - `振り返る` + `ふりかえる` $\rightarrow$ `[Ruby("振", "ふ"), Plain("り"), Ruby("返", "かえ"), Plain("る")]`
    - `大人` + `おとな` $\rightarrow$ `[Ruby("大人", "おとな")]`

---

### `TASK-2.2`: Implement `MeCabTokenizerAdapter` & `JapaneseLanguagePlugin`
- **Target Project:** `src/Plugins/ImmersiveReading.Plugins.Japanese`
- **Files to Create:**
  1. `src/Plugins/ImmersiveReading.Plugins.Japanese/MeCabTokenizerAdapter.cs`
  2. `src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseLanguagePlugin.cs`
- **Step-by-Step Instructions:**
  1. In `MeCabTokenizerAdapter.cs` (implements [`ITextTokenizer`](../immersive-reader/src/ImmersiveReading.Core/Plugins/LanguagePluginContracts.cs#L82-L87)):
     - Create a `MeCab.MeCabTagger` instance via `MeCabTagger.Create(new MeCabParam())`. *(Note: `MeCab.DotNet` bundles the default IPADic dictionary automatically when no custom dictionary path is needed!)*
     - In `TokenizeParagraph(ReadOnlySpan<char> paragraphText, IReadOnlyList<SourceRubySpan> authorRubies)`:
       - Call `_tagger.ParseToNodes(paragraphText.ToString())`.
       - Track `int currentOffset = 0;` across `paragraphText`.
       - For each `node` where `node.CharType > 0` (skip BOS/EOS nodes where `node.Stat` is `MeCabNodeStat.Bos` or `MeCabNodeStat.Eos`):
         - Advance `currentOffset` to match `node.Surface` in `paragraphText`.
         - Split `node.Feature` by `','`:
           - `features[0]` = `PartOfSpeech` (e.g., `"名詞"`, `"動詞"`, `"形容詞"`, `"助詞"`, `"助動詞"`, `"記号"`)
           - `features[4]` = `ConjugationType` (e.g., `"一段"`, `"五段・カ行イ音便"`)
           - `features[5]` = `ConjugationForm` (e.g., `"連用タ接続"`, `"基本形"`)
           - `features[6]` = `Lemma` (if `"*"` or missing, use `node.Surface`)
           - `features[7]` = `Reading` in Katakana (convert via `KanaNormalizer.ToHiragana`; if missing, use `KanaNormalizer.ToHiragana(node.Surface)`)
         - **Compound Inflection Merging Rule**:
           - If the current token is a verb (`動詞`) or i-adjective (`形容詞`), and the immediately following MeCab node(s) are bound inflectional auxiliaries/particles (`助動詞` like `た`, `だ`, `ない`, `れる`, `られる`, `せる`, `させる`, `たい`, `ます`, or conjunctive `助詞` `て`/`で`), merge their `Surface` and `Reading` into the verb/adjective token while keeping the root verb's `Lemma` and `DictionaryReading`! (This ensures `食べた` is one cohesive token `<span class="ir-tok">食<rt>た</rt>べた</span>` with `Lemma = "食べる"`).
         - **Author `<ruby>` Priority Rule**:
           - Check if any `SourceRubySpan` in `authorRubies` overlaps `[startOffset .. endOffset]`.
           - If an author ruby covers the token, set `Reading = KanaNormalizer.ToHiragana(authorRuby.Reading)` and `ReadingSource = RubySource.EpubAuthor`.
           - Otherwise, if the token contains Kanji, set `ReadingSource = RubySource.PluginGenerated`; else `RubySource.None`.
  2. In `JapaneseLanguagePlugin.cs` (implements [`ILanguagePlugin`](../immersive-reader/src/ImmersiveReading.Core/Plugins/LanguagePluginContracts.cs#L103-L116)):
     - Set `LanguageCode => "ja"`, `DisplayName => "Japanese (MeCab.DotNet)"`.
     - Return `new MeCabTokenizerAdapter()` from `CreateTokenizer()` and `new OkuriganaRubyAligner()` from `GetRubyAligner()`.
- **Definition of Done (xUnit Test):**
  - Verify tokenizing `"人気のない森で、木の実を食べた。"` with `authorRubies: [new SourceRubySpan(0, 2, "人気", "ひとけ")]` produces a token for `"人気"` with `Reading == "ひとけ"` and `ReadingSource == RubySource.EpubAuthor`, and a token for `"食べた"` with `Lemma == "食べる"`.

---

### `TASK-2.3`: Implement `AngleSharpDomEnricher` & `ChapterEnrichmentCache`
- **Target Project:** `src/ImmersiveReading.Enrichment`
- **Files to Create:**
  1. `src/ImmersiveReading.Enrichment/ChapterTokenMap.cs`
  2. `src/ImmersiveReading.Enrichment/AngleSharpDomEnricher.cs`
  3. `src/ImmersiveReading.Enrichment/ChapterEnrichmentCache.cs`
- **Step-by-Step Instructions:**
  1. In `ChapterTokenMap.cs`:
     - Define `EnrichedTokenEntry(int TokenId, int ParagraphId, AnalyzedToken Token, IReadOnlyList<RubyUnit> RubyUnits, WordStatus Status, bool InBookDeck)`.
     - Define `ChapterTokenMap(string BookId, string ChapterHref, IReadOnlyList<EnrichedTokenEntry> Tokens, IReadOnlyList<string> ParagraphTexts)` with $O(1)$ lookup `GetToken(int tokenId)`.
  2. In `AngleSharpDomEnricher.cs`:
     - Parse the input chapter XHTML string using `AngleSharp.Html.Parser.HtmlParser`.
     - **Inject Reader Head Assets**:
       - Find `<head>` (or create if missing) and append:
         - `<link rel="stylesheet" href="ir-app://assets/ir-reader-injected.css" />`
         - `<script src="ir-app://assets/ir-reader-bridge.js" defer="defer"></script>`
       - Add furigana mode class onto `<html>` (`document.DocumentElement`), e.g.:
         - `FuriganaMode.AllKanji` $\rightarrow$ `ir-furigana-all`
         - `FuriganaMode.AuthorOnly` $\rightarrow$ `ir-furigana-author`
         - `FuriganaMode.UnknownAndLearningOnly` $\rightarrow$ `ir-furigana-unknown`
         - `FuriganaMode.Hover` $\rightarrow$ `ir-furigana-hover`
         - `FuriganaMode.Off` $\rightarrow$ `ir-furigana-off`
     - **Collect Block Elements**:
       - Find leaf block elements (`p`, `h1`..`h6`, `li`, `blockquote`, or `div` that contains direct text/ruby children).
       - **Never** descend into `<script>`, `<style>`, `<svg>`, `<math>`, `<rt>`, `<rp>`, or `<head>`.
     - **For each Block Element**:
       - Walk its descendant nodes to build:
         1. `StringBuilder blockText` (the ruby-free base text of the block).
         2. `List<DomSegment> segments` where each segment is either:
            - `PlainTextSegment(IText TextNode, int StartOffset, int EndOffset)`
            - `AtomicRubySegment(IElement RubyElement, int StartOffset, int EndOffset, SourceRubySpan RubySpan)`
       - Call `tokenizer.TokenizeParagraph(blockText.ToString(), authorRubySpans)`.
       - Mutate the DOM segments in reverse or via replacement document fragments:
         - For an `AtomicRubySegment` matching a token: add `class="ir-tok"` and `data-tid="{tokenId}"` onto the `<ruby>` (or wrap it in `<span class="ir-tok" data-tid="{tokenId}">`) **without altering its internal `<rb>`/`<rt>` children**.
         - For a `PlainTextSegment`: slice the text according to overlapping `AnalyzedToken` boundaries. Wrap each token slice in `<span class="ir-tok" data-tid="{tokenId}">`. Inside the span, if `rubyAligner` produces `RubyUnit`s with `Reading != null`, create `<ruby class="ir-ruby-gen">{baseText}<rt>{reading}</rt></ruby>`; otherwise append plain text.
         - If a single token spans across multiple `PlainTextSegment`s (e.g. `<em>人</em>気`), give each slice the **same** `data-tid="{tokenId}"` plus `data-part="0"`, `data-part="1"`, etc.
     - Return `(string EnrichedHtml, ChapterTokenMap TokenMap)`.
  3. In `ChapterEnrichmentCache.cs`:
     - Use a thread-safe LRU/dictionary cache keyed by `(string BookId, string ChapterHref)` storing the `ChapterTokenMap` (up to e.g. 20 recent chapters in memory).
- **Definition of Done (xUnit Test):**
  - All 5 scenarios in [03-prerender-anglesharp-enrichment-and-plugins.feature](features/03-prerender-anglesharp-enrichment-and-plugins.feature) pass in `tests/ImmersiveReading.Enrichment.Tests/AngleSharpDomEnricherTests.cs`.

---

### `TASK-2.4`: Implement `ir-book://` Custom Scheme Handler in `ImmersiveReading.App`
- **Target Project:** `src/ImmersiveReading.App`
- **Files to Create:**
  1. `src/ImmersiveReading.App/SchemeHandlers/BookSchemeHandler.cs`
- **Step-by-Step Instructions:**
  1. Parse incoming URLs of the form `ir-book://{bookId}/{chapterOrAssetPath}`.
  2. **Security Check (Path Traversal Guard)**:
     - Unescape the path and reject any request where the raw path contains `".."` segments that escape the root, returning an empty `MemoryStream([])` (satisfies Scenario 3 of [05-photino-reader-and-study-pack-bridge.feature](features/05-photino-reader-and-study-pack-bridge.feature)).
  3. Look up `bookId` via `LibraryManager.GetBookDetail(bookId)` to find its `.epub` disk path (`Path.Combine(FolderPath, EpubFileName)`).
  4. Open the `.epub` archive (`ZipArchiveMode.Read`) and locate the entry using `EpubMetadataExtractor.GetEntryNormalized(archive, relativePath)`.
  5. **If the entry is an XHTML/HTML chapter (`.xhtml`, `.html`, `.htm`)**:
     - Read the UTF-8 XHTML string.
     - Resolve the book's language plugin via `LanguagePluginRegistry.Resolve(book.Language)`.
     - Run `AngleSharpDomEnricher.EnrichChapter(...)`, store the `ChapterTokenMap` in `ChapterEnrichmentCache`, and return the enriched UTF-8 bytes with `contentType = "text/html; charset=utf-8"` (or `"application/xhtml+xml; charset=utf-8"`).
  6. **If the entry is a relative asset (`.jpg`, `.jpeg`, `.png`, `.webp`, `.gif`, `.svg`, `.css`, `.woff`, `.woff2`, `.ttf`, `.otf`)**:
     - Copy the entry stream to a `MemoryStream` and return it with the matching MIME type!

---

### `TASK-2.5`: Implement Reader RPC Methods & Injected Reader Bridge (`ir-reader-injected.css` & `ir-reader-bridge.js`)
- **Target Project:** `src/ImmersiveReading.App`
- **Files to Create / Update:**
  1. Create `src/ImmersiveReading.App/wwwroot/assets/ir-reader-injected.css`
  2. Create `src/ImmersiveReading.App/wwwroot/assets/ir-reader-bridge.js`
  3. Update `src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs`
  4. Update `src/ImmersiveReading.App/wwwroot/index.html` and `app.js`
- **Step-by-Step Instructions:**
  1. In `ir-reader-injected.css`:
     - Add the zero-reparse furigana visibility rules from [04-photino-reader-ui-and-ipc-spec.md](04-photino-reader-ui-and-ipc-spec.md#L127-L157) (`html.ir-furigana-off`, `html.ir-furigana-hover`, `html.ir-furigana-author`, `html.ir-furigana-unknown`) and hover styling for `.ir-tok:hover` (`background: rgba(59, 130, 246, 0.18); border-radius: 2px; cursor: pointer;`).
     - Add vertical typography rules (`html.ir-mode-vertical body { writing-mode: vertical-rl; text-orientation: mixed; }`).
  2. In `ir-reader-bridge.js`:
     - Listen for `mouseover` / `click` on `.ir-tok[data-tid]`. When a token has `data-tid`, highlight all spans with the same `data-tid` (`document.querySelectorAll('.ir-tok[data-tid="' + tid + '"]')`) and post `{ type: 'ir-token-click', tokenId: parseInt(tid, 10), rect: el.getBoundingClientRect() }` to `window.parent.postMessage(...)`.
     - Listen for messages from the parent Reader toolbar (`{ type: 'ir-set-furigana-mode', mode: '...' }` or `{ type: 'ir-set-writing-mode', mode: '...' }`) to toggle classes on `document.documentElement` instantly without reloading!
  3. In `LibraryRpcDispatcher.cs`:
     - Add `reader.openBook` (returns `BookDetail`, saved `ReadingProgress`, and initial chapter URL `ir-book://{bookId}/{spineHref}`).
     - Add `reader.saveProgress` (upserts `reading_progress` in `library.db` and updates `books.last_opened_at_utc`).
  4. In `index.html` and `app.js`:
     - Add a **"📖 Read Book"** button in the Inspector and Table rows that opens the full-screen **Reader View** containing:
       - Top Reader Toolbar: `[← Back to Library]`, Chapter selector dropdown, `[◀ Prev Chapter] [Next Chapter ▶]`, Writing Mode toggle (`Vertical 縦書き` / `Horizontal 横書き`), and Furigana Mode selector (`All Kanji`, `Author Only`, `Hover to Reveal`, `Off`).
       - `<iframe id="reader-frame" src="ir-book://..."></iframe>`.

---

## PHASE 3: Instant `< 5 ms` On-the-Fly Dictionary Lookup & Word Mining

**Phase 3 Goal:** Hovering or clicking any `.ir-tok` word in the Reader opens a `< 5 ms` Dictionary Popover showing Yomitan definitions, deinflection rule chains (`食べさせられた ← 食べる`), author `<ruby>` homograph verification, KanjiVG stroke-order diagrams, and `1/2/3/4` vocabulary status buttons (`New`, `Learning`, `Known`, `Ignored`).

**Gherkin Specs Covered:**
- [04-onthefly-dictionary-lookup-and-vocab.feature](features/04-onthefly-dictionary-lookup-and-vocab.feature)

---

### `TASK-3.1`: Implement `JapaneseDeinflector`
- **Target Project:** `src/Plugins/ImmersiveReading.Plugins.Japanese`
- **Files to Create:**
  1. `src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseDeinflector.cs`
- **Step-by-Step Instructions:**
  1. Define a rule record `DeinflectionRule(string InflectedSuffix, string DictionarySuffix, string RuleName, string[] FromPosTags, string[] ToPosTags)`.
  2. Populate the rule table covering:
     - Polite forms (`ます`, `ました`, `ません`, `ましょう` $\rightarrow$ stem)
     - Past & Te-forms:
       - Ichidan (`v1`): `た` $\rightarrow$ `る`, `て` $\rightarrow$ `る`, `なかった` $\rightarrow$ `る`, `ない` $\rightarrow$ `る`
       - Godan (`v5`):
         - `った` / `って` $\rightarrow$ `う`, `つ`, `る` (plus irregular `行った` / `行って` $\rightarrow$ `行く`)
         - `いた` / `いて` $\rightarrow$ `く`
         - `いだ` / `いで` $\rightarrow$ `ぐ`
         - `した` / `して` $\rightarrow$ `す`
         - `んだ` / `んで` $\rightarrow$ `ぬ`, `ぶ`, `む`
     - Passive / Potential (`られる` $\rightarrow$ `る`, `れる` $\rightarrow$ `う/く/ぐ/す/つ/ぬ/ぶ/む/る`)
     - Causative & Causative-Passive (`させる` $\rightarrow$ `る`, `させられる` $\rightarrow$ `る`, `せる` / `せられる` / `される` $\rightarrow$ Godan endings)
     - Colloquial contractions (`ちゃう` / `ちゃった` $\rightarrow$ `て`, `じゃう` / `じゃった` $\rightarrow$ `で`, `てる` / `てた` $\rightarrow$ `て`)
     - I-Adjectives (`adj-i`): `かった` $\rightarrow$ `い`, `くない` $\rightarrow$ `い`, `くなかった` $\rightarrow$ `い`, `くて` $\rightarrow$ `い`, `さ` $\rightarrow$ `い`.
  3. Implement breadth-first / iterative suffix replacement up to depth 4 so chained inflections like `食べさせられた` produce `食べる` with `RuleChain: ["past", "passive", "causative"]`.
- **Definition of Done (xUnit Test):**
  - Verify all 4 examples in Scenario Outline 1 of [04-onthefly-dictionary-lookup-and-vocab.feature](features/04-onthefly-dictionary-lookup-and-vocab.feature) (`食べさせられた` $\rightarrow$ `食べる`, `行かなかった` $\rightarrow$ `行く`, `美しくなかった` $\rightarrow$ `美しい`, `読んじゃった` $\rightarrow$ `読む`).

---

### `TASK-3.2`: Create `ImmersiveReading.Dictionaries` & SQLite Schema (`dictionaries.db`)
- **Target Project:** Create `src/ImmersiveReading.Dictionaries/ImmersiveReading.Dictionaries.csproj`
- **Files to Create:**
  1. `src/ImmersiveReading.Dictionaries/DictionaryDatabase.cs`
- **Step-by-Step Instructions:**
  1. Create the class library and add references to `ImmersiveReading.Core` and `Microsoft.Data.Sqlite`.
  2. In `DictionaryDatabase.cs`, execute the exact SQL schema from [03-onthefly-dictionary-lookup-spec.md](03-onthefly-dictionary-lookup-spec.md#L29-L95) (`dictionary_sources`, `dictionary_terms` with `idx_terms_expr`, `idx_terms_read_norm`, `idx_terms_expr_read`, `dictionary_term_meta`, and `dictionary_kanji`).

---

### `TASK-3.3`: Implement `YomitanSqliteImporter` & `KanjidicImporter`
- **Target Project:** `src/ImmersiveReading.Dictionaries`
- **Files to Create:**
  1. `src/ImmersiveReading.Dictionaries/YomitanSqliteImporter.cs`
  2. `src/ImmersiveReading.Dictionaries/KanjidicImporter.cs`
- **Step-by-Step Instructions:**
  1. In `YomitanSqliteImporter.cs` (ports [`enrichement-extractor/immersive/yomitan.py`](../enrichement-extractor/immersive/yomitan.py)):
     - Open the Yomitan `.zip` archive, parse `index.json`, verify `format == 3` (or `version == 3`).
     - Stream each `term_bank_*.json` file: each row is a JSON array `[expression, reading, definitionTags, rules, score, glossaryArray, sequence, termTags]`.
     - Normalize `reading` (if empty, use `expression`) to Hiragana (`reading_norm`) and batch-insert into `dictionary_terms` inside a SQLite transaction (5,000 rows per transaction).
     - Stream each `term_meta_bank_*.json` (`[expression, mode, data]`) into `dictionary_term_meta`.
  2. In `KanjidicImporter.cs`:
     - Read `enrichement-extractor/data/kanjidic2.xml.gz` (`GZipStream` + `XDocument`), `data/kanji-alive/ka_data.csv`, and `data/kanjivg/source.zip` (extracting `<path d="..."/>` stroke strings), and upsert into `dictionary_kanji`.

---

### `TASK-3.4`: Implement Sub-5ms `DictionaryLookupEngine` & `VocabularyTracker`
- **Target Project:** `src/ImmersiveReading.Dictionaries` & `src/ImmersiveReading.Library`
- **Files to Create:**
  1. `src/ImmersiveReading.Dictionaries/DictionaryLookupEngine.cs`
  2. `src/ImmersiveReading.Library/VocabularyTracker.cs`
- **Step-by-Step Instructions:**
  1. In `DictionaryLookupEngine.cs`:
     - Given `LookupQuery(bookId, chapterHref, tokenId, charOffsetInToken)`:
     - Fetch `ChapterTokenMap` from `ChapterEnrichmentCache`.
     - Collect tokens starting at `tokenId` up to **16 Unicode characters** in the same paragraph.
     - Build candidate search prefixes that **end strictly on a token boundary** (so `気` + `に` + `ならなかった` generates prefixes `"気"`, `"気に"`, `"気にならなかった"`).
     - For each prefix, include the surface itself, the MeCab `Lemma`, and all `Deinflector.Deinflect(prefix)` candidates.
     - Query `dictionary_terms` with a single parameterized SQL statement:
       ```sql
       SELECT t.*, s.title AS dict_title, s.priority_order
       FROM dictionary_terms t
       JOIN dictionary_sources s ON s.dictionary_id = t.dictionary_id
       WHERE s.enabled = 1 AND (t.expression IN (...) OR t.reading_norm IN (...))
       ```
     - **Filter & Rank Candidates**:
       1. If the tapped token has `ReadingSource == RubySource.EpubAuthor` (e.g., `<ruby>人気<rt>ひとけ</rt></ruby>`), rank entries with `reading_norm == "ひとけ"` at Tier 1 (`ReadingMatchStatus = "epub_ruby_verified"`) above `"にんき"`.
       2. Otherwise rank by: **(a) Longest matched token span in the text** (`気になる` before `気`), **(b) Exact MeCab lemma + reading match**, **(c) Valid deinflection rule match**, **(d) Dictionary `priority_order ASC, score DESC`**.
     - Query `dictionary_kanji` for each unique kanji character in the top matched word.
  2. In `VocabularyTracker.cs`:
     - Implement `GetStatus(language, lemma, reading)` and `SetStatus(language, lemma, reading, WordStatus, customGloss)` against the `vocabulary_state` table in `library.db`.

---

### `TASK-3.5`: Build the Interactive Dictionary Popover UI in `ImmersiveReading.App`
- **Target Project:** `src/ImmersiveReading.App`
- **Files to Update:** `LibraryRpcDispatcher.cs`, `index.html`, `app.js`, `styles.css`
- **Step-by-Step Instructions:**
  1. Wire RPC methods `lookup.query`, `vocab.setStatus`, `dictionaries.list`, and `dictionaries.importYomitan`.
  2. When `ir-reader-bridge.js` posts `ir-token-click`, call `rpcCall('lookup.query', { bookId, chapterHref, tokenId })` and position the Popover card near `rect`.
  3. Render:
     - **Words Tab**: Headword, Reading, `Matches Book Ruby` badge, Inflection chain chip (`食べた ← 食べる`), Status buttons `[1: New] [2: Learning] [3: Known] [4: Ignored]`, and Dictionary senses.
     - **Kanji Tab**: Kanji cards with ON/KUN readings, meanings, and SVG stroke-order diagrams rendered from `kanjivg_strokes_json`.
  4. When the user clicks a Status button (e.g. `Known`), call `vocab.setStatus` and post a message to the reader `<iframe>` to update `.ir-st-known` on all matching tokens immediately without reloading the chapter!

---

## PHASE 4: Study Pack Bridge (`enrichement-extractor`) & Flashcard Export

**Phase 4 Goal:** Connect the .NET Library Manager and Reader with the Python `enrichement-extractor` pipeline so users can view/generate printable A4 PDFs (`kanji-master.pdf`, `vocabulary-master.pdf`, `grammar-master.pdf`), browse 20-word chapter learning sets, and mine 1-click sentence flashcards.

**Gherkin Specs Covered:**
- [05-photino-reader-and-study-pack-bridge.feature](features/05-photino-reader-and-study-pack-bridge.feature) (Scenario 5)

---

### `TASK-4.1`: Implement `PythonStudyPackBridge` & Snapshot Lease Manager
- **Target Project:** `src/ImmersiveReading.Library`
- **Files to Create:**
  1. `src/ImmersiveReading.Library/PythonStudyPackBridge.cs`
- **Step-by-Step Instructions:**
  1. Inspect `enrichement-extractor/output/.publication/active.json` (or `output/data/{bookId}/reader.json`).
  2. When reading a published generation under `.publication/releases/<generation>/`, create a lease file `.publication/leases/<generation>/{Guid.NewGuid()}.lock` containing `{ "pid": Environment.ProcessId, "created_at": DateTimeOffset.UtcNow.ToUnixTimeSeconds() }` so `immersive.retention` knows the snapshot is in use, and delete the lease file on `Dispose()`.
  3. Discover available per-book artifacts:
     - Master PDFs (`output/pdf/{bookId}/kanji-master.pdf`, `vocabulary-master.pdf`, `grammar-master.pdf`)
     - Chapter PDFs (`output/pdf/{bookId}/chapter-XX-kanji.pdf`, `chapter-XX-vocabulary.pdf`)
     - 20-Word Vocabulary Sets (`output/data/{bookId}/vocabulary-sets.json` or inside `reader.json`)
  4. Implement `BuildStudyPackAsync(string bookFolderPath, bool buildPdfs, bool buildCards, CancellationToken ct)`:
     - Spawn `enrichement-extractor\.venv\Scripts\python.exe -m immersive.cli build --book "<bookFolderPath>"` via `System.Diagnostics.ProcessStartInfo` (`UseShellExecute = false`, `CreateNoWindow = true`), streaming stdout/stderr progress lines to the Photino UI.

---

### `TASK-4.2`: Implement 1-Click Sentence Flashcard Exporter (`FlashcardMiningService`)
- **Target Project:** `src/ImmersiveReading.Library`
- **Files to Create:**
  1. `src/ImmersiveReading.Library/FlashcardMiningService.cs`
- **Step-by-Step Instructions:**
  1. When the user clicks **"+ Add Flashcard"** in the Dictionary Popover on token `tokenId`:
     - Extract the containing sentence from `ChapterTokenMap` (with furigana `RubyUnit`s and target word bolded) **without translating the story sentence** (preserving Hard Invariant #2!).
     - Append the card to `output/data/flashcards/library.json` (and optional Anki `.tsv` export file) using atomic temp-file + `File.Move(..., overwrite: true)`.

---

## PHASE 5: Extensible Feature Plugins, AI Comprehension Quizzes & $n+1$ Persona Chat

**Phase 5 Goal:** Enable modular feature extensions (`IFeaturePlugin`) and build the AI Companion plugin (`ImmersiveReading.Plugins.AiCompanion`) providing:
1. **Progressive Reading Comprehension Quizzes** strictly from the passage just read (**Multiple-Choice (MCQ)** $\rightarrow$ **Guided Short Answer** $\rightarrow$ **Free-Form Answers**).
2. **Spoiler-Safe $n+1$ Vocabulary-Constrained Book Persona Chat**, verified by `MeCab.DotNet` and enriched by `AngleSharp` so every word in the AI chat bubble has furigana, $+1$ badges, and `< 5 ms` hover dictionary lookup.

**Gherkin Specs Covered:**
- [06-ai-comprehension-and-n-plus-one-persona-chat.feature](features/06-ai-comprehension-and-n-plus-one-persona-chat.feature)

---

### `TASK-5.1`: Implement Core Extension Contracts & Zero-Spoiler `ReaderContextService`
- **Target Project:** `src/ImmersiveReading.Core` & `src/ImmersiveReading.Enrichment`
- **Files to Create:**
  1. `src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs` (interfaces from [05-extensibility-and-ai-companion-spec.md](05-extensibility-and-ai-companion-spec.md#L33-L120))
  2. `src/ImmersiveReading.Enrichment/ReaderContextService.cs`
  3. `src/ImmersiveReading.Enrichment/SnippetEnricher.cs`
- **Step-by-Step Instructions:**
  1. In `ReaderContextService.cs`:
     - Implement `GetJustReadPassageAsync(string bookId, int maxCharacters = 3500)`:
       - Read the user's current `reading_progress` (`current_chapter_index`, `current_spine_href`, `locator_token_id`) from `library.db`.
       - Load the `ChapterTokenMap` for `(bookId, current_spine_href)`.
       - **Hard Spoiler Gate**: Slice tokens strictly where `token.TokenId <= locator_token_id` (and `>= previousCheckpointTokenId`), concatenating at most `maxCharacters` ending at `locator_token_id`. **Never** include any token with `TokenId > locator_token_id` or from any chapter with `chapter_index > current_chapter_index`!
  2. In `SnippetEnricher.cs`:
     - Wrap `AngleSharpDomEnricher` so any HTML snippet (such as an AI quiz prompt, model answer, or persona chat reply) can be tokenized and enriched with `<span class="ir-tok" data-tid="...">` and `<ruby class="ir-ruby-gen">`.

---

### `TASK-5.2`: Implement `ILlmProvider` Adapter (Local Ollama / OpenAI-Compatible Endpoints)
- **Target Project:** Create `src/Plugins/ImmersiveReading.Plugins.AiCompanion/ImmersiveReading.Plugins.AiCompanion.csproj`
- **Files to Create:**
  1. `src/Plugins/ImmersiveReading.Plugins.AiCompanion/Providers/OpenAiCompatibleLlmProvider.cs`
- **Step-by-Step Instructions:**
  1. Use `System.Net.Http.HttpClient` to call configurable `/v1/chat/completions` endpoints (works out-of-the-box with local offline **Ollama** `http://localhost:11434/v1`, **LM Studio**, **`llama.cpp` server**, or OpenAI/Gemini/Claude compatible gateways).
  2. Implement `CompleteStructuredAsync<T>` using JSON schema response format (`response_format: { type: "json_object" }`) and deserialize with `System.Text.Json`.

---

### `TASK-5.3`: Implement Progressive `ComprehensionQuizService` (MCQ $\rightarrow$ Guided $\rightarrow$ Free-Form)
- **Target Project:** `src/Plugins/ImmersiveReading.Plugins.AiCompanion`
- **Files to Create:**
  1. `src/Plugins/ImmersiveReading.Plugins.AiCompanion/ComprehensionQuizService.cs`
- **Step-by-Step Instructions:**
  1. Create the SQLite tables `comprehension_sessions` and `comprehension_questions` from [05-extensibility-and-ai-companion-spec.md](05-extensibility-and-ai-companion-spec.md#L156-L180).
  2. Compute the user's current `ComprehensionStage` by querying the average `score` of the last 5 answered questions in `comprehension_questions`:
     - If $< 5$ questions answered or average score $< 0.75$ $\rightarrow$ default to `MultipleChoice`.
     - If average score $\ge 0.75$ in `MultipleChoice` $\rightarrow$ unlock/mix `GuidedShortAnswer`.
     - If average score $\ge 0.80$ across recent `MultipleChoice` / `GuidedShortAnswer` $\rightarrow$ graduate to `FreeForm` (while always allowing the user to pick a mode manually in the UI).
  3. **Generating & Grading Questions**:
     - Call `ReaderContextService.GetJustReadPassageAsync(bookId)` to get the spoiler-free passage slice.
     - For `MultipleChoice`: generate 4 options, 1 correct index, and the exact `EvidenceQuote` + `ParagraphId` from the passage. Grade deterministically in C# (`userChoice == correctOptionIdx`).
     - For `FreeForm`: prompt the user with an open-ended question about the passage; grade the user's free-form response against the passage facts, returning `ComprehensionScore (0..100)`, `EvidenceQuote`, `MissingOrMisunderstood`, and `ModelAnswerHtml` (enriched via `ISnippetEnricher`).

---

### `TASK-5.4`: Implement `VocabularyBudgetService` & $n+1$ `PersonaChatService` with MeCab Verification
- **Target Project:** `src/Plugins/ImmersiveReading.Plugins.AiCompanion`
- **Files to Create:**
  1. `src/Plugins/ImmersiveReading.Plugins.AiCompanion/VocabularyBudgetService.cs`
  2. `src/Plugins/ImmersiveReading.Plugins.AiCompanion/PersonaChatService.cs`
- **Step-by-Step Instructions:**
  1. In `VocabularyBudgetService.cs`:
     - Query `vocabulary_state` in `library.db` to load:
       - **`KnownLemmas` (the $n$ set)**: All words with `status = 2 (Known)`.
       - **`TargetPlusOneLemmas` (the $+1$ set)**: Words with `status = 1 (Learning)` plus up to 10 words from the current chapter's vocabulary deck.
     - Implement `AuditReplyTokens(IReadOnlyList<AnalyzedToken> tokens)`:
       - For each content word token (`token.IsContentWord == true`):
         - Classify as `Known` (in $n$ set), `TargetPlusOne` (in $+1$ set), or `OutOfBudgetUnknown`.
       - Return `VocabularyAuditResult(IReadOnlyList<string> UsedPlusOneLemmas, IReadOnlyList<string> OutOfBudgetLemmas, double KnownRatio)`.
  2. In `PersonaChatService.cs`:
     - Create the SQLite tables `book_personas`, `persona_chat_sessions`, and `persona_chat_messages` ([05-extensibility-and-ai-companion-spec.md](05-extensibility-and-ai-companion-spec.md#L226-L259)).
     - When the user sends a message to a `BookPersona`:
       1. Fetch story context strictly up to `ReadingProgress` via `ReaderContextService.GetStoryContextUpToProgressAsync(bookId)` (Spoiler Guard).
       2. Build the prompt with the Persona's `name`, `speech_style`, `voice_samples_json`, and 5 selected `TargetPlusOneLemmas` ($+1$ words).
       3. Get the candidate reply from `ILlmProvider`.
       4. **Closed-Loop `MeCab.DotNet` $n+1$ Verification**:
          - Tokenize the candidate reply using `JapaneseLanguagePlugin.CreateTokenizer()`.
          - Run `VocabularyBudgetService.AuditReplyTokens(tokens)`.
          - If `OutOfBudgetLemmas.Count > 1` (more than 1 unknown non-target word), make **one targeted rewrite call** to `ILlmProvider` instructing it to replace the specific `OutOfBudgetLemmas` with simpler everyday words from `KnownLemmas` while keeping the $+1$ target word.
       5. Enrich the final reply via `ISnippetEnricher`, adding CSS class `.ir-chat-target-plus1` to any token whose `Lemma` is in `UsedPlusOneLemmas`, and save to `persona_chat_messages`.
- **Definition of Done (xUnit Test):**
  - Implement `tests/ImmersiveReading.Enrichment.Tests/AiCompanionSpecsTests.cs` using a mock `ILlmProvider` to deterministically verify all 5 scenarios in [06-ai-comprehension-and-n-plus-one-persona-chat.feature](features/06-ai-comprehension-and-n-plus-one-persona-chat.feature) (zero-spoiler passage slicing, MCQ $\rightarrow$ FreeForm progression, persona spoiler cap, and MeCab $n+1$ rewrite verification).

---

## Summary Checklist for Code Reviews

| Phase | Required Verification Command | Expected Outcome |
| :--- | :--- | :--- |
| **Phase 1 (Done)** | `dotnet test` | 10/10 Phase 1 Library tests pass; `ImmersiveReading.App` manages 24k+ books, folders, series, tags, and custom fields. |
| **Phase 2 (Done)** | `dotnet test` | `@phase2` Gherkin tests pass (`OkuriganaRubyAligner`, `MeCabTokenizerAdapter`, `AngleSharpDomEnricher`, `ir-book://` scheme). |
| **Phase 3 (Done)** | `dotnet test` | `@phase3` Gherkin tests pass (`JapaneseDeinflector`, `YomitanSqliteImporter`, `< 5 ms` `DictionaryLookupEngine`, Popover). |
| **Phase 4 (Done)** | `dotnet test` | `@phase4` Gherkin tests pass (`PythonStudyPackBridge`, lease files, 1-click flashcard export). |
| **Phase 5 (Done)** | `dotnet test` | `@phase5` Gherkin tests pass (zero-spoiler `ReaderContextService`, MCQ $\rightarrow$ FreeForm quizzes, MeCab-verified $n+1$ Persona Chat). |

