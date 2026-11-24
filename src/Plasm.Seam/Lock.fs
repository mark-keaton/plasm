/// seam.lock: the generated contract, one fact a line, committed with the
/// source. A build that would change it says what changed; with drift set to
/// fail it stops instead of rewriting.
module Plasm.Seam.Lock

open System.IO
open System.Text

let private top =
    "# The C# contract Plasm.Seam wrote from the F# data assembly and Seam.cs. Commit this file.\n"
    + "# A build rewrites it when the contract changes, and says what changed; a build with\n"
    + "# -p:PlasmSeamDrift=fail (the default when ContinuousIntegrationBuild=true) stops instead.\n\n"

let describe (seam: SeamModel) (http: bool) : string =
    let text = StringBuilder(top)
    let add (s: string) = text.Append(s).Append('\n') |> ignore
    let fields (fs: Field seq) = fs |> Seq.map (fun f -> $"{f.Name}: {Emit.short f.Cross.CsFull}") |> String.concat ", "

    for r in seam.Records do
        add $"record {r.Name} = {r.Display}"

        for f in r.Fields do
            add $"  {f.Name}: {Emit.short f.Cross.CsFull}"

    for u in seam.Unions do
        add $"union {u.Name} = {u.Display}"

        for c in u.Cases do
            let with' = if c.Fields.Count > 0 then " with " + fields c.Fields else ""
            add $"  {c.Name} kind \"{c.Kind}\"{with'}"

    for r in seam.Refusals do
        add $"refusal {r.Display}"

        for code in r.Codes do
            add $"  {code}"

    for d in seam.Data do
        add $"class {d.Name} = {d.Display}"

        for m in d.Members do
            add $"  {Emit.short (Emit.signature m true false)}"

    if http then
        for s in seam.Shapes |> Seq.filter Emit.hasHttp do
            add $"http {s.Name}"

            for (cs, n) as c in s.Cases do
                add $"""  {(if cs.IsNone then "value" else n)} {snd (Emit.status s c).Value}"""

    add "json SeamJson"

    for j in Seq.append seam.Json seam.HostJson do
        add $"  {Emit.short j}"

    text.ToString()

/// Each fact as (section, line), comments and blank lines left out.
let private facts (text: string) =
    let mutable section = ""

    [ for raw in text.Split '\n' do
          let line = raw.TrimEnd('\r', ' ')

          if line.Length > 0 && line[0] <> '#' then
              if line[0] <> ' ' then section <- line
              ((if line[0] = ' ' then section else ""), line.Trim()) ]

/// What changed between two locks, each change a line.
let changes (before: string) (after: string) : string list =
    let b = facts before
    let a = facts after

    let gone =
        b
        |> List.filter (fun x -> not (List.contains x a))
        |> List.map (fun (s, l) -> if s = "" then $"removed: {l}" else $"removed from {s}: {l}")

    let added =
        a
        |> List.filter (fun x -> not (List.contains x b))
        |> List.map (fun (s, l) -> if s = "" then $"added: {l}" else $"added to {s}: {l}")

    match gone @ added with
    | [] when b <> a -> [ "the order of its lines changed" ]
    | [] -> [ "only its comments or spacing changed" ]
    | all -> all

/// What the lock check did: nothing, wrote a new lock, or rewrote one.
type Checked =
    | Same
    | Wrote
    | Rewrote of string list

let check (path: string) (text: string) (fail: bool) : Checked =
    let name = Path.GetFileName path

    if not (File.Exists path) then
        if fail then
            Seam.failAt
                "SEAM020"
                (Some path)
                $"{name} is not there, and PlasmSeamDrift=fail writes none: build once without it (or with -p:PlasmSeamDrift=write) and commit {name}"

        File.WriteAllText(path, text, UTF8Encoding false)
        Wrote
    else
        let old = File.ReadAllText(path).ReplaceLineEndings "\n"

        if old = text then
            Same
        else
            let found = changes old text

            if fail then
                for change in found do
                    eprintfn $"{path} : error SEAM020: the contract would change: {change}"

                let s = if found.Length = 1 then "" else "s"

                Seam.failAt
                    "SEAM020"
                    (Some path)
                    $"the generated C# no longer matches {name} ({found.Length} change{s}, above). If the change is meant, build without PlasmSeamDrift=fail and commit {name}"

            File.WriteAllText(path, text, UTF8Encoding false)
            Rewrote found
