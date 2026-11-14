# Lesson 12 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About child writes
  Writing a parent with its children is atomic, and removing children always needs a stated policy and scope.

  Prerequisites: lesson 11, lesson 09.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                  | the learner discovers                                                   | teaches                                                           |
      | Omitted children are untouched        | an update that does not mention Lines leaves them as they are           | core: Omitting an association does not modify it                  |
      | Children are matched by identity      | children with keys are updated, children without are inserted           | core: Updating an association matches existing identities         |
      | Removing children needs a policy      | without OnReplace, replacing children that would remove some is refused | core: Removing related records requires a configured policy       |
      | Parent and children succeed together  | a failing child rolls back the parent                                   | core: Parent and association writes are atomic                    |
      | A filtered list is not the whole list | replacing within a scope only affects children in that scope            | elegance: Filtered collections are not authoritative replacements |
      | The same child twice is refused       | duplicate child identities are rejected before any write                | elegance: Duplicate child identities are rejected                 |
