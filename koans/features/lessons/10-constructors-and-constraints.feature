# Lesson 10 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About changesets
  Changes are values you can inspect before anything is written. Plasm validates
  nothing itself: the database's refusals come back as the fields' errors.

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
      | A changeset leaves its input alone  | building a changeset does not change the record                               | core: Creating a changeset preserves its input              |
      | Unchanged and cleared differ        | Changeset.edit records only the fields you change, and None as Cleared        | core: Unspecified and explicitly cleared values differ      |
      | Invalid changes are never written   | a change the database refuses comes back as the field's error and writes nothing | core: Invalid changes are not persisted                     |
      | Constraints become field errors     | a duplicate of a unique field comes back as that field's error, by the constraint's default name | core: Database constraints produce structured errors        |
