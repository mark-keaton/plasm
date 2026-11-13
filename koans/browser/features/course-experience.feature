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

  Rule: The editor helps

    Scenario: Blanks are easy to find
      When a koan is shown
      Then each blank in its code is highlighted

    Scenario: The course links back to Plasm's specification
      When a koan is shown
      Then it links to the acceptance scenario it teaches
