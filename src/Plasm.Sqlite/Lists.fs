/// Lists as SQLite holds them: one canonical JSON array, sent as one
/// parameter (a list of keys, a prepared query's list argument) or stored as a
/// list column's TEXT, and read back as the array the column's type declares.
namespace Plasm.Sqlite

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json

module internal Lists =
    let private inv = CultureInfo.InvariantCulture

    /// A scalar's text as Microsoft.Data.Sqlite binds it (measured on 11.0.0),
    /// so an element of a list column and a scalar column of its type are the
    /// same text, and `element IN (SELECT value FROM json_each(column))` finds
    /// it. None for what JSON holds as a number.
    let text (v: obj) : string option =
        match v with
        | :? string as x -> Some x
        | :? decimal as x -> Some(Decimals.text x)
        | :? Guid as x -> Some(x.ToString().ToUpperInvariant())
        | :? DateTime as x -> Some(x.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", inv))
        | :? DateTimeOffset as x -> Some(x.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", inv))
        | :? DateOnly as x -> Some(x.ToString("yyyy-MM-dd", inv))
        | :? TimeOnly as x -> Some(x.ToString((if x.Ticks % TimeSpan.TicksPerSecond = 0L then "HH:mm:ss" else "HH:mm:ss.fffffff"), inv))
        | :? TimeSpan as x -> Some(x.ToString("c", inv))
        | :? char as x -> Some(string x)
        | _ -> None

    /// A list as one JSON array, canonical: no whitespace, one form for each
    /// value (its scalar's text, or the number SQLite binds it as: a bool as
    /// 1 or 0, an integer exactly, a float as the shortest text that reads back
    /// the same). Lists sent as parameters and list columns are both written so.
    let json (values: Array) : obj =
        use stream = new MemoryStream()

        (use w = new Utf8JsonWriter(stream)
         w.WriteStartArray()

         for v in values do
             match v with
             | null -> w.WriteNullValue()
             | :? int64 as x -> w.WriteNumberValue x
             | :? int as x -> w.WriteNumberValue x
             | :? int16 as x -> w.WriteNumberValue(int x)
             | :? byte as x -> w.WriteNumberValue(int x)
             | :? bool as x -> w.WriteNumberValue(if x then 1 else 0)
             | :? float as x when Double.IsFinite x -> w.WriteNumberValue x
             | :? float32 as x when Single.IsFinite x -> w.WriteNumberValue(float x)
             | :? float
             | :? float32 -> invalidArg "values" $"SQLite stores a list as JSON, which has no NaN or infinity: {v} cannot be one of its elements"
             | x ->
                 match text x with
                 | Some t -> w.WriteStringValue t
                 | None -> invalidArg "values" $"a {x.GetType().Name} has no form in a SQLite list"

         w.WriteEndArray())

        box (Encoding.UTF8.GetString(stream.ToArray()))

    /// A list column's JSON text as an array of `element`s, each read as its
    /// scalar column is. What is not a JSON array of such values (a file
    /// written by another tool) raises InvalidCastException naming the column
    /// and the element.
    let parse (arrayType: Type) (column: string) (stored: string) : Array =
        let element = arrayType.GetElementType()

        let refuse (what: string) =
            raise (InvalidCastException $"column {column} holds {what}, not a list of {element.Name}")

        let doc =
            try
                JsonDocument.Parse(stored)
            with :? JsonException ->
                refuse "text that is not JSON"

        use doc = doc

        if doc.RootElement.ValueKind <> JsonValueKind.Array then
            refuse $"a JSON {doc.RootElement.ValueKind}"

        let items = doc.RootElement.EnumerateArray() |> Array.ofSeq
        let result = Array.CreateInstanceFromArrayType(arrayType, items.Length)

        items
        |> Array.iteri (fun i (e: JsonElement) ->
            let wrong () = refuse $"element {i}, a JSON {e.ValueKind} ({e.GetRawText()})"

            let value: obj =
                try
                    match e.ValueKind with
                    | JsonValueKind.Null -> null
                    | JsonValueKind.Number when element = typeof<int64> -> box (e.GetInt64())
                    | JsonValueKind.Number when element = typeof<int> -> box (e.GetInt32())
                    | JsonValueKind.Number when element = typeof<int16> -> box (e.GetInt16())
                    | JsonValueKind.Number when element = typeof<byte> -> box (e.GetByte())
                    | JsonValueKind.Number when element = typeof<bool> ->
                        match e.GetInt64() with
                        | 0L -> box false
                        | 1L -> box true
                        | _ -> wrong ()
                    | JsonValueKind.True when element = typeof<bool> -> box true
                    | JsonValueKind.False when element = typeof<bool> -> box false
                    | JsonValueKind.Number when element = typeof<float> -> box (e.GetDouble())
                    | JsonValueKind.Number when element = typeof<float32> -> box (float32 (e.GetDouble()))
                    | JsonValueKind.Number when element = typeof<decimal> -> box (Decimals.parse (e.GetRawText()))
                    | JsonValueKind.String ->
                        let t = e.GetString()

                        if element = typeof<string> then box t
                        elif element = typeof<decimal> then box (Decimals.parse t)
                        elif element = typeof<Guid> then box (Guid.Parse t)
                        elif element = typeof<DateTime> then box (DateTime.Parse(t, inv))
                        elif element = typeof<DateTimeOffset> then box (DateTimeOffset.Parse(t, inv))
                        elif element = typeof<DateOnly> then box (DateOnly.Parse(t, inv))
                        elif element = typeof<TimeOnly> then box (TimeOnly.Parse(t, inv))
                        elif element = typeof<TimeSpan> then box (TimeSpan.ParseExact(t, "c", inv))
                        elif element = typeof<char> && t.Length = 1 then box t.[0]
                        else wrong ()
                    | _ -> wrong ()
                with
                | :? InvalidCastException -> reraise ()
                | :? FormatException
                | :? OverflowException -> wrong ()

            match value with
            | null when element.IsValueType -> refuse $"a null at element {i}"
            | v -> result.SetValue(v, i))

        result

    let rows (lists: (string * string option) list) (alias: (string * string list) option) (q: string -> string) =
        let names =
            match alias with
            | Some(_, columns) -> columns
            | None -> lists |> List.mapi (fun i _ -> $"k{i + 1}")

        let select =
            lists
            |> List.mapi (fun i _ -> $"l{i}.value AS {q names.[i]}")
            |> String.concat ", "

        let from =
            lists
            |> List.mapi (fun i (list, _) ->
                if i = 0 then $"json_each({list}) AS l0" else $"JOIN json_each({list}) AS l{i} ON l{i}.key = l0.key")
            |> String.concat " "

        let table = $"(SELECT {select} FROM {from})"

        match alias with
        | Some(name, _) -> $"{table} AS {q name}"
        | None -> table

/// SQLite's DDL (ddl.feature).
