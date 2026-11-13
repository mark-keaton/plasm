# Lesson 11 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About aggregates and grouping
  Aggregates are typed values computed in the database; grouping summarises without transferring rows.

  Prerequisites: lesson 04.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                            | the learner discovers                                        | teaches                                          |
      | Counting a page counts the page | Count over a paged query counts the page                     | aggregates: Counting a page counts the page      |
      | Sum and average are typed       | Agg.sum returns decimal, Agg.avg an option                   | aggregates: Aggregating a filtered query         |
      | No rows is explicit             | sum over no rows is 0; max over no rows is None              | aggregates: Aggregates over no rows are explicit |
      | Grouping by a key               | groupBy and select give one row per Customer                 | aggregates: Grouping Orders by Customer          |
      | Filtering groups                | having keeps only groups that satisfy an aggregate condition | aggregates: Filtering groups                     |
      | Grouping mistakes are refused   | selecting an ungrouped column is refused before execution    | aggregates: Grouping mistakes are explicit       |
