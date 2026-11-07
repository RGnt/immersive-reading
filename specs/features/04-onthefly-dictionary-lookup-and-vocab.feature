# language: en
@phase3 @dictionary @lookup @vocabulary
Feature: On-the-Fly Sub-5ms Dictionary Lookup, Deinflection, and Vocabulary Tracking
  As a reader hovering or clicking words in an enriched EPUB chapter
  I want instant dictionary definitions, deinflection chains, kanji stroke diagrams, and 1-click vocabulary status updates
  So that I can look up unfamiliar words and track my vocabulary mastery without breaking reading flow

  Background:
    Given "dictionaries.db" has indexed a Yomitan format-3 Japanese-English dictionary and KANJIDIC2 + KanjiVG
    And the chapter "OEBPS/Text/ch01.xhtml" of book "book-4e0541b184c4ace9" is cached in "ChapterEnrichmentCache"

  Rule: Dictionary lookup resolves inflected verbs, multi-token compounds, and homographs in under 5 ms

    Scenario Outline: Deinflecting conjugated Japanese verbs and adjectives to their dictionary headwords
      Given the user taps a token span whose surface window starts with "<surface>"
      When "DictionaryLookupEngine.QueryAsync" executes for that token
      Then the top returned "TermEntryViewModel" has Expression "<lemma>" and Reading "<reading>"
      And the inflection chain includes "<expected_rule>"
      And the lookup completes in under 5 ms

      Examples:
        | surface       | lemma   | reading | expected_rule          |
        | 食べさせられた  | 食べる   | たべる   | passive                |
        | 行かなかった    | 行く     | いく     | negative               |
        | 美しくなかった  | 美しい   | うつくしい| past                   |
        | 読んじゃった    | 読む     | よむ     | contraction (-chau)    |

    Scenario: Longest-prefix compound phrase matching across token boundaries (up to 16 chars)
      Given the paragraph contains tokens for "気", "に", "ならなかった" ("気にならなかった")
      When the user clicks the token for "気" (tokenId 10)
      Then "DictionaryLookupEngine" scans token-boundary prefixes up to 16 characters
      And ranks the multi-token expression "気になる" ("きになる") above the single-character noun "気"
      And returns "HighlightStartTokenId = 10" through the end token of "気にならなかった"

    Scenario: Author EPUB <ruby> reading disambiguates homographs during dictionary lookup
      Given token 42 has surface "人気" and EPUB author ruby "ひとけ" ("ReadingSource = EpubAuthor")
      And the dictionary contains both "人気 [にんき] (popularity)" and "人気 [ひとけ] (sign of life; human presence)"
      When the user triggers "lookup.query" on token 42
      Then "人気 [ひとけ] (sign of life; human presence)" is ranked first with "ReadingMatchStatus = 'epub_ruby_verified'"

    Scenario: Extracting constituent Kanji cards with KanjiVG stroke paths
      Given the user looks up the word "海辺" ("うみべ")
      When the lookup result is returned
      Then the "Kanji" list contains entries for "海" and "辺" in character order
      And each kanji card includes primary meanings, ON/KUN readings, stroke count, and ordered KanjiVG SVG stroke paths

  Rule: Updating a word's knowledge status immediately updates vocabulary_state and live chapter DOM classes

    Scenario: Marking a word as Known hides its generated furigana in Unknown-Only mode without page reload
      Given the reader is in furigana mode "UnknownAndLearningOnly" ("html.ir-furigana-unknown")
      And the word "森" ("もり") currently has status "New" and displays generated furigana "<ruby class='ir-ruby-gen'>森<rt>もり</rt></ruby>"
      When the user presses shortcut "3" (or clicks "Known") in the dictionary popover for "森"
      Then "vocabulary_state" in "library.db" updates ("ja", "森", "もり") to "WordStatus.Known"
      And all token spans with lemma "森" in the open chapter update their class from "ir-st-new" to "ir-st-known"
      And the CSS rule "html.ir-furigana-unknown .ir-st-known ruby.ir-ruby-gen > rt" immediately hides the furigana on "森"

