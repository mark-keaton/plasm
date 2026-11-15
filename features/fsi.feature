# Executable acceptance specification (Reqnroll): using Plasm from F# Interactive.
# Written before the implementation, following the conventions of core.feature.
# Steps evaluate code in a real F# Interactive session hosted by the test, one
# submission at a time, as a person typing into the REPL would. The session
# references the built Plasm assemblies with #r, standing in for
# #r "nuget: Plasm.Postgres" until the packages are published, and #loads the
# test domain's source so its types are defined in the session.
@implemented
Feature: Using Plasm from F# Interactive
  As an F# developer exploring data in F# Interactive
  I want queries, results and errors to display usefully and experiments to be undoable
  So that the REPL is a good place to learn the model, try queries and check the database

  Each submission is evaluated separately, so values, types and connections live
  across submissions. Nothing here changes how Plasm behaves in compiled code: it
  adds displays, a helper for awaiting results, a long-lived playground and
  scaffolding.

  Background:
    Given a configured relational database
    And an F# Interactive session that references Plasm
    And mappings for Customers, Orders, OrderLines, Products, and Payments evaluated in the session

  Rule: Referencing Plasm is the only setup

    Scenario: Referencing the packages is enough for Plasm values to display
      Given a new F# Interactive session
      When I reference Plasm and the PostgreSQL provider
      And I evaluate the mappings and a repository
      And I evaluate a query for a customer's published Orders
      Then the session displays the SQL with each parameter and its value
      And no setup call was needed

    Scenario: Referencing Plasm twice is harmless
      When I reference Plasm and the PostgreSQL provider again
      And I evaluate a query for a customer's published Orders
      Then the session reports no errors
      And the query is displayed once

  Rule: Values display what they mean

    Scenario: A query displays its SQL and parameters
      When I evaluate a query for a customer's published Orders
      Then the session displays the SQL with each parameter and its value
      And no database command was executed

    Scenario: A query with loads displays one command per load
      When I evaluate a query for Orders, which own their Lines
      Then the session displays the main command and one command for each load, in execution order
      And no database command was executed

    Scenario: A query that cannot be translated displays why
      When I evaluate a query whose filter calls an ordinary F# function on a field
      Then the session displays the translation error with the expression identified
      And the session does not display SQL

    Scenario: A prepared query displays its bound arguments
      When I evaluate a prepared query for the Orders of a Customer, bound to one Customer
      Then the session displays the SQL with each argument's bound value and position
      And no database command was executed

    Scenario: Displaying a query leaves how it runs unchanged
      When I evaluate a query and display it
      Then no repository has planned it
      And running it afterwards plans it once

    Scenario: Formatting a query in compiled code hides its parameter values
      Given a repository that does not include parameter values in diagnostics
      When compiled code formats a query with %A
      Then the SQL is shown with its parameters but not their values

    Scenario: Formatting a query shows values when the repository includes them
      Given a repository that includes parameter values in diagnostics
      When compiled code formats a query with %A
      Then the SQL is shown with each parameter's value

    # One style: "Relationships that were not loaded display as not loaded" was
    # removed; no relationship is ever unloaded.

  Rule: Results display without awaiting tasks

    Scenario: A read displays its rows
      Given Customers with Orders exist
      When I evaluate a read of Customers through the interactive helpers
      Then the session displays the Customers, not a task

    Scenario: A failed write displays the classified error
      Given a Customer with a given email exists
      When I evaluate an insert of another Customer with the same email through the interactive helpers
      Then the session displays a unique constraint violation naming the constraint
      And the session remains usable

  Rule: Redefining types in a session is explained

    Scenario: A query built before a type was redefined is rejected with an explanation
      Given a query for Customers evaluated in the session
      When I redefine the Customer record in a later submission and re-evaluate the mappings
      And I run the earlier query with the new repository
      Then the error says the query uses Customer from an earlier submission and must be re-evaluated
      And the earlier query now displays the same explanation instead of SQL
      And no database command was executed

    Scenario: Queries re-evaluated after a redefinition work
      When I redefine the Customer record in a later submission and re-evaluate the mappings
      And I evaluate and run a new query for Customers
      Then the Customers are returned using the new definition

  Rule: A playground keeps experiments across submissions and undoes them

    Scenario: Changes in a playground are visible in later submissions
      When I open a playground and insert a Customer
      And I read Customers from the playground in a later submission
      Then the inserted Customer is returned
      And the Customer is not visible outside the playground

    Scenario: Resetting a playground discards its changes and keeps it open
      Given a playground in which a Customer was inserted
      When I reset the playground
      Then the Customer no longer exists in the playground
      And later work in the playground succeeds

    Scenario: Closing a playground leaves the database unchanged
      Given a playground in which a Customer was inserted
      When I close the playground
      Then the Customer does not exist
      And using the closed playground fails with an explanation instead of a connection error

    Scenario: A database error does not end the playground
      Given a playground
      When code in the playground hits a unique constraint violation and then continues
      Then the violation is displayed as usual
      And earlier changes in the playground are kept

    Scenario: A sandbox inside a playground rolls back to the playground
      Given a playground in which a Customer was inserted
      When a sandbox inside the playground inserts another Customer
      Then after the sandbox ends the playground still has only its own Customer

    Scenario: A playground whose connection was lost says so and starts afresh
      Given a playground in which a Customer was inserted
      When the playground's database connection is terminated
      Then the next use of the playground reports that its changes were lost
      And later work in the playground succeeds

  Rule: Records can be generated from a table

    Scenario: Scaffolding a table prints a record that says how the table stores it
      When I scaffold the "customers" table
      Then the session displays an F# record that names its table
      And evaluating it in the session succeeds
      And the schema check reports no problems for it

    Scenario: Scaffolding maps nullable columns, keys and unknown types explicitly
      When I scaffold a table with a nullable column, a generated key and a column of an unsupported type
      Then the nullable column becomes an option field
      And the generated key is the record's Id, generated by the database
      And the unsupported column is left out with a comment naming its database type

    Scenario: Names that are not F# identifiers are escaped
      When I scaffold a table named "type" with columns "Weird Col" and "2fa"
      Then evaluating the scaffolded code in the session succeeds
      And the schema check reports no problems for it

    Scenario: A table with no mappable columns is reported
      When I scaffold a table whose only column has an unsupported type
      Then scaffolding fails because no column can be mapped

    Scenario: A missing table is reported as missing
      When I scaffold a table that does not exist
      Then scaffolding fails because the table does not exist
