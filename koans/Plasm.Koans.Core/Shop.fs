/// The koans' shop: Customers, Orders (with their OrderLines), Products,
/// Payments, Articles (linked to Tags), Accounts (with their Profiles) and
/// Deliveries. Ordinary F# records, the application's own; how they are stored
/// lives in Db.
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

/// An email address. The only way to build one is `Email.create`, which
/// refuses text without an '@'. Plasm stores it as text and reads it back
/// through `Email.create` (by convention: no mapping is registered).
type Email =
    private
    | Email of string

    member e.Value =
        let (Email v) = e
        v

    static member create(v: string) =
        if v.Contains "@" then Ok(Email v) else Error $"an email needs an @, got '{v}'"

/// A customer. Customers are referred to by id: an order holds a CustomerId,
/// not the customer.
type Customer =
    { Id: CustomerId
      Name: string
      Email: Email }

/// One line of an order: its own row in order_lines, owned by the order.
type OrderLine =
    { Id: OrderLineId
      OrderId: OrderId
      ProductId: ProductId
      Quantity: Quantity
      UnitPrice: decimal }

/// An order: an aggregate holding its lines, which are read and written with
/// it. Its customer is another aggregate, referred to by id.
type Order =
    { Id: OrderId
      CustomerId: CustomerId option
      Status: OrderStatus
      Total: decimal
      CancellationReason: string option
      PlacedAt: DateTime
      Version: int
      Lines: OrderLine list }

type Product =
    { Id: ProductId
      Sku: string
      Name: string }

/// A payment for an order: an aggregate of its own, referring to its order by id.
type Payment =
    { Id: PaymentId
      OrderId: OrderId
      Amount: decimal
      Status: PaymentStatus
      CreatedAt: DateTime }

type AccountId = AccountId of int64
type ProfileId = ProfileId of int64

/// An account's profile: its own row in profiles, owned by the account.
type Profile =
    { Id: ProfileId
      AccountId: AccountId
      Bio: string }

/// An account, which owns at most one profile.
type Account =
    { Id: AccountId
      Name: string
      Profile: Profile option }

type ArticleId = ArticleId of int64
type TagId = TagId of int64

/// A tag: shared by the articles that link to it.
type Tag = { Id: TagId; Name: string }

/// An article, linked to shared tags through the article_tags table.
type Article =
    { Id: ArticleId
      Title: string
      Tags: Tag list }

type DeliveryId = DeliveryId of int64

/// Where a delivery is. A union whose cases carry facts: Plasm stores it across
/// columns (stage, stage_carrier, stage_signed_by) by convention.
type DeliveryStage =
    | Packing
    | InTransit of carrier: string
    | Delivered of signedBy: string

/// An Order's delivery. Its members are the domain's own vocabulary;
/// [<ReflectedDefinition>] lets a query use them, translated to SQL.
type Delivery =
    { Id: DeliveryId
      OrderId: OrderId
      Stage: DeliveryStage }

    /// The carrier, while the delivery is in transit.
    [<ReflectedDefinition>]
    member d.Carrier =
        match d.Stage with
        | InTransit carrier -> Some carrier
        | _ -> None

    /// The stage's name, to count deliveries by stage.
    [<ReflectedDefinition>]
    member d.StageName =
        match d.Stage with
        | Packing -> "packing"
        | InTransit _ -> "in transit"
        | Delivered _ -> "delivered"

let qty n =
    match Quantity.create n with
    | Ok q -> q
    | Error e -> failwith e

let email s =
    match Email.create s with
    | Ok e -> e
    | Error e -> failwith e

/// Schemas and the model. Everything else follows from the records' types:
/// a column is named after its field, an id is stored as the number it wraps,
/// a Quantity or an Email as its value (read back through its `create`), and
/// OrderStatus or PaymentStatus as its case name ('draft', 'published').
module Db =

    let customers =
        schema<Customer> "customers" {
            identityKey (fun c -> c.Id)
            unique (fun c -> c.Email)
        }

    /// An order is read and written whole: its lines are its own rows, in
    /// the order of their key.
    let orders =
        schema<Order> "orders" {
            identityKey (fun o -> o.Id)
            version (fun o -> o.Version)
            ownsOrderedBy (fun o -> o.Lines) (fun l -> l.OrderId) (fun l -> l.Id)
        }

    let orderLines = schema<OrderLine> "order_lines" { identityKey (fun l -> l.Id) }

    let products =
        schema<Product> "products" {
            identityKey (fun p -> p.Id)
            unique (fun p -> p.Sku)
        }

    let payments = schema<Payment> "payments" { identityKey (fun p -> p.Id) }

    /// An article owns only its links to tags (rows of article_tags); the
    /// tags themselves are shared.
    let articles =
        schema<Article> "articles" {
            identityKey (fun a -> a.Id)
            links (fun a -> a.Tags) "article_tags" "article_id" "tag_id"
        }

    let tags = schema<Tag> "tags" { identityKey (fun t -> t.Id) }

    let accounts =
        schema<Account> "accounts" {
            identityKey (fun a -> a.Id)
            ownsOptional (fun a -> a.Profile) (fun p -> p.AccountId)
        }

    let profiles = schema<Profile> "profiles" { identityKey (fun p -> p.Id) }

    let deliveries = schema<Delivery> "deliveries" { identityKey (fun d -> d.Id) }

    let schemas: ISchema list =
        [ customers; orders; orderLines; products; payments; articles; tags; accounts; profiles; deliveries ]

    let model = Model.buildOrFail [] schemas

/// The shop's queries: values, so `repo.CheckQueries` can check them all.
module Queries =

    let drafts = plasm { for o in Db.orders do where (o.Status = Draft) }

    /// A customer's orders, oldest first.
    let ordersOf =
        Query.prepare (fun (customer: Arg<CustomerId>) ->
            plasm {
                for o in Db.orders do
                    where (o.CustomerId = Some customer.Value)
                    orderBy o.Id
            })

/// A query written by mistake: `shout` is F# that SQL cannot run, so Plasm
/// cannot translate it. `repo.CheckQueries` finds it before it is ever run.
module Mistakes =

    let shout (s: string) = s.ToUpperInvariant() + "!"

    let shouting = plasm { for c in Db.customers do where (shout c.Name = "ADA!") }

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
          Lines = [] }

    let line product quantity price =
        { Id = OrderLineId unsaved
          OrderId = OrderId unsaved
          ProductId = product
          Quantity = qty quantity
          UnitPrice = price }

    let customer name address =
        { Id = CustomerId unsaved
          Name = name
          Email = email address }

    let product sku name =
        { Id = ProductId unsaved
          Sku = sku
          Name = name }
