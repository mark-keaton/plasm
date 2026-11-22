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
    And an Order refers to its Customer by id
    And an Order owns its OrderLines
    And a Payment refers to its Order by id
    And an OrderLine refers to its Product by id

  Rule: Ordinary F# domain types remain usable

    Scenario: Persistence metadata does not require mutable records
      Given an immutable F# record with supported mapped fields
      When I configure its persistence mapping
      Then I do not need to add public setters
      And I do not need to add a parameterless constructor
      And I do not need to inherit from a persistence base class

    Scenario: Domain scalar wrappers preserve their meaning
      Given distinct CustomerId and OrderId types
      And each is stored as its value by convention
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
      When I read it with its Customer through a left join
      Then the Order remains in the result
      And its Customer is None

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

    # One style: "Filtered collections are not authoritative replacements" was
    # removed. An owned collection is always loaded whole, so there is no
    # filtered collection to write back.

    Scenario: Duplicate child identities are rejected
      Given an Order change whose OrderLines hold two entries for the same identity
      When I write it
      Then it reports the duplicate identity
      And it does not choose an entry based on incidental ordering

  Rule: Efficiency guarantees are testable

    Scenario: Loading plans can be inspected
      Given a query for Orders, which own their OrderLines
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

    @postgres
    Scenario: Retry behavior is explicit
      Given an operation encounters a retryable database failure
      When no retry policy is configured
      Then the library does not silently rerun application callbacks

    @postgres
    Scenario: Retried transactions use a fresh attempt context
      Given a configured transaction retry policy
      When a transaction is retried
      Then database-dependent reads are repeated within the new attempt
      And results from the failed attempt are not presented as committed

  Rule: A retried transaction waits before trying again, so that the transaction it lost to can commit

    # PostgreSQL cancels a serializable transaction that would depend on one
    # committed, or prepared to commit, that it cannot yet see. Retried at once,
    # it can spend every attempt while that commit is still being written. The
    # first Order's transaction is held there, prepared but not visible
    # (PREPARE TRANSACTION), while the second's runs: it reads the published
    # Orders, as the first did, and cancels the second Order.
    @postgres
    Scenario: Retried at once, a transaction loses every attempt to a commit still being written
      Given two published Orders
      And the first Order cancelled by a serializable transaction that read the published Orders, prepared but not yet committed
      When the second Order is cancelled the same way, retried up to 5 times without waiting
      Then it fails as a serialization failure after 5 attempts

    @postgres
    Scenario: Waiting between attempts, a transaction tries again once that commit has landed
      Given two published Orders
      And the first Order cancelled by a serializable transaction that read the published Orders, prepared but not yet committed
      When the second Order is cancelled the same way, retried up to 5 times waiting 100 ms between attempts, while the first commits 150 ms later
      Then the second Order's change is committed
      And both Orders are cancelled

    @postgres
    Scenario: A transaction that fails after waiting says how long it waited
      Given two published Orders
      And the first Order cancelled by a serializable transaction that read the published Orders, prepared but not yet committed
      When the second Order is cancelled the same way, retried up to 3 times waiting 50 ms between attempts
      Then it fails as a serialization failure after 3 attempts, having waited at least 100 ms

    @postgres
    Scenario: A wait between attempts ends when the transaction is cancelled
      Given two published Orders
      And the first Order cancelled by a serializable transaction that read the published Orders, prepared but not yet committed
      When the second Order is cancelled the same way, retried waiting 10 seconds between attempts, and cancelled after 300 ms
      Then the transaction ends as cancelled within 3 seconds

    Scenario: The policy for serialization failures waits longer after each attempt, never the same twice in a row
      When I ask RetryPolicy.OnSerializationFailure 5 for its waits, many times over
      Then each wait after attempt n lies between half of and the whole of 10 ms doubled n - 1 times
      And the waits are not all the same
