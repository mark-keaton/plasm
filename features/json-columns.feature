# Executable acceptance specification (Reqnroll): JSON columns. Written before
# the implementation.
@implemented
Feature: A field marked Json is stored as one jsonb document
  As an F# application developer
  I want a list or record that belongs only to its owner kept in one JSON column
  So that small nested data needs neither a table nor a column per field

  [<Json>] on a field stores its value as one jsonb document. The field may be
  a record with no table and no Id, a list of such records, a list of scalars,
  or an option of any of these. Plasm owns the mapping, so it is the same in
  every application: a record is an object keyed by its fields in snake case;
  None is a missing key; a list is an array; a scalar is
  stored as its column would be (a typed id as its number, a fieldless union as
  its case name in snake case, a DateTime as ISO 8601 text, a decimal as a
  number); a union with fields is an object with "case" and its fields. Reading
  is strict: a document that does not fit the type is an error naming the path
  in it, never repaired. A key the record does not have is an error too: a
  write replaces the whole document, so a tolerant read would let the next
  write delete what another writer added. Queries read a document's fields (`c.Settings.Theme`),
  `List.exists` over a JSON list and `List.contains` over a JSON list of
  scalars. A provider without JSON support refuses a model with a [<Json>] field
  when the repository is made.

  # Card = { Id: CardId; Title: string; [<Json>] Checklist: Item list; [<Json>] Settings: Settings; [<Json>] Estimate: Estimate option; [<Json>] Tags: string list }
  # Item = { Text: string; Done: bool; Due: DateTime option; Priority: Priority }
  # Priority = Low | High
  # Settings = { Theme: string; Columns: int }
  # Estimate = { Points: int }
  Background:
    Given a configured relational database
    And cards with a JSON checklist, settings, estimate and tags

  Rule: A field marked Json is one document in one column

    Scenario: A Card is stored with its checklist as one document
      When I insert a documented Card "Plan" with the items "draft" (done, high) and "review" (not done, low, due 2026-12-01)
      Then one command is sent, inserting into json_cards
      And the checklist column holds [{"text":"draft","done":true,"priority":"high"},{"text":"review","done":false,"due":"2026-12-01T00:00:00Z","priority":"low"}]

    Scenario: A Card is read back whole
      Given a Card "Plan" with the items "draft" (done, high) and "review" (not done, low, due 2026-12-01), the theme "dark" and the tags "urgent" and "q4"
      When I read the Cards and their documents
      Then the Card is read as it was inserted

    Scenario: An absent option is no document
      Given a Card "Plan" with no items, no estimate and no tags
      Then the estimate column is NULL
      And the checklist column holds []

    Scenario Outline: A document that does not fit is a read error naming the path
      Given a Card "Plan" whose checklist column is set behind Plasm's back to <document>
      When I read the Cards and their documents
      Then the read fails, naming the path <path>

      Examples:
        | document                                                    | path                  |
        | [{"text":"draft","priority":"high"}]                        | Checklist[0].done     |
        | [{"text":"draft","done":"yes","priority":"high"}]           | Checklist[0].done     |
        | [{"text":"draft","done":true,"priority":"urgent"}]          | Checklist[0].priority |
        | [{"text":"draft","done":true,"priority":"high","extra":1}]  | Checklist[0].extra, a key Item does not have |
        | {"text":"draft"}                                            | Checklist             |

  Rule: Queries read a document's fields

    Scenario: Filtering by a field of a JSON record
      Given a Card "Plan" with the theme "dark"
      And a Card "Ship" with the theme "light"
      When I read the Cards whose theme is "dark"
      Then the documented Cards read are "Plan"
      And one command is sent

    Scenario: Filtering by the items of a JSON list
      Given a Card "Plan" with the items "draft" (done, high) and "review" (not done, low, due 2026-12-01)
      And a Card "Ship" with the items "pack" (done, low) and "post" (done, high)
      When I read the Cards with an item not done
      Then the documented Cards read are "Plan"

    Scenario: Filtering by a value in a JSON list of scalars
      Given a Card "Plan" with the tags "urgent" and "q4"
      And a Card "Ship" with the tags "q4"
      When I read the Cards tagged "urgent"
      Then the documented Cards read are "Plan"

    Scenario: Selecting a field of a JSON record
      Given a Card "Plan" with the theme "dark"
      And a Card "Ship" with the theme "light"
      When I select the themes of the Cards, ordered by title
      Then the themes are "dark" and "light"

    Scenario: Comparing a whole document
      Given a Card "Plan" with the theme "dark"
      And a Card "Ship" with the theme "light"
      When I read the Cards whose settings are the theme "dark" with 3 columns
      Then the documented Cards read are "Plan"

    Scenario: A prepared argument compared with a document field
      Given a Card "Plan" with the theme "dark"
      And a Card "Ship" with the theme "light"
      When I read the Cards whose theme is "light", through a prepared query
      Then the documented Cards read are "Ship"

    Scenario: A module's queries into documents are checked
      When I check the queries of a module whose document queries translate
      Then no problem is reported

    Scenario: A field of an optional document is refused, naming it
      When I read the Cards whose estimate is over 3 points
      Then the read is refused, saying Card.Estimate is an optional document

  Rule: A change writes the document

    Scenario: Ticking an item writes the checklist
      Given a Card "Plan" with the items "draft" (done, high) and "review" (not done, low, due 2026-12-01)
      When the item "review" of "Plan" is ticked
      Then the update sets the checklist column and no other
      And "Plan" is read with every item done

  Rule: The database's tables are described and checked

    Scenario: Ddl prints a jsonb column
      When I print the DDL of the documented cards model
      Then it has the columns checklist jsonb not null, settings jsonb not null, estimate jsonb and tags jsonb not null

    Scenario: The schema check finds a column that is not jsonb
      Given the estimate column is text
      When I check the documented cards model against the database
      Then an incompatible type is reported for Card.Estimate

  Rule: A provider without documents refuses them when the repository is made

    Scenario: A repository over a provider without JSON refuses a model with a document
      When I make a repository of the documented cards model over a provider without JSON
      Then making it fails, naming Card.Checklist

  Rule: What cannot be a document is refused when the model is built

    Scenario Outline: <records> is refused
      When I build a JSON model of <records>
      Then the model is refused for <model>, saying "<message>"

      Examples:
        | records                         | model       | message                                             |
        | a scalar marked Json            | ScalarJson  | a scalar is stored in its own column                |
        | a stored record marked Json     | StoredJson  | is a stored record                                  |
        | a map marked Json               | MapJson     | Map is not supported in a document                  |
        | a document holding an Id record | IdJson      | looks like a stored record                          |
