using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Reqnroll;

namespace Kanban.Host.Specs;

/// features/host/authentication.feature
public sealed partial class Steps
{
    async Task Send(HttpMethod method, string path, object? input)
    {
        await Started();
        using var request = new HttpRequestMessage(method, path) { Content = input is null ? null : JsonContent.Create(input) };
        using var answer = await http.SendAsync(request);
        status = (int)answer.StatusCode;
        body = await answer.Content.ReadAsStringAsync();
    }

    /// A card's id and version, by its title.
    async Task<(Guid Id, int Version)> Card(string title)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("select id, version from cards where title = $1", connection) { Parameters = { new() { Value = title } } };
        await using var row = await read.ExecuteReaderAsync();
        if (!await row.ReadAsync()) throw new Exception($"no card \"{title}\"");
        return (row.GetGuid(0), row.GetInt32(1));
    }

    [When("someone registers as {string} with the password {string}")]
    public Task Register(string name, string password) => Send(HttpMethod.Post, "/account/register", new { name, password });

    [Given("the account {string} with the password {string}")]
    public async Task Account(string name, string password)
    {
        await Register(name, password);
        Equal(200, status, "registering: " + body);
        await Send(HttpMethod.Post, "/account/signout", null);
    }

    [When("{string} signs in with the password {string}")]
    public Task SignIn(string name, string password) => Send(HttpMethod.Post, "/account/signin", new { name, password });

    [Given("{string} is signed in with the password {string}")]
    public async Task SignedIn(string name, string password)
    {
        await Register(name, password);
        Equal(200, status, "registering: " + body);
    }

    [StepDefinition("the person signs out")]
    public Task SignOut() => Send(HttpMethod.Post, "/account/signout", null);

    [Then("the answer is {int}")]
    public void Status(int expected) => Equal(expected, status, "the status of the answer " + body);

    [Then("the answer is {int} for the field {string}: {string}")]
    public void Problem(int expected, string field, string message)
    {
        Status(expected);
        var problems = JsonNode.Parse(body)!["problems"]!.AsArray();
        if (!problems.Any(p => (string)p!["field"]! == field && (string)p!["message"]! == message))
            throw new Exception($"no problem \"{field}: {message}\" in {body}");
    }

    async Task<string?> WhoIsSignedIn()
    {
        var (savedStatus, savedBody) = (status, body);
        await Send(HttpMethod.Get, "/account/me", null);
        var who = status == 200 ? (string?)JsonNode.Parse(body)!["name"] : null;
        if (status != 200 && status != 401) throw new Exception($"asking who is signed in answered {status}: {body}");
        (status, body) = (savedStatus, savedBody);
        return who;
    }

    [Then("{string} is signed in")]
    public async Task IsSignedIn(string name) => Equal(name, await WhoIsSignedIn(), "who is signed in");

    [Then("nobody is signed in")]
    public async Task Nobody() => Equal(null, await WhoIsSignedIn(), "who is signed in");

    [Then("no stored account contains the text {string}")]
    public async Task NotStored(string text)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("select count(*) from accounts where accounts::text like '%' || $1 || '%'", connection) { Parameters = { new() { Value = text } } };
        Equal(0L, (long)(await count.ExecuteScalarAsync())!, "accounts containing the text");
        await using var all = new NpgsqlCommand("select count(*) from accounts", connection);
        Equal(1L, (long)(await all.ExecuteScalarAsync())!, "accounts stored");
    }

    [When("nobody signed in starts {string}")]
    public async Task StartSignedOut(string title)
    {
        var (id, version) = await Card(title);
        await Send(HttpMethod.Post, $"/cards/{id}/start", new { version });
    }

    [When("the person signed in starts {string}, the request also naming {string}")]
    public async Task StartNaming(string title, string other)
    {
        var (id, version) = await Card(title);
        await Send(HttpMethod.Post, $"/cards/{id}/start", new { version, assignee = other });
    }

    async Task<(string Stage, string? Assignee)> Stage(string title)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("select stage, stage_assignee from cards where title = $1", connection) { Parameters = { new() { Value = title } } };
        await using var row = await read.ExecuteReaderAsync();
        await row.ReadAsync();
        return (row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1));
    }

    [Then("{string} is in the backlog")]
    public async Task InBacklog(string title) => Equal(("backlog", (string?)null), await Stage(title), $"\"{title}\"'s stage");

    [Then("{string} is in progress for {string}")]
    public async Task InProgress(string title, string person) => Equal(("doing", (string?)person), await Stage(title), $"\"{title}\"'s stage");
}
