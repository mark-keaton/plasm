namespace Plasm.Koans.Browser.Specs

open System
open System.Threading.Tasks
open Microsoft.Playwright
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// Feature: The koans run in a web browser
[<Binding>]
type LoadingSteps(w: BrowserWorld) =

    let statusLog = "() => window.statusLog || []"
    let requests = Collections.Concurrent.ConcurrentQueue<string * string * string>() // method, url, post data
    let mutable bytes = 0L
    let mutable readyMs = 0L

    /// Records every request and compressed response size of a context
    /// (pages and their workers), and every status message the page shows.
    let watch (page: IPage) =
        task {
            page.Context.add_Request(fun _ r -> requests.Enqueue(r.Method, r.Url, (match r.PostData with null -> "" | d -> d)))

            page.Context.add_Response(fun _ r ->
                match r.Headers.TryGetValue "content-length" with
                | true, v -> Threading.Interlocked.Add(&bytes, int64 v) |> ignore
                | _ -> ())

            let! _ =
                page.AddInitScriptAsync(
                    "window.statusLog = []; new MutationObserver(() => { const s = document.getElementById('status'); if (s) window.statusLog.push(s.textContent); }).observe(document, { subtree: true, childList: true, characterData: true });"
                )

            return ()
        }

    let openAndWait (page: IPage) =
        task {
            let started = Diagnostics.Stopwatch.StartNew()
            let! _ = page.GotoAsync(baseUrl () + "index.html")
            let! _ = page.WaitForFunctionAsync("() => window.koansReady === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            readyMs <- started.ElapsedMilliseconds
        }

    [<StepDefinition("the koans site is served as static files")>]
    member _.Served() = baseUrl () |> ignore

    [<StepDefinition("a learner opens the koans page in a browser")>]
    member _.Open() =
        task {
            let! page = newPage ()
            w.Page <- page
            do! watch page
            do! openAndWait page
        }
        |> unit

    [<StepDefinition("the page shows the lessons and the first koan with its blank")>]
    member _.ShowsFirst() =
        task {
            let! buttons = w.Page.Locator("nav button[data-koan]").CountAsync()
            let! first = koanAt w.Page 0 0
            let! name = w.Page.TextContentAsync "#name"
            let! blanks = w.Page.Locator("#highlight mark.blank").CountAsync()
            let expected = Specs.koans.Value.Length
            check (buttons = expected) $"{buttons} koans listed, not the {expected} the lessons specify"
            check (name = first.Name) $"the page shows {name}"
            check (blanks > 0) "the first koan shows no blank"
        }
        |> unit

    [<StepDefinition("the first koan can be run once loading has finished")>]
    [<StepDefinition("the first koan can be run")>]
    member _.RunFirst() =
        task {
            let! first = koanAt w.Page 0 0
            do! show w.Page first
            let! _ = run w.Page
            let! status, text = result w.Page
            check (status = "unanswered") $"status {status}: {text}"
        }
        |> unit

    [<StepDefinition("the page shows what is loading \\(the compiler, the libraries, the database\\) until it is ready")>]
    member _.Progress() =
        task {
            let! log = w.Page.EvaluateAsync<string[]>(statusLog)
            let text = String.concat "\n" log

            for part in [ "the database"; "the .NET libraries"; "the compiler" ] do
                check (text.Contains $"Loading" && text.Contains part) $"the loading messages never mention {part}:\n{text}"
        }
        |> unit

    [<StepDefinition("a learner opens the koans page on a typical connection")>]
    member _.OpenTypical() =
        task {
            // 25 Mbit/s, shared by every download, as on one home connection.
            Site.bytesPerSecond <- 25_000_000.0 / 8.0

            try
                let! page = newPage ()
                w.Page <- page
                do! watch page
                do! openAndWait page
            finally
                Site.bytesPerSecond <- 0.0
        }
        |> unit

    [<StepDefinition("the first koan can be run within (\\d+) seconds")>]
    member _.Within(seconds: int) =
        let encodings =
            Site.served |> Seq.groupBy (fun (_, path, _) -> path.Split('/').[0]) |> Seq.map (fun (k, v) -> k, Seq.length v, (v |> Seq.sumBy (fun (_, _, b) -> b)) / 1048576L) |> List.ofSeq
        check (readyMs <= int64 seconds * 1000L) $"the first koan could be run after {readyMs} ms ({float bytes / 1048576.0:F1} MB; served %A{encodings}; accept-encoding {Site.lastAccept})"

    [<StepDefinition("the page transfers at most (\\d+) MB, compressed")>]
    member _.AtMost(mb: int) =
        let total = float bytes / 1048576.0
        check (total <= float mb) $"the page transferred {total:F1} MB"

    [<StepDefinition("the koans page is opened in a browser without WebAssembly")>]
    member _.NoWasm() =
        task {
            let! page = newPage ()
            w.Page <- page
            let! _ = page.AddInitScriptAsync("delete window.WebAssembly;")
            let! _ = page.GotoAsync(baseUrl () + "index.html")
            do! Task.Delay 1000
        }
        |> unit

    [<StepDefinition("the page explains that WebAssembly is required and links to the koans for the terminal")>]
    member _.Explains() =
        task {
            let! text = w.Page.TextContentAsync "#status"
            let! href = w.Page.GetAttributeAsync("#status a", "href")
            check (text.Contains "WebAssembly") $"the page says: {text}"
            check (not (isNull href)) "no link to the terminal koans"
            let! status = w.Page.EvaluateAsync<int>("async href => (await fetch(href)).status", href)
            check (status = 200) $"the link {href} gives {status}"
        }
        |> unit

    [<StepDefinition("a learner runs every koan of the first lesson")>]
    member _.RunFirstLesson() =
        task {
            let! page = newPage ()
            w.Page <- page
            do! watch page
            do! openAndWait page
            let! count = page.EvaluateAsync<int>("() => window.koans.course.lessons[0].koans.length")

            for k in 0 .. count - 1 do
                let! koan = koanAt page 0 k
                do! show page koan
                do! page.FillAsync("#code", fill koan.Body answers.Value.[koan.Name])
                let! _ = run page
                ()
        }
        |> unit

    [<StepDefinition("every network request is for a static file the site serves")>]
    member _.OnlyStatic() =
        let base' = baseUrl ()
        let foreign = requests |> Seq.filter (fun (_, url, _) -> not (url.StartsWith base' || url.StartsWith "data:" || url.StartsWith "blob:")) |> List.ofSeq
        check foreign.IsEmpty $"requests outside the site: %A{foreign}"
        let notGet = requests |> Seq.filter (fun (m, _, _) -> m <> "GET") |> List.ofSeq
        check notGet.IsEmpty $"requests that are not GET: %A{notGet}"

    [<StepDefinition("no code or data is sent anywhere")>]
    member _.NothingSent() =
        let withBody = requests |> Seq.filter (fun (_, _, body) -> body <> "") |> List.ofSeq
        check withBody.IsEmpty $"requests that send data: %A{withBody}"

    [<StepDefinition("a learner has opened the koans page once")>]
    member _.OpenedOnce() =
        task {
            let! page = newPage ()
            w.Page <- page
            do! openAndWait page
            // The site is stored for offline use in the background.
            let! _ = page.WaitForFunctionAsync("() => window.koansOffline === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            ()
        }
        |> unit

    [<StepDefinition("the learner opens it again without a network connection")>]
    member _.OpenOffline() =
        task {
            do! w.Page.Context.SetOfflineAsync true
            let! _ = w.Page.ReloadAsync()
            let! _ = w.Page.WaitForFunctionAsync("() => window.koansReady === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            ()
        }
        |> unit
