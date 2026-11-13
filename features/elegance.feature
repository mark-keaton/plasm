# Executable acceptance specification (Reqnroll).
# Source: the supplied specification, preserved verbatim in docs/specs/original/elegance.feature.md.
# Added for Gherkin validity: the Feature header and Background (shared with core.feature);
# the non-keyword line "Or ..." is written "* Or ...". Scenario wording is unchanged.
@implemented
Feature: Idiomatic and principled F# persistence
  Refinements of the core specification: domain types, relational meaning,
  algebraic composition, safe partial data, testable efficiency and explicit retries.

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And an Order belongs to a Customer
    And an Order has many OrderLines
    And an Order has many Payments
    And an OrderLine belongs to a Product

  Rule: Ordinary F# domain types remain usable

    Scenario: Persistence metadata does not require mutable records
      Given an immutable F# record with supported mapped fields
      When I configure its persistence mapping
      Then I do not need to add public setters
      And I do not need to add a parameterless constructor
      And I do not need to inherit from a persistence base class

    Scenario: Domain scalar wrappers preserve their meaning
      Given distinct CustomerId and OrderId types
      And each has a configured scalar mapping
      When I construct queries and changes
      Then those identifiers remain distinct at compile time
      And their database parameters use the configured scalar representation

    Scenario: Domain reconstruction can fail explicitly
      Given a persisted value violates a domain constructor's requirements
      When the library materializes the record
      Then it reports a structured materialization error
      And it does not bypass the constructor or invent a valid value

  Rule: Query composition preserves relational meaning

    Scenario: Relationship filtering does not duplicate parents
      Given an Order has several Payments matching a relationship predicate
      When I query Orders satisfying that predicate
      Then the Order appears once
      And parent pagination is not affected by the number of matching Payments

    Scenario: Optional relationships preserve parent rows
      Given an Order has no optional Customer relationship
      When I load that relationship
      Then the Order remains in the result
      And the relationship is represented as loaded and absent

    Scenario: Null comparisons have explicit semantics
      Given a mapped optional field
      When I query for values equal to None
      Then the database predicate tests for SQL NULL
      And the comparison does not silently become SQL equality with NULL

    Scenario: Empty membership predicates remain valid
      When I filter Orders by membership in an empty identifier collection
      Then the result is empty
      And no invalid SQL is generated

    Scenario: Pagination requires stable ordering
      When I paginate a query without a deterministic ordering
      Then the library requires an explicit ordering
      * Or applies a documented unique ordering
      And it does not silently promise stable pages

  Rule: Composition has consistent algebraic behavior

    Scenario: Independently defined filters preserve their meaning
      Given two pure predicates combined by logical AND
      When I compose them in either order
      Then both queries return equivalent results

    Scenario: Repeating an identical load does not duplicate work
      Given two query-building functions request the same relationship load
      When I compose them
      Then the relationship is loaded once
      And its records are not duplicated

    Scenario: Empty transaction descriptions are valid
      When I execute a transaction description containing no operations
      Then it succeeds with an empty result
      And it performs no writes

  Rule: Partial data cannot accidentally become destructive writes

    Scenario: Projected values do not imply complete entity state
      Given I query only an Order identifier and status
      When I construct an update from that projection
      Then unselected fields cannot implicitly become defaults or NULL
      And only explicitly described fields are eligible for update

    Scenario: Filtered collections are not authoritative replacements
      Given I load only successful Payments for an Order
      When I submit an unrelated Order change
      Then unsuccessful Payments remain unchanged
      When I attempt to replace Payments using the filtered collection
      Then the API requires explicit acknowledgement of replacement scope

    Scenario: Duplicate child identities are rejected
      Given an association changeset contains two entries for the same child identity
      When I validate the changeset
      Then it reports the duplicate identity
      And it does not choose an entry based on incidental ordering

  Rule: Efficiency guarantees are testable

    Scenario: Loading plans can be inspected
      Given a query with nested separate-query loads
      When I inspect its execution plan
      Then I can identify the requested load paths
      And the planned batching strategy
      And inspection performs no database reads or writes

    Scenario: Larger parent sets do not cause N+1 behavior
      Given the same relationship load executed for different parent counts
      When both counts fit within provider batching limits
      Then the number of relationship commands does not grow per parent

    Scenario: Database work remains bounded by the requested page
      Given a large Orders table
      When I request one page with related collections
      Then the library does not materialize all matching Orders in application memory
      And relationship queries are restricted to the selected parents

  Rule: Transaction retries do not conceal repeated execution

    Scenario: Retry behavior is explicit
      Given an operation encounters a retryable database failure
      When no retry policy is configured
      Then the library does not silently rerun application callbacks

    Scenario: Retried transactions use a fresh attempt context
      Given a configured transaction retry policy
      When a transaction is retried
      Then database-dependent reads are repeated within the new attempt
      And results from the failed attempt are not presented as committed
