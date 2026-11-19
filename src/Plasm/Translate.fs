/// Translation of F# quotations into the SQL AST.
///
/// Rules:
/// - A subexpression that does not reference a query row variable is *closed*:
///   it is evaluated once, during translation, and sent as a parameter.
/// - A row-dependent subexpression must translate to SQL. Otherwise translation
///   fails with `UnsupportedExpression`; nothing is evaluated client-side.
module internal Plasm.Translate

open System
open System.Collections
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Reflection

exception TranslationFailure of TranslationError

/// A source in the query's variable space. `Optional` marks a left-joined
/// source, which F# code sees as `'T option`.
type Binding =
    { Alias: string
      Schema: SchemaInfo
      Optional: bool }

/// The sources a tuple-typed row variable stands for (after joins). The
/// computation expression gives a flat tuple (o, l, p).
type Space =
    | Source of Binding
    | Sources of Space list

/// The names of a statement's sources (readable-sql.feature).
module Naming =
    /// The names Plasm gives the tables it derives: a load's numbered rows,
    /// a distinct selection, a counted page.
    let derived = set [ "ranked"; "d"; "q" ]

    /// A variable the query's author wrote: not one the F# compiler made for a
    /// tuple or `_` pattern, nor one of Plasm's own (a tie's).
    let written (v: Var) =
        not (v.Name.StartsWith "_") && v.Name <> "tupledArg" && not (v.Name.Contains "@")

    /// The variable of a lambda over one record of type `t`.
    let over (t: Type) (lambda: Expr) =
        match lambda with
        | Lambda(v, _) when v.Type = t && written v -> Some v
        | _ -> None

    /// The variable that reads a query's record: `for c in ...`, as the first
    /// lambda over it has it.
    let root (def: QueryDef) (select: Expr option) =
        match def.Return with
        | Some r when written r.Root -> Some r.Root
        | _ ->
            [ yield! def.Wheres
              yield! def.Orders |> List.map fst
              match def.Joins with
              | j :: _ -> yield j.OuterKey
              | [] -> ()
              yield! def.GroupBy
              yield! Option.toList select ]
            |> List.tryPick (over def.Root)

type Ctx =
    { Model: Model
      Vars: Map<Var, Binding>
      /// Tuple-typed row variables (after joins).
      Spaces: Map<Var, Space>
      /// Variables standing for a document or one of its elements (a lambda
      /// over a [<Json>] list, json-columns.feature): its SQL and its shape.
      Docs: Map<Var, Sql.Expr * JsonShape>
      /// The names the statement's sources have taken (readable-sql.feature).
      Taken: Collections.Generic.HashSet<string>
      /// Set when a closed subexpression may evaluate differently next time
      /// (e.g. DateTime.UtcNow), which makes the translated plan non-reusable.
      Volatile: bool ref }

    /// A source's name: the variable that reads it, or `otherwise` (its
    /// table) when it has none, numbered if a source of the statement or a
    /// table Plasm derives already has it (o, o2).
    member c.Name(v: Var option, otherwise: string) =
        let wanted =
            match v with
            | Some v when Naming.written v -> v.Name
            | _ -> otherwise

        let mutable name = wanted
        let mutable n = 1

        while c.Taken.Contains name || Naming.derived.Contains name do
            n <- n + 1
            name <- $"{wanted}{n}"

        c.Taken.Add name |> ignore
        name

let newCtxWith model (vol: bool ref) =
    { Model = model
      Vars = Map.empty
      Spaces = Map.empty
      Docs = Map.empty
      Taken = Collections.Generic.HashSet<string>()
      Volatile = vol }

let newCtx model = newCtxWith model (ref false)

// ---------------------------------------------------------------------------
// Compact expression printer for error messages
// ---------------------------------------------------------------------------

/// Shows an expression in a message the same under the runtime and in a
/// native binary: values through Reflection.Shown, never printf or a
/// quotation's ToString, which differ between the two (docs/aot.md).
let rec show (e: Expr) : string =
    match e with
    | Var v -> v.Name
    | Value(v, t) when FSharpType.IsFunction t ->
        // A captured local function: show its source name, not the closure type.
        let n = v.GetType().Name
        if n.Contains "@" then n.Substring(0, n.IndexOf '@') else n
    | Value(v, t) -> Reflection.Shown.typed t v
    // A decimal literal: F# quotes it as the decimal's construction.
    | Call(None, mi, [ Value(lo, _); Value(mid, _); Value(hi, _); Value(neg, _); Value(scale, _) ]) when
        mi.Name = "MakeDecimal" && mi.DeclaringType.Name = "IntrinsicFunctions"
        ->
        Reflection.Shown.value (box (Decimal(unbox<int> lo, unbox<int> mid, unbox<int> hi, unbox<bool> neg, unbox<byte> scale)))
    | ValueWithName(_, _, n) -> n
    | PropertyGet(Some o, p, []) -> $"{show o}.{p.Name}"
    | PropertyGet(None, p, []) -> p.Name
    | Lambda(v, b) -> $"fun {v.Name} -> {show b}"
    | IfThenElse(UnionCaseTest(x, _), _, _) -> $"a match on {show x}"
    | IfThenElse(a, b, Bool false) -> $"{show a} && {show b}"
    | IfThenElse(a, Bool true, b) -> $"{show a} || {show b}"
    | Call(None, mi, [ a; b ]) when mi.Name.StartsWith "op_" ->
        let op =
            match mi.Name with
            | "op_Equality" -> "="
            | "op_Inequality" -> "<>"
            | "op_LessThan" -> "<"
            | "op_LessThanOrEqual" -> "<="
            | "op_GreaterThan" -> ">"
            | "op_GreaterThanOrEqual" -> ">="
            | "op_Addition" -> "+"
            | "op_Subtraction" -> "-"
            | "op_Multiply" -> "*"
            | "op_Division" -> "/"
            | "op_PipeRight" -> "|>"
            | n -> n

        $"{show a} {op} {show b}"
    | Call(None, mi, [ a ]) when mi.Name = "MakeDecimal" -> show a
    | Call(None, mi, args) when mi.IsDefined(typeof<CompilationSourceNameAttribute>, false) ->
        // An F# module function as written: List.length payments.
        let m = mi.DeclaringType.Name
        let m = if m.EndsWith "Module" then m.Substring(0, m.Length - 6) else m
        let name = (mi.GetCustomAttributes(typeof<CompilationSourceNameAttribute>, false).[0] :?> CompilationSourceNameAttribute).SourceName
        String.concat " " ($"{m}.{name}" :: (args |> List.map show))
    | Call(None, mi, args) -> $"""{mi.Name} ({args |> List.map show |> String.concat ", "})"""
    | Call(Some o, mi, args) -> $"""{show o}.{mi.Name}({args |> List.map show |> String.concat ", "})"""
    | NewUnionCase(c, []) -> c.Name
    | NewUnionCase(c, args) -> $"""{c.Name}({args |> List.map show |> String.concat ", "})"""
    | NewTuple args -> $"""({args |> List.map show |> String.concat ", "})"""
    | Application(f, x) -> $"{show f} {show x}"
    | Let(v, a, b) -> $"let {v.Name} = {show a} in {show b}"
    | _ -> "an expression of type " + Reflection.fsharpTypeName e.Type

let private fail (e: Expr) reason =
    raise (TranslationFailure(TranslationError.UnsupportedExpression(show e, reason)))

let private invalid msg = raise (TranslationFailure(TranslationError.InvalidQuery msg))

/// A call of an FSharp.Core operator (`=`, `<>`, `not`), by its compiled name.
/// FSharp.Core's SpecificCall compares metadata tokens, and a native binary
/// has none (docs/aot.md).
let private (|OperatorCall|_|) (name: string) (e: Expr) =
    match e with
    | Call(None, mi, args) when mi.Name = name && mi.DeclaringType.FullName = "Microsoft.FSharp.Core.Operators" -> Some args
    | _ -> None

// ---------------------------------------------------------------------------
// Normalisation: inline pipes, applications and let-bindings.
// ---------------------------------------------------------------------------

/// The body of a member or function marked [<ReflectedDefinition>]: the
/// domain's own vocabulary (`c.Assignee`), inlined so a query can use it
/// (union-fields.feature). Cached per method.
let private reflectedBodies = Collections.Concurrent.ConcurrentDictionary<Reflection.MethodBase, Expr option>()

let private reflectedBody (m: Reflection.MethodBase) =
    if isNull m then
        None
    else
        reflectedBodies.GetOrAdd(
            m,
            fun m ->
                try
                    Expr.TryGetReflectedDefinition m
                // A native binary has no metadata tokens, by which FSharp.Core
                // finds a definition (docs/aot.md): found by name instead.
                with :? InvalidOperationException ->
                    try
                        Definitions.tryFind m
                    with ex ->
                        let rec innermost (e: exn) = if isNull e.InnerException then e else innermost e.InnerException

                        raise (
                            TranslationFailure(
                                TranslationError.UnsupportedExpression(
                                    m.DeclaringType.Name + "." + m.Name,
                                    $"its [<ReflectedDefinition>] body could not be read in this native binary ({innermost(ex).Message}); a body that reads a union case's field cannot be read there yet (docs/aot.md)"
                                )
                            )
                        )
        )

/// The quoted body of a query that binds loads (loads.feature): read by its
/// own builder, names and all, so normalising leaves it as written.
let private isLoadsBody (e: Expr) =
    match e with
    | QuoteTyped x
    | QuoteRaw x ->
        match Reflection.functionResult x.Type with
        | Some r -> r.IsGenericType && r.GetGenericTypeDefinition() = typedefof<Loads<_>>
        | None -> false
    | _ -> false

/// True when `normalize` would change something (avoids rebuilding trees).
let rec private needsNormalize (e: Expr) =
    match e with
    | _ when isLoadsBody e -> false
    | Call(None, mi, [ _; _ ]) when mi.Name = "op_PipeRight" || mi.Name = "op_PipeLeft" -> true
    | PropertyGet(_, p, []) when (reflectedBody p.GetMethod).IsSome -> true
    | Call(_, mi, _) when (reflectedBody mi).IsSome -> true
    | Application _
    | Let _ -> true
    | ExprShape.ShapeVar _ -> false
    | ExprShape.ShapeLambda(_, b) -> needsNormalize b
    | ExprShape.ShapeCombination(_, args) -> List.exists needsNormalize args

/// Applies a reflected definition (a chain of lambdas) to its arguments.
let rec private applyReflected (body: Expr) (args: Expr list) =
    match body, args with
    | Lambda(v, b), a :: rest -> applyReflected (b.Substitute(fun v2 -> if v2 = v then Some a else None)) rest
    | Lambda(v, b), [] when v.Type = typeof<unit> -> b
    | b, _ -> b

let rec normalize (e: Expr) : Expr =
    if not (needsNormalize e) then e else
    match e with
    | _ when isLoadsBody e -> e
    | Call(None, mi, [ x; f ]) when mi.Name = "op_PipeRight" -> normalize (Expr.Application(f, x))
    | Call(None, mi, [ f; x ]) when mi.Name = "op_PipeLeft" -> normalize (Expr.Application(f, x))
    | Application(f, x) ->
        match normalize f with
        | Lambda(v, body) -> normalize (body.Substitute(fun v2 -> if v2 = v then Some x else None))
        | f' -> Expr.Application(f', normalize x)
    | Let(v, value, body) -> normalize (body.Substitute(fun v2 -> if v2 = v then Some value else None))
    // A reflected member or function: its body, applied to the target and arguments.
    | PropertyGet(target, p, []) when (reflectedBody p.GetMethod).IsSome ->
        normalize (applyReflected (reflectedBody p.GetMethod).Value (Option.toList target))
    | Call(target, mi, args) when (reflectedBody mi).IsSome ->
        normalize (applyReflected (reflectedBody mi).Value (Option.toList target @ args))
    | ExprShape.ShapeVar _ -> e
    | ExprShape.ShapeLambda(v, b) -> Expr.Lambda(v, normalize b)
    | ExprShape.ShapeCombination(o, args) -> Reflection.Quoted.rebuild e o (List.map normalize args)

/// `referring<T> o` and `referredBy<T> o` (referring.feature): the marker
/// condition `Refers(x, o)` or `ReferredBy(x, o)` as the equality the model
/// proves, `x.OrderId = o.Id` (or `= Some o.Id` for an optional field). The key
/// is never the tie, so a record referring to its own kind reads its children
/// one way and its parent the other. Not exactly one candidate is refused.
let private equalsDef =
    match <@ 1 = 1 @> with
    | Call(None, mi, _) -> mi.GetGenericMethodDefinition()
    | _ -> failwith "unreachable"

let expandTie (model: Model) (mi: Reflection.MethodInfo) (inner: Expr) (outer: Expr) : Expr =
    let outer =
        match outer with
        | Coerce(x, _) -> x
        | x -> x

    let referring = mi.Name = "Refers"
    let op = if referring then "referring" else "referredBy"
    let target = inner.Type

    let schemaOf (t: Type) =
        match model.Schema t with
        | Ok s -> s
        | Error _ ->
            let name =
                if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
                    $"{t.GetGenericArguments().[0].Name} option (pass the record, not the option)"
                else
                    t.Name

            invalid $"{op}<{target.Name}> takes a stored record: {name} is not one"

    let ts = schemaOf target
    let os = schemaOf outer.Type
    // The record whose key is held, and the record whose field holds it.
    let keyed, holder = if referring then os, ts else ts, os
    let keyType = keyed.Key.ScalarType
    // The tie is a field the model counts as a reference to the keyed record:
    // never the key, never one marked [<NotReference>], never a built-in type
    // (an int64 says nothing about whose key it holds).
    let candidates = holder.Fields |> Array.filter (fun f -> f.References = Some keyed.Type)
    let article (n: string) = if "AEIOU".Contains n.[0] then "an" else "a"
    let byHand = $"write the tie by hand in a where, comparing the field of {holder.Name} with {keyed.Name}.{keyed.Key.Name}"

    let field =
        match candidates with
        | [| f |] -> f
        | [||] ->
            let why =
                if Scalar.isBuiltIn keyType then
                    $", as {article keyed.Name} {keyed.Name}'s key is {article (Reflection.fsharpName keyType)} {Reflection.fsharpName keyType}: only a typed id says whose key a field holds"
                else
                    $" (a field of type {keyType.Name}, not marked [<NotReference>])"

            invalid $"{op}<{target.Name}> needs one field of {holder.Name} referring to {keyed.Name}: {holder.Name} has no field referring to {article keyed.Name} {keyed.Name}{why}; {byHand}"
        | many ->
            let names = many |> Array.map (fun f -> f.Name) |> String.concat " and "
            invalid $"{op}<{target.Name}> needs one field of {holder.Name} referring to {keyed.Name}: {holder.Name} has {many.Length} fields holding {article keyType.Name} {keyType.Name}, {names}; Plasm does not pick one, so {byHand}"

    let key, held = (if referring then outer else inner), (if referring then inner else outer)
    let keyExpr = Reflection.Quoted.propertyGet key (keyed.Type.GetProperty keyed.Key.Name)
    let heldExpr = Reflection.Quoted.propertyGet held (holder.Type.GetProperty field.Name)

    let keyExpr =
        if field.IsOption then
            let t = typedefof<option<_>>.MakeGenericType keyExpr.Type
            Expr.NewUnionCase(FSharpType.GetUnionCases t |> Array.find (fun c -> c.Name = "Some"), [ keyExpr ])
        else
            keyExpr

    Expr.Call(equalsDef.MakeGenericMethod heldExpr.Type, [ heldExpr; keyExpr ])

/// `e` with each tie marker replaced by its equality.
let rec expandTies (model: Model) (e: Expr) : Expr =
    match e with
    | Call(None, mi, [ a; b ]) when Ties.isMarker mi -> expandTie model mi a b
    | ExprShape.ShapeVar _ -> e
    | ExprShape.ShapeLambda(v, b) -> Expr.Lambda(v, expandTies model b)
    | ExprShape.ShapeCombination(o, args) -> Reflection.Quoted.rebuild e o (List.map (expandTies model) args)

let isAggCall (mi: Reflection.MethodInfo) = mi.DeclaringType.FullName = "Plasm.Agg"
let isRawCall (mi: Reflection.MethodInfo) = mi.DeclaringType.FullName = "Plasm.Raw"

/// Does `e` mention a query row variable (or an aggregate or SQL fragment,
/// which are SQL by definition)? Allocation-free walk; row variables are bound
/// by the translator, never inside the expression, so shadowing does not arise.
let isExistsCall (mi: Reflection.MethodInfo) =
    mi.Name = "exists" && mi.DeclaringType.FullName = "Plasm.QueryBuilderInstance"

let rec private mentions (ctx: Ctx) (e: Expr) =
    match e with
    | Call(None, mi, _) when isAggCall mi || isRawCall mi || isExistsCall mi -> true
    | ExprShape.ShapeVar v -> ctx.Vars.ContainsKey v || ctx.Spaces.ContainsKey v || ctx.Docs.ContainsKey v
    | ExprShape.ShapeLambda(_, b) -> mentions ctx b
    | ExprShape.ShapeCombination(_, args) -> List.exists (mentions ctx) args

let isRowDependent (ctx: Ctx) (e: Expr) = mentions ctx e

/// True for values whose contents cannot change: primitives, strings and
/// immutable F# data (lists, options, sets, maps, tuples, unions and records
/// without mutable fields) built from them. Arrays, collections such as
/// ResizeArray, and reference cells can change after the query is built.
let rec immutable (v: obj) =
    match v with
    | null -> true
    | :? string
    | :? decimal
    | :? DateTime
    | :? DateTimeOffset
    | :? DateOnly
    | :? TimeOnly
    | :? TimeSpan
    | :? Guid -> true
    | _ ->
        let t = v.GetType()

        if t.IsPrimitive || t.IsEnum then
            true
        elif t.IsArray then
            false
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<int>> then
            v :?> System.Collections.IEnumerable |> Seq.cast<obj> |> Seq.forall immutable
        elif Reflection.isOption t then
            immutable ((Shape.option (Reflection.elementType t)).Value v |> Option.toObj)
        elif (v :? System.Runtime.CompilerServices.ITuple) && FSharpType.IsTuple t then
            let tuple = v :?> System.Runtime.CompilerServices.ITuple
            Seq.init tuple.Length (fun i -> tuple.[i]) |> Seq.forall immutable
        elif Shape.isRecord t then
            let shape = Shape.record t
            (shape.Fields |> Array.forall (fun p -> not p.CanWrite)) && (shape.Deconstruct v |> Array.forall immutable)
        elif (not (isNull t.BaseType) && Shape.isUnion t.BaseType) || Shape.isUnion t then
            // A case with fields may be a class of its own, under the union's type.
            let u = Shape.union (if not (isNull t.BaseType) && Shape.isUnion t.BaseType then t.BaseType else t)
            let tag = u.Tag v
            (u.Cases |> Array.find (fun c -> c.Tag = tag)).Read v |> Array.forall immutable
        elif t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<Set<int>> || t.GetGenericTypeDefinition() = typedefof<Map<int, int>>) then
            v :?> System.Collections.IEnumerable |> Seq.cast<obj> |> Seq.forall immutable
        else
            false

/// True for closed expressions whose value cannot change between evaluations:
/// literals, captured values, immutable module values and constructors of them.
let rec isStable (e: Expr) =
    match e with
    | Value(v, _)
    | ValueWithName(v, _, _) -> immutable v
    // An F# module value (let-bound at module level, or in F# Interactive)
    // whose current value is immutable. Other static properties, such as
    // DateTime.UtcNow, compute a value on every read.
    | PropertyGet(None, p, []) ->
        not p.CanWrite
        && (p.GetCustomAttributes(typeof<CompilationMappingAttribute>, false)
            |> Array.exists (fun a ->
                (a :?> CompilationMappingAttribute).SourceConstructFlags.HasFlag SourceConstructFlags.Value))
        && immutable (p.GetValue null)
    | NewUnionCase(_, args)
    | NewTuple args
    | NewRecord(_, args) -> List.forall isStable args
    | Call(None, mi, args) when mi.Name = "MakeDecimal" -> List.forall isStable args
    | Coerce(x, _) -> isStable x
    | _ -> false

/// Every character .NET's `Trim` removes (`Char.IsWhiteSpace`), for the SQL
/// trims (query-semantics.feature).
let private dotnetWhiteSpace: obj =
    box (String([| for c in 0 .. 0xFFFF do if Char.IsWhiteSpace(char c) then yield char c |]))

/// The value of what a condition reads of no row, computed by Plasm the same
/// under the runtime and natively (Computed, query-semantics.feature).
let evaluate (ctx: Ctx) (e: Expr) : obj =
    if not (isStable e) then
        ctx.Volatile.Value <- true

    try
        Computed.compute (fun _ -> None) (fun _ -> false) e
    with
    | Computed.Refused(part, why) -> fail part why
    | ex -> fail e $"captured expression could not be evaluated: {Err.describeExn ex}"

let private unwrapOption (t: Type) (v: obj) =
    if Reflection.isOption t then
        Reflection.getOptionValue t v
    else if isNull v then
        None
    else
        Some v

let private scalarType (t: Type) =
    if Reflection.isOption t then Reflection.elementType t else t

/// A parameter of a domain type, cast to the database type the type is stored
/// as when it has one (Scalar.asPgEnum).
let private typedParam (model: Model) (domainType: Type) (p: Sql.Expr) =
    match model.StoreTypeOf domainType with
    | Some t -> Sql.Cast(p, t, false)
    | None -> p

let toParam (ctx: Ctx) (e: Expr) (domainType: Type) (value: obj) : Sql.Expr =
    match ctx.Model.ScalarToDb(domainType, value) with
    | Ok v -> typedParam ctx.Model domainType (Sql.Param v)
    | Error msg -> fail e msg

// ---------------------------------------------------------------------------
// Prepared-query arguments
// ---------------------------------------------------------------------------

let mentionsArg (e: Expr) = Prepared.mentionsArg e

/// A function of the argument array computing a closed expression over a
/// prepared query's arguments, checked when the query is planned (Computed).
let private argFunction (e: Expr) : obj[] -> obj =
    let (|ArgValue|_|) (x: Expr) =
        match x with
        | PropertyGet(Some(Value(:? IArg as a, _) | ValueWithName(:? IArg as a, _, _)), p, []) when p.Name = "Value" -> Some a
        | _ -> None

    match e with
    | ArgValue a -> fun args -> args.[a.Index]
    | _ ->
        let rec bare (x: Expr) =
            match x with
            | ArgValue _ -> ()
            | Value(:? IArg, _)
            | ValueWithName(:? IArg, _, _) -> fail e "a prepared-query argument must be read with .Value"
            | ExprShape.ShapeVar _ -> ()
            | ExprShape.ShapeLambda(_, b) -> bare b
            | ExprShape.ShapeCombination(_, xs) -> List.iter bare xs

        bare e

        match Computed.refusal (fun x -> (|ArgValue|_|) x |> Option.isSome) e with
        | Some(part, why) -> fail part why
        | None -> ()

        fun args ->
            Computed.eval
                (fun x ->
                    match x with
                    | ArgValue a -> Some args.[a.Index]
                    | _ -> None)
                e

/// The refusal of a null where F# has no absent value (query-semantics.feature).
let private nullMessage (what: string) (e: Expr) =
    $"the {what} {show e} is null, and a {Reflection.fsharpName e.Type} has no absent value in F#: compared with null, every row is unequal, which SQL cannot say. An option says a value may be absent: compare with an option, or leave the condition out"

/// A parameter bound from the arguments each time the query runs. `transform`
/// adjusts the domain value (e.g. LIKE escaping) before conversion.
let private argParamWith (ctx: Ctx) (e: Expr) (valueType: Type) (transform: obj -> obj) : Sql.Expr =
    let f = argFunction e
    let model = ctx.Model
    let scalarT = if Reflection.isOption valueType then Reflection.elementType valueType else valueType

    let resolve (args: obj[]) =
        let v = f args
        let inner =
            if Reflection.isOption valueType then Reflection.getOptionValue valueType v
            elif isNull v then fail e (nullMessage "argument" e)
            else Some v

        match inner with
        | None -> box DBNull.Value
        | Some x ->
            match model.ScalarToDb(scalarT, transform x) with
            | Ok p -> p
            | Error m -> invalidOp m

    typedParam model scalarT (Sql.Param(box (Sql.Bound(resolve, show e))))

let private argParam ctx (e: Expr) = argParamWith ctx e e.Type id

// ---------------------------------------------------------------------------
// Values stored as text from a finite table (Scalar.enum)
// ---------------------------------------------------------------------------

/// `CASE col WHEN 'a' THEN 0 WHEN 'b' THEN 1 ... END`: a stored name's position
/// in F#'s order, so SQL compares the values as F# does rather than as text.
let private positionOf (names: string list) (stored: Sql.Expr) : Sql.Expr =
    let quote (n: string) = "'" + n.Replace("'", "''") + "'"

    Sql.Fragment(
        [ Choice1Of2 "CASE "; Choice2Of2 stored ]
        @ (names |> List.mapi (fun i n -> Choice1Of2 $" WHEN {quote n} THEN {i}"))
        @ [ Choice1Of2 " END" ]
    )

/// `(ARRAY['a', 'b', ...])[position + 1]`: the stored name at a position.
let private nameAt (names: string list) (position: Sql.Expr) : Sql.Expr =
    let quote (n: string) = "'" + n.Replace("'", "''") + "'"
    let array = names |> List.map quote |> String.concat ", "
    Sql.Fragment [ Choice1Of2 $"(ARRAY[{array}])["; Choice2Of2 position; Choice1Of2 " + 1]" ]

// ---------------------------------------------------------------------------
// Row field resolution
// ---------------------------------------------------------------------------

/// A whole source of the variable space: a row variable, or an element of a
/// joined tuple.
let (|RowBinding|_|) (ctx: Ctx) (e: Expr) =
    match e with
    | Var v -> ctx.Vars.TryFind v
    | TupleGet _ ->
        // Follow nested TupleGets down to the row variable, then back up.
        let rec space (e: Expr) =
            match e with
            | Var t -> ctx.Spaces.TryFind t
            | TupleGet(inner, i) ->
                match space inner with
                | Some(Sources items) -> List.tryItem i items
                | _ -> None
            | _ -> None

        match space e with
        | Some(Source b) -> Some b
        | _ -> None
    | _ -> None

let rec (|RowField|_|) (ctx: Ctx) (e: Expr) =
    match e with
    | PropertyGet(Some(RowBinding ctx b), prop, []) when not b.Optional ->
        b.Schema.TryField prop.Name |> Option.map (fun f -> b, f)
    // A field of a value stored in the row (value-records.feature): its own column.
    | PropertyGet(Some(RowField ctx (b, f)), prop, []) when not f.IsOption ->
        f.Value |> Option.bind (fun v -> v.Sub prop.Name) |> Option.map (fun sf -> b, sf)
    // A field of an optional value, through .Value: its column (NULL when absent).
    | PropertyGet(Some(PropertyGet(Some(RowField ctx (b, f)), v, [])), prop, []) when f.IsOption && v.Name = "Value" ->
        f.Value |> Option.bind (fun l -> l.Sub prop.Name) |> Option.map (fun sf -> b, sf)
    | _ -> None

/// A document, or a part of one, in the row (json-columns.feature): its SQL
/// and its shape. Reading through an optional document's .Value is refused.
let rec (|JsonPath|_|) (ctx: Ctx) (e: Expr) : (Sql.Expr * JsonShape) option =
    match e with
    | RowField ctx (b, f) when f.Json.IsSome && not f.IsOption -> Some(Sql.Column(b.Alias, f.Column), f.Json.Value)
    | Var v when ctx.Docs.ContainsKey v -> Some ctx.Docs.[v]
    | PropertyGet(Some(PropertyGet(Some(RowField ctx (b, f)), v, [])), _, []) when f.Json.IsSome && f.IsOption && v.Name = "Value" ->
        raise (
            TranslationFailure(
                TranslationError.UnsupportedExpression(
                    string e,
                    $"{b.Schema.Name}.{f.Name} is an optional document, whose fields cannot be read in a query; test {b.Schema.Name}.{f.Name}.IsSome in the query and read the field from the records it returns"
                )
            )
        )
    | PropertyGet(Some(JsonPath ctx (doc, JRecord(_, keys, _, _))), prop, []) ->
        keys |> List.tryFind (fun k -> k.Field = prop.Name) |> Option.map (fun k -> Sql.JsonMember(doc, k.Key), k.Shape)
    | _ -> None

/// Whether a field holds nothing: its column is NULL, or for a value every
/// column is.
let private fieldIsNull (alias: string) (f: FieldMap) =
    match f.Value with
    | Some _ -> Sql.conj (f.Parts |> List.map (fun p -> Sql.IsNull(Sql.Column(alias, p.Name))))
    | None -> Sql.IsNull(Sql.Column(alias, f.Column))

let private fieldIsNotNull (alias: string) (f: FieldMap) =
    match f.Value with
    | Some _ -> Sql.Not(fieldIsNull alias f)
    | None -> Sql.IsNotNull(Sql.Column(alias, f.Column))

/// A value in the row equals a value: every column equals its part.
let private valueEquals (alias: string) (f: FieldMap) (value: obj) : Sql.Expr =
    List.zip f.Parts (f.ToColumns value)
    |> List.map (fun (p, v) ->
        if isNull v || v = box DBNull.Value then Sql.IsNull(Sql.Column(alias, p.Name))
        elif f.Requires p.Name then Sql.Binary("=", Sql.Column(alias, p.Name), Sql.partValue p v)
        else Sql.NotDistinctFrom(Sql.Column(alias, p.Name), Sql.partValue p v))
    |> Sql.conj

/// A value in the row equals a prepared argument: every column equals its
/// part of the argument, each bound when the query runs (a whole key of
/// several parts by Arg.Value, composite-keys.feature).
let private valueEqualsArg (alias: string) (f: FieldMap) (v: Expr) : Sql.Expr =
    let value = argFunction v

    f.Parts
    |> List.mapi (fun i p ->
        let part (args: obj[]) =
            match value args with
            | null -> invalidOp (nullMessage "argument" v)
            | x -> (f.ToColumns x).[i]

        let bound = Sql.Param(box (Sql.Bound(part, show v)))
        let bound = match Sql.partStoreType p with Some t -> Sql.Cast(bound, t, false) | None -> bound

        if f.Requires p.Name then Sql.Binary("=", Sql.Column(alias, p.Name), bound)
        else Sql.NotDistinctFrom(Sql.Column(alias, p.Name), bound))
    |> Sql.conj

let (|RowAssoc|_|) (ctx: Ctx) (e: Expr) =
    match e with
    | PropertyGet(Some(RowBinding ctx b), prop, []) when not b.Optional ->
        b.Schema.TryAssoc prop.Name |> Option.map (fun a -> b, a)
    | _ -> None

/// `IsSome` / `IsNone` (or Option.isSome / isNone) of an owned optional record.
/// A list of scalars stored as one array column (array-columns.feature).
let (|ArrayField|_|) (ctx: Ctx) (e: Expr) =
    match e with
    | RowField ctx (b, f) when f.Array.IsSome -> Some(b, f)
    | _ -> None

/// A call or property over an array column that is not List.contains or
/// List.isEmpty: refused, naming the field.
let (|OverArray|_|) (ctx: Ctx) (e: Expr) =
    let direct (args: Expr list) =
        args |> List.tryPick (fun a -> match a with ArrayField ctx bf -> Some bf | _ -> None)

    match e with
    | Call(target, _, args) -> direct (Option.toList target @ args)
    | PropertyGet(Some(ArrayField ctx bf), _, _) -> Some bf
    | _ -> None

let private arrayRefusal (b: Binding) (f: FieldMap) (what: string) =
    $"{b.Schema.Name}.{f.Name} is an array column: {what}; test it with List.contains or List.isEmpty"

let (|OwnedPresence|_|) (ctx: Ctx) (e: Expr) =
    let owned (x: Expr) =
        match x with
        | RowAssoc ctx (_, a) when a.Owned && a.Cardinality = OptionalOne -> Some a
        | _ -> None

    match e with
    | Call(None, mi, [ x ]) when mi.Name = "get_IsSome" || mi.Name = "get_IsNone" || (mi.DeclaringType.Name = "OptionModule" && (mi.Name = "IsSome" || mi.Name = "IsNone")) ->
        owned x |> Option.map (fun a -> x, a, mi.Name.EndsWith "IsSome")
    | PropertyGet(Some x, p, []) when p.Name = "IsSome" || p.Name = "IsNone" -> owned x |> Option.map (fun a -> x, a, p.Name = "IsSome")
    | _ -> None

// Unions with fields (union-fields.feature): the union in a row, its case
// fields' columns, and its case tests.

/// The part of a union a case field's property reads. A case's fields are
/// properties of the case's own class, named after the case; a single-case
/// union's are properties of the union.
let private casePart (u: UnionLayout) (p: Reflection.PropertyInfo) =
    if not (u.Type.IsAssignableFrom p.DeclaringType) then
        None
    else
        u.Cases
        |> Array.filter (fun c -> p.DeclaringType = u.Type || c.Case.Name = p.DeclaringType.Name)
        |> Array.tryPick (fun c ->
            c.Parts
            |> List.tryFind (function
                | Leaf f -> f.Name = p.Name
                | Nested(name, _) -> name = p.Name))

/// A union with fields in the row: a field's own, or a nested case field of one.
let rec (|UnionPath|_|) (ctx: Ctx) (e: Expr) : (string * UnionLayout) option =
    match e with
    | RowField ctx (b, f) when f.Union.IsSome -> Some(b.Alias, f.Union.Value)
    | PropertyGet(Some(UnionPath ctx (alias, u)), p, []) ->
        match casePart u p with
        | Some(Nested(_, n)) -> Some(alias, n)
        | _ -> None
    | _ -> None

/// A case field of a union in the row, stored in a column.
let (|UnionLeaf|_|) (ctx: Ctx) (e: Expr) : (string * FieldMap) option =
    match e with
    | PropertyGet(Some(UnionPath ctx (alias, u)), p, []) ->
        match casePart u p with
        | Some(Leaf f) -> Some(alias, f)
        | _ -> None
    | _ -> None

/// `if case then Some field else None` over a union in the row (what
/// `match c.Stage with Doing(who, _) -> Some who | _ -> None` becomes): its
/// column, which holds NULL in every other case.
let (|UnionOptionalLeaf|_|) (ctx: Ctx) (e: Expr) : (string * FieldMap) option =
    match e with
    | IfThenElse(UnionCaseTest(UnionPath ctx (alias, u), case), NewUnionCase(some, [ UnionLeaf ctx (alias2, f) ]), NewUnionCase(none, [])) when
        some.Name = "Some" && none.Name = "None" && alias = alias2 && not f.IsOption
        && (u.Cases |> Array.exists (fun c -> c.Case.Tag = case.Tag && u.ColumnsOf c |> List.contains f.Column))
        ->
        Some(alias, f)
    | _ -> None

/// As UnionOptionalLeaf, with the union, the case's tag and the case field
/// itself: what Option.exists and Option.forall test.
let (|UnionOptionalCase|_|) (ctx: Ctx) (e: Expr) : (string * UnionLayout * int * Expr) option =
    match e with
    | UnionOptionalLeaf ctx _ ->
        match e with
        | IfThenElse(UnionCaseTest(UnionPath ctx (alias, u), case), NewUnionCase(_, [ leaf ]), _) -> Some(alias, u, case.Tag, leaf)
        | _ -> None
    | _ -> None

/// A case's stored name, written into the SQL: it is an identifier made from an
/// F# case name, and a literal keeps the expression the same wherever it
/// appears (GROUP BY and SELECT must match).
let private storedLiteral (stored: string) = Sql.Literal("'" + stored + "'")

/// The union in the row is in the case of this tag.
let private caseIs (alias: string) (u: UnionLayout) (tag: int) =
    let c = u.Cases |> Array.find (fun c -> c.Case.Tag = tag)
    Sql.Binary("=", Sql.Column(alias, u.TagColumn), storedLiteral c.Stored)

/// A union in the row equals a value: its case, and each of that case's fields.
let rec private unionEquals (alias: string) (u: UnionLayout) (value: obj) : Sql.Expr list =
    let c = u.Cases.[u.Tag value]
    let fields = c.Read value

    Sql.Binary("=", Sql.Column(alias, u.TagColumn), Sql.Param(box c.Stored))
    :: (c.Parts
        |> List.mapi (fun i p ->
            match p with
            | Leaf f when f.IsOption -> [ Sql.NotDistinctFrom(Sql.Column(alias, f.Column), Sql.fieldValue f fields.[i]) ]
            | Leaf f -> [ Sql.Binary("=", Sql.Column(alias, f.Column), Sql.fieldValue f fields.[i]) ]
            | Nested(_, n) -> unionEquals alias n fields.[i])
        |> List.concat)

let private isOptionModule (mi: Reflection.MethodInfo) (name: string) =
    mi.DeclaringType.Name = "OptionModule" && mi.Name = name

/// `Option.map (fun c -> c.Field) c` / `Option.bind` over a left-joined source:
/// the field's column, NULL when the source is missing.
let (|OptionalField|_|) (ctx: Ctx) (e: Expr) =
    match e with
    | Call(None, mi, [ Lambda(x, PropertyGet(Some(Var x2), prop, [])); RowBinding ctx b ]) when
        b.Optional
        && x = x2
        && (isOptionModule mi "Map" || isOptionModule mi "Bind")
        ->
        match b.Schema.TryField prop.Name with
        | Some f when mi.Name = "Map" && f.IsOption ->
            fail e $"{b.Schema.Name}.{f.Name} is optional; use Option.bind rather than Option.map"
        | Some f when mi.Name = "Bind" && not f.IsOption ->
            fail e $"{b.Schema.Name}.{f.Name} is not optional; use Option.map rather than Option.bind"
        | Some f -> Some(b, f)
        | None -> fail e $"property '{prop.Name}' of {b.Schema.Name} is not a mapped column"
    // A field of an optional value (value-records.feature): its column, as an
    // option (NULL when the value is absent).
    | Call(None, mi, [ Lambda(x, PropertyGet(Some(Var x2), prop, [])); RowField ctx (b, f) ]) when
        f.IsOption && f.Value.IsSome && x = x2 && (isOptionModule mi "Map" || isOptionModule mi "Bind")
        ->
        match f.Value.Value.Sub prop.Name with
        | Some sf when mi.Name = "Map" && sf.IsOption ->
            fail e $"{b.Schema.Name}.{f.Name}.{sf.Name} is optional; use Option.bind rather than Option.map"
        | Some sf when mi.Name = "Bind" && not sf.IsOption ->
            fail e $"{b.Schema.Name}.{f.Name}.{sf.Name} is not optional; use Option.map rather than Option.bind"
        | Some sf when sf.Composite ->
            fail e $"{b.Schema.Name}.{f.Name}.{sf.Name} is stored in several columns; read one of its fields"
        | Some sf -> Some(b, sf)
        | None -> fail e $"property '{prop.Name}' of {b.Schema.Name}.{f.Name} is not a mapped column"
    | _ -> None

/// A source's key, for telling whether a left-joined row is there: its key's
/// first column, which is never NULL in a row that is (a key of several
/// parts, composite-keys.feature, has every part NOT NULL).
let private keyOf (b: Binding) = Sql.Column(b.Alias, b.Schema.Key.Parts.Head.Name)

let private isCollectionModule (mi: Reflection.MethodInfo) =
    match mi.DeclaringType.FullName with
    | "Microsoft.FSharp.Collections.ListModule"
    | "Microsoft.FSharp.Collections.SeqModule"
    | "Microsoft.FSharp.Collections.ArrayModule" -> true
    | _ -> false

let private comparisonOps =
    dict
        [ "op_LessThan", "<"
          "op_LessThanOrEqual", "<="
          "op_GreaterThan", ">"
          "op_GreaterThanOrEqual", ">=" ]

let private arithmeticOps =
    dict
        [ "op_Addition", "+"
          "op_Subtraction", "-"
          "op_Multiply", "*"
          "op_Division", "/"
          "op_Modulus", "%" ]

let private escapeLike (s: string) =
    s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

/// `p` is the field of a single-case union of one field: a wrapper.
let private isWrapperField (t: Type) (p: Reflection.PropertyInfo) =
    Shape.isUnion t
    && (match FSharpType.GetUnionCases(t, true) with
        | [| case |] ->
            match case.GetFields() with
            | [| field |] -> field.Name = p.Name && field.DeclaringType = p.DeclaringType
            | _ -> false
        | _ -> false)

/// A property declared by the application, not by .NET or FSharp.Core.
let private isApplicationMember (p: Reflection.PropertyInfo) =
    let a = p.DeclaringType.Assembly
    a <> typeof<obj>.Assembly && a <> typeof<list<int>>.Assembly && not (a.GetName().Name.StartsWith "System")

let rec scalar (ctx: Ctx) (e: Expr) : Sql.Expr =
    if not (isRowDependent ctx e) && mentionsArg e then
        argParam ctx e
    elif not (isRowDependent ctx e) then
        let v = evaluate ctx e

        match unwrapOption e.Type v with
        | None -> Sql.Literal "NULL"
        | Some inner when e.Type = typeof<bool> -> Sql.Literal(if unbox<bool> inner then "TRUE" else "FALSE")
        | Some inner -> toParam ctx e (scalarType e.Type) inner
    else
        match e with
        | JsonPath ctx (doc, JScalar(t, _)) -> Sql.JsonText(doc, Some t)
        | UnionPath ctx _ ->
            fail e "a union with fields is stored in several columns; test its case (c.Stage.IsDoing, match) or compare it with a value"
        | RowField ctx (b, f) when f.Value.IsSome && f.Name = b.Schema.Key.Name ->
            fail e $"{b.Schema.Name}.{f.Name} is a key of several parts, which has no order of its own: order by its parts, and compare its parts with < or >"
        | RowField ctx (b, f) when f.Value.IsSome ->
            fail e $"{b.Schema.Name}.{f.Name} is a {f.ScalarType.Name} stored in several columns; use one of its fields here, or compare it whole with ="
        | ArrayField ctx (b, f) -> fail e (arrayRefusal b f "it cannot be ordered by, grouped by or compared as a value")
        | OverArray ctx (b, f) -> fail e (arrayRefusal b f $"{show e} reads more of it than a query translates")
        | RowField ctx (b, f) -> Sql.Column(b.Alias, f.Column)
        | OptionalField ctx (b, f) -> Sql.Column(b.Alias, f.Column)
        | UnionLeaf ctx (alias, f) -> Sql.Column(alias, f.Column)
        | UnionOptionalLeaf ctx (alias, f) -> Sql.Column(alias, f.Column)
        // A value chosen by a test (an `if`, or a `match` on a union's case).
        // A match on a union's case (union-fields.feature): F# makes its last
        // case the else, so it gets its own WHEN, and a stored case no case
        // names is NULL, refused when read as reading the record refuses it.
        | IfThenElse(UnionCaseTest(UnionPath ctx (alias, u), _), _, _) when
            e.Type <> typeof<bool>
            && (let rec same (x: Expr) =
                    match x with
                    | IfThenElse(UnionCaseTest(UnionPath ctx (a2, u2), _), _, rest) when a2 = alias && u2.Type = u.Type -> same rest
                    | _ -> true

                same e)
            ->
            let rec chain (x: Expr) acc =
                match x with
                | IfThenElse(UnionCaseTest(UnionPath ctx (a2, u2), case), v, rest) when a2 = alias && u2.Type = u.Type ->
                    chain rest ((case, v) :: acc)
                | otherwise -> List.rev acc, otherwise

            let tested, otherwise = chain e []
            let left = u.Cases |> Array.filter (fun c -> tested |> List.forall (fun (t, _) -> t.Tag <> c.Case.Tag))

            let branches =
                [ for case, v in tested -> caseIs alias u case.Tag, branch ctx v
                  if left.Length > 0 then
                      yield Sql.disj [ for c in left -> caseIs alias u c.Case.Tag ], branch ctx otherwise ]

            Sql.Case(branches, Sql.Literal "NULL")
        | IfThenElse(cond, a, b) when e.Type <> typeof<bool> -> Sql.Case([ predicate ctx cond, branch ctx a ], branch ctx b)
        // `Some c.Id` in a comparison or join key: the inner value.
        | NewUnionCase(c, [ x ]) when c.Name = "Some" && Reflection.isOption e.Type -> scalar ctx x
        | RowAssoc ctx (_, a) ->
            fail e $"'{a.Name}' cannot be used as a value in a query; filter with List.exists or List.forall over it"
        | PropertyGet(Some(RowField ctx (b, f)), p, []) when f.IsOption && p.Name = "Value" -> Sql.Column(b.Alias, f.Column)
        | Call(None, mi, [ RowField ctx (b, f) ]) when
            f.IsOption
            && mi.DeclaringType.Name = "OptionModule"
            && (mi.Name = "GetValue" || mi.Name = "Get")
            ->
            Sql.Column(b.Alias, f.Column)
        | PropertyGet(Some(RowBinding ctx b), p, []) when not b.Optional ->
            fail e $"property '{p.Name}' of {b.Schema.Name} is not a mapped column"
        | RowBinding ctx b when b.Optional ->
            fail e $"a left-joined {b.Schema.Name} cannot be used as a value; use Option.map, Option.exists or IsNone"
        | RowBinding ctx b -> fail e $"a whole {b.Schema.Name} cannot be used as a value; compare its fields"
        | Call(None, mi, [ a; b ]) when arithmeticOps.ContainsKey mi.Name ->
            let op =
                if mi.Name = "op_Addition" && a.Type = typeof<string> then
                    "||"
                else
                    arithmeticOps.[mi.Name]

            if Reflection.isOption a.Type || Reflection.isOption b.Type then
                fail e "arithmetic on optional values is not supported"

            Sql.Binary(op, scalar ctx a, scalar ctx b)
        | Call(Some s, mi, []) when s.Type = typeof<string> ->
            match mi.Name with
            | "ToLower"
            | "ToLowerInvariant" -> Sql.Func("lower", [ scalar ctx s ])
            | "ToUpper"
            | "ToUpperInvariant" -> Sql.Func("upper", [ scalar ctx s ])
            // .NET trims every Char.IsWhiteSpace character, not only spaces.
            | "Trim" -> Sql.Func("btrim", [ scalar ctx s; Sql.Param dotnetWhiteSpace ])
            | "TrimStart" -> Sql.Func("ltrim", [ scalar ctx s; Sql.Param dotnetWhiteSpace ])
            | "TrimEnd" -> Sql.Func("rtrim", [ scalar ctx s; Sql.Param dotnetWhiteSpace ])
            | n -> fail e $"string method '{n}' has no SQL translation"
        | PropertyGet(Some s, p, []) when s.Type = typeof<string> && p.Name = "Length" -> Sql.Func("char_length", [ scalar ctx s ])
        // A wrapper's own field (the body of its [<ReflectedDefinition>] Value,
        // `let (Title v) = t in v`) is what the wrapper stores: its column
        // (scalar-conventions.feature).
        | PropertyGet(Some w, p, []) when isWrapperField w.Type p -> scalar ctx w
        | Call(None, mi, args) when isAggCall mi -> aggregate ctx e mi args
        | Call(None, mi, [ sql; args ]) when isRawCall mi -> fragment ctx e sql args
        | _ when e.Type = typeof<bool> -> predicate ctx e
        // A member of the application's own type that Plasm cannot see into.
        | PropertyGet(Some _, p, []) when isApplicationMember p ->
            fail e $"{p.DeclaringType.Name}.{p.Name} is a member Plasm cannot read: mark it [<ReflectedDefinition>] so that a query reads its body"
        | _ -> fail e $"{show e} cannot be translated to SQL, and it will not be evaluated in application memory"

/// A value a CASE chooses. A constant that is an identifier (a fieldless
/// union's stored name) or a number is written into the SQL, so the same
/// CASE in GROUP BY and SELECT is the same expression; anything else is a
/// parameter or a column, as in any expression.
and branch (ctx: Ctx) (e: Expr) : Sql.Expr =
    if isRowDependent ctx e || mentionsArg e then
        scalar ctx e
    else
        // A value written in the query's own code is written into the SQL, so
        // the same branch in SELECT and GROUP BY is the same expression (two
        // parameters would not be). A captured value stays a parameter.
        match e, scalar ctx e with
        | ValueWithName _, other -> other
        | Value(:? string as text, _), Sql.Param _ when not (text.Contains '\000') -> Sql.Literal("'" + text.Replace("'", "''") + "'")
        | _, Sql.Param(:? string as text) when Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z0-9_]+$") -> storedLiteral text
        | _, Sql.Param(:? int as n) -> Sql.Literal(string n)
        | _, Sql.Param(:? int64 as n) -> Sql.Literal(string n)
        | _, other -> other

/// True when the translated expression can be NULL: it reads an option field,
/// a left-joined source, an aggregate or a raw fragment.
and mayBeNull (ctx: Ctx) (e: Expr) : bool =
    match e with
    | UnionLeaf ctx _
    | UnionOptionalLeaf ctx _ -> true
    | RowField ctx (_, f) -> f.IsOption
    | RowBinding ctx b -> b.Optional
    | PropertyGet(Some(RowBinding ctx b), _, _) when b.Optional -> true
    | Call(None, mi, _) when isAggCall mi || isRawCall mi -> true
    | ExprShape.ShapeVar _ -> false
    | ExprShape.ShapeLambda(_, body) -> mayBeNull ctx body
    | ExprShape.ShapeCombination(_, args) -> List.exists (mayBeNull ctx) args

and predicate (ctx: Ctx) (e: Expr) : Sql.Expr =
    match e with
    | Call(None, mi, [ a; b ]) when Ties.isMarker mi -> predicate ctx (expandTie ctx.Model mi a b)
    | _ ->
    if not (isRowDependent ctx e) then
        scalar ctx e
    else
        match e with
        | Call(None, mi, [ q ]) when isExistsCall mi -> existsQuery ctx e q
        | IfThenElse(a, b, Bool false) -> Sql.Binary("AND", predicate ctx a, predicate ctx b)
        | IfThenElse(a, Bool true, b) -> Sql.Binary("OR", predicate ctx a, predicate ctx b)
        | OperatorCall "Not" [ a ] ->
            // SQL's NOT leaves NULL unknown, so the row would be dropped; in F#
            // the comparison is false and its negation true. Only operands that
            // can be NULL need IS NOT TRUE; plain NOT keeps indexes usable.
            match predicate ctx a with
            | Sql.Exists _ as x -> Sql.Not x
            | x when mayBeNull ctx a -> Sql.IsNotTrue x
            // Neither side can be NULL, so the negation is the opposite comparison.
            | Sql.Binary("=", l, r) -> Sql.Binary("<>", l, r)
            | Sql.Binary("<>", l, r) -> Sql.Binary("=", l, r)
            | Sql.Binary("<", l, r) -> Sql.Binary(">=", l, r)
            | Sql.Binary(">=", l, r) -> Sql.Binary("<", l, r)
            | Sql.Binary(">", l, r) -> Sql.Binary("<=", l, r)
            | Sql.Binary("<=", l, r) -> Sql.Binary(">", l, r)
            | x -> Sql.Not x
        // Left-joined sources: presence tests and option predicates.
        | Call(None, mi, [ RowBinding ctx b ]) when b.Optional && (mi.Name = "get_IsSome" || isOptionModule mi "IsSome") ->
            Sql.IsNotNull(keyOf b)
        | Call(None, mi, [ RowBinding ctx b ]) when b.Optional && (mi.Name = "get_IsNone" || isOptionModule mi "IsNone") ->
            Sql.IsNull(keyOf b)
        | PropertyGet(Some(RowBinding ctx b), p, []) when b.Optional && p.Name = "IsSome" -> Sql.IsNotNull(keyOf b)
        | PropertyGet(Some(RowBinding ctx b), p, []) when b.Optional && p.Name = "IsNone" -> Sql.IsNull(keyOf b)
        | Call(None, mi, [ Lambda(x, body); RowBinding ctx b ]) when b.Optional && isOptionModule mi "Exists" ->
            let inner = { ctx with Vars = ctx.Vars.Add(x, { b with Optional = false }) }
            Sql.Binary("AND", Sql.IsNotNull(keyOf b), predicate inner body)
        | Call(None, mi, [ Lambda(x, body); RowBinding ctx b ]) when b.Optional && isOptionModule mi "ForAll" ->
            let inner = { ctx with Vars = ctx.Vars.Add(x, { b with Optional = false }) }
            Sql.Binary("OR", Sql.IsNull(keyOf b), predicate inner body)
        // Option.exists / Option.forall over an optional value (value-records.feature):
        // the predicate over its columns, read through .Value.
        | Call(None, mi, [ Lambda(x, body); (RowField ctx (b, f) as field) ]) when
            f.IsOption && f.Value.IsSome && (isOptionModule mi "Exists" || isOptionModule mi "ForAll")
            ->
            let value = Reflection.Quoted.propertyGet field (field.Type.GetProperty "Value")
            let inner = predicate ctx (body.Substitute(fun v -> if v = x then Some value else None))

            if mi.Name = "Exists" then
                Sql.Binary("AND", fieldIsNotNull b.Alias f, inner)
            else
                Sql.Binary("OR", fieldIsNull b.Alias f, inner)
        // Option.exists / Option.forall over a case field as an option (what a
        // domain's member `match c.Stage with Doing(_, since) -> Some since |
        // _ -> None` reads): the case test, and the predicate over the field's
        // column, which in that case holds a value.
        | Call(None, mi, [ Lambda(x, body); UnionOptionalCase ctx (alias, u, tag, leaf) ]) when
            isOptionModule mi "Exists" || isOptionModule mi "ForAll"
            ->
            let inner = predicate ctx (body.Substitute(fun v -> if v = x then Some leaf else None))

            if mi.Name = "Exists" then
                Sql.Binary("AND", caseIs alias u tag, inner)
            else
                Sql.Binary("OR", Sql.Not(caseIs alias u tag), inner)
        | UnionCaseTest(UnionPath ctx (alias, u), case) -> caseIs alias u case.Tag
        | PropertyGet(Some(UnionPath ctx (alias, u)), p, []) when
            p.Name.StartsWith "Is" && u.Cases |> Array.exists (fun c -> "Is" + c.Case.Name = p.Name)
            ->
            caseIs alias u (u.Cases |> Array.find (fun c -> "Is" + c.Case.Name = p.Name)).Case.Tag
        | OperatorCall "op_Equality" [ UnionPath ctx (alias, u); v ]
        | OperatorCall "op_Equality" [ v; UnionPath ctx (alias, u) ] when not (isRowDependent ctx v) ->
            Sql.conj (unionEquals alias u (evaluate ctx v))
        | OperatorCall "op_Inequality" [ UnionPath ctx (alias, u); v ]
        | OperatorCall "op_Inequality" [ v; UnionPath ctx (alias, u) ] when not (isRowDependent ctx v) ->
            Sql.Not(Sql.conj (unionEquals alias u (evaluate ctx v)))
        // A whole document equals a value: jsonb compares documents by content.
        | OperatorCall "op_Equality" [ RowField ctx (b, f); v ]
        | OperatorCall "op_Equality" [ v; RowField ctx (b, f) ] when f.Json.IsSome && not (isRowDependent ctx v) && not (mentionsArg v) ->
            match f.ToDb(evaluate ctx v) with
            | x when x = box DBNull.Value -> Sql.IsNull(Sql.Column(b.Alias, f.Column))
            | x -> Sql.Binary("=", Sql.Column(b.Alias, f.Column), Sql.partValue f.Parts.Head x)
        | OperatorCall "op_Equality" [ RowField ctx (b, f); v ]
        | OperatorCall "op_Equality" [ v; RowField ctx (b, f) ] when f.Value.IsSome && not (isRowDependent ctx v) && mentionsArg v ->
            valueEqualsArg b.Alias f v
        | OperatorCall "op_Equality" [ RowField ctx (b, f); v ]
        | OperatorCall "op_Equality" [ v; RowField ctx (b, f) ] when f.Value.IsSome && not (isRowDependent ctx v) ->
            valueEquals b.Alias f (evaluate ctx v)
        | OperatorCall "op_Inequality" [ RowField ctx (b, f); v ]
        | OperatorCall "op_Inequality" [ v; RowField ctx (b, f) ] when f.Value.IsSome && not (isRowDependent ctx v) ->
            Sql.IsNotTrue(valueEquals b.Alias f (evaluate ctx v))
        | OperatorCall "op_Equality" [ a; b ] -> equality ctx e a b false
        | OperatorCall "op_Inequality" [ a; b ] -> equality ctx e a b true
        | Call(None, mi, [ a; b ]) when comparisonOps.ContainsKey mi.Name ->
            if Reflection.isOption a.Type || Reflection.isOption b.Type then
                fail
                    e
                    "ordering comparisons on optional values differ between F# (None < Some) and SQL (NULL); compare x.Value after testing x.IsSome"

            Sql.Binary(comparisonOps.[mi.Name], ordered ctx a, ordered ctx b)
        | PropertyGet(Some(RowField ctx (b, f)), p, []) when f.IsOption && p.Name = "IsSome" ->
            fieldIsNotNull b.Alias f
        | PropertyGet(Some(RowField ctx (b, f)), p, []) when f.IsOption && p.Name = "IsNone" ->
            fieldIsNull b.Alias f
        // x.IsSome / x.IsNone compile to static calls on the option type.
        | Call(None, mi, [ RowField ctx (b, f) ]) when f.IsOption && Reflection.isOption mi.DeclaringType && mi.Name = "get_IsSome" ->
            fieldIsNotNull b.Alias f
        | Call(None, mi, [ RowField ctx (b, f) ]) when f.IsOption && Reflection.isOption mi.DeclaringType && mi.Name = "get_IsNone" ->
            fieldIsNull b.Alias f
        | Call(None, mi, [ RowField ctx (b, f) ]) when f.IsOption && mi.DeclaringType.Name = "OptionModule" ->
            match mi.Name with
            | "IsSome" -> fieldIsNotNull b.Alias f
            | "IsNone" -> fieldIsNull b.Alias f
            | n -> fail e $"Option.{n} has no SQL translation"
        | Call(Some s, mi, [ arg ]) when s.Type = typeof<string> && arg.Type = typeof<string> ->
            if isRowDependent ctx arg then
                fail e "string pattern arguments must be values, not columns"

            if mentionsArg arg then
                let shape (t: string) =
                    match mi.Name with
                    | "StartsWith" -> escapeLike t + "%"
                    | "EndsWith" -> "%" + escapeLike t
                    | "Contains" -> "%" + escapeLike t + "%"
                    | n -> fail e $"string method '{n}' has no SQL translation"

                shape "" |> ignore
                Sql.Like(scalar ctx s, argParamWith ctx arg typeof<string> (fun v -> box (shape (string v))))
            else

            let text = evaluate ctx arg :?> string

            if isNull text then
                fail e "string pattern argument is null"

            let pattern =
                match mi.Name with
                | "StartsWith" -> escapeLike text + "%"
                | "EndsWith" -> "%" + escapeLike text
                | "Contains" -> "%" + escapeLike text + "%"
                | n -> fail e $"string method '{n}' has no SQL translation"

            Sql.Like(scalar ctx s, Sql.Param pattern)
        // A [<Json>] list of scalars contains a value: the document contains [value].
        | Call(None, mi, [ x; JsonPath ctx (doc, JList(element, _)) ]) when
            mi.Name = "Contains" && mi.DeclaringType.Name = "ListModule" && not (isRowDependent ctx x)
            ->
            if mentionsArg x then
                fail e "a prepared argument cannot yet be looked for in a document list; compare it with a field instead"

            let one = System.Text.Json.Nodes.JsonArray()
            one.Add(JsonDoc.write element (evaluate ctx x))
            Sql.JsonContains(doc, Sql.Param(box (one.ToJsonString())))
        // List.exists / List.forall over a [<Json>] list: over its elements.
        | Call(None, mi, [ Lambda(x, body); JsonPath ctx (doc, JList(element, _)) ]) when
            mi.DeclaringType.Name = "ListModule" && (mi.Name = "Exists" || mi.Name = "ForAll")
            ->
            let alias = ctx.Name(Some x, "element")
            let inner = { ctx with Docs = ctx.Docs.Add(x, (Sql.JsonElement alias, element)) }
            let condition = predicate inner body

            if mi.Name = "Exists" then
                Sql.JsonAny(doc, alias, condition)
            else
                Sql.Not(Sql.JsonAny(doc, alias, Sql.IsNotTrue condition))
        // An array column holds a value (array-columns.feature): value = ANY(column).
        | Call(None, mi, [ x; ArrayField ctx (b, f) ]) when mi.Name = "Contains" && mi.DeclaringType.Name = "ListModule" ->
            Sql.Fragment [ Choice2Of2(scalar ctx x); Choice1Of2 " = ANY("; Choice2Of2(Sql.Column(b.Alias, f.Column)); Choice1Of2 ")" ]
        // An array column holds nothing: its cardinality is 0.
        | Call(None, mi, [ ArrayField ctx (b, f) ]) when mi.Name = "IsEmpty" && mi.DeclaringType.Name = "ListModule" ->
            Sql.Binary("=", Sql.Func("cardinality", [ Sql.Column(b.Alias, f.Column) ]), Sql.Literal "0")
        | PropertyGet(Some(ArrayField ctx (b, f)), p, []) when p.Name = "IsEmpty" ->
            Sql.Binary("=", Sql.Func("cardinality", [ Sql.Column(b.Alias, f.Column) ]), Sql.Literal "0")
        | OverArray ctx (b, f) -> fail e (arrayRefusal b f $"{show e} reads more of it than a query translates")
        | Call(None, mi, [ x; coll ]) when mi.Name = "Contains" && isCollectionModule mi -> membership ctx e x coll
        | Call(Some coll, mi, [ x ]) when mi.Name = "Contains" && not (isRowDependent ctx coll) -> membership ctx e x coll
        // An owned or linked list is a plain list: F#'s own List.exists and
        // List.forall over it are correlated EXISTS subqueries.
        | Call(None, mi, [ Lambda(pv, body); RowAssoc ctx (_, a) as assoc ]) when
            (a.Owned || a.Linked) && mi.DeclaringType.Name = "ListModule" && (mi.Name = "Exists" || mi.Name = "ForAll")
            ->
            relationship ctx e pv body assoc (mi.Name = "ForAll")
        // An owned optional record is an option: Option.exists, Option.forall,
        // IsSome and IsNone over it are correlated EXISTS subqueries.
        | Call(None, mi, [ Lambda(pv, body); RowAssoc ctx (_, a) as assoc ]) when
            a.Owned && a.Cardinality = OptionalOne && (isOptionModule mi "Exists" || isOptionModule mi "ForAll")
            ->
            relationship ctx e pv body assoc (mi.Name = "ForAll")
        | OwnedPresence ctx (assoc, a, present) ->
            let v = Var("x", a.Target)
            let found = relationship ctx e v (Expr.Value true) assoc false
            if present then found else Sql.Not found
        | RowField ctx (b, f) when f.ScalarType = typeof<bool> && not f.IsOption -> Sql.Column(b.Alias, f.Column)
        | JsonPath ctx (doc, JScalar(t, _)) when t = typeof<bool> -> Sql.JsonText(doc, Some t)
        | Call(None, mi, [ sql; args ]) when isRawCall mi -> fragment ctx e sql args
        | _ -> fail e $"the condition {show e} cannot be translated to SQL, and it will not be evaluated in application memory"

and aggregate ctx (e: Expr) (mi: Reflection.MethodInfo) (args: Expr list) : Sql.Expr =
    let arg () =
        match args with
        | [ x ] ->
            let sql = scalar ctx x

            if containsAggregate sql then
                fail e "aggregates cannot be nested"

            sql
        | _ -> fail e "unexpected aggregate arguments"

    match mi.Name with
    | "count" -> Sql.Aggregate("COUNT", false, None)
    | "countDistinct" -> Sql.Aggregate("COUNT", true, Some(arg ()))
    // F# meaning: the sum of no values is zero (List.sum []).
    | "sum" -> Sql.Func("COALESCE", [ Sql.Aggregate("SUM", false, Some(arg ())); Sql.Literal "0" ])
    | "min"
    | "max" as name ->
        match args, ctx.Model.OrderedNames e.Type with
        | [ x ], Some names -> nameAt names (Sql.Aggregate(name.ToUpperInvariant(), false, Some(ordered ctx x)))
        | _ -> Sql.Aggregate(name.ToUpperInvariant(), false, Some(arg ()))
    | "avg" -> Sql.Aggregate("AVG", false, Some(arg ()))
    | n -> fail e $"Agg.{n} has no SQL translation"

and fragment ctx (e: Expr) (sql: Expr) (args: Expr) : Sql.Expr =
    let text =
        match sql with
        | NewObject(_, Value(:? string as t, _) :: _)
        | Coerce(NewObject(_, Value(:? string as t, _) :: _), _) -> t.Replace("%%", "%")
        | _ ->
            invalid
                $"Raw.fragment SQL must be a string literal, so values can never be spliced into SQL text; pass values as fragment arguments instead (in: {show e})"

    let rec unboxed (x: Expr) =
        match x with
        | Coerce(y, _) -> unboxed y
        | Call(None, mi, [ y ]) when mi.Name = "Box" -> unboxed y
        | y -> y

    let items =
        match unboxed args with
        | NewTuple xs -> xs
        | Value(null, _) -> []
        | x -> [ x ]

    let parts = ResizeArray<Choice<string, Sql.Expr>>()
    let text' = Text.StringBuilder()
    let mutable i = 0

    let flush () =
        if text'.Length > 0 then
            parts.Add(Choice1Of2(text'.ToString()))
            text'.Clear() |> ignore

    while i < text.Length do
        let c = text.[i]

        if c = '{' && i + 1 < text.Length && text.[i + 1] = '{' then
            text'.Append '{' |> ignore
            i <- i + 2
        elif c = '}' && i + 1 < text.Length && text.[i + 1] = '}' then
            text'.Append '}' |> ignore
            i <- i + 2
        elif c = '{' then
            let close = text.IndexOf('}', i)

            match (if close > i then Int32.TryParse(text.Substring(i + 1, close - i - 1)) else (false, 0)) with
            | true, n when n >= 0 && n < items.Length ->
                flush ()
                parts.Add(Choice2Of2(scalar ctx items.[n]))
                i <- close + 1
            | true, n -> invalid $"Raw.fragment placeholder {{{n}}} has no argument ({items.Length} given)"
            | _ -> invalid $"Raw.fragment placeholders are {{0}}, {{1}}, ...; use {{{{ for a literal brace"
        else
            text'.Append c |> ignore
            i <- i + 1

    flush ()
    Sql.Fragment(List.ofSeq parts)

/// A value as SQL compares it: its position in F#'s order when it is stored as
/// text from a finite table, otherwise the value itself.
and ordered ctx (e: Expr) : Sql.Expr =
    match ctx.Model.OrderedNames e.Type with
    | None -> scalar ctx e
    | Some names ->
        let t = scalarType e.Type
        let model = ctx.Model

        let position (v: obj) =
            match model.ScalarToDb(t, v) with
            | Ok(:? string as stored) -> List.findIndex ((=) stored) names
            | Ok other -> invalidOp $"{Reflection.typeName t} was not stored as text: {other}"
            | Error m -> invalidOp m

        if not (isRowDependent ctx e) && mentionsArg e then
            argParamWith ctx e typeof<int> (fun v -> box (position v))
        elif not (isRowDependent ctx e) then
            match unwrapOption e.Type (evaluate ctx e) with
            | None -> Sql.Literal "NULL"
            | Some v -> Sql.Param(box (position v))
        else
            let stored = scalar ctx e
            if containsAggregate stored then fail e "aggregates cannot be nested"
            positionOf names stored

and containsAggregate (e: Sql.Expr) : bool =
    match e with
    | Sql.Aggregate _ -> true
    | Sql.Binary(_, a, b)
    | Sql.NotDistinctFrom(a, b)
    | Sql.DistinctFrom(a, b)
    | Sql.Like(a, b) -> containsAggregate a || containsAggregate b
    | Sql.Not a
    | Sql.IsNotTrue a
    | Sql.IsNull a
    | Sql.IsNotNull a
    | Sql.AnyOf(a, _, _)
    | Sql.Cast(a, _, _)
    | Sql.AsText a -> containsAggregate a
    | Sql.Func(_, xs) -> List.exists containsAggregate xs
    | Sql.Fragment ps ->
        ps
        |> List.exists (function
            | Choice2Of2 x -> containsAggregate x
            | _ -> false)
    | _ -> false

and equality ctx (whole: Expr) (a: Expr) (b: Expr) (negate: bool) : Sql.Expr =
    let aDep, bDep = isRowDependent ctx a, isRowDependent ctx b

    // `c = None` for a left-joined source tests whether it is missing.
    let missing (src: Expr) (other: Expr) =
        match src with
        | RowBinding ctx bnd when bnd.Optional ->
            if isRowDependent ctx other || not (isNull (evaluate ctx other)) then
                fail whole "a left-joined source can only be compared with None; use Option.exists for its fields"

            Some(if negate then Sql.IsNotNull(keyOf bnd) else Sql.IsNull(keyOf bnd))
        | _ -> None

    match missing a b |> Option.orElse (missing b a) with
    | Some sql -> sql
    | None ->

        match aDep, bDep with
        | false, true -> equality ctx whole b a negate
        | true, false when mentionsArg b ->
            // A bound argument: one SQL shape for every value, so options use
            // IS [NOT] DISTINCT FROM (F#: None = None).
            let lhs = scalar ctx a

            match b with
            | NewUnionCase(c, [ x ]) when c.Name = "Some" ->
                let rhs = argParam ctx x
                if negate && Reflection.isOption a.Type then Sql.DistinctFrom(lhs, rhs)
                else Sql.Binary((if negate then "<>" else "="), lhs, rhs)
            | _ when Reflection.isOption b.Type ->
                let rhs = argParam ctx b

                if negate then
                    Sql.DistinctFrom(lhs, rhs)
                else
                    // A value or absent, written so an index on the column can
                    // serve a value (readable-sql.feature); IS NOT DISTINCT
                    // FROM, which says the same, cannot use one.
                    Sql.Binary("OR", Sql.Binary("=", lhs, rhs), Sql.Binary("AND", Sql.IsNull lhs, Sql.IsNull rhs))
            | _ ->
                let rhs = argParam ctx b
                if negate && Reflection.isOption a.Type then Sql.DistinctFrom(lhs, rhs)
                else Sql.Binary((if negate then "<>" else "="), lhs, rhs)
        | true, false ->
            let v = evaluate ctx b

            match unwrapOption b.Type v with
            | None when not (Reflection.isOption b.Type) ->
                // A null string (or other reference) is no absent value in F#:
                // `c.Name = null` is false for every row, never IS NULL.
                fail whole (nullMessage "value" b)
            | None ->
                // F# `= None` means "is absent": SQL IS NULL, never `= NULL`.
                let col = scalar ctx a
                if negate then Sql.IsNotNull col else Sql.IsNull col
            | Some inner ->
                let lhs = scalar ctx a
                let rhs = toParam ctx whole (scalarType b.Type) inner

                if negate && Reflection.isOption a.Type then
                    // F#: (None <> Some x) is true, so NULL rows must match.
                    Sql.DistinctFrom(lhs, rhs)
                else
                    Sql.Binary((if negate then "<>" else "="), lhs, rhs)
        | true, true ->
            let l, r = scalar ctx a, scalar ctx b

            if Reflection.isOption a.Type || Reflection.isOption b.Type then
                // Structural F# equality: None = None.
                if negate then Sql.DistinctFrom(l, r) else Sql.NotDistinctFrom(l, r)
            else
                Sql.Binary((if negate then "<>" else "="), l, r)
        | false, false -> scalar ctx whole

and membership ctx (whole: Expr) (x: Expr) (coll: Expr) : Sql.Expr =
    if isRowDependent ctx coll then
        fail whole "membership collections must be values, not columns"

    if Reflection.isOption x.Type then
        fail whole "membership tests on optional columns are not supported; test IsSome and use .Value"

    let elemType = x.Type

    let providerType =
        match ctx.Model.ProviderTypeOf elemType with
        | Some t -> t
        | None -> fail whole $"type {Reflection.typeName elemType} has no scalar mapping"

    if mentionsArg coll then
        // One SQL shape for every list (empty included): `= ANY(@p)`.
        let f = argFunction coll
        let model = ctx.Model

        let resolve (args: obj[]) =
            let values =
                match f args with
                | null -> [||]
                | items -> (items :?> IEnumerable) |> Seq.cast<obj> |> Seq.distinct |> Seq.toArray

            let arr = Array.CreateInstance(providerType, values.Length)

            values
            |> Array.iteri (fun i v ->
                match model.ScalarToDb(elemType, v) with
                | Ok p -> arr.SetValue(p, i)
                | Error m -> invalidOp m)

            box arr

        let values: Sql.Expr =
            match model.StoreTypeOf elemType with
            | Some t -> Sql.Cast(Sql.Param(box (Sql.Bound(resolve, show coll))), t, true)
            | None -> Sql.Param(box (Sql.Bound(resolve, show coll)))

        Sql.Binary("=", scalar ctx x, Sql.Func("ANY", [ values ]))
    else

    let items = evaluate ctx coll

    if isNull items then
        fail whole "membership collection is null"

    let values =
        (items :?> IEnumerable)
        |> Seq.cast<obj>
        |> Seq.distinct
        |> Seq.map (fun v ->
            match ctx.Model.ScalarToDb(elemType, v) with
            | Ok p -> p
            | Error m -> fail whole m)
        |> Seq.toArray

    let arr = Array.CreateInstance(providerType, values.Length)
    values |> Array.iteri (fun i v -> arr.SetValue(v, i))
    Sql.AnyOf(scalar ctx x, arr, ctx.Model.StoreTypeOf elemType)

and relationship ctx (whole: Expr) (pv: Var) (body: Expr) (assoc: Expr) (all: bool) : Sql.Expr =
    match assoc with
    | RowAssoc ctx (parent, a) ->
        let target =
            match ctx.Model.Schema a.Target with
            | Ok s -> s
            | Error err -> raise (TranslationFailure err)

        let alias = ctx.Name(Some pv, target.Table)

        let through =
            a.Through |> Option.map (fun j -> j, ctx.Name(None, j.Table))

        let correlation =
            match a.Kind with
            | AssocKind.HasMany
            | AssocKind.HasOne ->
                Sql.Binary(
                    "=",
                    Sql.Column(alias, (target.Field a.ForeignKey).Column),
                    Sql.Column(parent.Alias, parent.Schema.Key.Column)
                )
            | AssocKind.ManyToMany ->
                let j, link = through.Value
                Sql.Binary("=", Sql.Column(link, j.OwnerColumn), Sql.Column(parent.Alias, parent.Schema.Key.Column))

        let throughJoin: Sql.Join list =
            match through with
            | Some(j, link) ->
                [ { Left = false
                    Table = j.Table
                    Alias = link
                    On = Sql.Binary("=", Sql.Column(link, j.TargetColumn), Sql.Column(alias, target.Key.Column)) } ]
            | None -> []

        let inner =
            { ctx with
                Vars =
                    ctx.Vars.Add(
                        pv,
                        { Alias = alias
                          Schema = target
                          Optional = false }
                    ) }

        // For all, no row may fail it: F#'s `not` of it, which is the opposite
        // comparison where nothing can be NULL and IS NOT TRUE where it can.
        let condition =
            if all then
                let notOf =
                    match <@ not true @> with
                    | Call(None, mi, _) -> mi
                    | _ -> failwith "unreachable"

                predicate inner (Expr.Call(notOf, [ body ]))
            else
                predicate inner body

        let sub =
            { Sql.select (Sql.Table(target.Table, alias)) with
                Joins = throughJoin
                Where = [ correlation; condition ] }

        if all then Sql.Not(Sql.Exists sub) else Sql.Exists sub
    | _ -> fail whole "List.exists / List.forall must be applied directly to an owned or linked list of the query row"

/// `exists (plasm { ... })`: the inner query, rebuilt from its builder calls
/// (its conditions may read the outer rows, so it cannot be evaluated), becomes
/// a correlated EXISTS.
and existsQuery ctx (whole: Expr) (q: Expr) : Sql.Expr =
    let inner = (innerCheckable "exists" ctx whole q).Def

    let refuse () =
        fail whole "an exists query is a condition only: it cannot page, order, join or group"

    if inner.Paged || not inner.Orders.IsEmpty || not inner.Joins.IsEmpty || not inner.GroupBy.IsEmpty || not inner.Havings.IsEmpty || inner.Template.IsSome then
        refuse ()

    match inner.Problems with
    | p :: _ -> fail whole p
    | [] -> ()

    let target =
        match ctx.Model.Schema inner.Root with
        | Ok s -> s
        | Error err -> raise (TranslationFailure err)

    let alias = ctx.Name(Naming.root inner None, target.Table)

    let binding =
        { Alias = alias
          Schema = target
          Optional = false }

    let wheres =
        inner.Wheres
        |> List.map (fun lambda ->
            match normalize lambda with
            | Lambda(v, body) -> predicate { ctx with Vars = ctx.Vars.Add(v, binding) } body
            | other -> fail other "expected a lambda")

    Sql.Exists { Sql.select (Sql.Table(target.Table, alias)) with Where = wheres }

/// Rebuilds a query written inside a condition or a load from its builder
/// calls, keeping each quoted argument as it is (it may read the outer rows).
/// Each call is read, not run: the builder's methods are generic over the
/// row and the key, and a native binary has no code for an instantiation only
/// a quotation names (`OrderByDescending<Card, DateTimeOffset>`, docs/aot.md).
/// The definition is carried as a `Query<obj>`, which only its definition
/// matters to.
and innerCheckable (what: string) ctx (whole: Expr) (q: Expr) : ICheckable =
    let carry (d: QueryDef) = Query<obj>(d)

    let quoted (a: Expr) =
        match a with
        | QuoteTyped x
        | QuoteRaw x -> x
        | _ -> fail a $"the {what} query takes a quotation here"

    let rec build (e: Expr) : ICheckable =
        match e with
        | Call(None, mi, [ x ]) when mi.DeclaringType.FullName = "Plasm.TieQueries" ->
            carry (Ties.def (mi.GetGenericArguments().[0]) (mi.Name = "referring") x)
        | Call(Some _, mi, args) when mi.DeclaringType = typeof<QueryBuilder> ->
            let source (a: Expr) : Query<obj> = carry (build a).Def

            let tableOf (t: Type) =
                if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Table<_>> then Some(t.GetGenericArguments().[0]) else None

            let built =
                try
                    match mi.Name, args with
                    | "For", [ src; body ] ->
                        let from =
                            match tableOf src.Type with
                            | Some t -> carry (QueryDef.Empty t)
                            | None -> source src

                        match body with
                        | QuoteTyped b
                        | QuoteRaw b -> (LetLoads.projection from.Def b: Projection<obj>) :> ICheckable
                        | _ -> from :> ICheckable
                    | ("Yield" | "Zero"), _ -> carry (QueryDef.Empty(mi.ReturnType.GetGenericArguments().[0])) :> ICheckable
                    | "Where", [ s; p ] -> QueryOps.where (source s) (quoted p) :> ICheckable
                    | ("OrderBy" | "ThenBy"), [ s; k ] -> QueryOps.order (source s) (quoted k) SortDirection.Ascending :> ICheckable
                    | ("OrderByDescending" | "ThenByDescending"), [ s; k ] -> QueryOps.order (source s) (quoted k) SortDirection.Descending :> ICheckable
                    | ("Take" | "Skip"), [ s; c ] -> QueryOps.take (mi.Name.ToLowerInvariant()) (source s) (Expr.Cast<int>(quoted c)) :> ICheckable
                    | "GroupBy", [ s; k ] -> QueryOps.groupBy (source s) (quoted k) :> ICheckable
                    | "Having", [ s; p ] -> QueryOps.having (source s) (quoted p) :> ICheckable
                    | ("Join" | "LeftJoin"), [ outer; inner; outerKey; innerKey; _ ] ->
                        let def =
                            match tableOf outer.Type with
                            | Some t -> QueryDef.Empty t
                            | None -> { (build outer).Def with Template = None }

                        let target =
                            match tableOf inner.Type with
                            | Some t -> t
                            | None -> fail inner $"the {what} query joins a table"

                        let kind = if mi.Name = "Join" then InnerJoin else LeftJoin
                        carry (QueryOps.join def kind target (quoted outerKey) (quoted innerKey)) :> ICheckable
                    | "Select", [ s; projection ] -> Projection<obj>((build s).Def, quoted projection) :> ICheckable
                    | "SelectDistinct", [ s; projection ] -> Projection<obj>({ (build s).Def with Distinct = true }, quoted projection) :> ICheckable
                    | name, _ -> fail e $"the {what} query uses {name}, which Plasm cannot read in a query written in place"
                with
                | TranslationFailure _ -> reraise ()
                | ex -> fail e $"the {what} query could not be built: {Err.describeExn ex}"

            built
        | _ when isRowDependent ctx e -> fail e $"{what} takes a query written in place, plasm {{ for x in source do where (...) }}"
        | _ ->
            match evaluate ctx e with
            | :? ICheckable as c -> c
            | _ -> fail whole $"{what} takes a query"

    build q

// ---------------------------------------------------------------------------
// Query-level translation
// ---------------------------------------------------------------------------

let private bindLambda (ctx: Ctx) (binding: Binding) (lambda: Expr) =
    match normalize lambda with
    | Lambda(v, body) -> { ctx with Vars = ctx.Vars.Add(v, binding) }, body
    | other -> fail other "expected a lambda"

/// Binds a lambda over the variable space. Operations written before any join
/// take the root record; later ones take the tuple of all sources so far.
let private bindSpace (ctx: Ctx) (space: Binding list) (lambda: Expr) =
    match normalize lambda with
    | Lambda(v, body) when space.Length > 1 && FSharpType.IsTuple v.Type ->
        // Shape the sources like the lambda's tuple type: each element that is
        // itself a tuple takes as many sources as it has elements.
        let rec shape (t: Type) (sources: Binding list) : Space * Binding list =
            if FSharpType.IsTuple t then
                let items, rest =
                    FSharpType.GetTupleElements t
                    |> Array.fold
                        (fun (acc, remaining) et ->
                            let item, rest = shape et remaining
                            item :: acc, rest)
                        ([], sources)

                Sources(List.rev items), rest
            else
                match sources with
                | b :: rest -> Source b, rest
                | [] -> fail lambda "the query's row has fewer sources than this lambda's tuple"

        let tree, _ = shape v.Type space
        { ctx with Spaces = ctx.Spaces.Add(v, tree) }, body
    | Lambda(v, body) -> { ctx with Vars = ctx.Vars.Add(v, space.Head) }, body
    | other -> fail other "expected a lambda"

let private orderItem ctx (body: Expr) (dir: SortDirection) : Sql.OrderItem =
    if mentionsArg body then
        invalid "a prepared query cannot order by an argument: the ordering is part of the query's shape"

    let desc = (dir = SortDirection.Descending)

    { Expr = ordered ctx body
      Desc = desc
      // Match F# ordering of options: None sorts before Some.
      NullsFirst = if Reflection.isOption body.Type then Some(not desc) else None }

let translateWhereOver ctx space (lambda: Expr) =
    let ctx, body = bindSpace ctx space lambda
    let sql = predicate ctx body

    if containsAggregate sql then
        invalid
            $"Agg functions cannot be used in where, which filters rows before they are grouped; use having (in: {show body})"

    sql

let private translateHavingOver ctx space (lambda: Expr) =
    let ctx, body = bindSpace ctx space lambda
    predicate ctx body

let translateOrderOver ctx space (lambda: Expr, dir: SortDirection) =
    let ctx, body = bindSpace ctx space lambda
    orderItem ctx body dir

let translateWhere ctx binding (lambda: Expr) = translateWhereOver ctx [ binding ] lambda

let translateOrder ctx binding (lambda: Expr, dir: SortDirection) : Sql.OrderItem =
    translateOrderOver ctx [ binding ] (lambda, dir)

let entityColumns (alias: string) (s: SchemaInfo) = Sql.readFields alias s.Fields

let private appendKey (orders: Sql.OrderItem list) (key: Sql.Expr) =
    if orders |> List.exists (fun o -> o.Expr = key) then
        orders
    else
        orders
        @ [ { Expr = key
              Desc = false
              NullsFirst = None } ]

/// Every column of a source's key, in order: one, or a composite key's parts.
let private keyColumns (alias: string) (s: SchemaInfo) =
    s.Key.Parts |> List.map (fun p -> Sql.Column(alias, p.Name))

let private appendKeys (orders: Sql.OrderItem list) (alias: string) (s: SchemaInfo) =
    keyColumns alias s |> List.fold appendKey orders

/// An ordering by an option needs its NULLS clause only where a row may hold
/// None (readable-sql.feature): not where the query's own conditions prove the
/// column present, by testing it, comparing it, or testing the union case whose
/// required field it holds. A clause the planner need not honour lets an
/// ordinary index serve the order.
let private withoutNeedlessNulls (space: Binding list) (wheres: Sql.Expr list) (orders: Sql.OrderItem list) =
    let rec conjuncts (e: Sql.Expr) =
        match e with
        | Sql.Binary("AND", a, b) -> conjuncts a @ conjuncts b
        | e -> [ e ]

    let all = wheres |> List.collect conjuncts

    let comparison (op: string) =
        match op with
        | "="
        | "<>"
        | "<"
        | "<="
        | ">"
        | ">=" -> true
        | _ -> false

    // The case `stored` of a union in `alias`'s row holds `column` in a required field.
    let caseHolds (alias: string) (tag: string) (stored: string) (column: string) =
        space
        |> List.exists (fun b ->
            b.Alias = alias
            && b.Schema.Fields
               |> Array.exists (fun f ->
                   match f.Union with
                   | Some u when u.TagColumn = tag ->
                       u.Cases
                       |> Array.exists (fun c ->
                           $"'{c.Stored}'" = stored
                           && c.Parts
                              |> List.exists (fun p ->
                                  match p with
                                  | Leaf leaf -> not leaf.IsOption && leaf.Column = column
                                  | Nested _ -> false))
                   | _ -> false))

    let present (col: Sql.Expr) =
        all
        |> List.exists (fun c ->
            match c, col with
            | Sql.IsNotNull x, _ -> x = col
            | Sql.Binary(op, a, b), _ when comparison op && (a = col || b = col) -> true
            | Sql.AnyOf(a, _, _), _
            | Sql.Like(a, _), _ -> a = col
            | Sql.Binary("=", Sql.Column(alias, tag), Sql.Literal stored), Sql.Column(alias2, column) when alias = alias2 ->
                caseHolds alias tag stored column
            | _ -> false)

    orders
    |> List.map (fun o ->
        match o.NullsFirst, o.Expr with
        | Some _, (Sql.Column _ as col) when present col -> { o with NullsFirst = None }
        | _ -> o)

/// Whether an ordering already tells a source's rows apart: it orders by the
/// key, or by a column that is unique and never NULL (readable-sql.feature).
let private tellsApart (alias: string) (s: SchemaInfo) (orders: Sql.OrderItem list) =
    orders
    |> List.exists (fun o ->
        (not s.Key.Composite && o.Expr = Sql.Column(alias, s.Key.Column))
        || s.Fields |> Array.exists (fun f -> f.Unique && not f.IsOption && f.Parts.Length = 1 && o.Expr = Sql.Column(alias, f.Column)))

/// Ensures a deterministic order for pagination: the primary key is appended as
/// a final tiebreaker unless the ordering already tells the rows apart.
let stableOrder (alias: string) (s: SchemaInfo) (orders: Sql.OrderItem list) =
    if tellsApart alias s orders then orders else appendKeys orders alias s

/// Over joined rows, a row is identified by the keys of every source, but a
/// source joined on its own key has at most one row for each row before it,
/// so it needs no tiebreaker of its own.
let private stableOrderOver (space: Binding list) (joins: Sql.Join list) orders =
    space
    |> List.fold
        (fun os b ->
            let byOwnKey =
                joins
                |> List.exists (fun j ->
                    j.Alias = b.Alias
                    && (match j.On with
                        | Sql.Binary("=", x, y) -> x = keyOf b || y = keyOf b
                        | _ -> false))

            if byOwnKey || tellsApart b.Alias b.Schema os then os else appendKeys os b.Alias b.Schema)
        orders

let wrap f =
    try
        Ok(f ())
    with
    | TranslationFailure err -> Error err
    | PlasmConfigurationException msg -> Error(TranslationError.InvalidQuery msg)

/// Translates the scalar part of a query (filters, ordering, paging) over a root alias.
let translateBody (ctx: Ctx) (def: QueryDef) (alias: string) (schema: SchemaInfo) =
    let binding =
        { Alias = alias
          Schema = schema
          Optional = false }

    let wheres = def.Wheres |> List.map (translateWhere ctx binding)
    let orders = def.Orders |> List.map (translateOrder ctx binding)
    wheres, orders

let private checkProblems (def: QueryDef) =
    match def.Problems with
    | p :: _ -> invalid p
    | [] -> ()

let private schemaOf (model: Model) (t: Type) =
    match model.Schema t with
    | Ok s -> s
    | Error e -> raise (TranslationFailure e)

/// The variable space of a query: the root, then each joined source, with the
/// SQL joins that bring them in.
let private joinSpace (ctx: Ctx) (def: QueryDef) (select: Expr option) (root: SchemaInfo) : Binding list * Sql.Join list =
    let rootBinding =
        { Alias = ctx.Name(Naming.root def select, root.Table)
          Schema = root
          Optional = false }

    def.Joins
    |> List.fold
        (fun (space: Binding list, joins: Sql.Join list) j ->
            let target = schemaOf ctx.Model j.Target

            let b =
                { Alias = ctx.Name(Naming.over j.Target j.InnerKey, target.Table)
                  Schema = target
                  Optional = (j.Kind = LeftJoin) }

            let outerCtx, outerKey = bindSpace ctx space j.OuterKey
            let innerCtx, innerKey = bindLambda ctx { b with Optional = false } j.InnerKey

            let on =
                match outerKey, innerKey with
                | NewTuple xs, NewTuple ys when xs.Length = ys.Length ->
                    List.zip xs ys
                    |> List.map (fun (x, y) -> Sql.Binary("=", scalar outerCtx x, scalar innerCtx y))
                    |> Sql.conj
                | x, y ->
                    if not (isRowDependent outerCtx x) || not (isRowDependent innerCtx y) then
                        fail j.InnerKey "both sides of a join condition must read the joined sources"

                    Sql.Binary("=", scalar outerCtx x, scalar innerCtx y)

            space @ [ b ],
            joins
            @ [ { Left = (j.Kind = LeftJoin)
                  Table = target.Table
                  Alias = b.Alias
                  On = on } ])
        ([ rootBinding ], [])

/// Keeps the rows whose number among their value's rows (`__rn`, numbered in
/// a derived table named ranked, never the name of a table inside it) is
/// within the offset and limit: a load's take per outer record, and
/// pageEach's page per value.
let private pageRanked (numbered: Sql.Select) (columns: (Sql.Expr * string) list) (limit: Sql.Expr option) (offset: Sql.Expr option) =
    let rn = Sql.Column("ranked", "__rn")

    let first =
        match offset with
        | Some off -> [ Sql.Binary(">", rn, off) ]
        | None -> []

    let last =
        match limit, offset with
        | Some l, Some off -> [ Sql.Binary("<=", rn, Sql.Binary("+", off, l)) ]
        | Some l, None -> [ Sql.Binary("<=", rn, l) ]
        | None, _ -> []

    { Sql.select (Sql.Subquery(numbered, "ranked")) with
        Columns = columns
        Where = first @ last
        OrderBy = [ { Expr = rn; Desc = false; NullsFirst = None } ] }

/// pageEach: `sel`'s rows numbered per `key` in its own order, each value
/// keeping its rows from `skip`, `take` of them; `columns` read them from
/// the numbered rows.
let private pagedEach (sel: Sql.Select) (columns: (Sql.Expr * string) list) (key: Sql.Expr) (skip: int) (take: int) =
    let numbered =
        { sel with
            Columns = sel.Columns @ [ Sql.RowNumber(key, sel.OrderBy), "__rn" ]
            OrderBy = []
            Limit = None
            Offset = None }

    pageRanked numbered columns (Some(Sql.count take)) (if skip > 0 then Some(Sql.count skip) else None)

let private limitExprs (def: QueryDef) =
    let fromArg (i: int) =
        Sql.Param(box (Sql.Bound((fun args -> box (int64 (unbox<int> args.[i]))), $"arg{i + 1}")))

    let limit =
        match def.LimitArg with
        | Some i -> Some(fromArg i)
        // repo.One's 2 is Plasm's own, written (readable-sql.feature).
        | None when def.ReadsTwo -> Some(Sql.Literal "2")
        | None -> def.Limit |> Option.map Sql.count

    let offset =
        match def.OffsetArg with
        | Some i -> Some(fromArg i)
        | None -> def.Offset |> Option.map Sql.count

    limit, offset

/// Root entity query (no joins; joined queries run as projections).
let rootSelect (model: Model) (vol: bool ref) (def: QueryDef) : Result<Sql.Select * SchemaInfo, TranslationError> =
    wrap (fun () ->
        checkProblems def
        let schema = schemaOf model def.Root

        if not def.Joins.IsEmpty then
            invalid "a joined query returns combined rows; execute it with repo.All (it is read as a projection)"

        if not def.GroupBy.IsEmpty || not def.Havings.IsEmpty then
            invalid "a grouped query returns summaries, not records; finish it with select (grouped keys and Agg functions)"

        let ctx = newCtxWith model vol
        let alias = ctx.Name(Naming.root def None, schema.Table)
        let wheres, orders = translateBody ctx def alias schema

        if def.Paged && orders.IsEmpty && not def.ReadsTwo then
            invalid
                $"take/skip on {schema.Name} requires an explicit ordering; pages are not stable without one (the primary key is appended as a tiebreaker to any ordering you give)"

        let orders = if def.Paged && not def.ReadsTwo then stableOrder alias schema orders else orders

        let orders =
            withoutNeedlessNulls [ { Alias = alias; Schema = schema; Optional = false } ] wheres orders

        let limit, offset = limitExprs def

        match def.PageEach with
        | None ->
            { Sql.select (Sql.Table(schema.Table, alias)) with
                Columns = entityColumns alias schema
                Where = wheres
                OrderBy = orders
                Limit = limit
                Offset = offset },
            schema
        | Some(key, skip, take) ->
            // Numbered with the stored columns, read as the record's outside.
            let binding = { Alias = alias; Schema = schema; Optional = false }
            let plain = [ for f in schema.Fields do for part in f.Parts -> Sql.Column(alias, part.Name), part.Name ]

            let inner =
                { Sql.select (Sql.Table(schema.Table, alias)) with
                    Columns = plain
                    Where = wheres
                    OrderBy = orders }

            pagedEach inner (entityColumns "ranked" schema) (translateOrder ctx binding (key, SortDirection.Ascending)).Expr skip take, schema)

/// True when a SQL expression reads a column outside the grouping keys and
/// outside any aggregate.
let rec private ungrouped (groups: Sql.Expr list) (e: Sql.Expr) =
    if List.contains e groups then
        false
    else
        match e with
        | Sql.Column _ -> true
        | Sql.Aggregate _ -> false
        | Sql.Binary(_, a, b)
        | Sql.NotDistinctFrom(a, b)
        | Sql.DistinctFrom(a, b)
        | Sql.Like(a, b) -> ungrouped groups a || ungrouped groups b
        | Sql.Not a
        | Sql.IsNotTrue a
        | Sql.IsNull a
        | Sql.IsNotNull a
        | Sql.AnyOf(a, _, _)
        | Sql.Cast(a, _, _)
        | Sql.AsText a -> ungrouped groups a
        | Sql.Func(_, xs) -> List.exists (ungrouped groups) xs
        | Sql.Fragment ps ->
            ps
            |> List.exists (function
                | Choice2Of2 x -> ungrouped groups x
                | _ -> false)
        | _ -> false

/// The translated clauses of a (possibly joined, grouped) query.
type private Core =
    { Ctx: Ctx
      Schema: SchemaInfo
      Space: Binding list
      Joins: Sql.Join list
      Wheres: Sql.Expr list
      Groups: Sql.Expr list
      Grouped: bool
      Havings: Sql.Expr list
      Orders: Sql.OrderItem list
      Limit: Sql.Expr option
      Offset: Sql.Expr option
      /// pageEach's key, skip and take.
      Each: (Sql.Expr * int * int) option }

    member c.Select(columns) =
        let select =
            { Sql.select (Sql.Table(c.Schema.Table, c.Space.Head.Alias)) with
                Columns = columns
                Joins = c.Joins
                Where = c.Wheres
                GroupBy = c.Groups
                Having = c.Havings
                OrderBy = c.Orders
                Limit = c.Limit
                Offset = c.Offset }

        match c.Each with
        | None -> select
        | Some(key, skip, take) -> pagedEach select [ for _, alias in columns -> Sql.Column("ranked", alias), alias ] key skip take

let private core (model: Model) (vol: bool ref) (def: QueryDef) (select: Expr option) : Core =
    checkProblems def
    let schema = schemaOf model def.Root

    let ctx = newCtxWith model vol
    let space, joins = joinSpace ctx def select schema
    let wheres = def.Wheres |> List.map (translateWhereOver ctx space)

    let groups =
        def.GroupBy
        |> List.collect (fun g ->
            let c, body = bindSpace ctx space g

            match body with
            | NewTuple xs -> xs |> List.map (scalar c)
            | x -> [ scalar c x ])

    let grouped = not groups.IsEmpty || not def.Havings.IsEmpty
    let havings = def.Havings |> List.map (translateHavingOver ctx space)

    let orders =
        def.Orders
        |> List.map (fun (lambda, dir) ->
            let o = translateOrderOver ctx space (lambda, dir)

            if grouped && ungrouped groups o.Expr then
                let _, body = bindSpace ctx space lambda
                invalid $"the ordering {show body} is neither grouped nor aggregated; order by a groupBy key or an Agg function"

            o)

    if def.Paged && orders.IsEmpty && not def.ReadsTwo then
        invalid
            $"take/skip on {schema.Name} requires an explicit ordering; pages are not stable without one (the key of every source is appended as a tiebreaker)"

    let orders =
        if not def.Paged || def.ReadsTwo then orders
        elif grouped then groups |> List.fold (fun os g -> appendKey os g) orders
        else stableOrderOver space joins orders

    let limit, offset = limitExprs def

    let each =
        def.PageEach
        |> Option.map (fun (key, skip, take) ->
            if grouped then
                invalid "pageEach cannot page a grouped query: it pages rows per value, and a grouped query's rows are its groups"

            (translateOrderOver ctx space (key, SortDirection.Ascending)).Expr, skip, take)

    { Ctx = ctx
      Schema = schema
      Space = space
      Joins = joins
      Wheres = wheres
      Groups = groups
      Grouped = grouped
      Havings = havings
      Orders = withoutNeedlessNulls space wheres orders
      Limit = limit
      Offset = offset
      Each = each }

/// `SELECT COUNT(*)` for a query; a page or a grouping is counted as a whole
/// (rows of the page, or groups).
let countSelect (model: Model) (vol: bool ref) (def: QueryDef) : Result<Sql.Select, TranslationError> =
    wrap (fun () ->
        let c = core model vol def None
        let count = [ Sql.Aggregate("COUNT", false, None), "n" ]

        if def.Paged || c.Grouped then
            let inner = c.Select [ Sql.Literal "1", "c0" ]
            { Sql.select (Sql.Subquery(inner, "q")) with Columns = count }
        else
            { c.Select count with OrderBy = [] })

/// Reads at most one row: the query matches something.
let existsSelect (model: Model) (vol: bool ref) (def: QueryDef) : Result<Sql.Select, TranslationError> =
    wrap (fun () ->
        let c = core model vol def None
        let one = [ Sql.Literal "1", "c0" ]

        if def.Paged || c.Grouped then
            { Sql.select (Sql.Subquery(c.Select one, "q")) with
                Columns = one
                Limit = Some(Sql.Literal "1") }
        else
            { c.Select one with
                OrderBy = []
                Limit = Some(Sql.Literal "1") })

// ---------------------------------------------------------------------------
// Projections (and joined queries)
// ---------------------------------------------------------------------------

/// How to rebuild a projected value from a row.
type Shape =
    /// Read result column `index`, converting with `convert`.
    | Leaf of index: int * providerType: Type * convert: (obj -> Result<obj, string>) * label: string
    /// A whole record from consecutive columns starting at `start`; `optional`
    /// for a left-joined source (None when its key is NULL).
    | EntityLeaf of start: int * schema: SchemaInfo * optional: bool
    /// A field stored in several columns (a union with fields): the result
    /// columns of its parts, made one value by `fromColumns`, as a whole
    /// record's field is.
    | Parts of columns: (int * Type) list * fromColumns: (obj list -> Result<obj, string>) * model: string * field: string
    /// A list a record owns or links, selected on its own: read with one
    /// more command for every row's key (result column `key`), as the whole
    /// record's list is.
    | Loaded of key: int * schema: SchemaInfo * assoc: string
    | Const of obj
    | Build of construct: (obj[] -> obj) * parts: Shape list

/// Translates a projection, or (with `select = None`) a joined query whose rows
/// are the tuple of its sources.
/// `selectDistinct` (distinct.feature): SELECT DISTINCT, ordered only by
/// selected values, never paged.
let private distinct (def: QueryDef) (sel: Sql.Select) (shape: Shape) : Sql.Select * Shape =
    let rec wholeRecord (s: Shape) =
        match s with
        | EntityLeaf(_, schema, _) -> Some schema.Name
        | Loaded(_, schema, assoc) -> Some $"{schema.Name}'s {assoc}"
        | Build(_, parts) -> parts |> List.tryPick wholeRecord
        | _ -> None

    match wholeRecord shape with
    | Some name -> invalid $"a whole {name} is unique by its key, so selectDistinct would keep every one; select the values to make distinct"
    | None -> ()

    if def.Paged then
        invalid
            "take and skip cannot be used with selectDistinct: in the query they come first and would page the rows, but the database pages the values after removing duplicates"

    let rec bare (e: Sql.Expr) =
        match e with
        | Sql.AsText x -> bare x
        | Sql.Cast(x, _, _) -> bare x
        | x -> x

    let label (i: int) =
        match List.tryItem i def.Orders with
        | Some(lambda, _) ->
            match normalize lambda with
            | Lambda(_, body) -> show body
            | other -> show other
        | None -> "the order"

    // The database orders distinct rows only by what they hold: the order is
    // applied outside, over the selected columns ("d"), and an order that
    // still reads any other column is refused.
    let selected = sel.Columns |> List.map (fun (c, alias) -> bare c, alias)

    let rec over (e: Sql.Expr) : Sql.Expr =
        match selected |> List.tryFind (fun (c, _) -> c = bare e) with
        | Some(_, alias) -> Sql.Column("d", alias)
        | None ->
            match e with
            | Sql.Binary(op, x, y) -> Sql.Binary(op, over x, over y)
            | Sql.Not x -> Sql.Not(over x)
            | Sql.IsNotTrue x -> Sql.IsNotTrue(over x)
            | Sql.IsNull x -> Sql.IsNull(over x)
            | Sql.IsNotNull x -> Sql.IsNotNull(over x)
            | Sql.NotDistinctFrom(x, y) -> Sql.NotDistinctFrom(over x, over y)
            | Sql.DistinctFrom(x, y) -> Sql.DistinctFrom(over x, over y)
            | Sql.Cast(x, t, arr) -> Sql.Cast(over x, t, arr)
            | Sql.AsText x -> Sql.AsText(over x)
            | Sql.Like(x, y) -> Sql.Like(over x, over y)
            | Sql.Func(f, xs) -> Sql.Func(f, List.map over xs)
            | Sql.Case(branches, otherwise) -> Sql.Case(branches |> List.map (fun (c, v) -> over c, over v), over otherwise)
            | Sql.AnyOf(x, vs, cast) -> Sql.AnyOf(over x, vs, cast)
            | Sql.Fragment parts -> Sql.Fragment(parts |> List.map (function Choice2Of2 x -> Choice2Of2(over x) | text -> text))
            | other -> other

    let rec readsOther (e: Sql.Expr) =
        match e with
        | Sql.Column(alias, _) -> alias <> "d"
        | Sql.Binary(_, x, y)
        | Sql.NotDistinctFrom(x, y)
        | Sql.DistinctFrom(x, y)
        | Sql.Like(x, y) -> readsOther x || readsOther y
        | Sql.Not x
        | Sql.IsNotTrue x
        | Sql.IsNull x
        | Sql.IsNotNull x
        | Sql.Cast(x, _, _)
        | Sql.AsText x
        | Sql.AnyOf(x, _, _) -> readsOther x
        | Sql.Func(_, xs) -> List.exists readsOther xs
        | Sql.Case(branches, otherwise) -> branches |> List.exists (fun (c, v) -> readsOther c || readsOther v) || readsOther otherwise
        | Sql.Fragment parts -> parts |> List.exists (function Choice2Of2 x -> readsOther x | _ -> false)
        | Sql.Aggregate _
        | Sql.Exists _
        | Sql.RowNumber _ -> true
        | _ -> false

    let orders =
        sel.OrderBy
        |> List.mapi (fun i o ->
            let e = over o.Expr

            if readsOther e then
                invalid
                    $"selectDistinct can order only by values it selects; {label i} is not selected (select it too, or order by a selected value)"

            { o with Expr = e })

    if orders.IsEmpty then
        { sel with Distinct = true }, shape
    else
        let inner = { sel with Distinct = true; OrderBy = [] }

        { Sql.select (Sql.Subquery(inner, "d")) with
            Columns = sel.Columns |> List.map (fun (_, alias) -> Sql.Column("d", alias), alias)
            OrderBy = orders },
        shape

let projection (model: Model) (vol: bool ref) (def: QueryDef) (select: Expr option) : Result<Sql.Select * Shape, TranslationError> =
    wrap (fun () ->
        let c = core model vol def select
        let ctx, space = c.Ctx, c.Space

        if c.Grouped && select.IsNone then
            invalid "a grouped query returns summaries, not records; finish it with select (grouped keys and Agg functions)"

        let columns = ResizeArray<Sql.Expr * string>()
        let labels = ResizeArray<string>()

        // A record's field names the one column it reads (readable-sql.feature);
        // anything else, or a name already taken, is c0, c1, ...
        let mutable fieldName: string option = None
        let taken = Collections.Generic.HashSet<string>()

        let add (sql: Sql.Expr) (label: string) =
            let i = columns.Count

            let alias =
                match fieldName with
                | Some n when taken.Add n -> n
                | _ -> $"c{i}"

            fieldName <- None
            taken.Add alias |> ignore
            columns.Add(sql, alias)
            labels.Add label
            i

        let addEntity (b: Binding) =
            fieldName <- None

            if c.Grouped then
                invalid $"a whole {b.Schema.Name} cannot be selected from a grouped query; select grouped keys and Agg functions"


            let start = columns.Count

            for f in b.Schema.Fields do
                for p in f.Parts do
                    add (Sql.readPart p (Sql.Column(b.Alias, p.Name))) $"{b.Schema.Name}.{f.Name}" |> ignore

            EntityLeaf(start, b.Schema, b.Optional)

        let addLeaf (ctx: Ctx) (e: Expr) =
            match e with
            // A union with fields: every column of its cases, so that whichever
            // case a row holds is read whole (union-fields.feature).
            | RowField ctx (b, f) when f.Composite && f.Union.IsSome ->
                let columns = f.Parts |> List.map (fun p -> add (Sql.readPart p (Sql.Column(b.Alias, p.Name))) $"{b.Schema.Name}.{f.Name}", p.ProviderType)
                Parts(columns, f.FromColumns, b.Schema.Name, f.Name)
            | RowField ctx (b, f) when f.Value.IsSome ->
                fail e $"{b.Schema.Name}.{f.Name} is a {f.ScalarType.Name} stored in several columns; select its fields, or the whole {b.Schema.Name}"
            | RowField ctx (b, f) ->
                let i = add (Sql.readColumn f (Sql.Column(b.Alias, f.Column))) (show e)
                Leaf(i, f.ProviderType, f.FromDb, f.Name)
            | OptionalField ctx (b, f) ->
                let i = add (Sql.readColumn f (Sql.Column(b.Alias, f.Column))) (show e)

                let convert =
                    if f.IsOption then
                        f.FromDb // Option.bind over an optional field: NULL is None.
                    else
                        let some = Reflection.makeSome e.Type

                        fun (v: obj) ->
                            if isNull v || v = box DBNull.Value then
                                Ok null
                            else
                                f.FromDb v |> Result.map some

                Leaf(i, f.ProviderType, convert, $"{b.Schema.Name}.{f.Name}")
            | RowBinding ctx b -> addEntity b
            // A list the record owns or links: its key read beside the row, the
            // list a round trip later (build-query.feature).
            | RowAssoc ctx (b, a) when (a.Owned || a.Linked) && not c.Grouped ->
                let key = b.Schema.Key
                let i = add (Sql.readColumn key (Sql.Column(b.Alias, key.Column))) $"{b.Schema.Name}.{key.Name}"
                Loaded(i, b.Schema, a.Name)
            | RowAssoc ctx (_, a) -> fail e $"'{a.Name}' cannot be projected on its own; select the whole record, which comes with it"
            // A union's case field (directly, or through a member such as
            // `match c.Stage with Doing(_, since) -> Some since | _ -> None`):
            // read with the field's own type, and named by it.
            | UnionLeaf ctx (alias, f)
            | UnionOptionalLeaf ctx (alias, f) ->
                let part =
                    { Name = f.Column
                      ProviderType = f.ProviderType
                      Mapping = f.Mapping }

                let i = add (Sql.readPart part (Sql.Column(alias, f.Column))) $"{f.Name} ({f.Column})"

                let convert =
                    if Reflection.isOption e.Type && not f.IsOption then
                        let some = Reflection.makeSome e.Type
                        fun (v: obj) -> if isNull v || v = box DBNull.Value then Ok null else f.FromDb v |> Result.map some
                    else
                        f.FromDb

                Leaf(i, f.ProviderType, convert, $"{f.Name} ({f.Column})")
            | _ ->
                let t = e.Type
                let scalarT = if Reflection.isOption t then Reflection.elementType t else t

                if (model.ProviderTypeOf scalarT).IsNone then
                    fail e $"computed projection of type {Reflection.typeName t} is not supported"

                let sql =
                    match model.StoreTypeOf scalarT with
                    | Some _ -> Sql.AsText(scalar ctx e) // e.g. Agg.max over an enum column
                    | None -> scalar ctx e

                let i = add sql (show e)
                // Computed values (arithmetic, aggregates, fragments) are read as
                // whatever the database returns and converted to the F# type.
                Leaf(i, typeof<obj>, (fun v -> model.ScalarFromDb(t, v)), show e)

        let rec shape (ctx: Ctx) (e: Expr) : Shape =
            if not (isRowDependent ctx e) then
                if mentionsArg e then
                    invalid $"a prepared-query argument cannot be selected; the caller already has its value (in: {show e})"

                Const(evaluate ctx e)
            else
                match e with
                | NewTuple parts ->
                    let ctor = Shape.tuple e.Type
                    Build(ctor, parts |> List.map (fun p -> fieldName <- None; shape ctx p))
                | NewRecord(t, parts) ->
                    let record = Shape.record t
                    let ctor = record.Construct
                    let names = record.Fields |> Array.map (fun f -> f.Name) |> List.ofArray

                    Build(
                        ctor,
                        List.map2
                            (fun name p ->
                                fieldName <- Some name
                                let part = shape ctx p
                                fieldName <- None
                                part)
                            names
                            parts
                    )
                | NewUnionCase(c, parts) ->
                    let ctor = FSharpValue.PreComputeUnionConstructor(c, true)
                    Build(ctor, parts |> List.map (shape ctx))
                | NewObject(ci, parts) -> Build((fun args -> ci.Invoke args), parts |> List.map (shape ctx))
                // The cells of a row of chosen fields (BuildQuery.select), each boxed.
                | NewArray(t, parts) when t = typeof<obj> ->
                    Build(box, parts |> List.map (fun p -> fieldName <- None; shape ctx p))
                | Coerce(inner, t) when t = typeof<obj> -> shape ctx inner
                | _ -> addLeaf ctx e

        let s =
            match select with
            | Some sel ->
                let ctx, body = bindSpace ctx space sel
                shape ctx body
            | None when space.Length = 1 -> addEntity space.Head
            | None ->
                // Rows are the tuple of the sources: flat for the computation
                // expression, paired for each pipeline join.
                let entity (b: Binding) =
                    let t = if b.Optional then typedefof<option<_>>.MakeGenericType b.Schema.Type else b.Schema.Type
                    addEntity b, t

                let pack (items: (Shape * Type) list) =
                    match items with
                    | [ single ] -> single
                    | _ ->
                        let t = items |> List.map snd |> Array.ofList |> FSharpType.MakeTupleType
                        Build(Shape.tuple t, items |> List.map fst), t

                let items = space |> List.map entity

                fst (pack items)

        if columns.Count = 0 then
            invalid "projection does not read any column"

        // Grouped (or aggregated) results may only read grouped keys outside
        // aggregates.
        let aggregated = c.Grouped || columns |> Seq.exists (fun (sql, _) -> containsAggregate sql)

        if aggregated then
            columns
            |> Seq.iteri (fun i (sql, _) ->
                if ungrouped c.Groups sql then
                    invalid
                        $"{labels.[i]} is neither grouped nor aggregated; add it to groupBy or wrap it in an Agg function")

        let sel = c.Select(List.ofSeq columns)

        if def.Distinct then
            distinct def sel s
        else
            sel, s)

/// One field of the row `return` builds (loads.feature).
type ReturnField =
    | RootRecord
    | Column of Reflection.PropertyInfo
    | Load of int

/// The row a query's `return` builds from its record and its loads.
type ReturnShape =
    { Type: Type
      Fields: ReturnField list
      Make: obj[] -> obj }

    /// The row for a record and what each load found for it, in order.
    member r.Build (record: obj) (loads: obj[]) =
        r.Make(
            [| for f in r.Fields ->
                   match f with
                   | RootRecord -> record
                   | Column p -> p.GetValue record
                   | Load i -> loads.[i] |]
        )

/// Checks and resolves what `return` builds (loads.feature): a record or
/// an anonymous record whose fields are the query's record, one of its fields,
/// or a name bound by `let!` or `and!`, each name once. Anything else would
/// need evaluating, so it is refused, naming the field.
let returnShape (def: QueryDef) : ReturnShape option =
    match def.Return with
    | None -> None
    | Some r ->
        match r.Shape with
        | Some p -> invalid p
        | None -> ()

        // An anonymous record's fields are in name order, so F# binds its
        // arguments first, keeping the order they are written in.
        let rec written (e: Expr) =
            match e with
            | Let(v, value, rest) -> written (rest.Substitute(fun x -> if x = v then Some value else None))
            | e -> e

        let t, args =
            match written r.Body with
            | NewRecord(t, args) -> t, args
            | _ -> invalid "return builds a record or an anonymous record, naming each field, as in return {| Order = o; Payments = payments |}"

        let names = (Shape.record t).Fields |> Array.map (fun p -> p.Name) |> List.ofArray
        let loadOf (v: Var) = r.Names |> List.tryFindIndex ((=) (Some v))

        let isQuery (t: Type) =
            t.IsGenericType
            && (let d = t.GetGenericTypeDefinition()
                d = typedefof<Query<_>>
                || d = typedefof<Projection<_>>
                || d = typedefof<One<_>>
                || d = typedefof<OneProjection<_>>)

        let example =
            match (Shape.record r.Root.Type).Fields |> Array.tryHead with
            | Some p -> $"{r.Root.Name}.{p.Name}"
            | None -> $"{r.Root.Name}.Field"

        let fields =
            List.zip names args
            |> List.map (fun (name, arg) ->
                match arg with
                | Var v when v = r.Root -> name, RootRecord
                | PropertyGet(Some(Var v), p, []) when v = r.Root -> name, Column p
                // A load goes in the field of its own name, so two loads of one
                // type cannot be crossed.
                | Var v when (loadOf v).IsSome ->
                    let own = string (Char.ToUpperInvariant v.Name.[0]) + v.Name.Substring 1

                    if name <> own && name <> v.Name then
                        invalid $"{v.Name} is returned in the field {name}: a load is returned in the field of its name, {own}"

                    name, Load (loadOf v).Value
                | Var v when r.Lets |> List.exists (fun (x, value) -> x = v && isQuery value.Type) ->
                    invalid $"the field {name} of return is {v.Name}, a query bound with let, which is not loaded: bind it with let!"
                | _ ->
                    invalid
                        $"the field {name} of return is {show arg}: a field of return is the query's record ({r.Root.Name}), one of its fields ({example}), or a name bound by let! or and!, each once; compute it from the rows read")

        r.Names
        |> List.iteri (fun i n ->
            let keyword = if i = 0 then "let!" else "and!"
            let label = n |> Option.map (fun v -> v.Name) |> Option.defaultValue "_"

            match fields |> List.filter (fun (_, f) -> f = Load i) |> List.map fst with
            | [] -> invalid $"{label} is bound by {keyword} but not returned: return it in a field, or remove it, as each load is one more command"
            | [ _ ] -> ()
            | [ a; b ] -> invalid $"{label} is returned twice, in {a} and {b}: return each load once"
            | many -> invalid $"""{label} is returned {many.Length} times, in {String.concat ", " many}: return each load once""")

        let make = (Shape.record t).Construct

        Some
            { Type = t
              Fields = List.map snd fields
              Make = make }

/// One load bound by `let!` or `and!` (loads.feature): the outer query
/// without it, and the inner query as one more load, read in
/// batches of the outer column's values.
type IncludingParts =
    { Outer: QueryDef
      OuterField: FieldMap
      Target: SchemaInfo
      MatchField: FieldMap
      /// The inner query for one batch of outer values.
      Build: Array -> Sql.Select
      /// An inner query ending in `select` or `selectDistinct`: what it reads,
      /// as (value, the matching column's value); None for whole records.
      Projected: Shape option
      /// The type of the list's items: the inner record, what it selects, or
      /// or the row its own `return` builds when it binds loads of its own.
      ElementType: Type
      /// The inner query's own loads, in the order written.
      Nested: IncludingParts list
      /// The one record a tie can find (`referredBy`): an option.
      One: bool
      /// The name it is bound to, for the plan.
      Name: string
      /// The row the inner query's `return` builds, when it binds loads.
      Return: ReturnShape option }

    /// What the outer tuple holds for this level: a list of its items, or an
    /// option of the one.
    member p.HeldType =
        if p.One then typedefof<option<_>>.MakeGenericType p.ElementType
        else typedefof<list<_>>.MakeGenericType p.ElementType

let rec includingParts (model: Model) (vol: bool ref) (def: QueryDef) (including: IncludingDef) : Result<IncludingParts, TranslationError> =
    let lambda = including.Inner

    let op =
        match including.Bound with
        | Some(keyword, name) -> $"{keyword} {name}"
        | None -> "a load"

    wrap (fun () ->
        if not def.Joins.IsEmpty || not def.GroupBy.IsEmpty then
            invalid $"{op} binds a load for each record of a query of one record type: a query that joins or groups cannot bind loads"

        let outerSchema = schemaOf model def.Root

        let o, body =
            match normalize lambda with
            | Lambda(v, b) -> v, b
            | other -> fail other "expected a lambda"

        let ctx =
            { newCtxWith model vol with
                Vars = Map [ o, { Alias = "outer"; Schema = outerSchema; Optional = false } ] }

        let checkable = innerCheckable op ctx lambda body
        let inner = checkable.Def
        checkProblems inner
        let shape = returnShape inner

        if not inner.Joins.IsEmpty || not inner.GroupBy.IsEmpty || not inner.Havings.IsEmpty then
            invalid $"the query of {op} reads one record type: it cannot join or group"

        let target = schemaOf model inner.Root

        for t in model.Types do
            match model.Schema t with
            | Ok s ->
                for a in s.Assocs do
                    if (a.Owned || a.Linked) && a.Target = target.Type then
                        invalid $"{target.Name} records are loaded with their {s.Name}: read the {s.Name}, which comes with them, instead of binding them with {op}"
            | Error _ -> ()

        let rec conjuncts (e: Expr) =
            match e with
            | IfThenElse(a, b, Bool false) -> conjuncts a @ conjuncts b
            | e -> [ e ]

        let mentionsOuter (e: Expr) = e.GetFreeVars() |> Seq.contains o

        // A column, or Some of one: an optional column compared with a plain
        // one (o.CustomerId = Some c.Id) is still one equality of two columns.
        let rec fieldOf (v: Var) (e: Expr) =
            match e with
            | PropertyGet(Some(Var x), p, []) when x = v -> Some p.Name
            | NewUnionCase(c, [ x ]) when c.Name = "Some" && c.DeclaringType.IsGenericType && c.DeclaringType.GetGenericTypeDefinition() = typedefof<option<_>> -> fieldOf v x
            | _ -> None

        let refersOtherwise (c: Expr) =
            fail c $"the query of {op} refers to the outer record only by one equality between an inner and an outer column, as in where (p.OrderId = o.Id)"

        let mutable correlation = None

        let wheres =
            inner.Wheres
            |> List.choose (fun w ->
                match normalize w with
                | Lambda(p, pred) ->
                    let kept =
                        conjuncts (expandTies model pred)
                        |> List.filter (fun c ->
                            if not (mentionsOuter c) then
                                true
                            else
                                let pair =
                                    match c with
                                    | OperatorCall "op_Equality" [ a; b ] ->
                                        match fieldOf p a, fieldOf o b, fieldOf o a, fieldOf p b with
                                        | Some i, Some u, _, _ -> Some(i, u)
                                        | _, _, Some u, Some i -> Some(i, u)
                                        | _ -> None
                                    | _ -> None

                                match pair, correlation with
                                | Some ab, None ->
                                    correlation <- Some ab
                                    false
                                | _ -> refersOtherwise c)

                    match kept with
                    | [] -> None
                    | c :: cs -> Some(Expr.Lambda(p, cs |> List.fold (fun acc x -> Expr.IfThenElse(acc, x, Expr.Value false)) c))
                | other -> fail other "expected a lambda")

        let innerName, outerName =
            match correlation with
            | Some c -> c
            | None -> fail lambda $"the query of {op} refers to the outer record by one equality between an inner and an outer column, as in where (p.OrderId = o.Id)"

        let matchField = target.Field innerName
        let outerField = outerSchema.Field outerName
        let innerDef = { inner with Wheres = wheres }

        // An inner query that binds loads reads whole records; its
        // projection is the tuple the reader builds, not a select.
        let selected = if inner.Includings.IsEmpty then checkable.Select else None

        // Named as its projection names it, from the same query.
        let innerCtx = newCtxWith model vol
        let alias = innerCtx.Name(Naming.root innerDef selected, target.Table)
        let iw, io = translateBody innerCtx innerDef alias target

        if innerDef.Paged && io.IsEmpty then
            invalid $"take/skip in the query of {op} needs an order: without one, which {target.Name} records come first for each {outerSchema.Name} is not defined"

        let ranked =
            stableOrder alias target io
            |> withoutNeedlessNulls [ { Alias = alias; Schema = target; Optional = false } ] iw
        let limit, offset = limitExprs innerDef
        let matchColumn = Sql.Column(alias, matchField.Column)

        let nested =
            inner.Includings
            |> List.map (fun l ->
                match includingParts model vol { inner with Includings = [] } l with
                | Ok p -> p
                | Error e -> raise (TranslationFailure e))

        let projected =
            match selected with
            | None -> None
            | Some select ->
                // The projection without the tie, then the matching column
                // appended (last) so each row can be paired with its record.
                let plainDef = { innerDef with Distinct = false; Limit = None; Offset = None; LimitArg = None; OffsetArg = None }

                let sel, shape =
                    match projection model vol plainDef (Some select) with
                    | Ok r -> r
                    | Error e -> raise (TranslationFailure e)

                let key = Sql.readColumn matchField matchColumn, "__key"
                let keyIndex = sel.Columns.Length
                let withKey = { sel with Columns = sel.Columns @ [ key ] }

                let pair =
                    Build(
                        (fun parts -> box (parts.[0], parts.[1])),
                        [ shape; Leaf(keyIndex, matchField.ProviderType, matchField.FromDb, $"{target.Name}.{matchField.Name}") ]
                    )

                // Checked once here, so a refusal comes before any command.
                if innerDef.Distinct then
                    distinct innerDef withKey shape |> ignore

                Some(withKey, pair)

        let buildProjected (keys: Array) (withKey: Sql.Select) =
            let filtered = { withKey with Where = Sql.AnyOf(matchColumn, keys, Sql.storeType matchField) :: withKey.Where }

            if innerDef.Distinct then
                fst (distinct innerDef filtered (Const null))
            elif not innerDef.Paged then
                filtered
            else
                let numbered = { filtered with Columns = filtered.Columns @ [ Sql.RowNumber(matchColumn, ranked), "__rn" ]; OrderBy = [] }
                pageRanked numbered [ for _, alias in filtered.Columns -> Sql.Column("ranked", alias), alias ] limit offset

        let buildRecords (keys: Array) : Sql.Select =
            let filtered =
                { Sql.select (Sql.Table(target.Table, alias)) with
                    Where = Sql.AnyOf(matchColumn, keys, Sql.storeType matchField) :: iw }

            if not innerDef.Paged then
                { filtered with
                    Columns = entityColumns alias target
                    // By the key, each record has at most one: nothing to order.
                    OrderBy = if matchField.Name = target.Key.Name then [] else ranked }
            else
                // Per outer value: number each one's rows in the inner order.
                let plain =
                    [ for f in target.Fields do
                          for part in f.Parts -> Sql.Column(alias, part.Name), part.Name ]

                let numbered = { filtered with Columns = plain @ [ Sql.RowNumber(matchColumn, ranked), "__rn" ] }

                // Each record's rows keep their order by number; the records'
                // rows need no order among each other.
                pageRanked numbered (entityColumns "ranked" target) limit offset

        { Outer = { def with Includings = [] }
          OuterField = outerField
          Target = target
          MatchField = matchField
          Build =
            match projected with
            | Some(withKey, _) -> fun keys -> buildProjected keys withKey
            | None -> buildRecords
          Projected = projected |> Option.map snd
          ElementType =
            match shape, selected with
            | Some r, _ -> r.Type
            | None, Some select -> (Reflection.functionResult select.Type).Value
            | None, None -> target.Type
          Nested = nested
          One = including.One
          Name = including.Bound |> Option.map snd |> Option.defaultValue "_"
          Return = shape })

// ---------------------------------------------------------------------------
// Bulk writes
// ---------------------------------------------------------------------------

/// SET assignments from a copy-and-update lambda body (`{ o with ... }`):
/// fields read unchanged from `row` are skipped; the key, version, generated
/// fields and relationships cannot be assigned.
let private assignments (ctx: Ctx) (schema: SchemaInfo) (row: Var) (body: Expr) : (string * Sql.Expr) list =
    let parts =
        match body with
        | NewRecord(t, parts) when t = schema.Type -> parts
        | _ ->
            invalid
                $"a bulk change must return a copy of the row, e.g. fun o -> {{ o with Field = value }} (in: {show body})"

    let sets =
        parts
        |> List.indexed
        |> List.collect (fun (i, value) ->
            let unchanged =
                match value with
                | PropertyGet(Some(Var v), p, []) when v = row -> Some p.Name
                | _ -> None

            match schema.Fields |> Array.tryFind (fun f -> f.Index = i) with
            | Some f when unchanged = Some f.Name -> []
            | Some f when f.Name = schema.Key.Name -> invalid $"the primary key {schema.Name}.{f.Name} cannot be changed by a bulk write"
            | Some f when schema.Version |> Option.exists (fun v -> v.Name = f.Name) ->
                invalid $"{schema.Name}.{f.Name} is the version; bulk writes increment it automatically"
            | Some f when f.Generated -> invalid $"{schema.Name}.{f.Name} is generated by the database and cannot be assigned"
            // A union with fields, or a value, is assigned a whole value, set in
            // all its columns.
            | Some f when f.Composite ->
                if isRowDependent ctx value then
                    invalid $"{schema.Name}.{f.Name} is stored in several columns; a bulk write assigns it a value, not one computed from the row"
                else
                    Sql.fieldColumns f (evaluate ctx value)
            | Some f -> [ f.Column, scalar ctx value ]
            | None ->
                match schema.Assocs |> Array.tryFind (fun a -> a.Index = i) with
                | Some a when unchanged = Some a.Name -> []
                | Some a -> invalid $"{schema.Name}.{a.Name} is an owned or linked list, which a bulk write cannot change; write the {schema.Name} with Tx.update"
                | None -> [])

    if sets.IsEmpty then
        invalid $"the bulk change assigns no field of {schema.Name}"

    let version =
        match schema.Version with
        | Some v -> [ v.Column, Sql.Binary("+", Sql.Column(schema.Table, v.Column), Sql.Literal "1") ]
        | None -> []

    sets @ version

let private bulkFilter (model: Model) (vol: bool ref) (def: QueryDef) (what: string) =
    checkProblems def
    let schema = schemaOf model def.Root

    let reject reason =
        invalid $"a bulk {what} applies to every row the query matches; {reason}"

    if not def.Joins.IsEmpty then reject "joins are not supported (filter with exists instead)"
    if not def.Orders.IsEmpty || def.Paged then reject "ordering, take and skip have no meaning there"
    if not def.GroupBy.IsEmpty || not def.Havings.IsEmpty then reject "grouping has no meaning there"

    let ctx = newCtxWith model vol

    let binding =
        { Alias = schema.Table
          Schema = schema
          Optional = false }

    ctx, schema, binding, def.Wheres |> List.map (translateWhere ctx binding)

let bulkUpdate (model: Model) (vol: bool ref) (def: QueryDef) (change: Expr) : Result<Sql.Update, TranslationError> =
    wrap (fun () ->
        let ctx, schema, binding, wheres = bulkFilter model vol def "update"

        match normalize change with
        | Lambda(v, body) ->
            let ctx = { ctx with Vars = ctx.Vars.Add(v, binding) }

            ({ Table = schema.Table
               Set = assignments ctx schema v body
               Where = wheres
               Returning = [] }: Sql.Update)
        | other -> fail other "expected a lambda")

let bulkDelete (model: Model) (vol: bool ref) (def: QueryDef) : Result<Sql.Delete, TranslationError> =
    wrap (fun () ->
        let _, schema, _, wheres = bulkFilter model vol def "delete"

        ({ Table = schema.Table
           Where = wheres
           Returning = [] }: Sql.Delete))

/// Columns named by an upsert's conflict target (a field or a tuple of fields).
let conflictTarget (model: Model) (t: Type) (on: Expr) : Result<string list, TranslationError> =
    wrap (fun () ->
        let schema = schemaOf model t

        match normalize on with
        | Lambda(v, body) ->
            let ctx =
                { newCtx model with
                    Vars =
                        Map.ofList
                            [ v,
                              { Alias = schema.Table
                                Schema = schema
                                Optional = false } ] }

            let column (x: Expr) =
                match x with
                | RowField ctx (b, f) when f.Composite && f.Name = b.Schema.Key.Name ->
                    fail x $"{b.Schema.Name}.{f.Name} is a key of several parts: an insert that skips or updates on a conflict with it is not supported yet"
                | RowField ctx (b, f) when f.Composite ->
                    fail x $"{b.Schema.Name}.{f.Name} is stored in several columns: a conflict target is one column, or a tuple of them"
                | RowField ctx (_, f) -> f.Column
                | _ -> fail x "a conflict target must be a field (or a tuple of fields) with a unique constraint"

            match body with
            | NewTuple xs -> xs |> List.map column
            | x -> [ column x ]
        | other -> fail other "expected a lambda")

/// Assignments for `ON CONFLICT DO UPDATE` from `fun current incoming -> ...`.
let conflictUpdate (model: Model) (vol: bool ref) (t: Type) (change: Expr) : Result<(string * Sql.Expr) list, TranslationError> =
    wrap (fun () ->
        let schema = schemaOf model t

        match normalize change with
        | Lambda(current, Lambda(incoming, body)) ->
            let ctx =
                { newCtxWith model vol with
                    Vars =
                        Map.ofList
                            [ current,
                              { Alias = schema.Table
                                Schema = schema
                                Optional = false }
                              incoming,
                              { Alias = "excluded"
                                Schema = schema
                                Optional = false } ] }

            assignments ctx schema current body
        | other -> fail other "expected fun current incoming -> { current with ... }")
