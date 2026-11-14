# Behaviour specification: the learner's experience of the browser course.
Feature: Walking the path in the browser
  As a learner
  I want the browser course to keep my place and help me focus
  So that I can learn over several visits

  Background:
    Given the koans page has finished loading

  Rule: The course is the same course

    Scenario: The browser course has every lesson and koan of the terminal version
      When the browser course is compared with the koans' lesson specifications
      Then it has every lesson, in order, and every koan, in order, with the same guidance

    Scenario: With the reference answers every koan passes in the browser
      When every koan is filled in with its reference answer and run in the page
      Then every koan passes

    Scenario: As shipped no koan passes before it is answered
      When every koan is run in the page as shipped
      Then every koan asks for its blank

  Rule: Progress is kept

    Scenario: Answers and progress survive a reload
      Given the learner has passed the first two koans and edited the third
      When the learner reloads the page
      Then the first two koans are shown as passed
      And the third koan shows the learner's edit

    Scenario: A koan can be reset to its original code
      Given the learner has edited a koan
      When the learner resets it
      Then it shows its original code with its blanks

    Scenario: Progress is shown per lesson
      Given the learner has passed some koans
      Then every lesson shows how many of its koans are passed

    Scenario: Progress through the whole course is shown
      Given the learner has passed some koans
      Then the page shows how many of all the koans are passed

    Scenario: Only the lesson being studied is unfolded
      When a koan in a later lesson is shown
      Then that lesson's koans are listed
      And the other lessons show only their titles and progress

  Rule: The editor helps

    Scenario: Blanks are easy to find
      When a koan is shown
      Then each blank in its code is highlighted

    Scenario: The course links back to Plasm's specification
      When the learner opens the koan "Wrapped keys keep their meaning"
      Then it links to the acceptance scenario it teaches

    Scenario: The first lesson names the F# it teaches
      When the learner opens the first koan
      Then it names the F# topic it teaches, without a link

    Scenario: Code is coloured as F#
      When the learner opens the koan "A record needs nothing from Plasm"
      Then its keywords, type names, strings, numbers and comments are coloured

    Scenario: Colouring follows the learner's edits
      When the learner types code into a koan
      Then the code they typed is coloured too

    Scenario: Lines are numbered as compile errors number them
      When a koan is shown
      Then each line of its code is numbered, starting from 1

    Scenario: A koan can be run from the keyboard
      When the learner fills in the first koan's blanks and presses Ctrl+Enter
      Then the koan shows that it passed

    Scenario: F# operators are drawn as single symbols
      When a koan is shown
      Then its code is set in a font that joins operators such as |> and ->

  # A koan's blanks must be answerable from the page: its code, the data it
  # starts from and the definitions it uses. Each koan opens with them.
  Rule: Each koan opens with what it is given

    Scenario: A koan shows the tables it uses, with their starting rows
      When the learner opens the koan "Wrapped keys keep their meaning"
      Then under Given the customers table shows its 3 starting rows
      And Ada's row shows the id 1

    Scenario: A NULL is shown as NULL
      When the learner opens the koan "Absent values are options"
      Then under Given the orders table shows NULL as the customer of order 4

    Scenario: A koan shows the definitions it uses, from the shop's source
      When the learner opens the koan "Stored values can be rejected"
      Then under Given the definition of Quantity includes "if v > 0 then Ok(Quantity v)"
      And the definition says it comes from Shop.fs

    Scenario: The fields a koan reads are marked in their record
      When the learner opens the koan "Absent values are options"
      Then under Given the definition of Order marks the fields Id, CustomerId and CancellationReason

    Scenario: A table changed with SQL is given too
      When the learner opens the koan "Stored values can be rejected"
      Then under Given the order_lines table shows its 4 starting rows

    Scenario: Every table is a click away
      When the learner opens the koan "Wrapped keys keep their meaning"
      And asks for all tables
      Then under Given every table of the koans database is shown

    Scenario: A koan that needs nothing has no Given
      When the learner opens the first koan
      Then no Given is shown
