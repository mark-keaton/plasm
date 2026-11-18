# A C# host over an F# domain

The domain and the data code stay F#; C# serves them. `examples/Kanban`
proves it with a second host beside its F# one:

- `src/Data` (F#, Kanban.Data): what is stored (`Db.fs`), the queries and
  workflows (`Workflows.fs`), and `KanbanData` (`Facade.fs`), the one type
  C# calls. Both hosts reference it, so each type exists once.
- `src/Host.CSharp` (C#): minimal APIs on the slim builder, with a JSON
  source-generation context, calling `KanbanData` and nothing else of F#.
- `tests/Kanban.CSharpHost.Specs`: `features/csharp/csharp-host.feature`,
  10 scenarios with C# steps, run against the host as its own process over
  HTTP, on a database of their own (`PLASM_TEST_PG`, or a `postgres:18.4`
  container, as Kanban's suite). `KANBAN_CSHARP_HOST` names a program to run
  instead of the built host, such as its native binary; the commands are in
  Kanban's README.

The host was written before its first 9 scenarios. Red was shown by running
them against a stub host that served only `GET /boards`: all 9 failed, each
on the endpoint the stub lacked (`creating board alpha: expected 201 but was
405`). The stage scenarios came before the stage view: against a card whose
stage was a string with a nullable assignee beside it, the 4 that read a
stage failed. Against the host, all 10 pass.

## Where the two meet

One F# type per application, its methods taking plain arguments and a
`CancellationToken` and returning a `Task`:

```csharp
var dataSource = NpgsqlDataSource.Create(connectionString);
var kanban = await KanbanData.OpenAsync(dataSource, CancellationToken.None);
builder.Services.AddSingleton(kanban);

app.MapGet("/boards/{slug}", async (string slug, KanbanData k, CancellationToken ct) =>
    await k.BoardAsync(slug, ct) switch
    {
        BoardView board => Results.Ok(board),
        Missing missing => Results.NotFound(missing),
    });

app.MapPut("/cards/{id:guid}/title", async (Guid id, Rename input, KanbanData k, CancellationToken ct) =>
    await k.RenameAsync(id, input.Version, input.Title, ct) switch
    {
        CardView card => Results.Ok(card),
        Missing missing => Results.NotFound(missing),
        Refused refused => Results.UnprocessableEntity(refused),
        Stale stale => Results.Conflict(stale),
        Invalid invalid => Results.BadRequest(invalid),
    });
```

C# writes no query and composes no transaction. One rule holds for what the
facade returns: nothing is null, an F# option or an F# Result; whatever can
go more than one way is a type of C#'s union shape, declared in F#: a struct
with `[<Union>]`, one constructor per case and `IUnion.Value`, each case a
record of its own.

- **A read that can miss returns `Found<'T>`**: the value, or `Missing`
  (`BoardAsync`). It is generic, since a read has two cases whatever it reads.
- **A write that can be refused returns its own outcome**: `ChangeOutcome`
  (`CardView`, `Missing`, `Refused`, `Stale`, `Invalid`), `AddCardOutcome`
  and `CreateBoardOutcome`, each with only the cases its write can have.
- **A field that holds a different fact in each case is a union-shaped
  type too.** A card's `Stage` is a `StageView`: `InBacklog`, `InProgress`
  (with its assignee and start), `WasShipped` (with its time) or
  `WasDropped` (with its reason and time). No view has a field that is null
  or `Nullable` because of which case its row is in.
- **A database that cannot be reached throws** `KanbanUnavailableException`,
  as ASP.NET Core expects of a failure the request cannot handle.

```csharp
var said = card.Stage switch
{
    InBacklog => "in the backlog",
    InProgress doing => $"{doing.Assignee} since {doing.Since:d}",
    WasShipped shipped => $"shipped {shipped.At:d}",
    WasDropped dropped => $"dropped: {dropped.Reason}",
};
```

C# switches over each with no default arm, and a case left out is CS8509
naming its type (`the pattern 'Kanban.Data.Missing' is not covered`), a
warning this repository makes an error.

Null was tried first and dropped. F# 9 nullness reaches C# through `Task<_>`
(`Task<string | null>` gives CS8602 on a dereference), but an F# record
cannot be `| null`, and with `<Nullable>enable</Nullable>` a record in
`Task<_>` is annotated as never null; without it, C# sees it as oblivious and
warns about nothing.

Opening is asynchronous: starting the repository checks every query and the
tables against the database, so the facade is made by
`KanbanData.OpenAsync(dataSource, ct)`, awaited before the application is
built, and the instance is registered as a singleton. DI cannot construct it
from a data source without blocking on that check. The check scans
Kanban.Data, where the queries are, for the F# server as for the C# host.

The requests the host reads from JSON are C# records declared in the host
(`NewBoard`, `NewCard`, `Start`, `Rename`), and the facade takes their fields
as plain arguments: no F# record is read from JSON, where a field the JSON
lacks would be left null.

## Where C# is awkward against F# types

Each was measured on .NET 11 (SDK 11.0.100, C# 15, FSharp.Core 8.0.403,
System.Text.Json 11.0.0):

| F# | What C# writes | Fix belongs |
|---|---|---|
| `'T option` | `FSharpOption<T>`; `None` is a null reference, and `card.Blocked.Value` on it compiles without a warning and throws NullReferenceException | Facade: `Found<'T>`, or `Nullable<T>` for a value in a view |
| `Result<_, _>` | `FSharpResult<T, E>`: `.IsOk`, `.ResultValue`, `.ErrorValue`, no deconstruction | Facade |
| F# list | Read as `IEnumerable<T>` as it is; built with `ListModule.OfSeq(...)` | Facade: arrays |
| Single-case union id | `CardId.NewCardId(guid)`, `.Item`; a private one (`Slug`) only through `Slug.create`, an `FSharpResult` | Facade: `Guid` and `string` |
| Multi-case union | `error switch { DomainError.WipLimitReached w => w.limit, _ => ... }`: each case is a nested class with F#'s field names; F# emits no union marker C# reads, so the switch needs a discard arm | Facade: a C# union of records |
| `?cancellationToken` | An `FSharpOption<CancellationToken>` parameter C# must pass: `repo.One(q)` does not compile (CS1501); `repo.One(q, cancellationToken: ct)` does, through `FSharpOption`'s implicit conversion; `Transact` needs `FSharpOption<IsolationLevel>.None, FSharpOption<RetryPolicy>.None, ct` | Plasm, only if C# is to call `Repo` itself; nowhere behind a facade |
| A prepared query | An `FSharpFunc` value: `Workflows.Queries.boardBySlug.Invoke(slug)` | Facade |
| `Tx` | `Tx.insert<Board, Unit>(board)`: the error type cannot be inferred, and `transaction { }` is F# only | Nowhere: composing transactions is the F# side's |

Plasm is callable from C# throughout: nothing above needed a change in it.
Behind a facade, C# reaches none of it.

## JSON

What System.Text.Json writes of each: reflection-based with web options, source-generated with a context's default options (hence `Card`, not `card`):

| Value | Reflection-based | Source-generated |
|---|---|---|
| `Some 3` / `None` | `3` / `null` | `{"Value":3}` |
| F# list | `[1,2]` | `[1,2]` |
| `CardId` (public single-case) | `{"$type":"cardId","item":"..."}` | `{"Tag":0,"Item":"..."}` |
| `Slug` (private single-case) | throws `ArgumentException`: its representation is private | `{"Value":"alpha"}` |
| `Stage.Backlog` (a case without fields) | `"backlog"` | not measured |
| `DomainError.WipLimitReached 2` | `{"$type":"wipLimitReached","limit":2}` | not measured |
| A record of the above (`Board`, `Card`) | throws, from its first private union | each field as above |
| The facade's views (records of strings, numbers, arrays, union-shaped fields) | as declared, camelCase | as declared, camelCase |
| The facade's outcome struct | its case: `{"card":"..."}` | its case: `{"Card":"..."}` |
| `StageView`, a union-shaped field | its case: `{"kind":"doing","assignee":"Ada","since":"..."}` | the same, natively too |

System.Text.Json writes a union-shaped type as its case alone, so each case
of `StageView` has a constant `Kind` property, written first. Its source
generator accepts a union-shaped type only if it could read the JSON back,
which the host never does: it refuses `StageView` (SYSLIB1227: every case is
a JSON object) unless the type names a classifier. `StageByKind`, in
Kanban.Data, is one: it reads ahead for `kind` and names the case, and
`[<JsonUnion(TypeClassifier = typeof<StageByKind>)>]` puts it on the type.
The case records stay plain F# records, read through their constructors, so
C# can neither make an empty one (`new InProgress()` is CS7036) nor change
one (CS0200). It works natively, reading each case back as itself, with no
IL warning of its own.

The built-in `JsonUnionTypeStructuralClassifier`, which tells cases apart by
their properties, was tried first and needs more of the records: it refuses
a case whose properties another case also has (`InBacklog`'s `kind` is in
every case), unless each case's fields are `JsonRequired`, which needs
setters (`[<CLIMutable>]`, and with it `new InProgress()` with a null
assignee), and the backlog case refuses unmapped members.

So the views are what a C# host serialises: records of plain types, which
both modes write the same and a native binary needs no reflection for. A
minimal API never writes an outcome itself; C# switches over it first.
Reading JSON into an F# record leaves a field the JSON lacks at null, even
one F# says cannot be, which is why the requests are C# records.

## Native AOT

The C# host publishes natively (`dotnet publish -r osx-arm64`, 47 s, a 33 MB
binary), with FSharp.Core's two summary warnings allowed as for any F#
program (`WarningsNotAsErrors` IL2104 and IL3053, see aot.md) and no other
IL warning. The 10 scenarios pass against the native binary as against the
built host (`KANBAN_CSHARP_HOST`). The C# union switch, the source-generated
JSON of the facade's records and Plasm's start-up check all run natively.
