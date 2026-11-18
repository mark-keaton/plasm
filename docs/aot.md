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
  `Arg.Value`, Plasm's builder inside `Query.prepare`). It keeps Plasm.dll
  whole; whole, every scalar type Plasm maps (`Scalar.builtIn`: the integers,
  `String`, `Boolean`, `Decimal`, `Double`, `Single`, `Guid`, `DateTime`,
  `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Byte`), since a query
  may call their members; and FSharp.Core's operators and modules the
  translator recognises. Unit tests (`tests/Plasm.Tests/TrimDescriptor.fs`)
  fail when the translator names a type the descriptor does not keep, when a
  scalar type Plasm maps is not kept whole, or when Plasm is not.
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

A native publish of the check reports IL trimming and AOT warnings, most of
them from Plasm's reflection. Since Plasm reads by reflection by design, "no
warnings" cannot come from removing it. The end state, not reached yet:

- Plasm's reflection is guaranteed by what keeps its targets: the rooted
  application and domain assemblies, the descriptor and its drift test, and
  the check below.
- Each warning that guarantee covers is suppressed where it arises, on the
  member (`UnconditionalSuppressMessage` with the reason), never by a
  project-wide `NoWarn`.
- An application's own native publish shows no IL warning naming Plasm, and
  the check fails if it does.

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

What it does today (native binary 24 MB, Plasm.dll kept whole; 293 F# field
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
| A binary published without the step for F# union fields refuses its union, naming it, its assembly and the step (`tests/Plasm.Aot.Unpatched`, `PlasmFieldGetters=false`) | | ✓ |
| No IL warning naming Plasm | | not yet |
