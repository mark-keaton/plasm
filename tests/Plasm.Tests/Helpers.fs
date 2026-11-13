module Plasm.Tests.Helpers

open System
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain

let ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "unexpected error: %O" e

let err r =
    match r with
    | Ok v -> failwithf "expected an error, got %A" v
    | Error e -> e

/// A repo whose connection is never opened; for Explain-only tests.
let offlineRepo = Repo(Schemas.model, Postgres.connect "Host=invalid.invalid;Database=none")

let explain (q: Query<'T>) = offlineRepo.Explain q

let sqlOf (q: Query<'T>) = (ok (explain q)).Sql

let contains (needle: string) (hay: string) =
    if not (hay.Contains needle) then
        failwithf "expected SQL to contain\n  %s\nbut was\n  %s" needle hay

let notContains (needle: string) (hay: string) =
    if hay.Contains needle then
        failwithf "expected SQL not to contain\n  %s\nbut was\n  %s" needle hay
