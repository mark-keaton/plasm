/// The F# side of a C# host: records, a union with fields, a query, and one
/// type C# calls (no database is needed: the model is built and the query
/// translated).
namespace Desk

open System
open Plasm
open Plasm.Postgres

type MemberId = MemberId of int64

type Standing =
    | Good
    | Owing of amount: decimal * since: DateOnly

[<Table "members">]
type Member = { Id: MemberId; Name: string; Standing: Standing }

module Queries =
    let owing = plasm { for m in tableOf<Member> do where (m.Id = MemberId 7L && m.Standing.IsOwing) }

/// What the C# host calls.
[<AbstractClass; Sealed>]
type LendingDesk =
    /// The model built and the query translated, as lines to print.
    static member Describe() : string[] =
        let fields = Reflection.FSharpType.GetUnionCases(typeof<MemberId>).[0].GetFields()
        let model = Model.buildOrFail [] [ typeof<Member> ]
        let repo = Repo.Start(model, Postgres.connect "Host=localhost", typeof<Member>.Assembly, RepoOptions.Unchecked).Result

        let query =
            match repo.Explain Queries.owing with
            | Ok plan -> "query: " + plan.Sql.Replace("\n", " ")
            | Error e -> "query: refused: " + string e

        [| "MemberId's case fields: " + String.Join(", ", fields |> Array.map (fun f -> f.Name))
           "model: built"
           query |]
