/// Real databases for tests, one fresh database per test. PLASM_TEST_PROVIDER
/// says which: postgres (or unset) runs on PostgreSQL, one container per test
/// process and a database cloned for each test (set PLASM_TEST_PG to an admin
/// connection string to use an existing server instead of Testcontainers);
/// sqlite runs on SQLite, a temp file copied for each test from one built
/// from schemaSqlite, and never opens PostgreSQL.
module Plasm.TestDomain.Database

open System
open System.Collections.Concurrent
open System.Data.Common
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Npgsql
open Plasm
open Plasm.Postgres
open Plasm.Sqlite
open Plasm.TestDomain.Domain

/// Says which database the run ran on, as read from a connection: on standard
/// error, and appended to the file PLASM_TEST_SUMMARY names when set.
let summarize (provider: string) (version: string) =
    let line = $"provider: {provider} {version}"
    eprintfn "%s" line

    match Environment.GetEnvironmentVariable "PLASM_TEST_SUMMARY" with
    | null
    | "" -> ()
    | file -> File.AppendAllText(file, line + Environment.NewLine)

/// The database a test run runs on.
type ProviderKind =
    | PostgreSQL
    | SQLite

/// PLASM_TEST_PROVIDER's database; any other value fails the run.
let providerKind =
    match Environment.GetEnvironmentVariable "PLASM_TEST_PROVIDER" with
    | null
    | ""
    | "postgres" -> PostgreSQL
    | "sqlite" -> SQLite
    | other -> failwith $"PLASM_TEST_PROVIDER is \"{other}\": it must be postgres or sqlite"

let schemaSql =
    """
create table accounts (
  id bigint generated always as identity primary key,
  name text not null
);
create table profiles (
  id bigint generated always as identity primary key,
  account_id bigint not null references accounts(id),
  bio text not null
);
create index on profiles(account_id);
create table customers (
  id bigint generated always as identity primary key,
  name text not null,
  email text not null constraint customers_email_key unique
);
create table products (
  id bigint generated always as identity primary key,
  sku text not null constraint products_sku_key unique,
  name text not null
);
create table orders (
  id bigint generated always as identity primary key,
  customer_id bigint references customers(id),
  status text not null,
  total numeric(12,2) not null constraint orders_total_check check (total >= 0),
  cancellation_reason text,
  placed_at timestamptz not null,
  version int not null default 1
);
create table order_lines (
  id bigint generated always as identity primary key,
  order_id bigint not null references orders(id),
  product_id bigint not null constraint order_lines_product_id_fkey references products(id),
  quantity int not null,
  unit_price numeric(12,2) not null constraint order_lines_unit_price_check check (unit_price >= 0)
);
create index on order_lines(order_id);
create table payments (
  id bigint generated always as identity primary key,
  order_id bigint not null references orders(id),
  amount numeric(12,2) not null,
  status text not null,
  created_at timestamptz not null
);
create index on payments(order_id);
create table articles (
  id bigint generated always as identity primary key,
  title text not null
);
create table tags (
  id bigint generated always as identity primary key,
  name text not null
);
create table article_tags (
  article_id bigint not null references articles(id),
  tag_id bigint not null references tags(id),
  primary key (article_id, tag_id)
);
create table outbox (
  id bigint generated always as identity primary key,
  topic text not null,
  payload text not null
);
"""

/// schemaSql for SQLite, stated on its own: the tables the library's Ddl
/// prints for Schemas.model, which the SQLite run checks by reading both
/// catalogues back (sqliteTemplate), and the constraints the scenarios
/// violate. A decimal is TEXT, so its CHECK compares it as a number: text
/// compared with 0 is always the greater.
/// A time column held as Ddl holds it (utc-only.feature): the dialect's own
/// check, which the schema check compares (column-checks.feature).
let private timeCheck (table: string) (column: string) =
    let ddl = (Sqlite.connect "Data Source=:memory:" :> IProvider).Dialect.Ddl

    match ddl.ColumnCheck(column, ColumnHolds.Scalar typeof<DateTime>) with
    | Some check -> $"constraint {table}_{column}_check {check},"
    | None -> ""

let schemaSqlite =
    """
create table accounts (
  id INTEGER not null primary key,
  name TEXT not null
);
create table profiles (
  id INTEGER not null primary key,
  account_id INTEGER not null,
  bio TEXT not null,
  constraint profiles_account_id_fkey foreign key (account_id) references accounts (id)
);
create index profiles_account_id_idx on profiles (account_id);
create table customers (
  id INTEGER not null primary key,
  name TEXT not null,
  email TEXT not null,
  constraint customers_email_key unique (email)
);
create table products (
  id INTEGER not null primary key,
  sku TEXT not null,
  name TEXT not null,
  constraint products_sku_key unique (sku)
);
create table orders (
  id INTEGER not null primary key,
  customer_id INTEGER,
  status TEXT not null,
  total TEXT not null constraint orders_total_check check (CAST(total AS REAL) >= 0),
  cancellation_reason TEXT,
  placed_at TEXT not null,
  version INTEGER not null default 1,
  {placed_at check}
  constraint orders_customer_id_fkey foreign key (customer_id) references customers (id)
);
create index orders_customer_id_idx on orders (customer_id);
create table order_lines (
  id INTEGER not null primary key,
  order_id INTEGER not null,
  product_id INTEGER not null,
  quantity INTEGER not null,
  unit_price TEXT not null constraint order_lines_unit_price_check check (CAST(unit_price AS REAL) >= 0),
  constraint order_lines_order_id_fkey foreign key (order_id) references orders (id),
  constraint order_lines_product_id_fkey foreign key (product_id) references products (id)
);
create index order_lines_order_id_idx on order_lines (order_id);
create index order_lines_product_id_idx on order_lines (product_id);
create table payments (
  id INTEGER not null primary key,
  order_id INTEGER not null,
  amount TEXT not null,
  status TEXT not null,
  created_at TEXT not null,
  {created_at check}
  constraint payments_order_id_fkey foreign key (order_id) references orders (id)
);
create index payments_order_id_idx on payments (order_id);
create table articles (
  id INTEGER not null primary key,
  title TEXT not null
);
create table tags (
  id INTEGER not null primary key,
  name TEXT not null
);
create table article_tags (
  article_id INTEGER not null,
  tag_id INTEGER not null,
  primary key (article_id, tag_id),
  constraint article_tags_article_id_fkey foreign key (article_id) references articles (id),
  constraint article_tags_tag_id_fkey foreign key (tag_id) references tags (id)
);
create index article_tags_tag_id_idx on article_tags (tag_id);
create table outbox (
  id INTEGER not null primary key,
  topic TEXT not null,
  payload TEXT not null
);
"""
    |> fun s -> s.Replace("{placed_at check}", timeCheck "orders" "placed_at").Replace("{created_at check}", timeCheck "payments" "created_at")


let private template = "plasm_template"

/// This machine, short: a test database's name says which machine's run made
/// it, so a run drops only the orphans of its own machine.
let private machine =
    Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(Environment.MachineName.ToLowerInvariant()))).[..7]

/// A test database's name: plasm_t<machine>_<process>_<n>.
let private ours = Text.RegularExpressions.Regex @"^plasm_t([0-9a-f]{8})_(\d+)_\d+$"

/// The name a run before machines were named gave one: plasm_t<process>_<n>.
let private older = Text.RegularExpressions.Regex @"^plasm_t(\d+)_\d+$"

/// Whether the run that made a database made at `created` is still running
/// as process `pid`: a process of that id, started before the database. One
/// started after has the id again, and the run that made it is gone.
let private running (pid: int) (created: DateTimeOffset option) =
    try
        use p = Diagnostics.Process.GetProcessById pid

        not p.HasExited
        && (match created with
            // A second of grace, for the server's clock against this machine's.
            | Some at -> DateTimeOffset(p.StartTime) <= at.AddSeconds 1.0
            | None -> true)
    with
    | :? ArgumentException -> false
    | :? InvalidOperationException -> false

type private Server =
    { Admin: string
      Container: IAsyncDisposable option }

let private server: Lazy<Task<Server>> =
    lazy
        (task {
            // The SQLite run proves it ran on SQLite: nothing in it opens
            // PostgreSQL.
            if providerKind = SQLite then
                failwith "the SQLite run (PLASM_TEST_PROVIDER=sqlite) asked for PostgreSQL"

            let! admin, container =
                match Environment.GetEnvironmentVariable "PLASM_TEST_PG" with
                | null
                | "" ->
                    task {
                        let c =
                            Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:18.4")
                                // Prepared transactions: elegance.feature holds a
                                // transaction committed but not yet visible with them.
                                .WithCommand("-c", "max_connections=300", "-c", "max_prepared_transactions=10")
                                .Build()

                        do! c.StartAsync()
                        return c.GetConnectionString(), Some(c :> IAsyncDisposable)
                    }
                | cs -> Task.FromResult(cs, None)

            use conn = new NpgsqlConnection(admin)
            do! conn.OpenAsync()

            // On a server runs share (PLASM_TEST_PG), the databases of this
            // machine's runs whose process is gone: a run killed, or one that
            // never dropped a database. Alive means kept, so a reused process
            // id is never a wrong drop. Names from before the machine was
            // named have no machine to tell: their process gone is enough.
            if container.IsNone then
                let names = Collections.Generic.List<string>()

                do!
                    task {
                        use read = conn.CreateCommand()
                        read.CommandText <- "select datname from pg_database where datname like 'plasm\\_t%'"
                        use! rows = read.ExecuteReaderAsync()

                        while rows.Read() do
                            names.Add(rows.GetString 0)
                    }

                // When a database was made: its PG_VERSION file's time, which
                // only a superuser may read; without it, a process of the id
                // alive keeps the database.
                let created (name: string) =
                    task {
                        try
                            use read = conn.CreateCommand()
                            read.CommandText <- "select (pg_stat_file('base/' || oid || '/PG_VERSION')).modification from pg_database where datname = @n"
                            read.Parameters.AddWithValue("n", name) |> ignore
                            let! at = read.ExecuteScalarAsync()

                            return
                                match at with
                                | :? DateTime as t -> Some(DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)))
                                | :? DateTimeOffset as t -> Some t
                                | _ -> None
                        with :? PostgresException ->
                            return None
                    }

                for name in names do
                    let m = ours.Match name
                    let o = older.Match name
                    let! at = if m.Success || o.Success then created name else Task.FromResult None

                    let orphan =
                        (m.Success && m.Groups[1].Value = machine && not (running (int m.Groups[2].Value) at))
                        || (o.Success && not (running (int o.Groups[1].Value) at))

                    if orphan then
                        use drop = conn.CreateCommand()
                        drop.CommandText <- $"drop database if exists {name} with (force)"
                        let! _ = drop.ExecuteNonQueryAsync()
                        ()

            let exec (sql: string) =
                task {
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <- sql
                    let! _ = cmd.ExecuteNonQueryAsync()
                    return ()
                }

            do! exec $"drop database if exists {template} with (force)"
            do! exec $"create database {template}"

            let b = NpgsqlConnectionStringBuilder(admin)
            b.Database <- template
            use t = new NpgsqlConnection(b.ConnectionString)
            do! t.OpenAsync()
            use cmd = t.CreateCommand()
            cmd.CommandText <- schemaSql
            let! _ = cmd.ExecuteNonQueryAsync()
            summarize "PostgreSQL" t.ServerVersion
            do! t.CloseAsync()
            NpgsqlConnection.ClearAllPools()
            return { Admin = admin; Container = container }
        })

let mutable private counter = 0

/// Records executed commands.
/// What a repo reported to OnCommand. Events, and everything counted from it,
/// are the statements, chosen by their Kind; All has the BEGIN, COMMIT and
/// ROLLBACK around them as well (transaction-events.feature).
type Recorder() =
    let events = ConcurrentQueue<CommandEvent>()
    member _.Record(e: CommandEvent) = events.Enqueue e
    member _.All = events.ToArray() |> List.ofArray
    member this.Events = this.All |> List.filter (fun e -> e.Kind = CommandKind.Statement)
    member this.Count = this.Events.Length
    member this.Selects = this.Events |> List.filter (fun e -> e.Sql.StartsWith "SELECT")
    member this.Writes = this.Events |> List.filter (fun e -> not (e.Sql.StartsWith "SELECT"))
    member _.Clear() = events.Clear()

/// The directory the SQLite run's databases are files in, one per test
/// process; every file in it is removed when its test is done, and the
/// directory when the process ends.
let sqliteDirectory = Path.Combine(Path.GetTempPath(), $"plasm-sqlite-{Environment.ProcessId}")

/// A SQLite database's files: itself, its write-ahead log and its index.
let sqliteFiles (file: string) = [ file; file + "-wal"; file + "-shm"; file + "-journal" ]

let private removeSqlite (file: string) =
    SqliteConnection.ClearAllPools()

    for f in sqliteFiles file do
        if File.Exists f then File.Delete f

/// What SQLite's catalogue says of a database's tables: for each, its columns
/// (name, type, not null, default, place in the key), its indexes (unique,
/// how made, columns) and its foreign keys (target, column, target column).
/// Names of indexes are left out: SQLite names a constraint's index itself.
let sqliteCatalogue (connectionString: string) =
    use c = new SqliteConnection(connectionString)
    c.Open()

    let rows (sql: string) =
        use cmd = c.CreateCommand()
        cmd.CommandText <- sql
        use r = cmd.ExecuteReader()

        [ while r.Read() do
              yield [ for i in 0 .. r.FieldCount - 1 -> if r.IsDBNull i then "NULL" else string (r.GetValue i) ] ]

    let tables = rows "select name from sqlite_schema where type = 'table' and name not like 'sqlite_%' order by name" |> List.map List.head

    [ for t in tables ->
          let columns = rows $"select name, upper(type), \"notnull\", dflt_value, pk from pragma_table_xinfo('{t}') order by cid"

          let indexes =
              rows $"select name, \"unique\", origin from pragma_index_list('{t}')"
              |> List.map (fun i ->
                  let cols = rows $"select name from pragma_index_info('{i.[0]}') order by seqno" |> List.map List.head
                  $"""unique {i.[1]} {i.[2]} ({String.concat ", " cols})""")
              |> List.sort

          let keys = rows $"select \"table\", \"from\", \"to\" from pragma_foreign_key_list('{t}')" |> List.map (String.concat " ") |> List.sort
          t, (columns |> List.map (String.concat " "), indexes, keys) ]
    |> Map.ofList

/// The SQLite template: schemaSqlite in a file every test's database is a
/// copy of, made once, after checking it against what the library's Ddl
/// prints for Schemas.model, catalogue against catalogue. A table of
/// schemaSqlite the model does not map (outbox) is not compared.
let private sqliteTemplate: Lazy<Task<string>> =
    lazy
        (task {
            // A run killed before it ended left its directory: removed now,
            // when no process of its number runs.
            for stale in Directory.GetDirectories(Path.GetTempPath(), "plasm-sqlite-*") do
                match Int32.TryParse(Path.GetFileName(stale).Substring("plasm-sqlite-".Length)) with
                | true, pid when pid <> Environment.ProcessId ->
                    let alive =
                        try
                            not (Diagnostics.Process.GetProcessById(pid).HasExited)
                        with _ -> false

                    if not alive then
                        try
                            Directory.Delete(stale, true)
                        with _ -> ()
                | _ -> ()

            Directory.CreateDirectory sqliteDirectory |> ignore
            AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
                try
                    SqliteConnection.ClearAllPools()
                    Directory.Delete(sqliteDirectory, true)
                with _ -> ())

            let make (name: string) (sql: string) =
                let file = Path.Combine(sqliteDirectory, name)
                removeSqlite file
                use c = new SqliteConnection($"Data Source={file}")
                c.Open()
                use cmd = c.CreateCommand()
                cmd.CommandText <- sql
                cmd.ExecuteNonQuery() |> ignore
                file

            // In WAL mode, as the provider makes a file it creates, so a
            // reader's snapshot does not keep a writer out; each test's copy
            // keeps it.
            let template = make "template.db" ("PRAGMA journal_mode = WAL;\n" + schemaSqlite)

            let version =
                use c = new SqliteConnection($"Data Source={template}")
                c.Open()
                c.ServerVersion

            summarize "SQLite" version

            let ddlProvider = Sqlite.connect "Data Source=:memory:"
            let! repo = Repo.Start(Schemas.model, ddlProvider, typeof<Order>.Assembly, { RepoOptions.Default with Queries = StartupCheck.Ignore; Schema = StartupCheck.Ignore })
            let printed = make "ddl.db" (repo.Ddl())
            let stated = sqliteCatalogue $"Data Source={template}"
            let made = sqliteCatalogue $"Data Source={printed}"
            removeSqlite printed

            let differences =
                [ for KeyValue(t, m) in made do
                      match stated.TryFind t with
                      | None -> yield $"{t}: in the Ddl, not in schemaSqlite"
                      | Some st when st <> m -> yield $"{t}: schemaSqlite has %A{st}, the Ddl %A{m}"
                      | Some _ -> () ]

            if not differences.IsEmpty then
                failwith $"""schemaSqlite is not what the Ddl makes: {String.concat "; " differences}"""

            return template
        })

/// An isolated database for one test. Disposing it closes its connections
/// and drops it (PostgreSQL) or removes its files (SQLite), so a run leaves no
/// database or connection behind.
type TestDb =
    { Name: string
      Admin: string
      ConnectionString: string
      Kind: ProviderKind
      Recorder: Recorder
      Provider: IProvider
      Repo: Repo
      Model: Model
      Drop: unit -> Task }

    /// A connection for raw SQL, unpooled and bypassing the library: on
    /// SQLite with foreign keys enforced, as the provider's are.
    member private db.Raw() : Task<DbConnection> =
        task {
            match db.Kind with
            | PostgreSQL ->
                // Unpooled: Npgsql's global pool would keep a connection to every
                // scenario's database open until the run ends; and not the
                // scenario's data source, whose type catalogue must be read after
                // setup has created its types.
                let conn = new NpgsqlConnection(db.ConnectionString + ";Pooling=false")
                do! conn.OpenAsync()
                return conn :> DbConnection
            | SQLite ->
                let conn = new SqliteConnection(db.ConnectionString + ";Pooling=False")
                do! conn.OpenAsync()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "PRAGMA foreign_keys = ON"
                cmd.ExecuteNonQuery() |> ignore
                return conn :> DbConnection
        }

    static member private Microseconds(ticks: int64) =
        let epoch = DateTime(2000, 1, 1).Ticks
        epoch + (ticks - epoch) / 10L * 10L

    member private db.Command(conn: DbConnection, sql: string, args: (string * obj) list) =
        let cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        for name, v in args do
            let p = cmd.CreateParameter()
            p.ParameterName <- name

            p.Value <-
                match db.Kind, v with
                // As the provider stores a decimal: its text, its scale kept.
                | SQLite, (:? decimal as d) -> box (d.ToString(CultureInfo.InvariantCulture))
                // As the provider stores a time: to the microsecond, as
                // PostgreSQL keeps it (the ticks since 2000 divided by ten,
                // towards zero), where Microsoft.Data.Sqlite would bind seven
                // digits of a second (utc-only.feature).
                | SQLite, (:? DateTime as d) -> box (DateTime(TestDb.Microseconds d.Ticks, d.Kind))
                | SQLite, (:? DateTimeOffset as d) -> box (DateTimeOffset(TestDb.Microseconds d.Ticks, d.Offset))
                | _, null -> box DBNull.Value
                | _ -> v

            cmd.Parameters.Add p |> ignore

        cmd

    /// Runs raw SQL (setup and assertions bypass the library).
    member db.Exec(sql: string, ?args: (string * obj) list) =
        task {
            use! conn = db.Raw()
            use cmd = db.Command(conn, sql, defaultArg args [])
            let! n = cmd.ExecuteNonQueryAsync()
            return n
        }

    member db.Scalar<'T>(sql: string, ?args: (string * obj) list) =
        task {
            use! conn = db.Raw()
            use cmd = db.Command(conn, sql, defaultArg args [])
            let! v = cmd.ExecuteScalarAsync()

            return
                match db.Kind, v with
                | PostgreSQL, _ -> unbox<'T> v
                | SQLite, (:? 'T as t) -> t
                // SQLite's integers are 64-bit, its decimals text.
                | SQLite, (:? string as text) when typeof<'T> = typeof<decimal> -> unbox<'T> (box (Decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)))
                | SQLite, v when not (isNull v) && not (v :? DBNull) -> Convert.ChangeType(v, typeof<'T>, CultureInfo.InvariantCulture) :?> 'T
                | SQLite, v -> unbox<'T> v
        }

    /// The first column of each row of raw SQL, as text.
    member db.Texts(sql: string) =
        task {
            use! conn = db.Raw()
            use cmd = db.Command(conn, sql, [])
            use! reader = cmd.ExecuteReaderAsync()
            let found = ResizeArray<string>()

            while reader.Read() do
                found.Add(string (reader.GetValue 0))

            return List.ofSeq found
        }

    /// The database without a table: PostgreSQL's public schema made again;
    /// every table of SQLite's file dropped, foreign keys off while they are.
    member db.Empty() =
        task {
            match db.Kind with
            | PostgreSQL ->
                let! _ = db.Exec "drop schema public cascade; create schema public;"
                ()
            | SQLite ->
                let! tables = db.Texts "select name from sqlite_schema where type = 'table' and name not like 'sqlite_%'"
                let drops = tables |> List.map (fun t -> $"drop table if exists \"{t}\";") |> String.concat " "
                let! _ = db.Exec $"PRAGMA foreign_keys = OFF; {drops} PRAGMA foreign_keys = ON;"
                ()
        }

    /// A scenario's own tables: `postgres`'s hand-written DDL on PostgreSQL; on
    /// SQLite, the tables Ddl makes for `model`, as Plasm.Sqlite declares them,
    /// and the rows the script inserts.
    member db.MakeTables(postgres: string, model: Model) =
        task {
            let! _ =
                match db.Kind with
                | PostgreSQL -> db.Exec postgres
                | SQLite ->
                    // Made again where the template has them, as the
                    // PostgreSQL scripts drop theirs first.
                    let ddl = Repo(model, db.Provider).Ddl()

                    let drops =
                        [ for m in Text.RegularExpressions.Regex.Matches(ddl, "create table \"?(\\w+)\"?", Text.RegularExpressions.RegexOptions.IgnoreCase) ->
                              $"drop table if exists {m.Groups.[1].Value};" ]
                        |> String.concat " "

                    // The rows the script writes, in SQL both read.
                    let rows =
                        [ for m in Text.RegularExpressions.Regex.Matches(postgres, "insert into [^;]*;", Text.RegularExpressions.RegexOptions.IgnoreCase) -> m.Value ]
                        |> String.concat " "

                    db.Exec($"PRAGMA foreign_keys = OFF; {drops} {ddl}; PRAGMA foreign_keys = ON; {rows}")

            ()
        }

    /// SQLite's table made again from its own text, `edit`ed (SQLite alters
    /// no constraint), its rows and indexes kept, foreign keys off while it is.
    member db.RebuildTable(table: string, edit: string -> string) =
        task {
            let! text = db.Scalar<string>("select sql from sqlite_schema where type = 'table' and name = @t", [ "t", box table ])
            let edited = edit text

            if edited = text then
                failwith $"the edit changed nothing in {text}"

            let! indexes =
                db.Scalar<string>(
                    "select coalesce(group_concat(sql || '; ', ''), '') from sqlite_schema where type = 'index' and tbl_name = @t and sql is not null",
                    [ "t", box table ]
                )

            let made = Text.RegularExpressions.Regex.Replace(edited, $"^create table \"?{table}\"?", $"create table {table}_rebuilt", Text.RegularExpressions.RegexOptions.IgnoreCase)

            let! _ =
                db.Exec(
                    $"PRAGMA foreign_keys = OFF; {made}; insert into {table}_rebuilt select * from {table}; drop table {table}; "
                    + $"alter table {table}_rebuilt rename to {table}; {indexes}PRAGMA foreign_keys = ON;"
                )

            ()
        }

    /// The table without its constraint `name`: ALTER TABLE on PostgreSQL; on
    /// SQLite, which drops no unique constraint or foreign key, the table made
    /// again without the constraint's line (RebuildTable).
    member db.DropConstraint(table: string, name: string) =
        match db.Kind with
        | PostgreSQL ->
            task {
                let! _ = db.Exec $"alter table {table} drop constraint if exists {name}"
                ()
            }
        // Its line taken out with the comma before it, the one after it kept;
        // a table without it is left as it is, as IF EXISTS leaves it.
        | SQLite ->
            task {
                let! text = db.Scalar<string>("select sql from sqlite_schema where type = 'table' and name = @t", [ "t", box table ])
                let line = $"(?m),\\s*constraint {name}\\b.*?(?=,?\\s*$)"

                if Text.RegularExpressions.Regex.IsMatch(text, line, Text.RegularExpressions.RegexOptions.IgnoreCase) then
                    do! db.RebuildTable(table, fun t -> Text.RegularExpressions.Regex.Replace(t, line, "", Text.RegularExpressions.RegexOptions.IgnoreCase))
            }

    /// A repo over the same database with different options or model.
    member db.RepoWith(?model: Model, ?options: PostgresOptions, ?includeParameterValues: bool) =
        let provider =
            match options, db.Kind with
            | Some o, PostgreSQL -> Postgres.connectWith o db.ConnectionString :> IProvider
            // The keys a command sends are the one option both providers have.
            | Some o, SQLite -> Sqlite.connectWith { SqliteOptions.Default with MaxKeysPerCommand = o.MaxKeysPerCommand } db.ConnectionString :> IProvider
            | None, _ -> db.Provider

        Repo(
            defaultArg model db.Model,
            provider,
            { RepoOptions.Default with
                OnCommand = Some db.Recorder.Record
                IncludeParameterValues = defaultArg includeParameterValues false }
        )

    /// Another session holding `table`'s rows against writers until the
    /// returned release: PostgreSQL's row locks (FOR UPDATE), SQLite's write
    /// lock (BEGIN IMMEDIATE), which a writer waits for in its busy timeout.
    member db.HoldWrites(table: string) : Task<unit -> Task> =
        task {
            let! conn = db.Raw()
            let! tx = conn.BeginTransactionAsync()
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx

            cmd.CommandText <-
                match db.Kind with
                | PostgreSQL -> $"select * from {table} for update"
                // BeginTransaction began a deferred transaction: a write takes the lock.
                | SQLite -> $"update {table} set id = id where 0"

            let! _ = cmd.ExecuteNonQueryAsync()

            return
                fun () ->
                    task {
                        do! tx.RollbackAsync()
                        do! conn.DisposeAsync()
                    }
                    :> Task
        }

    /// How many tables of this name the database has.
    member db.TablesNamed(name: string) =
        match db.Kind with
        | PostgreSQL -> db.Scalar<int64>("select count(*) from information_schema.tables where table_name = @n", [ "n", box name ])
        | SQLite -> db.Scalar<int64>("select count(*) from sqlite_schema where type = 'table' and name = @n", [ "n", box name ])

    interface IAsyncDisposable with
        member db.DisposeAsync() = db.Drop() |> ValueTask

let private freshPostgres (model: Model) : Task<TestDb> =
    task {
        let! s = server.Force()
        let n = Interlocked.Increment(&counter)
        let name = $"plasm_t{machine}_{Environment.ProcessId}_{n}"
        use conn = new NpgsqlConnection(s.Admin)
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"create database {name} template {template}"
        let! _ = cmd.ExecuteNonQueryAsync()
        let b = NpgsqlConnectionStringBuilder(s.Admin)
        b.Database <- name
        b.MaxPoolSize <- 10
        let cs = b.ConnectionString
        let recorder = Recorder()
        let provider = Postgres.connect cs

        let drop () =
            task {
                // A test-only plant (no-leftover-databases.sh --prove): the test
                // waits here, never dropping its database, so a run killed
                // then leaves it as a run that dies mid-test does.
                if Environment.GetEnvironmentVariable "PLASM_TEST_HOLD_DROP" = "1" then
                    do! Task.Delay Timeout.Infinite

                do! provider.DataSource.DisposeAsync()
                // A repo made by RepoWith has a data source of its own; FORCE
                // ends whatever connection is still open to the database.
                use conn = new NpgsqlConnection(s.Admin)
                do! conn.OpenAsync()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- $"drop database if exists {name} with (force)"
                let! _ = cmd.ExecuteNonQueryAsync()
                return ()
            }
            :> Task

        return
            { Name = name
              Admin = s.Admin
              ConnectionString = cs
              Kind = PostgreSQL
              Recorder = recorder
              Provider = provider
              Repo = Repo(model, provider, { RepoOptions.Default with OnCommand = Some recorder.Record })
              Model = model
              Drop = drop }
    }

let private freshSqlite (model: Model) : Task<TestDb> =
    task {
        let! template = sqliteTemplate.Force()
        let n = Interlocked.Increment(&counter)
        let name = $"t{n}.db"
        let file = Path.Combine(sqliteDirectory, name)
        File.Copy(template, file, true)
        let cs = $"Data Source={file}"
        let recorder = Recorder()
        let provider = Sqlite.connect cs

        return
            { Name = name
              Admin = ""
              ConnectionString = cs
              Kind = SQLite
              Recorder = recorder
              Provider = provider
              Repo = Repo(model, provider, { RepoOptions.Default with OnCommand = Some recorder.Record })
              Model = model
              Drop = fun () -> Task.Run(fun () -> removeSqlite file) }
    }

/// A fresh database for one test, on the run's provider.
let fresh (model: Model) : Task<TestDb> =
    match providerKind with
    | PostgreSQL -> freshPostgres model
    | SQLite -> freshSqlite model

let freshDefault () = fresh Schemas.model

// -- Seeding with raw SQL (independent of the library's write path) ----------

module Seed =
    let customer (db: TestDb) name email =
        task {
            let! id =
                db.Scalar<int64>(
                    "insert into customers(name, email) values (@n, @e) returning id",
                    [ "n", box name; "e", box email ]
                )

            return CustomerId id
        }

    let product (db: TestDb) sku =
        task {
            let! id =
                db.Scalar<int64>("insert into products(sku, name) values (@s, @s) returning id", [ "s", box sku ])

            return ProductId id
        }

    let order (db: TestDb) (customer: CustomerId option) (status: string) (total: decimal) (placedAt: DateTime) =
        task {
            let cid =
                match customer with
                | Some(CustomerId c) -> box c
                | None -> box DBNull.Value

            let! id =
                db.Scalar<int64>(
                    "insert into orders(customer_id, status, total, placed_at) values (@c, @s, @t, @p) returning id",
                    [ "c", cid; "s", box status; "t", box total; "p", box placedAt ]
                )

            return OrderId id
        }

    let line (db: TestDb) (OrderId o) (ProductId p) (quantity: int) (price: decimal) =
        task {
            let! id =
                db.Scalar<int64>(
                    "insert into order_lines(order_id, product_id, quantity, unit_price) values (@o, @p, @q, @u) returning id",
                    [ "o", box o; "p", box p; "q", box quantity; "u", box price ]
                )

            return OrderLineId id
        }

    let payment (db: TestDb) (OrderId o) (amount: decimal) (status: string) (createdAt: DateTime) =
        task {
            let! id =
                db.Scalar<int64>(
                    "insert into payments(order_id, amount, status, created_at) values (@o, @a, @s, @c) returning id",
                    [ "o", box o; "a", box amount; "s", box status; "c", box createdAt ]
                )

            return PaymentId id
        }

    let at (minutes: int) =
        DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(float minutes)
