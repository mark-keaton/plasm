/// What a query reads of no row, computed before it runs (query-semantics.feature,
/// docs/aot.md): Plasm's own evaluator, the same under the runtime and in a
/// native binary, which decides what it computes by the expression's form.
/// It reads values and what is read from them, builds records, unions,
/// tuples, options and lists, does arithmetic and conversions on numbers, and
/// calls functions and constructors. A function or type generic over a value
/// type it refuses everywhere: a native binary has no code to run for an
/// instantiation only a quotation names, and the runtime refuses it too, so a
/// query is never accepted by one and refused by the other.
module internal Plasm.Computed

open System
open System.Reflection
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Reflection

/// `e` cannot be computed before the query runs, and why; a translator names
/// it with its own description of `e`.
exception Refused of Expr * string

let private bindOutside = "bind its value outside the query"

/// A generic instantiation a native binary has code for: none of its type
/// arguments is a value type (generic code over reference types is shared).
let private shared (args: Type[]) = args |> Array.forall (fun a -> not a.IsValueType)

let private typeArgs (t: Type) = if t.IsGenericType then t.GetGenericArguments() else [||]

let private methodArgs (m: MethodBase) =
    Array.append (typeArgs m.DeclaringType) (if m.IsGenericMethod then m.GetGenericArguments() else [||])

/// An option or a list Plasm builds itself (Shape): of a reference type, or of
/// one of the provider's value types.
let private builtByShape (t: Type) =
    (Reflection.isOption t || Reflection.isList t)
    && (let e = Reflection.elementType t
        not e.IsValueType || Shape.writtenOut e)

let private isOperator (m: MethodInfo) = m.DeclaringType.FullName = "Microsoft.FSharp.Core.Operators"

/// A member as F# source names it: `List.sum`, not `ListModule.Sum`.
let private sourceName (m: MemberInfo) =
    let t = m.DeclaringType

    let typeName =
        match Attribute.GetCustomAttribute(t, typeof<CompilationRepresentationAttribute>) with
        | :? CompilationRepresentationAttribute as a when
            a.Flags.HasFlag CompilationRepresentationFlags.ModuleSuffix && t.Name.EndsWith "Module"
            ->
            t.Name.Substring(0, t.Name.Length - "Module".Length)
        | _ -> Reflection.fsharpName t

    let memberName =
        match Attribute.GetCustomAttribute(m, typeof<CompilationSourceNameAttribute>) with
        | :? CompilationSourceNameAttribute as a -> a.SourceName
        | _ -> m.Name

    typeName + "." + memberName

let private binaryOperators =
    set [ "op_Addition"; "op_Subtraction"; "op_Multiply"; "op_Division"; "op_Modulus" ]

let private conversions =
    set [ "ToSByte"; "ToByte"; "ToInt16"; "ToUInt16"; "ToInt"; "ToInt32"; "ToUInt32"; "ToInt64"; "ToUInt64"; "ToSingle"; "ToDouble"; "ToDecimal" ]

let private unaryOperators = set [ "op_UnaryNegation"; "op_UnaryPlus"; "Abs" ]

/// The operators Plasm computes itself, over numbers (and `+` over strings):
/// F#'s are inline and generic, with no code to call in a native binary.
let private isArithmetic (m: MethodInfo) =
    isOperator m
    && (binaryOperators.Contains m.Name || unaryOperators.Contains m.Name || conversions.Contains m.Name)

let inline private binary (name: string) (x: ^T) (y: ^T) : obj =
    match name with
    | "op_Addition" -> box (x + y)
    | "op_Subtraction" -> box (x - y)
    | "op_Multiply" -> box (x * y)
    | "op_Division" -> box (x / y)
    | _ -> box (x % y)

let private arithmetic (name: string) (a: obj) (b: obj) : obj =
    match a, b with
    | (:? int as x), (:? int as y) -> binary name x y
    | (:? int64 as x), (:? int64 as y) -> binary name x y
    | (:? int16 as x), (:? int16 as y) -> binary name x y
    | (:? sbyte as x), (:? sbyte as y) -> binary name x y
    | (:? byte as x), (:? byte as y) -> binary name x y
    | (:? uint16 as x), (:? uint16 as y) -> binary name x y
    | (:? uint32 as x), (:? uint32 as y) -> binary name x y
    | (:? uint64 as x), (:? uint64 as y) -> binary name x y
    | (:? float as x), (:? float as y) -> binary name x y
    | (:? float32 as x), (:? float32 as y) -> binary name x y
    | (:? decimal as x), (:? decimal as y) -> binary name x y
    | (:? string as x), (:? string as y) when name = "op_Addition" -> box (x + y)
    | _ -> invalidOp $"{name} of {a.GetType().Name} and {b.GetType().Name}"

let inline private unary (name: string) (x: ^T) : obj =
    match name with
    | "op_UnaryNegation" -> box (-x)
    | "Abs" -> box (abs x)
    | _ -> box x

let private negate (name: string) (a: obj) : obj =
    match a with
    | :? int as x -> unary name x
    | :? int64 as x -> unary name x
    | :? int16 as x -> unary name x
    | :? sbyte as x -> unary name x
    | :? float as x -> unary name x
    | :? float32 as x -> unary name x
    | :? decimal as x -> unary name x
    | _ -> invalidOp $"{name} of {a.GetType().Name}"

let inline private convertTo (target: string) (x: ^S) : obj =
    match target with
    | "ToSByte" -> box (sbyte x)
    | "ToByte" -> box (byte x)
    | "ToInt16" -> box (int16 x)
    | "ToUInt16" -> box (uint16 x)
    | "ToInt"
    | "ToInt32" -> box (int x)
    | "ToUInt32" -> box (uint32 x)
    | "ToInt64" -> box (int64 x)
    | "ToUInt64" -> box (uint64 x)
    | "ToSingle" -> box (float32 x)
    | "ToDouble" -> box (float x)
    | _ -> box (decimal x)

let private convert (target: string) (a: obj) : obj =
    match a with
    | :? int as x -> convertTo target x
    | :? int64 as x -> convertTo target x
    | :? int16 as x -> convertTo target x
    | :? sbyte as x -> convertTo target x
    | :? byte as x -> convertTo target x
    | :? uint16 as x -> convertTo target x
    | :? uint32 as x -> convertTo target x
    | :? uint64 as x -> convertTo target x
    | :? float as x -> convertTo target x
    | :? float32 as x -> convertTo target x
    | :? decimal as x -> convertTo target x
    | :? char as x -> convertTo target (int x)
    | :? string as x -> convertTo target (Decimal.Parse(x, Globalization.CultureInfo.InvariantCulture))
    | _ -> invalidOp $"{target} of {a.GetType().Name}"

let private genericRefusal (what: string) =
    $"{what} is generic over a value type, which Plasm does not compute before the query runs (a native binary has no code for it): {bindOutside}"

/// Why `e` cannot be computed, or None: a check of its form alone, so a query
/// is refused when it is first translated (at start-up), whatever its values.
/// `leaf` marks the expressions the caller supplies (a prepared argument).
let rec refusal (leaf: Expr -> bool) (e: Expr) : (Expr * string) option =
    let all (xs: Expr list) = xs |> List.tryPick (refusal leaf)
    let generic (what: string) = Some(e, genericRefusal what)

    match e with
    | _ when leaf e -> None
    | Value _
    | ValueWithName _ -> None
    | Coerce(x, _) -> refusal leaf x
    | Call(None, m, args) when isArithmetic m -> all args
    | Call(None, m, args) when m.Name = "MakeDecimal" -> all args
    | Call(target, m, args) ->
        if not (shared (methodArgs m)) then generic (sourceName m)
        else all (Option.toList target @ args)
    // A read of a value in hand: its type was made by compiled code.
    | PropertyGet(Some target, _, args) -> all (target :: args)
    | FieldGet(Some target, _) -> refusal leaf target
    | PropertyGet(None, p, args) ->
        if not (shared (typeArgs p.DeclaringType)) then generic (sourceName p) else all args
    | FieldGet(None, f) -> if not (shared (typeArgs f.DeclaringType)) then generic (sourceName f) else None
    | NewObject(c, args) ->
        if not (shared (typeArgs c.DeclaringType)) then generic (Reflection.fsharpName c.DeclaringType)
        else all args
    | NewUnionCase(c, args) ->
        if not (shared (typeArgs c.DeclaringType)) && not (builtByShape c.DeclaringType) then
            generic (Reflection.fsharpName c.DeclaringType)
        else
            all args
    | NewRecord(t, args) -> if not (shared (typeArgs t)) then generic (Reflection.fsharpName t) else all args
    | NewTuple args -> all args
    | _ -> Some(e, $"it is computed before the query runs, and Plasm computes values, records, unions, tuples, options, lists, arithmetic and calls, not this: {bindOutside}")

/// The option `t`'s case `name` from its fields.
let private optionCase (t: Type) (name: string) (fields: obj[]) : obj =
    if name = "None" then null else (Shape.option (Reflection.elementType t)).Some fields.[0]

let private listCase (t: Type) (name: string) (fields: obj[]) : obj =
    let shape = Shape.list (Reflection.elementType t)
    if name = "Empty" then shape.Make Seq.empty else shape.Make(Seq.append [ fields.[0] ] (shape.Items fields.[1]))

let private invoke (f: unit -> obj) =
    try
        f ()
    with :? TargetInvocationException as ex when not (isNull ex.InnerException) ->
        raise ex.InnerException

/// The value of `e`, whose form `refusal` accepts; `leaf` supplies the
/// caller's leaves (a prepared argument's value).
let rec eval (leaf: Expr -> obj option) (e: Expr) : obj =
    let ev = eval leaf

    match leaf e with
    | Some v -> v
    | None ->
        match e with
        | Value(v, _)
        | ValueWithName(v, _, _) -> v
        | Coerce(x, _) -> ev x
        | Call(None, m, [ a; b ]) when isOperator m && binaryOperators.Contains m.Name -> arithmetic m.Name (ev a) (ev b)
        | Call(None, m, [ a ]) when isOperator m && unaryOperators.Contains m.Name -> negate m.Name (ev a)
        | Call(None, m, [ a ]) when isOperator m && conversions.Contains m.Name -> convert m.Name (ev a)
        | Call(None, m, [ Value(lo, _); Value(mid, _); Value(hi, _); Value(neg, _); Value(scale, _) ]) when m.Name = "MakeDecimal" ->
            box (Decimal(unbox<int> lo, unbox<int> mid, unbox<int> hi, unbox<bool> neg, unbox<byte> scale))
        | Call(target, m, args) ->
            let o = target |> Option.map ev |> Option.toObj
            let vs = args |> List.map ev |> Array.ofList
            invoke (fun () -> m.Invoke(o, vs))
        | PropertyGet(target, p, args) when Reflection.isOption p.DeclaringType ->
            let o = target |> Option.map ev |> Option.toObj
            let value = (Shape.option (Reflection.elementType p.DeclaringType)).Value o

            match p.Name with
            | "Value" -> (match value with Some v -> v | None -> raise (NullReferenceException "the option is None"))
            | "IsSome" -> box value.IsSome
            | "IsNone" -> box value.IsNone
            | _ -> invoke (fun () -> p.GetValue(o, args |> List.map ev |> Array.ofList))
        | PropertyGet(target, p, args) ->
            let o = target |> Option.map ev |> Option.toObj
            invoke (fun () -> p.GetValue(o, args |> List.map ev |> Array.ofList))
        | FieldGet(target, f) -> f.GetValue(target |> Option.map ev |> Option.toObj)
        | NewObject(c, args) -> invoke (fun () -> c.Invoke(args |> List.map ev |> Array.ofList))
        | NewUnionCase(c, args) ->
            let vs = args |> List.map ev |> Array.ofList
            let t = c.DeclaringType

            if Reflection.isOption t then optionCase t c.Name vs
            elif Reflection.isList t then listCase t c.Name vs
            else (Shape.union t).Cases.[c.Tag].Make vs
        | NewRecord(t, args) -> (Shape.record t).Construct(args |> List.map ev |> Array.ofList)
        | NewTuple args -> Shape.tuple e.Type (args |> List.map ev |> Array.ofList)
        | _ -> raise (Refused(e, "unreachable: refusal accepts no other form"))

/// The value of `e`, or Refused naming the part Plasm does not compute.
let compute (leaf: Expr -> obj option) (isLeaf: Expr -> bool) (e: Expr) : obj =
    match refusal isLeaf e with
    | Some(part, why) -> raise (Refused(part, why))
    | None -> eval leaf e
