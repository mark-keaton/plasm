# Lesson 15 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About aggregates and grouping
  Aggregates are typed values computed in the database; grouping summarises without transferring rows.

  Prerequisites: lesson 08.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                            | the learner discovers                                                                     | teaches                                                     | hint                                                                                                                                                                                                             |
      | Counting rows with Agg.count    | select (Agg.count ()) counts in the database                                              | aggregates: Counting a filtered query                       | Count the published rows of the orders table under Given, and write the number as an int64.                                                                                                                      |
      | Adding up with Agg.sum          | Agg.sum adds up a column in the database                                                  | aggregates: Aggregating a filtered query                    | Add the totals of the published rows of the orders table under Given.                                                                                                                                            |
      | The largest with Agg.max        | Agg.max gives the largest value as an option                                              | aggregates: Aggregating a filtered query                    | Find the largest total in the orders table under Given, and wrap it as the comment says Agg.max gives it.                                                                                                        |
      | Grouping by one key             | groupBy gives one row per key, with its count                                             | aggregates: Grouping Orders by Customer                     | For each order_id in order_lines under Given, in order, pair the id as an OrderId with how many rows carry it, as an int64.                                                                                      |
      | Counting a page counts the page | Count over a paged query counts the page                                                  | aggregates: Counting a page counts the page                 | The koan's name says what is counted: the rows the take keeps.                                                                                                                                                   |
      | Sum and average are typed       | Agg.sum returns decimal, Agg.avg an option                                                | aggregates: Aggregating a filtered query                    | Add the totals the comment lists, and divide by how many there are: like Agg.max, an average is an option, and the sum is a plain decimal.                                                                       |
      | No rows is explicit             | sum over no rows is 0; max over no rows is None                                           | aggregates: Aggregates over no rows are explicit            | No order has a total over a thousand, so the sum is what adding up no rows gives, and Agg.max is, as its own koan said, an option.                                                                               |
      | Grouping by a key               | groupBy and select give one row per Customer                                              | aggregates: Grouping Orders by Customer                     | Count the orders of each customer_id in the orders table under Given, and order them as F# compares options (lesson 04 promised queries mean what F# means), where a missing value sorts before any present one. |
      | Filtering groups                | having keeps only groups that satisfy an aggregate condition                              | aggregates: Filtering groups                                | Keep only the group from the previous koan whose count passes the having.                                                                                                                                        |
      | Each value once                 | selectDistinct gives each customer once, None included; Agg.countDistinct leaves None out | distinct: No customer is one value, as List.distinct has it | Count the different customer_id values in the orders table under Given, the missing value included, and the comment says which of them countDistinct leaves out.                                                 |
      | Grouping mistakes are refused   | selecting an ungrouped column is refused before execution                                 | aggregates: Grouping mistakes are explicit                  | The koan's name says what Plasm does with this query, and lesson 04 showed whether a refused query is sent.                                                                                                      |
