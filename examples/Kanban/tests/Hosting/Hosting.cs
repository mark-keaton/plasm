// A Kanban host run as its own process, and a database for it: shared by the
// scenarios of the C# and GraphQL hosts, which talk to the host over HTTP.
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
public sealed class HostProcess : IAsyncDisposable
{
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
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        var host = new HostProcess(Process.Start(start)!, address);

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

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process.Dispose();
    }
}

