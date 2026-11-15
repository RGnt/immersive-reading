Feature: Spoiler-gated character knowledge graph
  Spec 18. Pure-logic scenarios run in the Python suite of knowledge-graph/ with a scripted fake LLM (no network).
  @spike scenarios are manual checks with the real model. Status: specification only, nothing implemented yet.

  Background:
    Given a synthetic series "Test Saga" with volume 1 (chapters 0 to 3) and volume 2 (chapters 0 to 1)
    And chapter 2 of volume 1 reveals that the masked man 仮面の男 is really the teacher 青木先生
    And chapter 3 of volume 1 reveals that 青木先生 is the culprit, using the canary word 真犯人

  # ---------- Position model ----------

  Scenario Outline: Positions compare by volume, then chapter, then offset
    Given position <a> and position <b>
    Then <a> is <order> than <b>

    Examples:
      | a      | b      | order   |
      | 1:2:0  | 1:3:0  | earlier |
      | 1:9:0  | 2:0:0  | earlier |
      | 2:1:5  | 2:1:5  | equal   |

  Scenario: The reader's position comes from the open book and its volume ordinal
    Given the reader has volume 2 open at chapter 1
    Then the reader position is 2:1 and all of volume 1 counts as read

  Scenario: Later volumes are never visible
    Given the reader is in volume 1 chapter 3
    Then no fact from volume 2 is visible

  # ---------- Gate ----------

  Scenario: Facts appear only after their chapter
    Given a fact established in volume 1 chapter 1
    Then it is not visible as of 1:1
    And it is visible as of 1:2

  Scenario: A fact is as late as its latest evidence
    Given a fact supported by one quote in chapter 1 and one in chapter 3
    Then it is not visible as of 1:3
    And it is visible as of 2:0

  Scenario: Names are revealed at their own position
    Given the masked man and the teacher are separate entities until chapter 2
    When I ask for the display names as of 1:2
    Then the entity is shown as "仮面の男"
    And the name "青木先生" is not present anywhere in the view
    When I ask as of 1:3
    Then both names belong to one character

  Scenario: Relationships end without erasing history
    Given a friendship from chapter 1 that ends in chapter 3
    Then as of 1:2 the relationship is "friend"
    And as of 1:3 it is still "friend" because the ending is written in chapter 3, which the reader has not read yet
    And as of 2:0 it is not a current relationship
    And as of 2:0 the history lists it as an ended relationship

  Scenario: Entity ids carry no names
    Then every entity id matches "e" followed by digits

  # ---------- The spoiler canary ----------

  Scenario Outline: Nothing about the culprit leaks before it is revealed
    When I build the profile of the teacher as of <position>
    And I render the prompt block
    Then the text does not contain "真犯人"
    And the hidden-name scanner finds no name that is not yet visible

    Examples:
      | position |
      | 1:1      |
      | 1:2      |

  Scenario: The scanner blocks a leak in free text
    Given a stored summary that names 青木先生 while the entity is still "仮面の男" at that position
    When I render the block as of 1:2
    Then rendering fails with the hidden name reported
    And nothing is returned to the caller

  # ---------- Causal extraction ----------

  Scenario: The model only sees the past
    When the builder processes volume 1 chapter 2
    Then the request contains the chapter 2 text
    And the request contains the snapshot as of 1:2
    And the request contains no text from chapter 3 or volume 2
    And the snapshot does not contain "青木先生"

  Scenario: Evidence quotes are located by code
    Given the model answers with a quote that occurs once in the chapter
    Then the stored evidence offsets point at that quote
    And the fact's position is the end of the quote

  Scenario: An invented quote rejects the operation
    Given the model answers with a quote that is not in the chapter text
    Then the operation is stored as rejected with a reason
    And no fact is created from it

  Scenario: An operation with no evidence is rejected
    Given the model answers with an add_attribute that has no evidence
    Then it is rejected

  Scenario: Unknown entity references are rejected
    Given an operation referring to the id "e9999"
    Then it is rejected with "unknown entity"

  Scenario: Malformed JSON is retried once and then reported
    Given the fake model answers invalid JSON twice
    Then the chapter is marked failed with the reason
    And chapters already built are untouched

  # ---------- Build state ----------

  Scenario: A failed build resumes where it stopped
    Given the build failed after chapter 1
    When I run the build with resume
    Then chapter 1 is not sent again
    And the build continues with chapter 2

  Scenario: Rebuilding from a chapter rebuilds everything after it and nothing before it
    When I rebuild from volume 1 chapter 2
    Then chapters 0 and 1 are untouched
    And chapters 2 and 3 and volume 2 are built again on the new facts
    And each chapter has exactly one set of operations
    And entity ids of the rebuilt chapters are new, never reused

  Scenario: A changed book marks chapters stale
    Given the text of volume 1 chapter 2 changed
    Then that chapter is reported stale

  Scenario: Volume 2 starts with everything from volume 1
    When the builder processes volume 2 chapter 0
    Then its snapshot contains the facts of volume 1

  # ---------- Findings from the real model (spike K0) ----------

  Scenario: Quotes tolerate edge punctuation but not changed wording
    Given the text "お帰りなさいませ、ユナ様。アップデート情報"
    Then the quote "「お帰りなさいませ、ユナ様」" is found
    And a quote with changed words inside is not found

  Scenario: One verified quote is enough, and the fact is positioned conservatively
    Given an operation with one verified quote and one paraphrased quote
    Then it is applied with only the verified quote stored
    And its position is the end of the text the model was shown

  Scenario: Invented names are rejected
    Given an add_entity whose name does not occur in the chapter text
    Then it is rejected
    And an add_entity whose quotes do not contain the name is rejected

  Scenario: A truncated answer is retried as two halves
    Given the model's answer is cut off at the server's output limit
    Then nothing of that answer is applied
    And the text is split in two and each half is asked with its own snapshot
    And the chapter fails with a clear reason only when a piece is too small to split

  Scenario: Thinking is off by default for extraction
    Given a reasoning model that spends its output budget on reasoning
    Then the request disables thinking unless the configuration turns it on
    And an answer that is empty because of reasoning is reported with that hint

  Scenario: A character whose snapshot was blocked uses the latest earlier one
    Given the snapshot of a character at the reader's chapter was blocked by the scanner
    Then the lookup returns that character's latest earlier snapshot
    And never a later one

  # ---------- Snapshots for the app ----------

  Scenario: Snapshots exist for every chapter boundary
    When snapshots are generated
    Then each character with enough presence has a profile for each chapter boundary where it is visible

  Scenario: The character list follows the reader
    When I list characters as of 1:1 and as of 1:3
    Then the second list contains characters the first does not
    And no character in the first list has a name that is not yet visible

  Scenario: Snapshot lookup needs no gating code in the app
    Given the reader is at volume 1 chapter 2
    When the app asks for a character's profile
    Then it reads the stored snapshot for 1:2 and does no filtering itself

  # ---------- Contracts and safety ----------

  Scenario: The API key is never on the command line
    When the builder is started by the app
    Then the key is passed in the environment variable KNOWLEDGE_API_KEY
    And the process arguments contain no key

  Scenario: Progress is reported as JSON lines
    When a chapter is processed
    Then a "chapter_done" line with counts is written to standard output

  Scenario: The study pipeline is untouched
    Then enrichement-extractor has no new dependency and makes no network call

  @spike
  Scenario: Real model on a real chapter
    Given a real chapter of a real book and the configured large-context model
    Then the share of valid answers, verified evidence and time per chapter are recorded in the spike report
