# Ecto behavioral compatibility

`tests/Plasm.Tests/EctoCompat.fs` adapts Ecto test cases (Apache-2.0, see `NOTICE`).
Outcomes and failure semantics are compared, not SQL text. Each test is named
`ecto: <original test name>` or `ecto (deliberate difference): ...` and cites its source.

Since 2026-11-14 Plasm has one style (`docs/status.md`). Ecto's changeset
validations and `cast` have no counterpart any more, and `preload` was removed
then, so the cases that tested them were removed; they stay in the table
below, marked removed, with the reason. A `let!` load (`loads.feature`) has
since taken a preload query's place, with `take` per record; the Ecto cases
have not been adapted to it. What remains (14 tests) is Ecto's repository and `Multi` behaviour,
expressed as typed transactions: every write is a `Tx.insert`, `Tx.update`
or `Tx.delete` step run by `repo.Transact`.

| Ecto source | Case | Outcome |
|---|---|---|
| preload.exs | preload has_many, multiple times, belongs_to, with shared parent, empty, with no associated entries, optional foreign key, with query, nested, nested via custom query, many assocs | Removed (one style, step a1; `preload` itself left in step a2). What a record owns is always loaded with it, one batched command per list (owned-collections.feature); another aggregate is reached by a join, `exists` or a `let!` load |
| preload.exs | preload skips already loaded (cardinality one/many) | Removed with `repo.Preload` (it was a deliberate difference: Plasm always reloaded what was asked for) |
| query.ex docs | limit in a preload query | Removed with `limitPerParent` (it was a deliberate difference: Plasm rejected `take` in a load query). Since then `take` inside a `let!` load applies per record (`loads.feature`); the case has not been adapted |
| preload.exs | through / many_to_many / embeds / prefixes / functions | Not adapted. Shared records through a join table are a linked list (`[<JoinTable>]`), always loaded; what an embed holds is a record with no table, stored in its owner's row, or a `[<Json>]` field (`value-records.feature`, `json-columns.feature`) |
| repo.exs | insert, update and delete | Same, as `Tx.insert`, `Tx.update` and `Tx.delete` steps |
| repo.exs | insert and update with empty changeset | Same: `Tx.update (order, order)` changes nothing, issues no command and is never a conflict |
| repo.exs | insert and update with changeset read after writes | Same: the generated id is returned in a new value; the submitted value is unchanged |
| repo.exs | optimistic locking in update/delete operations | Same outcome (`TxFailure.Conflict` instead of `StaleEntryError`). Versioning is declared on the record (`[<Version>]`); a record without a version field is checked by every column of the record read |
| repo.exs | unique constraint | Deliberately different (strict design, step 4): Ecto needs `unique_constraint` on each changeset. Plasm maps a constraint with PostgreSQL's default name (`customers_email_key`) to its field with nothing declared, "has already been taken", as `TxFailure.InvalidChanges`; any other name stays a database failure (`TxFailure.Failed` with `UniqueViolation`) |
| repo.exs | foreign key constraint | Same pattern (`<table>_<column>_fkey`, "does not exist") |
| repo.exs | invalid changeset is not written | Removed (one style, step b): Plasm validates no input; the domain's constructors do. A change the database refuses is covered by the `Multi` case below |
| changeset_test.exs | cast/4: with valid string keys; cast/4 permitted fields; cast/4: with empty values; cast type error | Removed (one style, step b): `cast` is gone; the application parses external input into domain values |
| changeset_test.exs | put_change/3 and delete_change/2; fetch_change/2 | Removed (one style, step b): `set`, `clear` and `tryGetChange` are gone; a change is a record built with `{ x with ... }` |
| changeset_test.exs | validate_required/3, validate_length/3 with string, validate_number/3, validate_inclusion/3, validate_format/4, validations only check changes on update | Removed (one style, step b): Plasm validates no input; the domain's constructors do |
| changeset_test.exs | change/2 with a struct | Same: values equal to the data are not changes. Tested on the internal changeset that `Tx.update` builds |
| multi_test.exs | new (empty multi) | Same: succeeds; Plasm issues no command at all |
| multi_test.exs | insert/update changeset, insert fun | Same; dependencies are typed `let!` bindings instead of a changes map |
| multi_test.exs | error | Same: the failure identifies the step (by file and line); earlier steps are rolled back and listed as `RolledBack` |
| multi_test.exs | invalid changeset in a multi | Same outcome: the step whose insert the database refuses (`orders_total_check`) fails with `TxFailure.InvalidChanges` and its field errors, and nothing is written |
| multi_test.exs | run with fun | Same (`Tx.run`, with commands enlisted in the transaction) |
| multi_test.exs | all queryable | Same (`Tx.all`) |
| query semantics | comparison with nil | **Deliberate difference**: Ecto raises on `== nil` and requires `is_nil/1`. In Plasm `= None` is `IS NULL` and `<> Some x` includes NULL rows (`IS DISTINCT FROM`), matching F# equality. |
