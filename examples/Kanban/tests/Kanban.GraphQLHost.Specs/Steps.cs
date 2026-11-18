using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kanban.Hosting;
using Npgsql;
using Reqnroll;

namespace Kanban.GraphQLHost.Specs;

[Binding]
public sealed class Steps : IAsyncDisposable
{
    HostProcess? host;
    HttpClient http = null!;
    string connectionString = "";
    string body = "";
    int mark;

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }

    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected\n  {expected}\nbut was\n  {actual}");
    }

    /// The SQL the host logged reading cards since the last request began.
    List<string> CardsSql()
    {
        Thread.Sleep(200);
        return host!.Lines.Skip(mark).Where(l => l.StartsWith("SQL: ") && l.Contains("FROM \"cards\"")).Select(l => l.Substring(5).Trim()).ToList();
    }

    [Given("the GraphQL host serves a migrated database")]
    public async Task HostServes()
    {
        connectionString = await Databases.Migrated();
        // The rows come before the host: the scenarios add them in the next step.
    }

    [Given("board {string} has the cards:")]
    public async Task Cards(string slug, DataTable table)
    {
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var board = Guid.NewGuid();
            await using (var b = new NpgsqlCommand("insert into boards (id, slug, name, wip_limit) values ($1, $2, $2, 3)", connection)
                         { Parameters = { new() { Value = board }, new() { Value = slug } } })
                await b.ExecuteNonQueryAsync();
            foreach (var row in table.Rows)
            {
                var doing = row["stage"] == "doing";
                await using var c = new NpgsqlCommand(
                    "insert into cards (id, board, title, stage, stage_assignee, stage_since, checklist, created, version) values ($1, $2, $3, $4, $5, $6, '[]', $7, $8)", connection)
                {
                    Parameters =
                    {
                        new() { Value = Guid.NewGuid() }, new() { Value = board }, new() { Value = row["title"] }, new() { Value = row["stage"] },
                        new() { Value = doing ? row["assignee"] : DBNull.Value },
                        new() { Value = doing ? DateTimeOffset.Parse(row["created"]) : DBNull.Value },
                        new() { Value = DateTimeOffset.Parse(row["created"]) }, new() { Value = int.Parse(row["version"]) },
                    },
                };
                await c.ExecuteNonQueryAsync();
            }
        }
        host = await HostProcess.Start(typeof(Program).Assembly.Location, "KANBAN_GRAPHQL_HOST", connectionString, "/graphql?sdl",
            new Dictionary<string, string> { ["KANBAN_SQL"] = "1" });
        http = new HttpClient { BaseAddress = host.Address };
    }

    [When("I ask for {string}")]
    public async Task Ask(string query)
    {
        mark = host!.Lines.Length - 1;
        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var answer = await http.PostAsync("/graphql", content);
        body = await answer.Content.ReadAsStringAsync();
    }

    List<string> Titles() =>
        JsonNode.Parse(body)!["data"]?["cards"]?.AsArray().Select(c => (string)c!["title"]!).ToList()
        ?? throw new Exception("no cards in the answer: " + body);

    [Then("the cards are {string}")]
    public void CardsAre(string expected) => Equal(expected, string.Join(", ", Titles()), "the cards");

    [Then("there are {int} cards")]
    public void Count(int n) => Equal(n, Titles().Count, "how many cards");

    [Then("the cards' SQL has a WHERE and ends with {string}")]
    public void SqlWhereOrder(string ending)
    {
        var sql = CardsSql().Single();
        if (!sql.Contains(" WHERE ")) throw new Exception("no WHERE in " + sql);
        if (!sql.EndsWith(ending)) throw new Exception($"the SQL does not end with {ending}: {sql}");
    }

    [Then("the cards' SQL has no WHERE and no ORDER BY")]
    public void SqlPlain()
    {
        var sql = CardsSql().Single();
        if (sql.Contains(" WHERE ") || sql.Contains("ORDER BY")) throw new Exception("the SQL filters or orders: " + sql);
    }

    [Then("the answer's JSON is {}")]
    public void Json(string expected) => Equal(expected, body, "the answer");

    [Then("the answer is the error {string}")]
    public void Error(string message)
    {
        var errors = JsonNode.Parse(body)!["errors"]?.AsArray() ?? throw new Exception("no error in " + body);
        Equal(message, (string)errors[0]!["message"]!, "the error");
    }

    [Then("no cards' SQL was sent")]
    public void NoSql() => Equal(0, CardsSql().Count, "cards' SQL sent");
}
