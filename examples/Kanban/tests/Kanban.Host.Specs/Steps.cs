using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kanban.Hosting;
using Npgsql;
using Reqnroll;

namespace Kanban.Host.Specs;

[Binding]
public sealed partial class Steps : IAsyncDisposable
{
    HostProcess? host;
    HttpClient http = null!;
    string connectionString = "";
    string body = "";
    int status;
    int mark;
    /// The host's environment, which a step may add to before it starts.
    readonly Dictionary<string, string> environment = new() { ["KANBAN_SQL"] = "1" };

    /// Reqnroll does not dispose a binding asynchronously: the host is stopped
    /// here, when the scenario ends, and then its database is dropped.
    [AfterScenario]
    public async Task StopHost()
    {
        if (locker is not null) await locker.DisposeAsync();
        locker = null;
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

    [Given("the host serves a migrated database")]
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
        host = await HostProcess.Start(typeof(Program).Assembly.Location, "KANBAN_HOST", connectionString, "/graphql?sdl", environment);
        // Keeps the cookie the host signs a person in with, as a browser does.
        http = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() }) { BaseAddress = host.Address };
    }

}
