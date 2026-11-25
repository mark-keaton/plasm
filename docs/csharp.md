# A C# host over an F# domain

The domain and the data code stay F#; C# serves them. With Plasm.CSharp the
build writes the C# side from the F# project, so the C# a host writes is its
endpoints and a few lines saying what to call things. This page describes
Plasm.CSharp as the repository has it, for 0.5.0, which is not released yet:
0.4.0's names differ, and [Upgrading](start/upgrading.md) lists each. It
needs .NET 11 and C# 15, for its unions.

`examples/Kanban` shows it with three C# hosts over one F# project:

- `src/Data` (F#, Kanban.Data): what is stored (`Db.fs`), the queries and
  workflows (`Workflows.fs`), and `Boards` (`Boards.fs`), the one type C#
  calls, with `BoardStore`, which says where its data is. Its methods take
  and return the domain's own records, and its requests are F# records.
- `src/Host.CSharp` (C#): minimal APIs on the slim builder, calling `Boards`
  and nothing else of F#.
- `src/Host` (C#): what an application would write around the same type
  (cookie sign-in, REST, GraphQL), on the runtime. Its review guide is
  [The Kanban example](kanban-host.md).
- `src/Lambda` (C#): a function, published natively
  ([Native AOT and Lambda](aot.md)).
- `tests/Kanban.CSharpHost.Specs`: `features/csharp/csharp-host.feature`,
  with C# steps, run against `src/Host.CSharp` as its own process over HTTP,
  on a database of their own (`PLASM_TEST_PG`, or a `postgres:18.4`
  container, as Kanban's suite). `KANBAN_CSHARP_HOST` names a program to run
  instead of the built host, such as its native binary; the commands are in
  Kanban's README.

Each host has a `Seam.cs` and a committed `seam.lock`.

## Where the two meet

The host registers the type and calls it. Each endpoint is one line:

<!-- from: examples/Kanban/src/Host.CSharp/Program.cs -->
```csharp
builder.Services.AddBoards(connectionString);

var app = builder.Build();
// ...
app.MapGet("/boards/{slug}", (string slug, Boards b, CancellationToken ct) =>
    b.BoardAsync(slug, ct).ToHttp());
// ...
app.MapPut("/cards/{id:guid}/title", (Guid id, Rename input, Boards b, CancellationToken ct) =>
    b.RenameAsync(id, input, ct).ToHttp());
```

The F# member it calls takes the request whole and answers in the domain's
terms, the card and its own refusal:

<!-- from: examples/Kanban/src/Data/Boards.fs -->
```fsharp
member _.RenameAsync(card: CardId, request: Rename, cancellationToken: CancellationToken) : Task<ChangeResult<Card, DomainError>> =
    match field "title" Title.create request.Title with
    | Error problems -> Task.FromResult(ChangeResult<Card, DomainError>(Invalid.fields problems))
    | Ok title -> change card request.Version (fun _ _ c -> Card.rename title c) cancellationToken
```

Between them is what the build writes. `Seam.cs` tells it which F# type C#
calls, and what C# leaves out:

<!-- from: examples/Kanban/src/Host.CSharp/Seam.cs -->
```csharp
[assembly: SeamData(typeof(Boards))]
// ...
[assembly: SeamView(typeof(Card), Omit = [nameof(Card.Board), nameof(Card.Created), nameof(Card.Blocked), nameof(Card.Checklist)])]
```

From that and the compiled F# project, before the C# compile, the build
writes into `Seam/` (ignored by git):

- **a view of each record and union** the methods take or return: `CardView`
  for `Card`, `StageView` for `Stage`. A typed id is its `Guid`, a value
  made by `create` (`Title`) its `string`, a list an array, an option a
  nullable. `Seam.cs` can leave fields out, and name a view or a case;
- **a C# `Boards` over the F# one**, whose methods take the views and the
  requests and answer with Plasm.CSharp's outcomes of views. A refusal of the
  domain's is `Refused`, its code the case's name;
- **its plumbing, from the Store**: `OpenAsync`, `OpenUncheckedAsync`, `Ddl`,
  `SandboxAsync`, and `AddBoards` for ASP.NET Core, which opens it as the
  host starts, before it serves;
- **`SeamJson`**, a source-generated JSON context with every view, request
  and array, and **`ToHttp`**, each outcome as its HTTP result.

`seam.lock`, beside `Seam.cs`, is the contract: what C# is given, one fact a
line. A build that changes it says what changed; a build in CI stops
instead, naming each change. Its format is [seam.lock's format](seam-lock.md).

C# writes no query, no transaction, no view and no mapping. One rule holds
for what `Boards` returns: nothing is null, an F# option or an F# Result;
whatever can go more than one way is a C# union.

The shapes every project would write the same are in the package
**Plasm.CSharp**, which depends on Plasm alone (either provider; an F# host
need not take it). It holds:

- the cases: `Missing { What }`, `Refused { Code; Message }`,
  `Stale { What; Key }` (the record type, and its key's parts as text in key
  order: one for a typed id, one per field of a key of several, so a part
  holding a "/" is still one part), `FieldProblem { Field; Message }` and
  `Invalid { Problems }`;
- the unions of them, each with only the cases its kind of call can have:
  - **a read that can miss returns `FindResult<'T>`**: the value, or `Missing`
    (`BoardAsync`);
  - **an add returns `AddResult<'T>`**: the value, or `Invalid` (`CreateBoardAsync`);
  - **a request about something that must be there, with input to check,
    returns `RequestResult<'T>`**: the value, `Missing` or `Invalid`
    (`AddCardAsync`, `DonePageAsync`);
  - **a change returns `ChangeResult<'T>`**: the value, `Missing`, `Refused`,
    `Stale` or `Invalid`. Every change can be `Invalid`: the database may
    refuse what it writes field by field (a unique or a check constraint);
- `Outcome.found`, `Outcome.added`, `Outcome.answered` and `Outcome.changed`,
  which read a read's or a transaction's result as one of them, holding the
  records themselves. With the seam that is what C# is given, as views.
  Without it, a view is mapped after:
  `Outcome.changed result |> ChangeResult.map View.card |> ChangeResult.refused View.refused`.
  A transaction stops with `Stop<'R>`, `Stop.Missing` or `Stop.Refused` of
  the domain's own refusal, so a project writes no stop union of its own.
  Plasm's failures are read for it: a stale copy is `Stale`, the database's
  refusal of fields `Invalid` (`Invalid.ofErrors`: camel-cased fields, a
  duplicate "is already taken"), and a database that fails throws
  `UnavailableException`;
- `Outcome.all`, for a read of many (`repo.All`): an array of the records, in
  the query's order, and an empty array for none. An array has no case to
  switch over, so it is no union. A read the database fails throws
  `UnavailableException`, as `Outcome.found` does;
- `repo.Change(query, version, rule, ct)`, a change in one call: it reads the
  record as the caller saw it, applies the rule (`'T -> Result<'T, 'R>`),
  writes it and answers a `ChangeResult<'T, 'R>`;
- `Invalid.field` and `Invalid.fields`, for input refused before the
  database, and `ByKind`, the base of a union's JSON classifier (below).

A host that names a union (GraphQL) names each by its use: Kanban's host
registers `ChangeResult<CardView>` as `ChangeOutcome`.

- **A field that holds a different fact in each case is a union too.** A
  card's `Stage` is a `StageView`: `Backlog`, `Doing` (with its assignee and
  start) or `Done` (with its resolution, `Shipped` or `Dropped` with its
  reason, and its time). No view has a field that is null or `Nullable`
  because of which case its row is in.
- **A database that cannot be reached throws** Plasm.CSharp's
  `UnavailableException`, as ASP.NET Core expects of a failure the request
  cannot handle.

```csharp
var said = card.Stage switch
{
    Backlog => "in the backlog",
    Doing doing => $"{doing.Assignee} since {doing.Since:d}",
    Done done => $"done {done.At:d}",
};
```

C# switches over each with no default arm, and a case left out is CS8509
naming its type (`the pattern 'Plasm.CSharp.Missing' is not covered`), a
warning this repository makes an error. It holds for the package's unions as
for a project's own: the package check builds a host over Plasm.CSharp
restored from a feed, and the same switch without its `Missing` arm is that
error (tests/Plasm.Package/seam).

Null was tried first and dropped. F# 9 nullness reaches C# through `Task<_>`
(`Task<string | null>` gives CS8602 on a dereference), but an F# record
cannot be `| null`, and with `<Nullable>enable</Nullable>` a record in
`Task<_>` is annotated as never null; without it, C# sees it as oblivious and
warns about nothing. So Kanban.Data and Kanban's Shared domain set
`<Nullable>enable</Nullable>`, and compile clean against FSharp.Core 8 (this
repository's) and 11 (what a new project gets). What matters is FSharp.Core
9 and later, where a `string` is never null: take raw input that may be null
as `string | null`, read with `match text with null -> "" | t -> t` (an
`if isNull` does not narrow it).

Opening is asynchronous: starting the repository checks every query and the
tables against the database. `AddBoards(connectionString)` registers `Boards`
and opens it as the host starts, so a query that would fail stops the host
before it serves. The check scans the queries' assembly the Store names,
Kanban.Data, for every host alike.

The requests are F# records in Kanban.Data (`NewBoard`, `NewCard`, `Start`,
`Rename` and the others). The seam gives C# a record of the same fields for
each, which the host reads from JSON, and makes the F# one from it: no F#
record is read from JSON, where a field the JSON lacks would be left null.

## Where C# is awkward against F# types

Each was measured on .NET 11 (SDK 11.0.100, C# 15, System.Text.Json 11.0.0)
with FSharp.Core 8.0.403, the oldest the packages are built against, and
again with 11.0.101, what a new project gets. Every row holds on both; the `Transact` call in the
`?cancellationToken` row, whose optional parameters are Plasm's own signature,
was measured on the first only:

| F# | What C# writes | Fix belongs |
|---|---|---|
| `'T option` | `FSharpOption<T>`; `None` is a null reference, and `card.Blocked.Value` on it compiles without a warning and throws NullReferenceException | The seam: `FindResult<'T>`, or a nullable in a view |
| `Result<_, _>` | `FSharpResult<T, E>`: `.IsOk`, `.ResultValue`, `.ErrorValue`, no deconstruction | The seam |
| F# list | Read as `IEnumerable<T>` as it is; built with `ListModule.OfSeq(...)` | The seam: arrays |
| Single-case union id | `CardId.NewCardId(guid)`, `.Item`; a private one (`Slug`) only through `Slug.create`, an `FSharpResult` | The seam: `Guid` and `string` |
| Multi-case union | `error switch { DomainError.WipLimitReached w => w.limit, _ => ... }`: each case is a nested class with F#'s field names; F# emits no union marker C# reads, so the switch needs a discard arm | The seam: a C# union of records |
| `?cancellationToken` | An `FSharpOption<CancellationToken>` parameter C# must pass: `repo.One(q)` does not compile (CS1501); `repo.One(q, cancellationToken: ct)` does, through `FSharpOption`'s implicit conversion; `Transact` needs `FSharpOption<IsolationLevel>.None, FSharpOption<RetryPolicy>.None, ct` | Plasm, only if C# is to call `Repo` itself; nowhere behind the seam |
| A prepared query | An `FSharpFunc` value: `Workflows.Queries.boardBySlug.Invoke(slug)` | The seam |
| `Tx` | `Tx.insert<Board, Unit>(board)`: the error type cannot be inferred, and `transaction { }` is F# only | Nowhere: composing transactions is the F# side's |

Plasm is callable from C# throughout: nothing above needed a change in it.
Behind the seam, C# reaches none of it.

## JSON

What System.Text.Json writes of each: reflection-based with web options, source-generated with a context's default options (hence `Card`, not `card`):

| Value | Reflection-based | Source-generated |
|---|---|---|
| `Some 3` / `None` | `3` / `null` | `{"Value":3}` |
| F# list | `[1,2]` | `[1,2]` |
| `CardId` (public single-case) | `{"$type":"cardId","item":"..."}` | `{"Tag":0,"Item":"..."}` |
| `Slug` (private single-case) | throws (an `ArgumentException` inside a `TargetInvocationException`): its representation is private | `{"Value":"alpha"}` |
| `Stage.Backlog` (a case without fields) | `"backlog"` | not measured |
| `DomainError.WipLimitReached 2` | `{"$type":"wipLimitReached","limit":2}` | not measured |
| A record of the above (`Board`, `Card`) | throws, from its first private union | each field as above |
| The seam's views (records of strings, numbers, arrays, union fields) | as declared, camelCase | as declared, camelCase |
| `StageView`, a union field | its case: `{"kind":"doing","assignee":"Ada","since":"..."}` | the same, natively too |

System.Text.Json writes a union as its case alone, so each case the seam
writes has a constant `Kind` property, written first. Its source generator
accepts a union only if it could read the JSON back, which a host never does:
it refuses one (SYSLIB1227: every case is a JSON object) unless the type
names a classifier. So the seam writes one for each union, inheriting
Plasm.CSharp's `ByKind`, which reads ahead for `kind` and names the case.
This is the template's, as the build writes it:

<!-- from: tests/Plasm.Package/starter/Views.g.cs -->
```csharp
public sealed record Withdrawn(string Reason, DateTimeOffset Since)
{
    [JsonPropertyOrder(-1)] public string Kind => "withdrawn";
}
// ...
[JsonUnion(TypeClassifier = typeof(ConditionViewByKind))]
public union ConditionView(Bookable, Withdrawn);
// ...
public sealed class ConditionViewByKind() : ByKind(typeof(ConditionView),
[
    Tuple.Create("bookable", typeof(Bookable)),
    Tuple.Create("withdrawn", typeof(Withdrawn)),
]);
```

The source generator takes a classifier whose override is inherited from
another assembly's base, natively too (tests/Plasm.Package/seam). A case is a
positional record, so C# cannot make an empty one (`new Withdrawn()` is
CS7036). It works natively, reading each case back as itself, with no IL
warning of its own.

The built-in `JsonUnionTypeStructuralClassifier`, which tells cases apart by
their properties, was tried first and needs more of the records: it refuses
a case whose properties another case also has (the `kind` every case has),
unless each case's fields are `JsonRequired`, which needs setters, and with
them an empty case with a null field; and a case without fields refuses
unmapped members.

So the views are what a C# host serialises: records of plain types, which
both modes write the same and a native binary needs no reflection for. A
minimal API never writes an outcome itself; C# switches over it first.
Reading JSON into an F# record leaves a field the JSON lacks at null, even
one F# says cannot be, which is why the seam reads each request as a C#
record first.

A read of many is an array of views, and a source-generated context must
declare the array type as well as the view, as in
`[JsonSerializable(typeof(CardView[]))]`. Without that line the native API
throws `NotSupportedException` when it writes the list: no `JsonTypeInfo`
metadata was provided for the array type. The seam's own context,
`SeamJson`, lists each array a data method returns.

Nothing null crosses to F#. A minimal API binds requests with the host's own
`SerializerOptions`, not the source-generated context's options, so the host
sets `RespectNullableAnnotations` and `RespectRequiredConstructorParameters`
there in `ConfigureHttpJsonOptions`. Then an argument left out or sent as
null is a 400 before any F# is called (csharp-host.feature). The attribute's
flags alone protect only a direct `JsonSerializer` call with the context,
which is how Kanban's Lambda function reads its events (lambda.feature).

## Native AOT

The C# host publishes natively (`dotnet publish -r osx-arm64`), with
FSharp.Core's two summary warnings allowed as for any F# program
(`WarningsNotAsErrors` IL2104 and IL3053, see aot.md) and no other IL
warning. Its scenarios pass against the native binary as against the built
host (`KANBAN_CSHARP_HOST`). The C# union switches, the JSON of the views the
seam writes and Plasm's start-up check all run natively. The same `Boards`
serves a Lambda function in C# (aot.md, "Kanban's C# function").
