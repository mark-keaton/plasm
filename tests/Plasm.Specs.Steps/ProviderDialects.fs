namespace Plasm.Specs

open System.Threading.Tasks
open Reqnroll
open Plasm
open Plasm.TestDomain
open Plasm.TestDomain.Domain
open Plasm.TestDomain.Database

/// A provider whose dialect differs from PostgreSQL's in every way the
/// feature checks. Commands are only explained or refused, never run.
type TestDialectProvider(inner: IProvider) =
    let dialect =
        { new ISqlDialect with
            member _.Name = "TestSQL"
            member _.QuoteIdentifier s = inner.Dialect.QuoteIdentifier s
            member _.ParameterName i = inner.Dialect.ParameterName i
            // A string column is declared text; the catalogue may also say
            // character varying, which this dialect lists first.
            member _.ColumnType t = inner.Dialect.ColumnType t
            member _.CatalogueTypes t = if t = typeof<string> then [ "character varying"; "text" ] else inner.Dialect.CatalogueTypes t
            member _.CastType t = inner.Dialect.CastType t
            member _.NullSafeComparison negated = if negated then "IS NOT" else "IS"

            member _.LikeEscape = " ESCAPE '\\'"
            member _.FunctionName name =
                match name with
                | "char_length" -> "length"
                | n -> n

            member _.Json = None
            member _.Begin isolation = inner.Dialect.Begin isolation

            member _.Capabilities =
                { inner.Dialect.Capabilities with
                    ArrayParameters = false
                    Upserts = false
                    Returning = false
                    RowLocking = false
                    EnumTypes = false
                    ArrayColumns = false } }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(_, _) = Task.FromResult None
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

/// PostgreSQL's provider, its null-safe comparison spelled with a marker the
/// SQL it sends must carry: valid PostgreSQL, so a write it renders runs.
type MarkedComparisonProvider(inner: IProvider) =
    let d = inner.Dialect

    let dialect =
        { new ISqlDialect with
            member _.Name = d.Name
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.NullSafeComparison negated = d.NullSafeComparison negated + " /* the dialect's */"
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)
