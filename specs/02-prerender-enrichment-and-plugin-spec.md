# 02 — Pre-Render AngleSharp DOM Enrichment & Language Plugin Specification

> **Modules:** `ImmersiveReading.Core.Plugins`, `ImmersiveReading.Enrichment`, `ImmersiveReading.Plugins.Japanese`  
> **Key Libraries:** `AngleSharp` (`>= 1.1.2`), `MeCab.DotNet` (`>= 1.1.0`)

---

## 1. Architectural Concept: Why Pre-Render DOM Enrichment?

Traditional web-based EPUB readers load raw XHTML into an `<iframe>` or WebView and rely on client-side JavaScript (`document.caretRangeFromPoint`) to guess word boundaries on hover, or run heavy JS regexes after layout that cause layout thrashing (CLS) and break existing `<ruby>` tags.

In **Immersive Reading**, enrichment happens **in .NET before the WebView renders the chapter**:

```text
Raw EPUB Chapter (.xhtml)
       │
       ▼
[1. AngleSharp XmlParser] ──► Parses into live IDocument (preserves SVG, CSS, attributes)
       │
       ▼
[2. Ruby-Aware Block & Text-Node Collector]
       │  Extracts logical paragraph text & existing EPUB <ruby> spans
       │  without ever entering <rt>, <rp>, <script>, <style>, or <svg>
       ▼
[3. ILanguagePlugin (e.g., Japanese + MeCab.DotNet)]
       │  Tokenizes text, resolves lemmas & readings (EPUB <ruby> takes priority!),
       │  aligns okurigana, and checks user VocabularyState + Jiten CSV deck
       ▼
[4. AngleSharp DOM Mutator]
       │  Wraps tokens in <span class="ir-tok" data-tid="...">
       │  Injects missing <ruby class="ir-gen-ruby"> (if configured)
       │  Adds vocabulary status classes & grammar/pitch markers
       ▼
[5. Serialize to XHTML/HTML] ──► Served via ir-book:// to Photino WebView
```

### Benefits of Pre-Render AngleSharp Enrichment
1. **Zero Layout Shift (Zero CLS)**: `<ruby>` furigana and token `<span>` wrappers are already present when the browser engine lays out the page (crucial for CSS `writing-mode: vertical-rl` and multi-column pagination!).
2. **Instant $O(1)$ Hover/Tap Lookup**: Every word/token in the DOM has a `data-tid="{int}"` attribute indexing into the backend's `ChapterTokenMap`. Hovering or clicking an element sends `tokenId` over IPC—no fragile `caretRangeFromPoint` math needed.
3. **100% Language-Plugin Driven**: The AngleSharp walker handles DOM mechanics while delegating all linguistic rules to `ILanguagePlugin`.

---

## 2. The Language Plugin SDK (`ImmersiveReading.Core.Plugins`)

### 2.1 Core Plugin Interfaces

```csharp
namespace ImmersiveReading.Core.Plugins;

public interface ILanguagePlugin : IDisposable
{
    /// <summary>BCP-47 primary language tag, e.g., "ja", "zh", "ko", "es".</summary>
    string LanguageCode { get; }

    /// <summary>Human-readable plugin name, e.g., "Japanese (MeCab + Deinflector)".</summary>
    string DisplayName { get; }

    LanguageCapabilities Capabilities { get; }

    ITextTokenizer CreateTokenizer();
    IDeinflector? GetDeinflector();
    IRubyAligner? GetRubyAligner();

    /// <summary>Normalizes a reading for dictionary comparison (e.g. Katakana -> Hiragana in JA).</summary>
    string NormalizeReading(ReadOnlySpan<char> reading);

    /// <summary>Checks whether a character requires phonetic annotation (e.g., CJK ideograph).</summary>
    bool NeedsPhoneticAnnotation(Rune character);
}

[Flags]
public enum LanguageCapabilities
{
    None                    = 0,
    MorphologicalTokenizer  = 1 << 0, // Non-whitespace segmentation (JA, ZH)
    Deinflection            = 1 << 1, // Verb/adjective conjugation reversal (JA, KO, ES, DE)
    RubyPhonetics           = 1 << 2, // Furigana / Pinyin / Bopomofo <ruby> support
    CharacterDecomposition  = 1 << 3, // Kanji / Hanzi character dictionary & stroke order
    PitchOrToneAccent       = 1 << 4, // Pitch accent (JA) or tone marks (ZH)
    VerticalTypography      = 1 << 5  // Supports vertical-rl layout
}
```

### 2.2 Tokenizer, Deinflector & Ruby Aligner Contracts

```csharp
public readonly record struct SourceRubySpan(
    int StartCharOffset,   // Paragraph-relative code-point start
    int EndCharOffset,     // Paragraph-relative code-point end (exclusive)
    string Surface,        // Base text inside <ruby>
    string Reading         // Text inside <rt>
);

public readonly record struct AnalyzedToken(
    int StartCharOffset,   // Paragraph-relative start
    int EndCharOffset,     // Paragraph-relative end
    string Surface,        // Surface text in book (e.g., "食べた")
    string Lemma,          // Dictionary base form (e.g., "食べる")
    string NormalizedForm, // Orthographic normalized form (e.g., "為る" -> "する")
    string Reading,        // Normalized reading (e.g., "たべた")
    string DictionaryReading, // Base lemma reading (e.g., "たべる")
    string PartOfSpeech,   // Primary POS tag (e.g., "動詞")
    string ConjugationType,// Conjugation class (e.g., "一段")
    string ConjugationForm,// Inflection form (e.g., "連用タ接続")
    RubySource ReadingSource, // None, EpubAuthor, or PluginGenerated
    bool IsContentWord     // True for nouns/verbs/adj/adv; False for punctuation/particles
);

public enum RubySource : byte
{
    None = 0,
    EpubAuthor = 1,
    PluginGenerated = 2
}

public interface ITextTokenizer : IDisposable
{
    /// <summary>
    /// Tokenizes a paragraph's normalized base text while respecting author-supplied <ruby> spans.
    /// Note: Like WorkerContext in enrichement-extractor, an ITextTokenizer instance is owned by one worker thread.
    /// </summary>
    IReadOnlyList<AnalyzedToken> TokenizeParagraph(
        ReadOnlySpan<char> paragraphText,
        IReadOnlyList<SourceRubySpan> authorRubies);
}

public readonly record struct DeinflectionCandidate(
    string BaseForm,
    string? ExpectedReadingPrefix,
    IReadOnlyList<string> RuleChain, // e.g. ["past (-ta)", "polite (-masu)"]
    string PartOfSpeechConstraint
);

public interface IDeinflector
{
    IReadOnlyList<DeinflectionCandidate> Deinflect(ReadOnlySpan<char> surface);
}

public readonly record struct RubyUnit(
    int RelativeStart,
    int RelativeEnd,
    string BaseText,
    string? Reading,       // Null for plain kana / okurigana outside ruby
    RubySource Source
);

public interface IRubyAligner
{
    /// <summary>
    /// Splits a token's surface and reading into kanji-stem ruby spans and plain okurigana spans.
    /// E.g., Surface="食べる", Reading="たべる" -> [Ruby("食","た"), Plain("べる")].
    /// </summary>
    IReadOnlyList<RubyUnit> AlignRuby(
        ReadOnlySpan<char> surface,
        ReadOnlySpan<char> reading,
        IReadOnlyList<SourceRubySpan> overlappingAuthorRubies);
}
```

### 2.3 Dynamic Plugin Discovery & Loading
- Built-in plugins (`JapaneseLanguagePlugin`, `WhitespaceLanguagePlugin`) are registered at startup.
- External third-party plugins can be placed in `plugins/<plugin-name>/`:
  - Contains `plugin.json` (`{ "id": "ko-mecab", "language": "ko", "assembly": "ImmersiveReading.Plugins.Korean.dll", "entryType": "..." }`).
  - Loaded via a collectible `AssemblyLoadContext` (`PluginLoadContext`) that shares `ImmersiveReading.Core` types with the host application.

---

## 3. AngleSharp Pre-Render DOM Enrichment Pipeline (`ImmersiveReading.Enrichment`)

### 3.1 Parsing EPUB XHTML Safely
EPUB 2 and EPUB 3 chapters are XHTML documents (`application/xhtml+xml`), often containing XML namespaces (`xmlns:epub="http://www.idpf.org/2007/ops"`), inline `<svg>` illustrations, and self-closing tags.

`AngleSharpDomEnricher` parses each chapter as follows:
1. Attempt `XmlParser` (`new XmlParser(new XmlParserOptions { IsSuppressingErrors = false })`) so XML namespaces, self-closing `<image/>` inside `<svg>`, and strict XHTML structure are preserved faithfully.
2. If an ill-formed EPUB fails strict XML parsing, automatically fall back to AngleSharp's `HtmlParser` (`new HtmlParser()`).

### 3.2 Ruby-Aware DOM Block & Text-Node Walker
Just like [`enrichement-extractor/immersive/epub.py`](../enrichement-extractor/immersive/epub.py#L74-L113), the enricher processes block elements (`p`, `h1`–`h6`, `li`, `blockquote`, `div` leaf blocks) while **never corrupting existing `<ruby>` elements or non-text elements**:

1. **Skipped Subtrees**:
   - Never descend into `<script>`, `<style>`, `<svg>`, `<math>`, `<rt>`, `<rp>`, or `<head>`.
2. **Collecting a Block's Text & Atomic `<ruby>` Map**:
   - Walk the block's descendant nodes in document order:
     - **Plain `IText` node** (not inside `<ruby>`): Record a `DomTextSegment(IText Node, int BlockStartOffset, int BlockEndOffset)`.
     - **`<ruby>` element**: Extract base text from `<rb>` children (or direct `IText` children) and reading text from `<rt>` children. Record a `SourceRubySpan(BlockStartOffset, BlockEndOffset, BaseText, Reading)` and a `DomRubySegment(IElement RubyElement, int BlockStartOffset, int BlockEndOffset)`.
   - Concatenate the segments into `blockText` (preserving exact character offsets back to each `IText` node or `<ruby>` element!).
3. **Tokenization & Enrichment Resolution**:
   - Pass `(blockText, authorRubies)` to `tokenizer.TokenizeParagraph(...)`.
   - For each `AnalyzedToken`:
     - Assign a sequential chapter-wide `TokenId` (`0, 1, 2, ...`).
     - Query `VocabularyTracker` and optional book CSV deck for `(language, token.Lemma, token.DictionaryReading)` to determine `WordStatus` (`New`, `Learning`, `Known`, `Ignored`) and whether the word is in the book's study deck (`InDeck`).
     - Determine whether generated furigana should be injected based on `EnrichmentOptions.FuriganaMode`:
       - `AuthorOnly`: Keep only EPUB `<ruby>` (`RubySource.EpubAuthor`).
       - `AllKanji`: Inject `<ruby class="ir-ruby-gen">` for all tokens containing kanji lacking author ruby.
       - `UnknownAndLearningOnly`: Inject `<ruby class="ir-ruby-gen">` only when `WordStatus` is `New` or `Learning` (hiding furigana on `Known` words to promote active reading recall!).
       - `Off`: Hide all furigana via CSS class `ir-hide-ruby`.
4. **Non-Destructive DOM Mutation**:
   - Replace/wrap the corresponding ranges in `IText` nodes and `<ruby>` elements:
     - For a token inside a plain `IText` node: split/replace the `IText` range with:
       ```html
       <span class="ir-tok ir-st-new" data-tid="142"><ruby class="ir-ruby-gen">食<rt>た</rt></ruby>べた</span>
       ```
     - For a token that matches an existing EPUB `<ruby>` element: wrap the existing `<ruby>` element (or add `class="ir-tok"` + `data-tid="142"` directly onto it) **without altering its internal `<rb>`/`<rt>` markup**.
     - For a multi-node token (e.g. a word partially bolded `<em>人</em>気`): wrap each text slice in `<span class="ir-tok" data-tid="142" data-part="0">...</span>` sharing the same `data-tid` so hovering either part highlights and looks up the whole word!
5. **Lightweight HTML Payload**:
   - Notice that `<span class="ir-tok" data-tid="142">` only needs `data-tid` (and optional CSS styling classes like `ir-st-learning`, `ir-in-deck`) in the HTML!
   - All heavy metadata (`Lemma`, `Reading`, `PartOfSpeech`, `SentenceId`, `ParagraphOffset`, `SourceRuby`) stays in C#'s `ChapterTokenMap` in memory, keeping the serialized DOM small and fast for the WebView to parse.

---

## 4. Japanese Language Plugin (`ImmersiveReading.Plugins.Japanese`)

### 4.1 `MeCab.DotNet` Integration (`MeCabTokenizerAdapter`)
- Uses `MeCab.MeCabTagger.Create(parameter)` initialized once per worker thread from a shared dictionary directory (defaulting to bundled IPADic or custom UniDic in `data/mecab/`).
- Parses MeCab node features (`node.Feature` CSV):
  - IPADic format: `[0]=POS1, [1]=POS2, [2]=POS3, [3]=POS4, [4]=ConjugationType, [5]=ConjugationForm, [6]=BaseForm (Lemma), [7]=Reading (Katakana), [8]=Pronunciation`.
- **Source Ruby Priority Rule** (matching [`enrichement-extractor/immersive/matching.py`](../enrichement-extractor/immersive/matching.py#L74-L90)):
  - When an author `<ruby>` span covers a token (e.g., `<ruby>人気<rt>ひとけ</rt></ruby>` or `<ruby>半身<rt>はんみ</rt></ruby>`), `Reading` is set to the normalized EPUB ruby (`ひとけ` / `はんみ`) with `ReadingSource = RubySource.EpubAuthor`, overriding MeCab's default guess (`にんき` / `はんしん`)!
- **Multi-Token Compound Merging**:
  - Combines verb/adjective stems with bound inflectional auxiliaries (`助動詞` like `た`, `ない`, `れる`, `られる`, `せる`, `させる`, `たい`, `ます` and连接 particles `て`/`で`) into cohesive reading/lookup tokens while also retaining constituent sub-tokens in `ChapterTokenMap` so the lookup engine can show both the full inflected phrase and its root verb.

### 4.2 `OkuriganaRubyAligner`
Directly ports the proven alignment algorithm from [`enrichement-extractor/immersive/furigana.py#L18-L51`](../enrichement-extractor/immersive/furigana.py#L18-L51):
- Splits `surface` into alternating runs of CJK kanji (`\u3400..\u9fff`, `\uf900..\ufaff`, `\U00020000..\U000323af`, `々`, `〆`, `〇`) and fixed hiragana/katakana anchors.
- Recursively matches fixed kana anchors (converted to hiragana via `hira()`) against the token's hiragana reading so that okurigana is emitted as plain text outside `<ruby>` tags.
- If a word has an irregular/jukujikun reading (e.g. `今日` -> `きょう`, `大人` -> `おとな`) or cannot be split cleanly at kana anchors, emits a single whole-word `<ruby>` unit.

### 4.3 `JapaneseDeinflector`
Rule-table driven deinflector (similar to Yomitan's deinflection rules, verified against MeCab conjugation classes):
- Supports ~140 suffix transformation rules with part-of-speech compatibility chains (`v1` Ichidan, `v5` Godan `う/く/ぐ/す/つ/ぬ/ぶ/む/る`, `vs` Suru/Jiru, `vk` Kuru, `adj-i` I-adjective):
  - Polite (`〜ます`, `〜ました`, `〜ません`, `〜ましょう`)
  - Past / Te-form (`〜た`, `〜だ`, `〜て`, `〜で`, including `行く -> 行った`)
  - Negative (`〜ない`, `〜なかった`, `〜ず`, `〜ぬ`, colloquial `〜ねえ`)
  - Passive / Potential (`〜れる`, `〜られる`, shorter potential `〜える`)
  - Causative & Causative-Passive (`〜せる`, `〜させる`, `〜せられる`, `〜される`)
  - Desire (`〜たい`), Volitional (`〜よう`, `〜おう`), Imperative, Conditional (`〜ば`, `〜たら`, `〜なら`)
  - Contractions (`〜ちゃう` -> `〜てしまう`, `〜じゃう` -> `〜でしまう`, `〜てる` -> `〜ている`, `〜とく` -> `〜ておく`).

