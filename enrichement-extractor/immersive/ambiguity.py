"""Explicit dictionary candidates and reading ties; no automatic sense selection."""
from collections import defaultdict

from .epub import uid
from .japanese import hira


def annotate_ambiguity(book):
    groups = defaultdict(list)
    unverified = 0
    for entry in book["vocabulary"]:
        candidates = []
        for record in entry.get("deck_records", []):
            text = record["fields"].get("Definitions", "").strip()
            if text:
                origin = {"kind": "csv", "file": record["file"], "csv_row": record["csv_row"]}
                candidates.append({"id": uid("definition", f'{entry["id"]}:csv:{record["file"]}:{record["csv_row"]}:{text}'),
                                   "text": text, "source": origin, "status": "source_definition_block"})
        for record in entry.get("dictionary_entries", []):
            for index, text in enumerate(record["glossary_text"]):
                if text:
                    origin = {"kind": "yomitan", "dictionary_id": record["dictionary_id"], "bank": record["bank"],
                              "row": record["row"], "glossary_index": index, "dictionary_title": record["dictionary_title"]}
                    candidates.append({"id": uid("definition", f'{entry["id"]}:{record["dictionary_id"]}:{record["bank"]}:{record["row"]}:{index}'),
                                       "text": text, "source": origin, "status": "source_definition_block"})
        # A flattened CSV definition is one source block, not a trustworthy
        # machine-readable list of senses/synonyms separated by commas.
        entry["definition_candidates"] = candidates
        reviewed = entry.get("context_gloss")
        entry["sense_selection"] = {
            "status": "reviewed_override" if reviewed else "unresolved" if candidates else "unavailable",
            "selected_candidate_ids": [c["id"] for c in candidates if reviewed and c["text"] == reviewed], "reviewed_text": reviewed,
            "source": {"kind": "book_config", "field": "context_glosses", "word": entry["word"]} if reviewed else None,
            "note": "Source definition blocks are retained; reading alignment does not select a contextual sense."}
        for occurrence in entry["occurrences"]:
            key = occurrence["chapter_id"], occurrence["start"], occurrence["end"]
            groups[key].append((entry, occurrence))
            unverified += occurrence.get("reading_match", {}).get("status") == "reading_unverified"
    ambiguities = []
    for (chapter_id, start, end), members in sorted(groups.items()):
        entries = {e["id"]: e for e, _ in members}
        readings = {hira(e["reading"]) for e in entries.values()}
        status = "ambiguous_reading" if len(readings) > 1 else "multiple_entries" if len(entries) > 1 else "single_candidate"
        ids = sorted(entries)
        for _, occurrence in members:
            occurrence["candidate_vocabulary_ids"] = ids
            occurrence["candidate_status"] = status
        if len(entries) > 1:
            ambiguities.append({"id": uid("ambiguity", f'{chapter_id}:{start}:{end}'), "chapter_id": chapter_id,
                                "start": start, "end": end, "status": status,
                                "candidate_vocabulary_ids": ids, "selected_vocabulary_id": None})
    book["ambiguities"] = ambiguities
    book["ambiguity_policy"] = {"definition_unit": "source_definition_block", "automatic_sense_selection": False,
                                "reading_ties": "All accepted candidates and source ruby retained; counter pronunciation remains explicitly unverified where needed.",
                                "review": "Only explicit configuration supplies reviewed glosses; no confidence percentages are invented."}
    book["quality"].update(ambiguous_reading_groups=sum(a["status"] == "ambiguous_reading" for a in ambiguities),
                           multiple_entry_groups=sum(a["status"] == "multiple_entries" for a in ambiguities),
                           unverified_reading_occurrences=unverified,
                           unresolved_sense_entries=sum(e["sense_selection"]["status"] == "unresolved" for e in book["vocabulary"]))
