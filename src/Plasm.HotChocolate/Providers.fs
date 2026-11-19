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

/// A GraphQL union named `name` over the cases of a type of C#'s union shape
/// (a struct with [<Union>], one constructor per case, IUnion.Value the case):
/// one object type per case record, the case picked by the value's own type.
/// A case's object type is referred to by its type, so a record that is a
/// case of several unions, or a field elsewhere, is one object type.
type PlasmUnionType<'U when 'U :> IUnion>(name: string) =
    inherit UnionType()

    static member Cases =
        [ for c in typeof<'U>.GetConstructors() do
              match c.GetParameters() with
              | [| p |] -> yield p.ParameterType
              | _ -> () ]

    override _.Configure(d: IUnionTypeDescriptor) =
        d.Name(name) |> ignore

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
            |> List.fold (fun (b: IRequestExecutorBuilder) case -> b.AddType(typedefof<ObjectType<_>>.MakeGenericType case)) builder

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

    [<Extension>]
    static member AddPlasmSorting(builder: IRequestExecutorBuilder) : IRequestExecutorBuilder =
        builder.AddSorting(fun (d: ISortConventionDescriptor) ->
            d.AddDefaultOperations().BindDefaultTypes().Provider(PlasmSortProvider()) |> ignore)
