"""Source definition blocks and accepted reading ties stay explicit."""
import copy
import csv
import json
from pathlib import Path
import tempfile
import unittest

from immersive.context import contextual_cards
from immersive.enrichment import enrich_book
from immersive.study_data import prepare_study_data
from immersive.validation import validate_reader, ValidationError
from test_reader_enrichment import fixture


def text(body):
    return '<html><body><h1 id="a">第一章</h1><p>'+body+'</p><h1 id="b">第二章</h1><p>終わり。</p></body></html>'


class AmbiguityTests(unittest.TestCase):
    def test_flattened_definitions_are_blocks_not_inferred_comma_separated_senses(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), rows=[["海", "うみ", "sea, ocean (large, open water)", ""], ["海", "うみ", "another supplied block", ""]])
            book = enrich_book(folder, resources)
            entry = book["vocabulary"][0]
            self.assertEqual([c["text"] for c in entry["definition_candidates"]], ["sea, ocean (large, open water)", "another supplied block"])
            self.assertEqual(entry["sense_selection"]["status"], "unresolved")
            self.assertFalse(entry["sense_selection"]["selected_candidate_ids"])
            self.assertFalse(book["ambiguity_policy"]["automatic_sense_selection"])

    def test_unverified_counter_readings_retain_both_candidates_in_context(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=text("十回で終わる。"), rows=[
                ["十回", "じゅっかい", "ten times", ""], ["十回", "じっかい", "ten repetitions", ""]])
            book = enrich_book(folder, resources)
            self.assertTrue(all(e["occurrences"] for e in book["vocabulary"]))
            group = next(a for a in book["ambiguities"] if a["status"] == "ambiguous_reading")
            self.assertIsNone(group["selected_vocabulary_id"])
            self.assertEqual(len(group["candidate_vocabulary_ids"]), 2)
            card = contextual_cards(book, book["chapters"][0])[0]
            self.assertEqual(card["context_selection"]["status"], "ambiguous")
            self.assertIsNone(card["context_entry_id"])
            self.assertEqual({c["reading"] for c in card["context_candidates"]}, {"じゅっかい", "じっかい"})
            self.assertEqual(card["context_gloss_status"], "ambiguous_dictionary_candidates")

    def test_explicit_source_ruby_resolves_homograph_reading_without_selecting_a_sense(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=text('<ruby>人気<rt>ひとけ</rt></ruby>がある。'), rows=[
                ["人気", "ひとけ", "human presence", ""], ["人気", "にんき", "popularity", ""]])
            book = enrich_book(folder, resources)
            self.assertTrue(book["vocabulary"][0]["occurrences"])
            self.assertFalse(book["vocabulary"][1]["occurrences"])
            self.assertFalse(book["ambiguities"])
            self.assertEqual(book["vocabulary"][0]["sense_selection"]["status"], "unresolved")

    def test_rejected_homograph_does_not_supply_study_readings_or_glosses(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=text("人気がある。"), rows=[
                ["人気", "ひとけ", "human presence", ""], ["人気", "にんき", "popularity", ""]])
            book = enrich_book(folder, resources)
            study = prepare_study_data(book)
            card = study.vocabulary["chapter-01"][0]
            self.assertEqual(card["reading"], "にんき")
            self.assertEqual(card["meaning"], "popularity")
            self.assertEqual([c["text"] for c in card["definition_candidates"]], ["popularity"])
            self.assertEqual(book["vocabulary"][0]["definition_candidates"][0]["text"], "human presence")

    def test_same_reading_entries_and_reviewed_glosses_keep_all_source_candidates(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            with (folder / "deck.csv").open("w", encoding="utf-8", newline="") as stream:
                csv.writer(stream).writerows([["Word", "ReadingKana", "Definitions", "JmDictWordId"],
                                             ["海", "うみ", "sea", "1"], ["海", "うみ", "ocean", "2"]])
            book = enrich_book(folder, resources)
            self.assertTrue(any(a["status"] == "multiple_entries" for a in book["ambiguities"]))
            card = contextual_cards(book, book["chapters"][0])[0]
            self.assertEqual(len(card["context_candidates"]), 2)
            self.assertIsNone(card["context_selection"]["selected_entry_id"])
            (folder / "study.config.json").write_text(json.dumps({"context_glosses": {"海": "sea in this passage"}}), encoding="utf-8")
            reviewed = enrich_book(folder, resources)
            for entry in reviewed["vocabulary"]:
                self.assertEqual(entry["sense_selection"]["status"], "reviewed_override")
                self.assertTrue(entry["definition_candidates"])
                self.assertTrue(entry["sense_selection"]["source"])

    def test_fabricated_sources_or_automatic_sense_selection_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            book = enrich_book(folder, resources)
            changed = copy.deepcopy(book)
            changed["vocabulary"][0]["definition_candidates"][0]["text"] = "invented"
            with self.assertRaisesRegex(ValidationError, "definition disagrees"): validate_reader(changed)
            changed = copy.deepcopy(book)
            changed["vocabulary"][0]["sense_selection"]["selected_candidate_ids"] = [changed["vocabulary"][0]["definition_candidates"][0]["id"]]
            with self.assertRaisesRegex(ValidationError, "cannot claim"): validate_reader(changed)


if __name__ == "__main__":
    unittest.main()
