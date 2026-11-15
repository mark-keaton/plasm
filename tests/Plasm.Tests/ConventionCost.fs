/// Reading through a scalar convention costs what reading through a registered
/// mapping costs: conventions are the one style, so they must not be the slow
/// path. Allocation is measured, not time, so the test is stable.
module Plasm.Tests.ConventionCost

open System
open Xunit
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Domain.Schemas

/// The same model with the mappings written out, as before conventions.
let registered =
    Model.buildOrFail
        [ Scalar.wrap CustomerId (fun (CustomerId v) -> v)
          Scalar.wrap OrderId (fun (OrderId v) -> v)
          Scalar.wrap ProductId (fun (ProductId v) -> v)
          Scalar.wrap OrderLineId (fun (OrderLineId v) -> v)
          Scalar.wrap PaymentId (fun (PaymentId v) -> v)
          Scalar.wrap ArticleId (fun (ArticleId v) -> v)
          Scalar.wrap TagId (fun (TagId v) -> v)
          Scalar.wrap AccountId (fun (AccountId v) -> v)
          Scalar.wrap ProfileId (fun (ProfileId v) -> v)
          Scalar.tryWrap Quantity.create (fun (q: Quantity) -> q.Value)
          Scalar.enum [ Draft, "draft"; Published, "published"; Cancelled, "cancelled" ]
          Scalar.enum [ Pending, "pending"; Succeeded, "succeeded"; Failed, "failed" ] ]
        Schemas.roots

let rows = 20_000

/// Bytes allocated reading `rows` values of `t` from `raw`, and the values read.
let read (model: Model) (t: Type) (raw: obj) =
    let values = Array.zeroCreate<obj> rows
    // Warm up: build whatever is built once.
    model.ScalarFromDb(t, raw) |> ignore
    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 0 .. rows - 1 do
        match model.ScalarFromDb(t, raw) with
        | Ok v -> values.[i] <- v
        | Error e -> failwith e

    GC.GetAllocatedBytesForCurrentThread() - before, values

[<Theory>]
[<InlineData("OrderId")>]
[<InlineData("Quantity")>]
[<InlineData("OrderStatus")>]
let ``a convention reads a value with no more allocation than a registered mapping`` (name: string) =
    let t, raw =
        match name with
        | "OrderId" -> typeof<OrderId>, box 42L
        | "Quantity" -> typeof<Quantity>, box 3
        | _ -> typeof<OrderStatus>, box "published"

    let byConvention, a = read Schemas.model t raw
    let byRegistration, b = read registered t raw
    Assert.Equal<obj[]>(b, a)
    // Within a fifth, and a small constant for the measurement itself.
    Assert.True(
        float byConvention <= 1.2 * float byRegistration + 4096.0,
        $"{name}: by convention {byConvention} bytes, registered {byRegistration} bytes, for {rows} values"
    )
