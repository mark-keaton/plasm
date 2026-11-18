Feature: Kanban over REST
  Every endpoint calls one KanbanData method and answers through one mapping
  from outcome to status: 200 or 201 with the value, 404 when something is
  missing, 422 with the domain's code when a rule refuses, 409 when the
  caller's copy is old, 400 with each field and why when the input cannot be
  made into the domain's values. Reading is open to anyone; a change needs
  someone signed in and is made in their name (docs/kanban-host.md).

  Background:
    Given the host serves a migrated database
    And "Margaret" is signed in with the password "moonshot-1969"
    And the board "apollo" allows 2 cards in progress

  Rule: Boards

    Scenario: A new board is listed with its counts
      When the board "gemini" is created, allowing 3 cards in progress
      Then the answer is 201
      And the boards are "apollo: 0 backlog, 0 doing, 0 done; gemini: 0 backlog, 0 doing, 0 done"

    Scenario: A slug belongs to one board
      When the board "apollo" is created, allowing 3 cards in progress
      Then the answer is 400 for the field "slug": "is already taken"

    Scenario: A board that does not exist is not found
      When I read the board "nowhere"
      Then the answer is 404

  Rule: A card moves through its stages as the domain allows

    Scenario: A new card is in the backlog
      When the card "Guidance" is added to "apollo"
      Then the answer is 201
      And "Guidance" is in the backlog

    Scenario: Starting within the limit
      Given the cards "Guidance", "Docking" on "apollo"
      When the person signed in starts "Guidance"
      Then the answer is 200
      And "Guidance" is in progress for "Margaret"

    Scenario: Starting beyond the limit is refused by the domain
      Given the cards "Guidance", "Docking", "Re-entry" on "apollo"
      And "Guidance" and "Docking" are in progress
      When the person signed in starts "Re-entry"
      Then the answer is 422 with the code "WipLimitReached"
      And "Re-entry" is in the backlog

    Scenario: A card in progress ships
      Given the cards "Guidance" on "apollo"
      And "Guidance" is in progress
      When "Guidance" ships
      Then the answer is 200
      And "Guidance" is shipped

    Scenario: Dropping a card needs a reason
      Given the cards "Guidance" on "apollo"
      When "Guidance" is dropped because ""
      Then the answer is 422 with the code "DropNeedsReason"

    Scenario: A request that leaves out a field never reaches the domain
      Given the cards "Guidance" on "apollo"
      When "Guidance" is dropped with the body '{"version": 1}'
      Then the answer is 400
      And "Guidance" is in the backlog

    Scenario: A dropped card is reopened to the backlog
      Given the cards "Guidance" on "apollo"
      And "Guidance" is dropped because "out of scope"
      When "Guidance" is reopened
      Then the answer is 200
      And "Guidance" is in the backlog

    Scenario: A card in progress is sent back
      Given the cards "Guidance" on "apollo"
      And "Guidance" is in progress
      When "Guidance" is sent back
      Then the answer is 200
      And "Guidance" is in the backlog

    Scenario: A blocked card does not ship until it is unblocked
      Given the cards "Guidance" on "apollo"
      And "Guidance" is in progress
      And "Guidance" is blocked because "waiting for the simulator"
      When "Guidance" ships
      Then the answer is 422 with the code "CardBlocked"
      When "Guidance" is unblocked
      And "Guidance" ships
      Then the answer is 200

    Scenario: Renaming from an old copy is refused
      Given the cards "Guidance" on "apollo"
      When "Guidance" is renamed "Navigation" from its first version, after it has changed
      Then the answer is 409
      And the card "Guidance" keeps its title

  Rule: Comments, labels and checklists are changes to a card

    Scenario: A comment is the signed-in person's
      Given the cards "Guidance" on "apollo"
      When the person signed in comments "Check the gimbal" on "Guidance"
      Then the answer is 200
      And "Guidance" has the comment "Check the gimbal" by "Margaret"

    Scenario: A card has at most five labels
      Given the cards "Guidance" on "apollo"
      When "Guidance" is labelled "a, b, c, d, e, f"
      Then the answer is 422 with the code "TooManyLabels"

    Scenario: A label that is not one is invalid, by its place
      Given the cards "Guidance" on "apollo"
      When "Guidance" is labelled "flight, "
      Then the answer is 400 for the field "labels[1]"

    Scenario: A card with items left does not ship
      Given the cards "Guidance" on "apollo"
      And "Guidance" is in progress
      And "Guidance" has the items "Align", "Test"
      And the item "Align" of "Guidance" is ticked
      When "Guidance" ships
      Then the answer is 422 with the code "ItemsLeft"

  Rule: Reading work and finished cards

    Scenario: Sending every card back in one change
      Given the cards "Guidance", "Docking" on "apollo"
      And "Guidance" and "Docking" are in progress
      When every card on "apollo" is sent back
      Then the answer is 200 with 2 cards sent back
      And "Guidance" is in the backlog
      And "Docking" is in the backlog

    Scenario: My work is the cards I am working on
      Given the cards "Guidance", "Docking" on "apollo"
      And "Guidance" is in progress
      When I read my work
      Then my work is "apollo: Guidance"

    Scenario: Finished cards a page at a time
      Given the cards "One", "Two", "Three" on "apollo"
      And "One", "Two" and "Three" are dropped because "cancelled"
      When I read page 2 of "apollo"'s finished cards, 2 to a page
      Then the page has 1 card of 3, on 2 pages

    Scenario: A page larger than allowed is invalid
      When I read page 1 of "apollo"'s finished cards, 51 to a page
      Then the answer is 400 for the field "size": "must be between 1 and 50"
