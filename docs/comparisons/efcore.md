# EF Core used from F#: the baseline Plasm replaces

*Researched 2026-11-13. Versions: Microsoft.EntityFrameworkCore 11.0.0 (GA 2026-11-10; 10.0.x is the LTS line, supported to 2028-11-10); Npgsql.EntityFrameworkCore.PostgreSQL latest stable 10.0.3 (2026-07-10), 11.0.0-rc.3 (2026-10-28), so the PostgreSQL provider is still behind EF 11 GA; EntityFrameworkCore.FSharp 10.0.0 (2026-10-27).*

*Plasm-side statements corrected 2026-11-14 for the one style (`docs/status.md`): relationships, preloads, public changesets, validation, pipeline queries and repository writes were removed. Where the original text described them, it now describes what replaced them. The EF Core findings are unchanged.*

*Plasm-side statements corrected again 2026-11-20 against `features/`: PostgreSQL enum types, unions with fields, composite keys, `let!` loads with `take` per parent, `selectDistinct`, `exists`, and the every-column concurrency check are built. One EF Core statement was added, on its Native AOT support, from its documentation as read on 2026-11-19.*

**Evidence labels.**
- **[probe]**: verified by running F# code. Sources: `scratchpad/efprobe/Program.fs` and `scratchpad/effsprobe/Program.fs`, with output in `scratchpad/efprobe/probe-output.txt`. The probes used EF Core 11.0.0-rc.3 and Npgsql EF 11.0.0-rc.3 on .NET 11 and PostgreSQL 18.4, and checked SQL with `ToQueryString()` plus a throwaway database.
- **[docs]** / **[issue]**: taken from the cited source.
- **[unverified]**: my inference.

---

## 1. Enums and discriminated unions

**EF Core core:**
- No F# union support. The issues on modelling unions are open: EFCore.FSharp #66 (2021) and #147 (empty unions). dotnet/efcore #36375 only considers EF support for the *upcoming C#* unions (`consider-for-next-release`), and #38059 covers C# unions mapped to JSON.
- Fieldless unions work through a hand-written `ValueConverter`. Equality with a case translates: `o.Status = Paid` → `WHERE o."Status" = 'paid'` **[probe]**.
- A converter is opaque. You cannot query into a converted value (efcore #10434, [value-conversion limitations][vc]). Nulls never reach a converter (#13850).
- A converter that throws is the only way to reject an unknown stored value [docs]. Plasm's `Scalar.tryWrap` returns a materialisation error value instead.

```fsharp
mb.Entity<Order>().Property(fun o -> o.Status)
  .HasConversion((fun s -> match s with Pending -> "pending" | Paid -> "paid"),
                 (fun s -> if s = "paid" then Paid else Pending))   // unknown text silently becomes Pending
```

**Single-case unions:**
- Converters handle equality: `o.Id = OrderId 5` → `WHERE o."Id" = 5` **[probe]**.
- Unwrapping inside a predicate fails. `(let (OrderId v) = o.Id in v) > 5` throws `NotSupportedException: Specified method is not supported` from F#'s quotation-to-LINQ conversion **[probe]**. EFCore.FSharp documents the `let (PositiveInteger votes) = blog.Votes` pattern inside `query { }` as working ([docs][fs-scu]) [unverified for method chains].
- EFCore.FSharp `RegisterSingleUnionCases()` / `SingleCaseUnionConverter`: private constructors are unsupported, and there are open bugs:
  - #139: the `Equal` operator is not defined for `UserId`
  - #142: a migration is not generated for an optional single-case union
  - #146: the translator returns non-null for non-applicable expressions
  - #149: fails from C#

**PostgreSQL enum types (Npgsql EF provider):**
- **EF 9.0+** ([enum docs][npg-enum], [9.0 release notes][npg9]):
  - The one gesture is `UseNpgsql(cs, fun o -> o.MapEnum<Mood>("mood") |> ignore)`. It maps at both the EF and ADO levels and adds the type to migrations.
  - With an external `NpgsqlDataSource` you **must also** call `dataSourceBuilder.MapEnum<Mood>()`.
  - `HasPostgresEnum` is no longer needed.
- **Before 9.0:** `HasPostgresEnum<T>()` in the model, plus ADO-level mapping. That was either `NpgsqlDataSourceBuilder.MapEnum`, or the obsolete static `NpgsqlConnection.GlobalTypeMapper.MapEnum` called before any EF use, which was fragile with several contexts.
- **Breaking change in 9.0:** "enums must now be configured at the EF level". ADO-level mappings are no longer picked up automatically [docs].
- Only **CLR enums** can be mapped (`type Mood = Happy = 0 | Sad = 1`), not F# unions [unverified, but `MapEnum<TEnum>` requires `struct, Enum`].

**What broke for users:**
- efcore.pg #3390 (v8→9): a new migration recreated every enum with labels in **alphabetical** rather than declaration order. Users had to keep `HasPostgresEnum` as well ("4 places instead of 2"). The maintainer's fix was an empty migration to realign the snapshot. Reports of the reordering persisting followed.
- #3667 (v8→10): with only `UseNpgsql(o => o.MapEnum)`, users saw alphabetical ordering and **wrong values on read/write**. Restoring correct behaviour needed MapEnum in three places. Closed not planned.
- #3653 (10.0 RC): `InvalidCastException: Reading as 'System.Int32' is not supported for ... 'public.recipe_difficulty'`. The config had worked in 8.x.
- #3478 (v9): calling `MapEnum` after `ConfigureDataSource` silently discarded the data-source callback (for example `EnableRecordsAsTuples`). Fixed in 9.0.4.
- Still open:
  - #3631: `NpgsqlDataSourceBuilder.MapEnum` doesn't take a schema
  - #3915: wrong non-schema enum mapping (2026-09)
  - #3675 / #3426: seeding with new enums fails in migrations
  - #2963: non-default schema

**Against Plasm:**
- `Scalar.enum` maps a fieldless union through an explicit, exhaustive table. It checks that every case is listed once and that stored names are distinct, and unknown text is a materialisation error. It is stored as **text**.
- `Scalar.asPgEnum "type"` stores the same union in a PostgreSQL enum type: the value is sent as text with a cast and read as `::text`, with no `MapEnum`, and the start-up check compares the type's labels with the union's cases, in order (`features/pg-enums.feature`).
- A union with fields is a configuration error in EF Core. Plasm stores one across columns by convention (`features/union-fields.feature`).

## 2. Options and nulls

**Plain EF Core:**
- An `option` property is mistaken for a navigation. `string option` with no converter gives `The entity type 'FSharpOption<string>' requires a primary key to be defined` **[probe]**.
- With a hand-written converter (`Option.toObj`/`Option.ofObj`):
  - `o.Note = Some "x"` and `o.Note = None` translate (`= 'x'`, `IS NULL`) **[probe]**.
  - `o.Note.IsSome` fails: *"Translation of method FSharpOption.get_IsSome failed"* **[probe]**.
  - `o.Note.Value.StartsWith "a"` fails **[probe]**.
  - `Option.map` in a projection fails at quotation conversion (`NotSupportedException`) **[probe]**.
- A value-type option (`int option`) needs a separate converter per type. A `box`-based converter does not handle value types [Daniel Summers, "F# options with EF Core", 2018].

**EFCore.FSharp:**
- `options.UseFSharpTypes()` plus `modelBuilder.RegisterOptionTypes()` adds member translators ([docs][fs-opt]). Under EF 11 rc.3 these all translate, and `None`/`Some 4` round-trip **[probe]**:
  - `IsSome` → `IS NOT NULL`
  - `Value.StartsWith` → `LIKE 'a%'`
  - `Score.Value > 3`
  - `Score = Some 3`
- Open bugs:
  - #157: NullReferenceException on the second migration with a `string option`
  - #154: NRE deleting optional fields
  - #156: scaffolding a nullable column gives `string`, not `string option`
  - #159: `UseFSharpTypes()` breaks Npgsql jsonb `where` translation
  - #143: Pomelo migrations with options
  - #29 and #144: optional navigation properties
  - #120: `EnsureCreated` makes all fields nullable on .NET 6
- dotnet/efcore #34552 (backlog) proposes moving these translators into EF itself.

**Null semantics:**
- EF emulates C# null semantics by default. `not (o.CustomerId = Nullable 3)` → `o."CustomerId" <> 3 OR o."CustomerId" IS NULL` **[probe]**, which is the same result as Plasm's two-valued `not`. `UseRelationalNulls()` switches this off [docs].
- In F#, nullable value columns are `Nullable<int>`, which is not idiomatic.
- Reference navigations not loaded are **`null`**, and so are collection navigations that were not included:
  - `line.Product` isNull=true
  - `order.Payments` isNull=true; this is null, not empty **[probe]**

  So "not loaded", "no related row" and "empty" all look alike.

**Plasm:**
- Options are native, including on keys and in joins (`leftJoin` gives `'T option`).
- `= None` → `IS NULL`; option-to-option equality → `IS NOT DISTINCT FROM`. `.IsSome`, `.Value`, `Option.map` and `Option.bind` to a field all translate.
- Nothing is ever "not loaded": what a record owns or links is a plain field, always loaded with it, so `None` and `[]` mean none.

## 3. Relationships and loading

**Records must be `[<CLIMutable>]`:**
- A plain record fails: *"No suitable constructor was found for the type 'ImmTodo'... Cannot bind 'id', 'title'"* **[probe]**. This is dotnet/efcore #34357 ("F# immutable Records do not work", open, backlog, `area-F#`).
- The constructor binding exists, but read-only record properties are not mapped by convention, so the parameters have nothing to bind to [unverified mechanism].

**Collection navigations:**
- They must be mutable collections EF can instantiate.
- `LChild list` passes model building and `Include` translation, but fails on materialisation: *"The type of navigation 'LParent.Children' is 'FSharpList<LChild>' for which it was not possible to create a concrete instance... use a type which can be assigned a 'HashSet<>' or 'List<>'"* **[probe]**.
- EFCore.FSharp's many-to-many guide says: "Use ResizeArray to avoid ... Collection was of a fixed size" ([docs][fs-m2m]).
- Recursive record types must be in the same file.

**Include / ThenInclude from F#:**
- After a collection include, `ThenInclude` does not compile. `.Include(fun o -> o.Lines).ThenInclude(fun (l: OrderLine) -> l.Product)` gives *FS0001: expected 'ResizeArray<OrderLine>' but here has type 'OrderLine'* **[probe]**.
- The cause: C# relies on `IIncludableQueryable<_, out TProperty>` covariance to pick the `IEnumerable<TPrev>` overload, and F# overload resolution does not apply that variance [mechanism unverified].
- The workaround is upcasting in every include: `.Include(fun o -> o.Lines :> seq<_>)` **[probe]**. The same upcast is needed in `HasMany(fun o -> o.Lines :> seq<_>)`.

```fsharp
ctx.Orders.AsNoTracking()
  .Include(fun o -> o.Lines :> seq<_>).ThenInclude(fun (l: OrderLine) -> l.Product)
  .Include(fun o -> o.Payments.Where(fun p -> p.Succeeded).OrderBy(fun p -> p.Id))  // filtered include: OK [probe]
  .AsSplitQuery()
```

**Single vs split** ([docs][split]):
- Single query is the default. EF warns when several collections are loaded without a chosen mode.
- `UseQuerySplittingBehavior(SplitQuery)` sets the mode globally, and `AsSingleQuery()` overrides it per query.
- Documented caveats of split queries:
  - no consistency across the queries unless you use a serializable or snapshot transaction
  - one round trip per collection
  - buffering of earlier results
  - before EF 10, non-unique ordering with Skip/Take could return **wrong data** (EF 10 fixed the ordering)
  - reference navigations joined into every split query (#29182, pruned in EF 11)
- Split queries re-join the parent query into each child query rather than sending a key list [docs SQL].
- `AsNoTracking` materialises one Product object per line (measured 2026-11-12, when Plasm's benchmark still loaded products: 10,000 instances against Plasm's 200). `AsNoTrackingWithIdentityResolution` fixes that. In the one style a line refers to its product by id, so the current benchmark no longer reads products.

**Filtered include:**
- `Where`/`OrderBy`/`Skip`/`Take` inside `Include` (EF 5+). `Take` inside a filtered include is per parent [docs; unverified here]. In Plasm an owned list always loads whole; a `let!` load takes its own `where`, `orderBy` and `take`, applied per parent (`features/loads.feature`).

**Lazy loading:**
- Proxies need `virtual` navigations with backing fields. F# explicit fields get an `@` suffix that EF can't discover: dotnet/efcore #12923, open since 2018, `area-F#`.
- `ILazyLoader` injection needs a constructor parameter or a mutable field.
- Not realistic for records.

**Explicit loading:**
- `ctx.Entry(o).Collection(...).LoadAsync()` needs tracking. Plasm has no explicit loading: another aggregate is read by a query of its own (for a page of parents, a prepared query with `List.contains` over their ids).

## 4. Writes, change tracking, transactions, concurrency

**Change tracking assumes mutation:**
- The idiomatic F# update `{ o with Total = 9m }` on a tracked entity fails: *"The instance of entity type 'Order' cannot be tracked because another instance with the same key value for {'Id'} is already being tracked"* **[probe]**.
- A detached record plus `Update` works, but marks **every column** modified **[probe]**. That is the opposite of Plasm's "unmentioned fields are never written".
- EFCore.FSharp's `updateEntity ctx (fun b -> b.Id :> obj) { b with ... }` hides the detach and attach ([docs][fs-helpers]).
- dotnet/efcore #11457 ("Support immutable entity updates", 153 upvotes, needs-design, backlog since 2018) is the root issue. roji: "change tracking and updating rely on mutability".
- Isaac Abraham (#34357) says most F# users would use EF "disconnected", without tracking.

**Other write features:**
- `SaveChanges` batches heterogeneous changes in one implicit transaction, with FK fix-up and cascade rules.
- `BeginTransaction` / `UseTransaction` / `TransactionScope` are available.
- `NpgsqlRetryingExecutionStrategy` retries transient failures, but user transactions must then be wrapped in `strategy.ExecuteAsync` [docs, not re-checked].
- `ExecuteUpdate` / `ExecuteDelete` (EF 7+). EF 10 setters are a plain lambda, which suits F#: `ExecuteUpdate(fun s -> s.SetProperty((fun o -> o.Total), 1m) |> ignore)` compiles and runs **[probe]**.
- **Concurrency:** `[<Timestamp>]` / `IsRowVersion()` on a `uint` mapped to PostgreSQL `xmin`, so no schema column is needed, or any `IsConcurrencyToken()` property. Npgsql 11 maps serialization failures (40001) and deadlocks (40P01) during `SaveChanges` to `DbUpdateConcurrencyException` ([docs][npg-cc]).
- **Validation:** none. EF Core does not run DataAnnotations validation on save. Nothing like a changeset.

**Plasm:**
- No validation either, on purpose: the domain's constructors validate, and constraints with PostgreSQL's default names come back as field errors.
- `Tx.update (original, changed)` writes only the columns that differ, and what the record owns by difference (ownership is the removal policy).
- `transaction { }` with typed steps; concurrency by an integer version column or by the original record's columns.
- Plasm lacks:
  - `xmin`-style tokens
  - belongs-to and has-one writes through the parent
  - deep graph inserts
  - a unit of work across arbitrary entities

## 5. Migrations from F#

**EFCore.FSharp** is the only F# design-time path.
- 10.0.0 (2026-10-27) generates F# migrations and scaffolds F# records, with option support.
- The README says: "created migrations must be manually added to your solution in the correct order". You can glob instead: `<Compile Include="Migrations/*.fs" />`.
- Open issues:
  - #43, #110: migrations generate into the wrong project or language
  - #157: NRE on the second migration with a `string option`
  - #142: optional single-case union not migrated
  - #160 / #161: jsonb snapshots don't compile (`JsonDocument` not defined, an empty `open`)
  - #145: scaffolding fails on DEFAULT
  - #164: cannot add migration on .NET 8 / EF 6
  - #152: Getting Started guide broken
- Enum-label ordering churn in snapshots (efcore.pg #3390) affects F# and C# alike.

**Alternatives** [unverified, common practice]:
- a C# project holding the DbContext and migrations
- SQL-first tools (DbUp, Grate, Flyway)

**EF 11 additions** ([what's new][ef11]):
- `database update --add` compiles a new migration with **Roslyn**, so presumably C# only [unverified for F#]
- the latest migration ID is recorded in the snapshot, which forces a merge conflict on divergent migrations
- `ExcludeForeignKeyFromMigrations`
- `.config/dotnet-ef.json`

**Plasm** deliberately has no migrations. `repo.CheckSchema()` checks the live catalogue. The closest EF equivalent, `HasPendingModelChanges()`, compares the model with the migration snapshot, not the database [unverified].

## 6. Errors: exceptions vs values

**EF throws:**
- `DbUpdateException` wraps the `PostgresException`. A unique violation surfaces as `DbUpdateException --> PostgresException: 23505: duplicate key value violates unique constraint "PK_Products"`, with DETAIL redacted unless `Include Error Detail` is set **[probe]**.
- `DbUpdateConcurrencyException` for stale rows.
- `InvalidOperationException` for untranslatable LINQ, at execution time.
- Model errors at first context use.

```fsharp
try do! ctx.SaveChangesAsync() :> Task
with :? DbUpdateException as e ->
    match e.InnerException with
    | :? PostgresException as pg when pg.SqlState = PostgresErrorCodes.UniqueViolation -> // pg.ConstraintName
    | _ -> reraise ()
```

- **EntityFrameworkCore.Exceptions.PostgreSQL** 10.0.1 (2026-05-24) gives typed subclasses of `DbUpdateException`: `UniqueConstraintException`, `ReferenceConstraintException`, `CannotInsertNullException`, `MaxLengthExceededException`, `NumericOverflowException`. Constraint properties are only filled for indexes in the EF model.
- No `Result`-returning API exists. F# users wrap calls themselves.
- **Plasm:** a transaction returns `Result<_, TransactionError<'E>>` with `TxFailure.Rejected` (the transaction's own reason), `InvalidChanges` (field errors), `Conflict`, and `Failed` with a classified `DbFailureKind`. It says where the failed step is written, and treats `MoreThanOneRow` and `RepositoryClosed` as values.

## 7. F# query translation

**Two front ends, both lossy:**
- **Method chains:** F# lambdas are converted to `Expression<Func<_>>` at method calls.
- **`query { }`:** quotations are converted to LINQ by `FSharp.Linq`.

**Verified [probe]:**

| F# shape | Result |
|---|---|
| tuple `select (o.Id, o.Total)` in `query { }`, then `ToListAsync` | OK (fsharp #3782 / #16503 did not reproduce on .NET 11) |
| record construction in `select`, `ToListAsync` | OK |
| `groupBy o.CustomerId into g; select (g.Key, g.Count())` | OK |
| two `leftOuterJoin`s | translates, but to nested `JOIN LATERAL (SELECT 1 ...) LEFT JOIN ... ON TRUE` |
| .NET 10 `LeftJoin` operator from F# | clean `LEFT JOIN` |
| anonymous record with fields out of alphabetical order `{| Z = ..; A = .. |}` | **fails** `NotSupportedException` (fsharp #11131/#15648 closed but still broken: fsharp #20649, 2026-09) |
| `List.contains p.Id ids` (F# list) | **fails**: `ListModule.Contains` not translated |
| `ids.Contains p.Id` (array) | OK |
| `Option.map` in a projection | **fails** |
| union pattern `let (OrderId v) = o.Id` in a lambda | **fails** |

Other known shapes: struct tuples (fsharp #15522), nested `for` and double left joins (#8459, #6552), nested lambdas (#2758).

**Captured values become SQL constants:**
- `let mutable minTotal = 11m in ...Where(fun o -> o.Total > minTotal)` gives `WHERE o."Total" > 11.0` **[probe]**. The same happens in `query { }`, and `[|1;2;3|].Contains` gives `IN (1, 2, 3)`. So every distinct value produces new SQL, an EF query-cache miss and a database plan miss.
- dotnet/fsharp #12841: Don Syme called it "by-design... I don't think it can be changed". Closed after EF added **`EF.Parameter(x)`**, which gives `WHERE o."Total" > @p` **[probe]**. A `ref` cell also works (`@Value`) **[probe]**.
- This is the biggest silent F#-specific EF pitfall. Plasm parameterises captured values by construction.

**Tests:**
- fsharp #20649 (open) notes that F#'s query tests only run over `AsQueryable()`, which executes anything. It proposes tests against EF Core SQLite and Cosmos.
- EF re-enabled its own F# tests in 11.0 (#34675, after a compiler bug, fsharp #17701).
- Performance: quotation-based ORM use allocates heavily (fsharp #10522, open).

## 8. Maintenance of the F# pieces

- **EF Core:** Microsoft, yearly releases; EF 10 is LTS to 2028-11.
  - F# is a labelled area, but the open items have sat in the backlog for a long time: #34357 (2024), #34552 (2024), #12923 (2018), #11457 (2018).
  - "F# Support" #6401 (2016) is closed. #10731 "Understand what working better with F# records would entail" is closed.
  - The Npgsql provider usually lags EF GA: 11 is still rc.3 on 2026-11-13.
- **EFCore.FSharp:** 242 stars, 26 open issues.
  - It went from 6.0.7 (2022-06-26) straight to 10.0.0 (2026-10-27), so EF 7, 8 and 9 had no release.
  - #155 "Support EF Core 7" is still open. Issue #166 "Maintainance" (2025-02): the collaborator has "the will, but not the time". An EF 9 attempt stalled on `CSharpDbContextGenerator` changes.
  - The 10.0 notes mention a C# interop project because "F# cannot handle covariant return types".
  - The package pins `FSharp.Core >= 10.0.102` (the notes say 9.0.100). That rules out hosts with older FSharp.Core, the same constraint Plasm avoids by targeting 8.0.
  - Its option translators did work under EF 11 rc.3 **[probe]**.
- **Smaller helpers:** FSharp.EFCore.OptionConverter (NuGet) and blog-post converters. Age and status not verified.

## 9. Developer experience: where EF is orthogonal to F#

**Where it fights F#:**
- `[<CLIMutable>]` on everything, with `ResizeArray` (not `list`) navigations.
- `null` navigations when not loaded.
- `Nullable<T>` instead of options unless you add EFCore.FSharp.
- `{ x with }` conflicts with the identity map.
- `:> seq<_>` upcasts in `Include` and `HasMany`.
- `|> ignore` after every fluent configuration call.
- `[<DefaultValue>] val mutable` for `DbSet` properties (or `this.Set<T>()`).
- Captured values silently inlined.
- Translation failures at runtime for idiomatic F# (`List.contains`, `Option.map`, union patterns, anonymous records).
- Exceptions rather than `Result`.
- Design-time tooling written for C#.

**Where it is neutral:**
- LINQ method chains read fine.
- `query { }` is pleasant for joins and grouping.
- `ExecuteUpdate`'s EF 10 lambda is idiomatic enough.
- `FromSql($"...")` takes an F# interpolated string as `FormattableString`.
- DI and ASP.NET integration, logging and diagnostics work.

Isaac Abraham's comment on #34357 sums up the F# usage model: disconnected, no tracking, "simply being able to construct records would be a big win".

---

## What EF Core does that Plasm does not

The gap list for an EF Core replacement. Plasm status is from `docs/status.md` (2026-11-12/13).

**Platform and tooling**
1. Providers: SQL Server, Azure SQL, SQLite, Cosmos DB, InMemory, plus third-party MySQL/MariaDB (Pomelo), Oracle and others. Plasm is PostgreSQL only.
2. Migrations: generation from model diffs, snapshots, bundles, idempotent scripts, `database update --add`, seeding (`HasData`, `UseSeeding`), `EnsureCreated`. Plasm has none, by design: it prints the DDL its model expects and checks the schema as the repository starts.
3. Reverse engineering of a whole database into a context, with relationships (`dotnet ef dbcontext scaffold`; EFCore.FSharp emits F#). Plasm's `Scaffold` does one table and no relationships.
4. Compiled models (startup), DbContext pooling, `AddDbContext`/`AddDbContextFactory` DI integration, health checks. The compiled model is also half of EF Core's Native AOT path, with precompiled queries, which its documentation calls "highly experimental" and "not yet suited for production use" ([NativeAOT Support and Precompiled Queries (Experimental)](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries), read 2026-11-19).
5. Ecosystem built on `IQueryable` and DbContext: ASP.NET Core Identity stores, OData, HotChocolate GraphQL projections and filtering, Aspire integration, EF Core Power Tools, many books and Stack Overflow answers.
6. Logging through `ILogger`, `EnableSensitiveDataLogging`, `TagWith` query tags, event IDs, `DiagnosticSource` and OpenTelemetry-friendly instrumentation, interceptors for commands, connections, transactions, `SaveChanges` and materialisation. Plasm has a diagnostics callback, `OnCommand`, and the same events as activities of the ActivitySource "Plasm", tagged by OpenTelemetry's database conventions (`features/activities.feature`); it has no interceptors.
7. Transient-fault execution strategies (connection resiliency). Plasm has only an opt-in `RetryPolicy` for transactions.

**Modelling**
8. Inheritance: TPH, TPT, TPC.
9. Owned and complex types (table splitting, value objects across several columns, optional complex types, structs) and JSON columns (`ToJson`, query into JSON, `ExecuteUpdate` on JSON). Plasm's owned records live in their own tables; a record with no table is stored in its owner's row (like a complex type), and a field marked `[<Json>]` is one jsonb document that queries can read into. Plasm has no table splitting, structs as values, or bulk updates into a document.
10. Alternate keys, shadow properties, backing fields, property access modes. Composite primary and foreign keys are in both: in Plasm a key of several fields is a record of them, with references, owned lists, upserts and linked lists by it (`features/composite-keys.feature`, `features/composite-references.feature`, `features/composite-links.feature`).
11. Primitive collections as columns, queryable (PostgreSQL arrays: `= ANY`, `@>`). Plasm stores a list of scalars as an array column and translates `List.contains` (`= ANY`) and `List.isEmpty`, nothing more over it.
12. Many-to-many with payload or skip navigations, and has-through equivalents. Plasm has plain linked lists (`[<JoinTable>]`) only.
13. Keyless entities, views, table-valued and user-defined function mapping (`HasDbFunction`), stored-procedure mapping for inserts, updates and deletes, raw SQL entity sources composable with LINQ (`FromSql(...).Where(...).Include(...)`). Plasm's `repo.Sql` is not composable.
14. Value generation: sequences, HiLo, identity options, computed columns, defaults, `ValueGeneratedOnAddOrUpdate`.
15. Concurrency tokens of any type, including PostgreSQL `xmin` without a schema column. Plasm has an integer `[<Version>]` field or, on a record without one, a check of every column of the record read (`features/concurrency.feature`); it does not use `xmin`.
16. Global and named query filters (soft delete, multi-tenancy).
17. Npgsql-specific types and functions:
    - PostgreSQL enums (mapped CLR enums)
    - arrays, ranges and multiranges, `jsonb` (DOM and POCO), hstore
    - full-text search (`tsvector`, `EF.Functions.ToTsVector`), `ILike`, citext, network types
    - NodaTime, PostGIS/NetTopologySuite
    - plugins through `ConfigureDataSource`
18. Index, constraint, collation and comment declarations in the model (feeding migrations).

**Querying**
19. Set operations (`Union`, `Concat`, `Except`, `Intersect`) and `Distinct`. Subqueries in projections, including **nested collections projected into DTOs** (`Select(fun o -> {| o.Id; Lines = o.Lines.Select(...) |})`). `GroupBy` final operator. `MaxBy`/`MinBy` (EF 11). Right and full joins (EF 10 and 11 operators). Non-equi joins, `SelectMany`, correlated subqueries. Of these Plasm has `Query.union`, `unionAll`, `intersect` and `except` (`features/set-operations.feature`), `selectDistinct`, `exists` over a correlated query, and nested lists in a result through `let!` loads; the rest it lacks.
20. Eager loading combined with joins and projections. A choice of single-query JOIN loading or split loading, per query or globally. Plasm always batches, always loads what a record owns, and lets no query choose what to load.
21. Identity resolution (`AsNoTrackingWithIdentityResolution`, the tracked identity map).
22. Lazy loading (proxies, `ILazyLoader`), explicit loading through `Entry`.
23. Streaming with `AsAsyncEnumerable`. Plasm streams with `repo.Stream` (an `IAsyncEnumerable` of `Result`), for queries of records without lists and for projections; EF Core streams any query, includes too.
24. Compiled queries (`EF.CompileAsyncQuery`), parameter-mode control for collections (`EF.Constant`, `EF.Parameter`, `UseParameterizedCollectionMode`). Plasm has `Query.prepare`, with prepared projections for one and two arguments.
25. A large, provider-extensible method-translation catalogue: date and time parts, math, string functions, `EF.Functions.*`, user-mapped functions. Plasm translates a fixed subset (comparisons, `StartsWith`/`EndsWith`/`Contains`, `ToLower`/`ToUpper`/`Trim`/`Length`) plus `Raw.fragment`.

**Writing**
26. A unit of work across arbitrary entity types in one `SaveChanges`, with statement batching, FK fix-up, and graph inserts or updates at any depth. That covers belongs-to and has-one writes and nested children. Plasm writes an aggregate with what it owns and links; another aggregate is written by its own step.
27. Cascade delete and delete-orphans configuration, and `DeleteBehavior` in the model and the DDL.
28. Bulk `ExecuteUpdate`/`ExecuteDelete` over navigations or joins (`UPDATE ... FROM`). Plasm's bulk writes reject joins; `Bulk.updateReturning` and `Bulk.deleteReturning` give back the rows written (`features/bulk-writes.feature`).
29. Conflict-resolution APIs (`GetDatabaseValues`, `Reload`, `OriginalValues`), `SaveChanges` events and interceptors (auditing).
30. Ambient and distributed transactions (`TransactionScope`), savepoints exposed (`CreateSavepoint`), sharing a connection or transaction with ADO.NET or Dapper (`UseTransaction`). Plasm hides the transaction inside `Tx.run`; `Tx.attempt` runs one step under a savepoint (`features/tx-attempt.feature`).

**Testing**
31. Swappable in-memory and SQLite providers for tests. Plasm's `Sandbox` is real-database only, which is arguably better but is a different choice.

## What Plasm does that EF Core does not

1. **Plain immutable F# records.** No `[<CLIMutable>]`, F# `list` children, options without an add-on. In EF, records without CLIMutable fail (#34357 **[probe]**), F# lists fail to materialise **[probe]**, and options need EFCore.FSharp.
2. **Nothing is left unloaded.** EF uses `null` for a not-loaded reference and for a not-included collection **[probe]**, the same as "none". In Plasm what a record owns or links is a plain field, always loaded, and nothing queries on access, so there is no lazy-loading trap.
3. **Captured values are always parameters.** EF from F# inlines them as constants unless you write `EF.Parameter` **[probe]** (fsharp #12841, by design).
4. **Idiomatic F# translates:**
   - `.IsSome`, `.Value`, `Option.map` / `Option.bind` to a field
   - `List.contains` / `Seq.contains` over F# lists
   - `exists (plasm { ... })`, and `List.exists` / `List.forall` over owned and linked lists
   - left-joined sources as `'T option`

   EF fails on `List.contains`, `Option.map`, union patterns and out-of-order anonymous records **[probe]**.
5. **Batched loading by key.** One `= ANY(@keys)` command per owned or linked list, a cartesian-free default with no mode to choose, and parent pages fetched first. In Plasm's own benchmark (`docs/benchmarks.md`) a page of 1,000 orders with their lines and payments took 14.5 ms against 21 ms for EF split query.
6. **The application's own records, read whole.** Types state what they can (wrappers, `create` functions, unions with fields stored across columns, typed ids as references); the record states the rest once, with `[<Table>]` and a few field attributes.
7. **Writes by difference.** Constraint-to-field error mapping by PostgreSQL's default names, and "only what differs is written". EF `Update` on a detached record writes every column **[probe]**.
8. **Immutable updates as values.** `Tx.update (order, { order with ... })` versus EF's identity-map conflict on `{ o with }` **[probe]** (#11457).
9. **Results, not exceptions.** `TxFailure.Rejected | InvalidChanges | Conflict | Failed` with a classified `DbFailureKind`, plus `DbError.MoreThanOneRow` and `RepositoryClosed`. EF throws `DbUpdateException` wrapping `PostgresException` **[probe]**.
10. **Transactions as values.** `transaction { }` with typed steps, failures that say where the step is written and carry the transaction's own error type (`TxFailure<'E>`), and explicit retry policy. `Tx.run` cannot commit or roll back.
11. **Ownership as the removal policy.** An owned item left out of its owner is deleted, an item of another owner or listed twice is refused, and a linked record is never written through its owner, instead of tracking-dependent orphan handling.
12. **Typed aggregates with F# semantics.** `Agg.sum` of nothing is 0, `min`/`max`/`avg` return options, `avg` requires `DivideByInt`. EF's `Max` over an empty set of non-nullable values throws [unverified for EF 11].
13. **`repo.CheckSchema()`** against the live catalogue. EF compares against its migration snapshot only [unverified].
14. **Test sandbox** (rolled back, savepoint per operation, nesting) as a first-class API.
15. **F# Interactive.** Queries display their SQL and parameters, `Playground`, `Scaffold`, and a `RedefinedType` explanation. EF in FSI works but has none of these [unverified].
16. **Typed raw-SQL fragments inside typed queries** (`Raw.fragment` with literal-only format strings). EF has `FromSql` only at the root, plus function mapping.
17. **Domain scalars with validating constructors** (a `create` function, found by convention), where refused stored values are errors, not exceptions or silent defaults.
18. **Every public union qualified, and FSharp.Core 8 compatibility.** EFCore.FSharp requires FSharp.Core ≥ 10.0.102.

**Parity worth noting** (no Plasm advantage):
- Two-valued `not` over nullable columns: EF also emits `<> 3 OR IS NULL` **[probe]**.
- `FormattableString` raw SQL.
- Prepared or compiled queries.
- Bulk update and delete.

[vc]: https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions
[split]: https://learn.microsoft.com/en-us/ef/core/querying/single-split-queries
[ef11]: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/whatsnew
[npg-enum]: https://www.npgsql.org/efcore/mapping/enum.html
[npg9]: https://www.npgsql.org/efcore/release-notes/9.0.html
[npg-cc]: https://www.npgsql.org/efcore/modeling/concurrency.html
[fs-opt]: https://github.com/efcore/EFCore.FSharp/blob/master/docsSrc/How_Tos/Querying_Options.md
[fs-scu]: https://github.com/efcore/EFCore.FSharp/blob/master/docsSrc/How_Tos/Using_Single_Case_unions.md
[fs-m2m]: https://github.com/efcore/EFCore.FSharp/blob/master/docsSrc/How_Tos/Many-To-Many_Relationships.md
[fs-helpers]: https://github.com/efcore/EFCore.FSharp/blob/master/docsSrc/How_Tos/Use_DbContextHelpers.md

**Issue index.**
- dotnet/efcore: #34357, #34552, #12923, #11457, #34675, #36375, #38059, #10434, #13850, #29182, #6401, #10731
- dotnet/fsharp: #12841, #20649, #16503, #3782, #8459, #6552, #11131, #15522, #2758, #10522, #10523, #17701
- npgsql/efcore.pg: #3390, #3667, #3653, #3478, #3631, #3915, #3675, #3426, #2963
- efcore/EFCore.FSharp: #166, #164, #161, #160, #159, #157, #156, #155, #154, #152, #149, #147, #146, #145, #144, #143, #142, #139, #120, #110, #82, #66, #43, #29
- Other: NuGet pages for Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL, EntityFrameworkCore.FSharp and EntityFrameworkCore.Exceptions.PostgreSQL.
