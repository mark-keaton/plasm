namespace Plasm.Specs

open System
open Reqnroll
open Plasm

/// union-references.feature: shelves whose status may name the member who
/// has them. Two cases share the borrower field, and so its column.
module UnionReferences =
    type MemberId = MemberId of int64
    type ShelfId = ShelfId of int64

    [<Table "members">]
    type Member = { Id: MemberId; Name: string }

    type ShelfStatus =
        | Free
        | OnLoan of borrower: MemberId
        | Held of borrower: MemberId * until: DateTimeOffset

    [<Table "shelves">]
    type Shelf =
        { Id: ShelfId
          Label: string
          Status: ShelfStatus }

    let model = Model.buildOrFail [] [ typeof<Member>; typeof<Shelf> ]

open UnionReferences

[<Binding>]
type UnionReferenceSteps(w: World) =

    let repo () = w.Db.RepoWith(model = UnionReferences.model)

    let insert (status: ShelfStatus) =
        task {
            let! r = repo().Transact(Tx.insert { Id = ShelfId 0L; Label = "A1"; Status = status }: Tx<Shelf, unit>)
            w.Set("shelf", r)
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^I list the references the model of shelves infers$")>]
    member _.References() =
        w.Set("references", UnionReferences.model.References)

    [<StepDefinition("^Shelf\\.Status\\.borrower references Member, once$")>]
    member _.Borrower() =
        let refs = w.Get<ModelReference list> "references"

        let found =
            refs |> List.filter (fun r -> r.Model = "Shelf" && r.Field = "Status.borrower" && r.Target = "Member")

        equal 1 found.Length $"references of Shelf.Status.borrower in %A{refs}"

    [<StepDefinition("^the shelves tables as Plasm's DDL creates them$")>]
    member _.Tables() = w.Db.Exec(repo().Ddl()) |> unit

    [<StepDefinition("^the shelves tables as Plasm's DDL creates them, without the foreign key on status_borrower$")>]
    member _.TablesWithoutKey() =
        task {
            let! _ = w.Db.Exec(repo().Ddl())
            let! _ = w.Db.Exec "alter table shelves drop constraint if exists shelves_status_borrower_fkey"
            ()
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^I insert a free Shelf$")>]
    member _.Free() = insert Free

    [<StepDefinition("^I insert a Shelf on loan to the member with id (\\d+)$")>]
    member _.OnLoan(id: int64) = insert (OnLoan(MemberId id))

    [<StepDefinition("^the Shelf is stored, with no borrower$")>]
    member _.Stored() =
        task {
            ok (w.Get<Result<Shelf, TransactionError<unit>>> "shelf") |> ignore
            let! nulls = w.Db.Scalar<int64>("select count(*) from shelves where status = 'free' and status_borrower is null")
            equal 1L nulls "free shelves with no borrower"
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^the Shelf is refused with a reference error on (\\w+), naming (\\w+)$")>]
    member _.Refused(field: string, name: string) =
        match w.Get<Result<Shelf, TransactionError<unit>>> "shelf" with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            check
                (errors |> List.exists (fun e -> e.Field = field && e.Kind = FieldErrorKind.ForeignKeyConstraint name))
                $"the errors are %A{errors}"
        | other -> failwithf "expected the field's reference error, got %A" other

    [<StepDefinition("^I check the shelves against the database$")>]
    member _.Check() =
        task {
            let! r = repo().CheckSchema()
            w.Set("issues", ok r)
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^the missing reference from Shelf's Status\\.borrower to members is reported$")>]
    member _.Missing() =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.MissingReference
                 && i.Model = "Shelf"
                 && i.Field = Some "Status.borrower"
                 && i.Column = Some "status_borrower"
                 && i.Message.Contains "members"))
            $"the issues are %A{issues}"
