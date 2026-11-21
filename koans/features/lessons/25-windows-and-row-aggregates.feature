# Lesson 25 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About windows and aggregates over rows
  A select can read, beside each row, values over a window of rows: its
  number, its rank, a running total, the row before it. Aggregates count and
  add the rows that meet a condition, test them, and collect them in a
  stated order.

  Prerequisites: lesson 16.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                        | the learner discovers                                                                     | teaches                                                                                                                              | hint                                                                                                                                                         |
      | Numbering each customer's orders            | Win.rowNumber numbers the rows of a partition in its order, from 1                          | window-functions: Each customer's Orders numbered, ranked and totalled in order of their day                                          | Under Given, number each customer's rows in the orders table by placed_at, from 1, and pair each order's id, as an OrderId, with its number as an int64.     |
      | A running total and the row before          | Win.runningSum adds up to the row in the window's order; Win.lag is the row before, or None | window-functions: Each customer's Orders numbered, ranked and totalled in order of their day                                          | Take the rows of the orders table under Given by placed_at: add each total to those before it, and give the total of the row before as an option.            |
      | A whole window beside each row              | Win.sum and Win.count over Win.whole are over every row the query reads                     | window-functions: A whole window's sum, average and count, beside each row                                                            | Add up every total in the orders table under Given, and count its rows as an int64.                                                                          |
      | The window is over the rows kept            | a window is over the rows the where keeps, before the order and page                       | window-functions: The window is over the rows the query keeps                                                                         | Of the rows of the orders table under Given, keep those whose total is over 20, and number them by placed_at, from 1, as int64s.                             |
      | A rank needs an order                       | a window function that depends on an order is refused, by name, over a window with none     | window-functions: <what> is refused                                                                                                   | The comment says what such a query is refused with, and the text the refusal says.                                                                           |
      | Counting and adding the rows that meet a test | Agg.countWhere and Agg.sumWhere count and add only the rows a condition keeps               | aggregates: Each customer's Orders, counted, summed, tested, listed and joined                                                        | For each customer with a row in the orders table under Given, count its published orders as an int64 and add up their totals.                                |
      | Whether any, whether all                    | Agg.any says whether some row meets a condition, Agg.all whether every one does             | aggregates: Each customer's Orders, counted, summed, tested, listed and joined                                                        | For each customer with a row in the orders table under Given, say whether one of its totals is over 50, and whether all of its orders are published.        |
      | A list in a stated order                    | Agg.listBy collects the rows' values in a key's order; listByDescending reverses it         | aggregates: The Descending forms reverse the stated order                                                                             | Give Ada's totals from the orders table under Given by placed_at, then the other way round.                                                                  |
      | Over no rows                                | over no rows each aggregate gives what the F# function it is named after gives              | aggregates: Over no rows each gives what F# gives                                                                                     | No order under Given has a total over 1000; give what List.length, List.exists, List.forall and an empty list give for no items.                            |
