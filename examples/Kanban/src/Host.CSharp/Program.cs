// Kanban served by C#: every endpoint calls KanbanData, the F# facade over the
// domain and its queries, and turns what it returns into a response. C# here
// writes no query and composes no transaction.
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanban.Data;
using Plasm.CSharp;
using Npgsql;

// Started by a scenario (KANBAN_EXIT_WITH_STDIN=1): exit when the test
// process that holds this host's standard input goes, so no host outlives a
// test run that died.
if (Environment.GetEnvironmentVariable("KANBAN_EXIT_WITH_STDIN") == "1")
    _ = Task.Run(() =>
    {
        Console.In.ReadToEnd();
        Environment.Exit(0);
    });

var builder = WebApplication.CreateSlimBuilder(args);
// The context's own options do not reach a minimal API's binding: the host's
// options must refuse a null or missing argument themselves, so that none
// reaches F# (docs/csharp.md).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, KanbanJson.Default);
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

var connectionString = builder.Configuration.GetConnectionString("Kanban")
    ?? throw new InvalidOperationException("Set the connection string 'Kanban' (ConnectionStrings:Kanban).");

// Opening the facade starts Plasm's repository, which checks every query and
// the tables before anything is served.
var dataSource = NpgsqlDataSource.Create(connectionString);
var kanban = await KanbanData.OpenAsync(dataSource, CancellationToken.None);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(kanban);

var app = builder.Build();

app.MapGet("/boards", (KanbanData k, CancellationToken ct) => k.BoardsAsync(ct));

app.MapGet("/boards/{slug}", async (string slug, KanbanData k, CancellationToken ct) =>
    await k.BoardAsync(slug, ct) switch
    {
        BoardView board => Results.Ok(board),
        Missing missing => Results.NotFound(missing),
    });

app.MapPost("/boards", async (NewBoard input, KanbanData k, CancellationToken ct) =>
    await k.CreateBoardAsync(input.Slug, input.Name, input.WipLimit, ct) switch
    {
        BoardSummary board => Results.Created($"/boards/{board.Slug}", board),
        Invalid invalid => Results.BadRequest(invalid),
    });

app.MapPost("/boards/{slug}/cards", async (string slug, NewCard input, KanbanData k, CancellationToken ct) =>
    await k.AddCardAsync(slug, input.Title, ct) switch
    {
        CardView card => Results.Created($"/cards/{card.Id}", card),
        Missing missing => Results.NotFound(missing),
        Invalid invalid => Results.BadRequest(invalid),
    });

app.MapPost("/cards/{id:guid}/start", async (Guid id, Start input, KanbanData k, CancellationToken ct) =>
    Changed(await k.StartAsync(id, input.Version, input.Assignee, ct)));

app.MapPut("/cards/{id:guid}/title", async (Guid id, Rename input, KanbanData k, CancellationToken ct) =>
    Changed(await k.RenameAsync(id, input.Version, input.Title, ct)));

app.Run();

static IResult Changed(Changed<CardView> outcome) => outcome switch
{
    CardView card => Results.Ok(card),
    Missing missing => Results.NotFound(missing),
    Refused refused => Results.UnprocessableEntity(refused),
    Stale stale => Results.Conflict(stale),
    Invalid invalid => Results.BadRequest(invalid),
};

record NewBoard(string Slug, string Name, int WipLimit);
record NewCard(string Title);
record Start(int Version, string Assignee);
record Rename(int Version, string Title);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BoardSummary[]))]
[JsonSerializable(typeof(BoardView))]
[JsonSerializable(typeof(CardView))]
[JsonSerializable(typeof(InBacklog))]
[JsonSerializable(typeof(InProgress))]
[JsonSerializable(typeof(WasShipped))]
[JsonSerializable(typeof(WasDropped))]
[JsonSerializable(typeof(Missing))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(Stale))]
[JsonSerializable(typeof(Invalid))]
[JsonSerializable(typeof(NewBoard))]
[JsonSerializable(typeof(NewCard))]
[JsonSerializable(typeof(Start))]
[JsonSerializable(typeof(Rename))]
partial class KanbanJson : JsonSerializerContext;

/// Visible to the scenarios, which start this program.
public partial class Program;
