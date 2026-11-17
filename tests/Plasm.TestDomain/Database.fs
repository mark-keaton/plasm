/// Real PostgreSQL for tests: one container per test process, one freshly
/// cloned database per test. Set PLASM_TEST_PG to an admin connection string to
/// use an existing server instead of Testcontainers.
module Plasm.TestDomain.Database

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Npgsql
open Plasm
open Plasm.Postgres
open Plasm.TestDomain.Domain

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

let private template = "plasm_template"

type private Server =
    { Admin: string
      Container: IAsyncDisposable option }

let private server: Lazy<Task<Server>> =
    lazy
        (task {
            let! admin, container =
                match Environment.GetEnvironmentVariable "PLASM_TEST_PG" with
                | null
                | "" ->
                    task {
                        let c =
                            Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:18.4")
                                .WithCommand("-c", "max_connections=300")
                                .Build()

                        do! c.StartAsync()
                        return c.GetConnectionString(), Some(c :> IAsyncDisposable)
                    }
                | cs -> Task.FromResult(cs, None)

            use conn = new NpgsqlConnection(admin)
            do! conn.OpenAsync()

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
            do! t.CloseAsync()
            NpgsqlConnection.ClearAllPools()
            return { Admin = admin; Container = container }
        })

let mutable private counter = 0

/// Records executed commands.
type Recorder() =
    let events = ConcurrentQueue<CommandEvent>()
    member _.Record(e: CommandEvent) = events.Enqueue e
    member _.Events = events.ToArray() |> List.ofArray
    member this.Count = this.Events.Length
    member this.Selects = this.Events |> List.filter (fun e -> e.Sql.StartsWith "SELECT")
    member this.Writes = this.Events |> List.filter (fun e -> not (e.Sql.StartsWith "SELECT"))
    member _.Clear() = events.Clear()

/// An isolated database for one test.
type TestDb =
    { ConnectionString: string
      Recorder: Recorder
      Provider: PostgresProvider
      Repo: Repo
      Model: Model }

    /// Runs raw SQL (setup and assertions bypass the library).
    member db.Exec(sql: string, ?args: (string * obj) list) =
        task {
            // Unpooled: Npgsql's global pool would keep a connection to every
            // scenario's database open until the run ends; and not the
            // scenario's data source, whose type catalogue must be read after
            // setup has created its types.
            use conn = new NpgsqlConnection(db.ConnectionString + ";Pooling=false")
            do! conn.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql

            for name, v in defaultArg args [] do
                cmd.Parameters.AddWithValue(name, v) |> ignore

            let! n = cmd.ExecuteNonQueryAsync()
            return n
        }

    member db.Scalar<'T>(sql: string, ?args: (string * obj) list) =
        task {
            // Unpooled: Npgsql's global pool would keep a connection to every
            // scenario's database open until the run ends; and not the
            // scenario's data source, whose type catalogue must be read after
            // setup has created its types.
            use conn = new NpgsqlConnection(db.ConnectionString + ";Pooling=false")
            do! conn.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql

            for name, v in defaultArg args [] do
                cmd.Parameters.AddWithValue(name, v) |> ignore

            let! v = cmd.ExecuteScalarAsync()
            return unbox<'T> v
        }

    /// A repo over the same database with different options or model.
    member db.RepoWith(?model: Model, ?options: PostgresOptions, ?includeParameterValues: bool) =
        let provider =
            match options with
            | Some o -> Postgres.connectWith o db.ConnectionString
            | None -> db.Provider

        Repo(
            defaultArg model db.Model,
            provider,
            { RepoOptions.Default with
                OnCommand = Some db.Recorder.Record
                IncludeParameterValues = defaultArg includeParameterValues false }
        )

let fresh (model: Model) : Task<TestDb> =
    task {
        let! s = server.Force()
        let n = Interlocked.Increment(&counter)
        let name = $"plasm_t{Environment.ProcessId}_{n}"
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

        return
            { ConnectionString = cs
              Recorder = recorder
              Provider = provider
              Repo =
                Repo(
                    model,
                    provider,
                    { RepoOptions.Default with
                        OnCommand = Some recorder.Record }
                )
              Model = model }
    }

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
