/// Exact decimal SUM and AVG as SQLite window functions (providers.feature):
/// linear over a running window, where gathering each window's values as
/// JSON was quadratic. Microsoft.Data.Sqlite and SQLitePCLRaw register no
/// window function, so this calls sqlite3_create_window_function in the
/// e_sqlite3 library they load, with blittable arguments only (the same under
/// native AOT). The one function serves the plain aggregate too.
module internal Plasm.Sqlite.Windows

open System
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Text
open System.Threading

[<Literal>]
let private Library = "e_sqlite3"

[<Literal>]
let private Utf8 = 1

/// SQLITE_DETERMINISTIC: the same arguments give the same result.
[<Literal>]
let private Deterministic = 0x800

[<Literal>]
let private Null = 5

type private Step = delegate of context: nativeint * count: int * values: nativeint -> unit
type private Final = delegate of context: nativeint -> unit

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern int private sqlite3_create_window_function(
    nativeint db,
    nativeint name,
    int arguments,
    int textRep,
    nativeint app,
    nativeint step,
    nativeint final,
    nativeint value,
    nativeint inverse,
    nativeint destroy
)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private sqlite3_aggregate_context(nativeint context, int bytes)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern int private sqlite3_value_type(nativeint value)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint private sqlite3_value_text(nativeint value)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern int private sqlite3_value_bytes(nativeint value)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void private sqlite3_result_text(nativeint context, nativeint text, int bytes, nativeint destructor)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void private sqlite3_result_null(nativeint context)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void private sqlite3_result_error(nativeint context, nativeint message, int bytes)

/// A window's running total: the sum of its non-NULL values and their count.
type private Total = { mutable Sum: decimal; mutable Count: int64 }

/// Each window's total, by the id its aggregate context holds. SQLite calls
/// xFinal for every window it stepped, a failed or interrupted query's too,
/// which takes its total out.
let private totals = ConcurrentDictionary<int64, Total>()
let mutable private lastId = 0L

/// How many windows hold a total now: none outside a running query.
let live () = totals.Count

/// The total `context` holds; with `create`, one made the first time.
let private total (context: nativeint) (create: bool) =
    let slot = sqlite3_aggregate_context (context, (if create then 8 else 0))

    if slot = 0n then
        None
    else
        let id = Marshal.ReadInt64 slot

        if id <> 0L then
            match totals.TryGetValue id with
            | true, t -> Some(id, t)
            | _ -> None
        elif create then
            let id = Interlocked.Increment &lastId
            let t = { Sum = 0m; Count = 0L }
            totals.[id] <- t
            Marshal.WriteInt64(slot, id)
            Some(id, t)
        else
            None

let private withText (text: string) (send: nativeint -> int -> unit) =
    let bytes = Encoding.UTF8.GetBytes text
    let handle = GCHandle.Alloc(bytes, GCHandleType.Pinned)

    try
        send (handle.AddrOfPinnedObject()) bytes.Length
    finally
        handle.Free()

/// SQLITE_TRANSIENT: SQLite copies the text before the call returns.
let private transient = nativeint -1

let private resultText context (text: string) =
    withText text (fun p n -> sqlite3_result_text (context, p, n, transient))

let private resultError context (message: string) =
    withText message (fun p n -> sqlite3_result_error (context, p, n))

/// The argument's decimal, None for NULL.
let private argument (values: nativeint) =
    let value = Marshal.ReadIntPtr values

    if sqlite3_value_type value = Null then
        None
    else
        let text = sqlite3_value_text value
        let length = sqlite3_value_bytes value
        Some(Decimals.parse (Marshal.PtrToStringUTF8(text, length)))

/// `f`, its failure the query's error: an exception never returns to SQLite.
let private guarded (context: nativeint) (f: unit -> unit) =
    try
        f ()
    with e ->
        try
            resultError context e.Message
        with _ ->
            ()

let private step =
    Step(fun context _ values ->
        guarded context (fun () ->
            match argument values with
            | Some v ->
                let _, t = (total context true).Value
                t.Sum <- t.Sum + v
                t.Count <- t.Count + 1L
            | None -> total context true |> ignore))

let private inverse =
    Step(fun context _ values ->
        guarded context (fun () ->
            match argument values, total context false with
            | Some v, Some(_, t) ->
                t.Sum <- t.Sum - v
                t.Count <- t.Count - 1L
            | _ -> ()))

/// The window's value now (`average`: the mean), NULL over no values.
let private current (average: bool) (context: nativeint) (t: Total option) =
    match t with
    | Some t when t.Count > 0L -> resultText context (if average then Decimals.divide t.Sum (decimal t.Count) else Decimals.text t.Sum)
    | _ -> sqlite3_result_null context

let private value average =
    Final(fun context -> guarded context (fun () -> current average context (total context false |> Option.map snd)))

let private final average =
    Final(fun context ->
        let held = total context false

        try
            guarded context (fun () -> current average context (held |> Option.map snd))
        finally
            match held with
            | Some(id, _) -> totals.TryRemove id |> ignore
            | None -> ())

// Kept alive for as long as the process: SQLite holds their pointers.
let private sumValue = value false
let private sumFinal = final false
let private avgValue = value true
let private avgFinal = final true

let private pointers =
    lazy
        {| Step = Marshal.GetFunctionPointerForDelegate<Step> step
           Inverse = Marshal.GetFunctionPointerForDelegate<Step> inverse
           SumValue = Marshal.GetFunctionPointerForDelegate<Final> sumValue
           SumFinal = Marshal.GetFunctionPointerForDelegate<Final> sumFinal
           AvgValue = Marshal.GetFunctionPointerForDelegate<Final> avgValue
           AvgFinal = Marshal.GetFunctionPointerForDelegate<Final> avgFinal |}

/// Registers `plasm_decimal_sum(x)` and `plasm_decimal_avg(x)` on the
/// connection `db` (sqlite3*), as window functions and so as aggregates: NULLs
/// left out, NULL over none, as SUM and AVG.
let register (db: nativeint) =
    let p = pointers.Value

    for name, value, final in [ "plasm_decimal_sum", p.SumValue, p.SumFinal; "plasm_decimal_avg", p.AvgValue, p.AvgFinal ] do
        let bytes = Encoding.UTF8.GetBytes(name + "\000")
        let handle = GCHandle.Alloc(bytes, GCHandleType.Pinned)

        try
            let rc =
                sqlite3_create_window_function (db, handle.AddrOfPinnedObject(), 1, Utf8 ||| Deterministic, 0n, p.Step, final, value, p.Inverse, 0n)

            if rc <> 0 then
                invalidOp $"SQLite refused the window function {name} (error {rc})"
        finally
            handle.Free()
