# GraphQL with Hot Chocolate

A GraphQL client chooses a request's conditions and order. With
Plasm.HotChocolate, the database does both: Plasm builds them onto the
application's query from fields the application declared, once, in F#.
Built against Hot Chocolate 16.7.6 (`HotChocolate.Data`,
`HotChocolate.AspNetCore` for a host); `examples/Kanban/src/Host.GraphQL` is a
host over Kanban's cards.

## The two parts

**In Plasm** (`src/Plasm/BuildQuery.fs`, `features/build-query.feature`), for
any program that builds a query at run time (a GraphQL layer, a report
builder); an application writes its queries with `plasm { }`, not with these:

- `QueryField.equal`, `ordered`, `equalVia` and `orderedVia` declare a field
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
  (below).

## A host

Kanban.Data declares what a client may filter and order cards by:

```fsharp
[<RequireQualifiedAccess>]
module CardFields =
    let all: QueryField<Card> list =
        [ QueryField.orderedVia "title" <@ fun (c: Card) -> c.Title @> title "x"
          QueryField.ordered "created" <@ fun (c: Card) -> c.Created @>
          QueryField.ordered "version" <@ fun (c: Card) -> c.Version @>
          QueryField.equalVia "stage" <@ fun (c: Card) -> kind c @> stage "backlog" ]
```

`title` is given as a string and made a `Title` by `Title.create`; `stage` is
given as `backlog`, `doing` or `done` and compared with Kanban's own
`kind c`. `KanbanData.OpenAsync` checks them after the repository starts, and
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
    [UseFiltering(typeof(CardFilter))]
    [UseSorting(typeof(CardSort))]
    public Task<CardView[]> Cards(IResolverContext context, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.CardsAsync(query => PlasmRequest.Apply(context, CardFields.all, query), ct);
}

public class CardFilter() : PlasmFilterInputType<Card>("CardFilterInput", CardFields.all);
public class CardSort() : PlasmSortInputType<Card>("CardSortInput", CardFields.all);
```

The GraphQL types are the application's views of plain types (`CardView`),
as for a C# host (csharp.md): the package exposes no domain record.

## What is done by the database, and what is not

Done by the database:

- **The where**: `eq`, `neq`, `gt`, `gte`, `lt` and `lte` on each field as
  declared, and `and` and `or` of them, as the SQL's WHERE, every value a
  parameter.
- **The order**: each orderable field, ascending or descending, in the order
  given, as the SQL's ORDER BY.

Not done by the database, yet:

- **Paging.** A request reads every row its where selects.
- **Field selection.** Every column of the record is read, and its owned and
  linked lists are loaded (a card's comments and labels, one command each for
  all the cards read), whatever the request selects; Hot Chocolate picks the
  selected fields from the views.
- **A nested collection's own where and order.**
- **Text operators** (`contains`, `startsWith`) on a scalar Plasm reads
  through a create function, such as `Title`: Plasm reads it as its column but
  cannot reach the text inside.

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

## Translation

A request's query is built and translated for each request: a request names
values Plasm makes parameters, but its shape (which fields, which operators,
how they nest) is the request's. Translation is cheap under the runtime and
several times dearer in a native binary (benchmarks.md, "Steady state": a
read by key built per call costs 15 µs to translate under the runtime, 90 µs
natively, against a prepared read's 175 µs in all). Translating each declared
part once, so that a request only assembled them, would need: a way to put a
prepared query's argument into a built condition (an `Arg` is made only by
`Query.prepare` today), prepared queries of any number of arguments (today
one to three), and a bounded cache of templates keyed by a request's shape,
never by its text or values. None of it is built.
