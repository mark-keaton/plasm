# Behaviour specification (Reqnroll): what keeps the course honest.
# These rules are checked by the koans' own test suite, not by learners.
Feature: The curriculum is verified
  As a maintainer of the Plasm Koans
  I want every koan checked against its reference answer and the library's specification
  So that the course never teaches behaviour Plasm does not have

  Every koan has a reference answer kept outside the learner's files. The
  maintainers' test suite runs every koan twice: once as shipped, with blanks,
  and once with the reference answers filled in.

  Rule: Koans are correct, and only correct answers pass

    Scenario: Every koan passes with its reference answer
      When every koan is run with its reference answer
      Then every koan passes

    Scenario: No koan passes before it is answered
      When every koan is run as shipped
      Then every koan fails because a blank is unfilled
      And each failure points at the file and line of that blank

    Scenario: Shipped koans compile
      When the koans are built as shipped
      Then they compile without errors, because blanks are typed placeholders

  Rule: Koans teach the specified behaviour

    Lesson 01, the orientation lesson, teaches the F# the course relies on
    rather than Plasm, so its koans name an F# topic. Every other koan names
    an implemented acceptance scenario of Plasm.

    Scenario: Every koan traces to an acceptance scenario
      When the lesson specifications are read
      Then every koan after the orientation lesson names the acceptance scenario of Plasm that it teaches
      And every named scenario exists in Plasm's features and is implemented

    Scenario: The orientation lesson names the F# it teaches
      When the lesson specifications are read
      Then every koan of lesson 01 names the F# topic it teaches, as "F#: topic"
      And no koan of a later lesson names an F# topic instead of a scenario

    Scenario: Koans use only the API areas ready to teach
      When the koans' source is checked against the list of API areas ready to teach
      Then no koan uses an API area that is kept out of the course

    Scenario: Lessons build on each other
      When the lesson order is checked
      Then every lesson's prerequisites come before it

  Rule: A koan's hint points at its answer and does not give it

    A hint is the last cell of a koan's row in its lesson's feature: one
    sentence saying where on the page the answer comes from. The terminal runner
    shows it after a wrong answer; the browser, when the learner asks. A koan whose answer the page does not yet
    determine gets no hint to cover for it: its cell is empty, and
    no-hint-yet.txt names the koan and says what its page lacks.

    Scenario: Every koan has a hint, or a stated reason for having none yet
      When the lesson specifications are read
      Then every koan has a hint or is listed in no-hint-yet.txt with its reason
      And no koan listed there has a hint

    Scenario: A hint is one sentence
      When the lesson specifications are read
      Then every hint is a single sentence

    Scenario: No hint contains its answer
      When the lesson specifications are read
      Then no hint contains a reference answer of its koan
