Feature: Kanban as a Lambda function in C#
  The domain and its data code stay F#; a C# function serves them on AWS
  Lambda's custom runtime, as the C# host serves them over HTTP: it calls
  Boards, the class the seam writes over one F# type (Kanban.Data.Boards),
  opened once in the function's init, and switches over what it returns with
  no default arm. C# writes no query and composes no
  transaction. An event names a call and its arguments; the answer is the
  outcome's case, by name, with its value. The function is published natively
  for linux-arm64 inside Amazon Linux 2023 and invoked through the Lambda
  runtime interface emulator, on a database of its own for each scenario; no
  AWS account is used (docs/aot.md, "Kanban's C# function").

  Background:
    Given the Kanban function is published natively for Amazon Linux 2023
    And it runs in the Lambda runtime interface emulator over a migrated database
    And board "alpha" named "Alpha" allows 1 card in progress
    And board "alpha" has the cards "Write", "Test"

  Rule: The function is one native binary, opened before its first event

    Scenario: The binary is bootstrap, for Amazon Linux 2023, with no warning naming Plasm
      Then the publish has no IL warning naming Plasm
      And bootstrap needs no glibc newer than Amazon Linux 2023's

    Scenario: The function is built on the FSharp.Core a new project gets
      Then the publish resolved FSharp.Core at the version Plasm's applications run on

    Scenario: Boards is opened once, in the function's init
      When I invoke it with {"call":"board","slug":"alpha"}
      Then the function's log says Boards was opened once, before its first event

  Rule: A read answers with what it read, or that it is missing

    Scenario: A board is read by its slug, with its cards
      When I invoke it with {"call":"board","slug":"alpha"}
      Then the answer is a board "Alpha" whose cards are "Write (backlog, v1); Test (backlog, v1)"

    Scenario: A board no one has is missing
      When I invoke it with {"call":"board","slug":"nope"}
      Then it answers {"missing":{"what":"board nope"}}

  Rule: A change answers with the card, or why the card was not changed

    Scenario: The domain refuses a start beyond the board's limit
      Given "Ada" has started "Write"
      When "Bob" starts "Test" from version 1
      Then it answers {"refused":{"code":"WipLimitReached","message":"This board allows 1 cards in progress at once. Finish or send one back first."}}

    Scenario: Of two renames from the same copy, the second is stale
      When "Ada" renames "Write" to "Write it" from version 1
      Then the answer is the card "Write" titled "Write it" at version 2
      When "Bob" renames "Write" to "Write more" from version 1
      Then the answer is stale, naming the card "Write"

    Scenario: A start of a card no one has is missing
      When "Ada" starts a card no one has from version 1
      Then it answers {"missing":{"what":"card"}}

  Rule: A create answers with what was made, or what was invalid, field by field

    Scenario: A title the domain cannot make a value of is invalid
      When I invoke it with {"call":"addCard","board":"alpha","title":"  "}
      Then it answers {"invalid":{"problems":[{"field":"title","message":"is required"}]}}

    Scenario: A slug another board has is invalid
      When I invoke it with {"call":"createBoard","slug":"alpha","name":"Again","wipLimit":3}
      Then it answers {"invalid":{"problems":[{"field":"slug","message":"is already taken"}]}}

  Rule: Nothing null crosses to F#

    # The event is read with required properties and nullable annotations
    # respected, so an argument left out or null stops in C#, as the
    # invocation's error, before any F# is called.
    Scenario Outline: An event with an argument left out, or null, is refused before Boards is called
      When I invoke it with <event>
      Then the invocation fails with a JsonException naming "title"
      And the board "alpha" still has 2 cards

      Examples:
        | event                                         |
        | {"call":"addCard","board":"alpha"}            |
        | {"call":"addCard","board":"alpha","title":null} |

  Rule: A function may open without the start-up check, which these scenarios run

    # Every scenario's Background opens Boards with its check, in this
    # binary, over Kanban's migrations: that check is what a function that
    # skips it at start relies on (docs/aot.md, "Kanban's C# function").
    Scenario: Opened without its start-up check, the function answers as when checked
      Given it runs again, opening Boards without its start-up check
      When "Ada" renames "Write" to "Write it" from version 1
      Then the answer is the card "Write" titled "Write it" at version 2
      When I invoke it with {"call":"board","slug":"nope"}
      Then it answers {"missing":{"what":"board nope"}}
      And the function's log says Boards was opened without its start-up check

    Scenario: Only the check finds a table that differs from the model, at start
      Given the table "accounts" has its column "password_hash" renamed to "hash"
      When it runs again, opening Boards with its start-up check
      And I invoke it with {"call":"board","slug":"alpha"}
      Then its init fails, its log naming "accounts"
      When it runs again, opening Boards without its start-up check
      And I invoke it with {"call":"board","slug":"alpha"}
      Then the answer is a board "Alpha" whose cards are "Write (backlog, v1); Test (backlog, v1)"
