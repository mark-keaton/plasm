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
            member _.StoreCast(t, array) = inner.Dialect.StoreCast(t, array)
            member _.TextCast = inner.Dialect.TextCast
            member _.EmptyArray = "'[]'"
            member _.ListHas(w, element, column) = inner.Dialect.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = inner.Dialect.ListIsEmpty(w, column)
            // A list is one JSON parameter, read as rows by json_each.
            member _.ListValue values = inner.Dialect.ListValue values
            member _.NotInList(w, operand, list, cast) = inner.Dialect.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = inner.Dialect.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = inner.Dialect.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, _) =
                operand ()
                w $" IN (SELECT value FROM json_each({list}))"
            member _.Catalogue = inner.Dialect.Catalogue
            // Its own key clause, and foreign keys in their tables.
            member _.Ddl =
                let d = inner.Dialect.Ddl

                { new IDdlDialect with
                    member _.Reserved = d.Reserved
                    member _.GeneratedKey = " primary key autoincrement"
                    member _.StoreColumn typeName = d.StoreColumn typeName
                    member _.EnumType(typeName, labels) = d.EnumType(typeName, labels)
                    member _.EnumCheck(column, labels) = d.EnumCheck(column, labels)
                    member _.ListCheck column = d.ListCheck column
                    member _.ForeignKeysInTables = true }
            member _.UnboundedLimit = Some "-1"
            member _.NullSafeComparison negated = if negated then "IS NOT" else "IS"

            member _.LikeEscape = " ESCAPE '\\'"
            member _.FunctionName name =
                match name with
                | "char_length" -> "length"
                // Over 0 and 1, the greatest is whether any is true.
                | "BOOL_OR" -> "MAX"
                | n -> n

            member _.Json = None
            member _.Exact = inner.Dialect.Exact
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
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(_, _) = Task.FromResult None
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

/// PostgreSQL's dialect for queries, with a database type of its own (an enum
/// type, a document type) stored as the dialect says: values of an enum sent
/// as they are and read as they are, and documents of `documentType`.
type StoredAsDialectSays(inner: IProvider, documentType: string list) =
    let d = inner.Dialect

    let json =
        d.Json
        |> Option.map (fun j ->
            { new IJsonDialect with
                member _.StoreType = documentType
                member _.Member(w, doc, key) = j.Member(w, doc, key)
                member _.Text(w, doc) = j.Text(w, doc)
                member _.AnyElement(w, doc, alias, condition) = j.AnyElement(w, doc, alias, condition)
                member _.Element(w, alias) = j.Element(w, alias)
                member _.Contains(w, doc, value) = j.Contains(w, doc, value) })

    let dialect =
        { new ISqlDialect with
            member _.Name = d.Name
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.StoreCast(_, _) = None
            member _.TextCast = None
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) = d.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = d.ListIsEmpty(w, column)
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = d.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue = d.Catalogue
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = json
            member _.Exact = d.Exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    member _.DocumentType = documentType

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)
        member _.Describe(c, ct) = inner.Describe(c, ct)

/// PostgreSQL's provider, its dialect's catalogue query for columns marked, so
/// the schema check can be seen sending the dialect's query.
type MarkedCatalogue(inner: IProvider) =
    let d = inner.Dialect

    let dialect =
        { new ISqlDialect with
            member _.Name = d.Name
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.StoreCast(t, array) = d.StoreCast(t, array)
            member _.TextCast = d.TextCast
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) = d.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = d.ListIsEmpty(w, column)
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = d.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue =
                let c = d.Catalogue

                { new ICatalogueDialect with
                    member _.Columns tables = "/* the dialect's columns */ " + c.Columns tables
                    member _.CurrentSchema = c.CurrentSchema
                    member _.EnumLabels names = c.EnumLabels names
                    member _.UniqueIndexes tables = c.UniqueIndexes tables
                    member _.References tables = c.References tables
                    member _.PrimaryKeys tables = c.PrimaryKeys tables
                    member _.ForeignKeys tables = c.ForeignKeys tables
                    member _.TableColumns table = c.TableColumns table }
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Exact = d.Exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)
        member _.Describe(c, ct) = inner.Describe(c, ct)

/// PostgreSQL's provider, its captured lists sent one parameter a value
/// (no array parameters); what it sends as lists of its own is PostgreSQL's.
type NoArrayParameters(inner: IProvider) =
    let d = inner.Dialect

    let dialect =
        { new ISqlDialect with
            member _.Name = "NoArraysSQL"
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.StoreCast(t, array) = d.StoreCast(t, array)
            member _.TextCast = d.TextCast
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) = d.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = d.ListIsEmpty(w, column)
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = d.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue = d.Catalogue
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Exact = d.Exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = { d.Capabilities with ArrayParameters = false } }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)
        member _.Describe(c, ct) = inner.Describe(c, ct)

/// PostgreSQL's dialect, its lists sent as JSON and read as rows by json_each:
/// only explained, never run.
type JsonLists(inner: IProvider) =
    let d = inner.Dialect

    let dialect =
        { new ISqlDialect with
            member _.Name = "JsonListsSQL"
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.StoreCast(t, array) = d.StoreCast(t, array)
            member _.TextCast = d.TextCast
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) = d.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = d.ListIsEmpty(w, column)
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) =
                w "("
                columns |> List.iteri (fun i c -> (if i > 0 then w ", "); c ())
                w ") IN (SELECT "
                w (lists |> List.mapi (fun i _ -> $"l{i}.value") |> String.concat ", ")
                lists
                |> List.iteri (fun i (list, _) ->
                    if i = 0 then w $" FROM json_each({list}) AS l0"
                    else w $" JOIN json_each({list}) AS l{i} ON l{i}.key = l0.key")
                w ")"
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue = d.Catalogue
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Exact = d.Exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    interface IProvider with
        member _.Dialect = dialect
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)
        member _.Describe(c, ct) = inner.Describe(c, ct)

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
            member _.StoreCast(t, array) = d.StoreCast(t, array)
            member _.TextCast = d.TextCast
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) = d.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = d.ListIsEmpty(w, column)
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = d.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue = d.Catalogue
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated + " /* the dialect's */"
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Exact = d.Exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    interface IProvider with
        member _.Dialect = dialect
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

/// PostgreSQL's provider with exact hooks of its own: each decimal comparison,
/// null-safe comparison, arithmetic and aggregate is written by value through
/// numeric, each order and group key as it is, and each marked `/* exact */`, and a decimal is sent with three
/// more places (12.50 as 12.50000), so what the hooks were asked can be seen
/// in what PostgreSQL is sent and still runs.
type ExactDecimalsProvider(inner: IProvider) =
    let d = inner.Dialect

    let exact =
        { new IExactDialect with
            member _.Compare(w, op, a, b) =
                w "CAST("
                a ()
                w $" AS numeric) {op} CAST("
                b ()
                w " AS numeric) /* exact */"

            member _.NullSafe(w, negated, a, b) =
                w "CAST("
                a ()
                w $" AS numeric) {d.NullSafeComparison negated} CAST("
                b ()
                w " AS numeric) /* exact */"

            member _.Arithmetic(w, op, a, b) =
                w "(CAST("
                a ()
                w $" AS numeric) {op} CAST("
                b ()
                w " AS numeric) /* exact */)"

            member _.Aggregate(w, name, a) =
                w $"{name}(CAST("
                a ()
                w " AS numeric) /* exact */)"

            member _.WindowAggregate(w, name, a, over) =
                w $"{name}(CAST("
                a ()
                w " AS numeric) /* exact */)"
                over ()

            // As it is, marked: PostgreSQL groups a column by its own value,
            // and selects it only where it is grouped as it is.
            member _.Ordered(w, a) =
                a ()
                w " /* exact */"

            member _.Value m = box (m * 1.000m) }

    let dialect =
        { new ISqlDialect with
            member _.Name = d.Name
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.StoreCast(t, array) = d.StoreCast(t, array)
            member _.TextCast = d.TextCast
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) = d.ListHas(w, element, column)
            member _.ListIsEmpty(w, column) = d.ListIsEmpty(w, column)
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = d.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue = d.Catalogue
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Exact = Some exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    interface IProvider with
        member _.Dialect = dialect
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)

/// PostgreSQL's provider, its tests of a list column marked, so a query can be
/// seen asking the dialect for them.
type MarkedListsProvider(inner: IProvider) =
    let d = inner.Dialect

    let dialect =
        { new ISqlDialect with
            member _.Name = d.Name
            member _.QuoteIdentifier s = d.QuoteIdentifier s
            member _.ParameterName i = d.ParameterName i
            member _.ColumnType t = d.ColumnType t
            member _.CatalogueTypes t = d.CatalogueTypes t
            member _.CastType t = d.CastType t
            member _.StoreCast(t, array) = d.StoreCast(t, array)
            member _.TextCast = d.TextCast
            member _.EmptyArray = d.EmptyArray
            member _.ListHas(w, element, column) =
                d.ListHas(w, element, column)
                w " /* the dialect's */"
            member _.ListIsEmpty(w, column) =
                d.ListIsEmpty(w, column)
                w " /* the dialect's */"
            member _.ListValue values = d.ListValue values
            member _.NotInList(w, operand, list, cast) = d.NotInList(w, operand, list, cast)
            member _.ListRows(w, lists, alias) = d.ListRows(w, lists, alias)
            member _.RowsIn(w, columns, lists) = d.RowsIn(w, columns, lists)
            member _.InList(w, operand, list, cast) = d.InList(w, operand, list, cast)
            member _.Catalogue = d.Catalogue
            member _.Ddl = d.Ddl
            member _.UnboundedLimit = d.UnboundedLimit
            member _.NullSafeComparison negated = d.NullSafeComparison negated
            member _.LikeEscape = d.LikeEscape
            member _.FunctionName name = d.FunctionName name
            member _.Json = d.Json
            member _.Exact = d.Exact
            member _.Begin isolation = d.Begin isolation
            member _.Capabilities = d.Capabilities }

    interface IProvider with
        member _.Dialect = dialect
        member _.Describe(c, ct) = inner.Describe(c, ct)
        member _.OpenConnection ct = inner.OpenConnection ct
        member _.Classify ex = inner.Classify ex
        member _.BeginSnapshot(c, ct) = inner.BeginSnapshot(c, ct)
        member _.BeginTransaction(c, i, ct) = inner.BeginTransaction(c, i, ct)
