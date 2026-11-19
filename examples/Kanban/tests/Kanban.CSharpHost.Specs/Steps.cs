using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Reqnroll;

using Kanban.Hosting;

namespace Kanban.CSharpHost.Specs;

[Binding]
public sealed class Steps : IAsyncDisposable
{
    HostProcess? host;
    HttpClient http = null!;
    string connectionString = "";
    HttpStatusCode status;
    string body = "";
    readonly Dictionary<string, Guid> cards = new();

    /// Reqnroll does not dispose a binding asynchronously: the host is stopped
    /// here, when the scenario ends, and then its database is dropped.
    [AfterScenario]
    public async Task StopHost()
    {
        http?.Dispose();
        if (host is not null) await host.DisposeAsync();
        host = null;
        if (connectionString != "") await Databases.Drop(connectionString);
        connectionString = "";
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }

    async Task Send(HttpMethod method, string path, object? content = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (content is not null) request.Content = JsonContent.Create(content);
        using var answer = await http.SendAsync(request);
        status = answer.StatusCode;
        body = await answer.Content.ReadAsStringAsync();
    }

    void Expect(HttpStatusCode expected, string what)
    {
        if (status != expected) throw new Exception($"{what}: expected {(int)expected} but was {(int)status}: {body}");
    }

    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected\n  {expected}\nbut was\n  {actual}");
    }

    async Task<JsonObject> Card(string title)
    {
        await Send(HttpMethod.Get, "/boards/alpha");
        Expect(HttpStatusCode.OK, "reading board alpha");
        return JsonNode.Parse(body)!["cards"]!.AsArray().Select(c => c!.AsObject())
            .Single(c => (Guid)c["id"]! == cards[title]);
    }

    // -- Given ----------------------------------------------------------------

    [Given("the C# host serves a migrated database")]
    public async Task HostServes()
    {
        connectionString = await Databases.Migrated();
        host = await HostProcess.Start(typeof(Program).Assembly.Location, "KANBAN_CSHARP_HOST", connectionString, "/boards");
        http = new HttpClient { BaseAddress = host.Address };
    }

    [Given("board {string} named {string} allows {int} card(s) in progress")]
    public async Task BoardAllows(string slug, string name, int limit)
    {
        await Send(HttpMethod.Post, "/boards", new { slug, name, wipLimit = limit });
        Expect(HttpStatusCode.Created, $"creating board {slug}");
    }

    [Given("board {string} has the cards {string}, {string}")]
    public async Task BoardHasCards(string slug, string first, string second)
    {
        foreach (var title in new[] { first, second })
        {
            await Send(HttpMethod.Post, $"/boards/{slug}/cards", new { title });
            Expect(HttpStatusCode.Created, $"adding {title}");
            cards[title] = (Guid)JsonNode.Parse(body)!["id"]!;
        }
    }

    /// Written to the tables directly: the C# host has no endpoint for either.
    [Given("the card {string} has a comment by {string} saying {string} and the label {string}")]
    public async Task CommentAndLabel(string title, string author, string text, string label)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var labelId = Guid.NewGuid();
        await using var write = new NpgsqlBatch(connection)
        {
            BatchCommands =
            {
                new("insert into comments (id, card, author, body, at) values ($1, $2, $3, $4, now())")
                {
                    Parameters = { new() { Value = Guid.NewGuid() }, new() { Value = cards[title] }, new() { Value = author }, new() { Value = text } },
                },
                new("insert into labels (id, name) values ($1, $2)") { Parameters = { new() { Value = labelId }, new() { Value = label } } },
                new("insert into card_labels (card_id, label_id) values ($1, $2)") { Parameters = { new() { Value = cards[title] }, new() { Value = labelId } } },
            },
        };
        await write.ExecuteNonQueryAsync();
    }

    [Given("{string} has started {string}")]
    public async Task HasStarted(string person, string title)
    {
        await Send(HttpMethod.Post, $"/cards/{cards[title]}/start", new { version = 1, assignee = person });
        Expect(HttpStatusCode.OK, $"{person} starting {title}");
    }

    // -- When -----------------------------------------------------------------

    [When("I ask for the boards")]
    public Task AskBoards() => Send(HttpMethod.Get, "/boards");

    [When("I ask for board {string}")]
    public Task AskBoard(string slug) => Send(HttpMethod.Get, $"/boards/{slug}");

    [When("{string} starts {string} from version {int}")]
    public Task Starts(string person, string title, int version) =>
        Send(HttpMethod.Post, $"/cards/{cards[title]}/start", new { version, assignee = person });

    [When("{string} renames {string} to {string} from version {int}")]
    public Task Renames(string _, string title, string to, int version) =>
        Send(HttpMethod.Put, $"/cards/{cards[title]}/title", new { version, title = to });

    [When("I add a card titled {string} to board {string}")]
    public Task AddCard(string title, string slug) => Send(HttpMethod.Post, $"/boards/{slug}/cards", new { title });

    [When("I create board {string} named {string} allowing {int} cards in progress")]
    public Task CreateBoard(string slug, string name, int limit) =>
        Send(HttpMethod.Post, "/boards", new { slug, name, wipLimit = limit });

    // -- Then -----------------------------------------------------------------

    [Then("the answer is {int}")]
    public void AnswerIs(int expected) => Expect((HttpStatusCode)expected, "the answer");

    [Then("the answer's JSON is {}")]
    public void AnswerJson(string expected) => Equal(expected, body, "the answer's JSON");

    [Then("the boards are {string}")]
    public void BoardsAre(string expected)
    {
        var boards = JsonNode.Parse(body)!.AsArray().Select(b =>
        {
            var newest = b!["newest"]!.AsArray().Select(t => (string)t!).ToList();
            return $"{b["name"]}: {b["backlog"]} in backlog, newest {(newest.Count == 0 ? "none" : string.Join(", ", newest))}";
        });
        Equal(expected, string.Join("; ", boards), "the boards");
    }

    [Then("its cards are {string}")]
    public void CardsAre(string expected)
    {
        static string Listed(IEnumerable<string> items) { var l = items.ToList(); return l.Count == 0 ? "none" : string.Join(", ", l); }
        var shown = JsonNode.Parse(body)!["cards"]!.AsArray().Select(c =>
            $"{c!["title"]} ({c["stage"]!["kind"]}, v{c["version"]}, labels {Listed(c["labels"]!.AsArray().Select(l => (string)l!))}, " +
            $"comments {Listed(c["comments"]!.AsArray().Select(m => $"{m!["author"]}: {m["body"]}"))})");
        Equal(expected, string.Join("; ", shown), "the board's cards");
    }

    [Then("the first card's JSON, without its id, is {}")]
    public void FirstCardJson(string expected)
    {
        var card = JsonNode.Parse(body)!["cards"]![0]!.AsObject();
        card.Remove("id");
        Equal(expected, card.ToJsonString(), "the first card's JSON");
    }

    JsonObject StageIn(string title) =>
        JsonNode.Parse(body)!["cards"]!.AsArray().Single(c => (Guid)c!["id"]! == cards[title])!["stage"]!.AsObject();

    [Then("the stage of {string} in the answer has the kind {string}, the assignee {string} and a start time")]
    public void StageDoing(string title, string kind, string assignee)
    {
        var stage = StageIn(title);
        Equal((kind, assignee), ((string?)stage["kind"], (string?)stage["assignee"]), $"{title}'s stage");
        Equal("kind,assignee,since", string.Join(",", stage.Select(p => p.Key)), $"{title}'s stage's fields");
        DateTimeOffset.Parse((string)stage["since"]!);
    }

    [Then("the stage of {string} in the answer is {}")]
    public void StageIs(string title, string expected) => Equal(expected, StageIn(title).ToJsonString(), $"{title}'s stage");

    [Then("the answer names the card {string}")]
    public void NamesCard(string title) => Equal(cards[title], (Guid)JsonNode.Parse(body)!["card"]!, "the card named");

    [Then("the card {string} is in the backlog at version {int}")]
    public async Task InBacklog(string title, int version)
    {
        var card = await Card(title);
        Equal(("backlog", version), ((string)card["stage"]!["kind"]!, (int)card["version"]!), $"{title}'s stage and version");
    }

    [Then("the card {string} is titled {string} at version {int}")]
    public async Task TitledAt(string title, string expected, int version)
    {
        var card = await Card(title);
        Equal((expected, version), ((string)card["title"]!, (int)card["version"]!), $"{title}'s title and version");
    }
}
