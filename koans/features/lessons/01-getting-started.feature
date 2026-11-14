# Lesson 01 of the Plasm Koans, the orientation lesson. Each row is one koan:
# what the learner discovers, and the F# topic it teaches (F#: topic). It needs
# no database.
Feature: Getting started
  A koan is a few lines of F# with a blank (__) to fill in. This lesson shows how a blank works, and the F# the course relies on: values, lists and the pipe, records and with, wrappers, options, results and tasks.

  Prerequisites: none.

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the F# topic "<teaches>"

    Examples:
      | koan                              | the learner discovers                                                                                          | teaches                                |
      | Fill in the blank                 | a koan passes when the value written in place of __ is the value the code produces                             | F#: Values and let bindings            |
      | Values do not change              | let names a value once; working out a new value leaves the old one as it was                                   | F#: Immutable values                   |
      | The pipe passes a value along     | the pipe operator hands the value on its left to the function on its right, so a list flows through each step  | F#: Lists and the pipe operator        |
      | A record groups named fields      | a record is a value with named fields, read with a dot                                                         | F#: Records                            |
      | with makes a changed copy         | { r with Name = ... } is a new record; the record it was made from keeps its fields                            | F#: Copy-and-update record expressions |
      | A wrapper keeps a value's meaning | CustomerId 1L is not a plain number; matching on the case takes the number back out                            | F#: Single-case unions                 |
      | An option may be absent           | an option is Some value or None, and Option.defaultValue says what to use when it is None                      | F#: Options                            |
      | A result is Ok or Error           | Quantity.create returns Ok for a valid quantity and Error with a message otherwise; ok and err take them apart | F#: Results                            |
      | let! waits for a task             | a task is work that finishes later; inside task { }, let! waits for it and names its value                     | F#: Tasks and let!                     |
