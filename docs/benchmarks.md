# Graph-load measurements: Plasm vs EF Core

Measured 2026-11-12, before and after the performance fixes described below.
These numbers describe one machine and one dataset; read the limitations before
generalising.

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

BenchmarkDotNet labels the runtime "DEBUG". All five benchmark assemblies were
checked and have `DebuggableAttribute.IsJITOptimizerDisabled = false`. The label
looks like 0.15.8 misreading .NET 11's JIT information.

## Dataset

100 customers, 200 products and 10,000 orders. Two in three orders are `published`,
and `placed_at` rises one minute per order. Each order has 10 lines, whose
products are spread over the 200 products, and 10 payments. A third of the
payments are `succeeded`; the rest are `failed` or `pending`. That gives 100,000
lines and 100,000 payments, with indexes on `order_lines(order_id)` and
`payments(order_id)`. The schema is `tests/Plasm.TestDomain/Database.fs`.

## Workloads

1. **Page.** Published orders ordered by `placed_at desc, id`, taking N = 20,
   100 or 1,000. Each order loads its Lines and each line's Product, plus only
   its succeeded Payments ordered by `created_at`.
2. **Siblings.** Order 1 with all 10 lines and all 10 payments. Two
   collections are loaded side by side to show the cartesian product.

The variants are:

- **Plasm:** `repo.All` with `preload o.Lines linesWithProduct` and
  `preload o.Payments successfulPayments`. The records are immutable; see
  `Workloads.fs`.
- **EF single query:** `AsNoTracking`, `Include(o => o.Lines).ThenInclude(l => l.Product)`,
  and a filtered `Include(o => o.Payments.Where(..).OrderBy(..))`, run with `AsSingleQuery`.
- **EF split query:** the same query with `AsSplitQuery`.

The EF model and queries are written in C#
(`benchmarks/Plasm.Benchmarks.EfModel`). That is EF's idiomatic surface, and it
avoids F#-quotation-to-LINQ translation differences. EF needs mutable classes
with settable navigation collections.

Every run checks that all variants return the same graph (orders, lines and
payments counts). They did in every run.

## Methodology

- **Commands and rows:** for Plasm, `RepoOptions.OnCommand` (`CommandEvent.Rows`).
  For EF, a `DbCommandInterceptor` captures each command. Its rows are counted
  by re-running the captured SQL as `select count(*) from (<sql>)` with the same
  parameters. The rows column counts rows, not bytes; rows are not the same width
  across variants (see below).
- **Allocations and time:** `counts` mode reports `GC.GetTotalAllocatedBytes(true)`
  for one warm execution, and the median of 21 executions after 60 warm-ups
  (enough to reach Tier-1 JIT). `bench` mode reports BenchmarkDotNet
  `[MemoryDiagnoser]` means. The two agree to within about 10%.
- **Per run:** EF creates a new `DbContext` per execution, which is typical usage.
  The `Repo` is reused.

## Results

### Commands and rows transferred

| Workload | Variant | Commands | Rows transferred | Product instances |
|---|---|---:|---:|---:|
| page 20 | Plasm | 4 | 446 | 166 |
| page 20 | EF single query | 1 | 600 | 200 |
| page 20 | EF split query | 3 | 280 | 200 |
| page 100 | Plasm | 4 | 1,600 | 200 |
| page 100 | EF single query | 1 | 3,000 | 1,000 |
| page 100 | EF split query | 3 | 1,400 | 1,000 |
| page 1000 | Plasm | 4 | 14,200 | 200 |
| page 1000 | EF single query | 1 | 30,000 | 10,000 |
| page 1000 | EF split query | 3 | 14,000 | 10,000 |
| siblings (10 lines × 10 payments) | Plasm | 3 | 21 | – |
| siblings | EF single query | 1 | **100** (cartesian) | – |
| siblings | EF split query | 3 | 21 | – |

How to read the row counts:

- **Plasm** sends one command per relationship whatever the parent count: orders,
  lines, products and payments. Its products are fetched once per distinct key
  (166 rows for the page of 20), and each Product record is shared by every line
  that references it.
- **EF split query** joins products into the line query, so it has fewer rows,
  but each line row carries the product columns. That is 200, 1,000 and 10,000
  product copies.
- **EF single query** multiplies lines by succeeded payments per order:
  10 × 3 = 30 rows per order. In the siblings case it transfers the full 10 × 10
  cross product.
- **Product instances:** EF `AsNoTracking` materialises one Product object per
  line. `AsNoTrackingWithIdentityResolution` (not measured) would share them.

### Time and allocations (BenchmarkDotNet, mean of 10 iterations)

Measured after the materialiser and planner fixes listed under "Plasm
performance findings". The figures from before the fixes are kept for
comparison.

| Workload | Variant | Mean | Allocated | vs Plasm (time) | Plasm before fixes |
|---|---|---:|---:|---:|---:|
| page 20 | Plasm | 1.56 ms | 401 KB | 1.00 | 1.66 ms / 690 KB |
| page 20 | EF single query | 2.57 ms | 610 KB | 1.65 | |
| page 20 | EF split query | 4.10 ms | 450 KB | 2.64 | |
| page 100 | Plasm | 2.47 ms | 1,107 KB | 1.00 | 2.50 ms / 1,733 KB |
| page 100 | EF single query | 18.64 ms | 2,739 KB | 7.55 | |
| page 100 | EF split query | 13.31 ms | 1,836 KB | 5.39 | |
| page 1000 | Plasm | 16.51 ms | 8,751 KB | 1.00 | 18.38 ms / 13,094 KB |
| page 1000 | EF single query | 76.17 ms | 26,681 KB | 4.61 | |
| page 1000 | EF split query | 34.82 ms | 18,230 KB | 2.11 | |
| siblings | Plasm | 435 µs | 206 KB | 1.00 | 470 µs / 225 KB |
| siblings | EF single query | 411 µs | 128 KB | 0.94 | |
| siblings | EF split query | 449 µs | 91 KB | 1.03 | |

The page benchmarks reuse one query value, so Plasm's cached plan applies. The
siblings benchmark builds a new query on every call (`plasmSiblings id`), so
it pays for building the query value and planning it each time. The two cases
measure the warm path and the cold path respectively. Measured in isolation
(Release, the same machine, siblings-shaped query with two preloads):

| Per call | Time | Allocated |
|---|---:|---:|
| Building the query value (F# materialises the `ReflectedDefinition` quotations at each call site) | ~79 µs | ~63 KB |
| Planning a new query value (translation and load planning) | ~62 µs | ~38 KB |
| Reusing a planned query value | ~0 | ~0 |

**Prepared queries.** `Query.prepare` removes both costs from the per-call
path: the query is built once and planned once, and each call binds the
argument. A rerun of the siblings benchmark with a prepared variant
(`plasmSiblingsPrepared`, the same query; BenchmarkDotNet, one run, same
machine):

| Siblings, per call | Mean | Allocated | vs rebuilt Plasm |
|---|---:|---:|---:|
| Plasm, query rebuilt per call | 474 µs | 207 KB | 1.00 |
| Plasm, prepared | **392 µs** | 108 KB | 0.83 |
| EF single query | 415 µs | 128 KB | 0.88 |
| EF split query | 453 µs | **91 KB** | 0.96 |

The rebuilt variant measured 435 µs in the earlier run (table above), so the
difference between runs is about ±10%. The prepared query is the fastest
variant in both runs. It allocates less than EF single query, but more than EF
split query.

`counts` mode medians after the fixes, using the same single-pass harness:

| Workload | Plasm | EF single | EF split |
|---|---:|---:|---:|
| page 20 | 2.71 ms / 419 KB | 3.90 ms / 633 KB | 4.42 ms / 474 KB |
| page 100 | 4.67 ms / 1,141 KB | 25.78 ms / 2,817 KB | 12.35 ms / 1,913 KB |
| page 1000 | 14.48 ms / 8,768 KB | 84.48 ms / 27,339 KB | 32.80 ms / 18,891 KB |
| siblings (query reused) | 0.36 ms / 110 KB | 0.38 ms / 133 KB | 0.46 ms / 97 KB |

### How each variant restricts child queries

Plasm sends the selected parent keys as one array parameter, so each child
query reads only those parents' rows and the parent page is not evaluated again:

```sql
SELECT "t0"."id", "t0"."order_id", ... FROM "order_lines" AS "t0"
WHERE ("t0"."order_id" = ANY(@p0)) ORDER BY "t0"."id" ASC
SELECT ... FROM "payments" AS "t0"
WHERE (("t0"."order_id" = ANY(@p0)) AND ("t0"."status" = @p1))
ORDER BY "t0"."created_at" ASC, "t0"."id" ASC
```

EF split query restricts each child query by running the parent query again
(filter, `ORDER BY` and `LIMIT`) as a derived table and joining to it:

```sql
SELECT s.id, s.order_id, ..., s.id0, s.name, s.sku, o1.id
FROM (SELECT o.id, o.placed_at FROM orders AS o
      WHERE o.status = 'published' ORDER BY o.placed_at DESC, o.id LIMIT @p) AS o1
INNER JOIN (SELECT o0.id, ..., p.id AS id0, p.name, p.sku
            FROM order_lines AS o0 INNER JOIN products AS p ON o0.product_id = p.id) AS s
        ON o1.id = s.order_id
ORDER BY o1.placed_at DESC, o1.id
```

This keeps EF's split queries consistent without sending keys. The cost is that
the database evaluates the parent filter, sort and limit once per collection.
The results can also differ if the data changes between commands. Plasm's key
lists cannot drift that way: children always belong to the parents that were
returned. Plasm still needs `ReadConsistency.Snapshot` to see one database state
across commands.

## Interpretation

- **Bounded command count.** Plasm used 4 commands for 20, 100 and 1,000
  parents. EF split query also has a constant count (3). EF single query always
  uses 1 command, but its rows grow with the product of the collection sizes.
- **No cartesian product.** On the sibling workload, Plasm and EF split query
  each transfer 21 rows; EF single query transfers 100.
- **Larger pages.** At 1,000 parents Plasm was 2.1× faster than EF split
  query and 4.6× faster than EF single query, and allocated 52% and 67% less.
  Most of the saving comes from fetching each distinct product once and sharing
  its instance, and from not re-running the parent query.
- **Small loads.** For one order with 21 rows:
  - With the query value reused, Plasm matches EF on time and allocates between
    EF's two variants (110 KB, against 97–133 KB).
  - When the query is rebuilt per call, Plasm is about 6% slower than EF single
    query and allocates 1.6–2.3× more. Almost all of the difference is building
    and planning the quotation-based query value (see the table above).
  - EF caches its compiled LINQ query by expression shape. Plasm caches only
    per query value.

## Plasm performance findings

Found during the first measurement run. Items 1 and 2 are now fixed; item 3 is open.

1. **Planning was redone on every execution.** *Fixed.* Plans are cached per
   repo, keyed by the immutable query value. A plan is not reused if translation
   evaluated a volatile closed expression, such as `DateTime.UtcNow`.
   - Load filters and orderings are translated once per plan, not once per key
     batch.
   - Load signatures are rendered only when a relationship is requested more
     than once.
   - The validation-only dry run is gone.
2. **Per-row materialisation overhead.** *Fixed in part.*
   - Records are built and decomposed by compiled expression-tree delegates.
   - Column readers are resolved once per schema.
   - Option wrap/unwrap and list builders are precomputed when the model is
     built.
   - Still open: `ReadAsync` is awaited for every row.
   - Allocation at 1,000 parents fell from 13.1 MB to 8.8 MB.
3. **Building and planning the query value per call.** *Open.* A query built
   inside a function on every request costs about 140 µs and 100 KB before any
   I/O; see the breakdown above. Two possible fixes:
   - A compiled-query form, built once with typed parameter placeholders and
     bound per call, like EF's `CompileQuery`.
   - A structural plan-cache key.

   Until then, queries that take no per-request values can be defined once as
   module-level values. A query parameterised per request (for example by an id)
   is rebuilt on each call and pays this cost. That is correct, just slower.

## Limitations

- One machine and a local container over loopback. Network latency would favour
  fewer round trips (EF single query) more than it does here.
- Warm caches throughout: the dataset fits in `shared_buffers`, and connections
  are pooled. No cold-start numbers are reported.
- The EF provider is a release candidate (11.0.0-rc.3).
- EF uses `AsNoTracking`. Tracking queries and `AsNoTrackingWithIdentityResolution`
  were not measured. A new `DbContext` is created for each execution;
  `DbContextPool` was not used.
- Each variant has a single, sequential client. There is no concurrency or
  throughput testing.
- The "Rows transferred" column counts rows, not bytes or column widths.
- BenchmarkDotNet ran in-process (`InProcessEmitToolchain`) with 10 iterations.
  The 99.9% error margins are 2–8% of the mean, except EF split query at
  1,000 parents (±14%).
