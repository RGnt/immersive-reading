# Specification 09: Part-of-Speech Highlighter Plugin & DOM Enrichment Pipeline

## Status: Approved / Ready for Implementation
**Author:** AI Agent & Development Team  
**Scope:** `immersive-reader` POS Highlighting Plugin, Morphological Tag Classification, AngleSharp DOM Enrichment Stage Pipeline, Settings Schema, and Reader Stylesheet Injection.

---

## 1. Executive Summary & Motivation

The Immersive Reading unified plugin system (`IImmersivePlugin`, `IPluginHostContext`, `PluginHostManager`) allows plugins to dynamically register RPC actions, Python pipeline stages, language analyzers, and DOM enrichment stages with hot-reload capabilities (`PluginReloadBehavior.Hot`).

To validate and demonstrate the runtime capabilities of the unified plugin system, this specification defines the **Part-of-Speech (POS) Highlighter Plugin** (`posHighlighter`). The plugin:
1. Classifies Japanese analyzed tokens into distinct grammatical categories:
   - **i-adjectives** (`形容詞`)
   - **na-adjectives** (`形容動詞` / `形状詞`)
   - **u-verbs** (`五段動詞` / Godan)
   - **ru-verbs** (`一段動詞` / Ichidan)
   - **Irregular verbs** (サ変、カ変、不規則)
   - **Suffixes** (`接尾辞` / `接尾`) — distinct from particles
   - **Particles** (`助詞`):
     - **Single-kana particles** (e.g. `は`, `が`, `を`, `に`, `で`, `と`, `も`, `へ`, `か`, `ね`, `よ`, `の`, `て`)
     - **Multi-kana / compound particles** (e.g. `から`, `まで`, `より`, `など`, `ばかり`)
   - Other content words (nouns, adverbs, conjunctions, auxiliaries, interjections).
2. Plugs directly into the reader pipeline via `IDomEnrichmentStage`:
   - Runs in order `Order = 200` after token segmentation and DOM tree construction.
   - Annotates token elements (`.ir-tok`) with semantic CSS classes (`ir-pos-adj-i`, `ir-pos-adj-na`, `ir-pos-verb-u`, `ir-pos-verb-ru`, `ir-pos-verb-irr`, `ir-pos-suffix`, `ir-pos-prt-single`, `ir-pos-prt-multi`) and attributes (`data-ir-pos="..."`).
   - Injects reader styling (`<style id="ir-pos-highlighter-style">`) that applies configurable colors via text coloring, background tint, or subtle underline modes.
3. Exposes dynamic configuration via `settings.schema.json` and `settings.defaults.json`, fully integrated into the existing Settings -> Installed Plugins tab.
4. Provides a UI extension (`ui.js`) that mounts a toggle button in the reader top bar (`ReaderToolbarPluginSlot`) allowing instant one-click toggling during reading.
5. Operates 100% offline, deterministically, respecting all core invariants.

---

## 2. Morphological Classification Matrix

| Category | MeCab IPAdic Features | UniDic Features | Surface / Heuristic | Output Class | Default Color |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **i-Adjective** | `pos == "形容詞"` | `pos.StartsWith("形容詞")` | Inflection contains `形容詞` | `ir-pos-adj-i` | `#fb923c` (Warm Orange) |
| **na-Adjective** | `pos == "名詞" && posSub1 == "形容動詞語幹"` | `pos == "形状詞"` | `posSub1.Contains("形容動詞")` | `ir-pos-adj-na` | `#f472b6` (Rose Pink) |
| **u-Verb (Godan)** | `pos == "動詞"` & `conjType` contains `"五段"` | `conjType` contains `"五段"` | e.g. 書く, 話す, 読む, 走る | `ir-pos-verb-u` | `#38bdf8` (Sky Blue) |
| **ru-Verb (Ichidan)** | `pos == "動詞"` & `conjType` contains `"一段"` | `conjType` contains `"一段"` | e.g. 食べる, 見る, 答える | `ir-pos-verb-ru` | `#818cf8` (Soft Indigo) |
| **Irregular Verb** | `pos == "動詞"` & `conjType` contains `"サ変"` or `"カ変"` | `conjType` contains `"サ変"` or `"カ変"` | e.g. する, 来る, 愛する | `ir-pos-verb-irr` | `#c084fc` (Purple) |
| **Suffix** | `posSub1.StartsWith("接尾")` or `pos == "接尾辞"` | `pos == "接尾辞"` | e.g. さん, 様, 的, 化, たち, 目 | `ir-pos-suffix` | `#facc15` (Warm Gold) |
| **Single-Kana Particle**| `pos == "助詞"` & `Surface.Length == 1` | `pos == "助詞"` & `Surface.Length == 1` | e.g. は, が, を, に, で, と, も, へ, か, ね, よ, の, て | `ir-pos-prt-single` | `#34d399` (Emerald Mint) |
| **Multi-Kana Particle** | `pos == "助詞"` & `Surface.Length > 1` | `pos == "助詞"` & `Surface.Length > 1` | e.g. から, まで, より, など, ほど | `ir-pos-prt-multi` | `#2dd4bf` (Teal) |
| **Adverb** | `pos == "副詞"` | `pos == "副詞"` | e.g. ゆっくり, たくさん | `ir-pos-adverb` | `#a3e635` (Lime) |
| **Noun (General)** | `pos == "名詞"` (not suffix, not na-stem) | `pos == "名詞"` | e.g. 本, 猫, 先生 | `ir-pos-noun` | Inherited / Default |

---

## 3. Architecture & Interfaces

### 3.1 DOM Enrichment Contract Update

In `ImmersiveReading.Core.Extensions`:
```csharp
public sealed record DomEnrichmentContext(
    string BookId,
    string ChapterHref,
    IReadOnlyList<EnrichedTokenEntry> Tokens,
    EnrichmentOptions Options
);

public interface IDomEnrichmentStage
{
    string StageId { get; }
    int Order { get; }
    void Enrich(AngleSharp.Dom.IDocument document, DomEnrichmentContext context);
}
```

### 3.2 Token Model Augmentation

In `ImmersiveReading.Core.Plugins.AnalyzedToken`:
```csharp
public readonly record struct AnalyzedToken(
    int StartCharOffset,
    int EndCharOffset,
    string Surface,
    string Lemma,
    string NormalizedForm,
    string Reading,
    string DictionaryReading,
    string PartOfSpeech,
    string ConjugationType,
    string ConjugationForm,
    RubySource ReadingSource,
    bool IsContentWord,
    string PartOfSpeechSub = ""
);
```

In `MeCabTokenizerAdapter.cs`:
Record node `PosSub1` into `AnalyzedToken.PartOfSpeechSub`.

### 3.3 DOM Enrichment Execution in `AngleSharpDomEnricher`

```csharp
public static EnrichedChapterResult EnrichChapter(
    string rawXhtml,
    string bookId,
    string chapterHref,
    ILanguagePlugin languagePlugin,
    EnrichmentOptions options,
    Func<string, string, WordStatus>? getWordStatus = null,
    Func<string, bool>? isWordInDeck = null,
    IReadOnlyList<IDomEnrichmentStage>? domStages = null)
{
    // ... parse document, tokenize blocks, apply tokens ...

    if (domStages != null && domStages.Count > 0)
    {
        var context = new DomEnrichmentContext(bookId, chapterHref, chapterTokens, options);
        foreach (var stage in domStages)
        {
            stage.Enrich(document, context);
        }
    }

    // ... PolyglotXhtmlMarkupFormatter serialization ...
}
```

### 3.4 Wireup in `BookSchemeHandler` & `PluginHostManager`

In `BookSchemeHandler`:
- Accept optional `Func<IReadOnlyList<IDomEnrichmentStage>>? domStagesProvider = null`.
- When enriching a chapter, invoke `domStagesProvider?.Invoke()` and pass to `AngleSharpDomEnricher.EnrichChapter`.

In `PluginHostManager`:
- Expose `public IReadOnlyList<IDomEnrichmentStage> ActiveDomStages`.
- Thread-safe copy under lock `_domStages`.

In `AppServices.cs`:
- Wire `bookScheme.DomStagesProvider = () => pluginHostManager.ActiveDomStages`.

---

## 4. Part-of-Speech Highlighter Plugin Implementation

### 4.1 Project Location & Dependencies
`immersive-reader/src/Plugins/ImmersiveReading.Plugins.PosHighlighter`:
- References `ImmersiveReading.Core`
- Target framework: `net10.0`
- Output: Class Library
- Embedded resources: `settings.schema.json`, `settings.defaults.json`, `Assets/ui.js`

### 4.2 Configuration Model & Schema
`PosHighlighterSettings`:
- `Enabled`: bool (default: `true`)
- `HighlightStyle`: string (`"text-color"` | `"underline"` | `"background"`, default: `"text-color"`)
- `ColorAdjI`: string hex (default: `"#fb923c"`)
- `ColorAdjNa`: string hex (default: `"#f472b6"`)
- `ColorVerbGodan`: string hex (default: `"#38bdf8"`)
- `ColorVerbIchidan`: string hex (default: `"#818cf8"`)
- `ColorVerbIrregular`: string hex (default: `"#c084fc"`)
- `ColorSuffix`: string hex (default: `"#facc15"`)
- `ColorParticleSingle`: string hex (default: `"#34d399"`)
- `ColorParticleMulti`: string hex (default: `"#2dd4bf"`)
- `ColorAdverb`: string hex (default: `"#a3e635"`)

### 4.3 RPC Endpoints
> Extended by [spec 10](10-pos-highlight-category-menu-spec.md): `getCategories`, `setCategories`, `getCss`, and the `enabledCategories` setting.

- `ext.posHighlighter.getSettings` -> `{ enabled: bool, highlightStyle: string, ...colors }`
- `ext.posHighlighter.toggle` -> `{ enabled: bool }` (toggles on/off and broadcasts change)
- `ext.posHighlighter.updateSettings` -> `{ success: bool }`

---

## 5. Verification Plan

1. **Unit Tests (`PosHighlighterTests.cs`)**:
   - `PosClassifier_Classifies_IAdjectives_Correctly`: e.g. `美味しい`, `楽しい` -> `AdjectiveI`.
   - `PosClassifier_Classifies_NaAdjectives_Correctly`: e.g. `静か`, `綺麗` -> `AdjectiveNa`.
   - `PosClassifier_Classifies_Verbs_ByConjugation`:
     - Godan / 五段: `行く`, `読む`, `走る` -> `VerbGodan`
     - Ichidan / 一段: `食べる`, `見る` -> `VerbIchidan`
     - Irregular: `する`, `来る` -> `VerbIrregular`
   - `PosClassifier_Distinguishes_Suffix_From_Particles`:
     - `さん`, `様`, `的` -> `Suffix`
     - `は`, `が`, `を` -> `ParticleSingle`
     - `から`, `まで` -> `ParticleMulti`
   - `PosDomEnrichmentStage_Annotates_HtmlNodes_With_Pos_Classes`:
     - Verifies `.ir-tok` nodes receive classes `.ir-pos-verb-u`, `.ir-pos-adj-i`, etc.
     - Verifies injected `<style id="ir-pos-highlighter-style">` is added to `<head>`.
   - `PluginLifecycle_HotReload`:
     - Verifies enabling registers the stage and disabling removes the stage.
2. **End-to-End Suite**:
   - `dotnet test ImmersiveReading.sln` passes.
   - `dotnet run --project immersive-reader/src/ImmersiveReading.App -- --verify-reader` passes.
   - `dotnet run --project immersive-reader/src/ImmersiveReading.App -- --verify-ui` passes.
