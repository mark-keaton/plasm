# Read measurements: Plasm vs EF Core

Measured 2026-11-15 in the one style: an Order owns its lines, and payments
are another aggregate read by id. These numbers describe one machine and one
dataset. Read the limitations before generalising. The earlier measurements
(2026-11-12) were taken of a different workload, with relationships loaded by
`preload` and products included, so they are not comparable; they are not
repeated here.

## How to run

```sh
cd benchmarks/Plasm.Benchmarks
dotnet run -c Release -- counts    # commands, rows, allocations, median time, EF SQL
dotnet run -c Release -- bench     # BenchmarkDotNet (time + allocations)
dotnet run -c Release -- profile   # planning cost, raw-driver floor, per-command time
```

A PostgreSQL 18.4 container is started by Testcontainers (Docker required), or
set `PLASM_TEST_PG` to an admin connection string. The dataset is seeded once
per process with bulk SQL (`benchmarks/Plasm.Benchmarks/Workloads.fs`).

## Environment

| | |
|---|---|
| Machine | Apple M4 Max, 16 cores, 64 GB |
| OS | macOS 26.3.1 (25D2128) |
| .NET | SDK 11.0.100, runtime 11.0.0, Arm64 RyuJIT, Release, workstation GC, tiered PGO |
| Database | PostgreSQL 18.4 (Debian build) in a local OrbStack container; client and server talk over loopback |
| Driver | Npgsql 10.0.3 for both libraries |
| Plasm | this repository (`src/Plasm`, `src/Plasm.Postgres`) |
| EF Core | Microsoft.EntityFrameworkCore 11.0.0-rc.3.26522.148 + Npgsql.EntityFrameworkCore.PostgreSQL 11.0.0-rc.3 (the provider is RC and pins EF to rc.3) |
| BenchmarkDotNet | 0.15.8, `InProcessEmitToolchain`, 3 warm-ups, 10 iterations, 1 launch |

BenchmarkDotNet labels the runtime "DEBUG". The benchmark assemblies have
`DebuggableAttribute.IsJITOptimizerDisabled = false` (checked 2026-11-12); the
label looks like 0.15.8 misreading .NET 11's JIT information.

During this run other builds and test suites were running on the same machine,
so the times are noisier than an idle machine would give: `counts` medians for
the same workload varied by up to 40% between two runs. Allocations and command
and row counts are stable.

## Dataset

100 customers, 200 products and 10,000 orders. Two in three orders are `published`,
and `placed_at` rises one minute per order. Each order has 10 lines and 10
payments. A third of the payments are `succeeded`; the rest are `failed` or
`pending`. That gives 100,000 lines and 100,000 payments, with indexes on
`order_lines(order_id)` and `payments(order_id)`. The schema is
`tests/Plasm.TestDomain/Database.fs`; the domain is
`tests/Plasm.TestDomain/Domain.fs`, whose scalars are all stored by convention.

## Workloads

1. **Page.** Published orders ordered by `placed_at desc, id`, taking N = 20,
   100 or 1,000, each with its lines, and the succeeded payments of those
   orders ordered by `created_at`.
2. **Siblings.** Order 1 with its 10 lines and all 10 payments. Two
   collections side by side show the cartesian product.

The variants are:

- **Plasm:** `repo.All (plasmPage n)` gives the orders with their lines (two
  commands: orders, then the lines of exactly those orders). The payments are a
  second query, prepared once:
  `succeededPaymentsOf (orders |> List.map (fun o -> o.Id))`, which is
  `List.contains p.OrderId ids && p.Status = Succeeded`, one array parameter.
  For the siblings workload there are two Plasm variants: both queries built
  for each call, and both prepared (`orderById`, `paymentsOfOrder`). See
  `Workloads.fs`.
- **EF single query:** `AsNoTracking`, `Include(o => o.Lines)` and a filtered
  `Include(o => o.Payments.Where(..).OrderBy(..))`, run with `AsSingleQuery`.
- **EF split query:** the same query with `AsSplitQuery`.

Products are no longer part of the workload: in the one style a line refers to
its product by id, and the EF query no longer includes them, so both sides read
the same rows.

The EF model and queries are written in C#
(`benchmarks/Plasm.Benchmarks.EfModel`). That is EF's idiomatic surface, and it
avoids F#-quotation-to-LINQ translation differences. EF needs mutable classes
with settable navigation collections.

Every run checks that all variants return the same graph (orders, lines and
payments counts). They did.

## Methodology

- **Commands and rows:** for Plasm, `RepoOptions.OnCommand` (`CommandEvent.Rows`).
  For EF, a `DbCommandInterceptor` captures each command. Its rows are counted
  by re-running the captured SQL as `select count(*) from (<sql>)` with the same
  parameters. The rows column counts rows, not bytes.
- **Allocations and time:** `counts` mode reports `GC.GetTotalAllocatedBytes(true)`
  for one warm execution, and the median of 21 executions after 60 warm-ups.
  `bench` mode reports BenchmarkDotNet `[MemoryDiagnoser]` means.
- **Per run:** EF creates a new `DbContext` per execution, which is typical usage.
  The `Repo` is reused.

## Results

### Commands and rows transferred (`counts`)

| Workload | Variant | Commands | Rows transferred |
|---|---|---:|---:|
| page 20 | Plasm | 3 | 280 |
| page 20 | EF single query | 1 | 600 |
| page 20 | EF split query | 3 | 280 |
| page 100 | Plasm | 3 | 1,400 |
| page 100 | EF single query | 1 | 3,000 |
| page 100 | EF split query | 3 | 1,400 |
| page 1000 | Plasm | 3 | 14,000 |
| page 1000 | EF single query | 1 | 30,000 |
| page 1000 | EF split query | 3 | 14,000 |
| siblings | Plasm (either variant) | 3 | 21 |
| siblings | EF single query | 1 | **100** (cartesian) |
| siblings | EF split query | 3 | 21 |

Plasm and EF split query now read the same rows in the same number of commands.
EF single query multiplies lines by succeeded payments per order (10 × 3 = 30
rows per order), and in the siblings case the full 10 × 10 cross product.

### Time and allocations after the convention fix (`counts`, commit dc6832f)

Re-measured after scalar conventions were compiled once per type and the row
reader stopped allocating a list per field (findings 1 and 2 below). Medians
from `counts` mode on the same shared machine; the BenchmarkDotNet run below
was not repeated.

| Workload | Plasm | EF single | EF split |
|---|---:|---:|---:|
| page 20 | 1.98 ms / 242 KB | 3.55 ms / 509 KB | 4.08 ms / 247 KB |
| page 100 | 3.91 ms / 935 KB | 18.13 ms / 2,216 KB | 12.36 ms / 815 KB |
| page 1000 | 14.54 ms / 8,781 KB | 66.30 ms / 21,409 KB | 21.03 ms / 7,670 KB |
| siblings, built per call | 0.69 ms / 342 KB | 0.39 ms / 133 KB | 0.45 ms / 97 KB |
| siblings, prepared | 0.38 ms / 74 KB | | |

At 1,000 orders Plasm now allocates about 15% more than EF split query and is
faster on this run, against 4× slower and 9× more allocation before the fix.

### Time and allocations before the convention fix (BenchmarkDotNet, mean of 10 iterations)

| Workload | Variant | Mean | Allocated | vs Plasm (time) |
|---|---|---:|---:|---:|
| page 20 | Plasm | 3.20 ms | 1,443 KB | 1.00 |
| page 20 | EF single query | 2.59 ms | 502 KB | 0.81 |
| page 20 | EF split query | 3.58 ms | 240 KB | 1.12 |
| page 100 | Plasm | 11.10 ms | 6,951 KB | 1.00 |
| page 100 | EF single query | 15.06 ms | 2,210 KB | 1.36 |
| page 100 | EF split query | 10.51 ms | 808 KB | 0.95 |
| page 1000 | Plasm | 107.77 ms | 69,043 KB | 1.00 |
| page 1000 | EF single query | 80.14 ms | 21,408 KB | 0.74 |
| page 1000 | EF split query | 24.98 ms | 7,668 KB | 0.23 |
| siblings | Plasm, queries built per call | 719 µs | 406 KB | 1.00 |
| siblings | Plasm, prepared queries | 510 µs | 138 KB | 0.71 |
| siblings | EF single query | 490 µs | 128 KB | 0.68 |
| siblings | EF split query | 605 µs | 92 KB | 0.84 |

`counts` medians from the same day agree on allocations to within 2%:

| Workload | Plasm | EF single | EF split |
|---|---:|---:|---:|
| page 20 | 5.96 ms / 1,466 KB | 3.69 ms / 510 KB | 4.09 ms / 248 KB |
| page 100 | 24.70 ms / 7,020 KB | 17.17 ms / 2,216 KB | 10.66 ms / 815 KB |
| page 1000 | 118.93 ms / 69,040 KB | 87.40 ms / 21,410 KB | 22.83 ms / 7,669 KB |
| siblings, built per call | 0.80 ms / 418 KB | 0.57 ms / 133 KB | 0.56 ms / 97 KB |
| siblings, prepared | 0.48 ms / 149 KB | | |

`profile` mode, page of 1,000: Plasm's three commands took about 3, 100 and 10
ms (orders, lines, payments), against about 14 ms for reading the same 10,000
line rows with raw Npgsql (`GetFieldValue` per column, no materialisation).

### How each variant restricts child queries

Plasm sends the selected order keys as one array parameter, so each child
query reads only those orders' rows and the page is not evaluated again:

```sql
SELECT "t0"."id" AS "id", "t0"."order_id" AS "order_id", ... FROM "order_lines" AS "t0"
WHERE ("t0"."order_id" = ANY(@p0)) ORDER BY "t0"."id" ASC
SELECT ... FROM "payments" AS "t0"
WHERE (("t0"."order_id" = ANY(@p0)) AND ("t0"."status" = @p1))
ORDER BY "t0"."created_at" ASC
```

EF split query restricts each child query by running the parent query again
(filter, `ORDER BY` and `LIMIT`) as a derived table and joining to it:

```sql
SELECT o0.id, o0.order_id, o0.product_id, o0.quantity, o0.unit_price, o1.id
FROM (SELECT o.id, o.placed_at FROM orders AS o
      WHERE o.status = 'published' ORDER BY o.placed_at DESC, o.id LIMIT @p) AS o1
INNER JOIN order_lines AS o0 ON o1.id = o0.order_id
ORDER BY o1.placed_at DESC, o1.id
```

The database evaluates the parent filter, sort and limit once per collection,
and the results can differ if the data changes between commands. Plasm's key
lists cannot drift that way, though it still needs `ReadConsistency.Snapshot`
to see one database state across commands.

## Interpretation

- **Bounded command count.** Plasm used 3 commands for 20, 100 and 1,000
  orders, like EF split query. EF single query always uses 1 command, but its
  rows grow with the product of the collection sizes.
- **No cartesian product.** On the siblings workload, Plasm and EF split query
  each transfer 21 rows; EF single query transfers 100.
- **Materialisation was Plasm's bottleneck, and is fixed.** Before the fix,
  with the same rows, Plasm was about 4× slower than EF split query at 1,000
  orders and allocated about 9× more, all of it per row read. After it, Plasm
  allocates about 15% more than EF split query (findings 1 and 2).
- **Prepared queries** cut the per-call cost of the siblings read from 719 µs
  and 406 KB to 510 µs and 138 KB, level with EF single query.

## Plasm performance findings

1. **Scalars read by convention were slow.** *Fixed in dc6832f*: each
   conversion is compiled to a delegate once per type, and
   `tests/Plasm.Tests/ConventionCost.fs` checks that a value read by convention
   allocates no more than one read through a registered mapping. The test domain registers
   no scalar mappings; its ids, `Quantity` and statuses are stored by
   convention. As a diagnostic, the page workload was run once more with the
   same schemas and registered mappings (`Scalar.wrap` for the ids,
   `Scalar.tryWrap Quantity.create`, `Scalar.enum` for the statuses). This was
   not committed, because registered mappings are not the one style.
   `counts` mode:

   | Page | By convention | Registered mappings | EF split query |
   |---|---:|---:|---:|
   | 20 | 5.93 ms / 1,466 KB | 2.11 ms / 467 KB | 4.05 ms / 248 KB |
   | 100 | 17.03 ms / 7,020 KB | 3.44 ms / 2,056 KB | 10.30 ms / 813 KB |
   | 1000 | 111.61 ms / 69,040 KB | 23.23 ms / 20,063 KB | 23.49 ms / 7,663 KB |

   With registered mappings Plasm matches EF split query on time at 1,000
   orders. The convention mappings in `src/Plasm/Model.fs` (`Conventions`)
   convert each value by reflection. A type read through its `create` calls
   `MethodInfo.Invoke` and then `FSharpValue.GetUnionFields` on the `Result`,
   for every value. A single-case union is built through
   `PreComputeUnionConstructor`, which allocates an argument array per value.
   Compiling these conversions to delegates once per type should bring the
   convention numbers to the registered ones.
2. **Per-field allocation in the row reader.** *Fixed in dc6832f*: a field
   stored in one column is read directly; only a union across columns builds a
   list. `Execution.readEntity`
   builds an F# list of column values for every field of every row, which is
   needed only for unions stored across columns. Even with registered mappings,
   Plasm allocates 2.6× more than EF split query at 1,000 orders.
3. **Building and planning a query per call.** Measured 2026-11-12 and not
   re-measured: building a query value costs about 80 µs and 60 KB and planning
   it about 60 µs and 40 KB. `Query.prepare` removes both from the per-call
   path. `profile` mode measured `Explain` (plan and render) at about 55–60 µs
   and 21 KB per call on this run.

## Limitations

- One machine, shared with other work during this run, and a local container
  over loopback. Network latency would favour fewer round trips (EF single
  query) more than it does here.
- Warm caches throughout: the dataset fits in `shared_buffers`, and connections
  are pooled. No cold-start numbers are reported.
- The EF provider is a release candidate (11.0.0-rc.3).
- EF uses `AsNoTracking`. Tracking queries and `AsNoTrackingWithIdentityResolution`
  were not measured. A new `DbContext` is created for each execution;
  `DbContextPool` was not used.
- Each variant has a single, sequential client. There is no concurrency or
  throughput testing.
- The "Rows transferred" column counts rows, not bytes or column widths.
- BenchmarkDotNet ran in-process (`InProcessEmitToolchain`) with 10 iterations,
  once.
