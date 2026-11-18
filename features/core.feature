# Executable acceptance specification (Reqnroll).
# Source: the supplied specification, preserved verbatim in docs/specs/original/core.feature.md.
# Changes for Gherkin validity only: the Markdown preamble/fences were removed and the
# non-keyword line "Or ..." is written "* Or ...". Scenario wording is unchanged.
# Tags: @implemented (verified by these steps), @changed (implemented with a documented
# deliberate difference), @planned (not yet implemented; ignored), @unsupported
# (explicitly rejected behavior). See docs/acceptance.md for the classification table.
@implemented
Feature: Predictable functional persistence for F#
  As an F# application developer
  I want composable queries, immutable data, explicit changes, and explicit execution
  So that persistence feels natural without sacrificing relationship loading or transactional correctness

  Background:
    Given a configured relational database
    And mappings for Customers, Orders, OrderLines, Products, and Payments
    And an Order refers to its Customer by id
    And an Order owns its OrderLines
    And a Payment refers to its Order by id
    And an OrderLine refers to its Product by id

  Rule: Persistence descriptions are values and execution is explicit

    Scenario: Constructing a query performs no database operations
      When I construct a query for Orders
      And add filters, sorting, projections, and relationship loads
      Then no database command is executed
      And the query can be passed to another function

    Scenario: Executing a query does not mutate its description
      Given a query for published Orders
      When I execute the query
      Then the query description remains unchanged
      And it can be executed again

    Scenario: Extending a query preserves the original query
      Given a base query for published Orders
      When I derive one query for a Customer
      And derive another query for a different Customer
      Then neither derived query changes the base query
      And neither derived query changes the other

    Scenario: Constructing a change performs no database operations
      Given an existing Order
      When I describe a change to its status
      Then the Order remains unchanged
      And no database command is executed

  Rule: The public API preserves useful F# type information

    Scenario: Querying a mapped property uses its declared type
      Given an Order has a decimal Total
      When I attempt to compare Total with an incompatible value
      Then the expression is rejected at compile time

    Scenario: Owned relationships are checked against the source type
      When I attempt to order the lines an Order owns by a field they do not have
      Then the declaration is rejected at compile time

    Scenario: Nullable values remain explicit
      Given an Order has an optional CancellationReason
      When I read an Order without a CancellationReason
      Then the value is represented as None
      And a present value is represented as Some

    Scenario: Unsupported model representations fail during configuration
      Given a model contains a type without a configured persistence mapping
      When I validate the persistence configuration
      Then configuration fails with the model and property identified
      And no implicit lossy conversion is selected

  Rule: Queries compose without silently changing execution location

    Scenario: Reusable filters compose
      Given a function that restricts Orders to a Customer
      And a function that restricts Orders to an outstanding balance
      When I apply both functions to an Order query
      Then the executed query applies both restrictions in the database

    Scenario: Captured values are treated as data
      Given a Customer name containing SQL syntax
      When I use that name as a query parameter
      Then the name is passed as a database parameter
      And it cannot alter the command structure

    Scenario: Unsupported translation is explicit
      Given a query contains an expression that cannot be translated
      When I attempt to execute it
      Then execution fails with the unsupported expression identified
      And the library does not silently load rows for client-side filtering

    Scenario: Client-side processing is an explicit boundary
      Given I explicitly execute a query into immutable values
      When I apply an ordinary F# function to those values
      Then the function runs in application memory
      And no additional database commands are issued

    Scenario: Projection retrieves the requested data
      Given a query projecting Order identifiers and totals
      When I execute the query
      Then unrelated mapped scalar columns are not retrieved
      And complete Order entities are not materialized

  Rule: An aggregate is loaded whole, and other aggregates are reached by query

    # One style (2026-11-14): no relationship is loaded on request. An Order owns
    # its OrderLines, always loaded with it; its Customer and its Payments are
    # other aggregates, referred to by id. Removed with Assoc and preload:
    # "Unloaded and empty relationships are distinguishable", "Nested loads
    # compose", "A child filter does not implicitly filter parents", "Per-parent
    # limits preserve their stated meaning", "Unsupported loading semantics are
    # rejected", "Conflicting relationship customizations are explicit" and
    # "Loading onto existing values preserves the originals". The latest few
    # rows per parent is a report: write it in SQL (raw-sql.feature).

    Scenario: Reading a relationship never triggers hidden database access
      Given an Order with an OrderLine, read from the database
      When application code inspects its OrderLines
      Then no database command is executed

    Scenario: Sibling collections load without a cartesian product
      Given an Order has 10 OrderLines
      And the Order has 10 Payments
      When I read the Order, and its Payments with a query by its id
      Then each related record appears once in its collection
      And it does not transfer their 100-row cross product

    Scenario: Relationship loading batches across parents
      Given a query returns 100 Orders
      When I read them, each with the OrderLines it owns
      Then the library fetches OrderLines in batches across those Orders
      And it does not issue one OrderLine query per Order
      And it associates each OrderLine with its owning Order

    Scenario: Pagination applies to parents before collection expansion
      Given Orders have a deterministic ordering
      When I request the first 20 Orders
      Then exactly the first 20 matching Orders are returned
      And their OrderLines are complete
      And child row counts do not change which Orders are selected

    Scenario: Parent filtering through relationships is explicit
      When I query Orders having at least one successful Payment
      Then only Orders satisfying that relationship predicate are returned
      And their Payments are read only by a query of their own

    Scenario: Filtered relationship queries are reusable
      Given a reusable query for successful Payments ordered by creation time
      When I use it for the Payments of a page of Orders
      Then the filter and ordering are applied in the database
      And the application groups the Payments by their Order

    Scenario: Provider parameter limits do not cause per-parent loading
      Given a relationship load contains more parent keys than one command permits
      When I execute the load
      Then keys are divided into supported batches
      And the assembled result is equivalent to an unrestricted batch load

  Rule: Changes are explicit and the database's refusals are inspectable

    # One style: validation is the domain's (its types' constructors), and
    # Plasm validates no input. "Invalid changes can be inspected without
    # execution" and "External input cannot change protected fields" were
    # removed with Changeset.validate* and Changeset.cast.

    Scenario: Creating a change preserves its input
      Given an existing Order
      When I make a change with a different status and write it
      Then the stored Order has the proposed status
      And the original Order retains its original status

    Scenario: Unspecified and explicitly cleared values differ
      Given an Order has a CancellationReason
      When a change does not mention CancellationReason
      Then the stored value is preserved
      When a change explicitly clears CancellationReason
      Then the stored value becomes absent

    Scenario: Invalid changes are not persisted
      Given a change the database refuses
      When I submit it for persistence
      Then the operation returns the field's errors
      And nothing is written

    Scenario: Database constraints produce structured errors
      Given a new Customer whose email the model states is unique
      When a concurrent operation causes that constraint to be violated
      Then the write returns the corresponding structured constraint error
      And unrelated database failures are not misreported as validation errors

    Scenario: Generated values are returned immutably
      Given an insert whose identifier is generated by the database
      When the insert succeeds
      Then the returned value contains the generated identifier
      And the submitted value remains unchanged

  Rule: Writing an aggregate writes what it owns

    # One style: what an aggregate owns is written with it, by difference from
    # the record it was read as. "Removing related records requires a
    # configured policy" became "Removing an owned record deletes it":
    # ownership is the policy.

    Scenario: Omitting an association does not modify it
      Given an Order with existing OrderLines
      When I update only the Order status
      Then existing OrderLines are preserved
      And no command is sent for its OrderLines

    Scenario: Updating an association matches existing identities
      Given an Order with an existing OrderLine
      When I change that OrderLine in the Order I read
      Then the existing row is updated
      And a duplicate row is not inserted

    Scenario: Removing an owned record deletes it
      Given an Order with two OrderLines
      When I remove one of them from the Order I read
      Then that OrderLine is deleted
      And the other is kept

    Scenario: Parent and association writes are atomic
      Given a valid Order change and an invalid OrderLine change
      When I submit them as one association write
      Then neither change is committed

  Rule: Transactions and concurrency are explicit

    Scenario: Transaction steps compose
      Given a transaction description with dependent operations
      When a later operation needs an earlier result
      Then it receives that result within the same transaction

    Scenario: A failed transaction identifies its failed step
      Given a transaction with multiple write steps
      When one step fails
      Then all writes in that transaction are rolled back
      And the returned error identifies where the failed step is written
      And no earlier step is reported as committed

    Scenario: Stale updates do not overwrite newer state
      Given an Order was read with a concurrency version
      And another operation has changed that version
      When I submit an update expecting the original version
      Then the operation returns a concurrency conflict
      And the newer state is preserved

    Scenario: Consistent multi-query reads can be requested
      Given a relationship graph is loaded through multiple queries
      When I request a consistent snapshot
      Then the queries execute within a provider-supported consistency mechanism
      * Or the request is rejected as unsupported
      And the guarantee is not silently weakened

    # A pooler in transaction mode, such as PgBouncer or RDS Proxy, may pin a
    # client to one server connection when it sees a SET. A snapshot needs
    # none: REPEATABLE READ alone gives it, and Plasm sends only SELECTs in it.
    Scenario: A snapshot read sends no SET, so a pooler need not pin its connection
      Given a relationship graph is loaded through multiple queries
      When I read it as a consistent snapshot, watching what Npgsql sends
      Then the read begins one transaction at REPEATABLE READ
      And no command Npgsql sends is a SET

    Scenario: Non-transactional effects are not presented as rollback-safe
      Given a transaction plan includes sending an external message
      When that effect cannot participate in the database transaction
      Then the API does not represent it as atomically committed with database writes
      And it requires an explicit post-commit or durable-outbox strategy

  Rule: Execution is observable and failures remain meaningful

    Scenario: Query execution exposes diagnostic information
      When I execute a query with relationship loads
      Then diagnostics identify the database commands and their durations
      And sensitive parameter values are omitted unless explicitly enabled

    Scenario: Cancellation reaches database execution
      Given an executing database operation
      When its cancellation token is cancelled
      Then cancellation is propagated to the provider
      And the operation does not return a successful result

    Scenario: Infrastructure failures remain distinct from domain errors
      When the database connection fails
      Then the failure is distinguishable from validation and concurrency errors
      And application code can handle it without parsing error messages
