# Executable acceptance specification (Reqnroll): a repository starts only when
# its queries and the database fit. Written before the implementation.
@implemented
Feature: A repository starts only when its queries and the database fit
  As an F# application developer
  I want my repository checked when it starts, and start-up to fail by default
  So that a query Plasm cannot translate, or a database that does not fit the
  model, is found when the application starts, never on first use

  `Repo.Start(model, provider, queriesIn = assembly)` is the one way to get a
  repository. It translates every query its assembly defines as a module value,
  prepared queries with their arguments as parameters, without running any,
  and compares the model with the database. What it does with what it finds is
  set in RepoOptions, separately for the queries and for the schema: `Crash`
  (the default) fails start-up with every problem in one exception, `Warn`
  reports each through OnProblem and starts, `Ignore` does not check. A check
  that finds no queries has checked nothing, so that is a problem too. A
  function returning a query is not a value, so it is not checked; how many
  there are is said.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  Rule: By default a problem fails start-up, naming every one

    Scenario: Queries that translate and a database that fits start a repository
      When I start a repository for the assembly whose queries all translate
      Then the repository starts
      And it reads the Customers

    Scenario: Queries that cannot be translated fail start-up, each named
      When I start a repository for the assembly with queries Plasm cannot translate
      Then start-up fails, saying "4 of 4 queries in Plasm.Specs.Queries.Bad cannot be translated"
      And the failure names the queries "Bad.normalized", "BadLoads.counted", "BadPrepared.normalizedNamed" and "BadProjection.normalizedNames", in that order, each with why

    Scenario: A database that does not fit fails start-up
      Given the customers table has lost its email column
      When I start a repository for the assembly whose queries all translate
      Then start-up fails, saying "1 schema difference"
      And the failure names the missing column customers.email

    Scenario: Queries come before schema differences, so two runs read the same
      Given the customers table has lost its email column
      When I start a repository for the assembly with queries Plasm cannot translate
      Then the failure lists the four queries by name, then the missing column

    Scenario: An assembly with no queries has checked nothing
      When I start a repository for an assembly that defines no queries
      Then start-up fails, saying "found no query values in Plasm.Annotations"

    Scenario: Functions returning a query are counted, not checked
      When I start a repository for the assembly whose queries all translate, warning
      Then the repository starts
      And a note says "1 function returning a query is not checked: Good.namedLike"

  Rule: Warn reports each problem and starts

    Scenario: Warned problems are reported and the repository starts
      When I start a repository for the assembly with queries Plasm cannot translate, warning
      Then the repository starts
      And 4 problems are reported, naming "Bad.normalized", "BadLoads.counted", "BadPrepared.normalizedNamed" and "BadProjection.normalizedNames"

    Scenario: Without OnProblem a warning goes to standard error
      When I start a repository for the assembly with queries Plasm cannot translate, warning, with no OnProblem
      Then standard error names "Bad.normalized"

    Scenario: The queries and the schema are set separately
      Given the customers table has lost its email column
      When I start a repository for the assembly whose queries all translate, warning only for the schema
      Then the repository starts
      And 1 problem is reported, naming the missing column customers.email

  Rule: Ignore does not check

    Scenario: Ignored, nothing is translated or read
      Given the customers table has lost its email column
      When I start a repository for the assembly with queries Plasm cannot translate, ignoring both
      Then the repository starts
      And no database command is executed

  Rule: A prepared query is still called like a function

    Scenario: A prepared projection reads with its argument
      Given Customers named "Ada" and "Bob"
      When I read the names with the checked prepared projection for the name "Bob"
      Then only the name "Bob" is returned

  Rule: A repository is made only by starting it

    Scenario: Repo has no public constructor
      When I compile code that makes a repository with Repo(model, provider)
      Then the code does not compile
