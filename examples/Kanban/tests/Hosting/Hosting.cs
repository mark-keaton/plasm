// A Kanban host run as its own process, and a database for it: shared by the
// scenarios of Host.CSharp and Host, which talk to the host over HTTP.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Kanban.Hosting;

/// Real PostgreSQL: the server PLASM_TEST_PG names, or one container per run.
/// Each scenario's database is named with this machine and this test
/// process's id, and dropped when its host has stopped (Drop). One a killed
/// run left is dropped by the next run on the same machine, whose first
/// database finds that process gone; a database another machine made is
/// never dropped here, and a process id since reused only delays a drop.
/// What the test project's build recorded (AssemblyMetadata in its project
/// file): where the host's own output is, and the FSharp.Core every
/// application here runs on.
public static class Built
{
    public static string Metadata(string key) =>
        typeof(Built).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// Every FSharp.Core version a deps.json names (its libraries).
    public static string FSharpCoreIn(string depsJson) =>
        string.Join(", ", Regex.Matches(File.ReadAllText(depsJson), "\"FSharp\\.Core/([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct());
}

public static class Databases
{
    /// This machine, as a database name may hold it: the first 8 hex digits
    /// of a hash of its name.
    static readonly string Machine = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.ToLowerInvariant())))[..8];

    static readonly Lazy<Task<string>> Admin = new(async () =>
    {
        var named = Environment.GetEnvironmentVariable("PLASM_TEST_PG");
        if (!string.IsNullOrEmpty(named))
        {
            await DropOrphans(named);
            return named;
        }
        var container = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:18.4").Build();
        await container.StartAsync();
        return container.GetConnectionString();
    });

    static readonly Regex Ours = new(@"^kanban_cs_([0-9a-f]{8})_(\d+)_[0-9a-f]{32}$");

    static async Task Execute(string admin, string sql)
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    /// The databases of test runs on this machine whose process is gone.
    /// Alive means kept, so a reused process id never causes a wrong drop.
    static async Task DropOrphans(string admin)
    {
        var names = new List<string>();
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var read = new NpgsqlCommand("select datname from pg_database where datname like 'kanban\\_cs\\_%'", connection);
            await using var rows = await read.ExecuteReaderAsync();
            while (await rows.ReadAsync()) names.Add(rows.GetString(0));
        }
        foreach (var name in names)
            if (Ours.Match(name) is { Success: true } m && m.Groups[1].Value == Machine && !Running(int.Parse(m.Groups[2].Value)))
                await Execute(admin, $"drop database if exists {name} with (force)");
    }

    /// Drops a database Migrated made, closing whatever connection is still open to it.
    public static async Task Drop(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        if (!Ours.IsMatch(name)) throw new ArgumentException($"{name} is not a scenario's database");
        await Execute(await Admin.Value, $"drop database if exists {name} with (force)");
    }

    /// A new database with the F# server's migrations applied.
    public static async Task<string> Migrated()
    {
        var admin = await Admin.Value;
        var name = $"kanban_cs_{Machine}_{Environment.ProcessId}_{Guid.NewGuid():N}";
        await Execute(admin, $"create database {name}");
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        // F#'s Result<unit, string>, as C# sees it.
        var migrated = Kanban.Server.Database.migrate(connectionString);
        if (migrated.IsError) throw new InvalidOperationException("migrating failed: " + migrated.ErrorValue);
        return connectionString;
    }
}

/// The host, started as its own process on a port the system gives it: it
/// listens on port 0, and its address is read from what Kestrel writes
/// ("Now listening on: ..."), so two test runs side by side cannot both be
/// handed one port.
/// A host started for a scenario, stopped three ways so that none outlives
/// its test run: when its scenario ends (Stop, from the steps' AfterScenario
/// hook), when the test process exits (every host still running is
/// stopped), and when the test process dies without exiting (the host is
/// started with KANBAN_EXIT_WITH_STDIN=1 and a standard input this process
/// holds open, and exits when it closes).
public sealed class HostProcess : IAsyncDisposable
{
    static readonly List<Process> Running = new();

    static HostProcess() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Running)
                foreach (var p in Running)
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        };

    readonly Process process;
    readonly StringBuilder output = new();
    volatile Uri? address;

    /// Where the host listens, once it has said so.
    public Uri Address => address ?? throw new InvalidOperationException("the host has not said where it listens");

    HostProcess(Process process)
    {
        this.process = process;
        process.OutputDataReceived += (_, e) =>
        {
            const string listening = "Now listening on: ";
            var at = e.Data?.IndexOf(listening) ?? -1;
            if (at >= 0 && address is null) address = new Uri(e.Data!.Substring(at + listening.Length).Trim());
        };
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Output { get { lock (output) return output.ToString(); } }

    /// What the host has written so far, one line each.
    public string[] Lines { get { lock (output) return output.ToString().Split('\n'); } }

    /// `built` run with dotnet, or the program the variable `native` names; ready
    /// when `readyPath` answers 200; `environment` set besides the connection string.
    public static async Task<HostProcess> Start(string built, string native, string connectionString, string readyPath, IDictionary<string, string>? environment = null)
    {
        var program = Environment.GetEnvironmentVariable(native);
        var start = string.IsNullOrEmpty(program)
            ? new ProcessStartInfo("dotnet") { ArgumentList = { built } }
            : new ProcessStartInfo(program);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ConnectionStrings__Kanban"] = connectionString;
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) start.Environment[k] = v;
        start.Environment["KANBAN_EXIT_WITH_STDIN"] = "1";
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        var started = Process.Start(start)!;
        lock (Running) Running.Add(started);
        var host = new HostProcess(started);

        using var http = new HttpClient();
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (host.process.HasExited)
                throw new InvalidOperationException($"the host exited with {host.process.ExitCode}:\n{host.Output}");
            if (host.address is { } listening)
            {
                try
                {
                    using var answer = await http.GetAsync(new Uri(listening, readyPath));
                    if (answer.IsSuccessStatusCode) return host;
                }
                catch (HttpRequestException) { }
            }
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"the host did not answer within 60 s:\n{host.Output}");
            await Task.Delay(100);
        }
    }

    /// Stops the host and everything it started.
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        lock (Running) Running.Remove(process);
        process.Dispose();
    }
}

