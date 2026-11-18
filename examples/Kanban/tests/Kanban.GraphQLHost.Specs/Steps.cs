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
    }

    /// The host starts at the first request, after every row a scenario adds.
    async Task Started()
    {
        if (host is not null) return;
        host = await HostProcess.Start(typeof(Program).Assembly.Location, "KANBAN_GRAPHQL_HOST", connectionString, "/graphql?sdl",
            new Dictionary<string, string> { ["KANBAN_SQL"] = "1" });
        http = new HttpClient { BaseAddress = host.Address };
    }

    [When("I ask for {string}")]
    public async Task Ask(string query)
    {
        await Started();
        mark = host!.Lines.Length - 1;
        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var answer = await http.PostAsync("/graphql", content);
        body = await answer.Content.ReadAsStringAsync();
    }

    List<string> Titles() =>
        JsonNode.Parse(body)!["data"]?["cards"]?["items"]?.AsArray().Select(c => (string)c!["title"]!).ToList()
        ?? throw new Exception("no cards in the answer: " + body);

    [Then("the cards are {string}")]
    public void CardsAre(string expected) => Equal(expected, string.Join(", ", Titles()), "the cards");

    void Orders(string sql, string ordering)
    {
        if (!sql.EndsWith(ordering + " LIMIT @p0") && !sql.Contains(ordering + " LIMIT @p"))
            throw new Exception($"the SQL does not order by {ordering} and then read a page: {sql}");
    }

    List<string> Reads() => CardsSql().Where(l => !l.StartsWith("SELECT COUNT(")).ToList();

    [Then("the cards' SQL has a WHERE and orders by {string}, then reads a page")]
    public void SqlWhereOrder(string ordering)
    {
        var sql = Reads().Single();
        if (!sql.Contains(" WHERE ")) throw new Exception("no WHERE in " + sql);
        Orders(sql, ordering);
    }

    [Then("the cards' SQL has no WHERE and orders by {string}, then reads a page")]
    public void SqlPlain(string ordering)
    {
        var sql = Reads().Single();
        if (sql.Contains(" WHERE ")) throw new Exception("the SQL filters: " + sql);
        Orders(sql, ordering);
    }

    JsonNode? PageInfo() => JsonNode.Parse(body)!["data"]?["cards"]?["pageInfo"];

    [Then("the page has a next page and a previous page")]
    public void BothSides() => Equal((true, true), ((bool)PageInfo()!["hasNextPage"]!, (bool)PageInfo()!["hasPreviousPage"]!), "next and previous");

    [Then("the page has no next page and a previous page")]
    public void LastPage() => Equal((false, true), ((bool)PageInfo()!["hasNextPage"]!, (bool)PageInfo()!["hasPreviousPage"]!), "next and previous");

    [Then("the total is {int}")]
    public void Total(int n) => Equal(n, (int)JsonNode.Parse(body)!["data"]!["cards"]!["totalCount"]!, "the total");

    [Then("the database counted the cards under a WHERE")]
    public void Counted()
    {
        var count = CardsSql().Single(l => l.StartsWith("SELECT COUNT("));
        if (!count.Contains(" WHERE ")) throw new Exception("the count does not filter: " + count);
    }

    [Then("the database counted nothing")]
    public void NotCounted() => Equal(0, CardsSql().Count(l => l.StartsWith("SELECT COUNT(")), "counts sent");

    IEnumerable<JsonNode> Boards() => JsonNode.Parse(body)!["data"]?["boards"]?.AsArray().Select(b => b!) ?? throw new Exception("no boards in " + body);

    static string Titles(JsonNode? cards) => string.Join(", ", cards!.AsArray().Select(c => (string)c!["title"]!));

    [Then("each board's cards are {string}")]
    public void BoardsCards(string expected) =>
        Equal(expected, string.Join("; ", Boards().Select(b => $"{b["slug"]}: {Titles(b["cards"])}")), "each board's cards");

    [Then("the board {string} has backlog {string} and doing {string}")]
    public void BacklogDoing(string slug, string backlog, string doing)
    {
        var board = Boards().Single(b => (string)b["slug"]! == slug);
        Equal((backlog, doing), (Titles(board["backlog"]), Titles(board["doing"])), $"{slug}'s cards");
    }

    [Then("the cards were read in {int} command(s), for every board at once")]
    public void ReadOnce(int n)
    {
        var reads = Reads();
        Equal(n, reads.Count, "commands reading cards: " + string.Join(" | ", reads));
        foreach (var sql in reads)
            if (!sql.Contains("\"board\" = ANY(")) throw new Exception("not every board at once: " + sql);
    }

    [Then("the answer is an error")]
    public void AnError()
    {
        if (JsonNode.Parse(body)!["errors"] is null) throw new Exception("no error in " + body);
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
