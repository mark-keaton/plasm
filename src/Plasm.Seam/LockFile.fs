/// seam.lock, the format: its model, its parser, its writer, and the
/// comparison that names what changed between two locks. Its grammar is
/// docs/seam-lock.md; nothing else in the seam formats or splits lock text.
module Plasm.Seam.LockFile

open System
open System.Text
open System.Text.RegularExpressions

/// The format this tool writes. A lock without a version line is format 0
/// (written before 0.5.0 was released), which is read and written again.
[<Literal>]
let Current = 1

type Field = { Name: string; Type: string }

type Case = { Name: string; Kind: string; Fields: Field list }

type Parameter = { Type: string; Name: string }

/// A member of a class: a method (Parameters given) or a property (None).
type Signature =
    { Static: bool
      Returns: string
      Name: string
      TypeParameters: string list
      Parameters: Parameter list option
      Constraints: (string * string) list }

type Section =
    | Record of name: string * ofType: string * fromCSharp: bool * what: string option * fields: Field list
    | Union of name: string * ofType: string * cases: Case list
    | Refusal of ofType: string * codes: string list
    /// opened: the Store's place; file: its database a file; registered: AddXxx.
    | Class of name: string * ofType: string * opened: string option * file: bool * registered: string option * members: Signature list
    | Http of name: string * statuses: (string * int) list
    | Json of types: string list

type Lock = { Version: int; Sections: Section list }

// ---- writing ----

let private header =
    [ "# The C# contract Plasm.Seam wrote from the F# data assembly and Seam.cs. Commit this file."
      "# A build rewrites it when the contract changes, and says what changed; a build in CI"
      "# (CI=true in a project made by the template, ContinuousIntegrationBuild=true, or"
      "# -p:PlasmSeamDrift=fail) stops instead. Its format: https://mark-keaton.github.io/plasm/docs/seam-lock.html"
      "# (and docs/seam-lock.md in Plasm.CSharp's package)." ]

let private quote (s: string) = "\"" + s + "\""

let fieldText (f: Field) = $"{f.Name}: {f.Type}"

let caseText (c: Case) =
    let fields = if c.Fields.IsEmpty then "" else " with " + (c.Fields |> List.map fieldText |> String.concat ", ")
    $"{c.Name} kind {quote c.Kind}{fields}"

let signatureText (s: Signature) =
    let st = if s.Static then "static " else ""
    let tps = if s.TypeParameters.IsEmpty then "" else "<" + String.concat ", " s.TypeParameters + ">"

    let ps =
        match s.Parameters with
        | None -> ""
        | Some ps -> "(" + (ps |> List.map (fun p -> $"{p.Type} {p.Name}") |> String.concat ", ") + ")"

    let cs = s.Constraints |> List.map (fun (t, c) -> $" where {t} : {c}") |> String.concat ""
    $"{st}{s.Returns} {s.Name}{tps}{ps}{cs}"

/// A section's header line.
let headerText (s: Section) =
    match s with
    | Record(name, of', fromCSharp, what, _) ->
        let from = if fromCSharp then ", and from C#" else ""
        let said = match what with Some w -> $", Missing and Stale say {quote w}" | None -> ""
        $"record {name} = {of'}{from}{said}"
    | Union(name, of', _) -> $"union {name} = {of'}"
    | Refusal(of', _) -> $"refusal {of'}"
    | Class(name, of', opened, file, registered, _) ->
        let o = match opened with Some p -> $", opened from {p}" | None -> ""
        let f = if file then ", its database a file" else ""
        let r = match registered with Some n -> $", registered by {n}" | None -> ""
        $"class {name} = {of'}{o}{f}{r}"
    | Http(name, _) -> $"http {name}"
    | Json _ -> "json SeamJson"

/// A section's member lines, without their indent.
let memberTexts (s: Section) : string list =
    match s with
    | Record(_, _, _, _, fields) -> fields |> List.map fieldText
    | Union(_, _, cases) -> cases |> List.map caseText
    | Refusal(_, codes) -> codes
    | Class(_, _, _, _, _, members) -> members |> List.map signatureText
    | Http(_, statuses) -> statuses |> List.map (fun (c, n) -> $"{c} {n}")
    | Json types -> types

/// The lock as text: its comment, its version, its sections.
let write (lock: Lock) : string =
    let text = StringBuilder()
    let add (s: string) = text.Append(s).Append('\n') |> ignore
    header |> List.iter add
    add ""
    add $"seam.lock {lock.Version}"

    for s in lock.Sections do
        add (headerText s)

        for m in memberTexts s do
            add ("  " + m)

    text.ToString()

// ---- reading ----

let private name = @"[A-Za-z_][A-Za-z0-9_]*"
let private dotted = $@"{name}(\.{name})*"
let private fsharp = $@"{dotted}(<.*?>)?"

/// The end of a C# type from `i` (the grammar's cs-type), or -1.
let rec private csTypeEnd (s: string) (i: int) : int =
    let m = Regex($@"\G{dotted}").Match(s, i)

    if not m.Success then
        -1
    else
        let mutable j = i + m.Length

        if j < s.Length && s[j] = '<' then
            j <- csTypeEnd s (j + 1)

            while j > 0 && j + 1 < s.Length && s.Substring(j, 2) = ", " do
                j <- csTypeEnd s (j + 2)

            if j > 0 && j < s.Length && s[j] = '>' then j <- j + 1 else j <- -1

        if j > 0 && j < s.Length && s[j] = '?' then j <- j + 1

        while j > 0 && j + 1 < s.Length && s.Substring(j, 2) = "[]" do
            j <- j + 2

            if j < s.Length && s[j] = '?' then j <- j + 1

        j

let private isType (s: string) = s.Length > 0 && csTypeEnd s 0 = s.Length

/// A field (Name: Type), or None.
let private field (s: string) : Field option =
    let m = Regex($@"^({name}): (.+)$").Match s
    if m.Success && isType m.Groups[2].Value then Some({ Name = m.Groups[1].Value; Type = m.Groups[2].Value }: Field) else None

/// Fields separated by ", ", each Name: Type, the types read whole.
let private fields (s: string) : Field list option =
    let rec go (i: int) (acc: Field list) =
        let m = Regex($@"\G({name}): ").Match(s, i)

        if not m.Success then
            None
        else
            let start = i + m.Length
            let e = csTypeEnd s start

            if e < 0 then
                None
            else
                let f: Field = { Name = m.Groups[1].Value; Type = s.Substring(start, e - start) }

                if e = s.Length then Some(List.rev (f :: acc))
                elif s.Substring(e).StartsWith ", " then go (e + 2) (f :: acc)
                else None

    go 0 []

/// A member line's signature, or None.
let signature (line: string) : Signature option =
    let st, s = if line.StartsWith "static " then true, line.Substring 7 else false, line
    let j = csTypeEnd s 0

    if j <= 0 || j >= s.Length || s[j] <> ' ' then
        None
    else
        let returns = s.Substring(0, j)
        let rest = s.Substring(j + 1)
        let m = Regex($@"^({name})(<({name}(, {name})*)>)?").Match rest

        if not m.Success then
            None
        else
            let tps = if m.Groups[3].Success then m.Groups[3].Value.Split(", ") |> List.ofArray else []
            let tail = rest.Substring m.Length

            // The parameters, when there are, run to the parenthesis that closes them.
            let close =
                if not (tail.StartsWith "(") then
                    -1
                else
                    let mutable depth = 0
                    let mutable at = -1

                    for k in 0 .. tail.Length - 1 do
                        if at < 0 then
                            match tail[k] with
                            | '(' -> depth <- depth + 1
                            | ')' ->
                                depth <- depth - 1
                                if depth = 0 then at <- k
                            | _ -> ()

                    at

            let readParameters (inner: string) : Parameter list option =
                let rec go (i: int) (acc: Parameter list) =
                    let e = csTypeEnd inner i

                    if e <= 0 || e >= inner.Length || inner[e] <> ' ' then
                        None
                    else
                        let n = Regex($@"\G{name}").Match(inner, e + 1)

                        if not n.Success then
                            None
                        else
                            let p: Parameter = { Type = inner.Substring(i, e - i); Name = n.Value }
                            let k = e + 1 + n.Length

                            if k = inner.Length then Some(List.rev (p :: acc))
                            elif inner.Substring(k).StartsWith ", " then go (k + 2) (p :: acc)
                            else None

                if inner = "" then Some [] else go 0 []

            // None: a property; Some None: parameters the grammar does not take.
            let parameters, after =
                if tail.StartsWith "(" then
                    if close < 0 then Some None, "" else Some(readParameters (tail.Substring(1, close - 1))), tail.Substring(close + 1)
                else
                    None, tail

            let constraints = Regex($@"\G where ({name}) : (struct|class)")

            let rec cons (i: int) acc =
                if i = after.Length then
                    Some(List.rev acc)
                else
                    let c = constraints.Match(after, i)
                    if c.Success then cons (i + c.Length) ((c.Groups[1].Value, c.Groups[2].Value) :: acc) else None

            match parameters, cons 0 [] with
            | Some None, _ -> None
            | None, _ when after <> "" -> None
            | ps, Some cs ->
                Some
                    { Static = st
                      Returns = returns
                      Name = m.Groups[1].Value
                      TypeParameters = tps
                      Parameters = Option.flatten ps
                      Constraints = cs }
            | _, None -> None

/// One line of a lock, by the grammar's productions.
type Line =
    | Comment
    | Blank
    | VersionLine of int
    | Header of Section
    | MemberOf of string

let private headers =
    [ Regex($@"^record ({name}) = ({fsharp}?)(, and from C#)?(, Missing and Stale say ""([^""]*)"")?$"),
      (fun (m: Match) -> Record(m.Groups[1].Value, m.Groups[2].Value, m.Groups[5].Success, (if m.Groups[7].Success then Some m.Groups[7].Value else None), []))
      Regex($@"^union ({name}) = ({fsharp})$"), (fun m -> Union(m.Groups[1].Value, m.Groups[2].Value, []))
      Regex($@"^refusal ({fsharp})$"), (fun m -> Refusal(m.Groups[1].Value, []))
      Regex($@"^class ({name}) = ({fsharp}?)(, opened from ({fsharp}?))?(, its database a file)?(, registered by ({name}))?$"),
      (fun m ->
          Class(
              m.Groups[1].Value,
              m.Groups[2].Value,
              (if m.Groups[5].Success then Some m.Groups[6].Value else None),
              m.Groups[9].Success,
              (if m.Groups[10].Success then Some m.Groups[11].Value else None),
              []
          ))
      Regex($@"^http ({name})$"), (fun m -> Http(m.Groups[1].Value, []))
      Regex("^json SeamJson$"), (fun _ -> Json []) ]

/// A line as the grammar reads it outside any section: a comment, a blank,
/// the version, a section's header, or an indented member line.
let line (raw: string) : Line option =
    let text = raw.TrimEnd('\r', ' ')

    if text = "" then
        Some Blank
    elif text.StartsWith "#" then
        Some Comment
    elif text.StartsWith "  " then
        Some(MemberOf(text.Substring 2))
    else
        let v = Regex(@"^seam\.lock ([0-9]+)$").Match text

        if v.Success then
            Some(VersionLine(int v.Groups[1].Value))
        else
            headers |> List.tryPick (fun (r, make) -> let m = r.Match text in if m.Success then Some(Header(make m)) else None)

/// A member line, read as the member of `section` it must be; the section
/// with it, or why not.
let addMember (version: int) (section: Section) (text: string) : Result<Section, string> =
    let no () = Error $"\"  {text}\" is no member line of {(headerText section).Split(' ').[0]} {(headerText section).Split(' ').[1]}"

    match section with
    | Record(n, o, f, w, fs) ->
        match field text with
        | Some fd -> Ok(Record(n, o, f, w, fs @ [ fd ]))
        | None -> no ()
    | Union(n, o, cs) ->
        let m = Regex($@"^({name}) kind ""([^""]*)""( with (.+))?$").Match text

        if not m.Success then
            no ()
        else
            match (if m.Groups[3].Success then fields m.Groups[4].Value else Some []) with
            | Some fds -> Ok(Union(n, o, cs @ [ { Name = m.Groups[1].Value; Kind = m.Groups[2].Value; Fields = fds } ]))
            | None -> no ()
    | Refusal(o, codes) -> if Regex.IsMatch(text, $"^{name}$") then Ok(Refusal(o, codes @ [ text ])) else no ()
    | Class(n, o, opened, file, registered, ms) ->
        // Format 0 wrote what the Store gives, and AddXxx, as member lines.
        let zero = Regex($@"^opened from ({fsharp}): OpenAsync, Ddl, SandboxAsync(, DatabaseFile, CreateDatabaseAsync)?$").Match text

        if version = 0 && zero.Success && ms.IsEmpty && opened.IsNone then
            Ok(Class(n, o, Some zero.Groups[1].Value, zero.Groups[4].Success, registered, ms))
        elif version = 0 && Regex.IsMatch(text, $"^Add{name}$") && ms.IsEmpty && registered.IsNone then
            Ok(Class(n, o, opened, file, Some text, ms))
        else
            match signature text with
            | Some s -> Ok(Class(n, o, opened, file, registered, ms @ [ s ]))
            | None -> no ()
    | Http(n, ss) ->
        let m = Regex($@"^(value|{name}) ([0-9]{{3}})$").Match text
        if m.Success then Ok(Http(n, ss @ [ m.Groups[1].Value, int m.Groups[2].Value ])) else no ()
    | Json ts -> if isType text then Ok(Json(ts @ [ text ])) else no ()

let private rank (s: Section) =
    match s with
    | Record _ -> 0
    | Union _ -> 1
    | Refusal _ -> 2
    | Class _ -> 3
    | Http _ -> 4
    | Json _ -> 5

let private word (s: Section) = (headerText s).Split(' ').[0]

/// A lock's text as its model, or each line the grammar does not take, with
/// its number and why. A lock of a format newer than this tool's is refused.
let parse (text: string) : Result<Lock, (int * string) list> =
    let errors = ResizeArray<int * string>()
    let sections = ResizeArray<Section>()
    let mutable version: int option = None
    let mutable seen = false

    for number, raw in text.Split '\n' |> Array.mapi (fun i l -> i + 1, l) do
        match line raw with
        | Some Comment
        | Some Blank -> ()
        | Some(VersionLine v) ->
            if seen || version.IsSome then
                errors.Add(number, "the version line comes first, before every section, and once")
            elif v > Current then
                errors.Add(number, $"seam.lock {v} is a format newer than this Plasm.Seam reads ({Current}): build with the Plasm.CSharp that wrote it, or a newer one")
            else
                version <- Some v
        | Some(Header s) ->
            seen <- true

            match Seq.tryLast sections with
            | Some last when rank s < rank last -> errors.Add(number, $"\"{raw.TrimEnd('\r', ' ')}\": a {word s} comes before every {word last}")
            | Some(Json _) when (match s with Json _ -> true | _ -> false) -> errors.Add(number, "a second json section")
            | _ -> ()

            sections.Add s
        | Some(MemberOf m) ->
            if sections.Count = 0 then
                errors.Add(number, $"\"{raw.TrimEnd('\r', ' ')}\" is a member line before any section")
            else
                match addMember (defaultArg version 0) sections[sections.Count - 1] m with
                | Ok s -> sections[sections.Count - 1] <- s
                | Error why -> errors.Add(number, why)
        | None -> errors.Add(number, $"\"{raw.TrimEnd('\r', ' ')}\" is no section's header")

    if not (sections |> Seq.exists (function Json _ -> true | _ -> false)) && errors.Count = 0 then
        errors.Add(0, "no json section")

    if errors.Count > 0 then
        Error(List.ofSeq errors)
    else
        Ok { Version = defaultArg version 0; Sections = List.ofSeq sections }

// ---- comparing ----

/// A section's identity: its word and its name.
let private key (s: Section) =
    match s with
    | Record(n, _, _, _, _) -> "record " + n
    | Union(n, _, _) -> "union " + n
    | Refusal(o, _) -> "refusal " + o
    | Class(n, _, _, _, _, _) -> "class " + n
    | Http(n, _) -> "http " + n
    | Json _ -> "json"

/// What changed from `before` to `after`, a line each; empty when they are the
/// same contract (their versions, comments and spacing aside).
let changes (before: Lock) (after: Lock) : string list =
    let index (l: Lock) = l.Sections |> List.map (fun s -> key s, s)
    let b = index before
    let a = index after
    let find k xs = xs |> List.tryFind (fun (x, _) -> x = k) |> Option.map snd

    [ for k, s in b do
          if (find k a).IsNone then yield $"removed: {headerText s}"
      for k, s in a do
          match find k b with
          | None -> yield $"added: {headerText s}"
          | Some old ->
              if headerText old <> headerText s then
                  yield $"changed: {headerText old}, now {headerText s}"

              let om = memberTexts old
              let nm = memberTexts s

              for m in om do
                  if not (List.contains m nm) then yield $"removed from {headerText s}: {m}"

              for m in nm do
                  if not (List.contains m om) then yield $"added to {headerText s}: {m}"

              if List.sort om = List.sort nm && om <> nm then
                  yield $"the order of {headerText s}'s lines changed"
      if (b |> List.map fst |> List.filter (fun k -> (find k a).IsSome)) <> (a |> List.map fst |> List.filter (fun k -> (find k b).IsSome)) then
          yield "the order of the sections changed" ]
