Feature: Kanban's changes over GraphQL, each outcome a member of a union
  Every mutation calls the same KanbanData method its REST endpoint calls and
  answers with the facade's outcome as it is: a GraphQL union whose members
  are the outcome's cases (the card, Missing, Refused, Stale, Invalid, ...),
  registered once for each outcome type (Plasm.HotChocolate's AddPlasmUnion).
  A client asks for `__typename` and the fields of the cases it handles. A
  change needs someone signed in and is made in their name; reading a card
  is open to anyone (docs/kanban-host.md). In a request, <Guidance> stands for
  the card Guidance's id and <Guidance:version> for its version as stored.

  Background:
    Given the host serves a migrated database
    And "Margaret" is signed in with the password "moonshot-1969"
    And the board "apollo" allows 2 cards in progress

  Rule: The schema has a mutation for every change, each answering a union

    Scenario: Every change is a mutation whose type is its outcome
      When I read the schema
      Then the mutations are:
        | mutation     | answers             |
        | addCard      | AddCardOutcome!     |
        | addItem      | ChangeOutcome!      |
        | blockCard    | ChangeOutcome!      |
        | comment      | ChangeOutcome!      |
        | createBoard  | CreateBoardOutcome! |
        | dropCard     | ChangeOutcome!      |
        | relabelCard  | ChangeOutcome!      |
        | renameCard   | ChangeOutcome!      |
        | reopenCard   | ChangeOutcome!      |
        | sendAllBack  | SendAllBackOutcome! |
        | sendCardBack | ChangeOutcome!      |
        | shipCard     | ChangeOutcome!      |
        | startCard    | ChangeOutcome!      |
        | tickItem     | ChangeOutcome!      |
        | unblockCard  | ChangeOutcome!      |
      And the union "ChangeOutcome" is "CardView | Missing | Refused | Stale | Invalid"
      And the union "CardResult" is "CardView | Missing"

  Rule: Each case of an outcome is answered as itself

    Scenario: A board is created
      When I ask for 'mutation { createBoard(slug: "gemini", name: "Gemini", wipLimit: 3) { __typename ... on BoardSummary { slug } } }'
      Then the answer is a "BoardSummary"
      And the boards are "apollo: 0 backlog, 0 doing, 0 done; gemini: 0 backlog, 0 doing, 0 done"

    Scenario: Starting a card answers the card as stored
      Given the cards "Guidance" on "apollo"
      When I ask for 'mutation { startCard(card: "<Guidance>", version: <Guidance:version>) { __typename ... on CardView { version stage { __typename } } } }'
      Then the answer is a "CardView"
      And "Guidance" is in progress for "Margaret"

    Scenario: A rule's refusal is Refused, with the domain's code
      Given the cards "Guidance", "Docking", "Re-entry" on "apollo"
      And "Guidance" and "Docking" are in progress
      When I ask for 'mutation { startCard(card: "<Re-entry>", version: <Re-entry:version>) { __typename ... on Refused { code message } } }'
      Then the answer is a "Refused" with the code "WipLimitReached"
      And "Re-entry" is in the backlog

    Scenario: A change from an old copy is Stale
      Given the cards "Guidance" on "apollo"
      When I ask for 'mutation { renameCard(card: "<Guidance>", version: 0, title: "Navigation") { __typename ... on Stale { card } } }'
      Then the answer is a "Stale"
      And the card "Guidance" keeps its title

    Scenario: Input the domain cannot make values of is Invalid, by field
      When I ask for 'mutation { addCard(board: "apollo", title: "") { __typename ... on Invalid { problems { field message } } } }'
      Then the answer is an "Invalid" on the field "title"

    Scenario: A card that does not exist is Missing
      When I ask for 'mutation { shipCard(card: "00000000-0000-0000-0000-000000000001", version: 1) { __typename ... on Missing { what } } }'
      Then the answer is a "Missing"

    Scenario: A comment is the signed-in person's
      Given the cards "Guidance" on "apollo"
      When I ask for 'mutation { comment(card: "<Guidance>", body: "Check the gimbal") { __typename } }'
      Then the answer is a "CardView"
      And "Guidance" has the comment "Check the gimbal" by "Margaret"

    Scenario: Sending every card back answers how many
      Given the cards "Guidance", "Docking" on "apollo"
      And "Guidance" and "Docking" are in progress
      When I ask for 'mutation { sendAllBack(board: "apollo") { __typename ... on SentBack { cards } } }'
      Then the answer is a "SentBack"
      And "Guidance" is in the backlog

    Scenario: A label that is not one is Invalid, by its place
      Given the cards "Guidance" on "apollo"
      When I ask for 'mutation { relabelCard(card: "<Guidance>", version: <Guidance:version>, labels: ["flight", ""]) { __typename ... on Invalid { problems { field } } } }'
      Then the answer is an "Invalid" on the field "labels[1]"

  Rule: A change needs someone signed in; reading a card does not

    Scenario: A mutation from nobody signed in is refused before the facade
      Given the cards "Guidance" on "apollo"
      And the person signs out
      When I ask for 'mutation { startCard(card: "<Guidance>", version: <Guidance:version>) { __typename } }'
      Then the answer is the GraphQL error "AUTH_NOT_AUTHENTICATED"
      And "Guidance" is in the backlog

    Scenario: A card is read by its id
      Given the cards "Guidance" on "apollo"
      And the person signs out
      When I ask for '{ card(id: "<Guidance>") { __typename ... on CardView { title } } }'
      Then the answer is a "CardView"

    Scenario: A card that does not exist is Missing when read
      When I ask for '{ card(id: "00000000-0000-0000-0000-000000000001") { __typename ... on Missing { what } } }'
      Then the answer is a "Missing"
