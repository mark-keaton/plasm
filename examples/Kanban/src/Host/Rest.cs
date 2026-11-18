// Kanban over REST: each endpoint reads its request, calls one KanbanData
// method with the person signed in as the actor where a change needs one, and
// answers through Outcomes. Reads are open to anyone; every change needs
// someone signed in. A change to a card carries the version the caller last
// saw, so a change made since is answered 409.
using System.Security.Claims;
using Kanban.Data;

namespace Kanban.Host;

public static class Rest
{
    record NewBoard(string Slug, string Name, int WipLimit);
    record NewCard(string Title);
    record Versioned(int Version);
    record WithReason(int Version, string Reason);
    record WithText(int Version, string Text);
    record WithTitle(int Version, string Title);
    record WithLabels(int Version, string[] Labels);
    record NewComment(string Body);

    public static void MapRest(this WebApplication app)
    {
        app.MapGet("/boards", (KanbanData kanban, CancellationToken ct) => kanban.BoardsAsync(ct));

        app.MapGet("/boards/{slug}", async (string slug, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.BoardAsync(slug, ct)));

        app.MapGet("/boards/{slug}/done", async (string slug, int? page, int? size, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.DonePageAsync(slug, page ?? 1, size ?? 10, ct)));

        var changes = app.MapGroup("").RequireAuthorization();

        changes.MapGet("/me/work", (ClaimsPrincipal user, KanbanData kanban, CancellationToken ct) =>
            kanban.WorkOfAsync(user.Actor(), ct));

        changes.MapPost("/boards", async (NewBoard input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.CreateBoardAsync(input.Slug, input.Name, input.WipLimit, ct)));

        changes.MapPost("/boards/{slug}/cards", async (string slug, NewCard input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.AddCardAsync(slug, input.Title, ct)));

        changes.MapPost("/boards/{slug}/send-all-back", async (string slug, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.SendAllBackAsync(slug, ct)));

        var card = changes.MapGroup("/cards/{id:guid}");

        // The person who starts the card is the person signed in; a name in
        // the request is not read.
        card.MapPost("/start", async (Guid id, Versioned input, ClaimsPrincipal user, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.StartAsync(id, input.Version, user.Actor(), ct)));

        card.MapPost("/ship", async (Guid id, Versioned input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.ShipAsync(id, input.Version, ct)));

        card.MapPost("/drop", async (Guid id, WithReason input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.DropAsync(id, input.Version, input.Reason, ct)));

        card.MapPost("/send-back", async (Guid id, Versioned input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.SendBackAsync(id, input.Version, ct)));

        card.MapPost("/reopen", async (Guid id, Versioned input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.ReopenAsync(id, input.Version, ct)));

        card.MapPost("/block", async (Guid id, WithReason input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.BlockAsync(id, input.Version, input.Reason, ct)));

        card.MapPost("/unblock", async (Guid id, Versioned input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.UnblockAsync(id, input.Version, ct)));

        card.MapPut("/title", async (Guid id, WithTitle input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.RenameAsync(id, input.Version, input.Title, ct)));

        card.MapPut("/labels", async (Guid id, WithLabels input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.RelabelAsync(id, input.Version, input.Labels, ct)));

        card.MapPost("/items", async (Guid id, WithText input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.AddItemAsync(id, input.Version, input.Text, ct)));

        card.MapPost("/items/tick", async (Guid id, WithText input, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.TickAsync(id, input.Version, input.Text, ct)));

        // The comment's author is the person signed in.
        card.MapPost("/comments", async (Guid id, NewComment input, ClaimsPrincipal user, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.CommentAsync(id, user.Actor(), input.Body, ct)));
    }
}
