# Lesson 15 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
# Caveat: there are no prepared projections yet.
Feature: About prepared queries
  A hot query can be built and planned once and bound to new arguments on each call.

  Prerequisites: lesson 03.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                     | the learner discovers                                                  | teaches                                                                            |
      | Built once, run many times               | Query.prepare builds the query once; each call only binds the argument | prepared-queries: A prepared query is built once and runs with different arguments |
      | Arguments are parameters                 | Arg values are always sent as parameters of their declared type        | prepared-queries: Arguments are typed and always parameters                        |
      | Paging by arguments                      | take size.Value and skip offset.Value bind per call                    | prepared-queries: Paging arguments                                                 |
      | Prepared queries still compose           | extending a prepared query applies the extension everywhere            | prepared-queries: Extending a prepared query applies the extension everywhere      |
      | Arguments are read only inside the query | reading .Value while building is refused with an explanation           | prepared-queries: Arguments are only read inside query operations                  |
      | Prepared load queries                    | a bound prepared query can be the query for a relationship load        | prepared-queries: A prepared query used as a load query binds its own arguments    |
