# Lesson 02 of the Plasm Koans. Each row is one koan: what the learner discovers,
# and the acceptance scenario of Plasm it teaches (feature: scenario).
Feature: What a repo is
  Every koan is given repo, a Plasm repository over the koans database. A repo runs queries and writes against one database with one model. A query is only a description: the repo turns it into SQL, sends it when asked, and returns a Result.

  Prerequisites: lesson 01.

  Background:
    Given the koans database with its starting data

  Scenario Outline: <koan>
    Given the koan "<koan>" in this lesson
    Then it passes with its reference answer and fails until its blanks are filled
    And it teaches the acceptance scenario "<teaches>"

    Examples:
      | koan                                 | the learner discovers                                                                             | teaches                                                    |
      | Every koan is given a repo        | every koan is given repo, a Plasm Repo whose changes are undone when the koan ends; its Count answers Ok 3L for the three customers | aggregates: Counting a filtered query                                    |
      | ok opens a Result                 | a repo answers with a Result; ok takes the value out of an Ok, so ok (Ok 4L) is 4L                    | core: Unsupported translation is explicit                                |
      | A repo pairs a model with a database | repo holds Db.model and its ten record types, and can check them against the database's tables | schema-check: A model that matches the database passes     |
      | In your app, you make the repo    | Repo(Db.model, provider) makes a repo, as an app does with Postgres.connect; it reads the same three customers | schema-check: A model that matches the database passes                   |
      | A query is a description             | Query.from builds a value; no command is sent until a repo runs it                                | core: Constructing a query performs no database operations |
      | The repo shows its SQL               | repo.Explain shows the SQL a repo would send for a query, without sending anything                       | elegance: Loading plans can be inspected                   |
      | Running sends that SQL               | repo.All sends the query as one command, exactly the SQL repo.Explain showed                             | core: Query execution exposes diagnostic information       |
      | A query runs each time you ask       | running the same query twice sends it twice; the query value itself does not change               | core: Executing a query does not mutate its description    |
      | Every call returns a Result          | a count comes back as Ok 4L, and finding no row is Ok None, not a failure                         | reading: A query matching no rows returns None             |
      | A repo knows only its model          | a repo whose model knows only Product returns an Error naming Customer, and sends nothing          | core: Unsupported translation is explicit                  |
