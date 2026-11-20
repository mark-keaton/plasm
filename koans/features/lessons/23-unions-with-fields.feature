# Lesson 23 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About unions with fields
  A union whose cases carry facts is stored across columns and queried as F#
  reads it, through the domain's own members.

  Prerequisites: lesson 07, lesson 13, lesson 16.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                              | the learner discovers                                                   | teaches                                                                    | hint                                                                                                                                                       |
      | A union with fields is read back whole            | a union's case and its fields come back from their columns as one value | union-fields: A Card <stage> is written and read back                      | Read stage and stage_carrier in the deliveries row of the third order under Given, and build the DeliveryStage case from Shop.fs that they describe.       |
      | A case test is a query                            | matching on a case becomes a test of the stored case name               | union-fields: A case test                                                  | Find the rows of the deliveries table under Given whose stage is delivered, and give their order ids as OrderIds.                                          |
      | Matching on a case's field                        | a case's field is a column a match can test                             | union-fields: Matching on a case's field                                   | Find the row of the deliveries table under Given that is in transit with that carrier, and give its order id as an OrderId.                                |
      | The domain's own member in a query                | a [<ReflectedDefinition>] member is translated, not run                 | union-fields: The domain's own member reads as its column                  | Carrier, in Shop.fs, is Some only for a delivery in transit, so this asks for the same row as the previous koan.                                           |
      | Comparing with a whole value                      | comparing with a case compares its name and each of its fields          | union-fields: Comparing with a whole value                                 | Find the row of the deliveries table under Given that is delivered and signed by that name, and give its order id as an OrderId.                           |
      | Counting by stage                                 | grouping by a member that names the case groups by case                 | union-fields: Counting by case, through the domain's own member            | StageName, in Shop.fs, gives each case its text, so pair each text with how many rows of the deliveries table have it, as an int64, sorted alphabetically. |
      | Changing the stage clears the old stage's columns | an update to another case writes NULL into the old case's columns       | union-fields: Changing the case clears the old case's columns              | The comment says the new case has no carrier, so the column holds NULL, which is read as the absent case of an option, in the list repo.Sql always gives.  |
      | A row that is no case is refused                  | a stored case name that is no case is a read error naming the field     | union-fields: A row that is no case is refused when read, naming the field | The comment says what reading such a row is, and what the error names.                                                                                     |
