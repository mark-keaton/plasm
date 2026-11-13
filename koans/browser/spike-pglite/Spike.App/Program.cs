using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Plasm.PGlite;

[assembly: SupportedOSPlatform("browser")]

/// The JavaScript side, registered by main.js with setModuleImports("main.js", ...).
internal static partial class Js
{
    [JSImport("pglite.query", "main.js")]
    internal static partial Task<string> Query(string sql, string paramsJson, string typesJson);

    [JSImport("pglite.execSync", "main.js")]
    internal static partial string ExecSync(string sql);

    [JSImport("report", "main.js")]
    internal static partial void Report(string name, bool passed, string detail);
}

/// PGlite reached through JS interop.
internal sealed class JsBridge : IPGliteBridge
{
    public Task<string> Query(string sql, string paramsJson, string typesJson) => Js.Query(sql, paramsJson, typesJson);

    public string Execute(string sql) => Js.ExecSync(sql);
}

public static class Program
{
    public static async Task Main()
    {
        Js.Report(".NET runtime started", true, Environment.Version.ToString());
        await Plasm.Koans.Browser.Scenarios.run(new JsBridge(), new Action<string, bool, string>(Js.Report));
    }
}
