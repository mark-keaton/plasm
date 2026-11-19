using System.Text.RegularExpressions;
using Npgsql;
using Reqnroll;

namespace Kanban.Host.Specs;

/// features/host/background.feature
public sealed partial class Steps
{
    /// The first line of the host's output the job has not yet been seen to
    /// write past, and the cards it sent back in the runs last waited for.
    int jobSeen;
    int sentBack;

    async Task Sql(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }

    [Given("board {string} has cards in progress:")]
    public async Task InProgressFor(string slug, DataTable table)
    {
        var board = Guid.NewGuid();
        await Sql("insert into boards (id, slug, name, wip_limit) values ($1, $2, $2, 5)", board, slug);
        foreach (var row in table.Rows)
        {
            var started = DateTimeOffset.UtcNow.AddDays(-int.Parse(Regex.Match(row["started"], @"^(\d+) days? ago$").Groups[1].Value));
            await Sql("insert into cards (id, board, title, stage, stage_assignee, stage_since, checklist, created, version) values ($1, $2, $3, 'doing', $4, $5, '[]', $5, 1)",
                Guid.NewGuid(), board, row["title"], row["assignee"], started);
        }
        // The job runs every second here, against the hour the host runs it at by default.
        environment["Kanban__StaleCardsEvery"] = "00:00:01";
    }

    /// Waits for `runs` more runs of the job to be written, and adds up the
    /// cards they sent back.
    async Task JobRuns(int runs)
    {
        await Started();
        sentBack = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (runs > 0)
        {
            var lines = host!.Lines;
            for (; jobSeen < lines.Length && runs > 0; jobSeen++)
            {
                var run = Regex.Match(lines[jobSeen], @"Stale cards sent back: (\d+)");
                if (!run.Success) continue;
                sentBack += int.Parse(run.Groups[1].Value);
                runs--;
            }
            if (runs == 0) break;
            if (DateTime.UtcNow > deadline) throw new Exception("the job did not run:\n" + string.Join("\n", host.Lines));
            await Task.Delay(100);
        }
    }

    [When("the host has sent stale cards back")]
    public Task JobRan() => JobRuns(1);

    /// Two runs, so that one began after the step before.
    [When("the host has sent stale cards back again")]
    public Task JobRanAgain() => JobRuns(2);

    [Then(@"^the host sent (\d+) cards? back$")]
    public void SentBack(int cards) => Equal(cards, sentBack, "the cards the job sent back");

    [Then("{string} is at version {int}")]
    public async Task AtVersion(string title, int version) => Equal(version, (await Card(title)).Version, $"\"{title}\"'s version");

    [When("{string} is renamed {string} from version {int}")]
    public async Task RenameFrom(string title, string to, int version)
    {
        var (id, _) = await Card(title);
        await Send(HttpMethod.Put, $"/cards/{id}/title", new { version, title = to });
    }

    [When("{string} has been in progress for {int} days")]
    public async Task InProgressDays(string title, int days)
    {
        await Sql("update cards set stage_since = $1 where title = $2", DateTimeOffset.UtcNow.AddDays(-days), title);
        jobSeen = host!.Lines.Length;
    }
}
