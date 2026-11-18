// Kanban's cards over GraphQL. A request's where and order are read from Hot
// Chocolate's filter and sorting contexts by Plasm.HotChocolate and built on
// Kanban's cards query from the fields Kanban.Data declares (CardFields), so
// the database does both; C# writes no query.
using System.Collections.Concurrent;
using GreenDonut;
using HotChocolate.Data;
using HotChocolate.Resolvers;
using HotChocolate.Types.Pagination;
using Kanban;
using Kanban.Data;
using Npgsql;
using Plasm.HotChocolate;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Kanban")
    ?? throw new InvalidOperationException("Set the connection string 'Kanban' (ConnectionStrings:Kanban).");

// KANBAN_SQL=1 writes each command's SQL to standard output, one line each.
Action<string>? onSql = Environment.GetEnvironmentVariable("KANBAN_SQL") == "1"
    ? sql => Console.WriteLine("SQL: " + sql.Replace('\n', ' '))
    : null;
var dataSource = NpgsqlDataSource.Create(connectionString);
// Opening checks every query, the tables, and every declared card field.
var kanban = await KanbanData.OpenAsync(dataSource, onSql!, CancellationToken.None);
builder.Services.AddSingleton(kanban);
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddTypeExtension<Boards>()
    .AddTypeExtension<BoardCards>()
    .AddDataLoader<BoardCardsLoader>()
    .AddPlasmFiltering()
    .AddPlasmSorting()
    .AddPlasmUnion<StageView>();

var app = builder.Build();
app.MapGraphQL();
app.Run();

public class Query
{
    /// A page of cards: the where, the order and the page are the database's,
    /// and the total is counted only when a request asks for it.
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

/// The boards, and a board's cards with their own where and order: one
/// DataLoader batch per argument set, so every board's cards are one query.
[ExtendObjectType(typeof(Query))]
public class Boards
{
    public Task<BoardNode[]> GetBoards([Service] KanbanData kanban, CancellationToken ct) => kanban.BoardNodesAsync(ct);
}

[ExtendObjectType(typeof(BoardNode))]
public class BoardCards
{
    [UseFiltering(typeof(CardFilter))]
    [UseSorting(typeof(CardSort))]
    public Task<CardView[]> Cards([Parent] BoardNode board, IResolverContext context, BoardCardsLoader loader, CancellationToken ct) =>
        loader.Load(board.Id, PlasmRequest.BatchKey(context), PlasmRequest.Refinement(context, CardFields.all, CardFields.created), ct);
}

/// Every board's cards a request asks for with the same arguments, in one
/// query: keyed by board and argument set, one query per argument set.
public sealed class BoardCardsLoader(KanbanData kanban, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<(Guid Board, string Request), CardView[]>(scheduler, options)
{
    readonly ConcurrentDictionary<string, Func<Plasm.Query<Card>, Plasm.Query<Card>>> refinements = new();

    public Task<CardView[]> Load(Guid board, string request, Func<Plasm.Query<Card>, Plasm.Query<Card>> refine, CancellationToken ct)
    {
        refinements.TryAdd(request, refine);
        return LoadAsync((board, request), ct)!;
    }

    protected override async Task<IReadOnlyDictionary<(Guid Board, string Request), CardView[]>> LoadBatchAsync(
        IReadOnlyList<(Guid Board, string Request)> keys, CancellationToken ct)
    {
        var result = new Dictionary<(Guid Board, string Request), CardView[]>();
        foreach (var request in keys.GroupBy(k => k.Request))
        {
            var byBoard = await kanban.CardsOfBoardsAsync(request.Select(k => k.Board).Distinct(), refinements[request.Key], ct);
            foreach (var key in request) result[key] = byBoard[key.Board];
        }
        return result;
    }
}

/// A page's size when a request does not say, and the most it may say.
static class Paging
{
    public const int Default = 10;
    public const int Most = 50;
}

/// The filter and sort inputs, generated from Kanban's declared card fields.
public class CardFilter() : PlasmFilterInputType<Card>("CardFilterInput", CardFields.all);
public class CardSort() : PlasmSortInputType<Card>("CardSortInput", CardFields.all);

/// Visible to the scenarios, which start this program.
public partial class Program;
