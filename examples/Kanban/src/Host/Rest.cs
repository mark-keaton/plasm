// Kanban over REST: each endpoint reads its request, calls one Boards method
// with the person signed in as the actor where a change needs one, and
// answers through ToHttp (Seam/SeamHttp.g.cs). Reads are open to anyone;
// every change needs someone signed in. A change to a card carries the
// version the caller last saw, so a change made since is answered 409. A
// body is the request record the seam writes from Kanban.Data's (Versioned,
// WithReason, ...), passed whole.
using System.Security.Claims;
using Plasm.CSharp;

namespace Kanban.Host;

public static class Rest
{
    public static void MapRest(this WebApplication app)
    {
        app.MapGet("/boards", async (Boards kanban, CancellationToken ct) => Results.Ok(await kanban.BoardsAsync(ct)));

        app.MapGet("/boards/{slug}", (string slug, Boards kanban, CancellationToken ct) =>
            kanban.BoardAsync(slug, ct).ToHttp());

        app.MapGet("/boards/{slug}/done", (string slug, int? page, int? size, Boards kanban, CancellationToken ct) =>
            kanban.DonePageAsync(slug, page ?? 1, size ?? 10, ct).ToHttp());

        var changes = app.MapGroup("").RequireAuthorization();

        changes.MapGet("/me/work", async (ClaimsPrincipal user, Boards kanban, CancellationToken ct) =>
            Results.Ok(await kanban.WorkOfAsync(user.Actor(), ct)));

        changes.MapPost("/boards", (NewBoard input, Boards kanban, CancellationToken ct) =>
            kanban.CreateBoardAsync(input, ct).ToHttp(board => $"/boards/{board.Slug}"));

        // A card added is created, at its Location: a RequestResult's value is
        // 200 to ToHttp, so this one answer is switched here.
        changes.MapPost("/boards/{slug}/cards", async (string slug, NewCard input, Boards kanban, CancellationToken ct) =>
            await kanban.AddCardAsync(slug, input, ct) switch
            {
                CardView card => Results.Created($"/cards/{card.Id}", card),
                Missing missing => Results.NotFound(missing),
                Invalid invalid => Results.BadRequest(invalid),
            });

        changes.MapPost("/boards/{slug}/send-all-back", (string slug, Boards kanban, CancellationToken ct) =>
            kanban.SendAllBackAsync(slug, ct).ToHttp());

        var card = changes.MapGroup("/cards/{id:guid}");

        // The person who starts the card is the person signed in; a name in
        // the request is not read.
        card.MapPost("/start", (Guid id, Versioned input, ClaimsPrincipal user, Boards kanban, CancellationToken ct) =>
            kanban.StartAsync(id, new Start(input.Version, user.Actor()), ct).ToHttp());

        card.MapPost("/ship", (Guid id, Versioned input, Boards kanban, CancellationToken ct) =>
            kanban.ShipAsync(id, input, ct).ToHttp());

        card.MapPost("/drop", (Guid id, WithReason input, Boards kanban, CancellationToken ct) =>
            kanban.DropAsync(id, input, ct).ToHttp());

        card.MapPost("/send-back", (Guid id, Versioned input, Boards kanban, CancellationToken ct) =>
            kanban.SendBackAsync(id, input, ct).ToHttp());

        card.MapPost("/reopen", (Guid id, Versioned input, Boards kanban, CancellationToken ct) =>
            kanban.ReopenAsync(id, input, ct).ToHttp());

        card.MapPost("/block", (Guid id, WithReason input, Boards kanban, CancellationToken ct) =>
            kanban.BlockAsync(id, input, ct).ToHttp());

        card.MapPost("/unblock", (Guid id, Versioned input, Boards kanban, CancellationToken ct) =>
            kanban.UnblockAsync(id, input, ct).ToHttp());

        card.MapPut("/title", (Guid id, Rename input, Boards kanban, CancellationToken ct) =>
            kanban.RenameAsync(id, input, ct).ToHttp());

        card.MapPut("/labels", (Guid id, WithLabels input, Boards kanban, CancellationToken ct) =>
            kanban.RelabelAsync(id, input, ct).ToHttp());

        card.MapPost("/items", (Guid id, WithText input, Boards kanban, CancellationToken ct) =>
            kanban.AddItemAsync(id, input, ct).ToHttp());

        card.MapPost("/items/tick", (Guid id, WithText input, Boards kanban, CancellationToken ct) =>
            kanban.TickAsync(id, input, ct).ToHttp());

        // The comment's author is the person signed in.
        card.MapPost("/comments", (Guid id, NewComment input, ClaimsPrincipal user, Boards kanban, CancellationToken ct) =>
            kanban.CommentAsync(id, user.Actor(), input, ct).ToHttp());
    }
}
