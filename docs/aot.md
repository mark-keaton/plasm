# Native AOT

Plasm is being made to run in a native binary (`PublishAot`). This page says
what works, how it works, and how it is checked. It is work in progress: the
check below covers a small program, and grows a step at a time.

## How a native binary reads F# types

Plasm reads an application's records and unions by reflection, under the
runtime and in a native binary alike, through one interface
(`src/Plasm/Shapes.fs`). Nothing is generated and the application writes
nothing for it.

- **Records** are read through FSharp.Reflection everywhere: a native binary
  keeps what it reads of a record.
- **Unions** are read through FSharp.Reflection where it sees their fields
  (the runtime, `dotnet run` of a project published natively, F# Interactive,
  the koans, the browser). A native binary keeps no attributes on a union's
  members, and FSharp.Reflection finds no fields there; Plasm then reads a
  union by its compiled structure: its cases and tags from the compiler's
  `Tags` class (or, for one case, its one `NewX` method or static property),
  each case built by `NewX` (or, without fields, read from its static
  property), each field read through the property its `NewX` parameter names
  (`_radius` is `radius`, `item1` is `Item1`) on the case's class, or on the
  union for one case or a struct. A probe decides which, once: whether
  FSharp.Reflection sees the field of a one-case union of Plasm's own. The
  check below asserts which reader each run used.
- **The compiled layout is the F# compiler's**, so it is pinned: a unit test
  (`tests/Plasm.Tests/Shapes.fs`) reads, by structure and by FSharp.Reflection,
  a union of every layout the compiler produces (one case with one field and
  with several, fieldless unions of two to nine cases, mixed cases, named and
  unnamed fields, struct unions, `[<RequireQualifiedAccess>]`, generic unions,
  a private representation, cases needing backticks, `UseNullAsTrueValue`,
  `option` and `Result`) and every union of the test domain, the koan shop and
  Kanban's domain, and requires the same cases, tags, fields, privacy and
  values. A layout the structural reader does not understand is an error naming
  the type when the model is built, never a shape guessed.
  `PLASM_SHAPES=structure` makes the runtime read by structure too; the unit
  and acceptance suites pass so.
- **Options and lists of value types** (`int64 option`, `DateOnly option`,
  `Guid list`) are written out in Plasm for the provider's own value types: a
  native binary has no code for one made at run time. Of a reference type
  (`CustomerId option`, a fieldless union, a record) they are made by
  reflection. An option or a list of the application's own struct is an error
  naming it when the model is built.

## How a native binary keeps what Plasm reads

Reflection reads only what a native binary keeps:

- **The application and its domain** are kept whole. Plasm's targets
  (`src/Plasm/build/Plasm.targets`) add the application's assembly and every
  project reference's (but Plasm's own) to `TrimmerRootAssembly`: a query names
  their members, and a `[<ReflectedDefinition>]` function only a query calls
  has nothing else referring to it. A domain referenced as a package rather
  than a project needs a `TrimmerRootAssembly` of its own. The targets change
  nothing in a build that does not trim. Today `tests/Plasm.Aot` imports them;
  in the package (not done yet) they would come with Plasm.
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

## Known: a quotation that reads a union case's field

A query, or a `[<ReflectedDefinition>]` it uses, that reads a union case's
field (Kanban's `Card.Assignee`: `match c.Stage with Doing(who, _) -> Some who`)
cannot be read in a native binary yet. FSharp.Core's quotation decoder takes a
case's field from `UnionCaseInfo.GetFields()`, which is empty there, and fails
with `Not a valid F# union case index`. Plasm reports it at start-up and when
the query runs, naming the query or member.

The cause: the F# compiler emits a union case field's getter without the
`SpecialName` flag a record field's getter has, and a native binary keeps no
attributes on a property whose getter lacks it, so FSharp.Reflection finds no
fields. Marking those getters `SpecialName` in the compiled assembly, and
nothing else, makes the attributes, FSharp.Reflection and FSharp.Core's decoder
work in a native binary (shown in a spike). What to do about it is not decided.

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
the two differ, or when a run did not use the reader it should; until the
known rows below are solved, it fails on them. It prints the
native binary's size. `PLASM_AOT_TRACE=1` adds the stack trace of a step that
throws (to standard error, not compared).

What it does today (native binary 24 MB, Plasm.dll kept whole):

| Step | Runtime | Native |
|---|---|---|
| Unions read through FSharp.Reflection / by compiled structure | ✓ | ✓ |
| Build the model of a record with a typed id and a unique field | ✓ | ✓ |
| Insert one | ✓ | ✓ |
| Read it by its typed id | ✓ | ✓ |
| Read through a `[<ReflectedDefinition>]` member of the domain | ✓ | ✓ |
| Read through a `[<ReflectedDefinition>]` function of the application | ✓ | ✓ |
| Insert the application's own record: a union with fields, an `int64 option`, a `DateOnly option`, an option of a fieldless union | ✓ | ✓ |
| Read it by its case | ✓ | ✓ |
| Select a row record the application declares | ✓ | ✓ |
| An API contract (a record of functions) beside them, which nothing stores | ✓ | ✓ |
| Start, checking every query value of the application | ✓ | known: two read a case's field |
| A prepared query by typed id | ✓ | ✓ |
| A prepared query through a reflected member over a case's field | ✓ | known |
| `List.exists` over owned rows, and over a JSON list | ✓ | ✓ |
| Loads bound with `let!` and `and!`, with `take` | ✓ | ✓ |
| A join selecting a record, through a reflected member over a case's field | ✓ | known |
| A prepared page, and a count | ✓ | ✓ |
| Read, decide and update in one transaction, adding an owned row | ✓ | ✓ |
| Insert what is missing (`Bulk.insertMissing`) | ✓ | ✓ |
| A broken constraint's message | ✓ | ✓ |
| No IL warning naming Plasm | | not yet |
