# First pipeline bug-fix pass

This implements a focused subset of the [refactoring plan](reader-integration-refactoring-plan.md). It changes enrichment behavior and library navigation, with regression fixtures independent of the supplied books.

## Fixed

1. **Incompatible homographs bypassed reading checks.** All three vocabulary matching paths now use `immersive/matching.py`. In 人気がある。, the incompatible 人気/ひとけ entry no longer gains an occurrence through normalized-token matching. Rejected entries do not receive a `first_appearance` value. When the EPUB explicitly supplies ひとけ, it takes priority over Sudachi's にんき guess.
2. **Context fallback could restore the wrong gloss.** A spelling match with incompatible source ruby can no longer silently replace the deck reading and retain its gloss. A reviewed `print_context` word override is required for that exception. A word-only override now correctly defaults its surface to the selected word. Existing reviewed corrections remain supported. New `context_gloss_status` fields distinguish dictionary excerpts from reviewed glosses and general character meanings; they do not claim automatic sense disambiguation.
3. **Paragraph concatenation created false matches and examples.** Tokenization and sentence splitting operate on individual paragraphs while keeping chapter-relative offsets. Vocabulary and grammar spans cannot cross paragraph edges. Regex grammar rules run within each paragraph. The regression fixture `<p>人</p><p>気</p>` no longer creates 人気. A word such as にがい that previously crossed a paragraph edge is intentionally no longer aligned.
4. **Chapter filters were ignored when they removed every chapter.** Filtering all chapters now raises a clear `ValueError`. Spine fallback occurs only when navigation has no usable spine entries. Filters also apply to the fallback section names.
5. **Spine fallback sections overlapped.** Without navigation, section 1 previously included later spine files as well. Fallback sections now use the next spine entry as their boundary, with a two-file regression fixture.
6. **Single-book builds hid earlier books.** Both commands now use `immersive/library.py` to index all existing HTML book pages. The index uses current reader titles, escapes their markup and is replaced only after a complete write. Concurrent publication and staging were subsequently implemented; see [reliable publication](reliable-publication.md).

## Reading evidence and limits

Vocabulary occurrences now contain `reading_match` with a source (`epub`, `sudachi`, `epub_and_sudachi` or `unavailable`), status and, when available, observed reading. Status is `exact_reading`, `inflected_reading` or `reading_unverified`.

The shared policy retains normal conjugations, supported irregular verbs such as 信じる, normalized colloquial adjective endings and orthographic alternatives such as 今日/きょう. Inflection checks require morphological evidence; they are not applied to every noun ending in a verb-like kana.

Numeric counters and Latin spellings are special cases: concatenated token readings do not reliably cover pronunciation changes or letter names. Those candidates remain explicitly unverified when readings disagree, including 本/ほん pronounced ぽん as a counter. Source readings remain in the evidence; dictionary readings and glosses are not silently rewritten. Retaining a candidate does not assert that its dictionary sense is the intended sense.

Stricter matching can reduce aligned-entry counts in existing books, particularly alternate readings, pronunciation variants and previous paragraph-joining mistakes. Dictionary entries remain in reader data even when unmatched. Counts should be reviewed as linguistic diagnostics; do not update expected totals solely to make a test pass.

## Verification

`tests/test_matching_regressions.py` covers homographs, source-ruby priority, multi-token readings, ordinary/irregular/colloquial inflections, numeric/Latin spellings, counters, paragraph boundaries, grammar boundaries, context fallback, chapter filtering and navigation-free sections. `tests/test_library_index.py` reproduces the navigation loss in both commands. The original failure cases were run before the fixes.

Run the complete suite from the project root:

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests
```

The supplied books can also be enriched in memory and validated against the reader schema without writing inputs or outputs. Existing PDFs, generated reader files and portable ZIPs retain their previously built behavior until an explicit rebuild.

Verification result for this pass: all 45 tests passed. Both supplied books were enriched in memory, passed schema validation and paragraph-span checks, and produced one context card per kanji (238 and 1,145). Input bytes were unchanged. Matched deck-record counts changed from 1,312 to 1,300 and from 3,590 to 3,544 against the previously generated reader files. These lower counts reflect the changed reading/boundary rules, not deleted dictionary entries; they are not a claim that every remaining linguistic ambiguity has been solved.

## Still planned

Explicit ambiguity handling is now implemented through source definition blocks, reading ties, review provenance and retained contextual candidates; see [the ambiguity guide](ambiguity-handling.md). Manifest-based cleanup/packaging, cancellation, locking and atomic publication are now implemented; see [reliable publication](reliable-publication.md). Data/rendering separation, packaged exporters, shared orchestration and optimization-safe validation were implemented subsequently; see [shared study data](study-data.md) and [orchestration/validation](orchestration-validation.md). Source maps and reader lookup are handled separately.
