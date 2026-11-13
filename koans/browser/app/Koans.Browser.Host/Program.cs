using System;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Plasm.PGlite;

[assembly: SupportedOSPlatform("browser")]

/// PGlite, provided by worker.js.
internal static partial class Js
{
    [JSImport("pglite.query", "worker.js")]
    internal static partial Task<string> Query(string sql, string paramsJson, string typesJson);

    [JSImport("pglite.execSync", "worker.js")]
    internal static partial string ExecSync(string sql);
}

internal sealed class JsBridge : IPGliteBridge
{
    public Task<string> Query(string sql, string paramsJson, string typesJson) => Js.Query(sql, paramsJson, typesJson);
    public string Execute(string sql) => Js.ExecSync(sql);
}

public static partial class KoansHost
{
    const string RefsDir = "/refs";

    public static void Main() { }

    /// Stores one compiler reference assembly (fetched by worker.js).
    [JSExport]
    public static void AddReference(string name, byte[] bytes)
    {
        Directory.CreateDirectory(RefsDir);
        File.WriteAllBytes(Path.Combine(RefsDir, name), bytes);
    }

    [JSExport]
    public static Task<string> Init() => Plasm.Koans.Browser.KoanRun.init(new JsBridge());

    [JSExport]
    public static Task<string> RunKoan(string title, string name, string helpers, bool isTask, string lambda, string body) =>
        Plasm.Koans.Browser.KoanRun.run(RefsDir, title, name, helpers, isTask, lambda, body);
}
