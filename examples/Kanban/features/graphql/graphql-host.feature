Feature: Kanban's cards over GraphQL, filtered and ordered by the database
  A GraphQL client chooses a request's where and order. Kanban.Data declares,
  once, which fields of a card a client may filter and order by (CardFields),
  each a quotation over the domain's Card with the operators it takes, and
  checks them when it opens. The host's filter and sort inputs are generated
  from those declarations (Plasm.HotChocolate), so a client can name nothing
  else; Plasm builds the request's where and order onto the cards query, and
  the database does both. The host runs as its own process; each scenario
  talks to it over HTTP, on a database of its own, and reads the SQL it logs
  (docs/hotchocolate.md).

  Background:
    Given the GraphQL host serves a migrated database
    And board "alpha" has the cards:
      | title          | stage   | assignee | version | created              |
      | Write the spec | backlog |          | 1       | 2026-11-18T09:00:00Z |
      | Build it       | doing   | Ada      | 2       | 2026-11-18T10:00:00Z |
      | Ship it        | backlog |          | 3       | 2026-11-18T11:00:00Z |

  Rule: A request's where and order are done by the database

    Scenario: Cards in progress or past version 2, newest first
      When I ask for '{ cards(where: { or: [{ stage: { eq: "doing" } }, { version: { gt: 2 } }] }, order: [{ created: DESC }]) { title } }'
      Then the cards are "Ship it, Build it"
      And the cards' SQL has a WHERE and ends with 'ORDER BY "c"."created" DESC'

    Scenario: Conditions on two fields together
      When I ask for '{ cards(where: { title: { neq: "Ship it" }, version: { lte: 2 } }, order: [{ title: ASC }]) { title } }'
      Then the cards are "Build it, Write the spec"
      And the cards' SQL has a WHERE and ends with 'ORDER BY "c"."title"'

    Scenario: No where and no order read every card, as the database returns them
      When I ask for '{ cards { title } }'
      Then there are 3 cards
      And the cards' SQL has no WHERE and no ORDER BY

  Rule: The answer is the application's view

    Scenario: A card's stage is a GraphQL union over its cases
      When I ask for '{ cards(where: { stage: { eq: "doing" } }) { title stage { __typename ... on InProgress { kind assignee } } } }'
      Then the answer's JSON is {"data":{"cards":[{"title":"Build it","stage":{"__typename":"InProgress","kind":"doing","assignee":"Ada"}}]}}

  Rule: What the declarations do not allow is refused before any SQL

    Scenario: An operator no field was declared with is not in the schema
      When I ask for '{ cards(where: { title: { contains: "it" } }) { title } }'
      Then the answer is the error "The specified input object field `contains` does not exist."
      And no cards' SQL was sent

    Scenario: A value the field cannot make its own is refused, naming the field
      When I ask for '{ cards(where: { stage: { eq: "shipped" } }) { title } }'
      Then the answer is the error 'Card.stage: no stage is "shipped": backlog, doing or done'
      And no cards' SQL was sent
