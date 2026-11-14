namespace Plasm.Koans.Specs

open System
open System.IO
open System.Text.Json
open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open Reqnroll
open Plasm.Koans.Specs.Workspace

type NotebookCell =
    { Kind: string
      Source: string }

type CellRun =
    { Cell: NotebookCell
      Output: string
      Errors: string list
      Failure: string option }

module Notebooks =
    /// Console output is process-wide, so notebook runs take turns.
    let gate = obj ()

    let cells (json: string) =
        use doc = JsonDocument.Parse json

        doc.RootElement.GetProperty("cells").EnumerateArray()
        |> Seq.map (fun c ->
            { Kind = c.GetProperty("cell_type").GetString()
              Source = c.GetProperty("source").EnumerateArray() |> Seq.map (fun l -> l.GetString()) |> String.concat "" })
        |> List.ofSeq

    /// The notebook a built variant generates, referencing its own build.
    let generate (program: string) =
        task {
            let path = Path.GetTempFileName()
            let bin = Path.GetDirectoryName program
            let! r = runKoans program $"--notebook \"{path}\" --bin \"{bin}\"" true
            let json = File.ReadAllText path
            File.Delete path
            if not (r.Output.Contains "Wrote the koans notebook") then failwith r.Output
            return json
        }

    let koanName (cell: NotebookCell) =
        let m = Text.RegularExpressions.Regex.Match(cell.Source, "^koan(?:Task)? \"(.*?)\"")
        if m.Success then Some m.Groups.[1].Value else None

    /// Runs code cells one at a time in one F# Interactive session.
    let run (cells: NotebookCell list) =
        lock gate (fun () ->
            let out = new StringWriter()
            let fsiOut = new StringWriter()

            use session =
                FsiEvaluationSession.Create(
                    FsiEvaluationSession.GetDefaultConfiguration(),
                    [| "fsi"; "--noninteractive"; "--nologo" |],
                    new StringReader(""),
                    fsiOut,
                    new StringWriter(),
                    collectible = false
                )

            let original = Console.Out

            try
                Console.SetOut out

                [ for cell in cells |> List.filter (fun c -> c.Kind = "code") do
                      out.GetStringBuilder().Clear() |> ignore
                      let result, diagnostics = session.EvalInteractionNonThrowing cell.Source

                      let errors =
                          diagnostics
                          |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                          |> Array.map (fun d -> d.Message)
                          |> List.ofArray

                      yield
                          { Cell = cell
                            Output = out.ToString()
                            Errors = errors
                            Failure =
                              match result with
                              | Choice2Of2 ex when errors.IsEmpty -> Some ex.Message
                              | _ -> None } ]
            finally
                Console.SetOut original)

/// Runs notebooks in a real .NET Interactive kernel with dotnet-repl (a local
/// tool pinned in dotnet-tools.json), returning each code cell's output.
module Kernel =
    open System.Diagnostics

    let private exec (args: string) (cwd: string) =
        let psi = ProcessStartInfo("dotnet", args, WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true)
        // dotnet-repl targets .NET 10; let it run on the newer runtime the repository uses.
        psi.Environment.["DOTNET_ROLL_FORWARD"] <- "Major"
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode, out.Result + err.Result

    let run (notebookJson: string) =
        let dir = Directory.CreateTempSubdirectory("plasm-kernel").FullName
        let input = Path.Combine(dir, "koans.ipynb")
        let output = Path.Combine(dir, "out.ipynb")
        File.WriteAllText(input, notebookJson)
        let code, restoreOut = exec "tool restore" root
        if code <> 0 then failwithf "dotnet tool restore failed:\n%s" restoreOut

        let _, log =
            exec $"tool run dotnet-repl --run \"{input}\" --exit-after-run --output-format ipynb --output-path \"{output}\" --default-kernel fsharp" root

        if not (File.Exists output) then failwithf "dotnet-repl produced no notebook:\n%s" log
        use doc = JsonDocument.Parse(File.ReadAllText output)

        doc.RootElement.GetProperty("cells").EnumerateArray()
        |> Seq.filter (fun c -> c.GetProperty("cell_type").GetString() = "code")
        |> Seq.map (fun c ->
            let source = c.GetProperty("source").EnumerateArray() |> Seq.map (fun l -> l.GetString()) |> String.concat ""

            let text =
                c.GetProperty("outputs").EnumerateArray()
                |> Seq.map (fun o ->
                    let pick (name: string) =
                        match o.TryGetProperty name with
                        | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.map (fun l -> l.GetString()) |> String.concat ""
                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                        | _ -> ""

                    let data =
                        match o.TryGetProperty "data" with
                        | true, d ->
                            match d.TryGetProperty "text/plain" with
                            | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.map (fun l -> l.GetString()) |> String.concat ""
                            | true, v -> v.GetString()
                            | _ -> ""
                        | _ -> ""

                    pick "text" + data + pick "evalue")
                |> String.concat "\n"

            { Cell = { Kind = "code"; Source = source }
              Output = text
              Errors = []
              Failure = None })
        |> List.ofSeq

/// Feature: The koans as a notebook
[<Binding>]
type NotebookSteps(w: KoansWorld) =

    let mutable kernelRuns: (CellRun list * CellRun list) option = None

    let mutable notebook: NotebookCell list = []
    let mutable runs: CellRun list = []

    let koanCells () = runs |> List.filter (fun r -> (Notebooks.koanName r.Cell).IsSome)

    [<StepDefinition("the koans database server")>]
    member _.Server() =
        task {
            let! cs = serverConnectionString ()
            Environment.SetEnvironmentVariable("PLASM_KOANS_PG", cs)
        }
        |> unit

    [<StepDefinition("the notebook is generated from the koans as shipped")>]
    member _.Generate() =
        task {
            let! program = shipped ()
            let! json = Notebooks.generate program
            notebook <- Notebooks.cells json
            // The committed notebook references the build next to it, not the copy.
            let! r = runKoans program "--notebook \"generated.ipynb\"" true
            ignore r
            w.Koan <- File.ReadAllText(Path.Combine(Path.GetDirectoryName program, "generated.ipynb"))
        }
        |> unit

    [<StepDefinition("it has a heading for every lesson, in order")>]
    member _.Headings() =
        let headings =
            notebook
            |> List.filter (fun c -> c.Kind = "markdown" && c.Source.StartsWith "## ")
            |> List.map (fun c -> c.Source.Split('\n').[0].Substring(3))

        let lessons = koanSpecs () |> List.map (fun s -> s.Lesson) |> List.distinct
        check (headings = lessons) $"headings %A{headings}\nlessons %A{lessons}"

    [<StepDefinition("every koan has its own code cell under its lesson's heading, in order")>]
    member _.KoanCells() =
        let mutable lesson = ""

        let placed =
            [ for c in notebook do
                  if c.Kind = "markdown" && c.Source.StartsWith "## " then
                      lesson <- c.Source.Split('\n').[0].Substring(3)

                  match Notebooks.koanName c with
                  | Some k -> yield lesson, k
                  | None -> () ]

        let expected = koanSpecs () |> List.map (fun s -> s.Lesson, s.Koan)
        let inSourceOrder = koansInSource () |> List.map snd
        check (List.map snd placed = inSourceOrder) "koan cells are not in the lessons' order"
        check (List.sort placed = List.sort expected) $"koan cells under the wrong lessons:\n%A{placed}"

    [<StepDefinition("every koan cell names its koan")>]
    member _.Named() =
        let named = notebook |> List.choose Notebooks.koanName
        check (named.Length = (koanSpecs ()).Length) $"{named.Length} koan cells for {(koanSpecs ()).Length} koans"

    [<StepDefinition(@"^it is identical to koans/notebook/plasm-koans\.ipynb$")>]
    member _.UpToDate() =
        let committed = File.ReadAllText(Path.Combine(root, "koans", "notebook", "plasm-koans.ipynb"))
        check (committed = w.Koan) "koans/notebook/plasm-koans.ipynb is out of date; run: dotnet run --project koans/Plasm.Koans -- --notebook koans/notebook/plasm-koans.ipynb"

    [<StepDefinition("every cell of the notebook as shipped is run in order in F# Interactive")>]
    member _.RunShipped() =
        task {
            let! program = shipped ()
            let! json = Notebooks.generate program
            runs <- Notebooks.run (Notebooks.cells json)
        }
        |> unit

    [<StepDefinition("every cell of the notebook with the reference answers is run in order in F# Interactive")>]
    member _.RunAnswered() =
        task {
            let! program = answered ()
            let! json = Notebooks.generate program
            runs <- Notebooks.run (Notebooks.cells json)
        }
        |> unit

    [<StepDefinition("every cell runs without a compile error")>]
    member _.NoErrors() =
        let bad = runs |> List.filter (fun r -> not r.Errors.IsEmpty || r.Failure.IsSome)
        let shown = bad |> List.map (fun r -> $"{r.Cell.Source}\n=> %A{r.Errors} %A{r.Failure}") |> String.concat "\n\n"
        check bad.IsEmpty shown

    [<StepDefinition("every koan cell says which koan it is and to fill in the blank in that cell")>]
    member _.AsksForBlank() =
        for r in koanCells () do
            let name = (Notebooks.koanName r.Cell).Value
            check (r.Output.Contains $"✘ {name}" && r.Output.Contains "Fill in the blank (__) in this cell") $"{name}:\n{r.Output}"

    [<StepDefinition("every koan cell reports that its koan passed")>]
    member _.AllPassed() =
        let notPassed =
            koanCells ()
            |> List.filter (fun r -> not (r.Output.Contains $"✔ {(Notebooks.koanName r.Cell).Value}"))
            |> List.map (fun r -> $"{r.Cell.Source}\n=> {r.Output} %A{r.Errors} %A{r.Failure}")

        check notPassed.IsEmpty (String.concat "\n\n" notPassed)

    [<StepDefinition("the setup cell and a koan cell answered with a wrong value are run")>]
    member _.WrongAnswer() =
        task {
            let first = (koanSpecs ()).Head.Koan
            let rest = (answers ()).[first] |> List.tail
            let! program = variant "first-wrong" (fun k -> if k = first then Some("\"Gadget\"" :: rest) else None)
            let! json = Notebooks.generate program
            let cells = Notebooks.cells json
            let setup = cells |> List.find (fun c -> c.Kind = "code")
            let koan = cells |> List.find (fun c -> Notebooks.koanName c = Some first)
            runs <- Notebooks.run [ setup; koan ]
        }
        |> unit

    [<StepDefinition("the koan cell shows the expected and the actual value")>]
    member _.ShowsValues() =
        let out = (List.last runs).Output
        check (out.Contains "Expected:\n    \"Gadget\"" && out.Contains "Actual:\n    \"Widget\"") out

    [<StepDefinition("it shows what the koan teaches")>]
    member _.ShowsTeaching() =
        let out = (List.last runs).Output
        check (out.Contains $"Meditate on this: {(koanSpecs ()).Head.Discovers}") out

    [<StepDefinition("a koan cell that writes to the database is run twice with its reference answer")>]
    member _.Twice() =
        task {
            let! program = answered ()
            let! json = Notebooks.generate program
            let cells = Notebooks.cells json
            let setup = cells |> List.find (fun c -> c.Kind = "code")
            let index = cells |> List.findIndex (fun c -> Notebooks.koanName c = Some "Inserting returns the stored record")
            // The lesson's helper cell comes right after its heading; earlier koan cells are skipped.
            let heading = cells.[.. index] |> List.findIndexBack (fun c -> c.Kind = "markdown")
            let helpers = cells.[heading + 1 .. index - 1] |> List.filter (fun c -> (Notebooks.koanName c).IsNone)
            runs <- Notebooks.run ([ setup ] @ helpers @ [ cells.[index]; cells.[index] ])
        }
        |> unit

    [<StepDefinition("it passes both times")>]
    member _.PassesTwice() =
        let koanRuns = runs |> List.filter (fun r -> (Notebooks.koanName r.Cell).IsSome)
        check (koanRuns.Length = 2) "expected two runs of the koan cell"

        for r in koanRuns do
            check (r.Output.Contains "✔ Inserting returns the stored record") $"{r.Output} %A{r.Errors} %A{r.Failure}"

    [<StepDefinition("the notebook as shipped and the notebook with the reference answers are run by dotnet-repl")>]
    member _.RunInKernel() =
        task {
            let! shippedProgram = shipped ()
            let! answeredProgram = answered ()
            let! shippedJson = Notebooks.generate shippedProgram
            let! answeredJson = Notebooks.generate answeredProgram
            kernelRuns <- Some(Kernel.run shippedJson, Kernel.run answeredJson)
        }
        |> unit

    [<StepDefinition("as shipped every koan cell asks for its blank")>]
    member _.KernelShipped() =
        let runs, _ = kernelRuns.Value
        let koans = runs |> List.filter (fun r -> (Notebooks.koanName r.Cell).IsSome)
        check (koans.Length = (koanSpecs ()).Length) $"{koans.Length} koan cells ran"

        for r in koans do
            check (r.Output.Contains "Fill in the blank (__) in this cell") $"{(Notebooks.koanName r.Cell).Value}:\n{r.Output}"

    [<StepDefinition("with the reference answers every koan cell passes")>]
    member _.KernelAnswered() =
        let _, runs = kernelRuns.Value

        let failing =
            runs
            |> List.filter (fun r -> (Notebooks.koanName r.Cell).IsSome && not (r.Output.Contains "✔"))
            |> List.map (fun r -> $"{(Notebooks.koanName r.Cell).Value}:\n{r.Output}")

        check failing.IsEmpty (String.concat "\n\n" failing)

    [<StepDefinition("no other cell reports an error")>]
    member _.KernelNoErrors() =
        let a, b = kernelRuns.Value

        let bad =
            a @ b
            |> List.filter (fun r -> (Notebooks.koanName r.Cell).IsNone)
            |> List.filter (fun r -> r.Output.Contains "error" || r.Output.Contains "Exception")
            |> List.map (fun r -> $"{r.Cell.Source}\n=> {r.Output}")

        check bad.IsEmpty (String.concat "\n\n" bad)
