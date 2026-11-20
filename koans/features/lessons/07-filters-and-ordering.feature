# Lesson 07 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About filters and ordering
  Filters mean what the same F# predicate means. Anything that cannot run in the database is refused, never evaluated in memory.

  Prerequisites: lesson 06.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                              | the learner discovers                                                               | teaches                                                                         | hint                                                                                                                               |
      | Comparing numbers                 | o.Total > 20m keeps the Orders whose total is more than 20, as in F#                | core: Querying a mapped property uses its declared type                         | Count the rows of the orders table under Given whose total is over twenty.                                                         |
      | Contains on text becomes LIKE     | Name.Contains "o" is sent as SQL's LIKE and keeps the names with an o               | core: Querying a mapped property uses its declared type                         | The comment says what Contains becomes in SQL, and the customers table under Given shows which names have an o in them.            |
      | And needs both                    | && inside a where keeps a row only when both sides are true                         | elegance: Independently defined filters preserve their meaning                  | Count the rows of the orders table under Given that are published and also under fifty.                                            |
      | Or needs either                   | \|\| inside a where keeps a row when either side is true                            | core: Reusable filters compose                                                  | Count the rows of the orders table under Given that are drafts or over fifty, each once.                                           |
      | Ordering largest first            | orderByDescending sorts the rows largest first                                      | query-semantics: The ordering is done by the database                           | Write the totals in the orders table under Given from the largest down, each with an m.                                            |
      | Independent filters combine       | two where clauses combine with AND, each keeping its meaning                        | elegance: Independently defined filters preserve their meaning                  | A second where narrows the first, as && does, so count the published orders over fifty.                                            |
      | Comparing with None               | o.CustomerId = None means IS NULL                                                   | elegance: Null comparisons have explicit semantics                              | Lesson 04's koan 'Queries mean what F# means' asked this same question of the same data.                                           |
      | not keeps F#'s meaning            | not (o.CustomerId = Some ada) keeps Orders with no Customer, exactly as F# would    | query-semantics: Negating an optional equality keeps rows whose value is absent | Count the rows of the orders table under Given that are not Ada's as F# would, the order with no customer included.                |
      | Membership in an empty list       | List.contains over an empty list matches nothing, and is still a valid query        | elegance: Empty membership predicates remain valid                              | Ask how many statuses are in the list named wanted, and so how many orders can have a status that is in it.                        |
      | Some code cannot be translated    | calling an ordinary function on a field is refused with the expression named        | core: Unsupported translation is explicit                                       | Lesson 04's koan 'Nothing is evaluated behind your back' ran this same query, and the match here names the kind of error it got.   |
      | Pages need an order               | take and skip without an ordering are refused, because the page would not be stable | elegance: Pagination requires stable ordering                                   | The koan's name says what a take needs, and the second query sorts the totals smallest first before it takes its rows.             |
      | Captured arrays are read each run | a query sees the current contents of a captured array                               | query-semantics: A query sees the current contents of a captured <container>    | The array is read again at each run, so count the orders with the status it holds at first, then with the status it is changed to. |
