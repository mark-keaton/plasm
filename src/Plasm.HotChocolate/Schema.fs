namespace Plasm.HotChocolate

open System
open HotChocolate.Data.Filters
open HotChocolate.Data.Sorting
open HotChocolate.Types
open Plasm

module internal Names =
    /// The GraphQL scalar for a value a field is given as.
    let scalar (t: Type) : Type =
        if t = typeof<string> then typeof<StringType>
        elif t = typeof<int> then typeof<IntType>
        elif t = typeof<int64> then typeof<LongType>
        elif t = typeof<int16> then typeof<ShortType>
        elif t = typeof<decimal> then typeof<DecimalType>
        elif t = typeof<float> then typeof<FloatType>
        elif t = typeof<bool> then typeof<BooleanType>
        elif t = typeof<Guid> then typeof<UuidType>
        elif t = typeof<DateTimeOffset> || t = typeof<DateTime> then typeof<DateTimeType>
        elif t = typeof<DateOnly> then typeof<LocalDateType>
        else invalidArg "field" $"a declared field given as {t.Name} has no GraphQL scalar here; declare it given as a string, a number, a Guid or a date"

    /// Hot Chocolate's operation for a Plasm operator, and its name in a request.
    let operation (op: QueryOperator) =
        match op with
        | QueryOperator.Equal -> DefaultFilterOperations.Equals
        | QueryOperator.NotEqual -> DefaultFilterOperations.NotEquals
        | QueryOperator.GreaterThan -> DefaultFilterOperations.GreaterThan
        | QueryOperator.GreaterThanOrEqual -> DefaultFilterOperations.GreaterThanOrEquals
        | QueryOperator.LessThan -> DefaultFilterOperations.LowerThan
        | QueryOperator.LessThanOrEqual -> DefaultFilterOperations.LowerThanOrEquals

    let pascal (s: string) = if s = "" then s else string (Char.ToUpperInvariant s.[0]) + s.Substring 1

/// The operations one declared field takes, each typed as the value it is given.
type PlasmOperationFilterInputType(name: string, valueType: Type, operators: QueryOperator list) =
    inherit FilterInputType()

    override _.Configure(d: IFilterInputTypeDescriptor) =
        d.Name(name).AllowAnd(false).AllowOr(false) |> ignore

        for op in operators do
            d.Operation(Names.operation op).Type(Names.scalar valueType) |> ignore

/// A filter input generated from declared fields: each field with the
/// operators it was declared with, and and / or lists of the same input. A
/// host subclasses it with a parameterless constructor for UseFiltering.
type PlasmFilterInputType<'T>(name: string, fields: QueryField<'T> seq) =
    inherit FilterInputType()
    let fields = List.ofSeq fields

    override _.Configure(d: IFilterInputTypeDescriptor) =
        d.Name name |> ignore

        for f in fields do
            let operations = PlasmOperationFilterInputType(typeof<'T>.Name + Names.pascal f.Name + "Filter", f.ValueType, f.Operators)
            d.Field(f.Name).Type(operations) |> ignore

/// A sort input generated from declared fields: each one declared orderable.
type PlasmSortInputType<'T>(name: string, fields: QueryField<'T> seq) =
    inherit SortInputType()
    let fields = List.ofSeq fields

    override _.Configure(d: ISortInputTypeDescriptor) =
        d.Name name |> ignore

        for f in fields do
            if f.Orderable then
                d.Field(f.Name).Type<DefaultSortEnumType>() |> ignore
