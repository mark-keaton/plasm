namespace Plasm.HotChocolate

open System.Runtime.CompilerServices
open HotChocolate.Data
open HotChocolate.Data.Filters
open HotChocolate.Data.Sorting
open HotChocolate.Execution.Configuration
open System.Runtime.CompilerServices
open HotChocolate
open HotChocolate.Language.Visitors
open HotChocolate.Resolvers
open HotChocolate.Types
open Microsoft.Extensions.DependencyInjection
open Plasm

// Hot Chocolate's filtering and sorting providers translate a request into
// LINQ; Plasm translates it instead (PlasmRequest.Apply), so these accept
// every field and operation a generated input declares and translate none.

type PlasmFilterContext(t: IFilterInputType) =
    inherit FilterVisitorContext<obj>(t, FilterScope<obj>())

type PlasmFilterFieldHandler() =
    interface IFilterFieldHandler with
        member _.CanHandle(_, _, _) = true

    interface IFilterFieldHandler<PlasmFilterContext> with
        member _.TryHandleEnter(_, _, _, action: byref<ISyntaxVisitorAction>) =
            action <- SyntaxVisitor.Skip
            true

        member _.TryHandleLeave(_, _, _, action: byref<ISyntaxVisitorAction>) =
            action <- SyntaxVisitor.Continue
            true

type private Nothing() =
    interface IQueryBuilder with
        member _.Prepare _ = ()
        member _.Apply _ = ()

type PlasmFilterProvider() =
    inherit FilterProvider<PlasmFilterContext>(fun d -> d.AddFieldHandler(PlasmFilterFieldHandler()) |> ignore)
    override _.CreateBuilder<'E>(_: string) = Nothing() :> IQueryBuilder

type PlasmSortContext(t: ISortInputType) =
    inherit SortVisitorContext<obj>(t)

type PlasmSortFieldHandler() =
    interface ISortFieldHandler with
        member _.CanHandle(_, _, _) = true

    interface ISortFieldHandler<PlasmSortContext> with
        member _.TryHandleEnter(_, _, _, action: byref<ISyntaxVisitorAction>) =
            action <- SyntaxVisitor.Skip
            true

        member _.TryHandleLeave(_, _, _, action: byref<ISyntaxVisitorAction>) =
            action <- SyntaxVisitor.Continue
            true

type PlasmSortOperationHandler() =
    interface ISortOperationHandler with
        member _.CanHandle(_, _, _) = true

    interface ISortOperationHandler<PlasmSortContext> with
        member _.TryHandleEnter(_, _, _, _, action: byref<ISyntaxVisitorAction>) =
            action <- SyntaxVisitor.Skip
            true

type PlasmSortProvider() =
    inherit
        SortProvider<PlasmSortContext>(fun d ->
            d.AddFieldHandler(PlasmSortFieldHandler()).AddOperationHandler(PlasmSortOperationHandler()) |> ignore)

    override _.CreateBuilder<'E>(_: string) = Nothing() :> IQueryBuilder

/// Marks the object type of the fields of 'T a caller may read, so that a
/// request's selection is read off it (PlasmRequest.Chosen).
type IPlasmReadType<'T> =
    abstract Fields: ReadField<'T> list

/// The GraphQL object type of the fields of 'T a caller may read (ReadField),
/// named as the application's view 'V is and resolving each field from either:
/// a row of the fields a request chose (Selected<'T>), through the field's
/// declaration, or a whole view (a change's outcome), through the view's
/// property of the declared name. That each declared field has such a
/// property, of the declaration's type, is checked when the schema is built,
/// and refused naming the field.
type PlasmReadType<'T, 'V>(fields: ReadField<'T> list) =
    inherit ObjectType()

    static let camel (name: string) = string (System.Char.ToLowerInvariant name.[0]) + name.Substring 1

    /// The view's property for each declared field, or why there is none.
    static member Properties(fields: ReadField<'T> list) =
        [ for f in fields ->
              match typeof<'V>.GetProperties() |> Array.tryFind (fun p -> camel p.Name = f.Name) with
              | Some p when p.PropertyType = f.ValueType -> f, p
              | Some p ->
                  invalidOp
                      $"{typeof<'T>.Name}.{f.Name} is read as a {f.ValueType.Name}, but {typeof<'V>.Name}.{p.Name} is a {p.PropertyType.Name}"
              | None -> invalidOp $"{typeof<'T>.Name}.{f.Name} is read, but {typeof<'V>.Name} has no property {f.Name}" ]

    interface IPlasmReadType<'T> with
        member _.Fields = fields

    override _.Configure(d: IObjectTypeDescriptor) =
        // Named and described as the view is, as Hot Chocolate names and
        // describes a type it infers.
        let naming = d.Extend().Context.Naming

        d
            .Name(typeof<'V>.Name)
            .Description(naming.GetTypeDescription(typeof<'V>, TypeKind.Object))
            .IsOfType(fun _ value -> value :? Selected<'T> || value :? 'V)
        |> ignore

        for f, p in PlasmReadType<'T, 'V>.Properties fields do
            // Pure, as a property read is: a value already in hand, so the
            // field costs what the view's own would (no resolver weight).
            let read (context: IResolverContext) : obj =
                match context.Parent<obj>() with
                | :? Selected<'T> as row -> row.Get f
                | view -> p.GetValue view

            let field = d.Field(p).Name(f.Name)
            field.Extend().Configuration.PureResolver <- PureFieldDelegate read
            field.Extend().Configuration.Resolver <- FieldResolverDelegate(fun context -> System.Threading.Tasks.ValueTask<obj>(read context))

/// A GraphQL union named `name` over the cases of a type of C#'s union shape
/// (a struct with [<Union>], one constructor per case, IUnion.Value the case):
/// one object type per case record, the case picked by the value's own type.
/// A case's object type is the one its runtime type is bound to, so a record
/// that is a case of several unions, or a field elsewhere, is one object type.
type PlasmUnionType<'U when 'U :> IUnion>(name: string) =
    inherit UnionType()

    static member Cases =
        [ for c in typeof<'U>.GetConstructors() do
              match c.GetParameters() with
              | [| p |] -> yield p.ParameterType
              | _ -> () ]

    override _.Configure(d: IUnionTypeDescriptor) =
        d.Name(name).Description(d.Extend().Context.Naming.GetTypeDescription(typeof<'U>, TypeKind.Union)) |> ignore

        for case in PlasmUnionType<'U>.Cases do
            d.Type(Language.NamedTypeNode(case.Name)) |> ignore

/// Registers filtering and sorting for inputs generated from declared fields
/// (PlasmFilterInputType, PlasmSortInputType), with Plasm doing both.
[<Extension; AbstractClass; Sealed>]
type PlasmRequestExecutorBuilderExtensions =

    [<Extension>]
    static member AddPlasmFiltering(builder: IRequestExecutorBuilder) : IRequestExecutorBuilder =
        builder.AddFiltering(fun (d: IFilterConventionDescriptor) ->
            d.AddDefaultOperations().BindDefaultTypes().Provider(PlasmFilterProvider()) |> ignore)

    /// Shows a field of 'U, a type of C#'s union shape, as a GraphQL union
    /// over its cases, named as 'U is: Hot Chocolate makes such a struct an
    /// object type with no fields, which it then refuses.
    [<Extension>]
    static member AddPlasmUnion<'U when 'U :> IUnion>(builder: IRequestExecutorBuilder) : IRequestExecutorBuilder =
        if typeof<'U>.IsGenericType then
            invalidArg "U" $"{typeof<'U>} is generic, so it has no name GraphQL allows: give it one with AddPlasmUnion<'U>(name)."

        builder.AddPlasmUnion<'U>(typeof<'U>.Name)

    /// As AddPlasmUnion<'U>(), the union named `name`: for a generic type
    /// such as Found<CardView>, whose own name GraphQL does not allow.
    [<Extension>]
    static member AddPlasmUnion<'U when 'U :> IUnion>(builder: IRequestExecutorBuilder, name: string) : IRequestExecutorBuilder =
        let builder =
            PlasmUnionType<'U>.Cases
            |> List.fold (fun (b: IRequestExecutorBuilder) case -> b.AddType(case)) builder

        builder
            .AddType(PlasmUnionType<'U>(name))
            .BindRuntimeType(typeof<'U>, typeof<PlasmUnionType<'U>>)
            .UseField(fun next ->
                FieldDelegate(fun context ->
                    task {
                        do! next.Invoke context

                        match context.Result with
                        | :? 'U as u -> context.Result <- u.Value
                        | _ -> ()
                    }
                    |> System.Threading.Tasks.ValueTask))

    /// The object type of the fields of 'T a caller may read, named as the
    /// view 'V is, for both a request's chosen fields (Selected<'T>) and a
    /// whole 'V; each field resolves through its declaration from the one
    /// and by the property of its name from the other (PlasmReadType).
    [<Extension>]
    static member AddPlasmReadType<'T, 'V>(builder: IRequestExecutorBuilder, fields: ReadField<'T> seq) : IRequestExecutorBuilder =
        let fields = List.ofSeq fields
        // Checked here, as the schema is described, and again when it is built.
        PlasmReadType<'T, 'V>.Properties fields |> ignore

        builder
            .AddType(PlasmReadType<'T, 'V>(fields))
            .BindRuntimeType(typeof<Selected<'T>>, typeof<PlasmReadType<'T, 'V>>)
            .BindRuntimeType(typeof<'V>, typeof<PlasmReadType<'T, 'V>>)

    [<Extension>]
    static member AddPlasmSorting(builder: IRequestExecutorBuilder) : IRequestExecutorBuilder =
        builder.AddSorting(fun (d: ISortConventionDescriptor) ->
            d.AddDefaultOperations().BindDefaultTypes().Provider(PlasmSortProvider()) |> ignore)
