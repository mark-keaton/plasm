Feature: A kanban board with an F# domain at its centre

  Every rule below is decided by plain F# functions over plain F# types
  (src/Shared/Domain.fs), which reference no package. Plasm stores what they
  decide and ASP.NET Core serves it. The scenarios call the application's API
  the way the browser does.

  Background:
    Given the board "apollo" allows 2 cards in progress

  Rule: Input becomes domain values, or is refused field by field

    Scenario: Unacceptable input reports every field at once
      When a board "Bad Slug!" named "" is created allowing 0 cards in progress
      Then the request is invalid for the fields "slug, name, wipLimit"

    Scenario: A slug belongs to one board
      When a board "apollo" named "Another Apollo" is created allowing 3 cards in progress
      Then the request is invalid for the field "slug": "is already taken"

    Scenario: A card needs a title
      When a card "   " is added to "apollo"
      Then the request is invalid for the field "title": "is required"

    Scenario: A card needs a board
      When a card "Orphan" is added to "nowhere"
      Then the board "nowhere" is not found

  Rule: A card's stage carries exactly the facts that exist in it

    Scenario: A new card waits in the backlog
      When a card "Write the spec" is added to "apollo"
      Then the card "Write the spec" is in the backlog

    Scenario: Starting a card records who started it and when
      Given the card "Write the spec" on "apollo"
      And the time is 2026-11-14 09:30
      When Ada starts "Write the spec"
      Then the card "Write the spec" is in progress for Ada since 2026-11-14 09:30

    Scenario: Shipping a card records when
      Given Ada is working on "Write the spec" on "apollo"
      And the time is 2026-11-15 17:00
      When "Write the spec" is shipped
      Then the card "Write the spec" was shipped at 2026-11-15 17:00

    Scenario: Dropping a card keeps the reason
      Given the card "Gold plating" on "apollo"
      And the time is 2026-11-15 17:00
      When "Gold plating" is dropped because "out of scope"
      Then the card "Gold plating" was dropped at 2026-11-15 17:00 because "out of scope"

    Scenario: A card sent back has no assignee any more
      Given Ada is working on "Write the spec" on "apollo"
      When "Write the spec" is sent back
      Then the card "Write the spec" is in the backlog

    Scenario: A reopened card starts over
      Given Ada is working on "Write the spec" on "apollo"
      And "Write the spec" is shipped
      When "Write the spec" is reopened
      Then the card "Write the spec" is in the backlog

    Scenario: Stored facts that contradict each other are reported, not repaired
      Given Ada is working on "Write the spec" on "apollo"
      And the database loses the assignee of "Write the spec"
      When the board "apollo" is read
      Then the request fails as unavailable

  Rule: The domain refuses what makes no sense, and a refusal changes nothing

    Scenario: A card in the backlog cannot ship
      Given the card "Write the spec" on "apollo"
      When "Write the spec" is shipped
      Then the request is refused: "This card is not in progress."
      And the card "Write the spec" is in the backlog

    Scenario: A card is not dropped without a reason
      Given the card "Write the spec" on "apollo"
      When "Write the spec" is dropped because "  "
      Then the request is refused: "Say why the card is dropped."

    Scenario: A card in progress is not started twice
      Given Ada is working on "Write the spec" on "apollo"
      When Grace starts "Write the spec"
      Then the request is refused: "Ada is already working on this card."
      And the card "Write the spec" is in progress for Ada

    Scenario: A done card is closed
      Given Ada is working on "Write the spec" on "apollo"
      And "Write the spec" is shipped
      When "Write the spec" is renamed "Rewrite the spec"
      Then the request is refused: "This card is done. Reopen it to change it."
      And the board "apollo" has the cards "Write the spec"

    Scenario: Only a done card is reopened
      Given the card "Write the spec" on "apollo"
      When "Write the spec" is reopened
      Then the request is refused: "Only a done card can be reopened."

  Rule: A board's limit holds over all its cards

    Scenario: One card too many is refused
      Given Ada is working on "First" on "apollo"
      And Ada is working on "Second" on "apollo"
      And the card "Third" on "apollo"
      When Grace starts "Third"
      Then the request is refused: "This board allows 2 cards in progress at once. Finish or send one back first."
      And the card "Third" is in the backlog

    Scenario: Finishing a card makes room
      Given Ada is working on "First" on "apollo"
      And Ada is working on "Second" on "apollo"
      And the card "Third" on "apollo"
      And "First" is shipped
      When Grace starts "Third"
      Then the card "Third" is in progress for Grace

    @committed
    Scenario: The limit holds when two people start cards at the same moment
      Given the board "gemini" allows 1 cards in progress
      And the card "Left" on "gemini"
      And the card "Right" on "gemini"
      When Ada starts "Left" and Grace starts "Right" at the same moment, 20 times over
      Then every time exactly one of them succeeded
      And every time the other was refused: "This board allows 1 cards in progress at once. Finish or send one back first."

  Rule: A change based on an old copy is refused

    Scenario: The second of two edits to the same copy is stale
      Given the card "Write the spec" on "apollo"
      And Grace has an old copy of "Write the spec"
      And "Write the spec" is renamed "Write the specification"
      When Grace renames her copy "Write the docs"
      Then the request is stale
      And the board "apollo" has the cards "Write the specification"

  Rule: Comments belong to their card

    Scenario: Comments come back with the card, oldest first
      Given the card "Write the spec" on "apollo"
      And the time is 2026-11-14 09:00
      And Ada comments "Started an outline" on "Write the spec"
      And the time is 2026-11-14 10:00
      When Grace comments "Looks good" on "Write the spec"
      Then the card "Write the spec" has the comments "Ada: Started an outline, Grace: Looks good"

    Scenario: A done card takes no comments
      Given Ada is working on "Write the spec" on "apollo"
      And "Write the spec" is shipped
      When Grace comments "One more thing" on "Write the spec"
      Then the request is refused: "This card is done. Reopen it to change it."

  Rule: A card can be blocked, with a reason and since when

    Scenario: Blocking a card records why and since when
      Given Ada is working on "Write the spec" on "apollo"
      And the time is 2026-11-15 09:00
      When "Write the spec" is blocked because "waiting for the supplier"
      Then the card "Write the spec" is blocked since 2026-11-15 09:00 because "waiting for the supplier"

    Scenario: An unblocked card has no blocker any more
      Given Ada is working on "Write the spec" on "apollo"
      And "Write the spec" is blocked because "waiting for the supplier"
      When "Write the spec" is unblocked
      Then the card "Write the spec" is not blocked

    Scenario: A blocked card does not ship
      Given Ada is working on "Write the spec" on "apollo"
      And "Write the spec" is blocked because "waiting for the supplier"
      When "Write the spec" is shipped
      Then the request is refused: "This card is blocked: waiting for the supplier. Unblock it first."

    Scenario: A card is not blocked without a reason
      Given the card "Write the spec" on "apollo"
      When "Write the spec" is blocked because "  "
      Then the request is refused: "Say what blocks the card."

    Scenario: A card is not blocked twice
      Given the card "Write the spec" on "apollo"
      And "Write the spec" is blocked because "waiting for the supplier"
      When "Write the spec" is blocked because "no budget"
      Then the request is refused: "This card is already blocked: waiting for the supplier."

    Scenario: Only a blocked card is unblocked
      Given the card "Write the spec" on "apollo"
      When "Write the spec" is unblocked
      Then the request is refused: "This card is not blocked."

    Scenario: A dropped card is no longer blocked
      Given the card "Write the spec" on "apollo"
      And "Write the spec" is blocked because "waiting for the supplier"
      When "Write the spec" is dropped because "out of scope"
      Then the card "Write the spec" is not blocked

    Scenario: A board's blocked cards, the longest blocked first
      Given the card "First" on "apollo"
      And the card "Second" on "apollo"
      And the card "Third" on "apollo"
      And the time is 2026-11-15 10:00
      And "Second" is blocked because "no budget"
      And the time is 2026-11-15 09:00
      And "Third" is blocked because "waiting for the supplier"
      When the blocked cards on "apollo" are read
      Then the cards found are "Third, Second"

  Rule: Boards are summarised by stage

    Scenario: The list of boards counts each board's cards by stage
      Given Ada is working on "First" on "apollo"
      And the card "Second" on "apollo"
      And the card "Third" on "apollo"
      And "Third" is dropped because "duplicate"
      When the boards are listed
      Then the board "apollo" is listed with 1 in the backlog, 1 in progress and 1 done

    Scenario: The list of boards shows each board's three newest cards
      Given the card "First" on "apollo"
      And the card "Second" on "apollo"
      And the card "Third" on "apollo"
      And the card "Fourth" on "apollo"
      When the boards are listed
      Then the board "apollo" is listed with the newest cards "Fourth, Third, Second"

    Scenario: A board without cards is listed with no newest cards
      Given the board "gemini" allows 2 cards in progress
      And the card "First" on "apollo"
      When the boards are listed
      Then the board "gemini" is listed with the newest cards ""
      And the board "apollo" is listed with the newest cards "First"

    Scenario: However many boards and cards, listing sends the same three queries
      Given the board "gemini" allows 2 cards in progress
      And the card "First" on "apollo"
      And the card "Second" on "apollo"
      And the card "Third" on "gemini"
      When the boards are listed
      Then the database was sent 3 SELECT

    Scenario: An unknown board is not found
      When the board "voyager" is read
      Then the board "voyager" is not found

  Rule: A board can be created with its first cards, all together or not at all

    Scenario: A board starts with the cards it was given, in the order given
      When a board "gemini" named "Gemini" is created allowing 2 cards in progress, with the cards "Design, Build, Fly"
      Then the board "gemini" has the cards "Design, Build, Fly"
      And the card "Build" is in the backlog

    Scenario: The board and its cards are written by one statement each
      When a board "gemini" named "Gemini" is created allowing 2 cards in progress, with the cards "Design, Build, Fly"
      Then the database was sent 1 INSERT into "boards" and 3 into "cards"

    Scenario: One unacceptable card and the board is not created
      When a board "gemini" named "Gemini" is created allowing 2 cards in progress, with the cards "Design, , Fly"
      Then the request is invalid for the field "cards[1]": "is required"
      And no board "gemini" is stored

    Scenario: A taken slug and none of the cards is stored
      When a board "apollo" named "Apollo again" is created allowing 2 cards in progress, with the cards "Stray"
      Then the request is invalid for the field "slug": "is already taken"
      And no card "Stray" is stored

  Rule: Everything in progress can be sent back at once

    Scenario: All work in progress returns to the backlog
      Given Ada is working on "First" on "apollo"
      And Grace is working on "Second" on "apollo"
      And the card "Third" on "apollo"
      When everything in progress on "apollo" is sent back
      Then 2 cards were sent back
      And the card "First" is in the backlog
      And the card "Second" is in the backlog

    Scenario: However many cards, the database is sent one UPDATE
      Given Ada is working on "First" on "apollo"
      And Grace is working on "Second" on "apollo"
      When everything in progress on "apollo" is sent back
      Then the database was sent 1 UPDATE of "cards"

    Scenario: Cards that are done stay done
      Given Ada is working on "First" on "apollo"
      And the time is 2026-11-15 17:00
      And "First" is shipped
      When everything in progress on "apollo" is sent back
      Then 0 cards were sent back
      And the card "First" was shipped at 2026-11-15 17:00

    Scenario: Another board's work is left alone
      Given the board "gemini" allows 2 cards in progress
      And Ada is working on "First" on "apollo"
      And Grace is working on "Elsewhere" on "gemini"
      When everything in progress on "apollo" is sent back
      Then the card "Elsewhere" is in progress for Grace

    Scenario: A copy from before is stale afterwards
      Given Ada is working on "First" on "apollo"
      And Grace has an old copy of "First"
      And everything in progress on "apollo" is sent back
      When Grace renames her copy "Second thoughts"
      Then the request is stale

  Rule: What someone is working on is read across boards

    Scenario: Work in progress on every board, oldest first
      Given the board "gemini" allows 2 cards in progress
      And the time is 2026-11-14 10:00
      And Ada is working on "Fit the heat shield" on "gemini"
      And the time is 2026-11-14 09:00
      And Ada is working on "Write the spec" on "apollo"
      And Grace is working on "Train the crew" on "apollo"
      When Ada asks what she is working on
      Then the work is "apollo: Write the spec, gemini: Fit the heat shield"

    Scenario: Cards and their boards are read with one query
      Given the board "gemini" allows 2 cards in progress
      And Ada is working on "Fit the heat shield" on "gemini"
      And Ada is working on "Write the spec" on "apollo"
      When Ada asks what she is working on
      Then the database was sent 1 SELECT

    Scenario: Finished work is no longer listed
      Given Ada is working on "Write the spec" on "apollo"
      And "Write the spec" is shipped
      When Ada asks what she is working on
      Then the work is ""

  Rule: Done cards are read a page at a time, newest first

    Scenario: The first page has the newest
      Given 5 cards finished on "apollo", an hour apart
      When page 1 of the done cards on "apollo" is read, 2 to a page
      Then the page has the cards "Card 5, Card 4"
      And there are 5 done cards on 3 pages

    Scenario: The last page has what is left
      Given 5 cards finished on "apollo", an hour apart
      When page 3 of the done cards on "apollo" is read, 2 to a page
      Then the page has the cards "Card 1"

    Scenario: A page past the end is empty
      Given 5 cards finished on "apollo", an hour apart
      When page 4 of the done cards on "apollo" is read, 2 to a page
      Then the page has the cards ""
      And there are 5 done cards on 3 pages

    Scenario: Cards in other stages are not on any page
      Given 5 cards finished on "apollo", an hour apart
      And Ada is working on "Still going" on "apollo"
      When page 1 of the done cards on "apollo" is read, 50 to a page
      Then the page has the cards "Card 5, Card 4, Card 3, Card 2, Card 1"

    Scenario: Cards finished at the same moment are each on exactly one page
      Given 5 cards finished on "apollo", at the same moment
      When every page of the done cards on "apollo" is read, 2 to a page
      Then together the pages have each of the 5 cards once

    Scenario Outline: A page that cannot exist is refused
      When page <page> of the done cards on "apollo" is read, <size> to a page
      Then the request is invalid for the field "<field>": "<message>"

      Examples:
        | page | size | field | message                  |
        | 0    | 10   | page  | must be at least 1       |
        | 1    | 0    | size  | must be between 1 and 50 |
        | 1    | 51   | size  | must be between 1 and 50 |
