/// How Plasm shows a value in a message (Reflection.Shown, docs/aot.md): the
/// same text under the runtime as in a native binary, by kind of value.
module Plasm.Tests.Shown

open System
open Xunit
open Plasm

type CardId = CardId of int
type Stage =
    | Backlog
    | Doing of string * DateTimeOffset
type Card = { Id: CardId; Title: string; Stage: Stage; Points: int option }
type Deep = { Next: Deep option; N: int }

let shown (v: obj) = Reflection.Shown.value v

[<Theory>]
[<InlineData(1uy, "1uy")>]
[<InlineData(1y, "1y")>]
[<InlineData(1s, "1s")>]
[<InlineData(1us, "1us")>]
[<InlineData(1, "1")>]
[<InlineData(1u, "1u")>]
[<InlineData(2L, "2L")>]
[<InlineData(2UL, "2UL")>]
[<InlineData(1.0, "1.0")>]
[<InlineData(2.5, "2.5")>]
[<InlineData(1.0f, "1.0f")>]
[<InlineData('x', "'x'")>]
[<InlineData("s \"q\"", "\"s \\\"q\\\"\"")>]
[<InlineData(true, "true")>]
let ``a primitive is its F# literal`` (v: obj, expected: string) = Assert.Equal(expected, shown v)

[<Fact>]
let ``decimals, dates, times and Guids have one invariant form each`` () =
    Assert.Equal("15M", shown 15m)
    Assert.Equal("1.5M", shown 1.5m)
    Assert.Equal("2026-11-18T09:00:00+00:00", shown (DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero)))
    Assert.Equal("2026-11-18T09:00:00Z", shown (DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc)))
    Assert.Equal("2026-11-18", shown (DateOnly(2026, 11, 18)))
    Assert.Equal("09:30:00", shown (TimeOnly(9, 30)))
    Assert.Equal("01:02:03", shown (TimeSpan(1, 2, 3)))
    Assert.Equal("00000000-0000-0000-0000-000000000001", shown (Guid("00000000-0000-0000-0000-000000000001")))

[<Fact>]
let ``records, unions, options, tuples and lists as F# writes them, on one line`` () =
    let card = { Id = CardId 3; Title = "x"; Stage = Doing("ada", DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero)); Points = Some 3 }
    Assert.Equal("{ Id = CardId 3; Title = \"x\"; Stage = Doing (\"ada\", 2026-11-18T09:00:00+00:00); Points = Some 3 }", shown card)
    Assert.Equal("Backlog", shown Backlog)
    Assert.Equal("None", Reflection.Shown.typed typeof<int option> null)
    Assert.Equal("(1, \"a\")", shown (1, "a"))
    Assert.Equal("[1; 2]", shown [ 1; 2 ])
    Assert.Equal("[|1; 2|]", shown [| 1; 2 |])
    Assert.Equal("Some (CardId 3)", shown (Some(CardId 3)))

[<Fact>]
let ``a value is cut at three levels (an option is one), ten items and 200 characters`` () =
    let deep = { Next = Some { Next = Some { Next = Some { Next = None; N = 4 }; N = 3 }; N = 2 }; N = 1 }
    Assert.Equal("{ Next = Some { Next = ...; N = 2 }; N = 1 }", shown deep)
    Assert.Equal("[1; 2; 3; 4; 5; 6; 7; 8; 9; 10; ...]", shown [ 1..20 ])
    Assert.EndsWith("...", shown (String('a', 300)))
    Assert.Equal(203, (shown (String('a', 300))).Length)

[<Fact>]
let ``a type Shape cannot read shows its own ToString, else its F# type name`` () =
    Assert.Equal("http://example.com/", shown (Uri "http://example.com"))
    Assert.Equal("Object", shown (obj ()))
