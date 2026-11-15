Feature: Character Knowledge plugin
  Spec 20. Covered by CharacterKnowledgeTests (C#) and --verify-ui; @manual scenarios need a real model endpoint.

  Background:
    Given the series "Test Saga" with volumes 1 and 2 in the library

  Scenario: The builder is not installed
    Given the knowledge-graph folder has no virtual environment
    When the Characters tab is opened
    Then Build is disabled and the exact setup command is shown

  Scenario: Browsing follows the reading position
    Given volume 1 was built
    And the reader is in chapter 2 of volume 1
    When the character list is requested without a chapter
    Then it lists the characters known before chapter 2
    And a character first mentioned in chapter 2 is not listed
    And choosing a later chapter in the selector shows more characters

  Scenario: A character who turns out to be someone else
    Given the masked man and the teacher are linked in chapter 3
    When the profile of the masked man is requested as of chapter 2
    Then it shows the masked man only
    When the profile of the masked man is requested as of chapter 3
    Then it shows the merged profile of the teacher

  Scenario: Volumes are not renumbered when a volume is added
    Given volumes 1 and 2 were built
    When volume 3 is added to the library
    Then volumes 1 and 2 keep their numbers and volume 3 is number 3

  Scenario: A volume that sorts before built ones asks for a rebuild
    Given only volumes 2 and 3 were built
    When volume 1 is added to the library
    Then the status says the order changed and a rebuild is needed

  Scenario: Build, resume and cancel
    Given a build is running
    When Cancel is pressed
    Then the builder process is stopped and the job is cancelled
    And finished chapters are kept
    When Build is pressed again
    Then already built chapters with unchanged text are skipped

  Scenario: The API key never leaves the environment
    Given an API key is set in the plugin settings
    When a build starts
    Then the key is not in the builder's command line, config file or log

  @manual
  Scenario: Whole series on the real model
    Given the FreeToken endpoint at http://127.0.0.1:1919/v1
    When the whole series is built from the series modal
    Then progress advances chapter by chapter and the Characters tab lists characters as of the reading position
