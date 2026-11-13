/// What every lesson uses: blanks, assertions, and the koan context.
[<AutoOpen>]
module Plasm.Koans.Koans

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Plasm

/// Raised by a blank the learner has not filled in yet.
exception BlankException

/// Raised when a koan's assertion does not hold.
exception KoanFailure of expected: string * actual: string

/// A blank to fill in. It has whatever type its place needs, so the koans
/// compile as shipped; evaluating it means the koan is not answered yet.
let __<'T> : 'T = raise BlankException

let private show (v: 'T) = sprintf "%A" v

/// The koans' one assertion: `actual |> shouldBe expected`.
let shouldBe (expected: 'T) (actual: 'T) =
    if actual <> expected then
        raise (KoanFailure(show expected, show actual))

/// The value of an Ok result; anything else fails the koan, showing the error.
let ok (r: Result<'T, 'E>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> raise (KoanFailure("Ok _", show (Error e: Result<unit, 'E>)))

/// The error of an Error result; Ok fails the koan, showing the value.
let err (r: Result<'T, 'E>) : 'E =
    match r with
    | Error e -> e
    | Ok v -> raise (KoanFailure("Error _", show (Ok v: Result<'T, unit>)))

/// What a koan can use: a repository whose changes are rolled back when the
/// koan ends, and the commands it has sent.
type KoanContext internal (repo: Repo, commands: unit -> CommandEvent list, explained: ResizeArray<string>) =
    /// A repository inside this koan's sandbox.
    member _.Repo = repo

    /// A repository over another model, inside the same sandbox.
    member _.RepoWith(model: Model) = Repo(model, repo.Provider)

    /// The SQL of every command this koan has sent so far, in order.
    member _.Commands = commands () |> List.map (fun c -> c.Sql)

    /// The SQL the repository would send for a query (no database access).
    member _.Sql(query: Query<'T>) =
        match repo.Explain query with
        | Ok plan ->
            explained.Add plan.Sql
            plan.Sql
        | Error e -> raise (KoanFailure("a query that translates", show e))

/// One koan: a name (matching its row in the lesson's feature file) and a body.
type Koan =
    { Name: string
      File: string
      Line: int
      Body: KoanContext -> Task<unit> }

    /// A koan whose body is ordinary code.
    static member Sync(name: string, body: KoanContext -> unit, [<CallerFilePath>] ?file: string, [<CallerLineNumber>] ?line: int) =
        { Name = name
          File = defaultArg file ""
          Line = defaultArg line 0
          Body =
            fun k ->
                body k
                Task.FromResult() }

    /// A koan whose body is a task (it talks to the database).
    static member Task(name: string, body: KoanContext -> Task<unit>, [<CallerFilePath>] ?file: string, [<CallerLineNumber>] ?line: int) =
        { Name = name
          File = defaultArg file ""
          Line = defaultArg line 0
          Body = body }

/// A lesson: a title (matching its feature file) and its koans, in order.
type Lesson =
    { Title: string
      Koans: Koan list }

module Lesson =
    let make title koans = { Title = title; Koans = koans }
