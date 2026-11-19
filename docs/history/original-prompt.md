> A working note, kept for the record: the brief the first delivery of Plasm was built from, in November 2026. Parts of it (changesets, preloads, unloaded relationships) were later replaced by the one style; `docs/status.md` says what the library is now.

Build an idiomatic F# persistence library based on Ecto’s established architecture and behavior. Use the Gherkin specifications from this conversation as the initial acceptance contract.

The goal is a coherent, reliable library for immutable F# models, composable queries, explicit changesets, and efficient relationship loading. Developer experience, predictable semantics, and operational correctness are primary requirements.

### Architectural direction

Study Ecto’s implementation, documentation, and tests before designing the library. Understand how schemas, queries, changesets, repositories, preloads, transactions, and database adapters interact.

Adopt those established responsibilities and semantics. Express them through appropriate F# features rather than mechanically reproducing Elixir syntax.

Use immutable values to describe queries, changes, relationship loads, and transactions. Database execution must be explicit. Preserve useful static type information, including optional values and distinct domain identifier types.

Avoid requiring mutable domain records, public setters, parameterless constructors, persistence base classes, or hidden database access.

Treat this as production library engineering. AI-generated code must meet the same review and verification standards as handwritten code.

### Initial investigation

Inspect the repository and follow its instructions. If starting in an empty repository, establish a normal F# solution with library projects, integration tests, examples, and documentation.

Investigate SQLProvider, SqlHydra, EFCore.FSharp, Dapper.FSharp, Facil, and Rezoom.SQL. Determine what infrastructure can be reused without compromising the intended API and semantics. Evaluate EF Core as a possible execution backend rather than assuming it must be replaced.

Record a concise architectural decision covering:

- What is reused and why.
- How queries are represented and translated.
- How immutable records are materialized.
- How relationships and unloaded states are represented.
- How nested preloads are planned, batched, and assembled.
- How changesets distinguish unchanged, assigned, and cleared fields.
- How transactions and concurrency are handled.
- Which behavior follows Ecto and which behavior deliberately differs.

Check upstream licenses and preserve required attribution when adapting code or tests. Continue into implementation after recording the decision; do not stop at a proposal.

### Scope and priorities

Use PostgreSQL for the first supported provider unless the repository already establishes another provider.

Keep provider-specific behavior behind explicit boundaries. Implement one provider thoroughly before adding others.

The first milestone must demonstrate the defining capability:

A reusable, typed query selects a deterministically ordered page of Orders and composes loads for OrderLines, each line’s Product, and Payments. The library executes batched relationship queries and assembles immutable results automatically.

This milestone must establish that:

- Parent pagination remains correct.
- Sibling collections do not create a cartesian product.
- Relationship loading does not issue a query per parent.
- Child queries are restricted to the selected parents.
- Nested loads and reusable child filters compose.
- Loaded-empty, loaded-absent, and unloaded states are distinguishable.
- Original values remain unchanged.
- Unsupported expressions fail explicitly rather than silently running client-side.

Then implement a write path with pure validation, explicit changesets, generated-value handling, optimistic concurrency, and atomic parent/child writes.

### API quality

Exercise the public API through realistic F# examples before expanding internals. Use those examples to assess type inference, discoverability, composition, and error messages.

Computation expressions, quotations, typed descriptors, and generated source are implementation options—not goals. Choose the combination that produces the clearest consistent API.

Avoid unchecked string property paths in the ordinary public API. Keep raw SQL and other escape hatches explicit.

Support ordinary F# functions for reusable query and validation composition. Make effect boundaries visible.

Define conflict semantics for repeated relationship loads. Define whether limits apply globally or per parent. Never substitute one meaning for another.

Do not claim arbitrary domain types or discriminated unions are supported until their mapping, querying, and reconstruction semantics are implemented and tested.

### Acceptance and verification

Save the supplied Gherkin specifications in the repository. Classify each scenario as:

- Implemented and verified.
- Planned.
- Deliberately changed, with rationale.
- Unsupported, with explicit behavior.

Use executable tests to substantiate implemented scenarios. A Gherkin runner is optional; traceability from scenarios to tests is required.

Use real database integration tests for SQL behavior, constraints, transactions, concurrency, and relationship loading. Mocks are insufficient evidence for those guarantees.

Include compile-time acceptance and rejection checks for the typed API. Use property-based tests where they establish meaningful composition or preservation properties.

Adapt relevant Ecto cases into a behavioral compatibility suite. Compare outcomes and failure semantics; require identical SQL only where SQL shape is itself part of the contract.

Measure query counts, transferred row counts where practical, and materialization allocations for representative graph loads. Compare equivalent workloads with EF Core, including its split-query behavior. Report measurements with their conditions and limitations.

### Working approach

Deliver complete vertical slices rather than broad collections of interfaces or placeholders. Keep each slice usable through the public API.

Make reasonable implementation decisions autonomously. Ask only when a consequential choice cannot be resolved from repository context and the stated requirements.

Maintain a short record of decisions, supported behavior, and outstanding limitations. Avoid speculative abstractions for features that have no working use case.

Do not describe the library as production-ready until its supported scope has earned that claim.

### First delivery

Produce:

1. A buildable solution.
2. The architectural decision and acceptance specifications.
3. A working immutable query and nested batched-loading slice.
4. A working transactional changeset slice.
5. Real database tests and concise public API examples.
6. An honest report of verified behavior, measured performance, and remaining gaps.

Begin by inspecting the workspace and studying the relevant Ecto components, then proceed through the first vertical slice.
