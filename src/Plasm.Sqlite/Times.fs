/// Times as SQLite holds them (utc-only.feature): an instant in UTC, kept to
/// the microsecond as PostgreSQL keeps it, as TEXT in one form, which orders
/// and compares as the instants do.
namespace Plasm.Sqlite

open System
open System.Globalization

module internal Times =
    let private inv = CultureInfo.InvariantCulture
    let private epoch = DateTime(2000, 1, 1).Ticks

    /// A DateTime's form: what Microsoft.Data.Sqlite binds (measured on
    /// 11.0.0), the fraction without trailing zeros. A DateTimeOffset's is the
    /// same with "+00:00" after it.
    let form = "yyyy-MM-dd HH:mm:ss.FFFFFF"

    /// The ticks PostgreSQL keeps, as Npgsql sends them: the ticks since
    /// 2000-01-01 divided by ten, towards zero (so before 2000, upwards).
    let keptTicks (ticks: int64) = epoch + (ticks - epoch) / 10L * 10L

    let kept (v: obj) : obj =
        match v with
        | :? DateTime as d -> box (DateTime(keptTicks d.Ticks, d.Kind))
        | :? DateTimeOffset as d -> box (DateTimeOffset(keptTicks d.Ticks, d.Offset))
        | v -> v

    let text (d: DateTime) = DateTime(keptTicks d.Ticks).ToString(form, inv)

    let private refuse (column: string) (stored: string) (what: string) =
        raise (InvalidCastException $"column {column} holds '{stored}', not a time in Plasm's form ({what})")

    let private utc (column: string) (stored: string) (body: string) (what: string) =
        match DateTime.TryParseExact(body, form, inv, DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal) with
        | true, d when d.ToString(form, inv) = body -> d
        | _ -> refuse column stored what

    /// A DateTime column's text, of Kind Utc; anything else raises
    /// InvalidCastException naming the column.
    let dateTime (column: string) (stored: string) : DateTime =
        utc column stored stored "yyyy-MM-dd HH:mm:ss and up to six digits of a second, in UTC"

    /// A DateTimeOffset column's text, at the offset zero.
    let dateTimeOffset (column: string) (stored: string) : DateTimeOffset =
        let what = "yyyy-MM-dd HH:mm:ss and up to six digits of a second, then +00:00"

        if stored.EndsWith "+00:00" then
            DateTimeOffset(utc column stored (stored.Substring(0, stored.Length - 6)) what)
        else
            refuse column stored what

    /// The check holding a column to the form (Ddl): the date and time as
    /// digits, a real one, and a fraction of one to six digits, not ending in
    /// zero; a DateTimeOffset's with "+00:00" after it.
    let check (column: string) (offset: bool) =
        let body = if offset then $"substr({column}, 1, length({column}) - 6)" else column
        let digits = "[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] [0-9][0-9]:[0-9][0-9]:[0-9][0-9]"

        let form =
            $"(({body} GLOB '{digits}') OR ({body} GLOB '{digits}.[0-9]*' AND length({body}) <= 26 AND substr({body}, 21) NOT GLOB '*[^0-9]*' AND {body} NOT GLOB '*0'))"
            + $" AND datetime(substr({body}, 1, 19)) = substr({body}, 1, 19)"

        if offset then $"check ({column} GLOB '*+00:00' AND {form})" else $"check ({form})"
