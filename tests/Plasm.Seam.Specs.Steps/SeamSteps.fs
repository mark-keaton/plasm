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

    let writeAs (ci: bool) (http: bool) (ns: string) =
        let options: Program.Options =
            { Config = p.Config
              Out = p.Out
              Lock = p.Lock
              Namespace = ns
              References = Compilers.references () @ [ data () ] |> Array.ofList
              Data = [| data () |]
              Fail = ci
              Http = http }

        p.Ran <-
            Some(
                try
                    let ran = Program.run options

                    // Every lock is read back by the format and written again unchanged.
                    let text = File.ReadAllText p.Lock

                    match LockFile.parse text with
                    | Ok _ when ran.Lock = Lock.Same -> () // left as it was: a scenario may have changed it
                    | Ok lock when LockFile.write lock = text -> ()
                    | Ok lock -> fail $"seam.lock, read and written again, is not the same:\n{LockFile.write lock}\nwhere it was\n{text}"
                    | Error wrong -> fail $"the format does not read the lock it wrote: %A{wrong}"

                    Ok ran
                with
                | SeamError(code, text, origin) -> Error(code, text, origin)
                | ContractChanged(lock, changes) ->
                    Error("SEAM020", Program.contractChanged lock changes + "\n" + String.concat "\n" changes, Some lock)
            )

    let write = writeAs false false

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

    [<StepDefinition("^the seam is written into the namespace ([\\w.]+), as a CI build writes it$")>]
    member _.WrittenInCi(ns: string) = writeAs true false ns

    [<StepDefinition("^the seam is written into the namespace ([\\w.]+), as a web project$")>]
    member _.WrittenWeb(ns: string) = writeAs false true ns

    [<StepDefinition("^the seam wrote seam\\.lock$")>]
    member _.WroteLock() =
        match (ran ()).Lock with
        | Lock.Wrote -> ()
        | other -> fail $"the seam did not write seam.lock: %A{other}"

    [<StepDefinition("^the seam rewrote seam\\.lock, saying \"(.*)\"$")>]
    member _.RewroteLock(change: string) =
        match (ran ()).Lock with
        | Lock.Rewrote changes when List.contains (unquote change) changes -> ()
        | other -> fail $"the seam did not say {change}: %A{other}"

    [<StepDefinition("^the seam wrote its files, and left seam\\.lock as it was$")>]
    member _.LockSame() =
        match (ran ()).Lock with
        | Lock.Same -> ()
        | other -> fail $"seam.lock was not left as it was: %A{other}"

    [<StepDefinition("^it names the change \"(.*)\"$")>]
    member _.NamesChange(change: string) =
        match p.Ran with
        | Some(Error(_, text, _)) when text.Split('\n') |> Array.contains change -> ()
        | other -> fail $"the change {change} is not named: %A{other}"

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

    [<StepDefinition("^(\\w+\\.g\\.cs) does not declare \"(.*)\"$")>]
    member _.DoesNotDeclare(name: string, text: string) =
        let source = file name

        if (squeeze source).Contains(squeeze (unquote text)) then
            fail $"{name} declares\n  {unquote text}\nIt reads:\n{source}"

    [<StepDefinition("^seam\\.lock gains the comment \"(.*)\"$")>]
    member _.GainsComment(comment: string) =
        ran () |> ignore
        File.AppendAllText(p.Lock, $"\n# {comment}\n")

    [<StepDefinition("^seam\\.lock is written as it was before versions$")>]
    member _.FormatZero() =
        ran () |> ignore
        let lines = File.ReadAllLines p.Lock |> Array.filter (fun l -> not (l.StartsWith "seam.lock "))
        File.WriteAllText(p.Lock, String.Join("\n", lines) + "\n")

    [<StepDefinition("^seam\\.lock's version is (\\d+)$")>]
    member _.Version(v: int) =
        ran () |> ignore
        let lines = File.ReadAllLines p.Lock |> Array.map (fun l -> if l.StartsWith "seam.lock " then $"seam.lock {v}" else l)
        File.WriteAllText(p.Lock, String.Join("\n", lines) + "\n")

    [<StepDefinition("^seam\\.lock gains the line \"(.*)\" after \"(.*)\"$")>]
    member _.GainsLine(line: string, after: string) =
        ran () |> ignore
        let lines = File.ReadAllLines p.Lock |> List.ofArray
        let i = lines |> List.findIndex ((=) after)
        File.WriteAllText(p.Lock, String.Join("\n", List.insertAt (i + 1) line lines) + "\n")

    [<StepDefinition("^each example in docs/seam-lock\\.md is read as a lock, each refused one is refused, and each of its productions has an example$")>]
    member _.Grammar() =
        let rec up (d: DirectoryInfo) = if File.Exists(Path.Combine(d.FullName, "Plasm.slnx")) then d.FullName else up d.Parent
        let doc = File.ReadAllText(Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "docs", "seam-lock.md"))
        let blocks = Text.RegularExpressions.Regex.Matches(doc, "(?m)^ *```(example|refused)([^\n]*)\n((?:.*\n)*?) *```")
        let ebnf = Text.RegularExpressions.Regex.Match(doc, "(?s)```ebnf\n(.*?)```").Groups[1].Value
        let productions = Text.RegularExpressions.Regex.Matches(ebnf, @"(?m)^([a-z][a-z-]*)\s*=") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
        let shown = Collections.Generic.HashSet<string>()
        let problems = Collections.Generic.List<string>()

        for b in blocks do
            let indent = b.Value.Length - b.Value.TrimStart(' ').Length
            let lines = b.Groups[3].Value.Split('\n') |> Array.map (fun l -> if l.Length >= indent then l.Substring indent else l.TrimStart())
            let body = String.Join("\n", lines)
            let whole =
                (if body.Contains "seam.lock " then "" else "seam.lock 1\n") + body + (if body.Contains "json SeamJson" then "" else "json SeamJson\n")

            match b.Groups[1].Value, LockFile.parse whole with
            | "example", Ok _ -> for t in b.Groups[2].Value.Trim().Split(' ') do shown.Add t |> ignore
            | "example", Error wrong -> problems.Add $"an example is not read: %A{wrong}\n{whole}"
            | _, Ok _ -> problems.Add $"a refused example is read:\n{whole}"
            | _, Error _ -> ()

        for p in productions do
            if not (shown.Contains p) then problems.Add $"the production {p} has no example"

        if problems.Count > 0 then fail (String.concat "\n" problems)

    [<StepDefinition("^seam\\.lock holds$")>]
    member _.LockHolds(text: string) =
        // Read whether the seam wrote its files or stopped: a stopped build leaves it as it was.
        if p.Ran.IsNone then fail "the seam was not written"
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
