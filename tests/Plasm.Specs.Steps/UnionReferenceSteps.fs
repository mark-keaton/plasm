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

/// union-references.feature: people whose addresses, values stored in
/// their row, name a city.
module ValueReferences =
    type CityId = CityId of int64
    type PersonId = PersonId of int64

    [<Table "cities">]
    type City = { Id: CityId; Name: string }

    type Address =
        { Street: string
          City: CityId
          Near: CityId option }

    [<Table "people">]
    type Person =
        { Id: PersonId
          Home: Address
          Work: Address option }

    let model = Model.buildOrFail [] [ typeof<City>; typeof<Person> ]

open UnionReferences
open ValueReferences

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
            refs |> List.filter (fun r -> r.Record = "Shelf" && r.Field = "Status.borrower" && r.Target = "Member")

        equal 1 found.Length $"references of Shelf.Status.borrower in %A{refs}"

    [<StepDefinition("^the shelves tables as Plasm's DDL creates them$")>]
    member _.Tables() = w.Db.Exec(repo().Ddl()) |> unit

    [<StepDefinition("^the shelves tables as Plasm's DDL creates them, without the foreign key on status_borrower$")>]
    member _.TablesWithoutKey() =
        task {
            let! _ = w.Db.Exec(repo().Ddl())
            do! w.Db.DropConstraint("shelves", "shelves_status_borrower_fkey")
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
                 && i.Record = "Shelf"
                 && i.Field = Some "Status.borrower"
                 && i.Column = Some "status_borrower"
                 && i.Message.Contains "members"))
            $"the issues are %A{issues}"

    // -- An id in a value ----------------------------------------------------

    member private _.People() = w.Db.RepoWith(model = ValueReferences.model)

    [<StepDefinition("^I list the references the model of people infers$")>]
    member _.PeopleReferences() =
        w.Set("references", ValueReferences.model.References)

    [<StepDefinition("^Person.Home.City, Person.Home.Near, Person.Work.City and Person.Work.Near reference City, each once$")>]
    member _.PeopleCity() =
        let refs = w.Get<ModelReference list> "references"

        for field in [ "Home.City"; "Home.Near"; "Work.City"; "Work.Near" ] do
            let found = refs |> List.filter (fun r -> r.Record = "Person" && r.Field = field && r.Target = "City")
            equal 1 found.Length $"references of Person.{field} in %A{refs}"

    [<StepDefinition("^the people tables as Plasm's DDL creates them, with a city$")>]
    member this.PeopleTables() =
        task {
            let! _ = w.Db.Exec(this.People().Ddl())
            let! city = w.Db.Scalar<int64>("insert into cities (name) values ('Leeds') returning id")
            w.Set("city", city)
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^the people tables as Plasm's DDL creates them, without the foreign key on home_city$")>]
    member this.PeopleWithoutKey() =
        task {
            let! _ = w.Db.Exec(this.People().Ddl())
            do! w.Db.DropConstraint("people", "people_home_city_fkey")
        }
        :> Threading.Tasks.Task

    member private this.InsertPerson(city: int64) =
        task {
            let person =
                { Id = PersonId 0L
                  Home = { Street = "1 Park Row"; City = CityId city; Near = None }
                  Work = None }

            let! r = this.People().Transact(Tx.insert person: Tx<Person, unit>)
            w.Set("person", r)
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^I insert a Person at home in that city, near no city, with no work address$")>]
    member this.HomeOnly() = this.InsertPerson(w.Get<int64> "city")

    [<StepDefinition("^I insert a Person at home in the city with id (\\d+)$")>]
    member this.HomeIn(city: int64) = this.InsertPerson city

    [<StepDefinition("^the Person is stored, with NULL in home_near, work_city and work_near$")>]
    member _.PersonStored() =
        task {
            ok (w.Get<Result<Person, TransactionError<unit>>> "person") |> ignore
            let! n = w.Db.Scalar<int64>("select count(*) from people where home_near is null and work_city is null and work_near is null")
            equal 1L n "people with NULL in the absent references"
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^the Person is refused with a reference error on (\\w+), naming (\\w+)$")>]
    member _.PersonRefused(field: string, name: string) =
        match w.Get<Result<Person, TransactionError<unit>>> "person" with
        | Error { Failure = TxFailure.InvalidChanges(_, errors) } ->
            check
                (errors |> List.exists (fun e -> e.Field = field && e.Kind = FieldErrorKind.ForeignKeyConstraint name))
                $"the errors are %A{errors}"
        | other -> failwithf "expected the field's reference error, got %A" other

    [<StepDefinition("^I check the people against the database$")>]
    member this.CheckPeople() =
        task {
            let! r = this.People().CheckSchema()
            w.Set("issues", ok r)
        }
        :> Threading.Tasks.Task

    [<StepDefinition("^the missing reference from Person's Home\\.City to cities is reported$")>]
    member _.PeopleMissing() =
        let issues = w.Get<SchemaIssue list> "issues"

        check
            (issues
             |> List.exists (fun i ->
                 i.Kind = SchemaIssueKind.MissingReference
                 && i.Record = "Person"
                 && i.Field = Some "Home.City"
                 && i.Column = Some "home_city"
                 && i.Message.Contains "cities"))
            $"the issues are %A{issues}"
