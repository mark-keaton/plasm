# Lesson 29 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: About streaming reads
  repo.Stream gives a query's rows as the caller asks for them, never
  gathered into a list: nothing is sent before the first, a failure is a
  value, and a stream is one command.

  Prerequisites: lesson 08, lesson 09.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                          | the learner discovers                                                                        | teaches                                                                                | hint                                                                                                                                  |
      | A stream gives its rows as they are asked for | repo.Stream is an IAsyncEnumerable of Results, each row read as the caller asks for the next  | streaming: A stream gives every row of its query, in order                             | Give the names of the customers table under Given, by name.                                                                           |
      | Nothing is sent before the first row          | making a stream sends nothing; the first MoveNextAsync sends its one command                 | streaming: Nothing is sent until the caller asks for the first row                     | The comment says when the command is sent; count the statements sent before that and after the first row.                           |
      | A projection is streamed                      | a projection streams as a query of records does                                              | streaming: A projection is streamed                                                    | The same names as the first koan, read as a projection.                                                                              |
      | A stream is one command                       | records that own a list are read by more than one command, so streaming them is one Error     | streaming: A query of records that own a list is refused, naming the list               | The comment says what the stream gives instead of rows, and what the error names.                                                   |
