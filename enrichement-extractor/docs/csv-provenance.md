# CSV input and export provenance

`source_files[].sha256` identifies the current file bytes. `input_sha256` records bytes read before this run's explicit rewrite; `original_sha256` identifies the first observed input in the recognized CSV lineage, and `exported_sha256` identifies recognized pipeline output. Before a rewrite, input and current hashes agree. Immediately afterward, input and exported hashes can differ. A later reader-only run observes the exported bytes as its current input while retaining the original lineage hash.

Only explicit CSV updates write `.enrichment/deck-provenance.json` beside the book. An OS lock serializes deck updates. The journal records rewrite intent atomically before replacing a CSV; matching actual bytes determine whether an intent took effect. A failed write does not falsely label the input as exported. Repeated unchanged updates do not append history. A CSV changed since enrichment is rejected before rewriting.

External CSV edits start a new observed lineage; prior journal history remains available. Original means first observed by this tooling, not a claim about the dictionary publisher's original export. Hashes are retained, not complete byte backups. Reader-only enrichment does not create journal files. Cache hits refresh current provenance from disk, and the derived `first_appearance` column remains excluded from analysis keys.

Deck-update reports expose input/export/original hashes. Reader output remains compatible with older source records that contain only `file` and `sha256`. The journal is local audit data, not a signed attestation. Explicit source CSV mutation remains outside the output-publication transaction.
