/// The koans database: the schema, the starting data every koan sees, and
/// where the database comes from (PLASM_KOANS_PG, or a throwaway container).
module Plasm.Koans.Database

open System
open System.IO
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql

let schema =
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
  product_id bigint not null constraint order_lines_product_fkey references products(id),
  quantity int not null,
  unit_price numeric(12,2) not null constraint order_lines_price_check check (unit_price >= 0)
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
);"""

/// The starting data. Every koan runs in a sandbox, so every koan sees exactly this.
let data =
    """
insert into customers(name, email) values
  ('Ada', 'ada@example.com'), ('Bob', 'bob@example.com'), ('Cy', 'cy@example.com');
insert into products(sku, name) values
  ('WIDGET', 'Widget'), ('GADGET', 'Gadget'), ('GIZMO', 'Gizmo');
insert into orders(customer_id, status, total, cancellation_reason, placed_at) values
  (1, 'published', 30.00, null, '2026-01-01T09:00:00Z'),
  (1, 'draft', 12.50, null, '2026-01-02T09:00:00Z'),
  (2, 'published', 99.00, null, '2026-01-03T09:00:00Z'),
  (null, 'cancelled', 5.00, 'duplicate', '2026-01-04T09:00:00Z');
insert into order_lines(order_id, product_id, quantity, unit_price) values
  (1, 1, 2, 10.00), (1, 2, 1, 10.00), (2, 3, 5, 2.50), (3, 2, 9, 11.00);
insert into payments(order_id, amount, status, created_at) values
  (1, 30.00, 'succeeded', '2026-01-01T10:00:00Z'),
  (3, 99.00, 'failed', '2026-01-03T09:30:00Z'),
  (3, 99.00, 'succeeded', '2026-01-03T10:00:00Z');
insert into articles(title) values ('Intro to Plasm'), ('Deep dive'), ('Empty');
insert into tags(name) values ('fsharp'), ('sql'), ('draft'), ('ops');
insert into article_tags(article_id, tag_id) values (1, 1), (1, 2), (2, 1), (2, 3);
insert into accounts(name) values ('ada'), ('bob');
insert into profiles(account_id, bio) values (1, 'Writes compilers');
"""

/// A database for one run of the koans; disposing it removes it.
type KoansDatabase =
    { ConnectionString: string
      Description: string
      Dispose: unit -> Task }

let private exec (cs: string) (sql: string) =
    task {
        use conn = new NpgsqlConnection(cs)
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        let! _ = cmd.ExecuteNonQueryAsync()
        return ()
    }

/// Creates the koans database: on the server named by PLASM_KOANS_PG (an
/// admin connection string), or in a throwaway PostgreSQL container.
let provision () : Task<KoansDatabase> =
    task {
        let name = $"plasm_koans_{Guid.NewGuid():N}"

        let! admin, description, removeServer =
            match Environment.GetEnvironmentVariable "PLASM_KOANS_PG" with
            | null
            | "" ->
                task {
                    // Quiet: the learner's terminal (or notebook) is for the koans,
                    // so Testcontainers' console log is set aside while it starts.
                    let console = Console.Out
                    Console.SetOut TextWriter.Null

                    let container =
                        try
                            let c = PostgreSqlBuilder("postgres:18.4").WithLabel("plasm-koans", "true").Build()
                            c.StartAsync().GetAwaiter().GetResult()
                            c
                        finally
                            Console.SetOut console

                    return
                        container.GetConnectionString(),
                        "a throwaway PostgreSQL container (removed when the run ends)",
                        (fun () -> container.DisposeAsync().AsTask())
                }
            | cs -> Task.FromResult(cs, "the server named by PLASM_KOANS_PG", (fun () -> Task.CompletedTask))

        do! exec admin $"create database {name}"
        let b = NpgsqlConnectionStringBuilder(admin)
        b.Database <- name
        b.Pooling <- false
        let cs = b.ConnectionString
        do! exec cs schema
        do! exec cs data

        return
            { ConnectionString = cs
              Description = description
              Dispose =
                fun () ->
                    task {
                        try
                            do! exec admin $"drop database if exists {name} with (force)"
                        with _ ->
                            ()

                        do! removeServer ()
                    } }
    }
