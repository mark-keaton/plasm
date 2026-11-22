# Executable acceptance specification (Reqnroll): the checks Ddl writes on a
# column, read back by the schema check.
@implemented
Feature: A column's check is the model's
  As an F# application developer
  I want the checks Ddl writes on a column read back by the schema check
  So that a check that refuses what the model writes is found before a write fails

  Where a column's type does not hold what its field holds, Ddl writes a
  check named <table>_<column>_check: on SQLite, for a union stored as its
  cases' names, a list, a time and a document. PostgreSQL's types hold each
  (text holds any name, and an enum type, an array, timestamptz and jsonb
  hold the rest), so it writes none. The schema check reads each such check
  back and compares it with what Ddl writes, without case or spaces: one
  that leaves out a case's name, so that writing it would fail, or allows a
  name no case has, so that reading it would fail, or holds the column to
  something else, is reported. A check of the same names in another order
  holds the column as Ddl's does, and is not. A column without its check is
  reported, with the check to add, where PostgreSQL's type would hold the
  value (an enum type, a list, a document, a time): there the check is
  SQLite's only equivalent. A union stored as text has nothing holding it on
  PostgreSQL either, and a column of one without its check is not reported.

  Background:
    Given a configured relational database
    And the chores table, made by Ddl

  Scenario: A table made by Ddl has nothing to report
    When I check the chores model against the database
    Then no issue is reported for the chores

  @sqlite
  Scenario: On SQLite a union stored as text is held to its cases' names
    When the chores model's DDL is printed
    Then its state column is held by "constraint chk_chores_state_check check (state in ('open', 'done', 'dropped'))"

  @sqlite
  Scenario: On SQLite a check without a case's name is reported, as writing it would fail
    Given the chores table's check chk_chores_state_check is "check (state in ('open', 'done'))"
    When I check the chores model against the database
    Then Chore.State is reported, saying "check chk_chores_state_check has no label dropped for Dropped; writing it would fail"

  @sqlite
  Scenario: On SQLite a check allowing a name no case has is reported, as reading it would fail
    Given the chores table's check chk_chores_state_check is "check (state in ('open', 'done', 'dropped', 'snoozed'))"
    When I check the chores model against the database
    Then Chore.State is reported, saying "check chk_chores_state_check allows snoozed, which is not a ChoreState case; reading it would fail"

  @sqlite
  Scenario: On SQLite a check of the same names in another order is not reported
    Given the chores table's check chk_chores_state_check is "CHECK(state IN ('dropped','open','done'))"
    When I check the chores model against the database
    Then no issue is reported for the chores

  @sqlite
  Scenario: On SQLite a union stored as text without its check is not reported
    Given the chores table has lost its check chk_chores_state_check
    When I check the chores model against the database
    Then no issue is reported for the chores

  @sqlite
  Scenario: On SQLite a time column without its check is reported, with the check to add
    Given the chores table has lost its check chk_chores_due_check
    When I check the chores model against the database
    Then Chore.Due is reported as of an incompatible type, saying "column has no check chk_chores_due_check, so it may hold what Chore.Due cannot read: add `constraint chk_chores_due_check check ("

  @sqlite
  Scenario: On SQLite a check as Ddl writes it, in other case and spacing, is not reported
    Given the chores table's check chk_chores_due_check is Ddl's, in capitals and with its spaces doubled
    When I check the chores model against the database
    Then no issue is reported for the chores

  @sqlite
  Scenario: On SQLite a time column's check other than Ddl's is reported
    Given the chores table's check chk_chores_due_check is "check (due is not null)"
    When I check the chores model against the database
    Then Chore.Due is reported as of an incompatible type, saying "check chk_chores_due_check is `check (due is not null)`, where Ddl writes"
