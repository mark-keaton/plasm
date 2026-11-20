# Executable acceptance specification (Reqnroll): composite keys, first step.
# Written before the implementation.
@implemented
Feature: A record whose Id is a record has a key of several columns
  As an F# application developer
  I want a record keyed by more than one field, as existing tables often are
  So that a table keyed by (repo, number) maps without a surrogate id

  A stored record's key is its field named Id. When the Id's type is a record,
  the key has a part for each of its fields, and the key's parts are the
  table's own columns, by their own names in snake case, with no prefix; a value
  record anywhere else is prefixed with its field's column. The key record is
  the record's typed id. Its parts are scalars, none optional, at least two of
  them; the insert gives every part, since no part is generated. A query reads
  a part as a column (i.Id.Number) and compares a whole key with = over every
  part; a key has no order of its own, so ordering by it or comparing it with
  < is refused in favour of its parts. Tx.update finds the row by every part
  and its version, and a part cannot be changed. Ddl prints PRIMARY KEY over
  the parts and the schema check compares the primary key's columns.

  A bulk update or delete finds rows by any of the parts, and an upsert or an
  insert of the missing records takes the whole key as its conflict target,
  which is every part (ON CONFLICT (repo, number)).

  References to such a key, and owned lists under it, are
  composite-references.feature's. Refused by name for now: linked lists on
  either side, and an owned list of records keyed by several parts.

  # IssueKey = { Repo: string; Number: int }
  # Issue = { Id: IssueKey; Title: string; [<Version>] Version: int }
  Background:
    Given a configured relational database
    And issues keyed by their repository and number

  Rule: The key's parts are the table's own columns

    Scenario: An Issue is inserted with every part of its key
      When I insert the keyed Issue plasm#7 "Crash"
      Then one command is sent, inserting the columns repo, number, title and version into keyed_issues

    Scenario: An Issue is read by its whole key
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When I read the keyed Issue plasm#7 by its key, through a prepared query
      Then the keyed Issue read is plasm#7 "Crash"
      And the query's condition compares "repo" and "number"

    Scenario: Issues are read by one part of the key
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When I read the keyed Issues of the repository "plasm", ordered by number
      Then the keyed Issues read are plasm#7 "Crash" and plasm#8 "Docs"

    Scenario: A page is told apart by every part of the key
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When I read the first two keyed Issues ordered by title
      Then the query orders by "title", then "repo" and "number"

  Rule: A change finds the row by every part and its version

    Scenario: An Issue is renamed
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When the keyed Issue plasm#7 is renamed "Crash on start"
      Then the update finds the row by "repo", "number" and "version"
      And the keyed Issues read are other#7 "Elsewhere", plasm#7 "Crash on start" and plasm#8 "Docs"

    Scenario: A change from a stale copy is refused
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      And the keyed Issue plasm#7 is renamed "Crash on start"
      When the keyed Issue plasm#7 is renamed "Crash again" from the copy read first
      Then the change is refused as changed by another writer

    Scenario: A part of the key cannot be changed
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When the keyed Issue plasm#7 is given the number 9
      Then the change is refused, saying the primary key cannot be changed

    Scenario: An Issue is deleted by its key
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When the keyed Issue plasm#7 is deleted
      Then the keyed Issues read are other#7 "Elsewhere" and plasm#8 "Docs"

  Rule: Bulk writes and upserts take the whole key

    Scenario: Issues are upserted on their whole key
      Given the keyed Issues plasm#7 "Crash"
      When I upsert the keyed Issues plasm#7 "Crashes" and plasm#8 "Docs" on their key
      Then one command is sent, conflicting on "repo" and "number"
      And the keyed Issues read are plasm#7 "Crashes" and plasm#8 "Docs"

    Scenario: Issues missing by their whole key are inserted, the rest kept
      Given the keyed Issues plasm#7 "Crash"
      When I insert the keyed Issues plasm#7 "Duplicate" and plasm#8 "Docs" where missing by their key
      Then only plasm#8 "Docs" is returned as inserted
      And the keyed Issues read are plasm#7 "Crash" and plasm#8 "Docs"

    Scenario: A bulk update and a bulk delete find Issues by every part of the key
      Given the keyed Issues plasm#7 "Crash", plasm#8 "Docs" and other#7 "Elsewhere"
      When I retitle the keyed Issue plasm#7 "Crashes" and delete other#7, each in one bulk write
      Then the bulk update and the bulk delete each compare "repo" and "number"
      And the keyed Issues read are plasm#7 "Crashes" and plasm#8 "Docs"

  Rule: A key has no order of its own

    Scenario Outline: Ordering by or comparing a whole key is refused
      When I read the keyed Issues <how>
      Then the read is refused, saying Issue.Id and "<message>"

      Examples:
        | how                               | message           |
        | ordered by their key              | order by its parts |
        | whose key is less than plasm#8    | compare its parts  |

  Rule: The database's tables are described and checked

    Scenario: Ddl prints a primary key over the parts
      When I print the DDL of the keyed issues model
      Then it has the columns repo text not null, number integer not null and primary key (repo, number)

    Scenario: The schema check accepts the keyed issues
      When I check the keyed issues model against the database
      Then no keyed schema issue is reported

    Scenario: The schema check finds a primary key over other columns
      Given the keyed issues' primary key is (repo) alone
      When I check the keyed issues model against the database
      Then a primary key issue is reported for Issue, naming repo and number

  Rule: What cannot be a key, or is not yet supported with one, is refused

    Scenario Outline: <records> is refused
      When I build a keyed model of <records>
      Then the model is refused for <model>, saying "<message>"

      Examples:
        | records                                  | model        | message                                         |
        | a key with an optional part              | OptionalPart | a part of a key cannot be optional              |
        | a key with one part                      | OnePart      | a key of one part is a single-case wrapper      |
        | a key holding a list                     | ListPart     | a part of a key is a scalar                     |
        | a key holding a value                    | ValuePart    | a part of a key is a scalar                     |
        | a generated key                          | GeneratedKey | a key of several parts is given by the insert   |
        | a linked list under a key of parts       | Thread       | links Tag, but its key has several parts        |
        | an owned list of records keyed by parts  | Board        | owns Card, whose key has several parts          |
