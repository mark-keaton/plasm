/// The koans database's schema and starting data, shared by every way of
/// running the koans (the program and the browser).
module Plasm.Koans.KoansData

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
  -- A rule across two columns, so it belongs to no single field: a cancelled
  -- order needs a reason. Its violation stays a database failure.
  constraint orders_cancelled_has_reason check (status <> 'cancelled' or cancellation_reason is not null),
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
create table deliveries (
  id bigint generated always as identity primary key,
  order_id bigint not null references orders(id),
  stage text not null,
  stage_carrier text,
  stage_signed_by text
);
create table categories (
  id bigint generated always as identity primary key,
  name text not null,
  parent bigint references categories(id),
  keywords text[] not null default '{}'
);
create table stock (
  warehouse text not null,
  sku text not null,
  quantity int not null,
  version int not null default 1,
  primary key (warehouse, sku)
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
insert into deliveries(order_id, stage, stage_carrier, stage_signed_by) values
  (1, 'delivered', null, 'Ada'), (3, 'in_transit', 'Swift', null), (2, 'packing', null, null);
insert into categories(name, parent, keywords) values
  ('Tools', null, '{hardware}'), ('Saws', 1, '{cutting,hardware}'), ('Hand saws', 2, '{cutting}'),
  ('Drills', 1, '{hardware,power}'), ('Garden', null, '{}'), ('Hoses', 5, '{water}');
insert into stock(warehouse, sku, quantity) values
  ('north', 'WIDGET', 5), ('north', 'GADGET', 0), ('south', 'WIDGET', 2);
"""
