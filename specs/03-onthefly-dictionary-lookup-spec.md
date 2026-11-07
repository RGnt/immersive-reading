# 03 — On-the-Fly Dictionary Lookup Engine Specification

> **Module:** `ImmersiveReading.Dictionaries` & `ImmersiveReading.Core.Dictionaries`  
> **Target Latency:** < 5 ms per hover/tap lookup  
> **Supported Sources:** Yomitan Format-3 ZIPs (SQLite-indexed), Jiten CSV Decks, `study.config.json` Overrides, KANJIDIC2 + Kanji alive + KanjiVG, and `enrichement-extractor` `reader.json`

---

## 1. Overview & Design Principles

When the user hovers (e.g., holding `Shift` or after a configurable 150 ms hover delay) or taps/clicks any word in the Photino EPUB reader, the application displays an instant, source-grounded dictionary popover.

### Core Lookup Principles (Aligned with `enrichement-extractor`)
1. **Sub-5ms Indexed Queries**: Unlike batch enrichment which scans an entire book, interactive reading requires instantaneous response. Yomitan format-3 ZIPs and KANJIDIC2/Kanji alive datasets are imported once into an indexed local SQLite database (`dictionaries.db`) with `(expression, reading)` B-tree indexes.
2. **Author `<ruby>` Reading Priority**: If the tapped word in the EPUB has author-supplied `<ruby>` (e.g., `<ruby>人気<rt>ひとけ</rt></ruby>`), dictionary entries matching the author's reading (`ひとけ`) are ranked at the top and explicitly badged as `Matches Book Ruby`, preventing misleading homograph definitions (`にんき`).
3. **Longest-Prefix + Morphological Lemma + Deinflection**: A single tap resolves:
   - Multi-token compound phrases starting at the tapped position (up to 16 characters, aligned to token boundaries, e.g. `気にならなかった` -> `気になる`).
   - The MeCab morphological lemma (`AnalyzedToken.Lemma`) of the tapped token.
   - Rule-deinflected candidates (`IDeinflector.Deinflect`) with their conjugation chains (e.g. `食べさせられた` -> `食べる [passive <- causative <- past]`).
   - Constituent Kanji breakdown (primary learner meanings from `Kanji alive`, full `KANJIDIC2` readings/meanings, and interactive `KanjiVG` stroke-order diagrams).
4. **No Automatic Sense Invention**: All dictionary sense blocks are preserved with their dictionary title, tags, pitch accent, and frequency ranks. Any `context_glosses` override from `study.config.json` or the user's `vocabulary_state` is displayed prominently at the top with a `Reviewed Context Gloss` badge.

---

## 2. SQLite Dictionary Storage Schema (`dictionaries.db`)

Importing a Yomitan format-3 ZIP (following the validation & security rules in [`enrichement-extractor/immersive/yomitan.py`](../enrichement-extractor/immersive/yomitan.py#L113-L217)) populates `dictionaries.db`:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

-- 1. Installed Dictionaries Registry
CREATE TABLE IF NOT EXISTS dictionary_sources (
    dictionary_id       TEXT PRIMARY KEY,          -- 'yomitan-' + sha256(zip)
    title               TEXT NOT NULL,
    revision            TEXT NOT NULL,
    language            TEXT NOT NULL DEFAULT 'ja',
    target_language     TEXT NOT NULL DEFAULT 'en',
    priority_order      INTEGER NOT NULL,          -- Lower = higher priority
    enabled             INTEGER NOT NULL DEFAULT 1,
    author              TEXT,
    url                 TEXT,
    attribution         TEXT,
    notices_json        TEXT NOT NULL DEFAULT '{}',-- Bundled LICENSE/README texts
    bank_counts_json    TEXT NOT NULL DEFAULT '{}',
    imported_at_utc     TEXT NOT NULL
);

-- 2. Term Bank Entries (Format-3 term_bank_N.json)
CREATE TABLE IF NOT EXISTS dictionary_terms (
    term_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    dictionary_id       TEXT NOT NULL REFERENCES dictionary_sources(dictionary_id) ON DELETE CASCADE,
    expression          TEXT NOT NULL,             -- row[0] e.g. '食べる'
    reading             TEXT NOT NULL,             -- row[1] (or row[0] if empty)
    reading_norm        TEXT NOT NULL,             -- Hiragana-normalized reading ('たべる')
    definition_tags     TEXT NOT NULL,             -- row[2] space-separated tags
    rules               TEXT NOT NULL,             -- row[3] deinflection POS rules ('v1', 'v5k', etc.)
    score               REAL NOT NULL,             -- row[4] dictionary sort score
    glossary_json       TEXT NOT NULL,             -- row[5] raw structured/text glossary JSON
    glossary_text       TEXT NOT NULL,             -- Plain-text preview extracted safely
    sequence            INTEGER NOT NULL,          -- row[6]
    term_tags           TEXT NOT NULL,             -- row[7]
    bank_name           TEXT NOT NULL,
    bank_row            INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_terms_expr ON dictionary_terms(expression);
CREATE INDEX IF NOT EXISTS idx_terms_read_norm ON dictionary_terms(reading_norm);
CREATE INDEX IF NOT EXISTS idx_terms_expr_read ON dictionary_terms(expression, reading_norm);

-- 3. Term Metadata: Frequency, Pitch Accent & IPA (term_meta_bank_N.json)
CREATE TABLE IF NOT EXISTS dictionary_term_meta (
    meta_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    dictionary_id       TEXT NOT NULL REFERENCES dictionary_sources(dictionary_id) ON DELETE CASCADE,
    expression          TEXT NOT NULL,
    reading_norm        TEXT,                      -- NULL if applies to all readings of expression
    mode                TEXT NOT NULL,             -- 'freq', 'pitch', 'ipa'
    data_json           TEXT NOT NULL              -- Validated metadata payload
);
CREATE INDEX IF NOT EXISTS idx_term_meta_lookup ON dictionary_term_meta(expression, reading_norm);

-- 4. Kanji Dictionary (Merged KANJIDIC2 + Kanji alive + Yomitan kanji_bank)
CREATE TABLE IF NOT EXISTS dictionary_kanji (
    character           TEXT PRIMARY KEY,          -- Single kanji literal e.g. '海'
    on_readings_json    TEXT NOT NULL,             -- JSON array of katakana/hiragana on'yomi
    kun_readings_json   TEXT NOT NULL,             -- JSON array of kun'yomi
    primary_meanings    TEXT NOT NULL,             -- JSON array (Kanji alive preferred, KANJIDIC2 fallback)
    primary_source      TEXT NOT NULL,             -- 'Kanji alive' or 'KANJIDIC2 / EDRDG'
    kanjidic_meanings   TEXT NOT NULL,             -- Full KANJIDIC2 meanings JSON array
    stroke_count        INTEGER,
    grade               TEXT,
    radical             TEXT,
    frequency_json      TEXT,                      -- Optional kanji_meta_bank frequencies
    kanjivg_strokes_json TEXT                      -- Ordered SVG path 'd' strings from KanjiVG
);
```

---

## 3. On-the-Fly Lookup Algorithm (`DictionaryLookupEngine.QueryAsync`)

### Input Request (`LookupQuery`)
Sent from the Photino WebView when the user hovers/clicks a `<span class="ir-tok" data-tid="142">`:
```csharp
public sealed record LookupQuery(
    string BookId,
    string ChapterHref,
    int TokenId,
    int CharOffsetInToken = 0, // 0-based code-point offset within the tapped token
    int MaxResults = 25
);
```

### Step-by-Step Resolution Pipeline

1. **Retrieve Pre-Rendered Token Context (`O(1)`)**:
   - Look up `TokenId` in `ChapterEnrichmentCache`'s `ChapterTokenMap` for `(BookId, ChapterHref)`.
   - Extract:
     - Tapped `AnalyzedToken` (`Surface`, `Lemma`, `NormalizedForm`, `Reading`, `DictionaryReading`, `PartOfSpeech`, `ReadingSource`).
     - Forward token window: the tapped token plus subsequent tokens in the same paragraph up to **16 Unicode code points** (stopping at paragraph boundaries or sentence punctuation).
     - Overlapping `SourceRubySpan`s from the EPUB in that 16-char window.
2. **Generate Candidate Search Keys**:
   - **Token-Boundary Substrings**: Build candidate prefixes from the 16-char window that end on a token boundary (mirrors [`enrichement-extractor/immersive/build.py#L216-L225`](../enrichement-extractor/immersive/build.py#L216-L225): never match an arbitrary substring inside the middle of a token!).
   - **Deinflection Candidates**: Run `plugin.GetDeinflector().Deinflect(prefix)` on each prefix, keeping candidates whose `PartOfSpeechConstraint` (`v1`, `v5`, `adj-i`, `vs`, `vk`) is compatible with the dictionary entry's `rules` field.
   - **MeCab Morphological Lemma**: Always include `(tappedToken.Lemma, tappedToken.DictionaryReading)` and `tappedToken.NormalizedForm`.
   - **Book Config Aliases**: If the book's `study.config.json` defines `aliases` mapping a dictionary spelling to a book surface spelling, include the reverse alias mapping.
3. **Query Local Sources in Parallel/Batch**:
   - **Book Jiten CSV Deck & `study.config.json`** (if attached to `BookId`):
     - Match against loaded CSV rows and `context_glosses`.
   - **SQLite `dictionary_terms`**:
     - Single parameterized query: `SELECT * FROM dictionary_terms WHERE expression IN (@forms) OR reading_norm IN (@readings)` joined with enabled `dictionary_sources`.
   - **SQLite `dictionary_term_meta`**:
     - Fetch `freq`, `pitch`, and `ipa` records for matched `(expression, reading_norm)` pairs.
   - **User `vocabulary_state`** (`library.db`):
     - Fetch the user's current `WordStatus` (`New`, `Learning`, `Known`, `Ignored`) and any custom user note/gloss.
4. **Filter & Rank Candidates (`MatchPolicy` Parity)**:
   - Reject homograph entries whose reading contradicts an explicit **EPUB Author `<ruby>`** (`ReadingSource.EpubAuthor`), unless the user toggles *"Show all homographs"*.
   - Sort accepted term entries deterministically by:
     1. **Matched Span Length** (longest matched phrase in the text first, e.g., `気になる` before `気`).
     2. **Reading Verification Tier**:
        - Tier 1: Exact match with EPUB Author `<ruby>` (`RubySource.EpubAuthor`).
        - Tier 2: Exact match with MeCab token reading (`RubySource.PluginGenerated`).
        - Tier 3: Valid deinflected stem match.
     3. **Book Deck Priority**: Entries present in the book's Jiten CSV deck or `study.config.json` `context_glosses`.
     4. **Dictionary Priority & Score**: `dictionary_sources.priority_order ASC`, `dictionary_terms.score DESC`.
5. **Extract Constituent Kanji Cards**:
   - For every unique CJK kanji character (`plugin.NeedsPhoneticAnnotation(rune)`) in the top matched word (starting with the exact character under `CharOffsetInToken`), query `dictionary_kanji` to return `primary_meanings`, `on`/`kun` readings, stroke count, radical, grade, and `kanjivg_strokes_json`.

---

## 4. Output Contract (`LookupResult`)

```csharp
public sealed record LookupResult(
    string BookId,
    string ChapterHref,
    int HighlightStartTokenId,
    int HighlightEndTokenId,
    string MatchedSurface,
    string SentenceText,
    IReadOnlyList<RubyUnit> SentenceRuby,
    IReadOnlyList<TermEntryViewModel> Terms,
    IReadOnlyList<KanjiEntryViewModel> Kanji
);

public sealed record TermEntryViewModel(
    string Expression,
    string Reading,
    string ReadingNormalized,
    WordStatus UserStatus,                  // New, Learning, Known, Ignored
    string? ReviewedContextGloss,           // From study.config.json or user custom gloss
    string ReadingMatchStatus,              // 'epub_ruby_verified', 'mecab_matched', 'deinflected'
    IReadOnlyList<string> InflectionChain,  // e.g. ["past (-ta)", "polite (-masu)"]
    bool IsInBookDeck,                      // True if in book's Jiten CSV deck
    int? BookStudySetNumber,                // e.g. Set 4 (from vocabulary-sets.json if built)
    IReadOnlyList<PitchAccentInfo> Pitches, // Downstep positions for SVG pitch graph
    IReadOnlyList<FrequencyBadge> Frequencies,
    IReadOnlyList<DictionarySenseBlock> Senses
);
```

---

## 5. Interactive Popover Actions (Word Mining & Study Integration)

Inside the Dictionary Popover UI, the user can perform one-click actions on any `TermEntryViewModel`:
1. **Set Knowledge Status (`New` / `Learning` / `Known` / `Ignored`)**:
   - Immediately updates `vocabulary_state` in `library.db` and broadcasts a DOM class update (`ir-st-learning`, `ir-st-known`) to all occurrences of that lemma in the currently open chapter without reloading the page!
2. **Pin / Edit Reviewed Context Gloss**:
   - Saves a concise context gloss to `vocabulary_state.custom_gloss` and optionally writes it to `books/<book>/study.config.json` (`context_glosses[expression]`) so future PDF/HTML study-pack builds use the reviewed meaning.
3. **Add to Flashcard Deck**:
   - Appends the word, reading, dictionary sense, and the current untranslated Japanese sentence (with `SentenceRuby` furigana and target word highlight) to the user's flashcard export queue (compatible with `enrichement-extractor/immersive/card_export.py` schema).

