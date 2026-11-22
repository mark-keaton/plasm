/// Decimals stored as exact text, compared, ordered and added as PostgreSQL's
/// numeric is: a collation, a comparison function and two aggregates the
/// provider registers on each connection it opens.
module internal Plasm.Sqlite.Decimals

open System
open System.Globalization
open Microsoft.Data.Sqlite

let private invariant = CultureInfo.InvariantCulture

let parse (text: string) = Decimal.Parse(text, NumberStyles.Float, invariant)

/// A decimal as the text it is stored as: invariant, its scale kept (12.50
/// stays 12.50, as PostgreSQL's numeric keeps it), and a negative zero as
/// zero, as PostgreSQL's numeric has none.
let text (d: decimal) =
    let d = if d = 0m && Decimal.IsNegative d then Decimal.Negate d else d
    d.ToString(invariant)

/// The collation decimal columns are declared with: by value, so 9.5 sorts
/// before 10.10 and 2.000 equals 2.
let collation = "PLASM_DECIMAL"

/// PostgreSQL's numeric division scale (select_div_scale): the quotient has
/// at least 16 significant digits, and no fewer decimals than either operand.
/// Weights and first digits are of base-10000 digits, as numeric stores them.
let divisionScale (dividend: decimal) (divisor: decimal) =
    let weightAndFirst (v: decimal) =
        let a = abs v

        if a = 0m then
            0, 0
        else
            let mutable w = 0
            let mutable f = a

            if a >= 1m then
                while f >= 10000m do
                    f <- f / 10000m
                    w <- w + 1
            else
                while f < 1m do
                    f <- f * 10000m
                    w <- w - 1

            w, int (Math.Floor f)

    let w1, f1 = weightAndFirst dividend
    let w2, f2 = weightAndFirst divisor
    let qweight = if f1 < f2 then w1 - w2 - 1 else w1 - w2
    let scale = 16 - qweight * 4
    scale |> max (int dividend.Scale) |> max (int divisor.Scale) |> max 0 |> min 1000

/// `dividend / divisor` at PostgreSQL's scale, rounded half away from zero as
/// numeric rounds, and written with exactly that many decimals (at most the
/// 28 a decimal holds).
let divide (dividend: decimal) (divisor: decimal) =
    // In PostgreSQL's words, as numeric's division fails (division-by-zero.feature).
    if divisor = 0m then
        raise (DivideByZeroException "division by zero")

    let scale = min 28 (divisionScale dividend divisor)
    let q = Math.Round(dividend / divisor, scale, MidpointRounding.AwayFromZero)
    q.ToString("F" + string scale, invariant)

/// `op` of two decimals as text, NULL when either is, as numeric's is.
let private binary (op: decimal -> decimal -> string) =
    fun (a: string) (b: string) -> if isNull a || isNull b then null else op (parse a) (parse b)

/// Registers the collation, `plasm_decimal_compare(a, b)` (-1, 0 or 1, NULL
/// when either is), `plasm_decimal_same(a, b)` (equal by value, NULLs
/// equal), `plasm_decimal_add`, `_subtract`, `_multiply`, `_divide` and
/// `_remainder` (NULL when either operand is); `plasm_decimal_sum(x)` and
/// `plasm_decimal_avg(x)` are Windows.register's.
let register (c: SqliteConnection) =
    c.CreateCollation(collation, fun (a: string) (b: string) -> compare (parse a) (parse b))

    c.CreateFunction(
        "plasm_decimal_compare",
        fun (a: string) (b: string) -> if isNull a || isNull b then Nullable() else Nullable(compare (parse a) (parse b))
    )

    // Whether two decimals are equal by value, NULLs equal: never NULL.
    c.CreateFunction(
        "plasm_decimal_same",
        fun (a: string) (b: string) ->
            if isNull a || isNull b then (isNull a && isNull b) else parse a = parse b
    )

    c.CreateFunction("plasm_decimal_add", binary (fun a b -> text (a + b)))
    c.CreateFunction("plasm_decimal_subtract", binary (fun a b -> text (a - b)))
    c.CreateFunction("plasm_decimal_multiply", binary (fun a b -> text (a * b)))
    c.CreateFunction("plasm_decimal_divide", binary divide)
    c.CreateFunction("plasm_decimal_remainder", binary (fun a b -> text (if b = 0m then raise (DivideByZeroException "division by zero") else a % b)))

    // Reached only by a zero divisor (SqliteDialect.Division): SQLite
    // evaluates a CASE's branch only when it is taken, a literal divisor's
    // too (division-by-zero.feature). Not deterministic all the same, a
    // precaution: no plan may evaluate it ahead of its CASE.
    c.CreateFunction<obj, obj>("plasm_division_by_zero", (fun (_: obj) -> raise (DivideByZeroException "division by zero")), false)
