/// The koans' shop: Customers, Orders (with their OrderLines), Products,
/// Payments, Articles (linked to Tags), Accounts (with their Profiles) and
/// Deliveries. Ordinary F# records, the application's own. Each names its
/// table with [<Table>], and everything else about how it is stored follows
/// from its types: the key is the field named Id, a column is named after its
/// field in snake_case, an id is stored as the number it wraps, a Quantity or
/// an Email as its value (read back through its `create`), and OrderStatus or
/// PaymentStatus as its case name ('draft', 'published').
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
[<Table "customers">]
type Customer =
    { Id: CustomerId
      Name: string
      [<Unique>]
      Email: Email }

/// One line of an order: its own row in order_lines. It holds its order's
/// key (OrderId), so the order owns it.
[<Table "order_lines">]
type OrderLine =
    { Id: OrderLineId
      OrderId: OrderId
      ProductId: ProductId
      Quantity: Quantity
      UnitPrice: decimal }

/// An order: an aggregate holding its lines, which are read and written with
/// it, in the order of their key. Its customer is another aggregate, referred
/// to by id. Version is checked and incremented by every change.
[<Table "orders">]
type Order =
    { Id: OrderId
      CustomerId: CustomerId option
      Status: OrderStatus
      Total: decimal
      CancellationReason: string option
      PlacedAt: DateTime
      [<Version>]
      Version: int
      Lines: OrderLine list }

[<Table "products">]
type Product =
    { Id: ProductId
      [<Unique>]
      Sku: string
      Name: string }

/// A payment for an order: an aggregate of its own, referring to its order by
/// id. It holds an OrderId, but no Order has a list of payments, so no order
/// owns it.
[<Table "payments">]
type Payment =
    { Id: PaymentId
      OrderId: OrderId
      Amount: decimal
      Status: PaymentStatus
      CreatedAt: DateTime }

type AccountId = AccountId of int64
type ProfileId = ProfileId of int64

/// An account's profile: its own row in profiles. It holds its account's key,
/// so the account owns it.
[<Table "profiles">]
type Profile =
    { Id: ProfileId
      AccountId: AccountId
      Bio: string }

/// An account, which owns at most one profile.
[<Table "accounts">]
type Account =
    { Id: AccountId
      Name: string
      Profile: Profile option }

type ArticleId = ArticleId of int64
type TagId = TagId of int64

/// A tag: shared by the articles that link to it.
[<Table "tags">]
type Tag = { Id: TagId; Name: string }

/// An article, linked to shared tags: a tag holds no article's key, so the
/// links are rows of the article_tags table, and the tags stay shared.
[<Table "articles">]
type Article =
    { Id: ArticleId
      Title: string
      [<JoinTable "article_tags">]
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
[<Table "deliveries">]
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

type CategoryId = CategoryId of int64

/// A category of the shop's products. Its parent is another Category: the
/// categories form a tree, which Query.recursive reads.
[<Table "categories">]
type Category =
    { Id: CategoryId
      Name: string
      Parent: CategoryId option }

let qty n =
    match Quantity.create n with
    | Ok q -> q
    | Error e -> failwith e

let email s =
    match Email.create s with
    | Ok e -> e
    | Error e -> failwith e

/// The model: the records that are not owned. An order's lines and an
/// account's profile come with their owner.
module Db =

    let model =
        Model.buildOrFail
            []
            [ typeof<Customer>
              typeof<Order>
              typeof<Product>
              typeof<Payment>
              typeof<Article>
              typeof<Tag>
              typeof<Account>
              typeof<Delivery>
              typeof<Category> ]

/// The shop's queries: values, so `Repo.Start` can check them all.
module Queries =

    let drafts = plasm { for o in tableOf<Order> do where (o.Status = Draft) }

    /// A customer's orders, oldest first.
    let ordersOf =
        Query.prepare (fun (customer: Arg<CustomerId>) ->
            plasm {
                for o in tableOf<Order> do
                    where (o.CustomerId = Some customer.Value)
                    orderBy o.Id
            })

/// A query written by mistake: `shout` is F# that SQL cannot run, so Plasm
/// cannot translate it. `Repo.Start` finds it before it is ever run.
module Mistakes =

    let shout (s: string) = s.ToUpperInvariant() + "!"

    let shouting = plasm { for c in tableOf<Customer> do where (shout c.Name = "ADA!") }

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
