using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kanban;
using Kanban.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Quotations;
using Plasm.HotChocolate;
using Reqnroll;

namespace Kanban.Host.Specs;

/// features/host/graphql.feature
public sealed partial class Steps
{
    /// In a request, `<Title>` is the card's id and `<Title:version>` its
    /// version as stored.
    [When("I ask for {string}")]
    public async Task Ask(string query)
    {
        var (s, b) = await Send(query);
        status = s;
        body = b;
    }

    /// The request sent, its answer awaited by the caller.
    async Task<(int Status, string Body)> Send(string query)
    {
        await Started();
        foreach (var m in Regex.Matches(query, "<([^<>:]+)(:version)?>").Reverse())
        {
            var (id, version) = await Card(m.Groups[1].Value);
            query = query[..m.Index] + (m.Groups[2].Success ? version.ToString() : id.ToString()) + query[(m.Index + m.Length)..];
        }
        mark = host!.Lines.Length - 1;
        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var answer = await http.PostAsync("/graphql", content);
        return ((int)answer.StatusCode, await answer.Content.ReadAsStringAsync());
    }

    Npgsql.NpgsqlConnection? locker;
    Npgsql.NpgsqlTransaction? lockTx;
    Task<(int Status, string Body)>? sent;

    /// Taken once the host has started (its start-up job writes cards).
    [Given("the cards table is locked by another transaction")]
    public async Task LockCards()
    {
        await Started();
        locker = new Npgsql.NpgsqlConnection(connectionString);
        await locker.OpenAsync();
        lockTx = await locker.BeginTransactionAsync();
        await using var command = new Npgsql.NpgsqlCommand("LOCK TABLE cards IN ACCESS EXCLUSIVE MODE", locker, lockTx);
        await command.ExecuteNonQueryAsync();
    }

    [When("I send {string} while the cards are locked")]
    public void SendLocked(string query) => sent = Send(query);

    /// What waits for the lock in the scenario's database, from
    /// pg_stat_activity: the page once it is there, and then the count beside
    /// it, which is either sent with the page or not until the lock goes.
    [Then("the page and the count are both waiting for the lock")]
    public async Task BothWaiting()
    {
        var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
        // Not the locking connection: pg_stat_activity is read once per
        // transaction, so polling inside the lock's would see one moment.
        await using var watcher = new Npgsql.NpgsqlConnection(connectionString);
        await watcher.OpenAsync();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var waiting = new List<string>();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            waiting.Clear();
            await using (var command = new Npgsql.NpgsqlCommand(
                "select query from pg_stat_activity where datname = $1 and wait_event_type = 'Lock' and query like 'SELECT%'", watcher)
                { Parameters = { new() { Value = database! } } })
            await using (var rows = await command.ExecuteReaderAsync())
                while (await rows.ReadAsync()) waiting.Add(rows.GetString(0));

            var page = waiting.Any(q => q.Contains("ROW_NUMBER()"));
            var count = waiting.Any(q => q.Contains("COUNT(*)"));
            if (page && count) return;
            // The page is waiting and the count is not: give the count a
            // moment to arrive, then say it did not.
            if (page && waited.Elapsed > TimeSpan.FromSeconds(3)) break;
            await Task.Delay(50);
        }
        var activity = new List<string>();
        await using (var command = new Npgsql.NpgsqlCommand(
            "select coalesce(datname, '-') || ' ' || pid || ' ' || coalesce(state, '') || ' ' || coalesce(wait_event_type, '') || '/' || coalesce(wait_event, '') || ' ' || left(regexp_replace(query, '\\s+', ' ', 'g'), 120) from pg_stat_activity where backend_type = 'client backend' and pid <> pg_backend_pid() and datname = $1", watcher)
            { Parameters = { new() { Value = database! } } })
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) activity.Add(rows.GetString(0));
        throw new Exception("the page and the count are not both waiting; waiting for the lock:\n  " + string.Join("\n  ", waiting)
            + "\nthe database's other sessions:\n  " + string.Join("\n  ", activity)
            + "\nthe host's output since the request:\n  " + string.Join("\n  ", host!.Lines.Skip(mark)));
    }

    [When("the lock is released and the answer comes")]
    public async Task Release()
    {
        await lockTx!.CommitAsync();
        await locker!.DisposeAsync();
        locker = null;
        (status, body) = await sent!;
    }

    List<string> Titles() =>
        JsonNode.Parse(body)!["data"]?["cards"]?["items"]?.AsArray().Select(c => (string)c!["title"]!).ToList()
        ?? throw new Exception("no cards in the answer: " + body);

    [Then("the cards are {string}")]
    public void CardsAre(string expected) => Equal(expected, string.Join(", ", Titles()), "the cards in " + body);

    void Orders(string sql, string ordering)
    {
        if (!sql.EndsWith(ordering + " LIMIT @p0") && !sql.Contains(ordering + " LIMIT @p"))
            throw new Exception($"the SQL does not order by {ordering} and then read a page: {sql}");
    }

    List<string> Reads() => CardsSql().Where(l => !l.StartsWith("SELECT COUNT(")).ToList();

    /// The one command of the cards' SQL that `which` picks; otherwise a
    /// failure that names every command the host logged for the request.
    string OneOf(List<string> sql, Func<string, bool> which, string what)
    {
        var picked = sql.Where(which).ToList();
        if (picked.Count != 1)
            throw new Exception($"expected one {what}, but {picked.Count} of the request's commands are:\n  " + string.Join("\n  ", sql));
        return picked[0];
    }

    [Then("the cards' SQL has a WHERE and orders by {string}, then reads a page")]
    public void SqlWhereOrder(string ordering)
    {
        var sql = OneOf(Reads(), _ => true, "command reading cards");
        if (!sql.Contains(" WHERE ")) throw new Exception("no WHERE in " + sql);
        Orders(sql, ordering);
    }

    [Then("the cards' SQL searches the title with LIKE")]
    public void Like()
    {
        var sql = OneOf(Reads(), _ => true, "command reading cards");
        if (!sql.Contains("\"c\".\"title\" LIKE @p")) throw new Exception("the title is not searched with LIKE: " + sql);
    }

    [Then("the cards' SQL has no WHERE and orders by {string}, then reads a page")]
    public void SqlPlain(string ordering)
    {
        var sql = OneOf(Reads(), _ => true, "command reading cards");
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
        var count = OneOf(CardsSql(), l => l.StartsWith("SELECT COUNT("), "count");
        if (!count.Contains(" WHERE ")) throw new Exception("the count does not filter: " + count);
    }

    [Then("the database counted nothing")]
    public void NotCounted()
    {
        var sql = CardsSql();
        Equal(0, sql.Count(l => l.Contains("COUNT(")), "counts sent, of the request's commands:\n  " + string.Join("\n  ", sql));
    }

    IEnumerable<JsonNode> Boards() => JsonNode.Parse(body)!["data"]?["boards"]?.AsArray().Select(b => b!) ?? throw new Exception("no boards in " + body);

    /// A board's page of cards: its items' titles.
    static string Titles(JsonNode? cards) => string.Join(", ", cards!["items"]!.AsArray().Select(c => (string)c!["title"]!));

    [Then("each board's cards are {string}")]
    public void BoardsCards(string expected) =>
        Equal(expected, string.Join("; ", Boards().Select(b => $"{b["slug"]}: {Titles(b["cards"])}")), "each board's cards");

    [Then("the board {string} has backlog {string} and doing {string}")]
    public void BacklogDoing(string slug, string backlog, string doing)
    {
        var board = Boards().Single(b => (string)b["slug"]! == slug);
        Equal((backlog, doing), (Titles(board["backlog"]), Titles(board["doing"])), $"{slug}'s cards");
    }

    [Then("each board has a next page")]
    public void EveryNext()
    {
        foreach (var b in Boards())
            Equal(true, (bool)b["cards"]!["pageInfo"]!["hasNextPage"]!, $"{b["slug"]} has a next page");
    }

    [Then("the page has a next page and a previous page, for the board {string}")]
    public void BoardBothSides(string slug)
    {
        var page = Boards().Single(b => (string)b["slug"]! == slug)["cards"]!["pageInfo"]!;
        Equal((true, true), ((bool)page["hasNextPage"]!, (bool)page["hasPreviousPage"]!), "next and previous");
    }

    [Then("the cards were read in {int} command(s), for every board at once, numbered per board")]
    public void ReadOncePerBoard(int n)
    {
        ReadOnce(n);
        foreach (var sql in Reads())
            if (!sql.Contains("ROW_NUMBER() OVER (PARTITION BY \"c\".\"board\"")) throw new Exception("not numbered per board: " + sql);
    }

    [Then("each board's total is {string}")]
    public void BoardTotals(string expected) =>
        Equal(expected, string.Join("; ", Boards().Select(b => $"{b["slug"]}: {b["cards"]!["totalCount"]}")), "each board's total");

    [Then("the database counted every board's cards in one command, under a WHERE")]
    public void CountedPerBoard()
    {
        var count = OneOf(CardsSql(), l => l.StartsWith("SELECT ") && l.Contains("COUNT(*)"), "count of every board's cards");
        if (!count.Contains(" WHERE ") || !count.Contains("GROUP BY")) throw new Exception("the count is not per board under a where: " + count);
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

    /// What the host logged since the request began, every command.
    List<string> AllSql()
    {
        Thread.Sleep(200);
        return host!.Lines.Skip(mark).Where(l => l.StartsWith("SQL: ")).Select(l => l.Substring(5).Trim()).ToList();
    }

    [Then("the cards were read in one command, reading only {string}")]
    public void ReadOnly(string columns)
    {
        var sql = OneOf(Reads(), _ => true, "command reading cards");
        // The select over the cards table itself: a page per board reads it
        // through a derived table.
        var from = sql.IndexOf(" FROM \"cards\"", StringComparison.Ordinal);
        var selected = sql[sql.LastIndexOf("SELECT ", from, StringComparison.Ordinal)..from];
        // The page's numbering orders by columns it does not read.
        var numbering = selected.IndexOf(", ROW_NUMBER()", StringComparison.Ordinal);
        if (numbering >= 0) selected = selected[..numbering];
        var read = Regex.Matches(selected, "\"c\"\\.\"([a-z_]+)\"").Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal);
        Equal(columns, string.Join(", ", read), "the columns read: " + sql);
    }

    [Then("no comments or labels were read")]
    public void NoLists() =>
        Equal(0, AllSql().Count(l => l.Contains("FROM \"comments\"") || l.Contains("FROM \"labels\"")), "comments' and labels' commands: " + string.Join(" | ", AllSql()));

    [Then("the comments were read in one more command, and no labels")]
    public void CommentsOnly()
    {
        var sql = AllSql();
        Equal((1, 0), (sql.Count(l => l.Contains("FROM \"comments\"")), sql.Count(l => l.Contains("FROM \"labels\""))), "comments' and labels' commands: " + string.Join(" | ", sql));
    }

    string refused = "";

    void BuildSchema(Plasm.ReadField<Card> field, Type? views = null)
    {
        try
        {
            var server = new ServiceCollection().AddGraphQLServer();
            if (views is null) server.AddPlasmReadType<Card, CardView>(new[] { field });
            else server.AddPlasmReadType<Card, CardView>(new[] { field }, views);
        }
        catch (InvalidOperationException e)
        {
            refused = e.Message;
        }
    }

    /// `fun (c: Card) -> c.<property>`, as F# would quote it: C# writes no quotation.
    static FSharpExpr<FSharpFunc<Card, T>> Read<T>(string property)
    {
        var c = new FSharpVar("c", typeof(Card), null);
        return FSharpExpr.Cast<FSharpFunc<Card, T>>(FSharpExpr.Lambda(c, FSharpExpr.PropertyGet(FSharpExpr.Var(c), typeof(Card).GetProperty(property)!, null)));
    }

    [When("a schema is built with the card field {string} read from the title")]
    public void TitleMisnamed(string name) =>
        BuildSchema(Plasm.ReadField.via(name, Read<Title>("Title"), FuncConvert.FromFunc<Title, string>(t => t.Value)));

    [When("a schema is built over SeamMap with the card field {string} read as the domain's title")]
    public void TitleAsDomain(string name) =>
        BuildSchema(Plasm.ReadField.value(name, Read<Title>("Title")), typeof(SeamMap));

    [When("a schema is built with the card field {string} read as text")]
    public void VersionAsText(string name) =>
        BuildSchema(Plasm.ReadField.via(name, Read<int>("Version"), FuncConvert.FromFunc<int, string>(v => v.ToString())));

    [Then("building the schema is refused with {string}")]
    public void SchemaRefused(string message) => Equal(message, refused, "the refusal");

    [Then("no cards' SQL was sent")]
    public void NoSql()
    {
        var sql = CardsSql();
        Equal(0, sql.Count, "cards' SQL sent:\n  " + string.Join("\n  ", sql));
    }
}
