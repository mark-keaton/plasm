# Behaviour specification: running a koan's F# in the page.
Feature: Running a koan in the browser
  As a learner
  I want to edit a koan's code and run it in the page
  So that I see at once whether my answer is right, as in the terminal version

  Background:
    Given the koans page has finished loading

  Rule: Results read like the terminal version

    Scenario: A koan with its blank unfilled asks for it
      When the learner runs the first koan without changing it
      Then the koan shows that it is not yet passed
      And it points at the line of the blank in the editor
      And it shows no hint yet

    Scenario: A correct answer passes
      When the learner fills in the first koan's blanks with its reference answer and runs it
      Then the koan shows that it passed
      And the next koan is offered

    Scenario: A wrong answer shows what was expected and what happened
      When the learner fills in the first koan's first blank with a wrong value and runs it
      Then the koan shows the expected and the actual value, displayed as F# Interactive would
      And it shows what the koan teaches

    Scenario: A wrong answer shows the koan's hint
      When the learner fills in the first koan's first blank with a wrong value and runs it
      Then the koan shows its hint, as the lesson specification words it

    Scenario: A koan that talks to the database shows the SQL it generated
      When the learner runs a database koan with a wrong answer
      Then the koan shows the SQL Plasm generated, formatted by clause

    Scenario: A compile error points at the code
      When the learner writes code in a koan that does not compile and runs it
      Then the koan shows the compiler's message at its line and column
      And no earlier koan's result changes

  Rule: Running is safe for the page

    Scenario: The page stays responsive while a koan compiles and runs
      When the learner runs a koan
      Then the page keeps responding to input while it compiles and runs

    Scenario: A koan that never finishes can be stopped
      When the learner runs a koan whose code loops forever
      Then the learner can stop it without reloading the page
      And later koans can still be run

    Scenario: An exception is reported as the koan's failure
      When the learner fills in a koan with code that throws
      Then the koan shows the exception's message
      And the page carries on
