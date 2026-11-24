/// seam.lock: the generated contract, one fact a line, committed with the
/// source. A build that would change it says what changed; with drift set to
/// fail it stops instead of rewriting.
module Plasm.Seam.Lock

open System.IO
open System.Text

/// The contract the seam wrote, as the format's model (LockFile).
let model (seam: SeamModel) (http: bool) : LockFile.Lock =
    let field (f: Field) : LockFile.Field = { Name = f.Name; Type = Emit.short f.Cross.CsFull }

    let signature (m: Member) =
        let text = Emit.short (Emit.signature m true false)

        match LockFile.signature text with
        | Some s -> s
        | None -> failwith $"the seam wrote a member line its own format does not read: {text}"

    let sections =
        [ for r in seam.Records do
              let what =
                  match seam.Whats.TryGetValue(r.Display.Substring(r.Display.LastIndexOf '.' + 1)) with
                  | true, word -> Some word
                  | _ -> None

              LockFile.Record(r.Name, r.Display, r.Into.IsSome, what, [ for f in r.Fields -> field f ])
          for u in seam.Unions do
              LockFile.Union(u.Name, u.Display, [ for c in u.Cases -> { Name = c.Name; Kind = c.Kind; Fields = [ for f in c.Fields -> field f ] } ])
          for r in seam.Refusals do
              LockFile.Refusal(r.Display, r.Codes)
          for d in seam.Data do
              LockFile.Class(
                  d.Name,
                  d.Display,
                  d.Store |> Option.map _.Display,
                  (d.Store |> Option.exists _.File),
                  (if http && d.Store.IsSome then Some("Add" + d.Name) else None),
                  [ for m in d.Members -> signature m ]
              )
          if http then
              for s in seam.Shapes |> Seq.filter Emit.hasHttp do
                  LockFile.Http(s.Name, [ for (cs, n) as c in s.Cases -> (if cs.IsNone then "value" else n), snd (Emit.status s c).Value ])
          LockFile.Json [ for j in Seq.append seam.Json seam.HostJson -> Emit.short j ] ]

    { Version = LockFile.Current; Sections = sections }

/// The lock's text, as the format writes it.
let describe (seam: SeamModel) (http: bool) : string = LockFile.write (model seam http)

/// What the lock check did: nothing, wrote a new lock, or rewrote one.
type Checked =
    | Same
    | Wrote
    | Rewrote of string list

/// Compares the lock at `path` with `text`, the one the seam would write, as
/// the format reads both: their versions, comments and spacing aside.
let check (path: string) (text: string) (fail: bool) : Checked =
    let name = Path.GetFileName path

    let next =
        match LockFile.parse text with
        | Ok l -> l
        | Error wrong -> failwith $"the seam wrote a lock its own format does not read: %A{wrong}"

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

        match LockFile.parse old with
        | Error((n, why) :: _) when why.Contains "is a format newer" ->
            Seam.failAt "SEAM021" (Some $"{path}({n},1)") why
        | Error wrong ->
            let lines = wrong |> List.map (fun (n, why) -> $"line {n}: {why}")

            if fail then
                Seam.failAt "SEAM021" (Some path) $"""{name} is not a lock ({String.concat "; " lines}): build without PlasmSeamDrift=fail to write it again, and commit it"""

            File.WriteAllText(path, text, UTF8Encoding false)
            Rewrote [ for l in lines -> $"{name} was not a lock, {l}, and is written again" ]
        | Ok before ->
            match LockFile.changes before next with
            | [] ->
                // The same contract: a CI build passes; another writes the
                // lock as this format does, its comments and version too.
                if not fail && old <> text then
                    File.WriteAllText(path, text, UTF8Encoding false)

                Same
            | found ->
                if fail then
                    raise (ContractChanged(path, found))

                File.WriteAllText(path, text, UTF8Encoding false)
                Rewrote found
