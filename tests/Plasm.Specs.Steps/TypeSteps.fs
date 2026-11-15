namespace Plasm.Specs

open System
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A record with a field type that has no persistence mapping.
type Address = { Street: string; City: string }

[<Table "shipments">]
type Shipment =
    { Id: int64
      Destination: Address
      Weight: uint64 }

/// Rule: The public API preserves useful F# type information
[<Binding>]
type TypeSteps(w: World) =

    let compiles (code: string) =
        task {
            let! r = CompileCheck.check code
            check r.Compiles ("expected to compile:\n" + code + "\n" + String.concat "\n" r.Errors)
        }

    let rejected (code: string) (errorCode: string) =
        task {
            let! r = CompileCheck.check code

            check
                (r.Errors |> List.exists (fun e -> e.StartsWith errorCode))
                ("expected " + errorCode + " for:\n" + code + "\ngot: " + String.concat "\n" r.Errors)
        }

    // Scenario: Querying a mapped property uses its declared type

    [<StepDefinition("an Order has a decimal Total")>]
    member _.DecimalTotal() =
        equal typeof<decimal> (typeof<Order>.GetProperty("Total").PropertyType) "Total type"

    [<StepDefinition("I attempt to compare Total with an incompatible value")>]
    member _.CompareIncompatible() =
        w.Set("snippets", [ "let q = plasm { for o in stored<Order> do where (o.Total > \"100\") }", "FS0001"
                            "let q = plasm { for o in stored<Order> do where (o.Total = Some 100m) }", "FS0001" ])
        w.Set("control", "let q = plasm { for o in stored<Order> do where (o.Total > 100m) }")

    [<StepDefinition("the expression is rejected at compile time")>]
    member _.RejectedAtCompileTime() =
        task {
            do! compiles (w.Get "control")

            for code, error in w.Get<(string * string) list> "snippets" do
                do! rejected code error
        }
        |> unit

    // Scenario: Relationship loads are checked against the source type

    [<StepDefinition("I attempt to order the lines an Order owns by a field they do not have")>]
    member _.OwnUndefined() =
        // What an Order owns is its field's type; the one name an owned list
        // takes is the field it is ordered by, written with nameof.
        let declare field =
            $"[<Table \"orders\">]\ntype Ordered = {{ Id: OrderId; [<OrderedBy(nameof the<OrderLine>.{field})>] Lines: OrderLine list }}"

        w.Set("snippets", [ declare "Price", "FS0039" ])
        w.Set("control", declare "UnitPrice")

    [<StepDefinition("the declaration is rejected at compile time")>]
    member _.OwnRejected() =
        task {
            do! compiles (w.Get "control")

            for code, error in w.Get<(string * string) list> "snippets" do
                do! rejected code error
        }
        |> unit

    // Scenario: Nullable values remain explicit

    [<StepDefinition("an Order has an optional CancellationReason")>]
    member _.OptionalReason() =
        task {
            equal typeof<string option> (typeof<Order>.GetProperty("CancellationReason").PropertyType) "type"
            let! a = Seed.order w.Db None "cancelled" 1m (Seed.at 1)
            let! b = Seed.order w.Db None "cancelled" 2m (Seed.at 2)
            let! _ = w.Db.Exec("update orders set cancellation_reason = 'customer request' where id = @id", [ "id", box (let (OrderId v) = b in v) ])
            w.Set("without", a)
            w.Set("with", b)
        }
        |> unit

    [<StepDefinition("I read an Order without a CancellationReason")>]
    member _.ReadWithout() =
        task {
            let! all = w.Repo.All(plasm { for o in stored<Order> do orderBy o.Id })
            w.Set("orders", ok all)
        }
        |> unit

    [<StepDefinition("the value is represented as None")>]
    member _.IsNone() =
        let orders: Order list = w.Get "orders"
        let o = orders |> List.find (fun o -> o.Id = w.Get "without")
        equal None o.CancellationReason "reason"

    [<StepDefinition("a present value is represented as Some")>]
    member _.IsSome() =
        let orders: Order list = w.Get "orders"
        let o = orders |> List.find (fun o -> o.Id = w.Get "with")
        equal (Some "customer request") o.CancellationReason "reason"

    // Scenario: Unsupported model representations fail during configuration

    [<StepDefinition("a model contains a type without a configured persistence mapping")>]
    member _.UnmappedType() =
        w.Set("shipments", typeof<Shipment>)

    [<StepDefinition("I validate the persistence configuration")>]
    member _.Validate() =
        let shipments: System.Type = w.Get "shipments"
        w.Set("config", Model.build Schemas.scalars (Schemas.roots @ [ shipments ]))

    [<StepDefinition("configuration fails with the model and property identified")>]
    member _.ConfigFails() =
        let errors = w.Get<Result<Model, ConfigError list>> "config" |> err
        let destination = errors |> List.tryFind (fun e -> e.Property = Some "Destination")
        check destination.IsSome $"expected an error for Shipment.Destination, got %A{errors}"
        equal "Shipment" destination.Value.Model "model"
        check (destination.Value.Message.Contains "Address") "message names the type"

    [<StepDefinition("no implicit lossy conversion is selected")>]
    member _.NoLossyConversion() =
        let errors = w.Get<Result<Model, ConfigError list>> "config" |> err
        // uint64 could be squeezed into bigint; it is rejected, not converted.
        check (errors |> List.exists (fun e -> e.Property = Some "Weight")) "uint64 field is rejected"
        equal 2 errors.Length $"errors: %A{errors}"
