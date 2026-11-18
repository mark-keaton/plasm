# Kanban: an F# domain at the centre of an ASP.NET Core application

A small kanban board, built to answer one question: can Plasm carry an
application whose centre is a plain F# domain, the place EF Core usually
occupies? It has the shape of a SAFE-stack application: ASP.NET Core with
Giraffe and Fable.Remoting on the server, Fable with Elmish in the browser, and
a project shared by both.

## What depends on what

```
src/Shared   Domain.fs   types and rules, and on each stored record the few
                         storage facts its types cannot say; references
                         FSharp.Core and Plasm's attribute classes
             Api.fs      the contract between server and browser
src/Data     Db.fs         which records are stored, and the server's own Account
             Workflows.fs  queries and transactions, as values
             Facade.fs     KanbanData, the one type a C# host calls
src/Server   Service.fs    the API: parse input, run a workflow, map the outcome
             Database.fs   migrations (DbUp), and the repository, checked as it starts
             Host.fs       ASP.NET Core: services, cookie authentication, start-up,
                           the pipeline
src/Client   App.fs      the Elmish application
src/Host.CSharp          the same data served by C# minimal APIs (docs/csharp.md)
features/    the acceptance scenarios (Reqnroll), run against real PostgreSQL;
             features/csharp/ the C# host's
```

There is one model, declared once. The records Plasm reads and writes are the
domain's own, and how `Kanban.Card` is stored is read from `Kanban.Card`:
`[<Table "cards">]` on the type, `[<Version>]` and `[<OrderedBy ...>]` on the
fields that need them. The key is the field named `Id`; references, owned
comments and linked labels follow from the fields' types. There is no second
set of row records, no separate schema declaration, and no code that converts
between two shapes.

`Domain.fs` is compiled twice: for .NET, where the server decides with it and
stores its records, and to JavaScript, where the browser validates forms with
the same constructors and puts a refusal into words with the same
`DomainError.describe`. The only thing it takes from Plasm is attribute
classes, which Fable compiles from source like the domain itself.

In `src/Data`, `Db.fs` lists the stored records to build the model.
`Workflows.fs` describes
each change as read, decide, write, where "decide" is a function from
`Domain.fs` and what is read and written is a `Card`: a write is
`Tx.insert card` or `Tx.update (card, changed)`, and Plasm writes the
difference. `Service.fs` runs those descriptions with `repo.Transact`, the
only way to write, and matches on every way they can fail.

## Run it

```
docker compose up -d                      # PostgreSQL on localhost:5544
dotnet run --project src/Server           # the API on http://localhost:5080
npm install && npm run dev                # the client on http://localhost:5173
```

`npm run build` puts the client into `src/Server/wwwroot`, and the server then
serves everything from http://localhost:5080.

The scenarios need Docker (or `PLASM_TEST_PG`, as the library's tests do):

```
dotnet test --project tests/Kanban.Specs
dotnet test --project tests/Kanban.CSharpHost.Specs
```

The C# host's scenarios start the host as its own process and talk to it
over HTTP. To run them against its native binary instead, publish it and
name the binary in `KANBAN_CSHARP_HOST`:

```
dotnet publish src/Host.CSharp -c Release -r osx-arm64 -o artifacts/host-native
KANBAN_CSHARP_HOST=$PWD/artifacts/host-native/Kanban.Host.CSharp dotnet test --project tests/Kanban.CSharpHost.Specs
```

(`osx-arm64` is the machine's runtime identifier: `linux-x64`, `win-x64`
and so on elsewhere.)

## What the scenarios show

- **A union with fields at the centre.** `Stage` is `Backlog | Doing of
  assignee * since | Done of resolution * at`, and it is a field of the
  stored record. Plasm spreads it over the columns `stage`, `stage_assignee`,
  `stage_since`, `stage_resolution`, `stage_resolution_reason` and
  `stage_at`, and refuses to read a row whose columns contradict each other:
  it is reported as an error, never repaired.
- **A value in the card's row.** A card can be blocked: `Blocked` is a
  `Blocker option`, a plain record of a reason and a time with no table and
  no attribute. Plasm stores it as the columns `blocked_reason` and
  `blocked_since`, both empty when the card is not blocked, and the DDL it
  prints includes a constraint that there is no reason without a time. A
  board's blocked cards are a query over those columns.
- **A document in the card's row.** A card's checklist is a
  `ChecklistItem list` marked `[<Json>]`: one `jsonb` column, written whole
  when the list changes. "Cards with something left to do" is
  `List.exists (fun item -> not item.Done)` over it, which the database
  runs. A stored document that does not fit the record is an error naming
  the place, never repaired, so changing `ChecklistItem` needs a migration
  of the stored documents first.
- **Queries in the domain's words.** `Card.Assignee` is an ordinary member of
  the domain's record, a `match` on the stage. Marked `[<ReflectedDefinition>]`,
  it can be used in a query (`where (c.Assignee = Some person.Value)`), where
  it becomes the column `stage_assignee`.
- **Validated types end to end.** `Slug`, `Title`, `Person` and `WipLimit`
  have private constructors. The same `create` functions check the browser's
  forms, parse the API's input and read the database's columns. No mapping is
  listed anywhere: Plasm finds each type's `create` by convention.
- **A rule over several rows.** A board's limit on work in progress holds when
  two people start cards at the same moment: the workflow runs serializable
  with `RetryPolicy.OnSerializationFailure`, and the loser is told the limit is
  reached. The scenario fails if the isolation level is lowered.
- **Stale copies.** A change based on an old version of a card is refused.
  Cards carry a version number because that number travels to the browser.
- **Failures as values, with their types.** A workflow is a
  `Tx<Card, Refusal>`: what it can refuse is in its type, and the service
  matches on it without a cast. Invalid input by field, a refusal by the
  domain, a missing board, a stale copy, a unique slug and an unreachable
  database each reach the client as their own case of `ApiError`.
- **Constraints stated once.** `[<Unique>]` is on `Board.Slug`. A
  violation comes back as an error on that field; no constraint's name is
  written in the code. That a card's `Board` references a board is inferred
  from its type, `BoardId`.
- **What a card owns.** A card's comments are a plain `Comment list`, read
  with the card and written by changing the list. They are the card's own
  because a `Comment` holds a `CardId`. Its labels are shared rows that exist
  once: the card holds a plain `Tag list`, a `Tag` holds no `CardId`, and
  changing the list writes only rows of `card_labels` (`[<JoinTable>]`).
  `Bulk.insertMissing` stores the labels not yet known.
- **Aggregates refer to each other by id.** A board has no list of cards; a
  board's cards are a query, narrowed to a label with `List.exists` over the
  card's labels. A board can be created with its first cards: several inserts
  in one transaction, and nothing is stored if the slug is taken.
- **Asking for another aggregate.** The list of boards shows each board's
  three newest cards and its blocked cards. Cards are not part of a board,
  so the query asks for them, once for each list:
  `let! newest = plasm { for c in referring<Card> b do ... select c.Title }`
  and `and! blocked = ...`, and it ends in
  `return { Board = b; Newest = newest; Blocked = blocked }`. Its result is
  a `BoardLine`, a record the application declares, which a query that did
  not ask could not be mistaken for. A load is returned in the field of its
  own name, so the two lists of titles cannot be crossed. `referring<Card> b`
  is the cards that hold the board's id; a `Card` has one field of type
  `BoardId`, so the tie is never written out. Each inner query ends in
  `select c.Title`, so only titles are read, and each `let!` or `and!` is
  one more command: listing sends four queries however many boards and
  cards there are. The page also lists every blocked card with its board. A
  card holds one board's id, so `let! board = referredBy<Board> c` binds a
  `Board option`.
- **A bulk write.** "Send all back" is one `UPDATE` over a board's cards in
  progress (`Bulk.update` with `{ c with Stage = Backlog }`), run as a step of
  a transaction. It runs no per-card decision, and `Workflows.sendAllBack`
  says why that is sound here. Versions are incremented, so copies read before
  it are stale.
- **A join.** What someone is working on is read across boards with one
  query that joins cards to their boards and selects five columns into a
  record, `WorkLine`, so they are read by name.
- **Paging.** A board's done cards are read a page at a time, newest first,
  with a prepared query (`skip` and `take` are arguments) and `repo.Count`.
  Cards finished at the same moment are still each on exactly one page.
- **Authentication.** Accounts are a name and a password hash (ASP.NET Core
  Identity's hasher). Who is signed in travels in an `HttpOnly`,
  `SameSite=Strict` cookie; the service receives it as a `Session` value, so
  the scenarios supply their own. Reading is open; every change needs someone
  signed in and is recorded in their name, never a name the request supplies.
- **Start-up.** The application applies its SQL migrations, then starts its
  repository with `Repo.Start`, which checks two things and by default raises
  `PlasmStartupException` on either: whether the tables fit the model, and
  whether every query the assembly declares can be translated. Nothing is
  written to ask for the checks; turning one down to `Warn` or `Ignore` is
  what has to be written. All the application's queries are values, so none
  is first tried in front of a user. `/health` asks about the tables again
  with `repo.CheckSchema`.
- **Tests on real PostgreSQL.** Each scenario runs in a `repo.Sandbox` that is
  rolled back, so they share one database.

## What it does not show

There are still two shapes of a card: the domain's `Card` and the `CardView`
the browser is sent (`Api.fs`). The browser's input is never trusted as a
domain value, and what it is sent is a view; that boundary is the
application's, and Plasm has no part in it.

Authorisation: anyone signed in can change any board. Signing in has no rate
limit or lockout, and nothing here says where the cookie's keys are kept in a
deployment. Has-one relationships, left joins, upserts that update,
hand-written SQL in the application, tenancy, and detecting a stale copy by
comparing against the record that was read (the cards use a version) are not
used. Nothing in this example has run in production.

## Two things that are not Plasm's

- The API's collections are arrays. Compiled by Fable 5, the remoting client
  cannot read an F# `list`.
- `Shared.fsproj` and `Client.fsproj` reference FSharp.Core explicitly: Fable
  does not see the SDK's implicit reference under central package management.
