# Ecto behavioral compatibility

`tests/Plasm.Tests/EctoCompat.fs` adapts Ecto test cases (Apache-2.0, see `NOTICE`).
Outcomes and failure semantics are compared, not SQL text. Each test is named
`ecto: <original test name>` or `ecto (deliberate difference): ...` and cites its source.

| Ecto source | Case | Outcome |
|---|---|---|
| preload.exs | preload has_many | Same: children assembled per parent, input order kept, parent without children gets `Loaded []`, unloaded is `NotLoaded` |
| preload.exs | preload has_many multiple times | Same: duplicates in the input and re-preloading give no duplicated children |
| preload.exs | preload belongs_to | Same |
| preload.exs | preload belongs_to with shared parent | Same; one batched query for the shared key |
| preload.exs | preload empty | Same: no command issued |
| preload.exs | preload has_many with no associated entries | Same (`Loaded []`) |
| preload.exs | preload belongs_to with no associated entry / has_one with no associated entries | Same: `Loaded None`, and no command when there are no keys |
| preload.exs | preload has_many (optional foreign key) | Same |
| preload.exs | preload with query | Same: filter and order applied in SQL |
| preload.exs | preload nested | Same |
| preload.exs | preload nested via custom query | Same |
| preload.exs | preload many assocs | Same |
| preload.exs | preload skips already loaded (cardinality one/many) | **Deliberate difference**: `Repo.Preload` always reloads requested relationships. An explicit load request is authoritative, so results never silently mix stale and fresh data. |
| query.ex docs | limit in a preload query | **Deliberate difference**: Ecto applies it globally across all parents (a documented trap). Plasm rejects `take`/`skip` in a load query as ambiguous before any command, and offers `limitPerParent` (window function) instead. |
| preload.exs | through / many_to_many / embeds / prefixes / functions | Not applicable: not implemented |
| repo.exs | insert, update and delete | Same |
| repo.exs | insert and update with empty changeset | Same: an empty update is a no-op that issues no command and never reports stale |
| repo.exs | insert and update with changeset read after writes | Same: generated id returned in a new value, submitted value unchanged |
| repo.exs | optimistic locking in update/delete operations | Same outcome (`Stale` instead of `StaleEntryError`). Versioning is declared on the schema rather than per changeset. |
| repo.exs | unique constraint | Deliberately different (strict design, step 4): Ecto needs `unique_constraint` on each changeset. Plasm maps a constraint with PostgreSQL's default name (`customers_email_key`) to its field with nothing declared, "has already been taken"; any other name stays a database failure (`Failed` with `UniqueViolation`) |
| repo.exs | foreign key constraint | Same pattern (`<table>_<column>_fkey`, "does not exist") |
| repo.exs | invalid changeset is not written | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | cast/4: with valid string keys | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | cast/4 permitted fields | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | cast/4: with empty values | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | cast type error | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | change/2 with a struct | Same: values equal to the data are not changes (via `Changeset.edit`) |
| changeset_test.exs | put_change/3 and delete_change/2 | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | fetch_change/2 | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | validate_required/3 | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | validate_length/3 with string | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | validate_number/3 | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | validate_inclusion/3 | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | validate_format/4 | Removed (one style): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | validations only check changes on update | Removed (one style): Plasm validates no input; the domain's constructors do |
| multi_test.exs | new (empty multi) | Same: succeeds; Plasm issues no command at all |
| multi_test.exs | insert/update changeset, insert fun | Same; dependencies are typed `let!` bindings instead of a changes map |
| multi_test.exs | error | Same: failure identifies the step; earlier steps rolled back and listed as `RolledBack` |
| multi_test.exs | invalid changeset in a multi | Same: failed step plus its errors and changeset |
| multi_test.exs | run with fun | Same (`Tx.run` with the transaction's connection) |
| multi_test.exs | all queryable | Same (`Tx.all`) |
| query semantics | comparison with nil | **Deliberate difference**: Ecto raises on `== nil` and requires `is_nil/1`. In Plasm `= None` is `IS NULL` and `<> Some x` includes NULL rows (`IS DISTINCT FROM`), matching F# equality. |
