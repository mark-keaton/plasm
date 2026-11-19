# Executable acceptance specification (Reqnroll): composite keys, second step:
# references to a key of several parts. Written before the implementation.
@implemented
Feature: A key of several parts is referred to, and owns, as a key of one does
  As an F# application developer
  I want to map tables keyed by several columns that refer to one another
  So that an existing tracker's tables fit without changing them

  A field whose type is another record's key record refers to that record, over
  every part: by default its columns are the field's column followed by the
  key's parts (Blocker: TicketKey is blocker_project, blocker_number), and
  [<Column("project", "ticket_number")>] names them otherwise, one name per
  part in the key record's order. A key record inside an Id is a part of this
  record's key and a reference too; there, as for every part of an Id, its
  columns are the table's own, by the key's part names. [<NotReference>] says a
  field of a key type refers to no row. An optional one stores every part NULL
  or none. A list of records holding the owner's key is owned, as for a key of
  one part, and is read and written with its owner. The tables are made from
  hand-written DDL, not from Ddl(), since the point is to fit tables Plasm did
  not make; Ddl() for the same model prints the same keys and references.

  # TicketKey  = { Project: string; Number: int }
  # Ticket     = { Id: TicketKey; Title; Tags: string list; Watchers: string list; [<NotReference>] Parent: TicketKey option; Version; Notes: Note list }
  # Note       = { Id: NoteId; [<Column("project", "ticket_number")>] Ticket: TicketKey; Body }
  # ReleaseKey = { Project: string; Number: int }
  # Release    = { Id: ReleaseKey; Name; Version }
  # Step       = { Id: { Release: ReleaseKey; Position: int }; Task }
  # Shipped    = { Id: { Release: ReleaseKey; [<Column("ticket_project", "ticket_number")>] [<NotReference>] Ticket: TicketKey } }
  # Blocker    = { Id: { Ticket: TicketKey; [<Column("blocker_project", "blocker_number")>] [<NotReference>] Blocker: TicketKey } }
  Background:
    Given a configured relational database
    And a tracker's tables, written by hand

  Rule: The tables fit as they are

    Scenario: The start-up check of the tables finds nothing to refuse
      When I start a repository over the tracker, checking its tables
      Then it starts

    Scenario: Ddl prints the same keys and references
      When I print the DDL of the tracker model
      Then it has primary key (project, number) for tickets, releases
      And it has foreign key (project, ticket_number) references tickets (project, number)
      And it has foreign key (project, number) references releases (project, number) for release_steps and release_tickets
      And it has foreign key (project, number) references tickets (project, number) for ticket_blockers
      And it has no foreign key over blocker_project, ticket_project or parent_project

    Scenario: The schema check finds a reference over several columns with no foreign key
      Given the foreign key from ticket_notes to tickets is dropped
      When I check the tracker model against the database
      Then a missing reference is reported for Note.Ticket

  Rule: An owned list under a key of several parts is read and written with its owner

    Scenario: A ticket is read with its notes
      Given the ticket core#1 "Crash" with the notes "seen twice" and "logs attached"
      And the ticket core#2 "Docs" with the note "typo"
      When I read the tickets of the project "core"
      Then the tickets are core#1 "Crash" with "seen twice", "logs attached" and core#2 "Docs" with "typo"
      And the notes were read in one command, for every ticket at once

    Scenario: A note added to a ticket is written with it
      Given the ticket core#1 "Crash" with the note "seen twice"
      When the note "fixed in main" is added to core#1
      Then the note row "fixed in main" holds project core and ticket_number 1
      And the ticket core#1 is read with the notes "seen twice" and "fixed in main"

    Scenario: A note removed from a ticket is deleted
      Given the ticket core#1 "Crash" with the notes "seen twice" and "logs attached"
      When the note "seen twice" is removed from core#1
      Then the ticket core#1 is read with the note "logs attached"

  Rule: An optional reference stores every part, or none

    Scenario: A ticket with a parent stores both parts of the parent's key
      Given the ticket core#1 "Crash" with no notes
      And the ticket core#2 "Crash again" under core#1
      Then the parent columns of core#2 hold core and 1
      And the ticket core#2 is read under core#1

    Scenario: A ticket with no parent stores neither part
      Given the ticket core#1 "Crash" with no notes
      Then the parent columns of core#1 are both NULL

    Scenario: A parent stored partly is a read error naming the field
      Given the ticket core#1 "Crash" with no notes
      And the parent_number of core#1 is set behind Plasm's back to 7
      When I read the tickets of the project "core"
      Then reading the tickets fails, naming Parent

  Rule: A key record inside an Id is part of the key, and a reference unless it says not

    Scenario: A release's steps are read by the release part of their key
      Given the release core#1 "1.0" with the steps 1 "build" and 2 "ship"
      And the release core#2 "1.1" with the step 1 "build"
      When I read the steps of the release core#1, ordered by position
      Then the steps are 1 "build" and 2 "ship"
      And the query's condition compares "project" and "number"

    Scenario: A release ships tickets that need not exist
      Given the release core#1 "1.0" with no steps
      When the release core#1 ships the tickets core#5 and web#3
      Then the release core#1 ships core#5 and web#3

    Scenario: A ticket is blocked by another, which need not exist
      Given the ticket core#1 "Crash" with no notes
      When core#1 is blocked by core#9
      Then core#1 is read as blocked by core#9

  Rule: What the attributes cannot say is refused when the model is built

    Scenario Outline: <records> is refused
      When I build a tracker model of <records>
      Then the model is refused for <model>, saying "<message>"

      Examples:
        | records                                        | model       | message                                       |
        | two column names on a field of one column      | TwoNames    | is one column, and Column names one           |
        | one column name for a key of two parts         | ShortNames  | names 1 column for a key of 2 parts           |
        | Column inside a plain value                    | ValueColumn | Column is not supported inside a value        |
        | two key parts of an Id with the same columns   | Twins       | both map to the column project                |
