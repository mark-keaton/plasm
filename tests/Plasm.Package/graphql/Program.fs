/// One GraphQL request whose where and order Plasm builds from declared
/// fields, answered in process with the SQL Plasm would send (no database).
module GraphQLConsumer.Program

open System.Threading.Tasks
open HotChocolate
open HotChocolate.Execution
open HotChocolate.Resolvers
open HotChocolate.Types
open Microsoft.Extensions.DependencyInjection
open Plasm
open Plasm.HotChocolate
open Plasm.Postgres

[<Table "tickets">]
type Ticket = { Id: int64; Title: string; Points: int }

let fields =
    [ QueryField.equal "title" <@ fun (t: Ticket) -> t.Title @>
      QueryField.ordered "points" <@ fun (t: Ticket) -> t.Points @> ]

let model = Model.buildOrFail [] [ typeof<Ticket> ]
let repo = Repo.Start(model, Postgres.connect "Host=localhost", typeof<Ticket>.Assembly, RepoOptions.Unchecked).Result

type TicketFilter() =
    inherit PlasmFilterInputType<Ticket>("TicketFilterInput", fields)

type TicketSort() =
    inherit PlasmSortInputType<Ticket>("TicketSortInput", fields)

type Query() =
    [<HotChocolate.Data.UseFiltering(typeof<TicketFilter>); HotChocolate.Data.UseSorting(typeof<TicketSort>)>]
    member _.Sql(context: IResolverContext) : string =
        match repo.Explain(PlasmRequest.Apply(context, fields, Query.from tableOf<Ticket>)) with
        | Ok plan -> plan.Sql.Replace("\n", " ")
        | Error e -> "refused: " + string e

[<EntryPoint>]
let main _ =
    repo.CheckFields fields
    let executor =
        (ServiceCollection().AddGraphQL().AddQueryType<Query>().AddPlasmFiltering().AddPlasmSorting().BuildRequestExecutorAsync())
            .Result

    let result = executor.ExecuteAsync("{ sql(where: { points: { gt: 2 } }, order: [{ points: DESC }]) }").Result
    System.Console.WriteLine(result.ToJson(false))
    0
