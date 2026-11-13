namespace Plasm

open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks

/// Why a named transaction step failed.
type StepFailure =
    /// Validation or declared-constraint errors. `Changeset` is the boxed
    /// `Changeset<'T>` carrying them.
    | InvalidChanges of entity: string * errors: FieldError list * changeset: obj
    /// Optimistic concurrency conflict (or the row no longer exists).
    | ConcurrencyConflict of entity: string
    | DatabaseFailure of DbFailure
    | ReadFailure of DbError
    /// Raised by `Tx.fail` or a `Tx.run` step.
    | Rejected of reason: obj

/// A failed transaction. Every write in it was rolled back.
type TransactionError =
    { /// Name of the step that failed ("commit" if the commit itself failed).
      Step: string
      Failure: StepFailure
      /// Steps that had completed before the failure; their effects were rolled back.
      RolledBack: string list
      /// Attempts made (more than one only with a retry policy).
      Attempts: int }

/// Access to the transaction's connection for `Tx.run` steps. Only database
/// work belongs here: anything else cannot be rolled back.
type TxContext =
    { Connection: DbConnection
      Transaction: DbTransaction
      CancellationToken: CancellationToken }

type internal TxRuntime =
    { Session: Execution.Session
      Model: Model
      Completed: ResizeArray<string> }

/// A description of database work to run atomically. Building one performs no
/// database access; run it with `Repo.Transact`.
type Tx<'T> =
    internal
    | Pure of 'T
    | Effect of (TxRuntime -> Task<Result<'T, string * StepFailure>>)

/// Retry behaviour for transactions. The default is no retries: application
/// steps are never re-run silently.
type RetryPolicy =
    { MaxAttempts: int
      ShouldRetry: DbFailure -> bool }

    static member None =
        { MaxAttempts = 1
          ShouldRetry = fun _ -> false }

    /// Retries serialization failures and deadlocks, re-running the whole
    /// description (including its reads) in a fresh transaction.
    static member OnSerializationFailure(maxAttempts: int) =
        { MaxAttempts = maxAttempts
          ShouldRetry =
            fun f ->
                match f.Kind with
                | DbFailureKind.SerializationFailure
                | DbFailureKind.Deadlock -> true
                | _ -> false }

[<RequireQualifiedAccess>]
module Tx =

    let ret (v: 'T) : Tx<'T> = Pure v

    let internal run' (rt: TxRuntime) (tx: Tx<'T>) =
        match tx with
        | Pure v -> Task.FromResult(Ok v)
        | Effect f -> f rt

    let bind (f: 'T -> Tx<'U>) (tx: Tx<'T>) : Tx<'U> =
        match tx with
        | Pure v -> f v
        | Effect run ->
            Effect(fun rt ->
                task {
                    match! run rt with
                    | Ok v -> return! run' rt (f v)
                    | Error e -> return Error e
                })

    let map (f: 'T -> 'U) (tx: Tx<'T>) : Tx<'U> = bind (f >> Pure) tx

    let internal step (name: string) (body: TxRuntime -> Task<Result<'T, StepFailure>>) : Tx<'T> =
        Effect(fun rt ->
            task {
                if rt.Completed.Contains name then
                    return Error(name, Rejected(box $"step name '{name}' is used more than once in this transaction"))
                else
                    let! outcome =
                        task {
                            try
                                return! body rt
                            with
                            | :? OperationCanceledException as ex -> return Err.rethrow ex
                            | Execution.MaterializationFailure m -> return Error(ReadFailure(Materialization m))
                            | Translate.TranslationFailure t -> return Error(ReadFailure(Translation t))
                            | ex ->
                                match rt.Session.Provider.Classify ex with
                                | Some f -> return Error(DatabaseFailure f)
                                | None -> return Err.rethrow ex
                        }

                    match outcome with
                    | Ok v ->
                        rt.Completed.Add name
                        return Ok v
                    | Error f -> return Error(name, f)
            })

    let private writeStep name (cs: Changeset<'T>) : Tx<'T> =
        step name (fun rt ->
            task {
                match! Writes.run rt.Model rt.Session cs.State with
                | Ok v -> return Ok(unbox<'T> v)
                | Error(Writes.Invalid s) ->
                    let c = Changeset<'T>(s)
                    return Error(InvalidChanges(typeof<'T>.Name, c.Errors, box c))
                | Error(Writes.Stale _) -> return Error(ConcurrencyConflict typeof<'T>.Name)
                | Error(Writes.Failed f) -> return Error(DatabaseFailure f)
            })

    let insert name (cs: Changeset<'T>) : Tx<'T> = writeStep name cs
    let update name (cs: Changeset<'T>) : Tx<'T> = writeStep name cs
    let delete name (cs: Changeset<'T>) : Tx<'T> = writeStep name cs

    /// Runs a query inside the transaction.
    let all name (query: Query<'T>) : Tx<'T list> =
        step name (fun rt ->
            let rt = { rt with Session = { rt.Session with Args = query.Def.Args } }

            task {
              if not query.Def.Joins.IsEmpty then
                match Translate.projection rt.Model (ref false) query.Def None with
                | Error e -> return Error(ReadFailure(Translation e))
                | Ok(select, shape) ->
                    let! rows = Execution.readProjection rt.Session select shape query.Def.Root.Name
                    return Ok(rows |> List.map unbox<'T>)
              else
                match Planner.plan rt.Session.Provider.Dialect rt.Model query.Def with
                | Error e -> return Error(ReadFailure(Translation e))
                | Ok plan ->
                    let! rows = Execution.readRoot rt.Session plan
                    return Ok(rows |> List.map unbox<'T>)
            })

    /// Runs a bulk write (Bulk.update / delete / insert / upsert) as a step.
    let execute name (write: BulkWrite<'R>) : Tx<'R> =
        step name (fun rt ->
            task {
                match BulkExec.commands rt.Model rt.Session.Provider.Dialect write.Def with
                | Error e -> return Error(ReadFailure(Translation e))
                | Ok cmds ->
                    let args =
                        match write.Def with
                        | UpdateAll(q, _)
                        | DeleteAll q -> q.Args
                        | InsertAll _ -> [||]

                    let! v = BulkExec.run { rt.Session with Args = args } write.Def cmds
                    return Ok(write.Finish v)
            })

    /// Runs hand-written SQL as a step, reading rows as `'R` (see `Repo.Sql`).
    let sql<'R> name (sql: FormattableString) : Tx<'R list> =
        step name (fun rt ->
            task {
                let cmd = Execution.rawCommand rt.Model rt.Session.Provider.Dialect sql
                let! rows = Execution.query rt.Session cmd (Execution.rawReader rt.Model typeof<'R> "SQL")
                return Ok(rows |> Seq.map unbox<'R> |> List.ofSeq)
            })

    /// Runs a hand-written command as a step, returning the affected row count.
    let executeSql name (sql: FormattableString) : Tx<int> =
        step name (fun rt ->
            task {
                let cmd = Execution.rawCommand rt.Model rt.Session.Provider.Dialect sql
                let! n = Execution.execute rt.Session cmd
                return Ok n
            })

    let fail name (reason: obj) : Tx<'T> =
        step name (fun _ -> Task.FromResult(Error(Rejected reason)))

    /// A custom database step. It must only perform database work through the
    /// given context; external effects cannot be rolled back.
    let run name (f: TxContext -> Task<Result<'T, obj>>) : Tx<'T> =
        step name (fun rt ->
            task {
                let ctx =
                    { Connection = rt.Session.Connection
                      Transaction = Sandboxes.unwrap rt.Session.Transaction.Value
                      CancellationToken = rt.Session.Ct }

                match! f ctx with
                | Ok v -> return Ok v
                | Error r -> return Error(Rejected r)
            })

    /// Runs `f` for each item in order, naming each step `{name}[{index}]`.
    let traverse (name: string) (f: string -> 'a -> Tx<'b>) (items: 'a list) : Tx<'b list> =
        let rec go i acc items =
            match items with
            | [] -> Pure(List.rev acc)
            | x :: rest -> f $"{name}[{i}]" x |> bind (fun v -> go (i + 1) (v :: acc) rest)

        go 0 [] items

type TxBuilder() =
    member _.Return(v: 'T) = Tx.ret v
    member _.ReturnFrom(tx: Tx<'T>) = tx
    member _.Bind(tx: Tx<'T>, f: 'T -> Tx<'U>) = Tx.bind f tx
    member _.Zero() = Tx.ret ()
    member _.Delay(f: unit -> Tx<'T>) = f ()
    member _.Combine(a: Tx<unit>, b: Tx<'T>) = Tx.bind (fun () -> b) a

[<AutoOpen>]
module TxBuilderInstance =
    /// Describes a transaction: `transaction { let! o = Tx.insert "order" cs ... }`.
    let transaction = TxBuilder()
