# language: en
@phase2 @enrichment @anglesharp @mecab @plugins
Feature: Pre-Render AngleSharp DOM Enrichment and Pluggable Language Architecture
  As a language learner reading EPUBs in the desktop reader
  I want chapters to be tokenized and enriched with furigana and token spans in .NET before rendering
  So that there is zero layout shift (CLS), author ruby annotations are preserved, and any language can be added as a plugin

  Background:
    Given the Language Plugin Registry has loaded "JapaneseLanguagePlugin" ("ja") and "WhitespaceLanguagePlugin" ("*")
    And the "AngleSharpDomEnricher" pipeline is initialized

  Rule: Author-supplied EPUB <ruby> tags are treated as ground truth and never corrupted

    Scenario: Preserving author <ruby> reading over MeCab default reading for homographs
      Given a Japanese XHTML paragraph containing "<p><ruby>人気<rt>ひとけ</rt></ruby>のない森で、木の実を食べた。</p>"
      When "AngleSharpDomEnricher" enriches the chapter using "JapaneseLanguagePlugin"
      Then the token for "人気" is wrapped with a deterministic "data-tid" attribute
      And its internal "<ruby>人気<rt>ひとけ</rt></ruby>" markup is preserved intact
      And the "ChapterTokenMap" records the token for "人気" with:
        | Lemma | Reading | ReadingSource |
        | 人気  | ひとけ   | EpubAuthor    |
      And the "<rt>ひとけ</rt>" ruby text is never tokenized as a separate word in the paragraph

  Rule: Unannotated kanji receive okurigana-aligned generated <ruby> tags

    Scenario Outline: Aligning kanji stems and plain okurigana in generated ruby markup
      Given a plain text word "<surface>" tokenized with reading "<reading>" and no author ruby
      When "OkuriganaRubyAligner" aligns the word for DOM injection
      Then the generated XHTML inside the token span is "<expected_html>"

      Examples:
        | surface  | reading   | expected_html                                                        |
        | 食べた    | たべた     | <ruby class="ir-ruby-gen">食<rt>た</rt></ruby>べた                    |
        | 振り返る  | ふりかえる | <ruby class="ir-ruby-gen">振<rt>ふ</rt></ruby>り<ruby class="ir-ruby-gen">返<rt>かえ</rt></ruby>る |
        | 大人      | おとな     | <ruby class="ir-ruby-gen">大人<rt>おとな</rt></ruby>                  |

  Rule: Inline formatting and non-text elements (<svg>, <img>, <style>, <script>) are preserved without corruption

    Scenario: Enriching a token split across inline formatting tags (<em> or <strong>)
      Given an XHTML paragraph "<p><em>人</em>気がある</p>"
      When "AngleSharpDomEnricher" enriches the paragraph
      Then both slices of "人気" share the same "data-tid" attribute with "data-part='0'" and "data-part='1'"
      And the "<em>" element structure remains valid XHTML

    Scenario: Skipping <svg>, <script>, and <style> subtrees during DOM traversal
      Given an XHTML chapter containing an inline "<svg><text>表紙</text><image xlink:href='../Images/cover.jpg'/></svg>"
      When "AngleSharpDomEnricher" enriches the chapter
      Then the "<svg>" subtree is serialized without any "<span class='ir-tok'>" wrappers inside it
      And no whitespace is inserted between adjacent CJK "<span class='ir-tok'>" elements in paragraphs

  Rule: New languages can be added as plugins implementing ILanguagePlugin without modifying core enricher code

    Scenario: Enriching a space-delimited language chapter using a pluggable ILanguagePlugin
      Given an EPUB with language "es" (Spanish) and paragraph "<p>Los libros están en la mesa.</p>"
      When "AngleSharpDomEnricher" enriches the chapter using the active "ILanguagePlugin" for "es"
      Then each word token ("Los", "libros", "están", "en", "la", "mesa") is wrapped in "<span class='ir-tok' data-tid='...'>"
      And inter-word spaces and punctuation are preserved outside the word spans

