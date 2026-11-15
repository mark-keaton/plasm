# Lesson 22 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About unions with fields
  A union whose cases carry facts is stored across columns and queried as F#
  reads it, through the domain's own members.

  Prerequisites: lesson 07, lesson 13, lesson 15.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                              | the learner discovers                                                   | teaches                                                                  |
      | A union with fields is read back whole            | a union's case and its fields come back from their columns as one value | union-fields: A Card <stage> is written and read back                     |
      | A case test is a query                            | matching on a case becomes a test of the stored case name               | union-fields: A case test                                                |
      | Matching on a case's field                        | a case's field is a column a match can test                             | union-fields: Matching on a case's field                                 |
      | The domain's own member in a query                | a [<ReflectedDefinition>] member is translated, not run                 | union-fields: The domain's own member reads as its column                |
      | Comparing with a whole value                      | comparing with a case compares its name and each of its fields          | union-fields: Comparing with a whole value                               |
      | Counting by stage                                 | grouping by a member that names the case groups by case                 | union-fields: Counting by case, through the domain's own member          |
      | Changing the stage clears the old stage's columns | an update to another case writes NULL into the old case's columns       | union-fields: Changing the case clears the old case's columns            |
      | A row that is no case is refused                  | a stored case name that is no case is a read error naming the field     | union-fields: A row that is no case is refused when read, naming the field |
