namespace Plasm.Specs

open Reqnroll
open Plasm

/// A domain of its own for owned-records.feature: an account that owns an
/// optional profile and required settings, each its own row.
module OwnedAccounts =

    type AccountId = AccountId of int64
    type ProfileId = ProfileId of int64
    type SettingsId = SettingsId of int64

    type Profile =
        { Id: ProfileId
          Account: AccountId
          Bio: string }

    type Settings =
        { Id: SettingsId
          Account: AccountId
          Theme: string }

    type Account =
        { Id: AccountId
          Name: string
          Profile: Profile option
          Settings: Settings }

    let profiles = schema<Profile> "owned_profiles" { identityKey (fun p -> p.Id) }
    let settings = schema<Settings> "owned_settings" { identityKey (fun s -> s.Id) }

    let accounts =
        schema<Account> "owned_accounts" {
            identityKey (fun a -> a.Id)
            ownsOptional (fun a -> a.Profile) (fun p -> p.Account)
            ownsOne (fun a -> a.Settings) (fun s -> s.Account)
        }

    let model = Model.buildOrFail [] [ accounts; profiles; settings ]

    let tables =
        "drop table if exists owned_profiles; drop table if exists owned_settings; drop table if exists owned_accounts;
         create table owned_accounts (id bigint generated always as identity primary key, name text not null);
         create table owned_profiles (
           id bigint generated always as identity primary key,
           account bigint not null unique references owned_accounts(id),
           bio text not null);
         create table owned_settings (
           id bigint generated always as identity primary key,
           account bigint not null unique references owned_accounts(id),
           theme text not null);"

    let profile bio =
        { Id = ProfileId 0L
          Account = AccountId 0L
          Bio = bio }

    let theme t =
        { Id = SettingsId 0L
          Account = AccountId 0L
          Theme = t }

    let account name p t =
        { Id = AccountId 0L
          Name = name
          Profile = p
          Settings = theme t }

open OwnedAccounts

/// Feature: An owned record is a plain field, loaded and written with its owner
[<Binding>]
type OwnedOneSteps(w: World) =

    let repo () = w.Get<Repo> "accountRepo"

    let insert (a: Account) =
        task {
            let! r = repo().Insert(Changeset.insert a)

            match r with
            | Error(WriteError.Invalid cs) -> return failwithf "the insert was refused: %O" cs
            | _ -> return ok r
        }

    let readAll () =
        task {
            let! r = repo().All(plasm { for a in accounts do orderBy a.Name })
            return r
        }

    let readOne () =
        task {
            let! all = readAll ()
            return List.exactlyOne (ok all)
        }

    let update (f: Account -> Account) =
        task {
            let! read = readOne ()
            w.Set("profileKey", read.Profile |> Option.map (fun p -> p.Id))
            let! r = repo().Update(read |> Changeset.edit f)
            ok r |> ignore
        }
        |> unit

    let stored (table: string) (column: string) =
        task {
            let (AccountId raw) = (w.Get<Account> "account").Id
            return! w.Db.Scalar<string>($"select coalesce(string_agg({column}, ', '), '') from {table} where account = {raw}")
        }

    let count (table: string) = w.Db.Scalar<int64>($"select count(*) from {table}")

    [<StepDefinition("accounts, profiles and settings tables")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec OwnedAccounts.tables
            w.Set("accountRepo", w.Db.RepoWith(model = OwnedAccounts.model))
        }
        |> unit

    [<StepDefinition("^an Account \"(\\w+)\" with the bio \"(\\w+)\" and the theme \"(\\w+)\"$")>]
    member _.WithProfile(name: string, bio: string, t: string) =
        task {
            let! a = insert (account name (Some(profile bio)) t)
            w.Set("account", a)
        }
        |> unit

    [<StepDefinition("^an Account \"(\\w+)\" without a profile and with the theme \"(\\w+)\"$")>]
    member _.WithoutProfile(name: string, t: string) =
        task {
            let! a = insert (account name None t)
            w.Set("account", a)
        }
        |> unit

    [<StepDefinition("^Accounts \"(\\w+)\", \"(\\w+)\" and \"(\\w+)\", each with a profile$")>]
    member _.Three(a: string, b: string, c: string) =
        task {
            for n in [ a; b; c ] do
                let! _ = insert (account n (Some(profile "bio")) "dark")
                ()
        }
        |> unit

    [<StepDefinition("its settings row is deleted behind Plasm's back")>]
    member _.DeleteSettings() = w.Db.Exec "delete from owned_settings" |> unit

    [<StepDefinition("I read the Accounts")>]
    member _.Read() =
        task {
            w.MarkCommands()
            let! all = readAll ()
            w.Set("accounts", all)
        }
        |> unit

    member private _.Named(name: string) =
        ok (w.Get<Result<Account list, DbError>> "accounts") |> List.find (fun a -> a.Name = name)

    [<StepDefinition("^\"(\\w+)\" reads with the bio \"(\\w+)\" and the theme \"(\\w+)\"$")>]
    member this.ReadsWith(name: string, bio: string, t: string) =
        let a = this.Named name
        equal (Some bio) (a.Profile |> Option.map (fun p -> p.Bio)) "bio"
        equal t a.Settings.Theme "theme"

    [<StepDefinition("^\"(\\w+)\" reads without a profile$")>]
    member this.ReadsWithout(name: string) = equal None (this.Named name).Profile "profile"

    [<StepDefinition("three commands are sent")>]
    member _.ThreeCommands() = equal 3 w.CommandsSinceMark.Length "commands"

    [<StepDefinition("^the read is refused, naming the field (\\w+)$")>]
    member _.Refused(field: string) =
        match w.Get<Result<Account list, DbError>> "accounts" with
        | Error e -> check ((string e).Contains field) (string e)
        | Ok r -> failwithf "the read succeeded: %A" r

    [<StepDefinition("^I keep the Accounts whose profile's bio is \"(\\w+)\", through exists$")>]
    member _.KeepByBio(bio: string) =
        task {
            let! r =
                repo().All(
                    plasm {
                        for a in accounts do
                            where (exists (plasm { for p in profiles do where (p.Account = a.Id && p.Bio = bio) }))
                    }
                )

            w.Set("kept", ok r |> List.map (fun a -> a.Name))
        }
        |> unit

    [<StepDefinition("^only \"(\\w+)\" is read$")>]
    member _.OnlyRead(name: string) = equal [ name ] (w.Get<string list> "kept") "accounts"

    [<StepDefinition("a second profile row for it, behind Plasm's back")>]
    member _.SecondProfile() =
        task {
            let (AccountId raw) = (w.Get<Account> "account").Id
            let! _ = w.Db.Exec "alter table owned_profiles drop constraint owned_profiles_account_key"
            let! _ = w.Db.Exec $"insert into owned_profiles(account, bio) values ({raw}, 'again')"
            ()
        }
        |> unit

    [<StepDefinition("^I insert an Account \"(\\w+)\" with the bio \"(\\w+)\" and the theme \"(\\w+)\"$")>]
    member _.InsertWith(name: string, bio: string, t: string) =
        task {
            let! a = insert (account name (Some(profile bio)) t)
            w.Set("account", a)
        }
        |> unit

    [<StepDefinition("^the profiles table holds \"(\\w+)\" and the settings table holds \"(\\w+)\" for that Account$")>]
    member _.Holds(bio: string, t: string) =
        task {
            let! b = stored "owned_profiles" "bio"
            let! s = stored "owned_settings" "theme"
            equal bio b "stored bio"
            equal t s "stored theme"
        }
        |> unit

    [<StepDefinition("^the profiles table holds \"(\\w+)\" for that Account$")>]
    member _.HoldsBio(bio: string) =
        task {
            let! b = stored "owned_profiles" "bio"
            equal bio b "stored bio"
        }
        |> unit

    [<StepDefinition("the Account returned holds its records with their keys")>]
    member _.ReturnedKeys() =
        let a = w.Get<Account> "account"

        match a.Profile with
        | Some p -> check (p.Id <> ProfileId 0L && p.Account = a.Id && a.Settings.Id <> SettingsId 0L && a.Settings.Account = a.Id) $"%A{a}"
        | None -> failwithf "no profile returned: %A" a

    [<StepDefinition("^I read it, then change the bio to \"(\\w+)\" and the theme to \"(\\w+)\"$")>]
    member _.Change(bio: string, t: string) =
        update (fun a ->
            { a with
                Profile = a.Profile |> Option.map (fun p -> { p with Bio = bio })
                Settings = { a.Settings with Theme = t } })

    [<StepDefinition("the profile kept its key")>]
    member _.KeptKey() =
        task {
            let! read = readOne ()
            equal (w.Get<ProfileId option> "profileKey") (read.Profile |> Option.map (fun p -> p.Id)) "profile key"
        }
        |> unit

    [<StepDefinition("I read it, then remove its profile")>]
    member _.Remove() = update (fun a -> { a with Profile = None })

    [<StepDefinition("^I read it, then give it the bio \"(\\w+)\"$")>]
    member _.Give(bio: string) = update (fun a -> { a with Profile = Some(profile bio) })

    [<StepDefinition("the profiles table is empty")>]
    member _.ProfilesEmpty() =
        task {
            let! n = count "owned_profiles"
            equal 0L n "profiles"
        }
        |> unit

    [<StepDefinition("I delete the Account I read")>]
    member _.Delete() =
        task {
            let! read = readOne ()
            let! r = repo().Delete(Changeset.delete read)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the profiles table and the settings table are empty")>]
    member _.BothEmpty() =
        task {
            let! p = count "owned_profiles"
            let! s = count "owned_settings"
            equal (0L, 0L) (p, s) "profiles and settings"
        }
        |> unit

    [<StepDefinition("I build a model of Accounts without ownsOne")>]
    member _.WithoutOwnsOne() =
        let bare = schema<Account> "owned_accounts" { identityKey (fun a -> a.Id); ownsOptional (fun a -> a.Profile) (fun p -> p.Account) }

        match Model.build [] [ bare; profiles; settings ] with
        | Ok _ -> failwith "the model was built"
        | Error errors -> w.Set("accountRefusal", errors)

    [<StepDefinition("^the model is refused for (\\w+), saying to declare ownsOne$")>]
    member _.RefusedOwnsOne(field: string) =
        let errors = w.Get<ConfigError list> "accountRefusal"
        check (errors |> List.exists (fun e -> e.Property = Some field && e.Message.Contains "ownsOne")) $"%A{errors}"
