/// Runs koans in sandboxes and reports the path to enlightenment.
module Plasm.Koans.Runner

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Plasm

type Outcome =
    | Passed
    /// A blank (__) is still to be filled in, at this file and line.
    | Unanswered of file: string * line: int
    | Failed of expected: string * actual: string
    | Threw of exceptionType: string * message: string

type KoanResult =
    { Lesson: Lesson
      Koan: Koan
      Outcome: Outcome
      /// The SQL the koan sent or explained, in order.
      Sql: string list }

let private blank = Regex(@"(?<![\w.])__(?!\w)")

/// The first blank in the koan's source (from its line to the next koan's).
let blankOf (lesson: Lesson) (koan: Koan) : (string * int) option =
    if not (File.Exists koan.File) then
        None
    else
        let lines = File.ReadAllLines koan.File

        let next =
            lesson.Koans
            |> List.map (fun k -> k.Line)
            |> List.filter (fun l -> l > koan.Line)
            |> List.sort
            |> List.tryHead
            |> Option.defaultValue (lines.Length + 1)

        seq { koan.Line .. next - 1 }
        |> Seq.tryFind (fun n ->
            let text = lines.[n - 1]
            let code = match text.IndexOf "//" with | -1 -> text | i -> text.Substring(0, i)
            blank.IsMatch code)
        |> Option.map (fun n -> koan.File, n)

let rec private innermost (ex: exn) =
    match ex with
    | :? AggregateException as a when a.InnerExceptions.Count = 1 -> innermost a.InnerException
    | _ -> ex

/// Runs one koan inside a sandbox of `repo`.
let run (repo: Repo) (commands: ResizeArray<CommandEvent>) (lesson: Lesson) (koan: Koan) : Task<KoanResult> =
    task {
        commands.Clear()
        let explained = ResizeArray<string>()

        let! outcome =
            task {
                try
                    do! repo.Sandbox(fun sandbox -> koan.Body(KoanContext(sandbox, (fun () -> List.ofSeq commands), explained)))
                    return Passed
                with ex ->
                    match innermost ex with
                    | BlankException -> return Unanswered(koan.File, koan.Line)
                    | KoanFailure(expected, actual) -> return Failed(expected, actual)
                    | inner -> return Threw(inner.GetType().FullName, inner.Message)
            }

        // A koan with a blank left is unanswered, whatever else happened.
        let outcome =
            match outcome, blankOf lesson koan with
            | Passed, _ -> Passed
            | _, Some(file, line) -> Unanswered(file, line)
            | Unanswered _, None -> Failed("an answer", "a blank (__) that was evaluated")
            | other, None -> other

        let sql =
            (commands |> Seq.map (fun c -> c.Sql) |> List.ofSeq) @ List.ofSeq explained
            // The sandbox's own savepoint commands are not the koan's.
            |> List.filter (fun s -> not (s.StartsWith "SAVEPOINT" || s.StartsWith "RELEASE" || s.StartsWith "ROLLBACK"))

        return
            { Lesson = lesson
              Koan = koan
              Outcome = outcome
              Sql = sql }
    }

// ---------------------------------------------------------------------------
// Reporting
// ---------------------------------------------------------------------------

let private sqlKeywords =
    [ " FROM "; " INNER JOIN "; " LEFT JOIN "; " WHERE "; " GROUP BY "; " HAVING "; " ORDER BY "; " LIMIT "; " OFFSET "; " RETURNING "; " SET "; " VALUES " ]

/// SQL broken before its clauses; other text unchanged.
let formatSql (text: string) =
    let unquoted = text.Trim('"')

    if Regex.IsMatch(unquoted, @"^\s*(SELECT|INSERT|UPDATE|DELETE)\b") then
        sqlKeywords
        |> List.fold (fun (s: string) k -> s.Replace(k, "\n" + k.TrimStart())) unquoted
    else
        text

let private indent (prefix: string) (text: string) =
    text.Split('\n') |> Array.map (fun l -> prefix + l) |> String.concat "\n"

let relative (file: string) =
    match file.IndexOf "Lessons" with
    | -1 -> file
    | i -> file.Substring(i).Replace('\\', '/')

let guidanceFor (guidance: Guidance.LessonGuidance list) (r: KoanResult) =
    guidance
    |> List.tryFind (fun g -> g.Title = r.Lesson.Title)
    |> Option.bind (fun g -> g.Koans.TryFind r.Koan.Name)

/// The report for the koan the learner should meditate on next.
let describeNext (guidance: Guidance.LessonGuidance list) (r: KoanResult) =
    let lines = ResizeArray<string>()
    lines.Add $"The koan \"{r.Koan.Name}\" in \"{r.Lesson.Title}\" has damaged your karma."
    lines.Add ""

    match r.Outcome with
    | Unanswered(file, line) -> lines.Add $"  Fill in the blank (__) at {relative file}:{line}"
    | Failed(expected, actual) ->
        lines.Add $"  At {relative r.Koan.File}:{r.Koan.Line}"
        lines.Add "  Expected:"
        lines.Add(indent "    " (formatSql expected))
        lines.Add "  Actual:"
        lines.Add(indent "    " (formatSql actual))
    | Threw(t, m) ->
        lines.Add $"  At {relative r.Koan.File}:{r.Koan.Line}"
        lines.Add $"  It threw {t}:"
        lines.Add(indent "    " m)
    | Passed -> ()

    match r.Outcome, r.Sql with
    | (Failed _ | Threw _), sql when not sql.IsEmpty ->
        lines.Add ""
        lines.Add "  Plasm generated:"

        for s in sql do
            lines.Add(indent "    " (formatSql s))
    | _ -> ()

    match guidanceFor guidance r with
    | Some g ->
        lines.Add ""
        lines.Add $"  Meditate on this: {g.Discovers}"
    | None -> ()

    String.concat "\n" lines

let progressBar (passed: int) (total: int) =
    let width = 30
    let filled = if total = 0 then 0 else passed * width / total
    $"""[{String('#', filled)}{String('.', width - filled)}] {passed} of {total}"""
