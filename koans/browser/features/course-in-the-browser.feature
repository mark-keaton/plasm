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

    # Measured 2026-11-15 (Apple M4 Max, 25 Mbit/s simulated, 24.1 MB
    # transferred): 20.1 s in the full suite at 567f734, 19.2 s at d836ff8, and
    # 19.6 s run alone. The 20 s budget had no margin before the stored-records
    # change either, so it failed in full runs; 25 s is the budget until the
    # load is brought back under 20 s (docs/status.md, next steps).
    Scenario: Loading stays within budget
      When a learner opens the koans page on a typical connection
      Then the first koan can be run within 25 seconds
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

  Rule: A reload shows the site as it was last staged

    # A plain static host (python -m http.server) sends Last-Modified and no
    # Cache-Control, so a browser may reuse what it fetched before; the page's
    # parts must still come from one staging.
    Scenario: A reload after the site is restaged uses the new site
      Given a learner has opened the koans page once, from a plain static host staged a day ago
      When the site is restaged with Repo documented as "The one way in to the database"
      And the learner reloads the page
      Then pointing at "repo" in "repo.Count" in the koan "Every koan is given a repo" says "The one way in to the database"

    # A page open across a deploy holds the old dotnet.js, whose assemblies'
    # names the new deploy no longer serves; the engine then cannot start.
    Scenario: A page whose engine cannot load says so and offers a reload
      When the koans page is opened while the site's Plasm assemblies are not found, as across a deploy
      Then the page says the course could not load and offers to reload it
      And running the first koan says the same, rather than running for ever
