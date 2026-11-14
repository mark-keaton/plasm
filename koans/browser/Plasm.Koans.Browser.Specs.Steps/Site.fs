/// The koans site, staged once per test run and served as static files, and a
/// browser (Playwright) to open it in.
module Plasm.Koans.Browser.Specs.Site

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Playwright

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
let app = Path.Combine(root, "koans", "browser", "app")

/// Builds the site (unless PLASM_KOANS_SITE names an already staged one).
let private staged =
    lazy
        (match Environment.GetEnvironmentVariable "PLASM_KOANS_SITE" with
         | null
         | "" ->
             let site = Path.Combine(app, "site")
             let script = Path.Combine(app, "stage.sh")
             let psi = ProcessStartInfo("bash", $"\"{script}\" \"{site}\"", RedirectStandardOutput = true, RedirectStandardError = true)
             use p = Process.Start psi
             let out = p.StandardOutput.ReadToEndAsync()
             let err = p.StandardError.ReadToEndAsync()
             p.WaitForExit()
             if p.ExitCode <> 0 then failwithf "staging the koans site failed:\n%s\n%s" out.Result err.Result
             site
         | site -> site)

let private contentType (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".html" -> "text/html; charset=utf-8"
    | ".js"
    | ".mjs" -> "text/javascript"
    | ".json" -> "application/json"
    | ".wasm" -> "application/wasm"
    | ".css" -> "text/css"
    | _ -> "application/octet-stream"

/// What the site has served: (method, path, bytes on the wire), in order.
let served = Collections.Concurrent.ConcurrentQueue<string * string * int64>()

/// Simulated connection speed in bytes per second (0 = unlimited), shared by
/// every response, as one network link would be.
let mutable bytesPerSecond = 0.0

/// The Accept-Encoding of the last request (diagnostics).
let mutable lastAccept = ""
let private link = obj ()
let mutable private linkFree = DateTime.UtcNow

let private throttle (bytes: int) =
    task {
        if bytesPerSecond > 0.0 then
            let finish =
                lock link (fun () ->
                    let start = max DateTime.UtcNow linkFree
                    linkFree <- start.AddSeconds(float bytes / bytesPerSecond)
                    linkFree)

            let wait = finish - DateTime.UtcNow
            if wait > TimeSpan.Zero then do! Task.Delay wait
    }

/// Serves the staged site on a free local port; returns its base URL. Like a
/// static host, it sends the precompressed .br or .gz file when the browser accepts it.
let private server =
    lazy
        (let site = staged.Force()
         let port =
             let l = new TcpListener(IPAddress.Loopback, 0)
             l.Start()
             let p = (l.LocalEndpoint :?> IPEndPoint).Port
             l.Stop()
             p

         let listener = new HttpListener()
         listener.Prefixes.Add $"http://127.0.0.1:{port}/"
         listener.Start()

         let handle (ctx: HttpListenerContext) =
             task {
                 try
                     let path = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath.TrimStart('/'))
                     let path = if path = "" then "index.html" else path
                     let file = Path.GetFullPath(Path.Combine(site, path))

                     if ctx.Request.HttpMethod = "GET" && file.StartsWith(Path.GetFullPath site) && File.Exists file then
                         let accepted = defaultArg (Option.ofObj ctx.Request.Headers.["Accept-Encoding"]) ""
                         lastAccept <- accepted

                         let body, encoding =
                             if accepted.Contains "br" && File.Exists(file + ".br") then File.ReadAllBytes(file + ".br"), Some "br"
                             elif accepted.Contains "gzip" && File.Exists(file + ".gz") then File.ReadAllBytes(file + ".gz"), Some "gzip"
                             else File.ReadAllBytes file, None
                         served.Enqueue(ctx.Request.HttpMethod, path, int64 body.Length)
                         ctx.Response.ContentType <- contentType file
                         ctx.Response.Headers.["Cache-Control"] <- "no-cache"
                         encoding |> Option.iter (fun e -> ctx.Response.Headers.["Content-Encoding"] <- e)
                         ctx.Response.ContentLength64 <- int64 body.Length

                         // In chunks, so a throttled link interleaves parallel downloads.
                         for offset in 0 .. 65536 .. body.Length - 1 do
                             let n = min 65536 (body.Length - offset)
                             do! throttle n
                             do! ctx.Response.OutputStream.WriteAsync(body, offset, n)
                     else
                         served.Enqueue(ctx.Request.HttpMethod, path, 0L)
                         ctx.Response.StatusCode <- 404
                 finally
                     ctx.Response.Close()
             }
             :> Task

         Task.Run(fun () ->
             task {
                 while listener.IsListening do
                     let! ctx = listener.GetContextAsync()
                     handle ctx |> ignore
             }
             :> Task)
         |> ignore

         $"http://127.0.0.1:{port}/")

let baseUrl () = server.Force()

/// One browser for the run: the installed headless shell when present
/// (PLAYWRIGHT_CHROMIUM overrides), otherwise Playwright's own Chromium.
let private browser =
    lazy
        (task {
            let! pw = Playwright.CreateAsync()

            let shell =
                match Environment.GetEnvironmentVariable "PLAYWRIGHT_CHROMIUM" with
                | null
                | "" ->
                    let p =
                        Path.Combine(
                            Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                            "Library/Caches/ms-playwright/chromium_headless_shell-1248/chrome-headless-shell-mac-arm64/chrome-headless-shell"
                        )

                    if File.Exists p then Some p else None
                | p -> Some p

            let options = BrowserTypeLaunchOptions(Headless = true)
            shell |> Option.iter (fun p -> options.ExecutablePath <- p)
            return! pw.Chromium.LaunchAsync options
        })

/// A new browser context's page (a first visit), not yet navigated.
let newPage () =
    task {
        let! b = browser.Force()
        return! b.NewPageAsync()
    }

/// A new page with the koans loaded and ready.
let openKoans () =
    task {
        let! page = newPage ()
        let! _ = page.GotoAsync(baseUrl () + "index.html")
        let! _ = page.WaitForFunctionAsync("() => window.koansReady === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
        return page
    }

// ---------------------------------------------------------------------------
// Reference answers (koans/answers), applied to a koan's code
// ---------------------------------------------------------------------------

let answers =
    lazy
        (Directory.GetFiles(Path.Combine(root, "koans", "answers"), "*.answers")
         |> Array.collect (fun f ->
             let mutable current = None
             let found = Collections.Generic.Dictionary<string, ResizeArray<string>>()

             for line in File.ReadAllLines f do
                 if line.StartsWith "# " then
                     current <- Some(line.Substring 2)
                     found.[line.Substring 2] <- ResizeArray()
                 elif line.Trim() <> "" then
                     current |> Option.iter (fun k -> found.[k].Add line)

             found |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Array.ofSeq)
         |> Map.ofArray)

let private blank = Regex(@"(?<![\w.])__(?!\w)")

/// The koan's code with its blanks replaced, in order, by `values`.
let fill (body: string) (values: string list) =
    let mutable rest = values

    body.Split('\n')
    |> Array.map (fun line ->
        let cut = match line.IndexOf "//" with | -1 -> line.Length | i -> i
        let code = blank.Replace(line.Substring(0, cut), (fun _ ->
            let v = List.head rest
            rest <- List.tail rest
            "(" + v + ")"))
        code + line.Substring cut)
    |> String.concat "\n"

/// Where the first blank to fill in is in some code: the first __ outside
/// comments and strings (a comment may mention __), or -1.
let firstBlankAt (code: string) =
    let hidden = Regex.Replace(code, "//[^\n]*|\"(?:\\\\.|[^\"\\\\\n])*\"", (fun (m: Match) -> String(' ', m.Length)))
    let m = blank.Match hidden
    if m.Success then m.Index else -1

/// The 1-based line of the first blank in a koan's code.
let firstBlankLine (body: string) =
    body.Split('\n')
    |> Array.findIndex (fun l -> blank.IsMatch(match l.IndexOf "//" with | -1 -> l | i -> l.Substring(0, i)))
    |> (+) 1
