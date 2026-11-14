namespace Plasm.Specs

open System.Text.RegularExpressions
open Reqnroll
open Plasm

/// A domain of its own for owned-collections.feature: an invoice that owns its
/// lines, as a plain list.
module OwnedInvoices =

    type InvoiceId = InvoiceId of int64
    type LineId = LineId of int64

    type Line =
        { Id: LineId
          Invoice: InvoiceId
          Item: string
          Amount: decimal }

    type Invoice =
        { Id: InvoiceId
          Number: string
          Lines: Line list }

    let lines = schema<Line> "invoice_lines" { identityKey (fun l -> l.Id) }

    let invoices =
        schema<Invoice> "invoices" {
            identityKey (fun i -> i.Id)
            owns (fun i -> i.Lines) (fun l -> l.Invoice)
        }

    let model = Model.buildOrFail [] [ invoices; lines ]

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
            let! r = repo().Insert(Changeset.insert i)
            return ok r
        }

    let readAll () =
        task {
            let! r = repo().All(plasm { for i in invoices do orderBy i.Id })
            return ok r
        }

    let readOne () =
        task {
            let! all = readAll ()
            return List.exactlyOne all
        }

    let stored (id: InvoiceId) =
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
            let! r =
                repo().All(
                    plasm {
                        for i in invoices do
                            join l in lines on (i.Id = l.Invoice)
                            select (i, l)
                    }
                )

            w.Set("joined", r |> Result.map ignore)
        }
        |> unit

    [<StepDefinition("the query is refused, saying an owned list is loaded with its owner")>]
    member _.Refused() =
        match w.Get<Result<unit, DbError>> "joined" with
        | Error(DbError.Translation e) -> check ((string e).Contains "owned") (string e)
        | other -> failwithf "expected a translation error, got %A" other

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
            let! text = stored (w.Get<Invoice> "invoice").Id
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
                repo().Update(read |> Changeset.edit (fun i -> { i with Lines = [ { pen with Amount = 3m }; line "pad" 4m ] }))

            ok r |> ignore
        }
        |> unit

    [<StepDefinition("the pen line kept its key")>]
    member _.PenKept() =
        task {
            let! read = readOne ()
            let pen = read.Lines |> List.find (fun l -> l.Item = "pen")
            equal (w.Get<LineId> "penKey") pen.Id "pen's key"
        }
        |> unit

    [<StepDefinition("I delete the Invoice I read")>]
    member _.Delete() =
        task {
            let! read = readOne ()
            let! r = repo().Delete(Changeset.delete read)
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

    [<StepDefinition("I build a model of Invoices without owns")>]
    member _.WithoutOwns() =
        match Model.build [] [ schema<Invoice> "invoices" { identityKey (fun i -> i.Id) }; lines ] with
        | Ok _ -> failwith "the model was built"
        | Error errors -> w.Set("refusal", errors)
