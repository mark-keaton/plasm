/// Copies of the koans (as shipped, answered, or with chosen mistakes), built
/// and run the way a learner would run them, against one shared server.
module Plasm.Koans.Specs.Workspace

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Testcontainers.PostgreSql

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let koansDir = Path.Combine(root, "koans", "Plasm.Koans")
let answersDir = Path.Combine(root, "koans", "answers")
let lessonSpecsDir = Path.Combine(root, "koans", "features", "lessons")
let workDir = Path.Combine(root, "koans", ".work")

// ---------------------------------------------------------------------------
// Lesson sources, koan regions, blanks and reference answers
// ---------------------------------------------------------------------------

let private blank = Regex(@"(?<![\w.])__(?!\w)")
let private koanStart = Regex(@"Koan\.(Sync|Task)\(")
let private koanName = Regex("^\\s*\"(.*)\",\\s*$")

/// Lesson source files, in order (Lessons/L01_*.fs ...).
let lessonFiles () =
    Directory.GetFiles(Path.Combine(koansDir, "Lessons"), "L*.fs") |> Array.sort |> List.ofArray

/// The koans of a lesson file: (name, first line index, last line index).
let regions (lines: string[]) =
    let starts =
        [ for i in 0 .. lines.Length - 2 do
              if koanStart.IsMatch lines.[i] then
                  let m = koanName.Match lines.[i + 1]

                  if m.Success then
                      yield m.Groups.[1].Value, i ]

    starts
    |> List.mapi (fun n (name, start) ->
        let last =
            match List.tryItem (n + 1) starts with
            | Some(_, next) -> next - 1
            | None -> lines.Length - 1

        name, start, last)

/// Blanks in a region, in order: (line index, column).
let blanksIn (lines: string[]) (first: int) (last: int) =
    [ for i in first..last do
          let text = lines.[i]
          let code = match text.IndexOf "//" with | -1 -> text.Length | c -> c

          for m in blank.Matches text do
              if m.Index < code then
                  yield i, m.Index ]

/// Reference answers by koan, in the order of the koan's blanks.
let answers () : Map<string, string list> =
    Directory.GetFiles(answersDir, "*.answers")
    |> Array.collect (fun f ->
        let mutable current = None
        let found = Collections.Generic.Dictionary<string, ResizeArray<string>>()

        for line in File.ReadAllLines f do
            if line.StartsWith "# " then
                current <- Some(line.Substring 2)
                found.[line.Substring 2] <- ResizeArray()
            elif line.Trim() <> "" then
                match current with
                | Some k -> found.[k].Add line
                | None -> ()

        found |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Array.ofSeq)
    |> Map.ofArray

/// Fills the blanks of `koan` with `values` (in order); the counts must agree.
let fill (lines: string[]) (koan: string) (values: string list) =
    match regions lines |> List.tryFind (fun (n, _, _) -> n = koan) with
    | None -> lines
    | Some(_, first, last) ->
        let spots = blanksIn lines first last

        if spots.Length <> values.Length then
            failwithf "koan '%s' has %d blanks but %d reference answers" koan spots.Length values.Length

        let copy = Array.copy lines

        // Right to left, so earlier columns stay valid.
        for (i, col), value in List.zip spots values |> List.rev do
            copy.[i] <- copy.[i].Substring(0, col) + "(" + value + ")" + copy.[i].Substring(col + 2)

        copy

/// Every koan in source order: (lesson file, koan name).
let koansInSource () =
    lessonFiles ()
    |> List.collect (fun f -> regions (File.ReadAllLines f) |> List.map (fun (n, _, _) -> f, n))

// ---------------------------------------------------------------------------
// Variants: copies of the koans with chosen answers, built once per run
// ---------------------------------------------------------------------------

/// How a variant answers each koan: None leaves its blanks.
type Answering = string -> string list option

let private gate = new SemaphoreSlim(1, 1)
let private builds = ConcurrentDictionary<string, Lazy<Task<string>>>()

let private run (file: string) (args: string) (env: (string * string) list) (cwd: string) =
    task {
        let psi = ProcessStartInfo(file, args, WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true)

        for k, v in env do
            if isNull v then psi.Environment.Remove k |> ignore else psi.Environment.[k] <- v

        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        do! p.WaitForExitAsync()
        let! o = out
        let! e = err
        return p.ExitCode, o + e
    }

let private copyProject (target: string) (answering: Answering) =
    if Directory.Exists target then Directory.Delete(target, true)
    Directory.CreateDirectory(Path.Combine(target, "Lessons")) |> ignore

    for f in Directory.GetFiles koansDir do
        File.Copy(f, Path.Combine(target, Path.GetFileName f))

    for f in lessonFiles () do
        let lines = File.ReadAllLines f

        let filled =
            regions lines
            |> List.fold
                (fun (acc: string[]) (name, _, _) ->
                    match answering name with
                    | Some values -> fill acc name values
                    | None -> acc)
                lines

        File.WriteAllLines(Path.Combine(target, "Lessons", Path.GetFileName f), filled)

/// Builds (once per test run) a copy of the koans answered by `answering`;
/// returns the path of its program, or fails with the build output.
let variant (name: string) (answering: Answering) : Task<string> =
    builds
        .GetOrAdd(
            name,
            fun _ ->
                lazy
                    (task {
                        do! gate.WaitAsync()

                        try
                            let dir = Path.Combine(workDir, name, "Plasm.Koans")
                            copyProject dir answering
                            let! code, output = run "dotnet" "build -nologo -v q" [] dir

                            if code <> 0 then
                                failwithf "the %s koans did not build:\n%s" name output

                            return Path.Combine(dir, "bin", "Debug", "net11.0", "Plasm.Koans.dll")
                        finally
                            gate.Release() |> ignore
                    })
        )
        .Value

let shipped () = variant "shipped" (fun _ -> None)

let answered () =
    let a = answers ()
    variant "answered" (fun koan -> a.TryFind koan)

// ---------------------------------------------------------------------------
// The shared server and running a variant
// ---------------------------------------------------------------------------

let private server =
    lazy
        (task {
            match Environment.GetEnvironmentVariable "PLASM_TEST_PG" with
            | null
            | "" ->
                let c =
                    PostgreSqlBuilder("postgres:18.4")
                        .WithLogger(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                        .Build()

                do! c.StartAsync()
                return c.GetConnectionString()
            | cs -> return cs
        })

let serverConnectionString () = server.Force()

type RunResult =
    { ExitCode: int
      Output: string
      Report: JsonElement list }

/// Runs a built variant. With `useServer`, PLASM_KOANS_PG names the shared
/// server; otherwise the koans start their own throwaway container.
let runKoans (program: string) (args: string) (useServer: bool) =
    task {
        let! cs = if useServer then serverConnectionString () else Task.FromResult null
        let report = Path.GetTempFileName()
        let! code, output = run "dotnet" $"\"{program}\" {args} --report \"{report}\"" [ "PLASM_KOANS_PG", cs ] (Path.GetDirectoryName program)

        let rows =
            if File.Exists report && FileInfo(report).Length > 0L then
                use doc = JsonDocument.Parse(File.ReadAllText report)
                doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.Clone()) |> List.ofSeq
            else
                []

        File.Delete report

        return
            { ExitCode = code
              Output = output
              Report = rows }
    }

let private reports = ConcurrentDictionary<string, Lazy<Task<RunResult>>>()

/// Every koan of a variant, run once per test run (`--all`).
let fullReport (name: string) (program: unit -> Task<string>) =
    reports
        .GetOrAdd(
            name,
            fun _ ->
                lazy
                    (task {
                        let! p = program ()
                        return! runKoans p "--all" true
                    })
        )
        .Value

let field (name: string) (e: JsonElement) = e.GetProperty(name).ToString()

// ---------------------------------------------------------------------------
// Lesson specifications
// ---------------------------------------------------------------------------

type KoanSpec =
    { Lesson: string
      Number: int
      Koan: string
      Discovers: string
      Teaches: string
      /// One sentence pointing at where the answer comes from; "" for none.
      Hint: string }

// koan | the learner discovers | teaches | hint (the hint's cell may be empty).
let private specRow = Regex(@"^\s*\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?:(?<!\\)\|\s*(.*?)\s*)?\|\s*$")

/// Every koan named in the lesson feature files, in lesson order.
let koanSpecs () =
    Directory.GetFiles(lessonSpecsDir, "*.feature")
    |> Array.sort
    |> Array.collect (fun f ->
        let lines = File.ReadAllLines f
        let title = lines |> Array.pick (fun l -> let t = l.Trim() in if t.StartsWith "Feature:" then Some(t.Substring(8).Trim()) else None)
        let number = int ((Path.GetFileName f).Substring(0, 2))

        lines
        |> Array.choose (fun l ->
            let m = specRow.Match l

            if m.Success && m.Groups.[1].Value <> "koan" then
                Some
                    { Lesson = title
                      Number = number
                      Koan = m.Groups.[1].Value
                      Discovers = m.Groups.[2].Value.Replace("\\|", "|")
                      Teaches = m.Groups.[3].Value
                      Hint = m.Groups.[4].Value.Replace("\\|", "|") }
            else
                None))
    |> List.ofArray

/// The koans that have no hint yet, each with what its page lacks
/// (koans/no-hint-yet.txt: `koan name  # why`).
let noHintYet () : Map<string, string> =
    let file = Path.Combine(root, "koans", "no-hint-yet.txt")

    if not (File.Exists file) then
        Map.empty
    else
        File.ReadAllLines file
        |> Array.filter (fun l -> l.Trim() <> "" && not (l.TrimStart().StartsWith "#"))
        |> Array.map (fun l ->
            match l.IndexOf '#' with
            | -1 -> l.Trim(), ""
            | i -> l.Substring(0, i).Trim(), l.Substring(i + 1).Trim())
        |> Map.ofArray

/// The orientation lesson, which teaches the F# the course relies on: its
/// koans name an F# topic ("F#: Records") instead of a Plasm scenario.
let orientationLesson = 1

/// Whether `teaches` names an F# topic: "F#: " and the topic.
let isFSharpTopic (teaches: string) = Regex.IsMatch(teaches, @"^F#: \S.*$")

/// Plasm's implemented acceptance scenarios, as "feature: scenario".
let implementedScenarios () =
    Directory.GetFiles(Path.Combine(root, "features"), "*.feature")
    |> Array.filter (fun f -> File.ReadAllLines f |> Array.exists (fun l -> l.Trim() = "@implemented"))
    |> Array.collect (fun f ->
        let feature = Path.GetFileNameWithoutExtension f

        File.ReadAllLines f
        |> Array.choose (fun l ->
            let m = Regex.Match(l, @"^\s*Scenario( Outline)?:\s*(.+?)\s*$")
            if m.Success then Some $"{feature}: {m.Groups.[2].Value}" else None))
    |> Set.ofArray
