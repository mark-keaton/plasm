namespace Plasm.Specs

open System
open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A domain of its own for round-trips.feature: a sheet whose key the
/// application assigns (a Guid), and a card that owns comments and links labels.
module RoundTripDomain =

    type SheetId = SheetId of Guid
    type SheetRowId = SheetRowId of Guid

    [<Table "rt_sheets">]
    type Sheet =
        { Id: SheetId
          Name: string
          Rows: SheetRow list }

    and [<Table "rt_sheet_rows">] SheetRow =
        { Id: SheetRowId
          SheetId: SheetId
          Text: string }

    type CardId = CardId of int64
    type CommentId = CommentId of int64
    type LabelId = LabelId of int64

    [<Table "rt_labels">]
    type Label = { Id: LabelId; Name: string }

    [<Table "rt_cards">]
    type Card =
        { Id: CardId
          Title: string
          Comments: Comment list
          [<JoinTable "rt_card_labels">]
          Labels: Label list }

    and [<Table "rt_comments">] Comment = { Id: CommentId; CardId: CardId; Text: string }

    let model = Model.buildOrFail [] [ typeof<Sheet>; typeof<Card>; typeof<Label> ]

    let tables =
        "create table rt_sheets (id uuid primary key, name text not null);
         create table rt_sheet_rows (id uuid primary key, sheet_id uuid not null references rt_sheets(id), text text not null);
         create table rt_labels (id bigint generated always as identity primary key, name text not null);
         create table rt_cards (id bigint generated always as identity primary key, title text not null);
         create table rt_comments (id bigint generated always as identity primary key, card_id bigint not null references rt_cards(id), text text not null);
         create table rt_card_labels (
           card_id bigint not null references rt_cards(id),
           label_id bigint not null references rt_labels(id),
           primary key (card_id, label_id));
         insert into rt_labels (name) values ('urgent'), ('docs');"

    /// A provider whose dialect cannot send statements together.
    type OneAtATime(inner: IProvider) =
        let dialect =
            { new ISqlDialect with
                member _.Name = inner.Dialect.Name
                member _.QuoteIdentifier s = inner.Dialect.QuoteIdentifier s
                member _.ParameterName i = inner.Dialect.ParameterName i
                member _.ColumnTypes t = inner.Dialect.ColumnTypes t
                member _.NullSafeComparison negated = inner.Dialect.NullSafeComparison negated
                member _.LikeEscape = inner.Dialect.LikeEscape
                member _.FunctionName name = inner.Dialect.FunctionName name
                member _.Json = inner.Dialect.Json
                member _.Begin isolation = inner.Dialect.Begin isolation
                member _.Capabilities = { inner.Dialect.Capabilities with Batching = false } }

        interface IProvider with
            member _.Dialect = dialect
            member _.OpenConnection ct = inner.OpenConnection ct
            member _.Classify ex = inner.Classify ex
            member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
            member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

open RoundTripDomain

/// Feature: A write, and each level of loads, is one round trip
[<Binding>]
type RoundTripSteps(w: World) =

    let repo () = if w.Has "rtMainRepo" then w.Get<Repo> "rtMainRepo" else w.Repo
    let rtRepo () = w.Get<Repo> "rtRepo"

    let roundTrips () =
        w.CommandsSinceMark |> List.map (fun c -> c.RoundTrip) |> List.distinct |> List.length

    let product () =
        task {
            let! id =
                w.Db.Scalar<int64>(
                    "insert into products (sku, name) values ('pen', 'Pen') on conflict (sku) do update set name = excluded.name returning id"
                )

            return ProductId id
        }

    let insertOrder (lines: int) =
        task {
            let! p = product ()
            let lines = [ for n in 1..lines -> New.line p n (decimal n) ]
            let! r = repo().Transact(Tx.insert { New.order None Published 10m with Lines = lines }: Tx<_, unit>)
            return ok r
        }

    let readOrder (id: OrderId) =
        task {
            let! r = repo().All(plasm { for o in tableOf<Order> do where (o.Id = id) })
            return ok r |> List.exactlyOne
        }

    let order () = w.Get<Order> "rtOrder"

    let write (changed: Order) =
        task {
            w.MarkCommands()
            let! r = repo().Transact(Tx.update (order (), changed): Tx<_, unit>)
            w.Set("rtResult", r)
        }
        |> unit

    let result () = w.Get<Result<Order, TransactionError<unit>>> "rtResult"

    [<StepDefinition("sheets with rows, and cards with comments and labels")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec RoundTripDomain.tables
            w.Set("rtRepo", w.Db.RepoWith(model = RoundTripDomain.model))
        }
        |> unit

    [<StepDefinition("a provider that sends one statement at a time")>]
    member _.OneAtATime() =
        w.Set(
            "rtMainRepo",
            Repo(w.Db.Model, OneAtATime(w.Db.Provider), { RepoOptions.Default with OnCommand = Some w.Db.Recorder.Record })
        )

    [<StepDefinition("I insert an Order with two Lines")>]
    member _.InsertOrder() =
        task {
            let! p = product ()
            w.MarkCommands()
            let! r = repo().Transact(Tx.insert { New.order None Published 10m with Lines = [ New.line p 1 1m; New.line p 2 2m ] }: Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("^the (?:write|read) took (\\d+) round trips?$")>]
    member _.RoundTrips(n: int) = equal n (roundTrips ()) "round trips"

    [<StepDefinition("both Lines' INSERTs travelled in the second")>]
    member _.LinesTogether() =
        let commands = w.CommandsSinceMark
        let inserts = commands |> List.filter (fun c -> c.Sql.StartsWith "INSERT INTO \"order_lines\"")
        equal 2 inserts.Length "INSERTs into order_lines"
        equal 1 (inserts |> List.map (fun c -> c.RoundTrip) |> List.distinct).Length "round trips of the Lines"
        check (inserts.Head.RoundTrip <> commands.Head.RoundTrip) "the Lines travel after the Order"

    [<StepDefinition("I insert a Sheet with two Rows")>]
    member _.InsertSheet() =
        task {
            let id = SheetId(Guid.NewGuid())
            let row text = { Id = SheetRowId(Guid.NewGuid()); SheetId = id; Text = text }
            w.MarkCommands()
            let! r = rtRepo().Transact(Tx.insert { Id = id; Name = "plan"; Rows = [ row "one"; row "two" ] }: Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the Sheet reads back with its two Rows")>]
    member _.SheetBack() =
        task {
            let! r = rtRepo().All(Query.from (tableOf<Sheet>))
            let sheet = ok r |> List.exactlyOne
            equal [ "one"; "two" ] (sheet.Rows |> List.map (fun r -> r.Text) |> List.sort) "rows"
        }
        |> unit

    [<StepDefinition("an Order with three Lines")>]
    member _.OrderWithLines() =
        task {
            let! o = insertOrder 3
            w.Set("rtOrder", o)
        }
        |> unit

    [<StepDefinition("an Order with three Lines and two Payments")>]
    member _.OrderWithPayments() =
        task {
            let! c = w.Db.Scalar<int64>("insert into customers (name, email) values ('Ada', 'ada@example.com') returning id")
            let! o = insertOrder 3
            let (OrderId id) = o.Id
            let! _ = w.Db.Exec($"update orders set customer_id = {c} where id = {id}")

            let! _ =
                w.Db.Exec(
                    $"insert into payments (order_id, amount, status, created_at) values ({id}, 4, 'succeeded', now()), ({id}, 6, 'succeeded', now())"
                )

            w.Set("rtOrder", o)
        }
        |> unit

    [<StepDefinition("another Order with a Line")>]
    member _.Another() =
        task {
            let! o = insertOrder 1
            w.Set("rtOther", o)
        }
        |> unit

    [<StepDefinition("its second Line is changed by another writer")>]
    member _.ChangedElsewhere() =
        task {
            let (OrderLineId id) = (order ()).Lines.[1].Id
            let! _ = w.Db.Exec($"update order_lines set unit_price = unit_price + 1 where id = {id}")
            ()
        }
        |> unit

    [<StepDefinition("I change its first Line's quantity and remove its last Line")>]
    member _.ChangeAndRemove() =
        let o = order ()
        write { o with Lines = [ { o.Lines.[0] with Quantity = qty 5 }; o.Lines.[1] ] }

    [<StepDefinition("I change its first and second Lines' quantities")>]
    member _.ChangeTwo() =
        let o = order ()
        write { o with Lines = [ { o.Lines.[0] with Quantity = qty 5 }; { o.Lines.[1] with Quantity = qty 5 }; o.Lines.[2] ] }

    [<StepDefinition("I change its first Line's quantity and list the other Order's Line second")>]
    member _.ListOther() =
        let o = order ()
        let other = w.Get<Order> "rtOther"
        write { o with Lines = [ { o.Lines.[0] with Quantity = qty 5 }; other.Lines.[0]; o.Lines.[1]; o.Lines.[2] ] }

    [<StepDefinition("the Order reads back with two Lines, the first of quantity 5")>]
    member _.ReadsBack() =
        task {
            ok (result ()) |> ignore
            let! o = readOrder (order ()).Id
            equal 2 o.Lines.Length "lines"
            equal (qty 5) o.Lines.[0].Quantity "the first line's quantity"
        }
        |> unit

    [<StepDefinition("the Line's update compares \"(\\w+)\" once")>]
    member _.ComparesOnce(column: string) =
        let update = w.CommandsSinceMark |> List.find (fun c -> c.Sql.StartsWith "UPDATE \"order_lines\"")
        let where = update.Sql.Substring(update.Sql.IndexOf "\nWHERE ")
        let times = where.Split($"\"order_lines\".\"{column}\"").Length - 1
        equal 1 times $"comparisons of {column} in {update.Sql}"

    [<StepDefinition("the write fails as a conflict on the Order")>]
    member _.Conflict() =
        match (err (result ())).Failure with
        | TxFailure.Conflict entity -> equal "Order" entity "the conflicting record"
        | f -> failwithf "expected a conflict, got %A" f

    [<StepDefinition("the write is refused because (.+) does not belong to the Order")>]
    member _.NotOwned(field: string) =
        match (err (result ())).Failure with
        | TxFailure.InvalidChanges(_, errors) ->
            let e = errors |> List.exactlyOne
            equal field e.Field "the field"
            equal FieldErrorKind.NotOwned e.Kind "the error"
        | f -> failwithf "expected a refusal, got %A" f

    [<StepDefinition("no Line has quantity 5")>]
    member _.NoFive() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from order_lines where quantity = 5")
            equal 0L n "lines of quantity 5"
        }
        |> unit

    [<StepDefinition("a Card with two comments and two labels")>]
    member _.Card() =
        task {
            let! r = rtRepo().All(Query.from (tableOf<Label>))
            let labels = ok r
            let card = { Id = CardId 0L; Title = "a card"; Comments = [ for t in [ "one"; "two" ] -> { Id = CommentId 0L; CardId = CardId 0L; Text = t } ]; Labels = labels }
            let! r = rtRepo().Transact(Tx.insert card: Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("I read the Cards with what they own and link")>]
    member _.ReadCards() =
        task {
            w.MarkCommands()
            let! r = rtRepo().All(Query.from (tableOf<Card>))
            w.Set("rtCards", ok r)
        }
        |> unit

    [<StepDefinition("the Card reads with its two comments and two labels")>]
    member _.CardBack() =
        let card = w.Get<Card list> "rtCards" |> List.exactlyOne
        equal [ "one"; "two" ] (card.Comments |> List.map (fun c -> c.Text)) "comments"
        equal [ "docs"; "urgent" ] (card.Labels |> List.map (fun l -> l.Name) |> List.sort) "labels"

    [<StepDefinition("I read the Orders binding their Payments with let! and their Customer with and!")>]
    member _.LetAnd() =
        task {
            w.MarkCommands()

            let! r =
                repo().All(
                    plasm {
                        for o in tableOf<Order> do
                        let! payments = referring<Payment> o
                        and! customer = referredBy<Customer> o
                        return {| Order = o; Payments = payments; Customer = customer |}
                    }
                )

            let row = ok r |> List.exactlyOne
            equal 2 row.Payments.Length "payments"
            check row.Customer.IsSome "the customer is read"
        }
        |> unit

    [<StepDefinition("I read the Customers binding their Orders, each binding its Payments")>]
    member _.Nested() =
        task {
            w.MarkCommands()

            let! r =
                repo().All(
                    plasm {
                        for c in tableOf<Customer> do
                        let! orders =
                            plasm {
                                for o in referring<Order> c do
                                let! payments = referring<Payment> o
                                return {| Order = o; Payments = payments |}
                            }
                        return {| Customer = c; Orders = orders |}
                    }
                )

            let row = ok r |> List.exactlyOne
            equal 2 (row.Orders |> List.exactlyOne).Payments.Length "payments"
        }
        |> unit

    [<StepDefinition("each statement took a round trip of its own")>]
    member _.OneEach() =
        let commands = w.CommandsSinceMark
        check (commands.Length > 1) "more than one statement was sent"
        equal commands.Length (roundTrips ()) "round trips"
