# Executable acceptance specification (Reqnroll): window functions in a
# select. Written before the implementation.
@implemented
Feature: A select can read values over a window of rows
  As an F# application developer who ranks, numbers and totals rows
  I want each row's place and running total computed beside it by the database
  So that I never read every row only to number or add them up in memory

  A window function is read in select, beside the row's own values. Its
  window is `Win.partition key` (a value or a tuple, as groupBy takes) or
  `Win.whole`, ordered with `|> Win.orderBy k`, `Win.thenBy` and their
  Descending forms, and the function takes the window last:
  `Win.rowNumber (Win.partition o.CustomerId |> Win.orderBy o.PlacedAt)`.
  Win.rowNumber, Win.rank, Win.denseRank, Win.lag, Win.lead and
  Win.runningSum depend on an order, and one over a window with none is
  refused by name; Win.sum, Win.avg and Win.count are over every row of the
  window, whatever its order. Win.lag and Win.lead are None at the window's
  edge. The window is over the rows the query's where keeps, before its order
  and page: to filter by a window's value, query a query that reads it.

  # Ada: 10 (day 1), 40 (day 3), 40 (day 4); Bob: 20 (day 2)
  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And Ada with Orders of 10, 40 and 40 and Bob with an Order of 20, on days 1 to 4

  Rule: A window is a partition and an order

    Scenario: Each customer's Orders numbered, ranked and totalled in order of their day
      When I read each Order's total with its number, rank and dense rank by total, its running total, and the totals before and after it, within its customer by day
      Then the rows are
        | customer | total | number | rank | dense | running | before | after |
        | Ada      | 10    | 1      | 1    | 1     | 10      | none   | 40    |
        | Ada      | 40    | 2      | 2    | 2     | 50      | 10     | 40    |
        | Ada      | 40    | 3      | 2    | 2     | 90      | 40     | none  |
        | Bob      | 20    | 1      | 1    | 1     | 20      | none   | none  |

    Scenario: A whole window's sum, average and count, beside each row
      When I read each Order's total with the sum, average and count of all Orders, and of its customer's
      Then every row has the sum 110, the average 27.5 and the count 4
      And Ada's rows have the sum 90 and the count 3, and Bob's the sum 20 and the count 1

    Scenario: The window is over the rows the query keeps
      When I read the Orders over 15 numbered by day across all customers
      Then the numbers are 1, 2 and 3

  Rule: What a window function cannot be is refused by name

    Scenario Outline: <what> is refused
      When I read <query>
      Then the windowed read is refused, saying "<message>"

      Examples:
        | what                       | query                                         | message                       |
        | A rank over no order       | the Orders ranked within their customer      | Win.rank needs an order       |
        | A row number over no order | the Orders numbered over the whole           | Win.rowNumber needs an order  |
        | A window function in where | the Orders whose number by day is 1          | is read only in select        |
