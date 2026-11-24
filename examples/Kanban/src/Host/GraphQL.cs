// Kanban over GraphQL: reads, and every change as a mutation. A request's
// where and order are read from Hot Chocolate's filter and sorting contexts by
// Plasm.HotChocolate and built on Kanban's cards query from the fields
// Kanban.Data declares (CardFields), so the database does both; C# writes no
// query. A mutation answers the facade's outcome as a union of its cases.
using System.Collections.Concurrent;
using System.Security.Claims;
using GreenDonut;
using HotChocolate.Authorization;
using HotChocolate.Data;
using HotChocolate.Resolvers;
using HotChocolate.Types.Pagination;
using Kanban;
using Kanban.Data;
using Plasm.CSharp;
using Plasm;
using Plasm.HotChocolate;

namespace Kanban.Host;

public class Query
{
    /// A page of cards: the where, the order and the page are the database's,
    /// and the total is counted only when a request asks for it.
    [UseOffsetPaging(DefaultPageSize = Paging.Default, MaxPageSize = Paging.Most, IncludeTotalCount = true)]
    [UseFiltering(typeof(CardFilter))]
    [UseSorting(typeof(CardSort))]
    public async Task<CollectionSegment<Selected<Card>>> Cards(IResolverContext context, [Service] KanbanData kanban, CancellationToken ct)
    {
        Plasm.Query<Card> Filtered(Plasm.Query<Card> query) => PlasmRequest.Apply(context, CardFields.all, query, CardFields.created);
        var chosen = PlasmRequest.Chosen(context, CardRead.all);
        var rows = await kanban.CardsAsync(chosen, query => PlasmRequest.Page(context, Filtered(query), Paging.Default), ct);
        return await PlasmRequest.Segment(context, rows, Paging.Default, () => kanban.CountCardsAsync(Filtered, ct));
    }

    /// A card by its id: the card, or Missing (the union CardResult).
    public Task<FindResult<CardView>> Card(Guid id, [Service] KanbanData kanban, CancellationToken ct) => kanban.CardAsync(id, ct);
}

/// Every change, each the facade method its REST endpoint calls, answering
/// the facade's outcome as a union of its cases. A change needs someone
/// signed in, and is made as the person signed in (`user.Actor()`).
[Authorize]
public class Mutation
{
    public Task<AddResult<BoardSummary>> CreateBoard(string slug, string name, int wipLimit, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.CreateBoardAsync(slug, name, wipLimit, ct);

    public Task<RequestResult<CardView>> AddCard(string board, string title, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.AddCardAsync(board, title, ct);

    public Task<FindResult<SentBack>> SendAllBack(string board, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.SendAllBackAsync(board, ct);

    public Task<ChangeResult<CardView>> StartCard(Guid card, int version, ClaimsPrincipal user, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.StartAsync(card, version, user.Actor(), ct);

    public Task<ChangeResult<CardView>> ShipCard(Guid card, int version, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.ShipAsync(card, version, ct);

    public Task<ChangeResult<CardView>> DropCard(Guid card, int version, string reason, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.DropAsync(card, version, reason, ct);

    public Task<ChangeResult<CardView>> SendCardBack(Guid card, int version, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.SendBackAsync(card, version, ct);

    public Task<ChangeResult<CardView>> ReopenCard(Guid card, int version, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.ReopenAsync(card, version, ct);

    public Task<ChangeResult<CardView>> BlockCard(Guid card, int version, string reason, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.BlockAsync(card, version, reason, ct);

    public Task<ChangeResult<CardView>> UnblockCard(Guid card, int version, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.UnblockAsync(card, version, ct);

    public Task<ChangeResult<CardView>> RenameCard(Guid card, int version, string title, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.RenameAsync(card, version, title, ct);

    public Task<ChangeResult<CardView>> RelabelCard(Guid card, int version, string[] labels, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.RelabelAsync(card, version, labels, ct);

    public Task<ChangeResult<CardView>> AddItem(Guid card, int version, string text, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.AddItemAsync(card, version, text, ct);

    public Task<ChangeResult<CardView>> TickItem(Guid card, int version, string text, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.TickAsync(card, version, text, ct);

    public Task<ChangeResult<CardView>> Comment(Guid card, string body, ClaimsPrincipal user, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.CommentAsync(card, user.Actor(), body, ct);
}

/// The boards, and a board's cards as a page with their own where and order:
/// one DataLoader batch per argument set, so every board's page is one query.
[ExtendObjectType(typeof(Query))]
public class Boards
{
    public Task<BoardNode[]> GetBoards([Service] KanbanData kanban, CancellationToken ct) => kanban.BoardNodesAsync(ct);
}

[ExtendObjectType(typeof(BoardNode))]
public class BoardCards
{
    /// A board's page of cards: every board's page read in one command, each
    /// numbered among its own board's cards (PlasmRequest.PageEach), and
    /// every board's total in one more, only when selected. LoadedSegment
    /// starts both loads before it awaits either, so every board joins the
    /// same two batches.
    [UseOffsetPaging(DefaultPageSize = Paging.Default, MaxPageSize = Paging.Most, IncludeTotalCount = true)]
    [UseFiltering(typeof(CardFilter))]
    [UseSorting(typeof(CardSort))]
    public Task<CollectionSegment<Selected<Card>>> Cards(
        [Parent] BoardNode board, IResolverContext context, BoardCardsLoader cards, BoardCountsLoader counts, CancellationToken ct)
    {
        var chosen = PlasmRequest.Chosen(context, CardRead.all);
        var request = PlasmRequest.BatchKey(context, chosen);
        var refine = PlasmRequest.Refinement(context, CardFields.all, CardFields.created);
        var page = PlasmRequest.PageEach(context, CardFields.board, Paging.Default);
        var where = PlasmRequest.Where(context, CardFields.all);
        return PlasmRequest.LoadedSegment(context, Paging.Default,
            () => cards.Load(board.Id, request, chosen, query => page.Invoke(refine.Invoke(query)), ct),
            () => counts.Load(board.Id, request, where, ct));
    }
}

/// Every board's cards a request asks for with the same arguments and the
/// same selection, in one query: keyed by board and request, one query each.
public sealed class BoardCardsLoader(KanbanData kanban, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<(Guid Board, string Request), Selected<Card>[]>(scheduler, options)
{
    readonly ConcurrentDictionary<string, (IReadOnlyList<ReadField<Card>> Chosen, Func<Plasm.Query<Card>, Plasm.Query<Card>> Refine)> requests = new();

    public Task<Selected<Card>[]> Load(Guid board, string request, IReadOnlyList<ReadField<Card>> chosen, Func<Plasm.Query<Card>, Plasm.Query<Card>> refine, CancellationToken ct)
    {
        requests.TryAdd(request, (chosen, refine));
        return LoadAsync((board, request), ct)!;
    }

    protected override async Task<IReadOnlyDictionary<(Guid Board, string Request), Selected<Card>[]>> LoadBatchAsync(
        IReadOnlyList<(Guid Board, string Request)> keys, CancellationToken ct)
    {
        var result = new Dictionary<(Guid Board, string Request), Selected<Card>[]>();
        foreach (var request in keys.GroupBy(k => k.Request))
        {
            var (chosen, refine) = requests[request.Key];
            var byBoard = await kanban.CardsOfBoardsAsync(request.Select(k => k.Board).Distinct(), chosen, refine, ct);
            foreach (var key in request) result[key] = byBoard[key.Board];
        }
        return result;
    }
}

/// Every board's total a request asks for with the same arguments, in one
/// command grouped by board.
public sealed class BoardCountsLoader(KanbanData kanban, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<(Guid Board, string Request), int>(scheduler, options)
{
    readonly ConcurrentDictionary<string, Func<Plasm.Query<Card>, Plasm.Query<Card>>> wheres = new();

    public Task<int> Load(Guid board, string request, Func<Plasm.Query<Card>, Plasm.Query<Card>> where, CancellationToken ct)
    {
        wheres.TryAdd(request, where);
        return LoadAsync((board, request), ct);
    }

    protected override async Task<IReadOnlyDictionary<(Guid Board, string Request), int>> LoadBatchAsync(
        IReadOnlyList<(Guid Board, string Request)> keys, CancellationToken ct)
    {
        var result = new Dictionary<(Guid Board, string Request), int>();
        foreach (var request in keys.GroupBy(k => k.Request))
        {
            var byBoard = await kanban.CountCardsOfBoardsAsync(request.Select(k => k.Board).Distinct(), wheres[request.Key], ct);
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

