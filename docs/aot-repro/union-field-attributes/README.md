# F# union case fields lose their attributes in a native binary

A reproduction with no library in it: an F# union, a record and a quotation,
published with `PublishAot` (.NET 11, FSharp.Core as the SDK references it).

```sh
docs/aot-repro/union-field-attributes/run.sh
```

What it shows, under the runtime, as a native binary, and as a native binary
of the same IL with one flag changed:

| | runtime | native | native, getters `SpecialName` |
|---|---|---|---|
| attributes on `Rect.h` (a union case field) | 3 | 0 | 2 |
| attributes on `Id.Item` (a one-case union's field) | 3 | 0 | 2 |
| attributes on `Rec.Name` (a record field) | 1 | 1 | 1 |
| `FSharpType.GetUnionCases` fields | Circle/1, Rect/2 | Circle/0, Rect/0 | Circle/1, Rect/2 |
| a quotation reading a case's field | decoded | `ArgumentException: Not a valid F# union case index` | decoded |

The F# compiler emits a union case field's getter without the `SpecialName`
flag, which a record field's getter has (the run prints it). A native binary
keeps no custom attributes on a property whose getter lacks it, so
`CompilationMappingAttribute` is gone from those properties, FSharp.Reflection
finds no fields, and FSharp.Core's quotation decoder, which takes a case's
field from `UnionCaseInfo.GetFields()`, fails on any quotation that reads one.
`patch.fsx` sets `SpecialName` on those getters in the compiled assembly and
changes nothing else; the native binary then behaves as the runtime does.

Ruled out: the attribute set itself (a C# property carrying
`CompilationMapping`, `CompilerGenerated` and `DebuggerNonUserCode` keeps
`CompilationMapping` in a native binary), trimming (the assembly is rooted),
FSharp.Core's `ILLink.LinkAttributes.xml` (only under
`System.AggressiveAttributeTrimming`), and `IlcGenerateCompleteTypeMetadata`.

Plasm works around it (docs/aot.md). Whether to report it to the F# compiler
or to the .NET native compiler is the project owner's decision.
