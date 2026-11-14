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
    And an Order belongs to a Customer
    And an Order has many OrderLines
    And an Order has many Payments
    And an OrderLine belongs to a Product

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

    Scenario: Relationship loads are checked against the source type
      When I attempt to load a relationship not defined for Orders
      Then the load specification is rejected at compile time

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

  Rule: Relationship loading is explicit, composable, and batched

    Scenario: Unloaded and empty relationships are distinguishable
      Given an Order with no Payments
      When I read the Order without loading Payments
      Then Payments is represented as not loaded
      When I explicitly load Payments
      Then Payments is represented as loaded and empty

    Scenario: Reading a relationship never triggers hidden database access
      Given an Order with an unloaded Payments relationship
      When application code inspects that relationship
      Then no database command is executed
      And its unloaded state remains observable

    Scenario: Sibling collections load without a cartesian product
      Given an Order has 10 OrderLines
      And the Order has 10 Payments
      When I request both collections using separate-query loading
      Then the library loads both collections automatically
      And it does not transfer their 100-row cross product
      And each related record appears once in its collection

    Scenario: Relationship loading batches across parents
      Given a query returns 100 Orders
      When I request their OrderLines
      Then the library fetches OrderLines in batches across those Orders
      And it does not issue one OrderLine query per Order
      And it associates each OrderLine with its owning Order

    Scenario: Nested loads compose
      Given a reusable load specification for OrderLines and their Products
      When I combine it with a load specification for Payments
      Then the resulting query loads both branches
      And nested relationships are populated automatically

    Scenario: Pagination applies to parents before collection expansion
      Given Orders have a deterministic ordering
      When I request the first 20 Orders with OrderLines and Payments
      Then exactly the first 20 matching Orders are returned
      And their requested collections are complete
      And child row counts do not change which Orders are selected

    Scenario: A child filter does not implicitly filter parents
      When I load Orders with only successful Payments
      Then all Orders matching the parent query are returned
      And each loaded Payments collection contains only successful Payments

    Scenario: Parent filtering through relationships is explicit
      When I query Orders having at least one successful Payment
      Then only Orders satisfying that relationship predicate are returned
      And loading Payments remains a separate explicit choice

    Scenario: Filtered relationship queries are reusable
      Given a reusable query for successful Payments ordered by creation time
      When I use it as the Payments load for Orders
      Then the filter and ordering are applied in the database
      And Payments are assembled into the corresponding Orders

    Scenario: Per-parent limits preserve their stated meaning
      When I request the latest 3 Payments for each Order
      Then each Order receives at most its own latest 3 Payments
      And the limit is not applied globally across all Orders

    Scenario: Unsupported loading semantics are rejected
      Given the provider cannot implement a requested per-parent operation
      When I execute the load
      Then the library reports the unsupported operation
      And it does not substitute a global operation

    Scenario: Provider parameter limits do not cause per-parent loading
      Given a relationship load contains more parent keys than one command permits
      When I execute the load
      Then keys are divided into supported batches
      And the assembled result is equivalent to an unrestricted batch load

    @changed
    Scenario: Conflicting relationship customizations are explicit
      Changed: combining queries is total (it never throws). The conflict is reported
      as a structured LoadConflict when the combined query is planned (Explain) or
      executed, before any database command. Resolve it with replacePreload.
      Given two load specifications customize the same relationship differently
      When I combine those specifications
      Then the library reports a composition conflict
      And I must explicitly choose replacement or another supported resolution

    Scenario: Loading onto existing values preserves the originals
      Given immutable Orders already held by application code
      When I load their Payments
      Then new values containing the loaded relationships are returned
      And the original values remain unchanged

  Rule: Changes are explicit and validation is inspectable

    Scenario: Creating a changeset preserves its input
      Given an existing Order
      When I create a changeset with a different status
      Then the changeset exposes the proposed status
      And the original Order retains its original status

    Scenario: Unspecified and explicitly cleared values differ
      Given an Order has a CancellationReason
      When a changeset does not mention CancellationReason
      Then the stored value is preserved
      When a changeset explicitly clears CancellationReason
      Then the stored value becomes absent

    Scenario: Invalid changes can be inspected without execution
      When I construct a changeset violating a domain validation
      Then it contains structured validation errors
      And errors identify the affected fields
      And inspecting it performs no database operations

    Scenario: Invalid changes are not persisted
      Given an invalid changeset
      When I submit it for persistence
      Then the operation returns validation errors
      And no write command is executed

    Scenario: External input cannot change protected fields
      Given only specified fields are accepted from external input
      When external input includes an Order identifier or concurrency version
      Then those fields cannot be changed through that input path

    Scenario: Database constraints produce structured errors
      Given a named uniqueness constraint is associated with a changeset
      When a concurrent operation causes that constraint to be violated
      Then the write returns the corresponding structured constraint error
      And unrelated database failures are not misreported as validation errors

    Scenario: Generated values are returned immutably
      Given an insert whose identifier is generated by the database
      When the insert succeeds
      Then the returned value contains the generated identifier
      And the submitted value remains unchanged

  Rule: Association writes have explicit lifecycle semantics

    Scenario: Omitting an association does not modify it
      Given an Order with existing OrderLines
      When I update only the Order status
      Then existing OrderLines are preserved
      And an unloaded collection is not interpreted as empty

    Scenario: Updating an association matches existing identities
      Given an Order with an existing OrderLine
      When an association changeset modifies that OrderLine by its identity
      Then the existing row is updated
      And a duplicate row is not inserted

    Scenario: Removing related records requires a configured policy
      Given an association replacement omits an existing child
      When no removal policy has been configured
      Then the replacement is rejected
      And the child is not silently deleted or detached

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
