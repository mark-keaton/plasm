# Lesson 07 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About writes
  Inserts, updates and deletes take changesets and return new values or typed errors.

  Prerequisites: lesson 06.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                      | the learner discovers                                              | teaches                                                              |
      | Inserting returns the stored record       | the returned record has its generated key; the input is unchanged  | core: Generated values are returned immutably                        |
      | Updates write only the changes            | an update sends only the fields the changeset changed              | elegance: Projected values do not imply complete entity state        |
      | Stale updates are refused                 | a version mismatch returns WriteError.Stale and overwrites nothing | core: Stale updates do not overwrite newer state                     |
      | Infrastructure is not a domain error      | a lost connection is WriteError.Failed, not a validation error     | core: Infrastructure failures remain distinct from domain errors     |
      | Each call takes its own kind of changeset | repo.Update refuses an insert changeset before doing anything      | tx-steps: The repository rejects a mismatched changeset the same way |
