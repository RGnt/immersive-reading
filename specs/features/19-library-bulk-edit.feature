Feature: Library bulk editing
  Spec 23. Covered by BulkEditTests, LibraryBulkEditRpcTests and --verify-ui.

  Scenario: Put many books into one series, numbered by title
    Given three books titled "Saga 10", "Saga 2" and "Saga 1" selected in that order
    When I edit them with series "The Saga" and volume numbers in title order
    Then "Saga 1" is volume 1, "Saga 2" is volume 2 and "Saga 10" is volume 3
    And all three belong to the series "The Saga"

  Scenario: The preview is exactly what is written
    Given a selection and a series edit with title-order numbering
    When the preview is requested
    Then it lists each book with its current and its new series and volume
    And nothing has been written
    When the edit is applied
    Then every book has the series and volume the preview showed

  Scenario: Use the number written in the title
    Given books titled "Hero 4" and "Lonely book" where "Lonely book" has volume 9
    When I edit them with series "Hero" and "use the number written in each title"
    Then "Hero 4" is volume 4
    And "Lonely book" keeps volume 9 and the preview counts it as "no number to use"

  Scenario: Untouched fields stay untouched, ticked empty fields are cleared
    Given books in the series "S"
    When I edit only the publisher
    Then the series is unchanged
    When I tick the series with an empty name
    Then the series is cleared and the dialog warned that it would be

  Scenario: Select everything the filter matches, not only the page
    Given 250 books of which 120 carry the tag "pick" and a page size of 100
    When I filter by the tag and press "select all 120"
    Then 120 books are selected

  Scenario: Tags are added and removed in one go
    Given two books tagged "keep" and "drop"
    When I add "keep" and remove "drop" for both
    Then both books have only the tag "keep"

  Scenario: A bad request changes nothing
    When I send an unknown numbering mode
    Then the request fails
    And no book was changed
