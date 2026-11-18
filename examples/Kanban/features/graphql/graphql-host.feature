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
      When I ask for '{ cards(where: { or: [{ stage: { eq: "doing" } }, { version: { gt: 2 } }] }, order: [{ created: DESC }]) { items { title } } }'
      Then the cards are "Ship it, Build it"
      And the cards' SQL has a WHERE and orders by 'ORDER BY "c"."created" DESC, "c"."id"', then reads a page

    Scenario: Conditions on two fields together
      When I ask for '{ cards(where: { title: { neq: "Ship it" }, version: { lte: 2 } }, order: [{ title: ASC }]) { items { title } } }'
      Then the cards are "Build it, Write the spec"
      And the cards' SQL has a WHERE and orders by 'ORDER BY "c"."title", "c"."id"', then reads a page

    Scenario: No where and no order read the first page, oldest first
      When I ask for '{ cards { items { title } } }'
      Then the cards are "Write the spec, Build it, Ship it"
      And the cards' SQL has no WHERE and orders by 'ORDER BY "c"."created", "c"."id"', then reads a page

  Rule: A page is read by the database

    # A page reads one card more than it shows, to know whether another
    # follows, the skip and the take as parameters; Plasm orders a page by the
    # key after what the request orders by, so a page holds the same cards
    # each time it is read, and the host orders by creation when a request
    # gives no order, since Plasm reads no page without one.
    Scenario: A page in the middle knows there are pages on both sides
      When I ask for '{ cards(skip: 1, take: 1, order: [{ created: ASC }]) { items { title } pageInfo { hasNextPage hasPreviousPage } } }'
      Then the cards are "Build it"
      And the page has a next page and a previous page
      And the cards' SQL has no WHERE and orders by 'ORDER BY "c"."created", "c"."id"', then reads a page

    Scenario: The last page knows it is the last
      When I ask for '{ cards(skip: 2, take: 2, order: [{ created: ASC }]) { items { title } pageInfo { hasNextPage hasPreviousPage } } }'
      Then the cards are "Ship it"
      And the page has no next page and a previous page

    Scenario: The total is counted by the database, under the request's where
      When I ask for '{ cards(take: 1, where: { stage: { eq: "backlog" } }, order: [{ created: ASC }]) { items { title } totalCount } }'
      Then the cards are "Write the spec"
      And the total is 2
      And the database counted the cards under a WHERE

    Scenario: A total not asked for is not counted
      When I ask for '{ cards(take: 1) { items { title } } }'
      Then the database counted nothing

    Scenario: A page larger than the host allows is refused before any SQL
      When I ask for '{ cards(take: 500) { items { title } } }'
      Then the answer is an error
      And no cards' SQL was sent

  Rule: A board's cards, with their own where and order, are read in one command for every board

    # Each board's cards field is answered by one batch: every board's cards
    # in one query, the boards' keys as one array parameter, with the
    # request's where and order on it; a field asked twice with different
    # arguments is one query each.
    Scenario: The backlog of each board, by title, in one command
      Given board "beta" has the cards:
        | title  | stage   | assignee | version | created              |
        | Plan   | backlog |          | 1       | 2026-11-18T12:00:00Z |
        | Review | doing   | Grace    | 1       | 2026-11-18T13:00:00Z |
      When I ask for '{ boards { slug cards(where: { stage: { eq: "backlog" } }, order: [{ title: ASC }]) { title } } }'
      Then each board's cards are "alpha: Ship it, Write the spec; beta: Plan"
      And the cards were read in 1 command, for every board at once

    Scenario: A board's cards asked for twice, with different wheres, are read in a command each
      When I ask for '{ boards { slug backlog: cards(where: { stage: { eq: "backlog" } }) { title } doing: cards(where: { stage: { eq: "doing" } }) { title } } }'
      Then the board "alpha" has backlog "Write the spec, Ship it" and doing "Build it"
      And the cards were read in 2 commands, for every board at once

  Rule: The answer is the application's view

    Scenario: A card's stage is a GraphQL union over its cases
      When I ask for '{ cards(where: { stage: { eq: "doing" } }) { items { title stage { __typename ... on InProgress { kind assignee } } } } }'
      Then the answer's JSON is {"data":{"cards":{"items":[{"title":"Build it","stage":{"__typename":"InProgress","kind":"doing","assignee":"Ada"}}]}}}

  Rule: What the declarations do not allow is refused before any SQL

    Scenario: An operator no field was declared with is not in the schema
      When I ask for '{ cards(where: { title: { contains: "it" } }) { items { title } } }'
      Then the answer is the error "The specified input object field `contains` does not exist."
      And no cards' SQL was sent

    Scenario: A value the field cannot make its own is refused, naming the field
      When I ask for '{ cards(where: { stage: { eq: "shipped" } }) { items { title } } }'
      Then the answer is the error 'Card.stage: no stage is "shipped": backlog, doing or done'
      And no cards' SQL was sent
