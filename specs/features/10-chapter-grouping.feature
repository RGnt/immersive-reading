Feature: Chapters follow the table of contents
  As a reader
  I want one chapter per table-of-contents entry
  So that split files and illustration pages do not create extra chapters

  Scenario: A chapter split into two files is one chapter
    Given an EPUB whose ToC lists "001 ..." at part0005_split_000.html
    And part0005_split_001.html has no ToC entry
    When the book is imported
    Then there is one chapter "001 ..." with two member files
    And there is no "(Part 2)" chapter

  Scenario: Illustration pages stay inside their chapter
    Given an EPUB with text, an illustration page, then more text, all under one ToC entry
    When the chapter is opened
    Then the reader shows one continuous document in spine order
    And the illustration appears between the text parts

  Scenario: Relative images resolve per member file
    Given two members in different folders that both reference "../images/pic.jpg"
    When the chapter is rendered
    Then each image URL resolves relative to its own member file

  Scenario: Single-file chapters are unchanged
    Given a chapter with exactly one spine file
    Then its rendered HTML is identical to the previous behavior

  Scenario: Book without a table of contents
    Given an EPUB with no usable ToC
    Then each text spine file is a "Chapter N"
    And illustration-only files are merged into the preceding chapter

  Scenario: Front matter is still excluded
    Given a cover, illustrations page and an introduction before the first story chapter
    Then those records are marked excluded and not offered as readable chapters

  Scenario: Existing books upgrade when opened
    Given a book stored with the old per-file chapter list
    When I open it
    Then its chapters are rebuilt from the EPUB and the chapter model version is 2
    And books I have not opened are untouched

  Scenario: Reading position survives the upgrade
    Given saved progress inside the first file of a chapter
    When the book is upgraded
    Then I resume at the same position in the merged chapter

  Scenario: Position in a continuation file degrades gracefully
    Given saved progress or a bookmark inside a continuation file
    When the book is upgraded
    Then I land at the start of the owning chapter
    And the bookmark keeps its text and note

  Scenario: Failed upgrade does not break the book
    Given the EPUB file is missing or corrupt
    When I open the book
    Then the old chapter list is still used and the failure is logged

  Scenario: Links to a continuation file open the chapter
    Given a link in the book to part0005_split_001.html
    When I click it
    Then the owning chapter opens

  Scenario: Dry-run verification
    When "--verify-chapters" runs
    Then it reports before and after chapter totals for every book without writing to the database
