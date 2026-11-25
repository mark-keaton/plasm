using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Reqnroll;

namespace Kanban.Host.Specs;

/// features/host/rest.feature
public sealed partial class Steps
{
    /// The quoted titles in a list such as `"A", "B" and "C"`.
    static IEnumerable<string> Quoted(string list) => Regex.Matches(list, "\"([^\"]*)\"").Select(m => m.Groups[1].Value);

    /// A change to the card `title` at its current version, posted to the
    /// card's `action` with the body's other fields.
    async Task Change(HttpMethod method, string title, string action, Dictionary<string, object?>? fields = null)
    {
        var (id, version) = await Card(title);
        var input = new Dictionary<string, object?>(fields ?? new()) { ["version"] = version };
        await Send(method, $"/cards/{id}/{action}", input);
    }

    void Succeeded(string what) => Equal(200, status, $"{what}: {body}");

    [Given("the board {string} allows {int} cards in progress")]
    public async Task Board(string slug, int limit)
    {
        await CreateBoard(slug, limit);
        Equal(201, status, "creating the board: " + body);
    }

    [When("the board {string} is created, allowing {int} cards in progress")]
    public Task CreateBoard(string slug, int limit) => Send(HttpMethod.Post, "/boards", new { slug, name = slug, wipLimit = limit });

    [Then("the boards are {string}")]
    public async Task Boards(string expected)
    {
        await Send(HttpMethod.Get, "/boards", null);
        var boards = JsonNode.Parse(body)!.AsArray().Select(b => $"{b!["slug"]}: {b["backlog"]} backlog, {b["doing"]} doing, {b["done"]} done");
        Equal(expected, string.Join("; ", boards), "the boards");
    }

    [When("I read the board {string}")]
    public Task ReadBoard(string slug) => Send(HttpMethod.Get, $"/boards/{slug}", null);

    [When("the card {string} is added to {string}")]
    public Task AddCard(string title, string slug) => Send(HttpMethod.Post, $"/boards/{slug}/cards", new { title });

    [Given(@"^the cards (.*) on ""(.*)""$")]
    public async Task Cards(string titles, string slug)
    {
        foreach (var title in Quoted(titles))
        {
            await AddCard(title, slug);
            Equal(201, status, $"adding \"{title}\": {body}");
        }
    }

    [Given(@"^(.*) (?:is|are) in progress$")]
    public async Task InProgress(string titles)
    {
        foreach (var title in Quoted(titles))
        {
            await Change(HttpMethod.Post, title, "start");
            Succeeded($"starting \"{title}\"");
        }
    }

    [When("the person signed in starts {string}")]
    public Task Start(string title) => Change(HttpMethod.Post, title, "start");

    [Then("the answer is {int} with the code {string}")]
    public void Code(int expected, string code)
    {
        Status(expected);
        Equal(code, (string?)JsonNode.Parse(body)!["code"], "the code in " + body);
    }

    [Then("the answer is {int} for the field {string}")]
    public void ProblemOn(int expected, string field)
    {
        Status(expected);
        var problems = JsonNode.Parse(body)!["problems"]!.AsArray();
        if (!problems.Any(p => (string)p!["field"]! == field)) throw new Exception($"no problem on \"{field}\" in {body}");
    }

    [When("{string} ships")]
    public Task Ship(string title) => Change(HttpMethod.Post, title, "ship");

    [StepDefinition(@"^(.*) (?:is|are) dropped because ""(.*)""$")]
    public async Task Drop(string titles, string reason)
    {
        foreach (var title in Quoted(titles))
            await Change(HttpMethod.Post, title, "drop", new() { ["reason"] = reason });
    }

    [When("{string} is dropped with the body {string}")]
    public async Task DropRaw(string title, string json)
    {
        var (id, _) = await Card(title);
        await Started();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var answer = await http.PostAsync($"/cards/{id}/drop", content);
        status = (int)answer.StatusCode;
        body = await answer.Content.ReadAsStringAsync();
    }

    [When("{string} is reopened")]
    public Task Reopen(string title) => Change(HttpMethod.Post, title, "reopen");

    [When("{string} is sent back")]
    public Task SendBack(string title) => Change(HttpMethod.Post, title, "send-back");

    [StepDefinition("{string} is blocked because {string}")]
    public async Task Block(string title, string reason)
    {
        await Change(HttpMethod.Post, title, "block", new() { ["reason"] = reason });
        Succeeded($"blocking \"{title}\"");
    }

    [When("{string} is renamed {string}")]
    public Task Rename(string title, string to) => Change(HttpMethod.Put, title, "title", new() { ["title"] = to });

    // -- A card's JSON: the seam's view of the domain's Card -------------------

    JsonObject AnsweredCard => JsonNode.Parse(body)!.AsObject();

    [Then("the answer's card has the fields {string}")]
    public void CardFields(string fields) =>
        Equal(fields, string.Join(", ", AnsweredCard.Select(p => p.Key)), "the card's fields in " + body);

    [Then("the answer's card is not blocked, with no checklist")]
    public void NotBlocked() =>
        Equal("null []", $"{AnsweredCard["blocked"]?.ToJsonString() ?? "null"} {AnsweredCard["checklist"]!.ToJsonString()}", "the card's blocker and checklist");

    [Then("the answer's card's stage, without its time, is {}")]
    public void StageWithoutTime(string expected)
    {
        var stage = AnsweredCard["stage"]!.AsObject().DeepClone().AsObject();
        DateTimeOffset.Parse((string)stage["at"]!);
        stage.Remove("at");
        Equal(expected, stage.ToJsonString(), "the card's stage");
    }

    [Then("the answer's card is blocked because {string}, with the checklist {string}")]
    public void BlockedWith(string reason, string checklist)
    {
        var card = AnsweredCard;
        DateTimeOffset.Parse((string)card["blocked"]!["since"]!);
        var items = card["checklist"]!.AsArray().Select(i => $"{i!["text"]}: {((bool)i["done"]! ? "done" : "to do")}");
        Equal((reason, checklist), ((string)card["blocked"]!["reason"]!, string.Join(", ", items)), "the card's blocker and checklist");
    }

    [When("{string} is unblocked")]
    public Task Unblock(string title) => Change(HttpMethod.Post, title, "unblock");

    [When("{string} is renamed {string} from its first version, after it has changed")]
    public async Task RenameStale(string title, string to)
    {
        var (id, first) = await Card(title);
        await Change(HttpMethod.Post, title, "items", new() { ["text"] = "a change" });
        Succeeded("changing the card");
        await Send(HttpMethod.Put, $"/cards/{id}/title", new { version = first, title = to });
    }

    [Then("the card {string} keeps its title")]
    public async Task KeepsTitle(string title) => await Card(title);

    [When("the person signed in comments {string} on {string}")]
    public async Task Comment(string text, string title)
    {
        var (id, _) = await Card(title);
        await Send(HttpMethod.Post, $"/cards/{id}/comments", new { body = text });
    }

    [Then("{string} has the comment {string} by {string}")]
    public async Task HasComment(string title, string text, string author)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand(
            "select string_agg(m.author || ': ' || m.body, '; ') from comments m join cards c on c.id = m.card where c.title = $1", connection)
        { Parameters = { new() { Value = title } } };
        Equal($"{author}: {text}", (string?)await read.ExecuteScalarAsync(), $"\"{title}\"'s comments");
    }

    [When("{string} is labelled {string}")]
    public Task Label(string title, string labels) =>
        Change(HttpMethod.Put, title, "labels", new() { ["labels"] = labels.Split(", ") });

    [Given(@"^""(.*)"" has the items (.*)$")]
    public async Task Items(string title, string items)
    {
        foreach (var text in Quoted(items))
        {
            await Change(HttpMethod.Post, title, "items", new() { ["text"] = text });
            Succeeded($"adding the item \"{text}\"");
        }
    }

    [Given("the item {string} of {string} is ticked")]
    public async Task Tick(string text, string title)
    {
        await Change(HttpMethod.Post, title, "items/tick", new() { ["text"] = text });
        Succeeded($"ticking \"{text}\"");
    }

    async Task<(string Stage, string? Resolution)> Finished(string title)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("select stage, stage_resolution from cards where title = $1", connection) { Parameters = { new() { Value = title } } };
        await using var row = await read.ExecuteReaderAsync();
        await row.ReadAsync();
        return (row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1));
    }

    [Then("{string} is shipped")]
    public async Task Shipped(string title) => Equal(("done", (string?)"shipped"), await Finished(title), $"\"{title}\"'s stage");

    [When("every card on {string} is sent back")]
    public Task SendAllBack(string slug) => Send(HttpMethod.Post, $"/boards/{slug}/send-all-back", null);

    [Then("the answer is {int} with {int} cards sent back")]
    public void SentBack(int expected, int cards)
    {
        Status(expected);
        Equal(cards, (int)JsonNode.Parse(body)!["cards"]!, "the cards sent back");
    }

    [When("I read my work")]
    public Task MyWork() => Send(HttpMethod.Get, "/me/work", null);

    [Then("my work is {string}")]
    public void Work(string expected)
    {
        Status(200);
        var work = JsonNode.Parse(body)!.AsArray().Select(w => $"{w!["board"]}: {w["title"]}");
        Equal(expected, string.Join("; ", work), "my work");
    }

    [When("I read page {int} of {string}'s finished cards, {int} to a page")]
    public Task Done(int page, string slug, int size) => Send(HttpMethod.Get, $"/boards/{slug}/done?page={page}&size={size}", null);

    [Then("the page has {int} card(s) of {int}, on {int} pages")]
    public void Page(int cards, int total, int pages)
    {
        Status(200);
        var answer = JsonNode.Parse(body)!;
        Equal((cards, total, pages), (answer["cards"]!.AsArray().Count, (int)answer["total"]!, (int)answer["pages"]!), "the page " + body);
    }
}
