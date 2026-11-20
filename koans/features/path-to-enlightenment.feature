# Behaviour specification (Reqnroll): the Plasm Koans runner.
# Written before the implementation. The koans are an interactive course in the
# style of Ruby Koans: ordered exercises with blanks (__) that the learner fills
# in, each run against a real PostgreSQL database.
Feature: The path to enlightenment
  As a developer learning Plasm
  I want to run the koans and be told exactly which one to meditate on next
  So that I learn the library one small, verified step at a time

  The learner runs `dotnet run --project koans`. Koans run in lesson order and
  the run stops at the first koan that is not yet satisfied. Every koan runs in
  its own sandbox, so the database is the same for each koan and is never
  changed by the course.

  Background:
    Given a fresh copy of the koans

  Rule: The runner always points at the next step

    Scenario: A fresh copy points at the first koan
      When the learner runs the koans
      Then the first koan of the first lesson is reported as the next step
      And the report names the lesson, the koan and the file and line of its first blank
      And the report shows no Meditate line
      And no later koan is run

    Scenario: Filling in a blank correctly advances to the next koan
      Given the learner has filled in the first koan correctly
      When the learner runs the koans
      Then the first koan is reported as passed
      And the second koan is reported as the next step
      And progress shows 1 of the total number of koans
      And the report shows the first koan's Meditate line, what it found

    Scenario: A wrong answer shows what the learner wrote and what Plasm gave
      Given the learner has filled in the first koan with a wrong value
      When the learner runs the koans
      Then the report shows what the learner wrote and what Plasm gave, displayed as F# Interactive would
      And the report shows no Meditate line
      And the koan is still the next step

    Scenario: A wrong answer shows the koan's hint
      Given the learner has filled in the first koan with a wrong value
      When the learner runs the koans
      Then the report shows the first koan's hint

    Scenario: A blank not yet filled in shows no hint
      When the learner runs the koans
      Then the report shows no hint

    Scenario: A koan about SQL shows the SQL
      Given the learner has reached a koan that asserts on generated SQL
      And has filled it in with a wrong value
      When the learner runs the koans
      Then the report shows the SQL Plasm generated, formatted by clause

    Scenario: An unexpected exception is reported as the koan's failure
      Given the learner has filled in a koan with code that throws
      When the learner runs the koans
      Then the report names the koan and shows the exception's message
      And the runner does not crash

    Scenario: Finishing every koan
      Given every koan has been filled in correctly
      When the learner runs the koans
      Then the report says the path is complete and shows every lesson as passed

  Rule: The database is provided and never changed

    Scenario: A throwaway database is started when none is configured
      Given no koans database is configured
      When the learner runs the koans
      Then a throwaway PostgreSQL database is started with the koans' schema and data
      And it is removed when the run ends

    Scenario: A configured database is used instead
      Given the PLASM_KOANS_PG environment variable names a PostgreSQL server
      When the learner runs the koans
      Then the koans run against a database created on that server

    Scenario: Each koan starts from the same data
      Given a koan that inserts, updates and deletes rows
      When the learner runs the koans twice
      Then the koan sees the same starting data both times
      And the next koan sees the starting data, not the earlier koan's changes

  Rule: Learners can focus

    Scenario: Running one lesson
      When the learner runs the koans for the lesson "About query values"
      Then only that lesson's koans are run, in order, stopping at the first unsatisfied one

    Scenario: Listing the lessons
      When the learner asks for the list of lessons
      Then every lesson is listed in order with how many of its koans pass
