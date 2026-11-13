# Acceptance specification: classification and traceability

The supplied Gherkin specifications are the acceptance contract. `features/joins.feature`, and then `aggregates.feature`, `bulk-writes.feature`, `raw-sql.feature` and `prepared-queries.feature` (batch 2a) and `many-to-many.feature`, `schema-check.feature` and `sandbox.feature` (batch 2b), were added afterwards, each written and run red before its implementation. `fsi.feature` (F# Interactive) followed the same way; its steps run each scenario in a real F# Interactive session hosted by the test. After an architecture audit, `query-semantics.feature`, `tx-steps.feature`, `query-pipeline.feature`, `error-model.feature`, `has-one.feature` and `providers.feature` were added, with new rules in `prepared-queries.feature`, `sandbox.feature` and `fsi.feature`, each run red first. A second audit added `reading.feature` and new scenarios in `query-semantics`, `query-pipeline`, `fsi`, `prepared-queries`, `tx-steps`, `error-model` and `providers`. The supplied ones are kept verbatim in `docs/specs/original/`, and the executable copies in `features/` differ only
where Gherkin validity requires it (see each file's header). Every scenario runs
as a test through Reqnroll: `tests/Plasm.Specs` (the C# host that generates the tests) binds to F# step
definitions in `tests/Plasm.Specs.Steps`, against a real PostgreSQL 18 database
(Testcontainers, with a freshly cloned database per scenario).

```sh
dotnet test tests/Plasm.Specs          # 229 scenarios (outline examples counted separately)
dotnet test tests/Plasm.Tests          # 176 unit, property, compile-time and Ecto-compatibility tests
```

## Summary

| Classification | Scenarios |
|---|---:|
| Implemented and verified | 228 |
| Implemented, deliberately changed (with rationale) | 1 |
| Planned | 0 |
| Unsupported, with explicit behaviour | 0 |
| **Total** | **229** |

Behaviour that is outside these scenarios and not built yet (migrations,
other providers and so on) is listed in `docs/status.md`. None of it has an API, so
nothing pretends to work.

Each scenario name below is also the generated test name, so a failing test points
straight back here. "Related unit tests" lists the tests in `tests/Plasm.Tests` that cover
the same guarantee more exhaustively.

### core.feature: Persistence descriptions are values and execution is explicit

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Constructing a query performs no database operations | Implemented and verified | `ValueSteps.fs` | Properties.fs (query extension never changes the original) |  |
| Executing a query does not mutate its description | Implemented and verified | `ValueSteps.fs` | Properties.fs (query extension never changes the original) |  |
| Extending a query preserves the original query | Implemented and verified | `ValueSteps.fs` | Properties.fs (query extension never changes the original) |  |
| Constructing a change performs no database operations | Implemented and verified | `ValueSteps.fs` | Properties.fs (query extension never changes the original) |  |

### core.feature: The public API preserves useful F# type information

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Querying a mapped property uses its declared type | Implemented and verified | `TypeSteps.fs` | CompileTime.fs; Translation.fs (Model.build errors) | Compile-time checks with FSharp.Compiler.Service against the real assemblies. Note that `o.Total = 100` legitimately compiles, because F# implicitly converts int to decimal losslessly. |
| Relationship loads are checked against the source type | Implemented and verified | `TypeSteps.fs` | CompileTime.fs; Translation.fs (Model.build errors) |  |
| Nullable values remain explicit | Implemented and verified | `TypeSteps.fs` | CompileTime.fs; Translation.fs (Model.build errors) |  |
| Unsupported model representations fail during configuration | Implemented and verified | `TypeSteps.fs` | CompileTime.fs; Translation.fs (Model.build errors) |  |

### core.feature: Queries compose without silently changing execution location

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Reusable filters compose | Implemented and verified | `CompositionSteps.fs` | Translation.fs (unsupported expressions, parameters); Properties.fs |  |
| Captured values are treated as data | Implemented and verified | `CompositionSteps.fs` | Translation.fs (unsupported expressions, parameters); Properties.fs |  |
| Unsupported translation is explicit | Implemented and verified | `CompositionSteps.fs` | Translation.fs (unsupported expressions, parameters); Properties.fs |  |
| Client-side processing is an explicit boundary | Implemented and verified | `CompositionSteps.fs` | Translation.fs (unsupported expressions, parameters); Properties.fs |  |
| Projection retrieves the requested data | Implemented and verified | `CompositionSteps.fs` | Translation.fs (unsupported expressions, parameters); Properties.fs |  |

### core.feature: Relationship loading is explicit, composable, and batched

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Unloaded and empty relationships are distinguishable | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Reading a relationship never triggers hidden database access | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Sibling collections load without a cartesian product | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Relationship loading batches across parents | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Nested loads compose | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Pagination applies to parents before collection expansion | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| A child filter does not implicitly filter parents | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Parent filtering through relationships is explicit | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Filtered relationship queries are reusable | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Per-parent limits preserve their stated meaning | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Unsupported loading semantics are rejected | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Provider parameter limits do not cause per-parent loading | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |
| Conflicting relationship customizations are explicit | Implemented, deliberately changed | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) | Combination is total; the conflict is reported as `LoadConflict` by `Explain` or execution, before any command. `replacePreload` is the explicit resolution. |
| Loading onto existing values preserves the originals | Implemented and verified | `LoadingSteps.fs` | Translation.fs (load planning); Properties.fs (batch-size equivalence, child ordering); EctoCompat.fs (preload cases) |  |

### core.feature: Changes are explicit and validation is inspectable

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Creating a changeset preserves its input | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |
| Unspecified and explicitly cleared values differ | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |
| Invalid changes can be inspected without execution | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |
| Invalid changes are not persisted | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |
| External input cannot change protected fields | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |
| Database constraints produce structured errors | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |
| Generated values are returned immutably | Implemented and verified | `ChangesetSteps.fs` | EctoCompat.fs (changeset cases); Properties.fs (edit diff) |  |

### core.feature: Association writes have explicit lifecycle semantics

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Omitting an association does not modify it | Implemented and verified | `AssociationWriteSteps.fs` | Behaviour.fs (child writes) |  |
| Updating an association matches existing identities | Implemented and verified | `AssociationWriteSteps.fs` | Behaviour.fs (child writes) |  |
| Removing related records requires a configured policy | Implemented and verified | `AssociationWriteSteps.fs` | Behaviour.fs (child writes) |  |
| Parent and association writes are atomic | Implemented and verified | `AssociationWriteSteps.fs` | Behaviour.fs (child writes) |  |

### core.feature: Transactions and concurrency are explicit

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Named transaction steps compose | Implemented and verified | `TransactionSteps.fs` | Behaviour.fs (snapshot, Tx naming); EctoCompat.fs (Multi, optimistic locking) |  |
| A failed transaction identifies its failed step | Implemented and verified | `TransactionSteps.fs` | Behaviour.fs (snapshot, Tx naming); EctoCompat.fs (Multi, optimistic locking) |  |
| Stale updates do not overwrite newer state | Implemented and verified | `TransactionSteps.fs` | Behaviour.fs (snapshot, Tx naming); EctoCompat.fs (Multi, optimistic locking) |  |
| Consistent multi-query reads can be requested | Implemented and verified | `TransactionSteps.fs` | Behaviour.fs (snapshot, Tx naming); EctoCompat.fs (Multi, optimistic locking) | A real concurrent commit is injected between the parent and child commands; the snapshot read does not see it and the independent read does. A provider without the capability rejects the request with no commands. |
| Non-transactional effects are not presented as rollback-safe | Implemented and verified (by design) | `TransactionSteps.fs` | Behaviour.fs (snapshot, Tx naming); EctoCompat.fs (Multi, optimistic locking) | `Tx` can only describe database steps; `Tx.run` is the explicit escape hatch. The step demonstrates a durable outbox rolled back with the transaction, and delivery only after `Ok`. |

### core.feature: Execution is observable and failures remain meaningful

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Query execution exposes diagnostic information | Implemented and verified | `ObservabilitySteps.fs` | Behaviour.fs (cancellation, connection failure) |  |
| Cancellation reaches database execution | Implemented and verified | `ObservabilitySteps.fs` | Behaviour.fs (cancellation, connection failure) |  |
| Infrastructure failures remain distinct from domain errors | Implemented and verified | `ObservabilitySteps.fs` | Behaviour.fs (cancellation, connection failure) |  |

### elegance.feature: Ordinary F# domain types remain usable

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Persistence metadata does not require mutable records | Implemented and verified | `EleganceSteps.fs` | CompileTime.fs; Behaviour.fs (materialization) |  |
| Domain scalar wrappers preserve their meaning | Implemented and verified | `EleganceSteps.fs` | CompileTime.fs; Behaviour.fs (materialization) |  |
| Domain reconstruction can fail explicitly | Implemented and verified | `EleganceSteps.fs` | CompileTime.fs; Behaviour.fs (materialization) |  |

### elegance.feature: Query composition preserves relational meaning

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Relationship filtering does not duplicate parents | Implemented and verified | `EleganceSteps.fs` | Translation.fs (EXISTS, IS NULL, empty membership, ordering) |  |
| Optional relationships preserve parent rows | Implemented and verified | `EleganceSteps.fs` | Translation.fs (EXISTS, IS NULL, empty membership, ordering) |  |
| Null comparisons have explicit semantics | Implemented and verified | `EleganceSteps.fs` | Translation.fs (EXISTS, IS NULL, empty membership, ordering) |  |
| Empty membership predicates remain valid | Implemented and verified | `EleganceSteps.fs` | Translation.fs (EXISTS, IS NULL, empty membership, ordering) |  |
| Pagination requires stable ordering | Implemented and verified | `EleganceSteps.fs` | Translation.fs (EXISTS, IS NULL, empty membership, ordering) | Without an ordering the query is rejected. With one, the primary key is appended as a documented tiebreaker. |

### elegance.feature: Composition has consistent algebraic behavior

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Independently defined filters preserve their meaning | Implemented and verified | `EleganceSteps.fs` | Properties.fs (predicate order) |  |
| Repeating an identical load does not duplicate work | Implemented and verified | `EleganceSteps.fs` | Properties.fs (predicate order) |  |
| Empty transaction descriptions are valid | Implemented and verified | `EleganceSteps.fs` | Properties.fs (predicate order) |  |

### elegance.feature: Partial data cannot accidentally become destructive writes

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Projected values do not imply complete entity state | Implemented and verified | `EleganceSteps.fs` | Behaviour.fs (forKey, scoped replacement, duplicates) |  |
| Filtered collections are not authoritative replacements | Implemented and verified | `EleganceSteps.fs` | Behaviour.fs (forKey, scoped replacement, duplicates) |  |
| Duplicate child identities are rejected | Implemented and verified | `EleganceSteps.fs` | Behaviour.fs (forKey, scoped replacement, duplicates) |  |

### elegance.feature: Efficiency guarantees are testable

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Loading plans can be inspected | Implemented and verified | `EleganceSteps.fs` | Properties.fs (command counts); docs/benchmarks.md |  |
| Larger parent sets do not cause N+1 behavior | Implemented and verified | `EleganceSteps.fs` | Properties.fs (command counts); docs/benchmarks.md |  |
| Database work remains bounded by the requested page | Implemented and verified | `EleganceSteps.fs` | Properties.fs (command counts); docs/benchmarks.md |  |

### elegance.feature: Transaction retries do not conceal repeated execution

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Retry behavior is explicit | Implemented and verified | `EleganceSteps.fs` |  |  |
| Retried transactions use a fresh attempt context | Implemented and verified | `EleganceSteps.fs` |  |  |

### joins.feature: Joins are explicit, typed, and run in the database

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| An inner join returns matching pairs | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| A left join represents a missing match as None | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| Join keys are checked at compile time | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) | Rejections pin FS0041 (the `join` operation is overloaded) and name both key types; a control snippet compiles. |
| Filters on joined columns run in the database | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| Left-joined rows are filtered with option semantics | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |

### joins.feature: Joins preserve relational meaning

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A join returns one row per matching pair | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| Pagination over joined rows is stable | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) | The key of every joined source is appended as a tiebreaker. |
| Several joins compose in one query | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| Projections over joins retrieve only the requested columns | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |

### joins.feature: Joined queries are values

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Joined queries compose through ordinary functions | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| Joined queries can be inspected without database access | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |
| Unsupported join requests are explicit | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) | `preload` on a joined query is a planning error pointing to `repo.Preload`. A `join` cannot be the last clause of the computation expression (F# rule FS3098). |
| Joined queries run inside transactions | Implemented and verified | `JoinSteps.fs` | Joins.fs (SQL shape, option semantics, misuse errors) |  |

### aggregates.feature: Counting and existence never transfer rows

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Counting a filtered query | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) |  |
| Counting a page counts the page | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) | A page or a grouping is counted through a subquery. |
| Testing existence | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) |  |

### aggregates.feature: Aggregates are typed values with F# meaning

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Aggregating a filtered query | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) |  |
| Aggregates over no rows are explicit | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) | `Agg.sum` is `COALESCE(SUM(x), 0)` (as `List.sum []`); `min`, `max`, `avg` return options. |
| Aggregates are checked at compile time | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) | `Agg.sum` needs `+` and `Zero`, `Agg.avg` needs `DivideByInt` (as `List.average`); rejections pin FS0001, with a control snippet. |

### aggregates.feature: Grouping summarises in the database

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Grouping Orders by Customer | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) |  |
| Filtering groups | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) |  |
| Grouping across a join | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) |  |
| Grouping mistakes are explicit | Implemented and verified | `AggregateSteps.fs` | Batch2.fs (aggregates, grouping errors, count/exists over joins, pages and groups) | Checked when the query is planned (no database access), not at compile time. |

### bulk-writes.feature: Updates and deletes are driven by typed queries

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Updating every row a query matches | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |
| Bulk updates can compute from the current row | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |
| Bulk updates respect optimistic concurrency | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) | Bulk updates increment the version column. |
| Deleting every row a query matches | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |
| Bulk writes are explicit about what they bypass | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) | No changeset validation runs; the statement is atomic, so a constraint failure changes no row. |
| Unsupported bulk requests are rejected before execution | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |

### bulk-writes.feature: Inserts and upserts take many records at once

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Inserting many records in one command | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) | Inserts over 65,000 parameters are split into several commands inside one transaction. |
| Upserting on a unique key | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |
| Inserting only what is missing | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |

### bulk-writes.feature: Bulk writes are values

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Bulk writes can be inspected without database access | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |
| Bulk writes run inside transactions | Implemented and verified | `BulkSteps.fs` | Batch2.fs (bulk misuse errors, FK failures, chunked inserts, composite conflict targets) |  |

### raw-sql.feature: Hand-written queries return typed values

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Reading mapped records from SQL | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |
| Reading tuples and scalars from SQL | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |
| Reading unmapped records from SQL | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |
| Interpolated values are always parameters | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) | `repo.Sql` takes a `FormattableString`; a plain string does not type-check. |
| Mismatched results fail with the column identified | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |

### raw-sql.feature: SQL fragments extend typed queries

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A fragment inside a typed query | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |
| Fragment SQL must be literal | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) | Revised before implementation: the scenario first asked for a planning-time rejection. Quotations cannot tell a literal from a captured string, so the SQL is typed as an F# format string and a runtime string fails to compile (FS0001). Literal `%` is written `%%`. |

### raw-sql.feature: Hand-written commands and transactions

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Executing hand-written commands | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |
| Hand-written SQL inside a transaction | Implemented and verified | `RawSqlSteps.fs` | Batch2.fs (list, option and domain parameters, tuple arity, fragment escaping) |  |

### prepared-queries.feature: Prepared queries are built once and bound per call

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A prepared query is built once and runs with different arguments | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) |  |
| Arguments are typed and always parameters | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) | Optional arguments compare with `IS NOT DISTINCT FROM`, lists with `= ANY(@p)`, so one SQL text serves every value. |
| Paging arguments | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) |  |
| Prepared queries keep relationship loads | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) |  |
| Prepared queries can be inspected and counted | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) |  |

### prepared-queries.feature: Argument misuse is explicit

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Arguments are only read inside query operations | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) |  |
| Arguments cannot choose the ordering | Implemented and verified | `PreparedSteps.fs` | Batch2.fs (prepared patterns, three arguments, transactions, negative counts, argument expressions) | Rejected when the query is prepared. |

### many-to-many.feature: Many-to-many relationships load like other relationships

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Preloading a many-to-many relationship | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) | One command per batch of owner keys, joined through the join table; each distinct target row is materialised once and shared. |
| An Article without Tags has an empty, loaded relationship | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) |  |
| Loaded Tags can be filtered, ordered and limited per Article | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) |  |
| Filtering through a many-to-many relationship | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) |  |

### many-to-many.feature: Changing links writes only the join table

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Replacing an Article's Tags | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) |  |
| Adding and removing individual links | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) | Following "unknown is unloaded": the relationship comes back NotLoaded because the full set was not read. |
| Inserting an Article with its Tags | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) |  |
| Linking to a record that does not exist changes nothing | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) | Link writes run in the write's transaction; links never create target records. |
| Link writes need a many-to-many relationship | Implemented and verified | `ManyToManySteps.fs` | Batch2.fs (configuration error, duplicate targets, clearing links, Tx link writes, relationship-only updates) |  |

### schema-check.feature: The check reports what would fail, and nothing else

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A model that matches the database passes | Implemented and verified | `SchemaCheckSteps.fs` | Batch2.fs (join-table column type mismatch) | Reads `information_schema.columns` for the current schema only. |
| Missing tables and columns are reported | Implemented and verified | `SchemaCheckSteps.fs` | Batch2.fs (join-table column type mismatch) |  |
| Incompatible column types are reported | Implemented and verified | `SchemaCheckSteps.fs` | Batch2.fs (join-table column type mismatch) | Compatible types come from the dialect (`ISqlDialect.ColumnTypes`). |
| A required field over a nullable column is reported | Implemented and verified | `SchemaCheckSteps.fs` | Batch2.fs (join-table column type mismatch) |  |
| Required columns the model cannot write are reported | Implemented and verified | `SchemaCheckSteps.fs` | Batch2.fs (join-table column type mismatch) |  |
| Join tables are checked | Implemented and verified | `SchemaCheckSteps.fs` | Batch2.fs (join-table column type mismatch) |  |

### sandbox.feature: Everything inside a sandbox is rolled back

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Changes inside a sandbox are visible inside and gone afterwards | Implemented and verified | `SandboxSteps.fs` | Batch2.fs (failed transaction step inside a sandbox) |  |
| Transactions inside a sandbox keep their meaning | Implemented and verified | `SandboxSteps.fs` | Batch2.fs (failed transaction step inside a sandbox) | Transactions inside the sandbox become savepoints; their isolation level is not applied. |
| A database error does not break the sandbox | Implemented and verified | `SandboxSteps.fs` | Batch2.fs (failed transaction step inside a sandbox) | Every operation runs in its own savepoint and rolls back to it after an error. |
| Concurrent sandboxes are isolated | Implemented and verified | `SandboxSteps.fs` | Batch2.fs (failed transaction step inside a sandbox) |  |
| Overlapping work in one sandbox is serialised | Implemented and verified | `SandboxSteps.fs` | Batch2.fs (failed transaction step inside a sandbox) | One connection: operations run one at a time. |

### fsi.feature: Referencing Plasm is the only setup

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Referencing the packages is enough for Plasm values to display | Implemented and verified | `FsiSteps.fs` |  | Displays use `StructuredFormatDisplay`, so no printer registration is needed. The session references the built assemblies with `#r`, standing in for `#r "nuget: Plasm.Postgres"`. |
| Referencing Plasm twice is harmless | Implemented and verified | `FsiSteps.fs` |  |  |

### fsi.feature: Values display what they mean

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A query displays its SQL and parameters | Implemented and verified | `FsiSteps.fs` |  | Translated by the most recently created live repository that maps the type; no database access. |
| A query with loads displays one command per load | Implemented and verified | `FsiSteps.fs` |  |  |
| A query that cannot be translated displays why | Implemented and verified | `FsiSteps.fs` |  |  |
| Relationships that were not loaded display as not loaded | Implemented and verified | `FsiSteps.fs` |  | `NotLoaded` is the union case FSI already prints. |
| A prepared query displays its bound arguments | Implemented and verified | `FsiSteps.fs` |  | Shows each bound value with its argument position. |
| Displaying a query leaves how it runs unchanged | Implemented and verified | `FsiSteps.fs` |  | Displays plan without the repository's plan cache. |
| Formatting a query in compiled code hides its parameter values | Implemented and verified | `FsiSteps.fs` |  | Values appear only with `IncludeParameterValues` or F# Interactive types; `CompiledDisplaySteps` in `FsiSteps.fs`. |
| Formatting a query shows values when the repository includes them | Implemented and verified | `FsiSteps.fs` |  |  |

### fsi.feature: Results display without awaiting tasks

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A read displays its rows | Implemented and verified | `FsiSteps.fs` |  | `Plasm.Interactive.wait` blocks on the task. |
| A failed write displays the classified error | Implemented and verified | `FsiSteps.fs` |  |  |

### fsi.feature: Redefining types in a session is explained

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A query built before a type was redefined is rejected with an explanation | Implemented and verified | `FsiSteps.fs` |  | New `TranslationError.RedefinedType`; FSI types are recognised by their `FSI_nnnn` submission module. |
| Queries re-evaluated after a redefinition work | Implemented and verified | `FsiSteps.fs` |  |  |

### fsi.feature: A playground keeps experiments across submissions and undoes them

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Changes in a playground are visible in later submissions | Implemented and verified | `FsiSteps.fs` |  | `repo.Playground()` keeps one connection and transaction open across submissions; built on the sandbox provider. |
| Resetting a playground discards its changes and keeps it open | Implemented and verified | `FsiSteps.fs` |  |  |
| Closing a playground leaves the database unchanged | Implemented and verified | `FsiSteps.fs` |  |  |
| A database error does not end the playground | Implemented and verified | `FsiSteps.fs` |  |  |
| A sandbox inside a playground rolls back to the playground | Implemented and verified | `FsiSteps.fs` |  |  |
| A playground whose connection was lost says so and starts afresh | Implemented and verified | `FsiSteps.fs` |  | Classified as a connection failure whose message says the changes were discarded. |

### fsi.feature: Records can be generated from a table

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Scaffolding a table prints a record and schema that match the database | Implemented and verified | `FsiSteps.fs` |  | `repo.Scaffold table` reads `information_schema` only. |
| Scaffolding maps nullable columns, keys and unknown types explicitly | Implemented and verified | `FsiSteps.fs` |  |  |
| Names that are not F# identifiers are escaped | Implemented and verified | `FsiSteps.fs` |  | Double backticks for keywords and names that are not identifiers; `column` for names that do not round-trip. |
| A table with no mappable columns is reported | Implemented and verified | `FsiSteps.fs` |  | New `ScaffoldError` (`TableNotFound`, `NoMappableColumns`, `Failed`). |
| A missing table is reported as missing | Implemented and verified | `FsiSteps.fs` |  |  |

### error-model.feature: Expected failures are returned, not thrown

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Using a sandbox's repository after the sandbox ended | Implemented and verified | `ErrorModelSteps.fs` |  | New `DbFailureKind.RepositoryClosed`; the closed playground reports the same. |
| Using a closed playground | Implemented and verified | `ErrorModelSteps.fs` |  |  |
| A written row that cannot be read back | Implemented and verified | `ErrorModelSteps.fs` |  | New `WriteError.Materialization`. A single-row write without children is not rolled back; one in a transaction is. |

### error-model.feature: Error cases are always qualified

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Domain unions keep their own case names | Implemented and verified | `ErrorModelSteps.fs` |  | `DbError` and `TranslationError` are now `RequireQualifiedAccess` (breaking). |
| <union> cases must be qualified | Implemented and verified | `ErrorModelSteps.fs` |  | `QualifiedUnionSteps` in `ErrorModelSteps.fs`. |

### has-one.feature: Has-one relationships load like other relationships

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Preloading a has-one relationship | Implemented and verified | `HasOneSteps.fs` |  |  |
| Filtering through a has-one relationship | Implemented and verified | `HasOneSteps.fs` |  | New `Rel.exists` for optional relationships. |
| A has-one relationship with more than one related row is an error | Implemented and verified | `HasOneSteps.fs` |  |  |

### prepared-queries.feature: Prepared queries compose like any other query

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Extending a prepared query applies the extension everywhere | Implemented and verified | `PreparedSteps.fs` |  | Found by the architecture audit: extending a prepared query used to plan the template and drop the extension. |
| An extended prepared query still binds its arguments per call | Implemented and verified | `PreparedSteps.fs` |  |  |
| A prepared query used as a load query binds its own arguments | Implemented and verified | `PreparedSteps.fs` |  | Load nodes carry the bound child query's arguments. |

### providers.feature: Constructs the dialect can express are rendered by it

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Relationship loads without array parameters send one parameter per key | Implemented and verified | `ProviderSteps.fs` |  |  |
| Null-safe comparisons are rendered by the dialect | Implemented and verified | `ProviderSteps.fs` |  | New `ISqlDialect.NullSafeComparison` and `FunctionName` (breaking for dialect authors). |
| String functions are named by the dialect | Implemented and verified | `ProviderSteps.fs` |  |  |

### providers.feature: Constructs the dialect cannot express are refused before execution

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| <operation> needs <capability> | Implemented and verified | `ProviderSteps.fs` |  | New capabilities `Upserts` and `RowLocking`; `Returning` is now checked. Every call reports it as `TranslationError.UnsupportedByProvider`, naming every missing capability. |

### query-pipeline.feature: Pipeline queries equal their computation-expression forms

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| <operation> in a pipeline produces the same SQL | Implemented and verified | `PipelineSteps.fs` |  | The `Query` module now has every operation, including joins, grouping and child-query preloads. |

### query-semantics.feature: Negation follows F# meaning, including for absent values

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Negating an optional equality keeps rows whose value is absent | Implemented and verified | `QuerySemanticsSteps.fs` |  | `not` renders `(...) IS NOT TRUE`, so an unknown comparison negates to true, as in F#. `NOT EXISTS` is kept for relationship predicates. |
| not (a = b) and a <> b return the same rows | Implemented and verified | `QuerySemanticsSteps.fs` |  |  |
| Negating a compound predicate over absent values | Implemented and verified | `QuerySemanticsSteps.fs` |  |  |
| Negating a comparison that cannot be absent stays a plain NOT | Implemented and verified | `QuerySemanticsSteps.fs` |  | `IS NOT TRUE` only where an operand can be NULL. |

### query-semantics.feature: Captured values are read each time the query runs

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A query sees the current contents of a captured <container> | Implemented and verified | `QuerySemanticsSteps.fs` |  | Captured arrays, collections and reference cells mark the plan volatile; only immutable captured values (primitives, strings, F# lists, options, records without mutable fields) let a plan be cached. |
| A query capturing only immutable values is planned once | Implemented and verified | `QuerySemanticsSteps.fs` |  |  |
| A query comparing with the current time reads the clock each run | Implemented and verified | `QuerySemanticsSteps.fs` |  | Found by the second audit: static properties such as `DateTime.UtcNow` were treated as stable. |
| A query sees the current contents of a module-level <container> | Implemented and verified | `QuerySemanticsSteps.fs` |  | Only F# module values whose current value is immutable allow plan reuse. |

### sandbox.feature: Sandboxes nest

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A sandbox inside a sandbox rolls back to the outer sandbox | Implemented and verified | `SandboxSteps.fs` |  | Sandbox transactions pass named savepoints through. |

### tx-steps.feature: Write steps match their changesets

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A step whose changeset is for a different action is rejected | Implemented and verified | `TxStepSteps.fs` |  | Checked when the step is built (`ArgumentException`), like `repo.Insert/Update/Delete`. |
| The repository rejects a mismatched changeset the same way | Implemented and verified | `TxStepSteps.fs` |  |  |

### tx-steps.feature: Custom steps work inside the transaction without controlling it

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A custom step's commands run in the transaction | Implemented and verified | `TxStepSteps.fs` |  |  |
| A custom step cannot commit or roll back the transaction | Implemented and verified | `TxStepSteps.fs` |  | `TxContext` now offers `CreateCommand()` (enlisted in the transaction) and `CancellationToken`; the connection and transaction are no longer exposed (breaking). |
| A custom step cannot end the transaction with SQL | Implemented and verified | `TxStepSteps.fs` |  | `TxContext.CreateCommand()` returns a command that refuses transaction-control statements. |

### error-model.feature: Transaction failures are typed

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| A step's rejection reason comes back with its type | Implemented and verified | `ErrorModelSteps.fs` |  | `StepFailure.Rejected of Rejection`, read with `As<'R>()`. |
| A step's invalid changes come back as the changeset's errors | Implemented and verified | `ErrorModelSteps.fs` |  |  |

### providers.feature: An unsupported operation is reported the same way by every call

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| <call> reports an unsupported operation as UnsupportedByProvider | Implemented and verified | `ProviderSteps.fs` |  | `WriteError.Translation`, `DbError.Translation`, `StepFailure.Failed(DbError.Translation ...)`. |

### query-pipeline.feature: Pipelines chain like expressions

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| Chaining joins in a pipeline | Implemented and verified | `PipelineSteps.fs` |  | Pipeline joins nest (`((o, l), p)`); the translator follows the lambda's tuple shape. |

### reading.feature: One respects the query and reports too many rows as data

| Scenario | Classification | Step definitions | Related unit tests | Notes |
|---|---|---|---|---|
| One keeps the query's own ordering and limit | Implemented and verified | `ReadingSteps.fs` |  | Found by the second audit: `One` replaced the query's limit. |
| A query matching no rows returns None | Implemented and verified | `ReadingSteps.fs` |  |  |
| More than one matching row is reported as a property of the data | Implemented and verified | `ReadingSteps.fs` |  | New `DbError.MoreThanOneRow`. |
| Reading One of the same query twice plans it once | Implemented and verified | `ReadingSteps.fs` |  |  |
