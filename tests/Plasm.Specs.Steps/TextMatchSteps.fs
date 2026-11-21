/// Feature: Text is matched as it is written, on every database
namespace Plasm.Specs

open System.Text.RegularExpressions
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

[<Binding>]
type TextMatchSteps(w: World) =

    /// `"a", "b" and "c"` as ["a"; "b"; "c"]; `nobody` as [].
    let names (text: string) =
        [ for m in Regex.Matches(text, "\"([^\"]*)\"") -> m.Groups.[1].Value ]

    [<StepDefinition("^customers named (.*)$")>]
    member _.Customers(list: string) =
        task {
            for i, name in names list |> List.indexed do
                let! _ = Seed.customer w.Db name $"c{i}@example.com"
                ()
        }
        :> Task

    [<StepDefinition("^I read the customers whose name (starts with|ends with|contains) \"(.*)\"$")>]
    member _.Read(how: string, text: string) =
        task {
            let q =
                match how with
                | "starts with" -> plasm { for c in tableOf<Customer> do where (c.Name.StartsWith text) }
                | "ends with" -> plasm { for c in tableOf<Customer> do where (c.Name.EndsWith text) }
                | _ -> plasm { for c in tableOf<Customer> do where (c.Name.Contains text) }

            let! r = w.Repo.All q
            w.Set("textRead", ok r |> List.map _.Name |> List.sort)
        }
        :> Task

    [<StepDefinition("^I read the customers whose name starts with \"(.*)\", through a prepared query$")>]
    member _.ReadPrepared(text: string) =
        task {
            let starting = Query.prepare (fun (t: Arg<string>) -> plasm { for c in tableOf<Customer> do where (c.Name.StartsWith t.Value) })
            let! r = w.Repo.All(starting text)
            w.Set("textRead", ok r |> List.map _.Name |> List.sort)
        }
        :> Task

    [<StepDefinition("^the customers read are named (.*)$")>]
    member _.Named(list: string) =
        equal (names list |> List.sort) (w.Get<string list> "textRead") "the customers read"
