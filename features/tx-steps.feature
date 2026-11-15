# Executable acceptance specification (Reqnroll): transaction steps.
# Written before the implementation, following the conventions of core.feature.
# Added after the architecture audit.
@implemented
Feature: Transaction steps do what they say
  As an F# application developer
  I want each transaction step to do exactly the kind of work its name says
  So that a transaction reads truthfully and cannot end itself early

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments

  # One style: "The repository rejects a mismatched changeset the same way" was
  # removed with the repository's write methods; every write is a transaction
  # step named for its action (Tx.insert, Tx.update, Tx.delete).

  Rule: Custom steps work inside the transaction without controlling it

    Scenario: A custom step's commands run in the transaction
      When a transaction runs a custom step that inserts a row with a command and a later step fails
      Then the custom step's row was rolled back

    Scenario: A custom step cannot commit or roll back the transaction
      When I write a custom step that commits the transaction it was given
      Then the code does not compile, because the step context has no transaction to commit

    Scenario Outline: A custom step cannot end the transaction with SQL
      When a transaction inserts a Customer and then runs a custom step whose command is "<sql>"
      Then the transaction fails at the custom step, saying a step cannot control the transaction
      And the Customer was rolled back
      And no exception is thrown

      Examples:
        | sql                                  |
        | COMMIT                               |
        | rollback                             |
        | SELECT 1; END                        |
        | /* note */ BEGIN                     |
