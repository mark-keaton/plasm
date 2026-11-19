/// The koans database: the schema, the starting data every koan sees, and
/// where the database comes from (PLASM_KOANS_PG, or a throwaway container).
module Plasm.Koans.Database

open System
open System.IO
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql

let schema = KoansData.schema

/// The starting data. Every koan runs in a sandbox, so every koan sees exactly this.
let data = KoansData.data


/// A database for one run of the koans; disposing it removes it.
type KoansDatabase =
    { ConnectionString: string
      Description: string
      Dispose: unit -> Task }

let private exec (cs: string) (sql: string) =
    task {
        use conn = new NpgsqlConnection(cs)
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        let! _ = cmd.ExecuteNonQueryAsync()
        return ()
    }

/// Creates the koans database: on the server named by PLASM_KOANS_PG (an
/// admin connection string), or in a throwaway PostgreSQL container.
let provision () : Task<KoansDatabase> =
    task {
        let name = $"plasm_koans_{Guid.NewGuid():N}"

        let! admin, description, removeServer =
            match Environment.GetEnvironmentVariable "PLASM_KOANS_PG" with
            | null
            | "" ->
                task {
                    // Quiet: the learner's terminal is for the koans,
                    // so Testcontainers' console log is set aside while it starts.
                    let console = Console.Out
                    Console.SetOut TextWriter.Null

                    let container =
                        try
                            let c = PostgreSqlBuilder("postgres:18.4").WithLabel("plasm-koans", "true").Build()
                            c.StartAsync().GetAwaiter().GetResult()
                            c
                        finally
                            Console.SetOut console

                    return
                        container.GetConnectionString(),
                        "a throwaway PostgreSQL container (removed when the run ends)",
                        (fun () -> container.DisposeAsync().AsTask())
                }
            | cs -> Task.FromResult(cs, "the server named by PLASM_KOANS_PG", (fun () -> Task.CompletedTask))

        do! exec admin $"create database {name}"
        let b = NpgsqlConnectionStringBuilder(admin)
        b.Database <- name
        b.Pooling <- false
        let cs = b.ConnectionString
        do! exec cs schema
        do! exec cs data

        return
            { ConnectionString = cs
              Description = description
              Dispose =
                fun () ->
                    task {
                        try
                            do! exec admin $"drop database if exists {name} with (force)"
                        with _ ->
                            ()

                        do! removeServer ()
                    } }
    }
