# Dictionary and reading ambiguity

The pipeline keeps dictionary candidates separate from reviewed contextual glosses. It does not infer a correct sense from CSV order, dictionary priority or an example sentence. Examples remain Japanese; no language-model or translation stage is introduced.

## Reader data

`vocabulary[].definition_candidates` contains **source definition blocks**, each with an ID, text and source locator. A flattened Jiten `Definitions` field is one block; commas can separate synonyms or parenthetical material, so they are not interpreted as reliable sense boundaries. All source CSV rows are retained, including different blocks merged under one vocabulary ID. Yomitan candidates follow actual glossary-block boundaries and refer back to dictionary/bank/row/glossary index; original structured glossary data remains in `dictionary_entries`.

`sense_selection.status` is `unresolved`, `unavailable`, or `reviewed_override`. A match with source ruby can establish a reading while leaving the sense unresolved. A single definition block is not proof of a unique contextual meaning. Only explicit `context_glosses` configuration supplies a reviewed gloss; it records configuration provenance and retains all dictionary candidates. Exact reviewed block text can identify candidate IDs; an independently phrased reviewed gloss remains explicit reviewed text.

Each accepted occurrence records `candidate_vocabulary_ids` and `candidate_status` (`single_candidate`, `multiple_entries`, `ambiguous_reading`). Top-level `ambiguities` lists spans with multiple accepted entries and makes no automatic selection. It distinguishes competing readings from different entries sharing a reading. Overlapping phrases/constituents with different spans are not automatically treated as conflicting senses. Source-ruby compatibility, morphology and paragraph/token boundaries still determine acceptance. Counter pronunciation can remain explicitly unverified, including attested dictionary/source pronunciation variants; it is not discarded simply because the strings differ.

`quality` and diagnostics count ambiguous-reading groups, unverified reading occurrences and unresolved sense entries. Dictionary uncertainty is nonfatal. Validators check references, spans, source definition text and explicit review provenance, and reject automatic meaning selections without review.

## Study display

A chapter word/example can still be chosen by a stable display heuristic: character word preference, alignment, occurrence position and stable entry-ID tie breaking. `context_selection` names this as a display choice rather than a resolved sense. `context_candidates` retains all viable entries at the displayed occurrence, including definition blocks and reading evidence. Competing candidates display alternatives with `context_gloss_status="ambiguous_dictionary_candidates"` and no falsely selected entry ID. Reviewed configuration remains an explicit exception; word-level gloss review does not automatically settle a reading tie.

Study grouping now excludes rejected homographs when a spelling has matched entries. Previously, an unmatched reading/definition could leak into its matched spelling's combined card. Rejected entries remain in reader data and their supplied dictionary information is preserved; they simply do not supply the matched study card's meaning or reading.

HTML exposes original definition candidates in expandable sections. Card JSON carries candidates and sense-selection metadata. Kanji PDFs use compact `CANDIDATES` / `UNREVIEWED` labels for word glosses; primary character meanings retain their separate source labels. Vocabulary PDFs keep their compact dictionary-definition rows. These displayed definitions are study aids, not sentence translations or claims of automatic sense resolution.

Existing `print_context` corrections and attributed supplemental definitions remain supported. A reviewed gloss can guide the display toward a matching source entry, and unselected candidates are retained. Source mapping and the reader lookup API remain separate workstreams; consumers can use the candidate IDs/statuses without changing normalized coordinates.

## Verification

All 126 tests passed. The authored study pack produced and verified seven PDFs (37 pages), HTML, card JSON and a ZIP; candidate labels and layout were visually checked.

Fixtures cover flattened commas and parentheticals, multiple CSV blocks, same-reading entries, unresolved counter readings, source-ruby homographs, rejected-reading study isolation, explicit reviewed glosses, and fabricated-source/automatic-selection rejection. Existing homograph, inflection, counter, CSV-example/furigana and chapter-order regressions remain covered. A full authored study pack verified the new labels and candidate data without changing the approved card layout or regenerating existing books.
