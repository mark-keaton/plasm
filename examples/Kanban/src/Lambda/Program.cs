// Kanban as an AWS Lambda function in C# (docs/aot.md, "Kanban's C#
// function"): every call is a method of Boards, the C# the seam writes over
// the F# one (Seam/Boards.g.cs), and what it returns is switched over with no
// default arm. C# writes no query, no transaction, no view and no mapping. An
// event names its call and arguments; the answer is the outcome's case by
// name, with its value: {"missing":{"what":"board nope"}}.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Kanban.Lambda;
using Plasm.CSharp;

Console.WriteLine($"to main {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F1} ms");

// Init, before the runtime asks for the first event: Boards, whose opening
// starts Plasm's repository, which checks every query and the tables, and
// makes the first connection.
var clock = Stopwatch.StartNew();
var connectionString = Environment.GetEnvironmentVariable("KANBAN_CS")
    ?? throw new InvalidOperationException("Set KANBAN_CS, the connection string.");
// KANBAN_STARTUP_CHECKS=skip opens without the start-up check, which the
// function's scenarios run in this binary over the same migrations.
var check = Environment.GetEnvironmentVariable("KANBAN_STARTUP_CHECKS") switch
{
    null or "" or "run" => true,
    "skip" => false,
    var other => throw new InvalidOperationException($"KANBAN_STARTUP_CHECKS is \"{other}\": run or skip."),
};
var kanban = check
    ? await Boards.OpenAsync(connectionString, TimeProvider.System, CancellationToken.None)
    : await Boards.OpenUncheckedAsync(connectionString, TimeProvider.System, CancellationToken.None);
Console.WriteLine($"Boards opened{(check ? "" : " without its start-up check")} {clock.Elapsed.TotalMilliseconds:F1} ms");

using var bootstrap = LambdaBootstrapBuilder.Create(
    (Func<Stream, ILambdaContext, Task<Stream>>)(async (input, context) =>
    {
        var started = Stopwatch.StartNew();
        // A missing or null argument throws here, before F# is called.
        var call = await JsonSerializer.DeserializeAsync(input, KanbanJson.Default.Call)
            ?? throw new JsonException("The event is null.");
        using var timeout = new CancellationTokenSource(context.RemainingTime);
        var answer = await call.Answer(kanban, timeout.Token);
        Console.WriteLine($"invocation {started.Elapsed.TotalMilliseconds:F1} ms");
        return answer;
    })).Build();
await bootstrap.RunAsync();

/// An event: which Boards method to call, by `call`, and its arguments.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "call")]
[JsonDerivedType(typeof(ReadBoard), "board")]
[JsonDerivedType(typeof(CreateBoard), "createBoard")]
[JsonDerivedType(typeof(AddCard), "addCard")]
[JsonDerivedType(typeof(StartCard), "start")]
[JsonDerivedType(typeof(RenameCard), "rename")]
abstract record Call
{
    public abstract Task<Stream> Answer(Boards k, CancellationToken ct);

    /// {"<name>": value}, written with the type's generated serializer.
    protected static Stream Case<T>(string name, T value, JsonTypeInfo<T> type)
    {
        var answer = new MemoryStream();
        using (var w = new Utf8JsonWriter(answer))
        {
            w.WriteStartObject();
            w.WritePropertyName(name);
            JsonSerializer.Serialize(w, value, type);
            w.WriteEndObject();
        }
        answer.Position = 0;
        return answer;
    }

    protected static Stream ChangeResult(ChangeResult<CardView> outcome) => outcome switch
    {
        CardView card => Case("card", card, SeamJson.Default.CardView),
        Missing missing => Case("missing", missing, SeamJson.Default.Missing),
        Refused refused => Case("refused", refused, SeamJson.Default.Refused),
        Stale stale => Case("stale", stale, SeamJson.Default.Stale),
        Invalid invalid => Case("invalid", invalid, SeamJson.Default.Invalid),
    };
}

sealed record ReadBoard(string Slug) : Call
{
    public override async Task<Stream> Answer(Boards k, CancellationToken ct) =>
        await k.BoardAsync(Slug, ct) switch
        {
            BoardView board => Case("board", board, SeamJson.Default.BoardView),
            Missing missing => Case("missing", missing, SeamJson.Default.Missing),
        };
}

sealed record CreateBoard(string Slug, string Name, int WipLimit) : Call
{
    public override async Task<Stream> Answer(Boards k, CancellationToken ct) =>
        await k.CreateBoardAsync(new NewBoard(Slug, Name, WipLimit), ct) switch
        {
            BoardSummaryView board => Case("created", board, SeamJson.Default.BoardSummaryView),
            Invalid invalid => Case("invalid", invalid, SeamJson.Default.Invalid),
        };
}

sealed record AddCard(string Board, string Title) : Call
{
    public override async Task<Stream> Answer(Boards k, CancellationToken ct) =>
        await k.AddCardAsync(Board, new NewCard(Title), ct) switch
        {
            CardView card => Case("card", card, SeamJson.Default.CardView),
            Missing missing => Case("missing", missing, SeamJson.Default.Missing),
            Invalid invalid => Case("invalid", invalid, SeamJson.Default.Invalid),
        };
}

sealed record StartCard(Guid Card, int Version, string Assignee) : Call
{
    public override async Task<Stream> Answer(Boards k, CancellationToken ct) =>
        ChangeResult(await k.StartAsync(Card, new Start(Version, Assignee), ct));
}

sealed record RenameCard(Guid Card, int Version, string Title) : Call
{
    public override async Task<Stream> Answer(Boards k, CancellationToken ct) =>
        ChangeResult(await k.RenameAsync(Card, new Rename(Version, Title), ct));
}

/// The events, read by generated code: no reflection. An argument left out
/// or null is refused when the event is read, so none reaches F#. What the
/// function answers is written by the seam's context (Seam/SeamJson.g.cs).
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Call))]
partial class KanbanJson : JsonSerializerContext;
