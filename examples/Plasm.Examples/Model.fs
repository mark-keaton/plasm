/// 1. The domain: ordinary immutable F# records. Persistence metadata lives
///    in separate schema values, not in attributes or base classes.
module Shop.Model

open System
open Plasm

type CustomerId = CustomerId of int64
type OrderId = OrderId of int64
type ProductId = ProductId of int64
type OrderLineId = OrderLineId of int64
type PaymentId = PaymentId of int64

type OrderStatus =
    | Draft
    | Published
    | Cancelled

type PaymentStatus =
    | Pending
    | Succeeded
    | Failed

/// Only valid quantities can exist; the database cannot smuggle in a zero.
type Quantity =
    private
    | Quantity of int

    member q.Value = let (Quantity v) = q in v
    static member create v = if v > 0 then Ok(Quantity v) else Error $"quantity must be positive, got {v}"

type Customer =
    { Id: CustomerId
      Name: string
      Email: string
      Orders: Assoc<Order list> }

and Order =
    { Id: OrderId
      CustomerId: CustomerId option
      Status: OrderStatus
      Total: decimal
      CancellationReason: string option
      PlacedAt: DateTime
      Version: int
      Customer: Assoc<Customer option> // optional belongs-to
      Lines: Assoc<OrderLine list> // has-many
      Payments: Assoc<Payment list> } // has-many

and OrderLine =
    { Id: OrderLineId
      OrderId: OrderId
      ProductId: ProductId
      Quantity: Quantity
      UnitPrice: decimal
      Product: Assoc<Product> } // required belongs-to

and Product = { Id: ProductId; Sku: string; Name: string }

and Payment =
    { Id: PaymentId
      OrderId: OrderId
      Amount: decimal
      Status: PaymentStatus
      CreatedAt: DateTime }

type ArticleId = ArticleId of int64
type TagId = TagId of int64

/// Articles and tags are linked many-to-many through article_tags.
type Article =
    { Id: ArticleId
      Title: string
      Tags: Assoc<Tag list> } // many-to-many

and Tag = { Id: TagId; Name: string }

module Db =
    let customers =
        schema<Customer> "customers" {
            identityKey (fun c -> c.Id)
            hasMany (fun c -> c.Orders) (fun o -> o.CustomerId)
        }

    let orders =
        schema<Order> "orders" {
            identityKey (fun o -> o.Id)
            version (fun o -> o.Version)
            belongsTo (fun o -> o.Customer) (fun o -> o.CustomerId)
            hasMany (fun o -> o.Lines) (fun l -> l.OrderId) OnReplace.Delete
            hasMany (fun o -> o.Payments) (fun p -> p.OrderId)
        }

    let orderLines =
        schema<OrderLine> "order_lines" {
            identityKey (fun l -> l.Id)
            belongsTo (fun l -> l.Product) (fun l -> l.ProductId)
        }

    let products = schema<Product> "products" { identityKey (fun p -> p.Id) }
    let payments = schema<Payment> "payments" { identityKey (fun p -> p.Id) }

    let articles =
        schema<Article> "articles" {
            identityKey (fun a -> a.Id)
            manyToMany (fun a -> a.Tags) "article_tags" "article_id" "tag_id"
        }

    let tags = schema<Tag> "tags" { identityKey (fun t -> t.Id) }

    /// Every non-primitive scalar needs an explicit, lossless mapping.
    let scalars =
        [ Scalar.wrap CustomerId (fun (CustomerId v) -> v)
          Scalar.wrap OrderId (fun (OrderId v) -> v)
          Scalar.wrap ProductId (fun (ProductId v) -> v)
          Scalar.wrap OrderLineId (fun (OrderLineId v) -> v)
          Scalar.wrap PaymentId (fun (PaymentId v) -> v)
          Scalar.wrap ArticleId (fun (ArticleId v) -> v)
          Scalar.wrap TagId (fun (TagId v) -> v)
          Scalar.tryWrap Quantity.create (fun (q: Quantity) -> q.Value)
          Scalar.enum [ Draft, "draft"; Published, "published"; Cancelled, "cancelled" ]
          Scalar.enum [ Pending, "pending"; Succeeded, "succeeded"; Failed, "failed" ] ]

    /// Validated once at start-up; every problem is reported with model and field.
    let model = Model.buildOrFail scalars [ customers; orders; orderLines; products; payments; articles; tags ]
