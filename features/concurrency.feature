# Executable acceptance specification (Reqnroll): changes checked against what
# was read. Written before the implementation, following core.feature.
@implemented
Feature: Changes are checked against what was read
  As an F# application developer
  I want an update to fail when its row changed since I read it, without a version field in my record
  So that a lost update is refused while my record stays the domain's own

  A record without a version field is checked against the record the change was
  made from: an update or a delete applies only if every column still holds what
  was read, NULLs included, in the same command. A record with a version field is
  checked by its version instead (core.feature). A change made from a key alone
  has nothing to compare, so it is not checked unless the record has a version.
  A conflict names the record by its key.

  # Note = { Id: NoteId; Text: string; Author: string option }, without a version.
  Background:
    Given a configured relational database
    And a notes table

  Rule: A record without a version field is checked against what was read

    Scenario: A change to a row nobody else changed is saved
      Given a Note read from the database
      When I change the text of the Note I read
      Then the change is saved

    Scenario: A change after another writer changed the row is a conflict
      Given a Note read from the database
      And another writer changes that Note's text
      When I change the text of the Note I read
      Then the change fails as a conflict naming the Note's key
      And the other writer's text is kept

    Scenario: A delete after another writer changed the row is a conflict
      Given a Note read from the database
      And another writer changes that Note's text
      When I delete the Note I read
      Then the delete fails as a conflict naming the Note's key

    Scenario: A NULL that is still NULL matches
      Given a Note without an author read from the database
      When I change the text of the Note I read
      Then the change is saved

    Scenario: A NULL another writer filled in is a conflict
      Given a Note without an author read from the database
      And another writer gives that Note an author
      When I change the text of the Note I read
      Then the change fails as a conflict

    Scenario: The check is part of the change's own command
      Given a Note read from the database
      When I change the text of the Note I read
      Then one command was sent, comparing every column with what was read

    Scenario: A change made from a key alone is not checked
      Given a Note read from the database
      And another writer changes that Note's text
      When I change the Note's text by its key alone
      Then the change is saved

  Rule: A record with a version field is checked by its version

    Scenario: A versioned record is checked by its version alone
      Given an Order was read with a concurrency version
      And another writer changes its total without changing its version
      When I change the status of the Order I read
      Then the change is saved
