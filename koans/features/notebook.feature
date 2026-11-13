# Behaviour specification (Reqnroll): the koans as a notebook.
# Written before the implementation. The notebook is generated from the lesson
# sources, so it teaches exactly what the program does; its cells are F#
# Interactive submissions. The checks run the cells one at a time in a real
# hosted F# Interactive session, the engine behind .NET Interactive's F# kernel.
Feature: The koans as a notebook
  As a developer learning Plasm
  I want to walk the koans in a notebook, one cell at a time
  So that I can learn by editing and re-running cells, and see Plasm working in F# Interactive

  The notebook opens with a setup cell that references the built koans and
  starts the koans database. Each lesson has a heading, a cell for its helpers,
  and one cell per koan. Running a koan cell runs it in its own sandbox and
  shows that it passed, or what to fill in or fix.

  Background:
    Given the koans database server

  Rule: The notebook is generated from the lessons

    Scenario: Every koan has a cell, in lesson order, under its lesson's heading
      When the notebook is generated from the koans as shipped
      Then it has a heading for every lesson, in order
      And every koan has its own code cell under its lesson's heading, in order
      And every koan cell names its koan

    Scenario: The committed notebook is up to date
      When the notebook is generated from the koans as shipped
      Then it is identical to koans/notebook/plasm-koans.ipynb

  Rule: The notebook runs cell by cell in F# Interactive

    Scenario: As shipped, every koan cell asks for its blank and the notebook keeps going
      When every cell of the notebook as shipped is run in order in F# Interactive
      Then every cell runs without a compile error
      And every koan cell says which koan it is and to fill in the blank in that cell

    Scenario: With the reference answers, every koan cell passes
      When every cell of the notebook with the reference answers is run in order in F# Interactive
      Then every koan cell reports that its koan passed

    Scenario: A wrong answer shows the expected and the actual value
      When the setup cell and a koan cell answered with a wrong value are run
      Then the koan cell shows the expected and the actual value
      And it shows what the koan teaches

    Scenario: The notebook runs in a real .NET Interactive kernel
      When the notebook as shipped and the notebook with the reference answers are run by dotnet-repl
      Then as shipped every koan cell asks for its blank
      And with the reference answers every koan cell passes
      And no other cell reports an error

    Scenario: Re-running a koan cell starts from the same data
      When a koan cell that writes to the database is run twice with its reference answer
      Then it passes both times
