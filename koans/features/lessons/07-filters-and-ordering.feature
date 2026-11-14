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
      | koan                              | the learner discovers                                                               | teaches                                                                         |
      | Independent filters combine       | two where clauses combine with AND, each keeping its meaning                        | elegance: Independently defined filters preserve their meaning                  |
      | Comparing with None               | o.CustomerId = None means IS NULL                                                   | elegance: Null comparisons have explicit semantics                              |
      | not keeps F#'s meaning            | not (o.CustomerId = Some ada) keeps Orders with no Customer, exactly as F# would    | query-semantics: Negating an optional equality keeps rows whose value is absent |
      | Membership in an empty list       | List.contains over an empty list matches nothing, and is still a valid query        | elegance: Empty membership predicates remain valid                              |
      | Some code cannot be translated    | calling an ordinary function on a field is refused with the expression named        | core: Unsupported translation is explicit                                       |
      | Pages need an order               | take and skip without an ordering are refused, because the page would not be stable | elegance: Pagination requires stable ordering                                   |
      | Captured arrays are read each run | a query sees the current contents of a captured array                               | query-semantics: A query sees the current contents of a captured <container>    |
