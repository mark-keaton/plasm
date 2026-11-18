# Native AOT

Plasm is being made to run in a native binary (`PublishAot`). This page says
what works, how it works, and how it is checked. It is work in progress: the
check below covers a small program, and grows a step at a time.

## How a native binary reads F# types

Plasm reads an application's records and unions through FSharp.Reflection,
under the runtime and in a native binary alike (`src/Plasm/Shapes.fs`).
Nothing is generated and the application writes nothing for it.

- **Records** keep what FSharp.Reflection reads of them in a native binary.
- **Unions** do too once Plasm's build step has run (see the workaround
  below): without it a native binary loses the attributes on a union case's
  fields, and FSharp.Reflection sees the case without them. Plasm refuses such
  a union when it is first read (when the model is built, for a stored one),
  naming it and its assembly and the step that was not applied, rather than
  build its cases wrong.
- **Options and lists of value types** (`int64 option`, `DateOnly option`,
  `Guid list`) are written out in Plasm for the provider's own value types: a
  native binary has no code for one made at run time. Of a reference type
  (`CustomerId option`, a fieldless union, a record) they are made by
  reflection. An option or a list of the application's own struct is an error
  naming it when the model is built.
- **A typed id** (a one-case union of one field) is wrapped and unwrapped
  without an array per value: by code compiled once under the runtime, by
  method invokers in a native binary.

## How a native binary keeps what Plasm reads

Reflection reads only what a native binary keeps:

- **The application and its domain** are kept whole. Plasm's targets
  (`src/Plasm/build/Plasm.targets`) add the application's assembly and every
  project reference's (but Plasm's own) to `TrimmerRootAssembly`: a query names
  their members, and a `[<ReflectedDefinition>]` function only a query calls
  has nothing else referring to it. A domain referenced as a package rather
  than a project needs a `TrimmerRootAssembly` of its own. The targets change
  nothing in a build that does not trim. In this repository a project imports
  `src/Plasm/build/Plasm.Repository.targets`, and only it: it imports
  `Plasm.targets` and builds the tool `Plasm.targets` runs from the
  repository. Today `tests/Plasm.Aot` does; in the package (not done yet)
  `Plasm.targets` would come with Plasm, and the tool in its `tools/` folder.
- **What queries name** outside the application is kept by a trimming
  descriptor embedded in Plasm.dll (`src/Plasm/ILLink.Descriptors.xml`): a
  query is a quotation, and FSharp.Core reads every member it names back by
  reflection, members nothing compiled calls among them (a prepared query's
  `Arg.Value`, Plasm's builder inside a nested query). It keeps Plasm.dll
  whole; whole, every scalar type Plasm maps (`Scalar.builtIn`: the integers,
  `String`, `Boolean`, `Decimal`, `Double`, `Single`, `Guid`, `DateTime`,
  `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Byte`), since a query
  may call their members; FSharp.Core's operators and modules the translator
  recognises; and the constructor of the format `Raw.fragment` takes. Unit
  tests (`tests/Plasm.Tests/TrimDescriptor.fs`) fail when the translator names
  a type the descriptor does not keep, when a scalar type Plasm maps is not
  kept whole, when Plasm is not, or when a raw fragment's format constructor
  is not kept.
- **What a query names in Plasm** is marked once, `[<Trimming.QuerySurface>]`
  on each type or module whose public members a query names inside its
  quotations: the builder, `Arg`, `Agg`, `Raw`, `exists` and `plasm`,
  `referring` and `referredBy`, `tableOf`. Plasm is kept whole, so nothing
  marked can be trimmed; a unit test
  (`tests/Plasm.Tests/QuerySurface.fs`) reads every query of the check and
  fails when a member a quotation names is not marked, and when a marked
  member is named by no query of the check, unless it is listed there with
  why (`Zero` only). So the binary is what shows each works natively.
- **When something is missing**, FSharp.Core fails as it reads the quotation,
  before Plasm sees it, and names no member: a `TypeInitializationException`
  from the module that holds the query, its innermost exception
  `Could not bind to method`, `Failed to bind property` or
  `Not a valid F# union case index`. `Repo.Start` with `queriesIn` reads every
  query value, and reports one whose module cannot be read, or whose
  definition cannot be, as a problem naming the query and the reason, rather
  than letting it escape from the application's module.

FSharp.Core's `SpecificCall` compares metadata tokens, which a native binary
does not have; Plasm matches FSharp.Core's operators by name instead.

A `[<ReflectedDefinition>]` member or function in a query (`o.Large`, a
function in the application) is read in a native binary too. FSharp.Core keeps
these bodies in a resource of their assembly, and finds one by its member's
metadata token, in `Expr.TryGetReflectedDefinition` and in the table the public
`Expr.RegisterReflectedDefinitions` fills alike, so no public path works
there. Plasm decodes the same resource with FSharp.Core's own decoder, which is
internal (`PatternsModule.u_ReflectedDefinitions`, `SimpleUnpickle.unpickleObj`,
`BindingEnv`, `mkTyparSubst`; kept by the descriptor), and finds a body by its
member's declaring type, name and parameter types. It is used only where
FSharp.Core's lookup fails for want of tokens. Being internal, it may change
in a FSharp.Core release: it is verified with FSharp.Core 8.0.0.0 (8.0.403,
the version Plasm references) and 11.0.0.0, a unit test pins it, and a
FSharp.Core without it fails with its version and the part that is missing.

## What only a quotation names

A query is a quotation, and many of the types and methods in it appear
nowhere else in the application: nothing compiled calls a nested query's
`OrderByDescending<Card, DateTimeOffset>`, builds the tuple
`BoardId * StageKind * int` a `select` makes, or calls a prepared query's
`Arg.Value`. A native binary has no code for a generic instantiation over a
value type that nothing compiled uses, and may have trimmed a member nothing
calls. So the rule: in a native binary, nothing Plasm does to a type or method
that only a quotation names needs that instantiation's compiled code.

- A query written inside a condition or a load is rebuilt from its builder
  calls by reading them into a query definition, never by invoking the
  builder's generic methods.
- A tuple a query selects is made uninitialised and its fields are set, with
  `System.Tuple`'s and `System.ValueTuple`'s fields kept by the descriptor,
  never through its constructor. Those fields' names (`m_Item1`..`m_Item7` and
  `m_Rest`; a struct tuple's `Item1`..`Item7` and `Rest`) are the framework's
  own, so like FSharp.Core's decoder below they are verified per version, here
  .NET 11.0: a unit test (`tests/Plasm.Tests/Tuples.fs`) makes every arity from
  1 to 15, plain and struct, with a value type in every position, and requires
  the value F# makes; a .NET without a field fails naming its version and the
  field.
- What a quotation names in Plasm, FSharp.Core's operators, modules and
  `LanguagePrimitives` (an aggregate's `GenericZero`), and the scalar types
  Plasm maps, is kept by the descriptor.

- What a condition reads of no row is computed by Plasm's own evaluator
  (`src/Plasm/Computed.fs`), never by FSharp.Core's, which needs metadata
  tokens a native binary lacks; it decides by the expression's form, so the
  runtime accepts and refuses exactly what a native binary does
  (query-semantics.feature). It reads values and their properties and fields,
  builds records, unions, tuples, options and lists, computes arithmetic,
  negation, `abs` and conversions over numbers itself (F#'s operators are
  inline and generic), and calls functions and constructors. A function,
  constructor or static member generic over a value type it refuses, naming
  it, with "bind its value outside the query"; so are lambdas, `if`, `let` and
  arrays. Before this, a query computing `int64 (n.Value * 3)` or
  `since.AddDays(float n)` was refused natively only.
- A message formats a value with F#'s printf only over `obj` (`%A{box r}`, a
  transaction's rejection): printf has no native code for a value type it
  captures, as an `int` rejection, and over `obj` it prints the same; an
  expression's values are described without printf natively (`Translate.show`).
  A key is compared with its type's zero made uninitialised, not by a
  constructor.

The check has a row for each kind with a value type in it: a nested query
ordered by a date, a grouping by an int, tuple and record selects with an int,
an aggregate over decimal, a captured `Guid list`, an option of a value type
compared with `Some`, and a prepared argument of a value type.

## A workaround: F# union case fields in a native binary

The F# compiler emits a union case field's getter without the `SpecialName`
flag a record field's getter has, and a native binary keeps no attributes on a
property whose getter lacks it. Without them FSharp.Reflection finds no fields
on a union's cases, and FSharp.Core cannot read a quotation that reads one (a
query that matches a case and takes its field, Kanban's `Card.Assignee`): it
fails with `Not a valid F# union case index`. The reproduction, with no Plasm
in it: `docs/aot-repro/union-field-attributes`.

Plasm's targets work around it in a native publish only. Before the native
compiler reads them, `src/Plasm.FieldGetters` writes into `obj/plasm-aot/`
copies of every F# assembly the binary is made of (the application's, its
domain's, Plasm's), with `SpecialName` set on the getter of each F# field
property that lacks it, and the native compiler reads the copies. It sets two
bytes of a MethodDef row in place, so no table grows and nothing moves, with
the framework's System.Reflection.Metadata and nothing else; nothing outside
`obj` is touched, and a build that does not compile natively is unchanged. A
patched copy is no longer strong-name signed, which .NET does not check and
the native compiler does not need. It is removed when the F# compiler marks
those getters itself: the reproduction's native run then shows the fields
without it.

With it, a native binary reads unions through FSharp.Reflection as the runtime
does; the check asserts that the publish marked getters.

## Trimming warnings

Plasm reads an application's F# types by reflection by design, so the
trimming and AOT analysis warns about that reflection; no warning can be
removed by removing it. What makes it safe is what keeps its targets in the
binary: the rooted application and domain assemblies (`Plasm.targets`), the
descriptor and its unit tests, the step for F# union fields and its refusal,
and the check below, which runs it all as a binary.

So each warning is suppressed in `src/Plasm/Trimming.fs`, one
`UnconditionalSuppressMessage` per module and warning code, each giving what
keeps that reflection safe; never a project-wide `NoWarn`. The scope is the
module because an F# closure is a type of its own, nested in its module, which
no suppression on its function reaches, and its name (`parts@320`) changes
with every edit above it.

A suppression per module would also hide a new reflective call added to that
module later, so the list is kept honest by `tests/Plasm.Aot/trim-audit.sh`:
it publishes the check with Plasm compiled without the suppressions
(`PlasmTrimAudit=true`) and fails unless the (module, warning) pairs that warn
are exactly those suppressed, in either direction. It takes as long as the
check's publish, so it runs when Plasm's reflection changes, not with every
check; a new reflective call is then justified by name or made safe.

What an application's native publish shows: no warning naming Plasm (the check
fails if it does), and two lines from FSharp.Core, which is not annotated for
trimming:

    FSharp.Core.dll : warning IL2104: Assembly 'FSharp.Core' produced trim warnings.
    FSharp.Core.dll : warning IL3053: Assembly 'FSharp.Core' produced AOT analysis warnings.

They are not Plasm's: any F# program that uses a quotation or `printfn`
gets them, without Plasm. Plasm's targets do not quiet them, because neither
code can be quieted for one assembly: a `NoWarn` or `WarningsNotAsErrors` set
by a package would also silence the same summary for every other assembly the
application uses. An application that publishes with warnings as errors sets
it itself:

    <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2104;IL3053</WarningsNotAsErrors>

## Cold start and size

`tests/Plasm.Aot/coldstart.sh` runs the check program's `coldstart` mode as
fresh processes: build the model, start the repository, run one prepared
query, then it and another again, each phase timed. Medians of 11 runs on
osx-arm64 (Apple silicon) against PostgreSQL on localhost, so the connection
figures are a floor; 16 query values; milliseconds:

| Phase | runtime, queries checked | runtime, not checked | native, checked | native, not checked |
|---|---:|---:|---:|---:|
| Process start to `main` | 20.9 | 20.8 | 8.5 | 8.4 |
| Npgsql's first connection | 84.2 | 83.5 | 12.0 | 12.0 |
| Model build | 48.2 | 48.7 | 3.4 | 3.4 |
| `Repo.Start` | 92.7 | 2.1 | 9.0 | 0.1 |
| First prepared query (opens Plasm's pool) | 71.1 | 134.6 | 10.5 | 12.3 |
| The same query again | 1.4 | 1.7 | 0.6 | 0.6 |
| Another query | 1.6 | 4.0 | 0.6 | 0.6 |
| Whole process, measured outside it | 333 | 306 | 48 | 40 |

With `SCHEMA=1` the schema check costs 12 to 16 ms natively, its pool's
first connection included. Natively the query check is about half a
millisecond a query, of which the first query gets back about two by being
translated already.

Sizes of native binaries, osx-arm64, Npgsql 10.0.3, FSharp.Core 8.0.403:

| Program | Size |
|---|---:|
| F# hello world | 1.2 MB |
| ... with one quotation | 3.6 MB |
| Npgsql alone, `NpgsqlDataSource.Create` | 9.4 MB |
| Npgsql alone, `NpgsqlSlimDataSourceBuilder` | 5.1 MB |
| One record through Plasm, either builder | 25.0 MB |
| The check program, either builder | 25.5 MB |

Keeping Plasm.dll whole is about 13.5 MB of that, and makes the two Npgsql
builders the same size. Keeping only its public types instead (what a query
compiled outside Plasm can name) was measured at 20.6 MB for the check, 16.4
MB over the slim builder, and is not done: Plasm's reflection over its own
internals would then rest on the paths the check runs, and function comes
before size.

### On AWS Lambda

A function is a process started cold, so what it pays before its first
answer matters, and its package's size with it:

- **Publish inside Amazon Linux 2023**, the image Lambda's `provided.al2023`
  runs (`public.ecr.aws/amazonlinux/amazonlinux:2023` with
  `dnf install clang gcc zlib-devel` and the SDK from `dotnet-install`): a
  native binary needs the glibc it was linked against, and one published in
  the .NET SDK image needs GLIBC_2.38 and 2.43, where Amazon Linux 2023 has
  2.34, so it does not start. `run-linux.sh` proves Linux, not Lambda's Linux.
- **Plasm takes any data source**, `PostgresProvider(dataSource)`, so a
  function can build its own (pooling, TLS, Npgsql's slim builder). With the
  slim builder, enable what is used: arrays for a captured list, transport
  security for a TLS connection, without which the first connection throws:

  ```fsharp
  let provider =
      PostgresProvider(NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().EnableTransportSecurity().Build())
  ```

  The check, over a local connection without TLS, prints the same with
  `EnableArrays()` alone as with `NpgsqlDataSource.Create`
  (`-p:PlasmAotSlim=true`).
- **Check the queries in a test, not at each cold start**, when there are
  many: natively the check costs about half a millisecond a query (16 queries
  9 ms, of which the first query gets back 2). Start the function with
  `Queries = StartupCheck.Ignore` and run `Repo.Start` with
  `Queries = StartupCheck.Crash` in a test over the same assembly; a few dozen
  queries cost too little to bother.
- **The connection is most of a cold start**: 12 ms to a PostgreSQL on
  localhost, more to a database over the network. The check program opens two
  pools (its own to set up the schema, then Plasm's), so its first query pays a
  second connection an application does not.

### Behind a connection pooler

A function usually reaches PostgreSQL through a pooler in transaction mode
(PgBouncer, RDS Proxy), which hands each transaction whichever server
connection is free, so nothing may rest on the session. What Plasm sends
rests on none:

- **No prepared statement is kept on the server.** Plasm never prepares a
  command, and Npgsql's automatic preparation is off unless the connection
  string turns it on; every statement is sent unnamed. A prepared query is
  planned once in the process, not on the server.
- **A batch is one implicit transaction.** Statements Plasm sends together
  (a write's owned rows, loads bound with `let!` and `and!`) are one `DbBatch`,
  which Npgsql ends with one sync, so a pooler keeps it on one connection.
- **No session state.** No advisory locks, `SET`, temporary tables, cursors,
  `LISTEN` or `set_config`; a snapshot read is a REPEATABLE READ transaction
  alone (`core.feature`). Keys a query reads by are an array parameter, so a
  statement's text grows with the query, not the data.

Npgsql itself resets a pooled connection on reuse with `DISCARD ALL`, which
RDS Proxy pins a connection on and a pooler in transaction mode has no use
for. Set `No Reset On Close=true` in the connection string behind one; Plasm
leaves the connection string to the application.

Measured with the check under the runtime, straight to PostgreSQL 18.4 and
through PgBouncer 1.24.1 in transaction mode with `max_prepared_statements=0`
and two server connections: the output is the same line for line, the server
log shows every statement unnamed, and `No Reset On Close=true` takes
`DISCARD ALL` from 32 a run to 0. RDS Proxy itself was not tested, and whether
it pins on a key a column's default takes from a sequence (`nextval` run by
the server, not sent) is unknown.

## The check

`tests/Plasm.Aot` is a program over a small domain (`tests/Plasm.Aot.Domain`,
which references only Plasm.Annotations) and records and unions of its own,
as an application declares them beside its queries. `run.sh` runs it under the
runtime and as a native binary against one PostgreSQL, and compares what they
print:

```sh
tests/Plasm.Aot/run.sh
```

It runs from a clean clone. It uses `PLASM_TEST_PG` (an admin connection
string) when it is set, and otherwise starts `postgres:18.4` in Docker for the
run. It takes about 40 seconds on an Apple M4 Max, most of it the native
publish. The solution builds its projects but does not run it, and neither
does the acceptance suite. It fails when a step throws under the runtime, when
the two differ, or when the publish marked no getters. A second step publishes
a tiny program without Plasm's step for F# union fields and requires it to
refuse its union by name, assembly and step. It prints the
native binary's size. `PLASM_AOT_TRACE=1` adds the stack trace of a step that
throws (to standard error, not compared).

On Linux, in a container on the same machine, with PostgreSQL in a second
container on a network of their own:

```sh
tests/Plasm.Aot/run-linux.sh       # linux-arm64
tests/Plasm.Aot/run-linux.sh x64   # linux-x64, under emulation on an arm64 machine
```

It builds an image of the .NET SDK with clang and zlib
(`tests/Plasm.Aot/linux/Dockerfile`), copies the repository's files to a
temporary directory the container builds in, so nothing lands in the working
tree, and runs `run.sh` there, every condition the same. Packages are kept in
a Docker volume between runs. Verified, all three with every step identical
to the runtime, no IL warning naming Plasm, 292 getters marked and the
unpatched binary refusing its union:

| Target | Where | Binary | Whole run |
|---|---|---:|---:|
| osx-arm64 | Apple M4 Max | 24 MB | about 40 s |
| linux-arm64 | Ubuntu 26.04 container, .NET SDK 11.0.100 | 27 MB | 58 s |
| linux-x64 | the same, emulated on arm64 | 26 MB | 162 s, packages restored |

Kanban's native spike passes on both Linux targets too, and a function
published inside Amazon Linux 2023 ran on Lambda (arm64, `provided.al2023`)
against Aurora PostgreSQL 18.3: five queries correct, 176 ms of Lambda
start-up then 823 ms to ready (the secret, a TLS connection, the model, the
query check, the first read), 28 ms an invocation warm, a 12.7 MB package. Nothing in Plasm or
its targets changed for Linux: the field-getter step finds and copies the
same assemblies, and paths are matched as they are written. The check sets
`InvariantGlobalization`, so it needs no ICU. Two traps of the copy are
handled in the script: macOS's `tar` writes `._` files for extended
attributes (a `._Plasm.Aot.fsproj` MSBuild takes for a second project), and
the emulated container's `tar` cannot open files at all, so the copy is made
on the host.

What it does today (native binary 24 MB, Plasm.dll kept whole; 292 F# field
getters marked):

| Step | Runtime | Native |
|---|---|---|
| The publish marked F# field getters `SpecialName` (more than none) | needs nothing | ✓ |
| Build the model of a record with a typed id and a unique field | ✓ | ✓ |
| Insert one | ✓ | ✓ |
| Read it by its typed id | ✓ | ✓ |
| Read through a `[<ReflectedDefinition>]` member of the domain | ✓ | ✓ |
| Read through a `[<ReflectedDefinition>]` function of the application | ✓ | ✓ |
| Insert the application's own record: a union with fields, an `int64 option`, a `DateOnly option`, an option of a fieldless union | ✓ | ✓ |
| Read it by its case | ✓ | ✓ |
| Select a row record the application declares | ✓ | ✓ |
| An API contract (a record of functions) beside them, which nothing stores | ✓ | ✓ |
| Start, checking every query value of the application | ✓ | ✓ |
| A prepared query by typed id | ✓ | ✓ |
| A prepared query through a reflected member over a case's field | ✓ | ✓ |
| `List.exists` over owned rows, and over a JSON list | ✓ | ✓ |
| Loads bound with `let!` and `and!`, the first ordered descending by a date with `take 3` (Kanban's `boardsWithCards`) | ✓ | ✓ |
| A join selecting a record, through a reflected member over a case's field | ✓ | ✓ |
| A prepared page, and a count | ✓ | ✓ |
| Read, decide and update in one transaction, adding an owned row | ✓ | ✓ |
| Insert what is missing (`Bulk.insertMissing`) | ✓ | ✓ |
| A broken constraint's message | ✓ | ✓ |
| Grouped by an int | ✓ | ✓ |
| Grouped by a field and a reflected function's result, selecting both and a count (Kanban's `cardCounts`) | ✓ | ✓ |
| A tuple select with an int | ✓ | ✓ |
| A record select with an int | ✓ | ✓ |
| An aggregate over decimal | ✓ | ✓ |
| A captured `Guid list` (`List.contains`) | ✓ | ✓ |
| An option of a value type compared with `Some` | ✓ | ✓ |
| A prepared argument of a value type in `Some` | ✓ | ✓ |
| A raw fragment, `exists`, and loads ordered three ways, paged, and distinct | ✓ | ✓ |
| A load that joins and groups is refused, the same natively | ✓ | ✓ |
| A load of the record a row refers to (`referredBy`) | ✓ | ✓ |
| Every aggregate, selected in a five-tuple | ✓ | ✓ |
| A refused query's message, naming literal values of each kind | ✓ | ✓ |
| A transaction rejected with an `int` | ✓ | ✓ |
| Values a condition computes before it runs: a captured record's and anonymous record's fields, a ref cell, an option's value, arithmetic, `abs`, a conversion, `DateTimeOffset.AddDays`, `DateOnly(...)`, an application function, `take (n * 2)` | ✓ | ✓ |
| A prepared argument computed in the condition (`int64 (n.Value * 3)`) | ✓ | ✓ |
| A binary published without the step for F# union fields refuses its union, naming it, its assembly and the step (`tests/Plasm.Aot.Unpatched`, `PlasmFieldGetters=false`) | | ✓ |
| The publish has no IL warning naming Plasm | | ✓ |
