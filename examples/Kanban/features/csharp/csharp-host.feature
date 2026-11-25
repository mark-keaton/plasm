Feature: Kanban served by a C# host
  The domain and its data code stay F#; a C# minimal API serves them. C# calls
  Boards, the class the seam writes over one F# type (Kanban.Data.Boards), with
  the requests' bodies and a CancellationToken, and answers with what it
  returns: views the seam writes of the domain's own records, and outcomes
  whose cases ToHttp turns into responses. C# writes no query, no transaction,
  no view and no mapping. The host runs as its own process and each scenario
  talks to it over HTTP, on a database of its own (docs/csharp.md).

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

    # The seam's view of the domain's Card, in its fields' order, leaving out
    # what Seam.cs omits: the board, the time it was made, its blocker and
    # checklist, which this host never changes. A label is the domain's Tag.
    Scenario: A card's JSON is the domain's card as the seam writes it: camelCase, with no null in it
      When I ask for board "alpha"
      Then the first card's JSON, without its id, is {"title":"Write","stage":{"kind":"backlog"},"comments":[],"labels":[],"version":1}

    # The stage is the domain's union: JSON is written as its case alone, so
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
    Scenario Outline: A body with an argument null or left out is refused before Boards is called
      When I post <body> to "<path>"
      Then the answer is 400
      And the answer is not one of Boards'
      And board "alpha" still has 2 cards

      Examples:
        | path                | body                                   |
        | /boards/alpha/cards | {"title":null}                         |
        | /boards/alpha/cards | {}                                     |
        | /boards             | {"slug":"beta","name":null,"wipLimit":2} |
        | /boards             | {"slug":"beta","wipLimit":2}           |

  Rule: The host runs on the FSharp.Core a new project gets

    # Fable.Remoting holds the F# server below FSharp.Core 10.2; the C# host
    # does not reach it, so it resolves what a new project gets.
    Scenario: The host's build resolves the FSharp.Core a new project gets
      Then the host's build resolved FSharp.Core at the version Plasm's applications run on
