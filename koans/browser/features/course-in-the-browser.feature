# Behaviour specification: the Plasm Koans in a web browser, with no server.
# Written before the implementation. The page loads the F# compiler (WebAssembly)
# and PostgreSQL (PGlite, WebAssembly) and runs every koan in the page itself.
# Checks drive a real headless browser (Playwright).
# @serial: its loading budget is measured over a simulated connection that
# must not be shared with other scenarios' downloads.
@serial
Feature: The koans run in a web browser
  As a developer curious about Plasm
  I want to open a web page and start the koans
  So that I can learn Plasm without installing .NET, Docker or an editor

  Background:
    Given the koans site is served as static files

  Rule: Opening the page is all it takes

    Scenario: The first koan is ready without installing anything
      When a learner opens the koans page in a browser
      Then the page shows the lessons and the first koan with its blank
      And the first koan can be run once loading has finished

    Scenario: Loading shows its progress
      When a learner opens the koans page in a browser
      Then the page shows what is loading (the compiler, the libraries, the database) until it is ready

    Scenario: Loading stays within budget
      When a learner opens the koans page on a typical connection
      Then the first koan can be run within 20 seconds
      And the page transfers at most 40 MB, compressed

    Scenario: A browser without WebAssembly is told why the course cannot run
      When the koans page is opened in a browser without WebAssembly
      Then the page explains that WebAssembly is required and links to the koans for the terminal

  Rule: Nothing runs on a server

    Scenario: Every request is for a static file
      When a learner runs every koan of the first lesson
      Then every network request is for a static file the site serves
      And no code or data is sent anywhere

    Scenario: The course works offline after the first visit
      Given a learner has opened the koans page once
      When the learner opens it again without a network connection
      Then the first koan can be run
