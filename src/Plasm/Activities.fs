/// Each command Plasm sends as an activity of the ActivitySource "Plasm"
/// (activities.feature), made from the event RepoOptions.OnCommand is given
/// and tagged by OpenTelemetry's database conventions. Nothing is made unless
/// something listens to the source.
namespace Plasm

open System
open System.Diagnostics

module internal Activities =
    let source =
        new ActivitySource("Plasm", typeof<CommandEvent>.Assembly.GetName().Version |> string)

    let listening () = source.HasListeners()

    /// The statement's operation: its first word, as SQL writes it.
    let private operation (sql: string) =
        let text = sql.TrimStart()
        let i = text.IndexOfAny [| ' '; '\n'; '\r'; '\t'; '(' |]
        (if i < 0 then text else text.Substring(0, i)).ToUpperInvariant()

    /// The table a statement reads or writes first, unquoted, without its schema.
    let private collection (sql: string) =
        let m = Text.RegularExpressions.Regex.Match(sql, "\\b(?:FROM|INTO|UPDATE)\\s+((?:\"[^\"]+\"|\\w+)(?:\\.(?:\"[^\"]+\"|\\w+))?)", Text.RegularExpressions.RegexOptions.IgnoreCase)

        if m.Success then
            Some((m.Groups.[1].Value.Split('.') |> Array.last).Trim('"'))
        else
            None

    /// The activity of `e`, which has just ended, when something listens.
    let record (dialect: ISqlDialect) (e: CommandEvent) =
        if source.HasListeners() then
            let ended = DateTimeOffset.UtcNow
            let op = operation e.Sql
            let table = if e.Kind = CommandKind.Statement then collection e.Sql else None

            let name =
                match table with
                | Some t -> $"{op} {t}"
                | None -> op

            match source.StartActivity(name, ActivityKind.Client, ActivityContext(), startTime = ended - e.Duration) with
            | null -> ()
            | a ->
                a.SetTag("db.system.name", dialect.Name.ToLowerInvariant()) |> ignore
                a.SetTag("db.operation.name", op) |> ignore
                table |> Option.iter (fun t -> a.SetTag("db.collection.name", t) |> ignore)
                a.SetTag("db.query.text", e.Sql) |> ignore
                a.SetTag("db.response.returned_rows", e.Rows) |> ignore
                a.SetTag("plasm.round_trip", e.RoundTrip) |> ignore

                if not e.Succeeded then
                    a.SetTag("error.type", defaultArg e.Error "_OTHER") |> ignore
                    a.SetStatus(ActivityStatusCode.Error) |> ignore

                a.SetEndTime(ended.UtcDateTime) |> ignore
                a.Stop()

    /// Where a repository's events go: its OnCommand and, while something
    /// listens, the activities. None when nobody wants them, so none is made.
    let sink (onCommand: (CommandEvent -> unit) option) (dialect: ISqlDialect) : (CommandEvent -> unit) option =
        match onCommand, listening () with
        | None, false -> None
        | Some f, false -> Some f
        | None, true -> Some(record dialect)
        | Some f, true ->
            Some(fun e ->
                f e
                record dialect e)
