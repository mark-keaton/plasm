namespace Plasm.Specs

open System.Text.RegularExpressions
open Reqnroll
open Plasm

/// A domain of its own for owned-collections.feature: an invoice that owns its
/// lines, as a plain list.
module OwnedInvoices =

    type InvoiceId = InvoiceId of int64
    type LineId = LineId of int64

    [<Table "invoice_lines">]
    type Line =
        { Id: LineId
          Invoice: InvoiceId
          Item: string
          Amount: decimal }

    [<Table "invoices">]
    type Invoice =
        { Id: InvoiceId
          Number: string
          Lines: Line list }

    let model = Model.buildOrFail [] [ typeof<Invoice> ]

    /// Lines that name no table, so a list of them cannot be stored.
    type BareLine = { Item: string; Amount: decimal }

    [<Table "invoices">]
    type BareInvoice = { Id: InvoiceId; Lines: BareLine list }

    let tables =
        "drop table if exists invoice_lines; drop table if exists invoices;
         create table invoices (id bigint generated always as identity primary key, number text not null);
         create table invoice_lines (
           id bigint generated always as identity primary key,
           invoice bigint not null references invoices(id),
           item text not null,
           amount numeric(12,2) not null);"

    let line item amount =
        { Id = LineId 0L
          Invoice = InvoiceId 0L
          Item = item
          Amount = amount }

    let invoice number ls =
        { Id = InvoiceId 0L
          Number = number
          Lines = ls }

    /// `"pen" 2 and "ink" 5` as lines.
    let parse (text: string) =
        Regex.Matches(text, "\"(\\w+)\" (\\d+)") |> Seq.map (fun m -> line m.Groups.[1].Value (decimal m.Groups.[2].Value)) |> List.ofSeq

    let described (ls: Line list) = ls |> List.map (fun l -> l.Item, l.Amount)

open OwnedInvoices

/// Feature: An owned collection is a plain list, loaded and written with its owner
[<Binding>]
type OwnedSteps(w: World) =

    let repo () = w.Get<Repo> "invoiceRepo"

    let insert (i: Invoice) =
        task {
            let! r = repo().Transact(Tx.insert i: Tx<_, unit>)
            return ok r
        }

    let readAll () =
        task {
            let! r = repo().All(plasm { for i in tableOf<Invoice> do orderBy i.Id })
            return ok r
        }

    let readOne () =
        task {
            let! all = readAll ()
            return List.exactlyOne all
        }

    let storedLines (id: InvoiceId) =
        task {
            let (InvoiceId raw) = id
            let! text = w.Db.Scalar<string>($"select coalesce(string_agg(item || ' ' || trim(to_char(amount, 'FM9999990.##')), ', ' order by id), '') from invoice_lines where invoice = {raw}")
            return text
        }

    [<StepDefinition("invoices and invoice_lines tables")>]
    member _.Tables() =
        task {
            let! _ = w.Db.Exec OwnedInvoices.tables
            w.Set("invoiceRepo", w.Db.RepoWith(model = OwnedInvoices.model))
        }
        |> unit

    [<StepDefinition("^an Invoice \"(.*)\" with the lines (.+)$")>]
    member _.WithLines(number: string, text: string) =
        task {
            let! i = insert (invoice number (parse text))
            w.Set("invoice", i)
        }
        |> unit

    [<StepDefinition("^an Invoice \"(.*)\" without lines$")>]
    member _.WithoutLines(number: string) =
        task {
            let! i = insert (invoice number [])
            w.Set("invoice", i)
        }
        |> unit

    [<StepDefinition("^Invoices \"(.*)\", \"(.*)\" and \"(.*)\", each with one line$")>]
    member _.Three(a: string, b: string, c: string) =
        task {
            for n in [ a; b; c ] do
                let! _ = insert (invoice n [ line "pen" 1m ])
                ()
        }
        |> unit

    [<StepDefinition("I read the Invoices")>]
    member _.Read() =
        task {
            w.MarkCommands()
            let! all = readAll ()
            w.Set("invoices", all)
        }
        |> unit

    [<StepDefinition("^\"(.*)\" reads with the lines (.+), in the order they were written$")>]
    member _.ReadsWith(number: string, text: string) =
        let i = w.Get<Invoice list> "invoices" |> List.find (fun i -> i.Number = number)
        equal (described (parse text)) (described i.Lines) "lines"

    [<StepDefinition("^\"(.*)\" reads with no lines$")>]
    member _.ReadsWithout(number: string) =
        let i = w.Get<Invoice list> "invoices" |> List.find (fun i -> i.Number = number)
        equal [] i.Lines "lines"

    [<StepDefinition("two commands are sent")>]
    member _.TwoCommands() = equal 2 w.CommandsSinceMark.Length "commands"

    [<StepDefinition("I select whole Invoices joined with their lines")>]
    member _.Join() =
        task {
            w.MarkCommands()
            let! r =
                repo().All(
                    plasm {
                        for i in tableOf<Invoice> do
                            join l in tableOf<Line> on (i.Id = l.Invoice)
                            select (i, l)
                    }
                )

            w.Set("joined", ok r)
        }
        |> unit

    [<StepDefinition("each joined Invoice holds all its lines, read in one more command")>]
    member _.JoinedWithLines() =
        let joined = w.Get<(Invoice * Line) list> "joined"
        equal 2 joined.Length "one row per line"

        for i, _ in joined do
            equal [ "pen", 2m; "ink", 5m ] (described i.Lines) "the Invoice's lines"

        equal 2 w.CommandsSinceMark.Length "the join, and the lines its Invoices own"

    [<StepDefinition("^I insert an Invoice \"(.*)\" with the lines (.+)$")>]
    member _.InsertWith(number: string, text: string) =
        task {
            let! i = insert (invoice number (parse text))
            w.Set("invoice", i)
        }
        |> unit

    [<StepDefinition("^the invoice_lines table holds (.+) for that Invoice$")>]
    member _.Holds(text: string) =
        task {
            let expected = parse text |> List.map (fun l -> $"{l.Item} {l.Amount}") |> String.concat ", "
            let! text = storedLines (w.Get<Invoice> "invoice").Id
            equal expected text "stored lines"
        }
        |> unit

    [<StepDefinition("the Invoice returned holds its lines with their keys")>]
    member _.ReturnedKeys() =
        let i = w.Get<Invoice> "invoice"
        check (i.Lines.Length = 2 && i.Lines |> List.forall (fun l -> l.Id <> LineId 0L && l.Invoice = i.Id)) $"%A{i}"

    [<StepDefinition("I read it, then change the pen to 3, remove the ink and add \"pad\" 4")>]
    member _.ChangeLines() =
        task {
            let! read = readOne ()
            let pen = read.Lines |> List.find (fun l -> l.Item = "pen")
            w.Set("penKey", pen.Id)

            let! r =
                repo().Transact(read |> edit (fun i -> { i with Lines = [ { pen with Amount = 3m }; line "pad" 4m ] }))

            ok r |> ignore
        }
        |> unit

    [<StepDefinition("I read it, then change only the pen to 3")>]
    member _.ChangePen() =
        task {
            let! read = readOne ()
            let pen = read.Lines |> List.find (fun l -> l.Item = "pen")
            let others = read.Lines |> List.filter (fun l -> l.Item <> "pen")
            w.MarkCommands()
            let! r = repo().Transact(read |> edit (fun i -> { i with Lines = { pen with Amount = 3m } :: others }))
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the pen line's change is one command, which itself says the line belongs to the Invoice")>]
    member _.OnePenCommand() =
        let lines = w.CommandsSinceMark |> List.map (fun e -> e.Sql) |> List.filter (fun q -> q.Contains "invoice_lines")
        equal 1 lines.Length $"commands on invoice_lines: %A{lines}"
        check (lines.[0].StartsWith "UPDATE" && lines.[0].Contains "\"invoice\" = ") lines.[0]

    [<StepDefinition("the pen line kept its key")>]
    member _.PenKept() =
        task {
            let! read = readOne ()
            let pen = read.Lines |> List.find (fun l -> l.Item = "pen")
            equal (w.Get<LineId> "penKey") pen.Id "pen's key"
        }
        |> unit

    [<StepDefinition("^I read them, then add the line of \"(.*)\" to \"(.*)\"$")>]
    member _.Steal(from: string, into: string) =
        task {
            let! all = readAll ()
            let source = all |> List.find (fun i -> i.Number = from)
            let target = all |> List.find (fun i -> i.Number = into)
            let! r = repo().Transact(Tx.update (target, { target with Lines = target.Lines @ source.Lines }): Tx<Invoice, unit>)
            w.Set("stolen", r)
        }
        |> unit

    [<StepDefinition("^the change is refused, saying the line does not belong to \"(.*)\"$")>]
    member _.StealRefused(_into: string) =
        match w.Get<Result<Invoice, TransactionError<unit>>> "stolen" with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            check (errors |> List.exists (fun e -> e.Kind = FieldErrorKind.NotOwned && e.Message.Contains "does not belong")) $"%A{errors}"
        | other -> failwithf "expected the change to be refused, got %A" other

    [<StepDefinition("^the invoice_lines table holds (\\d+) lines$")>]
    member _.LineCount(n: int64) =
        task {
            let! count = w.Db.Scalar<int64>("select count(*) from invoice_lines")
            equal n count "lines"
        }
        |> unit

    [<StepDefinition("I read it, then write it with its pen line listed twice")>]
    member _.Twice() =
        task {
            let! read = readOne ()
            let pen = read.Lines |> List.find (fun l -> l.Item = "pen")
            let! r = repo().Transact(Tx.update (read, { read with Lines = [ pen; pen ] }): Tx<Invoice, unit>)
            w.Set("twice", r)
        }
        |> unit

    [<StepDefinition("the change is refused as a duplicate line")>]
    member _.TwiceRefused() =
        match w.Get<Result<Invoice, TransactionError<unit>>> "twice" with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            check (errors |> List.exists (fun e -> e.Kind = FieldErrorKind.DuplicateIdentity && e.Field = "Lines")) $"%A{errors}"
        | other -> failwithf "expected the change to be refused, got %A" other

    [<StepDefinition("I delete the Invoice I read")>]
    member _.Delete() =
        task {
            let! read = readOne ()
            let! r = repo().Transact(Tx.delete read: Tx<_, unit>)
            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the invoice_lines table is empty")>]
    member _.Empty() =
        task {
            let! n = w.Db.Scalar<int64>("select count(*) from invoice_lines")
            equal 0L n "lines"
        }
        |> unit

    [<StepDefinition("I build a model of Invoices whose lines name no table")>]
    member _.UnstoredLines() =
        match Model.build [] [ typeof<BareInvoice> ] with
        | Ok _ -> failwith "the model was built"
        | Error errors -> w.Set("refusal", errors)
