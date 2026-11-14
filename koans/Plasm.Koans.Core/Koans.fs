/// What every lesson uses: blanks, test helpers (shouldBe, ok, err, commands)
/// and the koan types.
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

/// What Plasm has reported to RepoOptions.OnCommand since the koan began. Set
/// by whatever runs the koans.
let mutable internal reported: unit -> CommandEvent list = fun () -> []

/// The SQL of every command this koan has sent to the database, in order. In
/// your app, Plasm reports each command to the function you give as
/// RepoOptions.OnCommand; the koans give one that keeps them here.
let commands () = reported () |> List.map (fun c -> c.Sql)

/// One koan: a name (matching its row in the lesson's feature file) and a body.
type Koan =
    { Name: string
      File: string
      Line: int
      /// Given a repository whose changes are undone when the koan ends.
      Body: Repo -> Task<unit> }

    /// A koan whose body is ordinary code.
    static member Sync(name: string, body: Repo -> unit, [<CallerFilePath>] ?file: string, [<CallerLineNumber>] ?line: int) =
        { Name = name
          File = defaultArg file ""
          Line = defaultArg line 0
          Body =
            fun repo ->
                body repo
                Task.FromResult() }

    /// A koan whose body is a task (it talks to the database).
    static member Task(name: string, body: Repo -> Task<unit>, [<CallerFilePath>] ?file: string, [<CallerLineNumber>] ?line: int) =
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
