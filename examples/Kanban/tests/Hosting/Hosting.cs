// A Kanban host run as its own process, and a database for it: shared by the
// scenarios of Host.CSharp and Host, which talk to the host over HTTP.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Kanban.Hosting;

/// Real PostgreSQL: the server PLASM_TEST_PG names, or one container per run.
public static class Databases
{
    static readonly Lazy<Task<string>> Admin = new(async () =>
    {
        var named = Environment.GetEnvironmentVariable("PLASM_TEST_PG");
        if (!string.IsNullOrEmpty(named)) return named;
        var container = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:18.4").Build();
        await container.StartAsync();
        return container.GetConnectionString();
    });

    /// A new database with the F# server's migrations applied.
    public static async Task<string> Migrated()
    {
        var admin = await Admin.Value;
        var name = "kanban_cs_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {name}", connection);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        // F#'s Result<unit, string>, as C# sees it.
        var migrated = Kanban.Server.Database.migrate(connectionString);
        if (migrated.IsError) throw new InvalidOperationException("migrating failed: " + migrated.ErrorValue);
        return connectionString;
    }
}

/// The host, started as its own process on a free port.
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
    public Uri Address { get; }

    HostProcess(Process process, Uri address)
    {
        this.process = process;
        Address = address;
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
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var address = new Uri($"http://127.0.0.1:{port}");
        var program = Environment.GetEnvironmentVariable(native);
        var start = string.IsNullOrEmpty(program)
            ? new ProcessStartInfo("dotnet") { ArgumentList = { built } }
            : new ProcessStartInfo(program);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(address.ToString());
        start.Environment["ConnectionStrings__Kanban"] = connectionString;
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) start.Environment[k] = v;
        start.Environment["KANBAN_EXIT_WITH_STDIN"] = "1";
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        var started = Process.Start(start)!;
        lock (Running) Running.Add(started);
        var host = new HostProcess(started, address);

        using var http = new HttpClient { BaseAddress = address };
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (host.process.HasExited)
                throw new InvalidOperationException($"the host exited with {host.process.ExitCode}:\n{host.Output}");
            try
            {
                using var answer = await http.GetAsync(readyPath);
                if (answer.IsSuccessStatusCode) return host;
            }
            catch (HttpRequestException) { }
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

