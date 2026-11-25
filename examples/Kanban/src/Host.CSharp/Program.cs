// Kanban served by C#: every endpoint calls Boards, the C# class the seam
// writes over the F# one (Seam/Boards.g.cs), and ToHttp turns its outcome
// into a response (Seam/SeamHttp.g.cs). C# writes no query, no transaction,
// no view and no mapping.
using Kanban.Host.CSharp;
using Plasm.CSharp;

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
// A minimal API binds with these options, not the context's own: both refuse
// a null or missing argument, so none reaches F# (docs/csharp.md).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, SeamJson.Default);
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

var connectionString = builder.Configuration.GetConnectionString("Kanban")
    ?? throw new InvalidOperationException("Set the connection string 'Kanban' (ConnectionStrings:Kanban).");

// Boards, opened as the host starts: Plasm checks every query and the tables
// first, so one that would fail stops the host before it serves.
builder.Services.AddBoards(connectionString);

var app = builder.Build();

// ToHttp: the value 200 (a board added 201, at its Location), Missing 404,
// Invalid 400, Refused 422, Stale 409, each with the case as its body.
app.MapGet("/boards", async (Boards b, CancellationToken ct) => Results.Ok(await b.BoardsAsync(ct)));

app.MapGet("/boards/{slug}", (string slug, Boards b, CancellationToken ct) =>
    b.BoardAsync(slug, ct).ToHttp());

app.MapPost("/boards", (NewBoard input, Boards b, CancellationToken ct) =>
    b.CreateBoardAsync(input, ct).ToHttp(board => $"/boards/{board.Slug}"));

// A card added is created, at its Location: a RequestResult's value is 200 to
// ToHttp, so this one answer is switched here.
app.MapPost("/boards/{slug}/cards", async (string slug, NewCard input, Boards b, CancellationToken ct) =>
    await b.AddCardAsync(slug, input, ct) switch
    {
        CardView card => Results.Created($"/cards/{card.Id}", card),
        Missing missing => Results.NotFound(missing),
        Invalid invalid => Results.BadRequest(invalid),
    });

app.MapPost("/cards/{id:guid}/start", (Guid id, Start input, Boards b, CancellationToken ct) =>
    b.StartAsync(id, input, ct).ToHttp());

app.MapPut("/cards/{id:guid}/title", (Guid id, Rename input, Boards b, CancellationToken ct) =>
    b.RenameAsync(id, input, ct).ToHttp());

app.Run();

/// Visible to the scenarios, which start this program.
public partial class Program;
