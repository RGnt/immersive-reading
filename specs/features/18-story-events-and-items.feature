Feature: Story pass: events and who has which item
  Spec 22. Covered by knowledge-graph tests (test_story_ops, test_story_pass, test_cli_story) and CharacterKnowledgeTests (C#).
  @manual scenarios need a real model endpoint.

  Background:
    Given the series "Loot Saga" was built with the story pass switched on

  Scenario: Events are gated by the reading position
    When the events are requested as of the start of chapter 2
    Then only the events of chapter 1 are listed
    And an event of chapter 2 is not listed

  Scenario: A dungeon contains its fights and drops
    When the events are requested after the dungeon chapter
    Then "竜との戦い" and "竜の宝箱" are listed under "古い迷宮の攻略"
    And the monster, the dropped items and the place are linked to them

  Scenario: Newer gear replaces older gear
    Given Ren wields an old knife
    When an iron sword is equipped in the weapon slot
    Then the knife is no longer equipped and its holding is closed as "replaced"
    When a flame sword is equipped in the weapon slot
    Then Ren's profile lists the flame sword as the weapon
    And the knife and the iron sword are listed as formerly equipped

  Scenario: Replacement is tagged on both items
    Given the knife was replaced by the iron sword and the iron sword by the flame sword
    When the items are requested after the dungeon chapter
    Then the flame sword is tagged "replaces iron sword" and the iron sword "replaced by flame sword"
    And the iron sword is tagged "replaces knife" and the knife "replaced by iron sword"
    When the items are requested before the dungeon chapter
    Then the iron sword is not yet tagged "replaced by" anything

  Scenario: An item is handed over
    When Ren gives the iron sword to Aya
    Then the iron sword belongs to Aya and not to Ren
    And handing it over is not a replacement

  Scenario: A card never names something the reader has not met
    Given an event summary of chapter 1 names an item that is only found in chapter 4
    When the cards are generated
    Then that event is not visible until the item has been met
    And generating the cards reports it as blocked

  Scenario: A holding does not show its own end
    Given the iron sword is sold in chapter 4
    When the items are requested as of chapter 4
    Then the iron sword is still held by Aya and no end is shown
    When the items are requested as of chapter 5
    Then the iron sword has no holder and its history shows it was lost

  Scenario: The story pass can be added to a series whose characters are built
    Given the characters of every chapter are built and the story pass was off
    When the build is started with the story pass on
    Then no character chapter is requested again
    And every chapter gets a story pass request

  Scenario: A failing story chapter does not block the characters
    Given the story pass fails in chapter 3
    Then every character chapter is built
    And the story pass stops at chapter 3 and resumes there on the next build

  Scenario: Old databases keep working
    Given a knowledge database of schema version 1
    When the builder opens it
    Then it is migrated in place and keeps its rows
    And a reader that has not been migrated shows no events and no items instead of failing

  @manual
  Scenario: A real model records a hunt, a drop and a new weapon
    Given a chapter in which the hero kills goblins, a sword drops and the hero equips it
    When the story pass runs against the real endpoint
    Then a hunt event with the goblins as participants exists
    And an item event for the sword exists
    And the sword is the hero's equipped weapon
