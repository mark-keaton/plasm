/// The koans' shop: Customers, Orders, OrderLines, Products, Payments, Articles
/// and Tags, Accounts and Profiles. Ordinary F# records; persistence lives in Db.
module Plasm.Koans.Shop

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

/// A validated quantity. The only way to build one is `Quantity.create`.
type Quantity =
    private
    | Quantity of int

    member q.Value =
        let (Quantity v) = q
        v

    static member create(v: int) =
        if v > 0 then Ok(Quantity v) else Error $"quantity must be positive, got {v}"

    static member unsafeOne = Quantity 1

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
      Customer: Assoc<Customer option>
      Lines: Assoc<OrderLine list>
      Payments: Assoc<Payment list> }

and OrderLine =
    { Id: OrderLineId
      OrderId: OrderId
      ProductId: ProductId
      Quantity: Quantity
      UnitPrice: decimal
      Product: Assoc<Product> }

and Product =
    { Id: ProductId
      Sku: string
      Name: string }

and Payment =
    { Id: PaymentId
      OrderId: OrderId
      Amount: decimal
      Status: PaymentStatus
      CreatedAt: DateTime }

type AccountId = AccountId of int64
type ProfileId = ProfileId of int64

/// An Account has at most one Profile (has-one; the key is on the Profile).
type Account =
    { Id: AccountId
      Name: string
      Profile: Assoc<Profile option> }

and Profile =
    { Id: ProfileId
      AccountId: AccountId
      Bio: string }

type ArticleId = ArticleId of int64
type TagId = TagId of int64

/// Articles and Tags are linked many-to-many through article_tags.
type Article =
    { Id: ArticleId
      Title: string
      Tags: Assoc<Tag list> }

and Tag = { Id: TagId; Name: string }

let qty n =
    match Quantity.create n with
    | Ok q -> q
    | Error e -> failwith e

/// Schemas, scalar mappings and the model.
module Db =

    let customers =
        schema<Customer> "customers" {
            identityKey (fun c -> c.Id)
            unique (fun c -> c.Email)
            hasMany (fun c -> c.Orders) (fun o -> o.CustomerId)
        }

    let private ordersWith (linesOnReplace: OnReplace) =
        schema<Order> "orders" {
            identityKey (fun o -> o.Id)
            version (fun o -> o.Version)
            belongsTo (fun o -> o.Customer) (fun o -> o.CustomerId)
            hasMany (fun o -> o.Lines) (fun l -> l.OrderId) linesOnReplace
            hasMany (fun o -> o.Payments) (fun p -> p.OrderId)
        }

    /// Orders whose Lines have no OnReplace policy (the default: reject).
    let orders = ordersWith OnReplace.Reject

    /// Orders whose omitted Lines are deleted on replacement.
    let ordersDeletingLines = ordersWith OnReplace.Delete

    let orderLines =
        schema<OrderLine> "order_lines" {
            identityKey (fun l -> l.Id)
            belongsTo (fun l -> l.Product) (fun l -> l.ProductId)
        }

    let products =
        schema<Product> "products" {
            identityKey (fun p -> p.Id)
            unique (fun p -> p.Sku)
        }

    let payments = schema<Payment> "payments" { identityKey (fun p -> p.Id) }

    let articles =
        schema<Article> "articles" {
            identityKey (fun a -> a.Id)
            manyToMany (fun a -> a.Tags) "article_tags" "article_id" "tag_id"
        }

    let tags = schema<Tag> "tags" { identityKey (fun t -> t.Id) }

    let accounts =
        schema<Account> "accounts" {
            identityKey (fun a -> a.Id)
            hasOne (fun a -> a.Profile) (fun p -> p.AccountId)
        }

    let profiles = schema<Profile> "profiles" { identityKey (fun p -> p.Id) }

    let scalars =
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

    let schemasWith (orders: Schema<Order>) : ISchema list =
        [ customers; orders; orderLines; products; payments; articles; tags; accounts; profiles ]

    let model = Model.buildOrFail scalars (schemasWith orders)
    let modelDeletingLines = Model.buildOrFail scalars (schemasWith ordersDeletingLines)

/// Values used to build records before insert (generated fields are ignored).
module New =
    let unsaved = 0L

    let order customer status total =
        { Id = OrderId unsaved
          CustomerId = customer
          Status = status
          Total = total
          CancellationReason = None
          PlacedAt = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
          Version = 1
          Customer = NotLoaded
          Lines = NotLoaded
          Payments = NotLoaded }

    let line product quantity price =
        { Id = OrderLineId unsaved
          OrderId = OrderId unsaved
          ProductId = product
          Quantity = qty quantity
          UnitPrice = price
          Product = NotLoaded }

    let customer name email =
        { Id = CustomerId unsaved
          Name = name
          Email = email
          Orders = NotLoaded }

    let product sku name =
        { Id = ProductId unsaved
          Sku = sku
          Name = name }
