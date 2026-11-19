# Kanban's host: an F# core under a C# application

For a reader who knows C# and ASP.NET Core and is deciding whether this
arrangement suits their own application. The domain's rules, its queries and
its transactions are F#, with Plasm; everything an application usually writes
around them is C#. This guide grows with the host: it describes what is built
so far, and says what is not.

| Slice | What it adds | State |
|---|---|---|
| 1 | The host, cookie sign-in, GraphQL reads (Hot Chocolate) | built |
| 2 | REST: boards, cards, every change, my work, done paging | built |
| 3 | GraphQL mutations and `card(id)`, each outcome a typed union member | built |
| 4 | A background job sending stale cards back, time from a `TimeProvider` | built |

## What is F# and what is C#

| Project | Language | What it is |
|---|---|---|
| `examples/Kanban/src/Shared` | F# | The domain: `Card`, `Board`, `Person`, `Password`, and every rule (`Card.start`, `Card.finish`, ...) as a function returning `Result<_, DomainError>`. No I/O. |
| `examples/Kanban/src/Data` | F# | `Db.fs`: the model Plasm stores. `Workflows.fs`: the queries and transactions, one per change (`changeCard`, `addCard`, ...). `Facade.fs`: `KanbanData`, the one type C# calls. |
| `examples/Kanban/src/Host` | C# | This host: ASP.NET Core, cookie authentication, REST (`Rest.cs`), GraphQL (`GraphQL.cs`), accounts (`Accounts.cs`), the background job (`StaleCards.cs`), and the one mapping from outcome to HTTP answer (`Outcomes.cs`). |
| `src/Plasm.HotChocolate` | F# | Turns Hot Chocolate's where, order and paging into Plasm query refinements, and shows a C# union as a GraphQL union (docs/hotchocolate.md). |
| `examples/Kanban/src/Host.CSharp` | C# | A second, minimal host that compiles natively; it is the proof that the F# core runs without code generation. Not the subject of this guide. |
| `examples/Kanban/src/Server`, `Client` | F# | Kanban's own F# server and Fable client, which share the domain and data projects. |

## The boundary

What the C# host sees of the F# core is `KanbanData` and the types its methods
take and return. The rules for that surface:

- **Nothing null crosses, either way.** Nothing the facade returns is null,
  an F# option or an F# `Result`: a read that can miss returns `Found<T>`,
  either the value or `Missing`, and a change returns an outcome such as
  `ChangeOutcome`: the card, or `Missing`, `Refused`, `Stale` or `Invalid`.
  Nothing the host passes in is null either. The host's JSON options respect
  C#'s nullable annotations and required constructor parameters, so a body
  that leaves out a field, or gives it as null, is answered 400 before any
  endpoint runs. The facade is opened without a logger, not with a null one,
  when no SQL is to be written.
- **Outcomes are C# unions.** Each is a struct marked `[Union]` whose cases
  are records of their own, so C# switches on them:

  ```csharp
  public static IResult Of(ChangeOutcome outcome) => outcome switch
  {
      CardView card => Results.Ok(card),
      Missing missing => Of(missing),
      Refused refused => Of(refused),
      Stale stale => Of(stale),
      Invalid invalid => Of(invalid),
  };
  ```

  No switch in the host has a default arm. If the facade adds a case, every
  switch that does not handle it is a compile error, not a 500 at run time.
- **Every rule is the domain's.** The host passes the request's values and
  the person signed in; whether that person may do it, and whether the values
  make a valid name, password or title, is the F# code's answer. The host has
  no `if` about the domain.
- **The person a change is made for is the person signed in.** It comes from
  the cookie's `ClaimsPrincipal` (`user.Actor()`), never from the request's
  body.
- **A database that cannot be reached** surfaces as
  `KanbanUnavailableException`; any other failure is one of the outcomes above.

## One request, end to end: starting a card over REST

`POST /cards/{id}/start` with the body `{"version": 1}`, from someone signed in.

1. **C# endpoint** (`Rest.cs`). The route is in a group that
   `RequireAuthorization()`s, so a request without the cookie is answered 401
   before any of this runs. The handler is one line:

   ```csharp
   changes.MapPost("/cards/{id:guid}/start", async (Guid id, Versioned input, ClaimsPrincipal user, KanbanData kanban, CancellationToken ct) =>
       Outcomes.Of(await kanban.StartAsync(id, input.Version, user.Actor(), ct)));
   ```

   The body is read as `Versioned(int Version)`. A name the request also
   sends (`"assignee": "Grace"`) is not read.
2. **Facade method** (`Facade.fs`, `StartAsync`). It makes a `Person` of the
   name, which the domain may refuse (then `Invalid`). Otherwise it runs the
   `changeCard` workflow with the rule `Card.start who now` as the decision,
   serializable, and retries up to five times on a serialization failure,
   waiting between attempts (docs/adr/0001-architecture.md).
3. **Workflow** (`Workflows.fs`, `changeCard`). It reads the card and checks
   the caller's version. It reads the card's board and counts the board's
   cards in progress. It asks the rule, then updates the card.
4. **SQL**, as the host logs it with `KANBAN_SQL=1`:

   ```sql
   SELECT "c"."id", ... , "c"."version" FROM "cards" AS "c" WHERE "c"."id" = @p0 LIMIT 2
   SELECT "comments"."id", ... FROM "comments" WHERE "comments"."card" = ANY(@p0) ORDER BY "comments"."at", "comments"."id"
   SELECT "labels"."id", "labels"."name", "card_labels"."card_id" AS "__owner" FROM "labels" INNER JOIN "card_labels" ON ... WHERE "card_labels"."card_id" = ANY(@p0) ORDER BY "labels"."name"
   SELECT "b"."id", "b"."slug", "b"."name", "b"."wip_limit" FROM "boards" AS "b" WHERE "b"."id" = @p0 LIMIT 2
   SELECT COUNT(*) AS "n" FROM "cards" AS "c" WHERE "c"."board" = @p0 AND "c"."stage" = 'doing'
   UPDATE "cards" SET "stage" = @p0, "stage_assignee" = @p1, "stage_since" = @p2, "stage_resolution" = NULL, "stage_resolution_reason" = NULL, "stage_at" = NULL, "version" = "cards"."version" + 1
     WHERE "cards"."id" = @p3 AND "cards"."version" = @p4 RETURNING ...
   ```

   The card's comments and labels are read with it, because they are part of
   the card the rule decides on. The update checks the version again, so a
   change made between the read and the write is refused as well.
5. **Outcome.** `ChangeOutcome` is one of five cases.
6. **HTTP answer** (`Outcomes.cs`): 200 with the card; 404 `Missing`; 422
   `Refused`, with the domain's code and words (`WipLimitReached`, ...); 409
   `Stale`, when the caller's copy is old; 400 `Invalid`, with each field and
   why.

Sent again with the same version, the same request is answered
`409 {"card": "..."}`.

## The same change over GraphQL

```graphql
mutation {
  startCard(card: "22222222-...", version: 1) {
    __typename
    ... on CardView { title version stage { __typename ... on InProgress { assignee } } }
    ... on Refused { code message }
    ... on Stale { card }
  }
}
```

1. **C# resolver** (`GraphQL.cs`, `Mutation.StartCard`). The `Mutation` type
   is marked `[Authorize]`, so a request without the cookie is answered with
   the error `AUTH_NOT_AUTHENTICATED` before any resolver runs, and no SQL is
   sent. The resolver is one line, the same call the REST endpoint makes:

   ```csharp
   public Task<ChangeOutcome> StartCard(Guid card, int version, ClaimsPrincipal user, [Service] KanbanData kanban, CancellationToken ct) =>
       kanban.StartAsync(card, version, user.Actor(), ct);
   ```

   Hot Chocolate refuses a request that leaves out `card` or `version`, or
   gives null for either, since both are non-null in the schema; nothing null
   reaches the facade this way either.
2. **Facade, workflow and SQL**: as over REST, above, statement for
   statement.
3. **Outcome.** The resolver returns the `ChangeOutcome` struct. Hot
   Chocolate is told once, in `Program.cs`, that `ChangeOutcome` is the
   GraphQL `union ChangeOutcome = CardView | Missing | Refused | Stale |
   Invalid` (`AddPlasmUnion<ChangeOutcome>()`); a field middleware hands it
   the case, and the case's own fields answer:

   ```json
   {"data":{"startCard":{"__typename":"CardView","title":"Build it","version":2,"stage":{"__typename":"InProgress","assignee":"Margaret"}}}}
   ```

   Sent again with version 1, it reads the card (three statements), finds
   the version moved on, and updates nothing:

   ```json
   {"data":{"startCard":{"__typename":"Stale","card":"22222222-..."}}}
   ```

A GraphQL answer has no status of its own: every case is a 200 with data,
and the client tells them apart by `__typename`. Only a request the schema
or the authorization refuses is an error.

## REST

Reads are open to anyone; every other endpoint needs someone signed in.

| Endpoint | Facade method | Answers |
|---|---|---|
| `GET /boards` | `BoardsAsync` | 200: each board's counts and newest titles |
| `GET /boards/{slug}` | `BoardAsync` | 200, 404 |
| `GET /boards/{slug}/done?page=&size=` | `DonePageAsync` | 200 (a page, the total, the pages), 404, 400 (page below 1, size outside 1 to 50) |
| `GET /me/work` | `WorkOfAsync` | 200: the cards the person signed in is working on |
| `POST /boards` `{slug, name, wipLimit}` | `CreateBoardAsync` | 201, 400 (a slug already taken is `slug`: "is already taken") |
| `POST /boards/{slug}/cards` `{title}` | `AddCardAsync` | 201, 404, 400 |
| `POST /boards/{slug}/send-all-back` | `SendAllBackAsync` | 200 `{cards}`, 404 |
| `POST /cards/{id}/start` `{version}` | `StartAsync`, as the person signed in | the change's answers |
| `POST /cards/{id}/ship` `{version}` | `ShipAsync` | the change's answers |
| `POST /cards/{id}/drop` `{version, reason}` | `DropAsync` | the change's answers |
| `POST /cards/{id}/send-back` `{version}` | `SendBackAsync` | the change's answers |
| `POST /cards/{id}/reopen` `{version}` | `ReopenAsync` | the change's answers |
| `POST /cards/{id}/block` `{version, reason}` | `BlockAsync` | the change's answers |
| `POST /cards/{id}/unblock` `{version}` | `UnblockAsync` | the change's answers |
| `PUT /cards/{id}/title` `{version, title}` | `RenameAsync` | the change's answers |
| `PUT /cards/{id}/labels` `{version, labels}` | `RelabelAsync` | the change's answers; a label that is not one is 400 on `labels[i]` |
| `POST /cards/{id}/items` `{version, text}` | `AddItemAsync` | the change's answers |
| `POST /cards/{id}/items/tick` `{version, text}` | `TickAsync` | the change's answers |
| `POST /cards/{id}/comments` `{body}` | `CommentAsync`, the person signed in the author | the change's answers |

A change's answers are those of `ChangeOutcome`, above: 200, 404, 422 with
the domain's code, 409, 400. A comment carries no version: it is added to
the card as stored. Shipping and dropping are two endpoints rather than one
`finish` with a resolution, so neither host nor facade parses a resolution
from text.

What the domain decides, the host passes on unchanged. Blocking a card stops
it shipping (`CardBlocked`), not starting: that is `Card.finish`'s rule, and
the REST scenarios follow it.

## Accounts and signing in

`POST /account/register`, `/account/signin`, `/account/signout`, and
`GET /account/me`, all in `Accounts.cs`.

- Whether a name and a password are acceptable is the domain's (`Person`,
  `Password`). `KanbanData.RegisterAsync` makes both and stores the account; a
  name already taken comes back as `Invalid` on `name`, from the database's
  unique constraint.
- The host hashes the password with ASP.NET Core Identity's
  `PasswordHasher` and passes the hash function to the facade, so only the
  hash is stored. The hasher's methods take the user the password is for,
  which its implementation does not read; the host passes one placeholder
  user, not null. Signing in reads the account (`AccountAsync`) and checks the
  hash in the host. A name with no account is checked against a hash of its
  own, so it takes as long to refuse as a wrong password.
- Who is signed in is kept in an HTTP-only, SameSite=Strict cookie. An API
  answers 401 where a site would redirect to a sign-in page.

## GraphQL

Reads are open to anyone; every mutation needs someone signed in.

- `cards(where, order, skip, take)` and `boards { cards(where, order) }`. The
  where and order a client may give are generated from the card fields that
  `Kanban.Data` declares once (`CardFields`), and the database does both. A
  board's cards, for every board a request asks for, are one query. See
  docs/hotchocolate.md.
- `card(id)` answers `union CardResult = CardView | Missing`, from
  `CardAsync`'s `Found<CardView>`. A generic type's own name is not one
  GraphQL allows, so this union alone is named where it is registered
  (`AddPlasmUnion<Found<CardView>>("CardResult")`).
- Each mutation calls the facade method its REST endpoint calls, and answers
  the facade's outcome as a union of its cases:

| Mutation | Facade method | Answers |
|---|---|---|
| `createBoard(slug, name, wipLimit)` | `CreateBoardAsync` | `CreateBoardOutcome = BoardSummary \| Invalid` |
| `addCard(board, title)` | `AddCardAsync` | `AddCardOutcome = CardView \| Missing \| Invalid` |
| `sendAllBack(board)` | `SendAllBackAsync` | `SendAllBackOutcome = SentBack \| Missing` |
| `startCard(card, version)` | `StartAsync`, as the person signed in | `ChangeOutcome` |
| `shipCard`, `sendCardBack`, `reopenCard`, `unblockCard` `(card, version)` | `ShipAsync`, `SendBackAsync`, `ReopenAsync`, `UnblockAsync` | `ChangeOutcome` |
| `dropCard`, `blockCard` `(card, version, reason)` | `DropAsync`, `BlockAsync` | `ChangeOutcome` |
| `renameCard(card, version, title)` | `RenameAsync` | `ChangeOutcome` |
| `relabelCard(card, version, labels)` | `RelabelAsync` | `ChangeOutcome` |
| `addItem`, `tickItem` `(card, version, text)` | `AddItemAsync`, `TickAsync` | `ChangeOutcome` |
| `comment(card, body)` | `CommentAsync`, the person signed in the author | `ChangeOutcome` |

`ChangeOutcome = CardView | Missing | Refused | Stale | Invalid`. Each
outcome type is registered once, in `Program.cs`; a record that is a case of
several unions (`CardView`, `Missing`, `Invalid`) is one object type.

## The background job

`StaleCards.cs` is a `BackgroundService` that sends stale cards back to the
backlog when the host starts, and then every `Kanban:StaleCardsEvery` (an
hour unless configured).

- **What is stale is the domain's.** `Card.staleAfter` (14 days) is how long
  a card may be in progress. The job passes the time and nothing else to
  `KanbanData.SendStaleBackAsync(now)`, which runs
  `Workflows.sendStaleBack now`: one UPDATE over every stale card, on every
  board:

  ```sql
  UPDATE "cards" SET "stage" = @p0, "stage_assignee" = NULL, "stage_since" = NULL, "stage_resolution" = NULL,
    "stage_resolution_reason" = NULL, "stage_at" = NULL, "version" = "cards"."version" + 1
    WHERE "cards"."stage" = 'doing' AND "cards"."stage_since" < @p1
  ```

- **Time is the `TimeProvider` in the host's services.** The job reads
  "now" from it and waits on it between runs (`PeriodicTimer`), so a test
  host could register a `FakeTimeProvider` instead. The scenarios need no
  fake: their cards are written as started days ago, and the job runs every
  second.
- **No per-card decision runs, and nothing is skipped.** A bulk write does
  not call `Card.sendBack` for each card; that is sound because
  `Card.sendBack` refuses no card in progress. Each card's version is
  incremented, so a copy read before the job ran is answered 409 (`Stale`).
  Kanban keeps no history of changes, so there is no audit for the bulk write
  to skip.
- **A database that cannot be reached** fails one run, which is logged; the
  next run tries again.

## Running it

```bash
cd examples/Kanban
# A PostgreSQL the F# server has migrated; then:
ConnectionStrings__Kanban="Host=...;Database=..." dotnet run --project src/Host
# Each command's SQL on standard output:
KANBAN_SQL=1 ConnectionStrings__Kanban="..." dotnet run --project src/Host
```

The scenarios are in `features/host/` (`authentication.feature`,
`rest.feature`, `graphql.feature`, `mutations.feature`,
`background.feature`). Each starts the host as its own process, on a database of
its own, and talks to it over HTTP:

```bash
dotnet test --project tests/Kanban.Host.Specs    # Docker, or PLASM_TEST_PG
```

`tests/Hosting/no-leftover-hosts.sh` checks that no host outlives its
scenarios.

## What is awkward or missing

- **Runtime only.** Hot Chocolate 16.7.6 does not start in a native binary,
  so this host runs on the JIT. The F# core does compile natively; that is
  `Host.CSharp`'s job to show (docs/aot.md).
- **Selection is not pushed down.** A GraphQL request that asks for a card's
  title alone still reads the whole card, with its comments and labels.
- **Nested paging is not pushed down.** A board's cards take a where and an
  order, but not a page.
- **Two hosts, two ways of hashing.** Kanban's F# server and this host both
  keep accounts in the `accounts` table, and both use ASP.NET Core Identity's
  hasher, but each wraps it in its own code. They are not meant to share a
  database of accounts.
- **Each switch handles every case.** A union with five cases needs five
  arms everywhere it is answered. `Outcomes.cs` keeps that to one place per
  outcome type.
- **A GraphQL client is not held to every case.** The C# host must handle a
  case the facade adds, or not compile; a GraphQL client that asks for no
  fragment on it still gets its `__typename` and nothing else, and learns
  of it only from the schema.
- **Two clocks.** The job's "now" is the host's `TimeProvider`; a change's
  own time (when a card was started, finished, commented on) is still the
  facade's, `DateTimeOffset.UtcNow`, since `KanbanData` is opened before the
  host's services are built and takes no clock.
- **Not every condition over a domain member translates.** Plasm reads
  `c.Since` (a member matching on the stage) as its column in an order, but
  refuses `c.Since |> Option.exists (fun since -> since < t)` in a where, at
  start, naming the query; `sendStaleBack`'s query is written as a match on
  `c.Stage` instead.
- **`[Authorize]` is a second package.** Hot Chocolate's authorization is
  `HotChocolate.AspNetCore.Authorization`, pinned in this example at the
  version Plasm.HotChocolate is built against; Plasm.HotChocolate does not
  depend on it.
