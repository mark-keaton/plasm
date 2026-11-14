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
      | A parent can be saved with a child    | putChildren on an insert writes the Order and its line together         | core: Parent and association writes are atomic                    |
      | The saved parent holds its children   | the returned Order has its new lines loaded, each with the Order's key  | core: Parent and association writes are atomic                    |
      | Saved children can be read back       | a preload after saving finds the lines that were written                | core: Parent and association writes are atomic                    |
      | Adding a child to an existing parent  | a child changeset without a key is inserted next to the others          | core: Updating an association matches existing identities         |
      | Omitted children are untouched        | an update that does not mention Lines leaves them as they are           | core: Omitting an association does not modify it                  |
      | Children are matched by identity      | children with keys are updated, children without are inserted           | core: Updating an association matches existing identities         |
      | Removing children needs a policy      | without OnReplace, replacing children that would remove some is refused | core: Removing related records requires a configured policy       |
      | Parent and children succeed together  | a failing child rolls back the parent                                   | core: Parent and association writes are atomic                    |
      | A filtered list is not the whole list | replacing within a scope only affects children in that scope            | elegance: Filtered collections are not authoritative replacements |
      | The same child twice is refused       | duplicate child identities are rejected before any write                | elegance: Duplicate child identities are rejected                 |
