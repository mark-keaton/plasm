// Kanban as an AWS Lambda function in C# (docs/aot.md, "Kanban's C#
// function"): every call is a KanbanData method, the F# facade over the
// domain and its queries, and what it returns is switched over with no
// default arm. C# writes no query and composes no transaction. An event names
// its call and arguments; the answer is the outcome's case by name, with its
// value: {"missing":{"what":"board nope"}}.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Kanban.Data;
using Npgsql;

Console.WriteLine($"to main {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F1} ms");

// Init, before the runtime asks for the first event: the data source, and
// KanbanData, whose opening starts Plasm's repository, which checks every
// query and the tables, and makes the first connection.
var clock = Stopwatch.StartNew();
var connectionString = Environment.GetEnvironmentVariable("KANBAN_CS")
    ?? throw new InvalidOperationException("Set KANBAN_CS, the connection string.");
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().EnableTransportSecurity().Build();
var kanban = await KanbanData.OpenAsync(dataSource, CancellationToken.None);
Console.WriteLine($"KanbanData opened {clock.Elapsed.TotalMilliseconds:F1} ms");

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

/// An event: which KanbanData method to call, by `call`, and its arguments.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "call")]
[JsonDerivedType(typeof(ReadBoard), "board")]
[JsonDerivedType(typeof(CreateBoard), "createBoard")]
[JsonDerivedType(typeof(AddCard), "addCard")]
[JsonDerivedType(typeof(StartCard), "start")]
[JsonDerivedType(typeof(RenameCard), "rename")]
abstract record Call
{
    public abstract Task<Stream> Answer(KanbanData k, CancellationToken ct);

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

    protected static Stream Changed(ChangeOutcome outcome) => outcome switch
    {
        CardView card => Case("card", card, KanbanJson.Default.CardView),
        Missing missing => Case("missing", missing, KanbanJson.Default.Missing),
        Refused refused => Case("refused", refused, KanbanJson.Default.Refused),
        Stale stale => Case("stale", stale, KanbanJson.Default.Stale),
        Invalid invalid => Case("invalid", invalid, KanbanJson.Default.Invalid),
    };
}

sealed record ReadBoard(string Slug) : Call
{
    public override async Task<Stream> Answer(KanbanData k, CancellationToken ct) =>
        await k.BoardAsync(Slug, ct) switch
        {
            BoardView board => Case("board", board, KanbanJson.Default.BoardView),
            Missing missing => Case("missing", missing, KanbanJson.Default.Missing),
        };
}

sealed record CreateBoard(string Slug, string Name, int WipLimit) : Call
{
    public override async Task<Stream> Answer(KanbanData k, CancellationToken ct) =>
        await k.CreateBoardAsync(Slug, Name, WipLimit, ct) switch
        {
            BoardSummary board => Case("created", board, KanbanJson.Default.BoardSummary),
            Invalid invalid => Case("invalid", invalid, KanbanJson.Default.Invalid),
        };
}

sealed record AddCard(string Board, string Title) : Call
{
    public override async Task<Stream> Answer(KanbanData k, CancellationToken ct) =>
        await k.AddCardAsync(Board, Title, ct) switch
        {
            CardView card => Case("card", card, KanbanJson.Default.CardView),
            Missing missing => Case("missing", missing, KanbanJson.Default.Missing),
            Invalid invalid => Case("invalid", invalid, KanbanJson.Default.Invalid),
        };
}

sealed record StartCard(Guid Card, int Version, string Assignee) : Call
{
    public override async Task<Stream> Answer(KanbanData k, CancellationToken ct) =>
        Changed(await k.StartAsync(Card, Version, Assignee, ct));
}

sealed record RenameCard(Guid Card, int Version, string Title) : Call
{
    public override async Task<Stream> Answer(KanbanData k, CancellationToken ct) =>
        Changed(await k.RenameAsync(Card, Version, Title, ct));
}

/// Every type read or written, by generated code: no reflection. An argument
/// left out or null is refused when the event is read, so none reaches F#.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Call))]
[JsonSerializable(typeof(BoardView))]
[JsonSerializable(typeof(BoardSummary))]
[JsonSerializable(typeof(CardView))]
[JsonSerializable(typeof(InBacklog))]
[JsonSerializable(typeof(InProgress))]
[JsonSerializable(typeof(WasShipped))]
[JsonSerializable(typeof(WasDropped))]
[JsonSerializable(typeof(Missing))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(Stale))]
[JsonSerializable(typeof(Invalid))]
partial class KanbanJson : JsonSerializerContext;
