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
      | koan                            | the learner discovers                                        | teaches                                          |
      | Counting rows with Agg.count    | select (Agg.count ()) counts in the database                 | aggregates: Counting a filtered query            |
      | Adding up with Agg.sum          | Agg.sum adds up a column in the database                     | aggregates: Aggregating a filtered query         |
      | The largest with Agg.max        | Agg.max gives the largest value as an option                 | aggregates: Aggregating a filtered query         |
      | Grouping by one key             | groupBy gives one row per key, with its count                | aggregates: Grouping Orders by Customer          |
      | Counting a page counts the page | Count over a paged query counts the page                     | aggregates: Counting a page counts the page      |
      | Sum and average are typed       | Agg.sum returns decimal, Agg.avg an option                   | aggregates: Aggregating a filtered query         |
      | No rows is explicit             | sum over no rows is 0; max over no rows is None              | aggregates: Aggregates over no rows are explicit |
      | Grouping by a key               | groupBy and select give one row per Customer                 | aggregates: Grouping Orders by Customer          |
      | Filtering groups                | having keeps only groups that satisfy an aggregate condition | aggregates: Filtering groups                     |
      | Each value once                 | selectDistinct gives each customer once, None included; Agg.countDistinct leaves None out | distinct: No customer is one value, as List.distinct has it |
      | Grouping mistakes are refused   | selecting an ungrouped column is refused before execution    | aggregates: Grouping mistakes are explicit       |
