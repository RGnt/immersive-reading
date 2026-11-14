Feature: Illustration pages fill the reader
  Merged chapters contain several spine files; illustration pages must not shrink.

  Scenario: Vertical book, chapter of six illustration pages
    Given a vertical-rl book whose chapter is six full-page illustrations
    When I open the chapter
    Then each illustration is as wide as the reader window and fitted inside the text area height
    And I move between pages with the horizontal scroll

  Scenario: Horizontal mode uses the whole window width
    Given the same chapter in horizontal mode
    Then each illustration uses the window width, not the 800 px text column

  Scenario: Text parts are unaffected
    Given a chapter with text files and one illustration file
    Then only the illustration file gets the full-page layout
