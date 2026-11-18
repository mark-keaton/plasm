// Kanban over REST: each endpoint reads its request, calls one KanbanData
// method with the person signed in as the actor where a change needs one, and
// answers through Outcomes. Reads are open to anyone; every change needs
// someone signed in.
using System.Security.Claims;
using Kanban.Data;

namespace Kanban.Host;

public static class Rest
{
    record Versioned(int Version);

    public static void MapRest(this WebApplication app)
    {
        var changes = app.MapGroup("").RequireAuthorization();

        // The person who starts the card is the person signed in; a name in
        // the request is not read.
        changes.MapPost("/cards/{id:guid}/start", async (Guid id, Versioned input, ClaimsPrincipal user, KanbanData kanban, CancellationToken ct) =>
            Outcomes.Of(await kanban.StartAsync(id, input.Version, user.Actor(), ct)));
    }
}
