namespace Plasm.Seam.Specs

open System
open System.IO
open Reqnroll
open Plasm.Seam

/// One scenario's project: its F# data assembly, its Seam.cs, and what the
/// seam made of them.
type SeamProject() =
    static let mutable n = 0
    let dir =
        // Scenarios run in parallel: each takes its own number.
        let d = Path.Combine(Compilers.work, $"scenario-{Threading.Interlocked.Increment &n}")
        Directory.CreateDirectory d |> ignore
        d

    member _.Dir = dir
    member _.Out = Path.Combine(dir, "Seam")
    member _.Lock = Path.Combine(dir, "seam.lock")
    member _.Config = Path.Combine(dir, "Seam.cs")
    member val Data: string option = None with get, set
    member val Ran: Result<Program.Ran, string * string * string option> option = None with get, set
    member val Times: Map<string, DateTime> = Map.empty with get, set

[<Binding>]
type SeamSteps(p: SeamProject) =

    let fail (message: string) : 'a = failwith message

    let data () =
        match p.Data with
        | Some d -> d
        | None -> fail "no F# data project was given"

    let ran () =
        match p.Ran with
        | Some(Ok r) -> r
        | Some(Error(code, text, origin)) -> fail $"the seam stopped: {code} at {origin}: {text}"
        | None -> fail "the seam was not written"

    let written () =
        [ yield p.Lock
          if Directory.Exists p.Out then yield! Directory.GetFiles(p.Out) ]

    let write (ns: string) =
        let options: Program.Options =
            { Config = p.Config
              Out = p.Out
              Lock = p.Lock
              Namespace = ns
              References = Compilers.references () @ [ data () ] |> Array.ofList
              Data = [| data () |]
              Fail = false
              Http = false }

        p.Ran <-
            Some(
                try
                    Ok(Program.run options)
                with SeamError(code, text, origin) ->
                    Error(code, text, origin)
            )

    let unquote (s: string) = s.Replace("\\\"", "\"")

    /// C# as a reader compares it: spacing and line breaks aside.
    let squeeze (s: string) =
        Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Replace("( ", "(").Replace(" )", ")")

    let file (name: string) =
        ran () |> ignore
        let path = Path.Combine(p.Out, name)
        if not (File.Exists path) then fail $"the seam wrote no {name}; it wrote {written () |> List.map Path.GetFileName}"
        File.ReadAllText path

    let compiles (host: (string * string) list) =
        let r = ran ()
        let sources = ("Seam.cs", File.ReadAllText p.Config) :: r.Files @ host
        let diagnostics = Compilers.csharp sources (data ())

        if not diagnostics.IsEmpty then
            fail ("the C# does not compile cleanly:\n" + (diagnostics |> List.map string |> String.concat "\n"))

    [<StepDefinition("^the F# data project:$")>]
    member _.DataProject(source: string) =
        match Compilers.fsharp "Shop" source with
        | Ok dll -> p.Data <- Some dll
        | Error errors -> fail $"the F# data project does not compile:\n{errors}"

    [<StepDefinition("^Seam\\.cs:$")>]
    member _.SeamCs(text: string) = File.WriteAllText(p.Config, text)

    [<StepDefinition("^the seam is written into the namespace ([\\w.]+)$")>]
    member _.Written(ns: string) = write ns

    [<StepDefinition("^the seam is written into the namespace ([\\w.]+) again$")>]
    member _.WrittenAgain(ns: string) =
        ran () |> ignore
        p.Times <- written () |> List.map (fun f -> f, File.GetLastWriteTimeUtc f) |> Map.ofList
        // A file written now would have a later time than any before.
        Threading.Thread.Sleep 20
        write ns

    [<StepDefinition("^(\\w+\\.g\\.cs) declares \"(.*)\"$")>]
    member _.Declares(name: string, text: string) =
        let source = file name
        let want = unquote text

        if not ((squeeze source).Contains(squeeze want)) then
            fail $"{name} does not declare\n  {want}\nIt reads:\n{source}"

    [<StepDefinition("^seam\\.lock holds$")>]
    member _.LockHolds(text: string) =
        ran () |> ignore
        let lock = File.ReadAllText(p.Lock).ReplaceLineEndings "\n"

        if not (lock.Contains(text.ReplaceLineEndings "\n")) then
            fail $"seam.lock does not hold\n{text}\nIt reads:\n{lock}"

    [<StepDefinition("^the C# compiles with the host$")>]
    member _.Compiles() = compiles []

    [<StepDefinition("^the C# compiles with the host:$")>]
    member _.CompilesWith(host: string) = compiles [ "Host.cs", host ]

    [<StepDefinition("^the C# with the host stops with (\\w+) at (.*?): \"(.*)\"$")>]
    member _.CompileStops(code: string, at: string, text: string, host: string) =
        let r = ran ()
        let sources = ("Seam.cs", File.ReadAllText p.Config) :: r.Files @ [ "Host.cs", host ]
        let diagnostics = Compilers.csharp sources (data ())
        let want = unquote text

        let found =
            diagnostics
            |> List.tryFind (fun d ->
                let where = d.Location.GetLineSpan()
                let place = $"{IO.Path.GetFileName where.Path}({where.StartLinePosition.Line + 1},{where.StartLinePosition.Character + 1})"

                d.Id = code
                && d.Severity = Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                && place = at
                && d.GetMessage().StartsWith want)

        if found.IsNone then
            fail ($"the C# did not stop with {code} at {at}: {want}\nIt says:\n" + (diagnostics |> List.map string |> String.concat "\n"))

    [<StepDefinition("^the seam stops with (SEAM\\d+) at (.*?): \"(.*)\"$")>]
    member _.Stops(code: string, at: string, text: string) =
        match p.Ran with
        | Some(Error(c, t, origin)) ->
            let where = origin |> Option.map Path.GetFileName |> Option.defaultValue "(nowhere)"

            if c <> code || where <> at || not (t.StartsWith(unquote text)) then
                fail $"the seam stopped with {c} at {where}: {t}"
        | Some(Ok _) -> fail "the seam wrote its files, and did not stop"
        | None -> fail "the seam was not written"

    [<StepDefinition("^the seam stops with (SEAM\\d+): \"(.*)\"$")>]
    member _.StopsAnywhere(code: string, text: string) =
        match p.Ran with
        | Some(Error(c, t, _)) ->
            if c <> code || not (t.StartsWith(unquote text)) then
                fail $"the seam stopped with {c}: {t}"
        | Some(Ok _) -> fail "the seam wrote its files, and did not stop"
        | None -> fail "the seam was not written"

    [<StepDefinition("^the host, run, prints \"(.*)\"$")>]
    member _.Prints(text: string, host: string) =
        let r = ran ()
        let sources = ("Seam.cs", File.ReadAllText p.Config) :: r.Files @ [ "Host.cs", host ]
        let printed = Compilers.run sources (data ())

        if printed <> unquote text then
            fail $"the host printed\n  {printed}\nnot\n  {unquote text}"

    [<StepDefinition("^no file in Seam is rewritten, nor seam\\.lock$")>]
    member _.NothingRewritten() =
        ran () |> ignore

        let changed =
            [ for KeyValue(f, before) in p.Times do
                  if not (File.Exists f) || File.GetLastWriteTimeUtc f <> before then Path.GetFileName f ]

        if not changed.IsEmpty then
            fail $"rewritten: {changed}"

/// The run's builds go when it ends, so a run leaves no folder behind.
[<Binding>]
type SeamRun() =
    [<AfterTestRun>]
    static member Clean() =
        if Directory.Exists Compilers.work && Path.GetFileName(Compilers.work).StartsWith "plasm-seam-specs-" then
            Directory.Delete(Compilers.work, true)
