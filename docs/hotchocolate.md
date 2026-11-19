# GraphQL with Hot Chocolate

A GraphQL client chooses a request's conditions and order. With
Plasm.HotChocolate, the database does both: Plasm builds them onto the
application's query from fields the application declared, once, in F#.
Built against Hot Chocolate 16.7.6 (`HotChocolate.Data`,
`HotChocolate.AspNetCore` for a host); Kanban's host, `examples/Kanban/src/Host`
(docs/kanban-host.md), serves Kanban's cards with it.

Plasm.HotChocolate is for the runtime, not for a native binary: Hot Chocolate
16.7.6 itself does not start natively (its DataLoader interceptor's
constructor is trimmed away, by its reflection-based and its source-generated
setup alike). Core Plasm, `BuildQuery` and the declared fields among it, stays
native, with no IL warning naming Plasm (`tests/Plasm.Aot`). So the split is:
a native host calls the application's facade directly (Kanban's
`Host.CSharp`), a GraphQL host runs on the runtime, and both stand on the same
F# data library.

## The two parts

**In Plasm** (`src/Plasm/BuildQuery.fs`, `features/build-query.feature`), for
any program that builds a query at run time (a GraphQL layer, a report
builder); an application writes its queries with `plasm { }`, not with these:

- `QueryField.equal`, `ordered`, `text`, `equalVia` and `orderedVia` declare a field
  a caller may filter, and perhaps order, by: a name, a quotation reading it
  from the record, the operators it takes, the type of the value a caller
  gives and, when that differs from the field's own type, how one is made
  from the other.
- `field.Condition(operator, value)` builds a condition, `QueryCondition.And`,
  `Or` and `Not` join conditions, and `BuildQuery.where`, `orderBy`,
  `orderByDescending`, `take` and `skip` add them to a query: each what the
  query builder adds for the same clause written, so the SQL, the rows and
  the refusals are the written query's.
- `repo.CheckFields fields` translates every declared field with each of its
  operators, and its ordering, once, and fails start-up naming each one Plasm
  cannot translate (`Card.title GreaterThan: ...`), as `Repo.Start` does for
  a query.

These live in the application's data library beside its queries: Kanban.Data
declares `CardFields` and references only Plasm, so Hot Chocolate reaches no
host that does not serve GraphQL (Kanban's native C# host carries the
declarations and nothing of Hot Chocolate).

**In Plasm.HotChocolate** (`src/Plasm.HotChocolate`), for a GraphQL host:

- `PlasmFilterInputType<'T>` and `PlasmSortInputType<'T>` are the filter and
  sort inputs, generated from the declarations: each field with the operators
  it was declared with, typed as the value it is given, `and` and `or` lists
  of the filter, and the orderable fields. A host subclasses each with a
  parameterless constructor for `UseFiltering` and `UseSorting`.
- `PlasmRequest.Apply(context, fields, query)` reads the field's `where` and
  `order` arguments as Hot Chocolate coerced them and builds them onto
  `query`.
- `AddPlasmFiltering()` and `AddPlasmSorting()` register filtering and sorting
  with Hot Chocolate's operations and scalar bindings and a provider of
  Plasm's in place of the one that translates to LINQ: its handlers accept
  every field the generated inputs declare and translate nothing, since
  Plasm does.
- `AddPlasmUnion<'U>()` shows a type of C#'s union shape as a GraphQL union
  named as the type is, and `AddPlasmUnion<'U>(name)` names it, for a
  generic type such as `Found<CardView>` (below).
- `PlasmRequest.Refinement(context, fields, orderWhenNone)` is the request's
  where and order as a function of a query, read from the context at once,
  for a DataLoader's batch, which runs after the resolver and must not keep
  its context; `PlasmRequest.BatchKey(context)` names the field and its where
  and order, so that requests with the same arguments share a batch.
- `PlasmRequest.Page(context, query, defaultTake)` pages a query as the
  field's offset paging (`skip`, `take`) asks, reading one row more than the
  page; `PlasmRequest.Segment(context, rows, defaultTake, total)` makes the
  `CollectionSegment` Hot Chocolate shows, running `total` only when the
  request selects `totalCount`.

## A host

Kanban.Data declares what a client may filter and order cards by:

```fsharp
[<RequireQualifiedAccess>]
module CardFields =
    let all: QueryField<Card> list =
        [ QueryField.text "title" <@ fun (c: Card) -> c.Title.Value @>
          QueryField.ordered "created" <@ fun (c: Card) -> c.Created @>
          QueryField.ordered "version" <@ fun (c: Card) -> c.Version @>
          QueryField.equalVia "stage" <@ fun (c: Card) -> kind c @> stage "backlog" ]
```

`title` is searched as the text of the card's `Title`: `Title.Value` is
marked `[<ReflectedDefinition>]`, so Plasm reads its body, the wrapper's own
field, as the title column (scalar-conventions.feature); `stage` is given as
`backlog`, `doing` or `done` and compared with Kanban's own `kind c`. `KanbanData.OpenAsync` checks them after the repository starts, and
`KanbanData.CardsAsync(refine, ct)` runs the cards query as `refine` makes it.
The host, in C#:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddPlasmFiltering()
    .AddPlasmSorting()
    .AddPlasmUnion<StageView>();

public class Query
{
    [UseOffsetPaging(DefaultPageSize = Paging.Default, MaxPageSize = Paging.Most, IncludeTotalCount = true)]
    [UseFiltering(typeof(CardFilter))]
    [UseSorting(typeof(CardSort))]
    public async Task<CollectionSegment<CardView>> Cards(IResolverContext context, [Service] KanbanData kanban, CancellationToken ct)
    {
        Plasm.Query<Card> Filtered(Plasm.Query<Card> query) => PlasmRequest.Apply(context, CardFields.all, query, CardFields.created);
        var rows = await kanban.CardsAsync(query => PlasmRequest.Page(context, Filtered(query), Paging.Default), ct);
        return await PlasmRequest.Segment(context, rows, Paging.Default, () => kanban.CountCardsAsync(Filtered, ct));
    }
}

public class CardFilter() : PlasmFilterInputType<Card>("CardFilterInput", CardFields.all);
public class CardSort() : PlasmSortInputType<Card>("CardSortInput", CardFields.all);
```

A board's cards with their own where and order are a field of `BoardNode`,
answered by a batch DataLoader keyed by board and argument set
(`BoardCardsLoader` in the host): one query per argument set for every board
in the batch, `Kanban.Data`'s `CardsOfBoardsAsync` writing the boards' keys as
one array parameter (`WHERE "c"."board" = ANY(@p0)`) and the request's where
and order built on it, its rows grouped by board in the query's order.

The GraphQL types are the application's views of plain types (`CardView`),
as for a C# host (csharp.md): the package exposes no domain record.

## What is done by the database, and what is not

Done by the database:

- **The where**: `eq`, `neq`, `gt`, `gte`, `lt` and `lte` on each field as
  declared, `contains`, `startsWith` and `endsWith` on a text field (as
  `LIKE`), and `and` and `or` of them, as the SQL's WHERE, every value a
  parameter. A text field may read a wrapper's text (`c.Title.Value`) when the
  wrapper's member is marked `[<ReflectedDefinition>]`; unmarked, Plasm
  refuses it and says to mark it.
- **The order**: each orderable field, ascending or descending, in the order
  given, as the SQL's ORDER BY.
- **The page**: offset paging's `skip` and `take` as the SQL's OFFSET and
  LIMIT, parameters both, one row more than the page so that `hasNextPage`
  needs no count; Plasm orders a page by the key after the request's order,
  so the same page holds the same rows each time it is read. Plasm reads no
  page without an order: `Apply`'s fourth argument names the declared field
  a page is ordered by when the request gives none (Kanban's `created`).
- **The total**, when the request selects `totalCount`: a COUNT under the
  request's where, not run otherwise. Hot Chocolate refuses a `take` over the
  field's `MaxPageSize` before the resolver runs.

Not done by the database, yet:

- **Field selection.** Every column of the record is read, and its owned and
  linked lists are loaded (a card's comments and labels, one command each for
  all the cards read), whatever the request selects; Hot Chocolate picks the
  selected fields from the views.
- **A nested collection's page.** A board's cards field takes a where and
  an order, done by the database for every board in one command, but no
  `take` per board. SQL can do it in the same command (Plasm's own loads do:
  `ROW_NUMBER() OVER (PARTITION BY` the parent key in a derived table named
  `ranked`), but Plasm plans that only for a load read from a `let!`
  quotation, and a query built at run time cannot be put inside one: the
  entry for adding a load from a built query is not public yet.

## Refused before any SQL

- **A field or operator not declared** is not in the generated input, so Hot
  Chocolate refuses the request in validation (`The specified input object
  field 'contains' does not exist.`).
- **A value the field cannot make its own** is a GraphQL error naming the
  field, with the declaration's own words (`Card.stage: no stage is
  "shipped": backlog, doing or done`), raised while the where is built.
- **A declared field Plasm cannot translate** fails start-up, by field and
  operator.

## A field of C#'s union shape

Hot Chocolate 16.7.6 does not read C#'s union shape: it makes a `[<Union>]`
struct such as `StageView` an object type with no fields, and refuses the
schema (`The object type 'StageView' has to at least define one field in
order to be valid.`). `AddPlasmUnion<StageView>()` makes it a GraphQL union
instead: one object type per case record (a constructor's parameter type),
`union StageView = InBacklog | InProgress | WasShipped | WasDropped`, every
field of the type bound to it, and a field middleware that hands Hot
Chocolate the case (`IUnion.Value`), so the case's fields resolve against it:

```graphql
{ cards { title stage { __typename ... on InProgress { kind assignee since } } } }
```

A case's object type is referred to by its record's type, so a record that
is a case of several unions, or the type of a field elsewhere, is one object
type: Kanban's host registers `ChangeOutcome`, `AddCardOutcome` and
`Found<CardView>`, and `CardView` is one type in all three and in `cards`.
A generic type's own name (``Found`1``) is not one GraphQL allows, so
`AddPlasmUnion<'U>()` refuses it, saying to name it:
`AddPlasmUnion<Found<CardView>>("CardResult")`.

## Translation

A request's query is built and translated for each request: a request names
values Plasm makes parameters, but its shape (which fields, which operators,
how they nest) is the request's. Translating it is now a few microseconds:
for a read by key, 3 µs under the runtime and 4 µs natively; for an order
with two loads, 17 and 30 µs (benchmarks.md, "Native translation, before and
after"). A prepared read takes 175 µs in all.

So translated plans are not cached, and no templates are kept by a request's
shape. A cache could save only those microseconds, and a request's shape is
known only after its query value is built, which costs more (8 to 43 µs
under the runtime, 48 to 348 µs natively). Translating each declared part
once, so that a request only assembled them, is not built either. It would
also need a way to put a prepared query's argument into a built condition
(an `Arg` is made only by `Query.prepare` today), and prepared queries of
any number of arguments (today one to three).
