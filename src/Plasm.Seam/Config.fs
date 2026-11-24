/// The developer's configuration: assembly attributes in a C# file (Seam.cs),
/// read as syntax. The file is compiled with the host too, against the
/// attribute types in Plasm.CSharp, so the C# compiler checks every typeof
/// and nameof in it.
///
///   [assembly: SeamData(typeof(BookingData))]
///   [assembly: SeamView(typeof(JobNote), Name = "NoteView", Omit = [nameof(JobNote.Id)])]
///   [assembly: SeamField(typeof(Order), nameof(Order.Total), After = nameof(Order.Lines))]
///   [assembly: SeamCase(typeof(Condition), nameof(Condition.Available), Name = "Bookable", Kind = "bookable")]
///   [assembly: SeamJson(typeof(NewJob), typeof(Quote))]
module Plasm.Seam.Config

open System.Collections.Generic
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

/// Where in the configuration file something was written, for an error.
type At =
    { File: string
      Line: int
      Column: int }

    member a.Origin = Some $"{a.File}({a.Line},{a.Column})"

type DataConfig =
    { Type: string
      At: At
      mutable Name: string option }

type ViewConfig =
    { Type: string
      At: At
      mutable Name: string option
      mutable What: string option
      Omit: List<string>
      mutable Used: bool
      OmitUsed: HashSet<string> }

type FieldConfig =
    { Record: string
      Member: string
      At: At
      mutable After: string option
      mutable Before: string option
      mutable Used: bool }

type CaseConfig =
    { Union: string
      Case: string
      At: At
      mutable Name: string option
      mutable Kind: string option
      mutable Used: bool }

type Config =
    { File: string
      Data: List<DataConfig>
      Views: List<ViewConfig>
      Cases: List<CaseConfig>
      Fields: List<FieldConfig>
      /// The host's own types the JSON context lists too, as written.
      Json: List<string> }

let private attributes = [ "SeamData"; "SeamView"; "SeamField"; "SeamCase"; "SeamJson" ]

let read (file: string) : Config =
    if not (System.IO.File.Exists file) then
        Seam.fail "SEAM001" $"the seam's configuration {file} is not there: write it, with at least [assembly: SeamData(typeof(YourDataType))]"

    let config =
        { File = file
          Data = List()
          Views = List()
          Cases = List()
          Fields = List()
          Json = List() }

    let where (node: SyntaxNode) =
        let p = node.GetLocation().GetLineSpan().StartLinePosition
        { File = file; Line = p.Line + 1; Column = p.Character + 1 }

    let bad (node: SyntaxNode) text = Seam.failAt "SEAM001" (where node).Origin text

    let typeOf (e: ExpressionSyntax) =
        match e with
        | :? TypeOfExpressionSyntax as t -> t.Type.ToString().Replace("global::", "").Replace(" ", "")
        | _ -> bad e $"expected typeof(...), found {e}"

    let text (e: ExpressionSyntax) =
        match e with
        | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression -> l.Token.ValueText
        | :? InvocationExpressionSyntax as i when
            (match i.Expression with
             | :? IdentifierNameSyntax as n -> n.Identifier.ValueText = "nameof"
             | _ -> false)
            && i.ArgumentList.Arguments.Count = 1
            ->
            i.ArgumentList.Arguments[0].Expression.ToString().Split('.') |> Array.last |> _.Trim()
        | _ -> bad e $"expected a string literal or nameof(...), found {e}: the seam tool reads this file as written and evaluates nothing else"

    let identifier (a: AttributeArgumentSyntax) =
        let t = text a.Expression

        if not (SyntaxFacts.IsValidIdentifier t) || SyntaxFacts.GetKeywordKind t <> SyntaxKind.None then
            bad a $"\"{t}\" is not a C# identifier, so it cannot be a type's name"

        t

    let texts (e: ExpressionSyntax) : string seq =
        match e with
        | :? CollectionExpressionSyntax as c ->
            c.Elements
            |> Seq.map (fun x ->
                match x with
                | :? ExpressionElementSyntax as x -> text x.Expression
                | _ -> bad x $"expected a string literal or nameof(...), found {x}")
            |> List.ofSeq
            |> Seq.ofList
        | :? ArrayCreationExpressionSyntax as a when not (isNull a.Initializer) -> a.Initializer.Expressions |> Seq.map text |> List.ofSeq |> Seq.ofList
        | :? ImplicitArrayCreationExpressionSyntax as a -> a.Initializer.Expressions |> Seq.map text |> List.ofSeq |> Seq.ofList
        | _ -> bad e $"expected a list such as [nameof(A.B), \"C\"], found {e}"

    let attribute (a: AttributeSyntax) =
        let name =
            let n = a.Name.ToString().Split('.', ':') |> Array.last
            if n.EndsWith "Attribute" then n.Substring(0, n.Length - "Attribute".Length) else n

        if not (List.contains name attributes) then
            if name.StartsWith "Seam" then
                bad a $"""{name} is not one of the seam's attributes ({String.concat ", " attributes})"""
        else
            let arguments = if isNull a.ArgumentList then [] else List.ofSeq a.ArgumentList.Arguments
            let positional = arguments |> List.filter (fun x -> isNull x.NameEquals)
            let named = arguments |> List.filter (fun x -> not (isNull x.NameEquals))
            let at = where a

            let setNamed (known: (string * (AttributeArgumentSyntax -> unit)) list) =
                for argument in named do
                    let key = argument.NameEquals.Name.Identifier.ValueText

                    match known |> List.tryFind (fun (k, _) -> k = key) with
                    | Some(_, set) -> set argument
                    | None ->
                        let has = if known.IsEmpty then "takes only types" else "has " + String.concat ", " (List.map fst known)
                        bad argument $"{name} has no {key}; it {has}"

            match name with
            | "SeamData" ->
                if positional.Length <> 1 then
                    bad a "SeamData takes one type: SeamData(typeof(YourDataType))"

                let data = { Type = typeOf positional[0].Expression; At = at; Name = None }
                setNamed [ "Name", (fun x -> data.Name <- Some(identifier x)) ]
                config.Data.Add data
            | "SeamView" ->
                if positional.Length <> 1 then
                    bad a "SeamView takes one type: SeamView(typeof(YourRecord), Name = \"...\")"

                let view =
                    { Type = typeOf positional[0].Expression
                      At = at
                      Name = None
                      What = None
                      Omit = List()
                      Used = false
                      OmitUsed = HashSet() }

                setNamed
                    [ "Name", (fun x -> view.Name <- Some(identifier x))
                      "Omit", (fun x -> view.Omit.AddRange(texts x.Expression))
                      "What", (fun x -> view.What <- Some(text x.Expression)) ]

                if config.Views |> Seq.exists (fun v -> v.Type = view.Type) then
                    bad a $"SeamView is written twice for {view.Type}"

                config.Views.Add view
            | "SeamField" ->
                if positional.Length <> 2 then
                    bad a "SeamField takes a record and one of its members: SeamField(typeof(YourRecord), nameof(YourRecord.Total), After = nameof(YourRecord.Lines))"

                let field =
                    { Record = typeOf positional[0].Expression
                      Member = text positional[1].Expression
                      At = at
                      After = None
                      Before = None
                      Used = false }

                setNamed
                    [ "After", (fun x -> field.After <- Some(text x.Expression))
                      "Before", (fun x -> field.Before <- Some(text x.Expression)) ]

                if field.After.IsSome && field.Before.IsSome then
                    bad a $"SeamField places {field.Record}.{field.Member} both after and before: give one"

                if config.Fields |> Seq.exists (fun f -> f.Record = field.Record && f.Member = field.Member) then
                    bad a $"SeamField is written twice for {field.Record}.{field.Member}"

                config.Fields.Add field
            | "SeamJson" ->
                setNamed []

                for t in positional do
                    let written =
                        match t.Expression with
                        | :? TypeOfExpressionSyntax as x -> x.Type.ToString()
                        | e -> bad t $"expected typeof(...), found {e}"

                    if config.Json.Contains written then
                        bad t $"SeamJson lists {written} twice"

                    config.Json.Add written
            | _ ->
                if positional.Length <> 2 then
                    bad a "SeamCase takes a union and a case: SeamCase(typeof(YourUnion), nameof(YourUnion.ACase), Name = \"...\")"

                let case =
                    { Union = typeOf positional[0].Expression
                      Case = text positional[1].Expression
                      At = at
                      Name = None
                      Kind = None
                      Used = false }

                setNamed
                    [ "Name", (fun x -> case.Name <- Some(identifier x))
                      "Kind", (fun x -> case.Kind <- Some(text x.Expression)) ]

                if config.Cases |> Seq.exists (fun c -> c.Union = case.Union && c.Case = case.Case) then
                    bad a $"SeamCase is written twice for {case.Union}.{case.Case}"

                config.Cases.Add case

    let tree =
        CSharpSyntaxTree.ParseText(System.IO.File.ReadAllText file, CSharpParseOptions(LanguageVersion.Preview), file)

    for list in tree.GetCompilationUnitRoot().AttributeLists do
        if not (isNull list.Target) && list.Target.Identifier.ValueText = "assembly" then
            for a in list.Attributes do
                attribute a

    if config.Data.Count = 0 then
        Seam.failAt "SEAM001" (Some $"{file}(1,1)") "no [assembly: SeamData(typeof(...))] names an F# data type, so there is nothing to write"

    config
