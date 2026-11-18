# Native AOT

Plasm is being made to run in a native binary (`PublishAot`). This page says
what works, how it works, and how it is checked. It is work in progress: the
check below covers a small program, and grows a step at a time.

## How a native binary reads F# types

A native binary has no reflection over F# unions: the attributes FSharp.Core
reads to find a union's cases and fields are not in it, and it cannot build a
generic type over a value type (`'T option` of a `DateOnly`, `'T list`) that
it did not compile. So the shapes of the application's F# types reach Plasm as
code:

- **Plasm's build step** (`src/Plasm/build/Plasm.targets`, running
  `src/Plasm.Build`) runs before an application compiles. It reads, from their
  metadata, the assemblies the application references that use
  Plasm.Annotations (its domain), and writes `Plasm.Shapes.g.fs` into the
  application's `obj` folder, compiled first: for each public record its
  fields, a constructor and a reader; for each public union its cases, a tag
  reader, and each case's constructor and reader; `'T option` and `'T list` for
  each record and each element type a field holds. Nothing is written per
  record by hand: the record is the only place its shape is stated.
- **Where there is no build step** (F# Interactive, the koans, the browser
  course), shapes are read by reflection, as before. Both supply one interface
  (`src/Plasm/Shapes.fs`). Under the runtime a generated shape is used when
  there is one.
- **A native binary without generated shapes** fails when the model is built,
  naming the record or union and this page.
- **Where the build step runs:** in a project that imports
  `src/Plasm/build/Plasm.targets`, which today is `tests/Plasm.Aot` alone; it
  builds `src/Plasm.Build` itself. Kanban, the benchmarks and the test hosts do
  not import it and are built as before. In the package (not done yet) the
  targets would apply to every executable that references Plasm, and the tool
  would ship in its `tools/` folder; what that costs a build is to be measured
  then.

## How a native binary keeps what queries name

A query is a quotation, read back at run time, and every type and method it
names must survive trimming. The domain's records come with their shapes.
FSharp.Core's operators and modules, `String`'s methods and Plasm's own query
functions are kept by a trimming descriptor embedded in Plasm.dll
(`src/Plasm/ILLink.Descriptors.xml`): the members the translator recognises.

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
the version Plasm references) and 11.0.0.0, a unit test pins it
(`tests/Plasm.Tests/TrimDescriptor.fs`), and a FSharp.Core without it fails
with its version and the part that is missing.

The build step also keeps the application's assembly and its domain's whole in
a native binary (`TrimmerRootAssembly`): a query names their members by
reflection, and a function only a query calls has nothing else referring to it.

## The check

`tests/Plasm.Aot` is a program over a small domain (`tests/Plasm.Aot.Domain`,
which references only Plasm.Annotations). `run.sh` runs it under the runtime
and as a native binary against one PostgreSQL, and compares what they print:

```sh
tests/Plasm.Aot/run.sh
```

It runs from a clean clone. It uses `PLASM_TEST_PG` (an admin connection
string) when it is set, and otherwise starts `postgres:18.4` in Docker for the
run. It takes about 40 seconds on an Apple M4 Max, most of it the native
publish. The solution builds its projects but does not run it, and neither
does the acceptance suite. `PLASM_AOT_TRACE=1` adds the stack
trace of a step that throws (to standard error, not compared).

What it does today:

| Step | Runtime | Native |
|---|---|---|
| Build the model of a record with a typed id and a unique field | ✓ | ✓ |
| Insert one | ✓ | ✓ |
| Read it by its typed id | ✓ | ✓ |
| Read through a `[<ReflectedDefinition>]` member of the domain | ✓ | ✓ |
| Read through a `[<ReflectedDefinition>]` function of the application | ✓ | ✓ |

The publish still reports IL trimming and AOT warnings; they become errors
once the program covers the shop domain.
