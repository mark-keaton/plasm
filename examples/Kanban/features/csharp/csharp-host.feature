Feature: Kanban served by a C# host
  The domain and its data code stay F#; a C# minimal API serves them. C# calls
  one F# type, KanbanData, with plain arguments and a CancellationToken, and
  switches over what it returns: a read that can miss returns null, a write
  that can be refused returns an outcome whose cases C# matches exhaustively.
  C# writes no query and composes no transaction. The host runs as its own
  process and each scenario talks to it over HTTP, on a database of its own
  (docs/csharp.md).

  Background:
    Given the C# host serves a migrated database
    And board "alpha" named "Alpha" allows 1 card in progress
    And board "alpha" has the cards "Write", "Test"

  Rule: Reads answer with what the F# queries read

    Scenario: The boards are listed with what was read of their cards
      Given board "beta" named "Beta" allows 2 cards in progress
      When I ask for the boards
      Then the answer is 200
      And the boards are "Alpha: 2 in backlog, newest Test, Write; Beta: 0 in backlog, newest none"

    Scenario: A board is read by its slug, its cards with their comments and labels
      Given the card "Write" has a comment by "Ada" saying "first" and the label "urgent"
      When I ask for board "alpha"
      Then the answer is 200
      And its cards are "Write (backlog, v1, labels urgent, comments Ada: first); Test (backlog, v1, labels none, comments none)"

    Scenario: A board no one has is not found
      When I ask for board "nope"
      Then the answer is 404
      And the answer's JSON is {"what":"board nope"}

    Scenario: A card's JSON is the C# host's own: camelCase, with no null in it
      When I ask for board "alpha"
      Then the first card's JSON, without its id, is {"title":"Write","stage":{"kind":"backlog"},"version":1,"labels":[],"comments":[]}

    # The stage is a union-shaped type: JSON is written as its case alone, so
    # each case carries a constant kind, and only the facts its stage has.
    Scenario: A card in progress says who works on it, and since when, in its stage
      Given "Ada" has started "Write"
      When I ask for board "alpha"
      Then the stage of "Write" in the answer has the kind "doing", the assignee "Ada" and a start time
      And the stage of "Test" in the answer is {"kind":"backlog"}

  Rule: Writes answer with the outcome the F# facade returns

    Scenario: The domain refuses a start beyond the board's limit
      Given "Ada" has started "Write"
      When "Bob" starts "Test" from version 1
      Then the answer is 422
      And the answer's JSON is {"code":"WipLimitReached","message":"This board allows 1 cards in progress at once. Finish or send one back first."}
      And the card "Test" is in the backlog at version 1

    Scenario: Of two renames from the same copy, the second loses
      When "Ada" renames "Write" to "Write it" from version 1
      Then the answer is 200
      When "Bob" renames "Write" to "Write more" from version 1
      Then the answer is 409
      And the answer names the card "Write"
      And the card "Write" is titled "Write it" at version 2

    Scenario: Input the domain cannot make values of is refused field by field
      When I add a card titled "  " to board "alpha"
      Then the answer is 400
      And the answer's JSON is {"problems":[{"field":"title","message":"is required"}]}

    Scenario: A slug another board has is refused as that field's error
      When I create board "alpha" named "Again" allowing 3 cards in progress
      Then the answer is 400
      And the answer's JSON is {"problems":[{"field":"slug","message":"is already taken"}]}

    Scenario: A card added to a board no one has is not found
      When I add a card titled "Lost" to board "nope"
      Then the answer is 404
      And the answer's JSON is {"what":"board nope"}

  Rule: Nothing null crosses to F#

    # The minimal API reads a body through the source-generated context,
    # inserted into the host's JSON options; the options must respect nullable
    # annotations and required constructor parameters, or a null reaches F#
    # (docs/csharp.md).
    Scenario Outline: A body with an argument null or left out is refused before KanbanData is called
      When I post <body> to "<path>"
      Then the answer is 400
      And the answer is not one of KanbanData's
      And board "alpha" still has 2 cards

      Examples:
        | path                | body                                   |
        | /boards/alpha/cards | {"title":null}                         |
        | /boards/alpha/cards | {}                                     |
        | /boards             | {"slug":"beta","name":null,"wipLimit":2} |
        | /boards             | {"slug":"beta","wipLimit":2}           |
