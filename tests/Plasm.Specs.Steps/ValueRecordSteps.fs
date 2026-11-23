/// A domain of its own for value-records.feature: a customer with an address
/// stored in its own row, and records the model must refuse.
namespace Plasm.Specs.ValueRecords

open Plasm

type CustomerId = CustomerId of int64

type Geo = { Lat: decimal; Lon: decimal }

type Address =
    { Street: string
      City: string
      Note: string option
      Geo: Geo }

[<Table "value_customers">]
type Customer =
    { Id: CustomerId
      Name: string
      Address: Address
      Billing: Address option }

module Schemas =
    let model = Model.buildOrFail [] [ typeof<Customer> ]

    let tables =
        "drop table if exists value_customers;
         create table value_customers (
           id bigint generated always as identity primary key,
           name text not null,
           address_street text not null,
           address_city text not null,
           address_note text,
           address_geo_lat numeric(9,4) not null,
           address_geo_lon numeric(9,4) not null,
           billing_street text,
           billing_city text,
           billing_note text,
           billing_geo_lat numeric(9,4),
           billing_geo_lon numeric(9,4));"

/// Records whose storage is unclear, each refused when the model is built.
module Unclear =
    type StoredAddressId = StoredAddressId of int64

    [<Table "stored_addresses">]
    type StoredAddress = { Id: StoredAddressId; Street: string }

    type ShipperId = ShipperId of int64

    [<Table "shippers">]
    type Shipper = { Id: ShipperId; Shipping: StoredAddress }

    type TablessId = TablessId of int64
    type Tabless = { Id: TablessId; Street: string }
    type TablessOwnerId = TablessOwnerId of int64

    [<Table "tabless_owners">]
    type TablessOwner = { Id: TablessOwnerId; Place: Tabless }

    type Vague = { A: string option; B: int option }
    type VagueOwnerId = VagueOwnerId of int64

    [<Table "vague_owners">]
    type VagueOwner = { Id: VagueOwnerId; Extra: Vague option }

    type Holder = { Inner: StoredAddress }
    type HolderOwnerId = HolderOwnerId of int64

    [<Table "holder_owners">]
    type HolderOwner = { Id: HolderOwnerId; Box: Holder }

    type UniqueInside = { [<Unique>] Email: string }
    type UniqueOwnerId = UniqueOwnerId of int64

    [<Table "unique_owners">]
    type UniqueOwner = { Id: UniqueOwnerId; Contact: UniqueInside }

    let models =
        Map
            [ "a field of a stored record that holds no owner's key", [ typeof<Shipper>; typeof<StoredAddress> ]
              "a value with an Id but no table", [ typeof<TablessOwner> ]
              "an optional value whose fields are all optional", [ typeof<VagueOwner> ]
              "a value holding a stored record", [ typeof<HolderOwner>; typeof<StoredAddress> ]
              "a value with a unique field", [ typeof<UniqueOwner> ] ]

namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.Specs.ValueRecords

/// Feature: A record without a table is stored in its owner's row
[<Binding>]
type ValueRecordSteps(w: World) =

    let repo () = w.Get<Repo> "valueRepo"

    let customers () =
        if w.Has "valueCustomers" then w.Get<Map<string, Customer>> "valueCustomers" else Map.empty

    let address street city (lat: decimal) (lon: decimal) =
        { Street = street
          City = city
          Note = None
          Geo = { Lat = lat; Lon = lon } }

    let insert (c: Customer) =
        task {
            let! r = repo().Transact(Tx.insert c: Tx<_, unit>)
            let saved = ok r
            w.Set("valueCustomers", (customers ()).Add(saved.Name, saved))
            w.Set("valueName", saved.Name)
        }

    let readAll (q: Query<Customer>) =
        task {
            w.MarkCommands()
            let! r = repo().All q
            w.Set("valueRead", r)
        }
        |> unit

    let read () =
        ok (w.Get<Result<Customer list, DbError>> "valueRead")

    let column (name: string) (sql: string) =
        w.Db.Scalar<obj>($"select {sql} from value_customers where name = @n", [ "n", box name ])

    [<StepDefinition("customers with an address and an optional billing address, in one table")>]
    member _.Tables() =
        task {
            do! w.Db.MakeTables(Schemas.tables, Schemas.model)
            w.Set("valueRepo", w.Db.RepoWith(model = Schemas.model))
        }
        |> unit

    [<StepDefinition("^(?:I insert )?(\\w+), living at \"(.*)\", \"(.*)\" \\((.*), (.*)\\), with no billing address$")>]
    member _.WithoutBilling(name: string, street: string, city: string, lat: decimal, lon: decimal) =
        task {
            w.MarkCommands()

            do!
                insert
                    { Id = CustomerId 0L
                      Name = name
                      Address = address street city lat lon
                      Billing = None }
        }
        |> unit

    [<StepDefinition("^(\\w+), living at \"(.*)\", \"(.*)\" \\((.*), (.*)\\), billed at \"(.*)\", \"(.*)\" \\((.*), (.*)\\)$")>]
    member _.WithBilling(name, street, city, lat: decimal, lon: decimal, bStreet, bCity, bLat: decimal, bLon: decimal) =
        insert
            { Id = CustomerId 0L
              Name = name
              Address = address street city lat lon
              Billing = Some(address bStreet bCity bLat bLon) }
        |> unit

    [<StepDefinition("one command is sent, inserting into value_customers")>]
    member _.OneInsert() =
        let sql = w.CommandsSinceMark |> List.map (fun e -> e.Sql)
        equal 1 sql.Length $"commands: %A{sql}"
        check (sql.[0].StartsWith "INSERT INTO \"value_customers\"") sql.[0]

    [<StepDefinition("the row's address_street is \"(.*)\", address_city is \"(.*)\" and address_geo_lat is (.*)")>]
    member _.Columns(street: string, city: string, lat: decimal) =
        task {
            let name = w.Get<string> "valueName"
            let! s = column name "address_street"
            let! c = column name "address_city"
            let! l = w.Db.Scalar<decimal>("select address_geo_lat from value_customers where name = @n", [ "n", box name ])
            equal (box street, box city, lat) (s, c, l) "address columns"
        }
        |> unit

    [<StepDefinition("the row's billing columns are all NULL")>]
    member _.BillingNull() =
        task {
            let name = w.Get<string> "valueName"

            let! n =
                w.Db.Scalar<bool>(
                    "select (billing_street is null and billing_city is null and billing_note is null and billing_geo_lat is null and billing_geo_lon is null) from value_customers where name = @n",
                    [ "n", box name ]
                )

            check n "some billing column holds a value"
        }
        |> unit

    [<StepDefinition("I read the Customers by name")>]
    member _.ReadByName() =
        readAll (plasm { for c in tableOf<Customer> do orderBy c.Name })

    [<StepDefinition("Ada and Bob are read as they were inserted")>]
    member _.AsInserted() =
        let c = customers ()
        equal [ c.["Ada"]; c.["Bob"] ] (read ()) "customers"

    [<StepDefinition("Ada's billing_street is set to \"(.*)\" behind Plasm's back, with no billing_city")>]
    member _.Partly(street: string) =
        task {
            // Behind Plasm's back: past the check SQLite's Ddl holds the
            // value's columns with, as another tool might go.
            if w.Db.Kind = Plasm.TestDomain.Database.SQLite then
                do! w.Db.DropConstraint("value_customers", "value_customers_billing_present")

            let! _ = w.Db.Exec("update value_customers set billing_street = @s where name = 'Ada'", [ "s", box street ])
            ()
        }
        |> unit

    [<StepDefinition("the read fails, naming (\\w+)")>]
    member _.ReadFails(field: string) =
        match w.Get<Result<Customer list, DbError>> "valueRead" with
        | Ok rows -> failwithf "the read succeeded: %A" rows
        | Error e -> check ((string e).Contains field) (string e)

    [<StepDefinition("I read the Customers whose address is in \"(.*)\"")>]
    member _.InCity(city: string) =
        readAll (plasm { for c in tableOf<Customer> do where (c.Address.City = city); orderBy c.Name })

    [<StepDefinition("I read the Customers whose address lies north of (.*)")>]
    member _.North(lat: decimal) =
        readAll (plasm { for c in tableOf<Customer> do where (c.Address.Geo.Lat > lat); orderBy c.Name })

    [<StepDefinition("I read the Customers whose address is Ada's")>]
    member _.AdasAddress() =
        let ada = (customers ()).["Ada"].Address
        readAll (plasm { for c in tableOf<Customer> do where (c.Address = ada); orderBy c.Name })

    [<StepDefinition("I read the Customers without a billing address")>]
    member _.NoBilling() =
        readAll (plasm { for c in tableOf<Customer> do where c.Billing.IsNone; orderBy c.Name })

    [<StepDefinition("^the Customers read are (\\w+)$")>]
    member _.ReadAre(name: string) =
        equal [ name ] (read () |> List.map (fun c -> c.Name)) "customers"

    [<StepDefinition("I read the Customers ordered by their billing city")>]
    member _.ByBillingCity() =
        readAll (plasm { for c in tableOf<Customer> do orderBy (c.Billing |> Option.map (fun b -> b.City)) })

    [<StepDefinition("^the Customers are read in the order (.*)$")>]
    member _.InOrder(text: string) =
        let names = text.Replace(" and ", ", ").Split(", ") |> List.ofArray
        equal names (read () |> List.map (fun c -> c.Name)) "customers"

    [<StepDefinition("I select the billing cities of the Customers, ordered by name")>]
    member _.BillingCities() =
        task {
            let! r = repo().All(plasm { for c in tableOf<Customer> do orderBy c.Name; select (c.Billing |> Option.map (fun b -> b.City)) })
            w.Set("valueBillingCities", ok r)
        }
        |> unit

    [<StepDefinition("the billing cities are none, \"(.*)\" and \"(.*)\"")>]
    member _.BillingCitiesAre(a: string, b: string) =
        equal [ None; Some a; Some b ] (w.Get<string option list> "valueBillingCities") "billing cities"

    [<StepDefinition("I read the Customers billed in \"(.*)\"")>]
    member _.BilledIn(city: string) =
        readAll (plasm { for c in tableOf<Customer> do where (c.Billing |> Option.exists (fun b -> b.City = city)); orderBy c.Name })

    [<StepDefinition("I read the Customers billed nowhere but \"(.*)\"")>]
    member _.BilledOnlyIn(city: string) =
        readAll (plasm { for c in tableOf<Customer> do where (c.Billing |> Option.forall (fun b -> b.City = city)); orderBy c.Name })

    [<StepDefinition("I read the Customers with a billing address in \"(.*)\", through Value")>]
    member _.ThroughValue(city: string) =
        readAll (plasm { for c in tableOf<Customer> do where (c.Billing.IsSome && c.Billing.Value.City = city); orderBy c.Name })

    [<StepDefinition("I read the Customers whose name, passed through id, is \"(.*)\"")>]
    member _.ThroughId(name: string) =
        readAll (plasm { for c in tableOf<Customer> do where (id c.Name = name) })

    [<StepDefinition("the read is refused, naming (\\S+) in the reason")>]
    member _.NamedInReason(name: string) =
        match w.Get<Result<Customer list, DbError>> "valueRead" with
        | Error(DbError.Translation(TranslationError.UnsupportedExpression(_, reason))) -> check (reason.Contains name) reason
        | other -> failwithf "expected an unsupported expression, got %A" other

    [<StepDefinition("I select the cities of the Customers' addresses, ordered by city")>]
    member _.Cities() =
        task {
            let! r = repo().All(plasm { for c in tableOf<Customer> do orderBy c.Address.City; select c.Address.City })
            w.Set("valueCities", ok r)
        }
        |> unit

    [<StepDefinition("the cities are \"(.*)\" and \"(.*)\"")>]
    member _.CitiesAre(a: string, b: string) =
        equal [ a; b ] (w.Get<string list> "valueCities") "cities"

    [<StepDefinition("^(\\w+) moves to \"(.*)\", \"(.*)\" \\((.*), (.*)\\)$")>]
    member _.Moves(name: string, street: string, city: string, lat: decimal, lon: decimal) =
        task {
            let before = (customers ()).[name]
            w.MarkCommands()
            let! r = repo().Transact(Tx.update (before, { before with Address = address street city lat lon }): Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("(\\w+)'s billing address is removed")>]
    member _.RemoveBilling(name: string) =
        task {
            let before = (customers ()).[name]
            let! r = repo().Transact(Tx.update (before, { before with Billing = None }): Tx<_, unit>)
            ok r |> ignore
            w.Set("valueName", name)
        }
        |> unit

    [<StepDefinition("the update sets the address columns and no others")>]
    member _.UpdateColumns() =
        let update = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.find (fun s -> s.StartsWith "UPDATE")
        let set = update.Substring(0, update.IndexOf "\nWHERE ")

        for c in [ "address_street"; "address_city"; "address_note"; "address_geo_lat"; "address_geo_lon" ] do
            check (set.Contains $"\"{c}\"") update

        for c in [ "\"name\""; "billing_" ] do
            check (not (set.Contains c)) update

    [<StepDefinition("(\\w+) is read living at \"(.*)\"")>]
    member _.LivingAt(name: string, street: string) =
        task {
            let! r = repo().One(plasm { for c in tableOf<Customer> do where (c.Name = name) })
            equal street (ok r).Value.Address.Street "street"
        }
        |> unit

    [<StepDefinition("(\\w+) is read with no billing address")>]
    member _.NoBillingRead(name: string) =
        task {
            let! r = repo().One(plasm { for c in tableOf<Customer> do where (c.Name = name) })
            equal None (ok r).Value.Billing "billing"
        }
        |> unit

    [<StepDefinition("I print the DDL of the customers model")>]
    member _.Ddl() = w.Set("valueDdl", repo().Ddl())

    [<StepDefinition("it has the columns (.*)")>]
    member _.HasColumns(text: string) =
        let ddl = w.Get<string> "valueDdl"

        // Without case, as SQL reads its types and keywords (SQLite's Ddl
        // writes TEXT and INTEGER).
        for c in text.Replace(" and ", ", ").Split(", ") do
            check (ddl.ToLowerInvariant().Contains(c.ToLowerInvariant())) $"'{c}' is not in:\n{ddl}"

    [<StepDefinition("it has a check that the billing columns are all NULL or all present")>]
    member _.HasCheck() =
        let ddl = w.Get<string> "valueDdl"
        check (ddl.Contains "value_customers_billing_present") ddl

    [<StepDefinition("the address_city column is dropped")>]
    member _.Drop() =
        task {
            let! _ = w.Db.Exec "alter table value_customers drop column address_city"
            ()
        }
        |> unit

    [<StepDefinition("I check the customers model against the database")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("valueIssues", ok r)
        }
        |> unit

    [<StepDefinition("a missing column address_city is reported for Customer.Address")>]
    member _.Missing() =
        let issues = w.Get<SchemaIssue list> "valueIssues"

        check
            (issues
             |> List.exists (fun i -> i.Kind = SchemaIssueKind.MissingColumn && i.Record = "Customer" && i.Field = Some "Address" && i.Column = Some "address_city"))
            $"issues: %A{issues}"

    [<StepDefinition("^I build a model of (a field of a stored record that holds no owner's key|a value with an Id but no table|an optional value whose fields are all optional|a value holding a stored record|a value with a unique field)$")>]
    member _.Build(records: string) =
        w.Set("storedBuild", Model.build [] Unclear.models.[records])
