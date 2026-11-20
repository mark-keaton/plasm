# Lesson 21 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About failures
  Expected failures are values you can match on; Plasm's error cases are always qualified, so your domain keeps its own names.

  Prerequisites: lesson 10, lesson 13.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                     | the learner discovers                                                                        | teaches                                                                   | hint                                                                                                                                                             |
      | Nothing found is not a failure           | One of a query matching no rows is Ok None                                                   | reading: A query matching no rows returns None                            | The koan's name says whether this is an Error, and lesson 02's koan 'Every call returns a Result' read the same missing customer.                                |
      | A failure is an Error you can match on   | a write the database refuses comes back as an Error, not an exception                        | core: Database constraints produce structured errors                      | The comment says the email is already taken, so see which branch of the match an Error takes.                                                                    |
      | A failed write says what kind of failure | TxFailure.Failed carries the database's DbFailureKind, here CheckViolation                   | core: Database constraints produce structured errors                      | The comment lists the kinds, the rule broken here is a check constraint, and the kind is written with its type's name in front.                                  |
      | Your refusals and Plasm's are apart      | a transaction's own rejection is TxFailure.Rejected, apart from Plasm's failures             | core: Infrastructure failures remain distinct from domain errors          | Tx.fail gives a transaction's own reason, and the cancelled order without a reason is refused by the database, so the comment says which TxFailure case each is. |
      | Failures are values                      | a read through an ended sandbox returns an error instead of throwing                         | error-model: Using a sandbox's repository after the sandbox ended         | The comment lists the kinds of a database failure and names the one for a repository that has ended. |
      | Error cases are qualified                | DbError.Database never collides with a domain case called Database                           | error-model: Domain unions keep their own case names                      | The match in classify sends every DbError that is not DbError.Database to its second branch, and that branch gives a case of Problem, under Given.               |
      | Unreadable rows are named                | a stored value the model rejects names its model and field                                   | error-model: A written row that cannot be read back                       | The pair names the record that was being read and the field whose stored text is no case of its union.                                                           |
      | SQL mismatches name the column           | a hand-written query whose column does not fit names that column                             | raw-sql: Mismatched results fail with the column identified               | The comment says which column the query leaves out, and the koan's name says what the error does with it.                                                        |
      | Too many rows is about the data          | One over several rows returns DbError.MoreThanOneRow, not a translation error                | reading: More than one matching row is reported as a property of the data | Two orders are published under Given, and the comment says what One gives for more than one row. |
      | Rejections keep their type               | Tx.fail with a domain error gives it back as itself: the transaction's own error type        | error-model: A step's rejection reason comes back with its type           | The reason is the very value that was given to Tx.fail.                                                                                                          |
      | Every union is qualified                 | a domain case Failed and Plasm's TxFailure.Failed never collide: Plasm's cases are qualified | error-model: <union> cases must be qualified                              | Follow each match by hand: find the branch a plain Failed takes in paid, and the branch a Conflict takes in databaseFailed.                                      |
