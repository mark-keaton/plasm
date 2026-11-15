Feature: Cards carry labels

  A label is a word, shared by every board: many cards carry the same label
  and a card carries up to five. Which card carries which label is stored in a
  table of its own.

  Background:
    Given the board "apollo" allows 2 cards in progress
    And the card "Write the spec" on "apollo"

  Scenario: A card takes labels
    When "Write the spec" is labelled "urgent, docs"
    Then the card "Write the spec" has the labels "docs, urgent"

  Scenario: Labelling again replaces the labels
    Given "Write the spec" is labelled "docs, urgent"
    When "Write the spec" is labelled "urgent, review"
    Then the card "Write the spec" has the labels "review, urgent"

  Scenario: Labelling with nothing removes the labels
    Given "Write the spec" is labelled "docs, urgent"
    When "Write the spec" is labelled ""
    Then the card "Write the spec" has the labels ""

  Scenario: Labels are tidied before they are compared
    When "Write the spec" is labelled " Docs , docs, URGENT"
    Then the card "Write the spec" has the labels "docs, urgent"

  Scenario: A label is stored once, however many cards carry it
    Given the card "Build the lander" on "apollo"
    And "Write the spec" is labelled "urgent"
    When "Build the lander" is labelled "urgent, hardware"
    Then the label "urgent" is stored once
    And the card "Write the spec" has the labels "urgent"

  Scenario: A card carries at most five labels
    When "Write the spec" is labelled "one, two, three, four, five, six"
    Then the request is refused: "A card carries at most 5 labels."
    And the card "Write the spec" has the labels ""

  Scenario: A label is a short word
    When "Write the spec" is labelled "docs, this label is far too long to be a label"
    Then the request is invalid for the field "labels[1]": "must be 1 to 24 characters"

  Scenario: A done card takes no labels
    Given "Write the spec" is dropped because "out of scope"
    When "Write the spec" is labelled "docs"
    Then the request is refused: "This card is done. Reopen it to change it."

  Scenario: Labelling a copy that is out of date is refused
    Given Grace has an old copy of "Write the spec"
    And "Write the spec" is renamed "Write the specification"
    When Grace labels her copy "docs"
    Then the request is stale

  Scenario: Cards are found by label
    Given the card "Build the lander" on "apollo"
    And the card "Train the crew" on "apollo"
    And "Write the spec" is labelled "urgent, docs"
    And "Build the lander" is labelled "hardware"
    And "Train the crew" is labelled "urgent"
    When the cards on "apollo" labelled "urgent" are read
    Then the cards found are "Write the spec, Train the crew"

  Scenario: A card with several labels is found once
    Given "Write the spec" is labelled "urgent, docs"
    When the cards on "apollo" labelled "docs" are read
    Then the cards found are "Write the spec"

  Scenario: Another board's cards are not found
    Given the board "gemini" allows 2 cards in progress
    And the card "Fit the heat shield" on "gemini"
    And "Fit the heat shield" is labelled "urgent"
    And "Write the spec" is labelled "urgent"
    When the cards on "gemini" labelled "urgent" are read
    Then the cards found are "Fit the heat shield"
