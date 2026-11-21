/// Plasm.Sqlite's interrupts (cancellation.feature): only the caller's
/// cancellation turns SQLite's SQLITE_INTERRUPT into OperationCanceledException.
module Plasm.Tests.SqliteInterrupts

open System
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open Plasm.Sqlite

/// An SQLITE_INTERRUPT, as SQLite gives one.
let private interrupted () =
    use c = new SqliteConnection("Data Source=:memory:")
    c.Open()
    use cmd = c.CreateCommand()
    cmd.CommandText <- "with recursive n(i) as (select 1 union all select i + 1 from n where i < 100000000) select count(*) from n"
    use _ = (new Timer((fun _ -> SQLitePCL.raw.sqlite3_interrupt c.Handle), null, 50, Timeout.Infinite))

    try
        cmd.ExecuteScalar() |> ignore
        failwith "the statement was not interrupted"
    with :? SqliteException as e when e.SqliteErrorCode = 9 ->
        e

[<Fact>]
let ``an interrupt the caller's token caused is its cancellation`` () =
    use cts = new CancellationTokenSource()
    cts.Cancel()

    match Interrupted.translate cts.Token (interrupted ()) with
    | :? OperationCanceledException as c -> Assert.Equal(cts.Token, c.CancellationToken)
    | other -> failwith $"expected OperationCanceledException, got {other}"

[<Fact>]
let ``an interrupt with a live token stays SQLite's error`` () =
    use cts = new CancellationTokenSource()
    let e = interrupted ()
    Assert.Same(e, Interrupted.translate cts.Token e)
    Assert.Same(e, Interrupted.translate CancellationToken.None e)
