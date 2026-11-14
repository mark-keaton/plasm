# Lesson 10 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About changesets
  Changes are values that can be validated and inspected before anything is written.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                | the learner discovers                                                         | teaches                                                     |
      | A changeset starts from a record    | Changeset.insert and Changeset.update wrap a record and say what they will do | core: Constructing a change performs no database operations |
      | Changes are visible before saving   | Proposed shows the record as it will be, before anything is written           | core: Creating a changeset preserves its input              |
      | A valid changeset has no errors     | a validation that holds leaves the changeset valid, with an empty error list  | core: Invalid changes can be inspected without execution    |
      | A validation adds an error          | a validation that fails adds an error naming its field                        | core: Invalid changes can be inspected without execution    |
      | A changeset leaves its input alone  | building a changeset does not change the record                               | core: Creating a changeset preserves its input              |
      | Unchanged and cleared differ        | Changeset.edit records only the fields you change, and None as Cleared        | core: Unspecified and explicitly cleared values differ      |
      | Errors can be inspected             | validation errors are values, named by field, before any database work        | core: Invalid changes can be inspected without execution    |
      | Input cannot touch protected fields | cast only accepts the fields you permit                                       | core: External input cannot change protected fields         |
      | Invalid changes are never written   | inserting an invalid changeset returns it with its errors and writes nothing  | core: Invalid changes are not persisted                     |
      | Constraints become field errors     | a duplicate of a unique field comes back as that field's error, by the constraint's default name | core: Database constraints produce structured errors        |
