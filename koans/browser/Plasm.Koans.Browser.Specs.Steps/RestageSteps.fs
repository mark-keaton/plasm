namespace Plasm.Koans.Browser.Specs

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Microsoft.Playwright
open Reqnroll
open Plasm.Koans.Browser.Specs.Site

/// Feature: The koans run in a web browser, rule "A reload shows the site as
/// it was last staged". A copy of the staged site, served by a plain static
/// host, is restaged in place between two visits.
[<Binding>]
type RestageSteps(w: BrowserWorld) =
    let mutable copy = ""
    let mutable hoverJson = ""

    let ready (page: IPage) =
        task {
            let! _ = page.WaitForFunctionAsync("() => window.koansReady === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            ()
        }

    [<StepDefinition("a learner has opened the koans page once, from a plain static host staged a day ago")>]
    member _.OpenedFromPlainHost() =
        task {
            let site = stagedSite ()
            copy <- Path.Combine(Path.GetTempPath(), "plasm-koans-restage-" + string (Guid.NewGuid()))
            let dayAgo = DateTime.UtcNow.AddDays -1.0

            for file in Directory.GetFiles(site, "*", SearchOption.AllDirectories) do
                let target = Path.Combine(copy, Path.GetRelativePath(site, file))
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                File.Copy(file, target)
                File.SetLastWriteTimeUtc(target, dayAgo)

            let url = serve copy true
            let! page = newPage ()
            w.Page <- page
            let! _ = page.GotoAsync(url + "index.html")
            do! ready page
            // The site is stored for offline use in the background.
            let! _ = page.WaitForFunctionAsync("() => window.koansOffline === true", null, PageWaitForFunctionOptions(Timeout = Nullable 180000.f))
            ()
        }
        |> unit

    /// Restages the copy as stage.sh would after a change to Plasm's doc
    /// comments: the new refs/Plasm.xml, then the seal step.
    [<StepDefinition("the site is restaged with Repo documented as \"(.*)\"")>]
    member _.Restage(doc: string) =
        let refs = Path.Combine(copy, "refs")
        let old = Directory.GetFiles refs |> Array.find (fun f -> Regex.IsMatch(Path.GetFileName f, @"^Plasm(\.[0-9a-f]{16})?\.xml$"))
        let text = File.ReadAllText old
        check (text.Contains "A repository: where Plasm meets the database") "Plasm.xml no longer documents Repo as expected"
        for f in [ old; old + ".br"; old + ".gz" ] do File.Delete f
        File.WriteAllText(Path.Combine(refs, "Plasm.xml"), text.Replace("A repository: where Plasm meets the database", doc))
        let seal = Path.Combine(app, "seal.mjs")
        use p = Process.Start(ProcessStartInfo("node", $"\"{seal}\" \"{copy}\"", RedirectStandardOutput = true, RedirectStandardError = true))
        let err = p.StandardError.ReadToEnd()
        p.WaitForExit()
        check (p.ExitCode = 0) $"sealing the restaged site failed: {err}"

    [<StepDefinition("pointing at \"(\\w+)\" in \"(.*)\" in the koan \"(.*)\" says \"(.*)\"")>]
    member _.PointingSays(name: string, within: string, koanName: string, text: string) =
        task {
            let! koan = koanNamed w.Page koanName
            let at = koan.Body.IndexOf within + within.IndexOf name + name.Length
            let! json = w.Page.EvaluateAsync<string>("async ([l, k, c, o]) => JSON.stringify(await window.koans.hover(l, k, c, o))", box [| box koan.Lesson; box koan.Index; box koan.Body; box at |])
            check (json.Contains text) $"pointing at {name} says {json}"
        }
        |> unit

    [<AfterScenario>]
    member _.RemoveCopy() =
        if copy <> "" && Directory.Exists copy then
            try Directory.Delete(copy, true) with _ -> ()
