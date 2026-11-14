Feature: Image-only books are skipped at import with a warning
  As a reader
  I want comics and scanned image books to be ignored with a clear warning
  So that the library only contains books the reader can actually work with

  Scenario: A manga-style EPUB is skipped during a directory scan
    Given a folder with one text novel and one EPUB where every page is an image
    When I scan the folder
    Then the novel is imported
    And the image-only EPUB is not imported
    And the result reports 1 skipped image-only book with a warning naming the file

  Scenario: The skipped book is not a failure
    When an image-only EPUB is skipped
    Then the failed-files count does not increase

  Scenario: Warning is shown in the scan dialog
    Given a scan skipped image-only books
    Then the dialog shows an amber warning with the count and an explanation
    And I can expand the list of skipped files with their reasons

  Scenario: Importing a single image-only EPUB is rejected clearly
    When I import an image-only EPUB directly
    Then the import fails with a message saying image-only books are not supported yet
    And no book, cover or search entry is created

  Scenario: Light novels with many illustrations are still imported
    Given an EPUB with long text chapters and many full-page illustrations
    When I scan the folder
    Then it is imported

  Scenario: Fixed layout alone is not enough
    Given an EPUB that declares fixed layout but has real text on every page
    Then it is imported

  Scenario: Books already in the library are never modified
    Given an image-only book was imported earlier
    When I scan its folder again
    Then it is skipped with a warning
    And its library entry, chapters and progress are unchanged

  Scenario: Profiling problems never block a normal book
    Given a text book containing an unreadable spine document
    Then the book is imported

  Scenario: Dry run lists existing image-only books
    When "--verify-chapters" runs
    Then it reports how many existing library books the rule would flag, with titles
    And it changes nothing
