Feature: Reader menu bar POS category selector
  As a Japanese learner
  I want to choose which parts of speech are highlighted from the reader menu bar
  So that I can focus on one grammar topic at a time

  Background:
    Given the "posHighlighter" plugin is enabled
    And a book is open in the reader

  Scenario: Highlights button is shown only while the plugin is enabled
    Then the reader top bar shows a "Highlights" button
    When the plugin is disabled
    Then the "Highlights" button is not shown

  Scenario: Popover lists every category
    When I click "Highlights"
    Then I see 13 categories each with a color swatch, a name and a checkbox
    And all checkboxes are ticked by default

  Scenario: Selecting a single category updates the open chapter live
    When I click "Highlights"
    And I choose the "Particles" preset
    Then only single-kana and multi-kana particles are colored in the chapter
    And verbs, adjectives and nouns are uncolored
    And the chapter is not reloaded and my scroll position is unchanged

  Scenario: Selection persists
    Given I have enabled only "Verbs"
    When I restart the application and open another chapter
    Then only verbs are colored
    And Settings > Installed Plugins shows the same selection

  Scenario: Master switch
    When I turn the master switch off
    Then no part-of-speech colors are shown and the category checkboxes are disabled
    When I turn it on again
    Then my previous category selection is restored

  Scenario: Unticking everything
    When I click "None"
    Then no part-of-speech colors are shown
    And furigana color is unaffected

  Scenario: Legacy configuration
    Given the saved configuration has no "enabledCategories" value
    Then all categories are treated as enabled

  Scenario: Unknown category ids are rejected
    When a client sends setCategories with an unknown id "bogus"
    Then "bogus" is ignored and not stored

  Scenario: Popover dismissal
    Given the popover is open
    When I press Escape or click outside it
    Then the popover closes
