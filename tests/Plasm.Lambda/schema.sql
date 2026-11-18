-- The function's tables, and the rows every run starts from.
create table customers (id bigint primary key, name text not null);
create table tickets (
  id bigint primary key,
  customer bigint not null references customers(id),
  title text not null,
  stage text not null,
  stage_assignee text,
  stage_since timestamptz,
  stage_at timestamptz,
  version int not null
);
create table notes (id bigint primary key, ticket bigint not null references tickets(id), text text not null);
insert into customers values (1, 'Ada');
insert into tickets values (1, 1, 'Ship it', 'todo', null, null, null, 1);
insert into notes values (1, 1, 'call back'), (2, 1, 'check logs');
