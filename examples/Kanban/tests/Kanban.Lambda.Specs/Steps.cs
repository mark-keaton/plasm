using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Reqnroll;

using Kanban.Hosting;

namespace Kanban.Lambda.Specs;

/// What publish.sh made, once a test run, and what it printed.
sealed record Published(string Work, string Log)
{
    static readonly Lazy<Task<Published>> Once = new(async () =>
    {
        // Named with this machine and process, as the containers are, so that
        // the next run removes the folder of a run that was killed.
        foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "kanban-lambda.*"))
            if (Regex.Match(Path.GetFileName(old), @"^kanban-lambda\.([0-9a-f]{8})_(\d+)\.") is { Success: true } m
                && m.Groups[1].Value == Docker.Machine && !Docker.Running(int.Parse(m.Groups[2].Value)))
                Directory.Delete(old, true);
        var work = Directory.CreateTempSubdirectory($"kanban-lambda.{Docker.Owner}.").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
        // Beside this project's source, which the build output is under.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "publish.sh"))) dir = dir.Parent;
        var script = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("publish.sh is not above " + AppContext.BaseDirectory), "publish.sh");
        var (code, output) = await Docker.Run("bash", script, work);
        if (code != 0) throw new InvalidOperationException($"publish.sh exited with {code}:\n{output}");
        return new Published(work, output);
    });

    public static Task<Published> Get() => Once.Value;

    public string Out => Path.Combine(Work, "out");
    public string Rie => Path.Combine(Work, "rie");

    public string Line(string pattern) =>
        Regex.Match(Log, pattern, RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value
            : throw new InvalidOperationException($"publish.sh printed nothing matching {pattern}:\n{Log}");
}

/// Docker, run as a program. Every function container is labelled with this
/// machine and this test process's id (Owner): removed when its scenario
/// ends, when the test process exits, and, for one a killed run left, by the
/// next run on the same machine, which finds that process gone.
static class Docker
{
    const string Label = "kanban.lambda.specs";

    public static readonly string Owner = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.ToLowerInvariant())))[..8]
        + "_" + Environment.ProcessId;

    static readonly List<string> Started = new();

    static Docker() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Started)
                foreach (var id in Started)
                    Process.Start(new ProcessStartInfo("docker") { ArgumentList = { "rm", "-f", id }, RedirectStandardOutput = true, RedirectStandardError = true })?.WaitForExit();
        };

    public static async Task<(int Code, string Output)> Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }

    static async Task<string> Must(params string[] arguments)
    {
        var (code, output) = await Run("docker", arguments);
        if (code != 0) throw new InvalidOperationException($"docker {string.Join(" ", arguments)} exited with {code}:\n{output}");
        return output.Trim();
    }

    public static string Machine => Owner[..8];

    public static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    static readonly Lazy<Task> Orphans = new(async () =>
    {
        var machine = Machine;
        foreach (var line in (await Must("ps", "-a", "--filter", "label=" + Label, "--format", "{{.ID}} {{.Label \"" + Label + "\"}}")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', '_');
            if (parts.Length == 3 && parts[1] == machine && int.TryParse(parts[2], out var pid) && !Running(pid))
                await Must("rm", "-f", parts[0]);
        }
    });

    /// The function, in the emulator in Amazon Linux 2023, its port 8080 on a
    /// free port of this machine's loopback.
    public static async Task<(string Id, int Port)> Function(Published published, string connectionString, string checks)
    {
        await Orphans.Value;
        var id = await Must("run", "-d", "--label", $"{Label}={Owner}", "--add-host", "host.docker.internal:host-gateway",
            "-p", "127.0.0.1::8080", "-v", published.Out + ":/var/task:ro", "-v", published.Rie + ":/rie:ro",
            "-e", "KANBAN_CS=" + connectionString, "-e", "KANBAN_STARTUP_CHECKS=" + checks,
            "public.ecr.aws/amazonlinux/amazonlinux:2023", "/rie/aws-lambda-rie", "/var/task/bootstrap");
        lock (Started) Started.Add(id);
        var port = int.Parse((await Must("port", id, "8080/tcp")).Split('\n')[0].Split(':').Last());
        return (id, port);
    }

    public static Task<string> Logs(string id) => Must("logs", id);

    public static async Task Remove(string id)
    {
        await Must("rm", "-f", id);
        lock (Started) Started.Remove(id);
    }
}

[Binding]
public sealed class Steps(IReqnrollOutputHelper output)
{
    Published published = null!;
    string? function;
    HttpClient http = null!;
    string connectionString = "";
    string reached = "";
    string answer = "";
    readonly Dictionary<string, Guid> cards = new();

    [AfterScenario]
    public async Task Stop()
    {
        await StopFunction();
        if (connectionString != "") await Databases.Drop(connectionString);
        connectionString = "";
    }

    async Task StopFunction()
    {
        http?.Dispose();
        if (function is not null)
        {
            // The function's log, for its timings (docs/aot.md).
            output.WriteLine(await Docker.Logs(function));
            await Docker.Remove(function);
        }
        function = null;
    }

    /// The function in a container of its own, KANBAN_STARTUP_CHECKS set to `checks`.
    async Task StartFunction(string checks)
    {
        var (id, port) = await Docker.Function(published, reached, checks);
        function = id;
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try { using var _ = await http.GetAsync("/"); return; }
            catch (HttpRequestException) when (waited.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(100); }
        }
    }


    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected\n  {expected}\nbut was\n  {actual}");
    }

    async Task Invoke(string json)
    {
        var watch = Stopwatch.StartNew();
        using var sent = await http.PostAsync("/2015-03-31/functions/function/invocations", new StringContent(json, Encoding.UTF8, "application/json"));
        answer = await sent.Content.ReadAsStringAsync();
        output.WriteLine($"invoked {json} -> {answer} ({watch.Elapsed.TotalMilliseconds:F0} ms round trip)");
    }

    JsonObject Case(string name)
    {
        if (answer == "") throw new Exception($"expected the answer {{\"{name}\": ...}} but there was none (the function's init failed?)");
        var node = JsonNode.Parse(answer) as JsonObject;
        if (node is null || node.Count != 1 || node[name] is not JsonObject value)
            throw new Exception($"expected the answer {{\"{name}\": ...}} but was\n  {answer}");
        return value;
    }

    // -- Given ----------------------------------------------------------------

    [Given("the Kanban function is published natively for Amazon Linux 2023")]
    public async Task IsPublished() => published = await Published.Get();

    /// The database is reached from the function's container through the
    /// host's gateway, at the port it has here.
    [Given("it runs in the Lambda runtime interface emulator over a migrated database")]
    public async Task Runs()
    {
        connectionString = await Databases.Migrated();
        var there = new NpgsqlConnectionStringBuilder(connectionString);
        if (there.Host is "localhost" or "127.0.0.1" or "::1") there.Host = "host.docker.internal";
        reached = there.ConnectionString;
        await StartFunction("run");
    }

    /// A new container over the same database, the first one stopped.
    [Given("^it runs again, opening KanbanData (with|without) its start-up check$")]
    [When("^it runs again, opening KanbanData (with|without) its start-up check$")]
    public async Task RunsAgain(string with)
    {
        await StopFunction();
        await StartFunction(with == "with" ? "run" : "skip");
    }

    /// A difference only the schema check reads: no call the function has
    /// touches accounts.
    [Given("the table {string} has its column {string} renamed to {string}")]
    public async Task Renamed(string table, string column, string to)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"alter table {table} rename column {column} to {to}", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Given("board {string} named {string} allows {int} card(s) in progress")]
    public async Task BoardAllows(string slug, string name, int limit)
    {
        await Invoke(new JsonObject { ["call"] = "createBoard", ["slug"] = slug, ["name"] = name, ["wipLimit"] = limit }.ToJsonString());
        Equal(slug, (string?)Case("created")["slug"], $"creating board {slug}");
    }

    [Given("board {string} has the cards {string}, {string}")]
    public async Task BoardHasCards(string slug, string first, string second)
    {
        foreach (var title in new[] { first, second })
        {
            await Invoke(new JsonObject { ["call"] = "addCard", ["board"] = slug, ["title"] = title }.ToJsonString());
            cards[title] = (Guid)Case("card")["id"]!;
        }
    }

    [Given("{string} has started {string}")]
    public async Task HasStarted(string person, string title)
    {
        await Starts(person, title, 1);
        Case("card");
    }

    // -- When -----------------------------------------------------------------

    [When("I invoke it with {}")]
    public Task InvokeWith(string json) => Invoke(json);

    [When("{string} starts {string} from version {int}")]
    public Task Starts(string person, string title, int version) =>
        Invoke(new JsonObject { ["call"] = "start", ["card"] = cards[title], ["version"] = version, ["assignee"] = person }.ToJsonString());

    [When("{string} starts a card no one has from version {int}")]
    public Task StartsNoCard(string person, int version) =>
        Invoke(new JsonObject { ["call"] = "start", ["card"] = Guid.NewGuid(), ["version"] = version, ["assignee"] = person }.ToJsonString());

    [When("{string} renames {string} to {string} from version {int}")]
    public Task Renames(string _, string title, string to, int version) =>
        Invoke(new JsonObject { ["call"] = "rename", ["card"] = cards[title], ["version"] = version, ["title"] = to }.ToJsonString());

    // -- Then -----------------------------------------------------------------

    [Then("the publish has no IL warning naming Plasm")]
    public void NoWarning()
    {
        output.WriteLine(published.Log);
        Equal("0", published.Line(@"^publish: \d+ IL warnings, (\d+) naming Plasm"), "IL warnings naming Plasm");
    }

    [Then("bootstrap needs no glibc newer than Amazon Linux 2023's")]
    public void Glibc()
    {
        Equal(true, File.Exists(Path.Combine(published.Out, "bootstrap")), "bootstrap published");
        var needed = Version.Parse(published.Line(@"newest glibc symbol ([0-9.]+)"));
        var has = Version.Parse(published.Line(@"^publish: Amazon Linux 2023 has glibc ([0-9.]+)"));
        if (needed > has) throw new Exception($"bootstrap needs glibc {needed}; Amazon Linux 2023 has {has}");
    }

    /// The function logs "KanbanData opened" when OpenAsync returns, and
    /// "invocation" as its handler ends each event. (The emulator starts the
    /// function when the first event comes, so its own "START RequestId" line
    /// comes before the function's init; on Lambda, init comes first.)
    [Then("the function's log says KanbanData was opened once, before its first event")]
    public async Task OpenedOnce()
    {
        var lines = (await Docker.Logs(function!)).Split('\n');
        var opened = lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("KanbanData opened")).Select(x => x.i).ToList();
        var first = Array.FindIndex(lines, l => Regex.IsMatch(l, @"\binvocation [0-9.]+ ms"));
        Equal(1, opened.Count, "times KanbanData was opened");
        if (first < 0 || opened[0] > first) throw new Exception("KanbanData was not opened before the first event's handler ended:\n" + string.Join("\n", lines));
    }

    [Then("the function's log says KanbanData was opened without its start-up check")]
    public async Task OpenedUnchecked()
    {
        var log = await Docker.Logs(function!);
        if (!log.Contains("KanbanData opened without its start-up check")) throw new Exception("the function's log does not say so:\n" + log);
    }

    /// The emulator answers an event whose init failed with no body, and logs
    /// the failure (on Lambda, the invoker gets Runtime.ExitError); the
    /// function's log has the exception.
    [Then("its init fails, its log naming {string}")]
    public async Task InitFails(string name)
    {
        Equal("", answer, "the answer to an event whose init failed");
        var log = await Docker.Logs(function!);
        if (!log.Contains("Init failed") || !log.Contains("PlasmStartupException") || !log.Contains(name))
            throw new Exception($"the log does not have the init failing with PlasmStartupException naming {name}:\n{log}");
    }

    [Then("it answers {}")]
    public void AnswerIs(string expected) => Equal(JsonNode.Parse(expected)!.ToJsonString(), JsonNode.Parse(answer)?.ToJsonString(), "the answer");

    [Then("the answer is a board {string} whose cards are {string}")]
    public void BoardIs(string name, string expected)
    {
        var board = Case("board");
        Equal(name, (string?)board["name"], "the board's name");
        var shown = board["cards"]!.AsArray().Select(c => $"{c!["title"]} ({c["stage"]!["kind"]}, v{c["version"]})");
        Equal(expected, string.Join("; ", shown), "the board's cards");
    }

    [Then("the answer is the card {string} titled {string} at version {int}")]
    public void CardIs(string card, string title, int version)
    {
        var c = Case("card");
        Equal((cards[card], title, version), ((Guid)c["id"]!, (string)c["title"]!, (int)c["version"]!), "the card");
    }

    [Then("the answer is stale, naming the card {string}")]
    public void StaleNames(string title) => Equal(cards[title], (Guid)Case("stale")["card"]!, "the card named");

    /// The emulator answers an invocation that threw with the exception's
    /// type and message.
    [Then("the invocation fails with a JsonException naming {string}")]
    public void FailsWith(string name)
    {
        var error = JsonNode.Parse(answer) as JsonObject;
        Equal("JsonException", (string?)error?["errorType"], "the invocation's error type, of " + answer);
        var message = (string?)error?["errorMessage"] ?? "";
        // A missing argument is named as 'title', a null one by its path, $.title.
        if (!message.Contains($"'{name}'") && !message.Contains($"$.{name}")) throw new Exception($"the error does not name '{name}': {message}");
    }

    [Then("the board {string} still has {int} cards")]
    public async Task StillHas(string slug, int count)
    {
        await Invoke(new JsonObject { ["call"] = "board", ["slug"] = slug }.ToJsonString());
        Equal(count, Case("board")["cards"]!.AsArray().Count, $"cards on board {slug}");
    }
}
