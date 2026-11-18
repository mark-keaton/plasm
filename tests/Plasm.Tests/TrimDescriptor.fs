/// The trimming descriptor in Plasm.dll (docs/aot.md) keeps every type whose
/// methods the translator recognises: a type Translate.fs names that the
/// descriptor does not keep would be trimmed from a native binary, and a query
/// using it would fail there. The translator names them by full name or by
/// module name; this reads those names from its source.
module Plasm.Tests.TrimDescriptor

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Xunit

let private repository =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "the repository root (Plasm.slnx) was not found"
        elif File.Exists(Path.Combine(d.FullName, "Plasm.slnx")) then d.FullName
        else up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// The types the descriptor keeps, by full name (nested types with '/').
let private kept =
    use stream = typeof<Plasm.Repo>.Assembly.GetManifestResourceStream "ILLink.Descriptors.xml"
    let doc = XDocument.Load stream

    let rec names (outer: string) (t: XElement) =
        let name =
            match t.Attribute(XName.Get "fullname") with
            | null -> outer + "/" + t.Attribute(XName.Get "name").Value
            | full -> full.Value

        name :: (t.Elements(XName.Get "type") |> Seq.collect (names name) |> List.ofSeq)

    doc.Descendants(XName.Get "assembly")
    |> Seq.collect (fun a -> a.Elements(XName.Get "type"))
    |> Seq.collect (names "")
    |> Set.ofSeq

/// The assemblies the descriptor keeps whole.
let private wholes =
    use stream = typeof<Plasm.Repo>.Assembly.GetManifestResourceStream "ILLink.Descriptors.xml"
    let doc = XDocument.Load stream

    doc.Descendants(XName.Get "assembly")
    |> Seq.filter (fun a -> match a.Attribute(XName.Get "preserve") with null -> false | p -> p.Value = "all")
    |> Seq.map (fun a -> a.Attribute(XName.Get "fullname").Value)
    |> Set.ofSeq

let private translator = File.ReadAllText(Path.Combine(repository, "src", "Plasm", "Translate.fs"))

[<Fact>]
let ``every type the translator names by its full name is kept`` () =
    let named =
        Regex.Matches(translator, "\"((?:Microsoft\\.FSharp|System|Plasm)\\.[A-Za-z0-9_.`]+)\"")
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> Seq.filter (fun n -> Char.IsUpper(n.Split('.') |> Array.last |> Seq.head))
        |> Set.ofSeq

    let missing = named |> Set.filter (fun n -> not (kept.Contains n) && not (wholes |> Set.exists (fun a -> n.StartsWith(a + "."))))
    Assert.True(missing.IsEmpty, $"""Translate.fs names types the trimming descriptor does not keep: {String.Join(", ", missing)}""")

[<Fact>]
let ``every module the translator names by its own name is kept`` () =
    let named =
        Regex.Matches(translator, "DeclaringType\\.Name = \"([A-Za-z0-9_]+)\"")
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> Set.ofSeq

    let keeps (name: string) =
        kept |> Set.exists (fun k -> k.EndsWith("." + name) || k.EndsWith("/" + name))

    let missing = named |> Set.filter (keeps >> not)
    Assert.True(missing.IsEmpty, $"""Translate.fs names modules the trimming descriptor does not keep: {String.Join(", ", missing)}""")

[<Fact>]
let ``every scalar type Plasm maps is kept whole, since a query may call its members`` () =
    use stream = typeof<Plasm.Repo>.Assembly.GetManifestResourceStream "ILLink.Descriptors.xml"
    let doc = XDocument.Load stream

    let whole =
        doc.Descendants(XName.Get "type")
        |> Seq.filter (fun t -> match t.Attribute(XName.Get "preserve") with null -> false | p -> p.Value = "all")
        |> Seq.choose (fun t -> match t.Attribute(XName.Get "fullname") with null -> None | n -> Some n.Value)
        |> Set.ofSeq

    // byte[] is kept with Byte.
    let scalars = Plasm.Scalar.builtIn |> Set.map (fun n -> if n = "System.Byte[]" then "System.Byte" else n)
    let missing = scalars - whole
    Assert.True(missing.IsEmpty, $"""scalar types the descriptor does not keep whole: {String.Join(", ", missing)}""")

[<Fact>]
let ``Plasm is kept whole, since quotations name its builder and its arguments`` () = Assert.Contains("Plasm", wholes)

/// `Raw.fragment "..."` puts the format's constructor in the quotation
/// (`new PrintfFormat<...>("...")`), which FSharp.Core binds when it reads it.
[<Fact>]
let ``a raw fragment's format keeps its constructor`` () =
    use stream = typeof<Plasm.Repo>.Assembly.GetManifestResourceStream "ILLink.Descriptors.xml"
    let doc = XDocument.Load stream

    let ctorKept (name: string) =
        doc.Descendants(XName.Get "type")
        |> Seq.exists (fun t ->
            (match t.Attribute(XName.Get "fullname") with null -> false | n -> n.Value = name)
            && t.Elements(XName.Get "method") |> Seq.exists (fun m -> m.Attribute(XName.Get "name").Value = ".ctor"))

    let format = <@ Plasm.Raw.fragment<bool> "{0}" 1 @>
    let named =
        match format with
        | Microsoft.FSharp.Quotations.Patterns.Call(_, _, [ Microsoft.FSharp.Quotations.Patterns.Coerce(Microsoft.FSharp.Quotations.Patterns.NewObject(c, _), _); _ ]) ->
            c.DeclaringType.GetGenericTypeDefinition().FullName
        | e -> failwith $"a raw fragment's format is not a constructor call: {e}"

    Assert.True(ctorKept named, $"the descriptor does not keep {named}'s constructor")

[<Fact>]
let ``the string methods the translator reads are kept`` () =
    Assert.True(translator.Contains "typeof<string>")
    Assert.Contains("System.String", kept)

[<Fact>]
let ``decimal literals the translator reads are kept`` () =
    Assert.True(translator.Contains "\"MakeDecimal\"")
    Assert.Contains("Microsoft.FSharp.Core.LanguagePrimitives/IntrinsicFunctions", kept)

/// The decoder of [<ReflectedDefinition>] bodies a native binary uses
/// (Definitions in src/Plasm/Shapes.fs) is FSharp.Core's own and not public:
/// this fails, naming FSharp.Core's version, if a release moves it.
module Definitions =
    open Microsoft.FSharp.Quotations
    open Plasm.TestDomain.Domain

    [<Fact>]
    let ``FSharp.Core's decoder of definitions is all there`` () = Plasm.Definitions.check ()

    [<Fact>]
    let ``a definition read without metadata tokens is the one FSharp.Core reads`` () =
        let getter = typeof<Order>.GetProperty("Large").GetGetMethod()
        let expected = Expr.TryGetReflectedDefinition getter
        Assert.True(expected.IsSome, "Order.Large has a definition")
        Assert.Equal(string expected.Value, string (Plasm.Definitions.tryFind getter).Value)
