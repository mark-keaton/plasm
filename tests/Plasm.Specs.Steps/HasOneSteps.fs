namespace Plasm.Specs

open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// Feature: Has-one relationships
/// (Data: Ada has a Profile, Bob has none.)
[<Binding>]
type HasOneSteps(w: World) =

    let account name =
        task {
            let! id = w.Db.Scalar<int64>("insert into accounts(name) values (@n) returning id", [ "n", box name ])
            return id
        }

    let profile (accountId: int64) bio =
        task {
            let! _ = w.Db.Scalar<int64>("insert into profiles(account_id, bio) values (@a, @b) returning id", [ "a", box accountId; "b", box bio ])
            return ()
        }

    let withProfile = plasm { for a in Schemas.accounts do orderBy a.Id; preload a.Profile }

    [<StepDefinition("mappings for Accounts and their Profiles")>]
    member _.Mappings() =
        for t in [ typeof<Account>; typeof<Profile> ] do
            check (List.contains t w.Db.Model.Types) $"{t.Name} is mapped"

    [<StepDefinition("Accounts with and without a Profile")>]
    member _.Accounts() =
        task {
            let! ada = account "Ada"
            let! _ = account "Bob"
            do! profile ada "Writes compilers"
        }
        |> unit

    [<StepDefinition("an Account with two Profiles")>]
    member _.TwoProfiles() =
        task {
            let! ada = account "Ada"
            do! profile ada "first"
            do! profile ada "second"
            w.Set("ada", ada)
        }
        |> unit

    [<StepDefinition("I query Accounts and preload their Profile")>]
    member _.Preload() =
        task {
            w.MarkCommands()
            let! r = w.Repo.All withProfile
            w.Set("accounts", r)
        }
        |> unit

    [<StepDefinition("each Account with a Profile has Some of it")>]
    member _.HasSome() =
        let accounts = w.Get<Result<Account list, DbError>> "accounts" |> ok
        let ada = accounts |> List.find (fun a -> a.Name = "Ada")

        match ada.Profile with
        | Loaded(Some p) -> equal "Writes compilers" p.Bio "Ada's profile"
        | other -> failwithf "expected Ada's profile, got %A" other

    [<StepDefinition("each Account without a Profile has None")>]
    member _.HasNone() =
        let accounts = w.Get<Result<Account list, DbError>> "accounts" |> ok
        let bob = accounts |> List.find (fun a -> a.Name = "Bob")
        equal (Loaded None) bob.Profile "Bob's profile"

    [<StepDefinition("the Profiles are loaded with one batched command")>]
    member _.OneCommand() =
        equal 1 (commandsOn "profiles" w.CommandsSinceMark).Length "commands on profiles"

    [<StepDefinition("I keep only Accounts whose Profile matches a condition")>]
    member _.Filter() =
        task {
            w.MarkCommands()
            let! r = w.Repo.All(plasm { for a in Schemas.accounts do where (Rel.exists (fun p -> p.Bio.Contains "compiler") a.Profile) })
            w.Set("filtered", ok r |> List.map (fun a -> a.Name))
        }
        |> unit

    [<StepDefinition("only those Accounts are returned")>]
    member _.OnlyThose() = equal [ "Ada" ] (w.Get<string list> "filtered") "accounts"

    [<StepDefinition("the condition runs in the database")>]
    member _.InDatabase() =
        let cmds = w.CommandsSinceMark
        equal 1 cmds.Length "commands"
        check (cmds.Head.Sql.Contains "EXISTS" && cmds.Head.Sql.Contains "\"profiles\"") cmds.Head.Sql

    [<StepDefinition("the read fails with a materialisation error naming the relationship and the Account")>]
    member _.TwoRowsFail() =
        match w.Get<Result<Account list, DbError>> "accounts" with
        | Error(DbError.Materialization m) ->
            equal "Account" m.Model "model"
            equal "Profile" m.Property "relationship"
            let ada = w.Get<int64> "ada"
            check (m.Message.Contains $"key AccountId {ada}L") $"the Account is not identified: {m.Message}"
        | other -> failwithf "expected a materialisation error, got %A" other
