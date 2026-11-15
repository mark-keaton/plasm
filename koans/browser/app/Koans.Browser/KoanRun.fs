/// Runs one koan in the browser: compiles its code, then runs it in a sandbox
/// over PGlite, exactly as the terminal runner does over PostgreSQL.
module Plasm.Koans.Browser.KoanRun

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Plasm
open Plasm.Koans
open Plasm.Koans.Runner
open Plasm.PGlite

let private commands = ResizeArray<CommandEvent>()
let mutable private repo: Repo option = None

/// Creates the koans database in PGlite and the repository over it.
let init (bridge: IPGliteBridge) : Task<string> =
    task {
        for sql in [ KoansData.schema; KoansData.data ] do
            match bridge.Execute sql with
            | "" -> ()
            | error -> failwithf "setting up the koans database failed: %s" error

        let! provider = PGlite.connect bridge

        repo <-
            Some(
                Repo(
                    Shop.Db.model,
                    provider,
                    { RepoOptions.Default with
                        OnCommand = Some commands.Add
                        IncludeParameterValues = true }
                )
            )

        return "ready"
    }

/// The source compiled for one koan: its lesson's helpers, then the koan's
/// code inside the frame every koan shares, `fun repo -> task { ... }` (or
/// `fun repo -> ...` when the koan is not framed). The page shows only the code;
/// its lines start at `prefixLines` + 1, indented by `indent`.
let source (helpers: string) (isTask: bool) (framed: bool) (body: string) =
    let indent = if framed then 8 else 4

    let head =
        String.concat
            "\n"
            [ "module Koan"
              "open Plasm"
              "open Plasm.Koans"
              "open Plasm.Koans.Shop"
              helpers
              // Boxed, so it compiles to a property (a function-typed `let`
              // would compile to a method).
              "let koanBody : obj = box ((fun (repo: Repo) ->"
              if framed then "    task {" ]

    let code =
        body.Split('\n') |> Array.map (fun l -> if l = "" then l else String(' ', indent) + l) |> String.concat "\n"

    let close = if framed then "\n    }" else ""
    let signature = if isTask then "Repo -> System.Threading.Tasks.Task<unit>" else "Repo -> unit"
    head + "\n" + code + close + $") : {signature})", head.Split('\n').Length, indent

let private json (fields: (string * obj) list) = JsonSerializer.Serialize(dict fields)

/// Compiles and runs a koan; returns JSON: {status, text, errors?, sql?}.
/// Compile errors are reported with lines relative to the koan's body.
let run (refsDir: string) (title: string) (name: string) (helpers: string) (isTask: bool) (framed: bool) (body: string) : Task<string> =
    task {
        let src, prefix, indent = source helpers isTask framed body

        match! Compiler.compile refsDir src with
        | Error errors ->
            let shown =
                errors
                |> List.map (fun e -> dict [ "line", box (e.Line - prefix); "column", box (max 1 (e.Column - indent)); "code", box e.Code; "message", box e.Message ])
                |> Array.ofList

            return json [ "status", box "compile"; "errors", box shown ]
        | Ok assembly ->
            let value = assembly.GetType("Koan").GetProperty("koanBody").GetValue(null)

            let compiledBody: Repo -> Task<unit> =
                if isTask then
                    value :?> (Repo -> Task<unit>)
                else
                    let sync = value :?> (Repo -> unit)

                    fun repo ->
                        sync repo
                        Task.FromResult()

            let koan =
                { Name = name
                  File = ""
                  Line = 0
                  Body = compiledBody }

            let! r = Runner.run repo.Value commands (Lesson.make title [ koan ]) koan

            let status =
                match r.Outcome with
                | Passed -> "passed"
                | Unanswered _ -> "unanswered"
                | Failed _ -> "failed"
                | Threw _ -> "threw"

            let text = if r.Outcome = Passed then "" else describeNext [] r

            // The first blank still in the koan's code (1-based, within the body).
            let blankLine =
                body.Split('\n')
                |> Array.tryFindIndex (fun l ->
                    let code = match l.IndexOf "//" with | -1 -> l | i -> l.Substring(0, i)
                    Text.RegularExpressions.Regex.IsMatch(code, @"(?<![\w.])__(?!\w)"))
                |> Option.map ((+) 1)
                |> Option.defaultValue 0

            // The parts of the result, for the page to lay out.
            let expected, actual, threw =
                match r.Outcome with
                | Failed(e, a) -> Runner.formatSql e, Runner.formatSql a, null
                | Threw(t, m) -> null, null, $"{t}: {m}"
                | _ -> null, null, null

            let shownSql =
                match r.Outcome with
                | Failed _ | Threw _ -> r.Sql |> List.map (fun s -> (Runner.formatSql s).Replace("\n", "\n    ")) |> Array.ofList
                | _ -> [||]

            return
                json
                    [ "status", box status
                      "text", box text
                      "blankLine", box blankLine
                      "sql", box (Array.ofList r.Sql)
                      "expected", box expected
                      "actual", box actual
                      "threw", box threw
                      "shownSql", box shownSql ]
    }

/// For checks: compiles `code` (the body of `fun (repo: Repo) -> Task<string>`,
/// indented by four), runs it (in a sandbox when `sandboxed`) and returns
/// JSON {result, sql} or {error}.
let evaluate (refsDir: string) (code: string) (sandboxed: bool) : Task<string> =
    task {
        let src =
            String.concat
                "\n"
                [ "module Koan"
                  "open System"
                  "open System.Threading.Tasks"
                  "open Plasm"
                  "open Plasm.Koans"
                  "open Plasm.Koans.Shop"
                  "let evaluate (repo: Repo) : Task<string> ="
                  code ]

        match! Compiler.compile refsDir src with
        | Error errors -> return json [ "error", box (errors |> List.map (fun e -> $"({e.Line},{e.Column}) {e.Code} {e.Message}") |> String.concat "\n") ]
        | Ok assembly ->
            let f = assembly.GetType("Koan").GetMethod("evaluate")
            let call (r: Repo) = f.Invoke(null, [| box r |]) :?> Task<string>
            commands.Clear()

            try
                let! result = if sandboxed then repo.Value.Sandbox(fun r -> call r) else call repo.Value

                let sql =
                    commands
                    |> Seq.map (fun c -> c.Sql)
                    |> Seq.filter (fun s -> not (s.StartsWith "SAVEPOINT" || s.StartsWith "RELEASE" || s.StartsWith "ROLLBACK"))
                    |> Array.ofSeq

                return json [ "result", box result; "sql", box sql ]
            with ex ->
                return json [ "error", box (string ex) ]
    }

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices

/// Type-checks a koan's code with the F# compiler's checker, for the editor:
/// the results, with the cursor at `offset` as a line (1-based), that line's
/// text and a column (0-based) in the compiled source.
let private checkAt (refsDir: string) (helpers: string) (isTask: bool) (framed: bool) (body: string) (offset: int) =
    task {
        let src, prefix, indent = source helpers isTask framed body
        let before = body.Substring(0, min offset body.Length)
        let line = prefix + before.Split('\n').Length
        let lineText = src.Split('\n').[line - 1]
        let column = before.Length - (before.LastIndexOf '\n' + 1) + indent

        let file = IO.Path.Combine(IO.Path.GetTempPath(), "koan", "editor.fs")
        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName file) |> ignore
        let refs = IO.Directory.GetFiles(refsDir, "*.dll") |> Array.map (fun r -> "-r:" + r)
        let checker = Compiler.checker.Value

        let options =
            { checker.GetProjectOptionsFromCommandLineArgs("koan.fsproj", Array.append [| "--noframework"; "--target:library" |] refs) with
                SourceFiles = [| file |] }

        let! parsed, answer = checker.ParseAndCheckFileInProject(file, 0, FSharp.Compiler.Text.SourceText.ofString src, options) |> Async.StartAsTask

        return
            match answer with
            | FSharpCheckFileAnswer.Succeeded check -> Some(parsed, check, line, lineText, column, prefix, indent)
            | _ -> None
    }

/// The text of an XML doc comment's summary.
let private summaryOf (xml: string) =
    let m = Text.RegularExpressions.Regex.Match(xml, "<summary>(.*?)</summary>", Text.RegularExpressions.RegexOptions.Singleline)
    let text = if m.Success then m.Groups.[1].Value else xml
    let text = Text.RegularExpressions.Regex.Replace(text, "<see cref=\"\\w:([^\"]*)\"\\s*/>", "$1")
    let text = Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "")
    Net.WebUtility.HtmlDecode(Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim())

/// A doc comment's summary from a reference assembly's XML file in `refsDir`.
let private docIn (refsDir: string) (assembly: string) (signature: string) =
    let file = IO.Path.Combine(refsDir, assembly + ".xml")

    if IO.File.Exists file then
        let m = Text.RegularExpressions.Regex.Match(IO.File.ReadAllText file, "<member name=\"" + Text.RegularExpressions.Regex.Escape signature + "\">(.*?)</member>", Text.RegularExpressions.RegexOptions.Singleline)
        if m.Success then summaryOf m.Groups.[1].Value else ""
    else
        ""

/// The documentation of something the compiler describes: its own XML text,
/// or its entry in the reference assembly's XML file in `refsDir`.
let private docOf (refsDir: string) (doc: FSharp.Compiler.Symbols.FSharpXmlDoc) =
    match doc with
    | FSharp.Compiler.Symbols.FSharpXmlDoc.FromXmlText x -> summaryOf (String.concat "\n" x.UnprocessedLines)
    | FSharp.Compiler.Symbols.FSharpXmlDoc.FromXmlFile(dll, signature) -> docIn refsDir (IO.Path.GetFileNameWithoutExtension dll) signature
    | _ -> ""

/// What can follow the cursor at `offset` in a koan's code, from the F#
/// compiler's own checker: JSON [{name, kind, detail}].
let complete (refsDir: string) (helpers: string) (isTask: bool) (framed: bool) (body: string) (offset: int) : Task<string> =
    task {
        match! checkAt refsDir helpers isTask framed body offset with
        | Some(parsed, check, line, lineText, column, _, _) ->
            let partial = QuickParse.GetPartialLongNameEx(lineText, column - 1)
            let list = check.GetDeclarationListInfo(Some parsed, line, lineText, partial)

            let detail (ToolTipText elements) =
                elements
                |> List.tryPick (function
                    | ToolTipElement.Group(first :: _) -> Some first.MainDescription.Text
                    | _ -> None)
                |> Option.defaultValue ""

            // Every object's members (Equals, GetHashCode, ...) are left out, and
            // a description is kept short: its type, without the member's name
            // again, the optional cancellation token or namespaces.
            let everyObject = set [ "Equals"; "GetHashCode"; "GetType"; "ToString"; "ReferenceEquals" ]

            let short (name: string) (text: string) =
                let text = Text.RegularExpressions.Regex.Replace(text, @"^(member|val|property|method|abstract member|static member|union case|type)\s+([\w.`']*\.)?" + Text.RegularExpressions.Regex.Escape name + @"\s*:\s*", "")
                let text = Text.RegularExpressions.Regex.Replace(text, @"\s*\*\s*\?cancellationToken:\s*System\.Threading\.CancellationToken", "")
                let text = Text.RegularExpressions.Regex.Replace(text, @"\?cancellationToken:\s*System\.Threading\.CancellationToken\s*->\s*", "unit -> ")
                text.Replace("System.Threading.Tasks.", "").Replace("System.Threading.", "").Replace("System.Collections.Generic.", "")

            let items =
                list.Items
                |> Array.filter (fun i -> not (everyObject.Contains i.NameInList))
                |> Array.map (fun i -> dict [ "name", box i.NameInList; "kind", box (string i.Glyph); "detail", box (short i.NameInList (detail i.Description)) ])

            return JsonSerializer.Serialize items
        | None -> return "[]"
    }

/// What the name at `offset` in a koan's code is: JSON {name, signature, doc},
/// or {} when there is nothing there.
let hover (refsDir: string) (helpers: string) (isTask: bool) (framed: bool) (body: string) (offset: int) : Task<string> =
    task {
        match! checkAt refsDir helpers isTask framed body offset with
        | Some(_, check, line, lineText, column, _, _) ->
            match QuickParse.GetCompleteIdentifierIsland true lineText column with
            | Some(ident, endColumn, _) ->
                let names = ident.Split('.') |> List.ofArray
                let (ToolTipText elements) = check.GetToolTip(line, endColumn, lineText, names, FSharp.Compiler.Tokenization.FSharpTokenTag.Identifier)

                // A value with no doc of its own (such as repo) is explained by its type's doc.
                let typeDoc () =
                    match check.GetSymbolUseAtLocation(line, endColumn, lineText, names) with
                    | Some u ->
                        match u.Symbol with
                        | :? FSharp.Compiler.Symbols.FSharpMemberOrFunctionOrValue as v when v.FullType.HasTypeDefinition ->
                            let t = v.FullType.TypeDefinition

                            match docOf refsDir t.XmlDoc with
                            | "" -> docIn refsDir t.Assembly.SimpleName t.XmlDocSig
                            | d -> d
                        | _ -> ""
                    | None -> ""

                match elements |> List.tryPick (function ToolTipElement.Group(first :: _) -> Some first | _ -> None) with
                | Some item ->
                    let doc = match docOf refsDir item.XmlDoc with "" -> typeDoc () | d -> d

                    return
                        JsonSerializer.Serialize(
                            dict [ "name", box (List.last names); "signature", box item.MainDescription.Text; "doc", box doc ]
                        )
                | None -> return "{}"
            | None -> return "{}"
        | None -> return "{}"
    }

/// The compiler's errors and warnings in a koan's code, as offsets in that
/// code: JSON [{from, to, severity, message}]. A mistake outside the code the
/// page shows (in the frame around it) is put at the code's end.
let diagnose (refsDir: string) (helpers: string) (isTask: bool) (framed: bool) (body: string) : Task<string> =
    task {
        match! checkAt refsDir helpers isTask framed body 0 with
        | Some(_, check, _, _, _, prefix, indent) ->
            let lines = body.Split('\n')
            let starts = lines |> Array.scan (fun at (l: string) -> at + l.Length + 1) 0

            let offset (line: int) (column: int) =
                let n = line - prefix // 1-based line of the body

                if n < 1 || n > lines.Length then
                    body.Length
                else
                    starts.[n - 1] + (max 0 (column - (if lines.[n - 1] = "" then 0 else indent)) |> min lines.[n - 1].Length)

            let shown =
                check.Diagnostics
                // Only mistakes in the koan's own code, not about the compiler's options.
                |> Array.filter (fun d -> d.FileName.EndsWith "editor.fs")
                |> Array.filter (fun d ->
                    d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error
                    || d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning)
                |> Array.map (fun d ->
                    let from = offset d.StartLine d.StartColumn
                    let until = max from (offset d.EndLine d.EndColumn)

                    dict
                        [ "from", box from
                          "to", box until
                          "severity", box (if d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error then "error" else "warning")
                          "message", box d.Message ])

            return JsonSerializer.Serialize shown
        | None -> return "[]"
    }

