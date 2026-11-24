/// Plasm.Seam: reads the metadata of a built F# data assembly and writes the C#
/// a host calls: views, mapping, a class per data type, the JSON context and
/// the HTTP helper, with a lock file that describes the contract. Run by
/// Plasm.CSharp's build targets before a host's C# is compiled:
///
///   Plasm.Seam --config <Seam.cs> --out <folder> --lock <file> --namespace <ns>
///              --refs <file listing every reference, one path a line>
///              --data <file listing the assemblies the seam may read from>
///              [--drift write|fail] [--http true|false]
///
/// Every failure is one line in MSBuild's own format, so the build shows it as
/// an error with its code (SEAMnnn).
module Plasm.Seam.Program

open System
open System.IO
open System.Text

/// What a run is given.
type Options =
    { Config: string
      Out: string
      Lock: string
      Namespace: string
      References: string[]
      Data: string[]
      /// Stop when the contract would change, rather than rewrite the lock.
      Fail: bool
      Http: bool }

/// What a run did: the files it wrote, by name, and what became of the lock.
type Ran = { Files: (string * string) list; Lock: Lock.Checked }

/// Writes each file whose text changed (so an unchanged seam does not
/// recompile the host) and deletes generated files no longer written.
let private write (folder: string) (files: (string * string) list) =
    Directory.CreateDirectory folder |> ignore
    let names = files |> List.map fst |> set

    for old in Directory.GetFiles(folder, "*.g.cs") do
        if not (names.Contains(Path.GetFileName old)) then
            File.Delete old

    for name, text in files do
        let path = Path.Combine(folder, name)

        if not (File.Exists path) || File.ReadAllText path <> text then
            File.WriteAllText(path, text, UTF8Encoding false)

/// What a build that is not to change the contract says when it would.
let contractChanged (lock: string) (changes: string list) =
    let name = Path.GetFileName lock
    let s = if changes.Length = 1 then "" else "s"
    $"the generated C# no longer matches {name} ({changes.Length} change{s}, below). If the change is meant, build without PlasmSeamDrift=fail and commit {name}"

/// One run: reads, checks the lock, writes. Throws SeamError for a mistake,
/// and ContractChanged when the contract would change and must not.
let run (o: Options) : Ran =
    let config = Config.read o.Config
    use reader = new Reader(config, o.References, o.Data, o.Namespace)
    let seam = reader.Read()
    let files = Emit.files seam o.Http
    let lock = Lock.check o.Lock (Lock.describe seam o.Http) o.Fail
    write o.Out files
    { Files = files; Lock = lock }

[<EntryPoint>]
let main args =
    let options =
        args
        |> Array.chunkBySize 2
        |> Array.choose (function
            | [| k; v |] -> Some(k, v)
            | _ -> None)
        |> dict

    let need name =
        match options.TryGetValue name with
        | true, v when v <> "" -> v
        | _ -> Seam.fail "SEAM000" $"the argument {name} is missing; Plasm.Seam is run by Plasm.CSharp's build targets, which pass it"

    let lines path =
        File.ReadAllLines path |> Array.filter (fun l -> l.Trim() <> "")

    let flag name value =
        match options.TryGetValue name with
        | true, v -> v.Equals(value, StringComparison.OrdinalIgnoreCase)
        | _ -> false

    try
        let o =
            { Config = need "--config"
              Out = need "--out"
              Lock = need "--lock"
              Namespace = need "--namespace"
              References = lines (need "--refs")
              Data = lines (need "--data")
              Fail = flag "--drift" "fail"
              Http = not (flag "--http" "false") }

        let ran = run o
        let name = Path.GetFileName o.Lock

        match ran.Lock with
        | Lock.Same -> ()
        | Lock.Wrote -> printfn $"Plasm.Seam: wrote {o.Lock}, the contract of the generated C#. Commit it."
        | Lock.Rewrote changes ->
            let s = if changes.Length = 1 then "" else "s"
            printfn $"Plasm.Seam: the contract changed, and {name} is rewritten ({changes.Length} change{s}). Commit it."

            for change in changes do
                printfn $"Plasm.Seam:   {change}"

        0
    with
    | ContractChanged(lock, changes) ->
        eprintfn $"{lock} : error SEAM020: {contractChanged lock changes}"

        for change in changes do
            eprintfn $"{lock} : error SEAM020: the contract would change: {change}"

        1
    | SeamError(code, text, origin) ->
        eprintfn $"""{origin |> Option.defaultValue "Plasm.Seam"} : error {code}: {text}"""
        1
    | e ->
        // The tool's own fault: one readable line, then the trace.
        eprintfn $"Plasm.Seam : error SEAM999: the seam tool failed ({e.GetType().Name}: {e.Message.ReplaceLineEndings(String.Empty)}); this is a fault in the tool, not in your code"
        eprintfn $"{e.StackTrace}"
        1
