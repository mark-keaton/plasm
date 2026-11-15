-- A label is stored once; card_labels says which cards carry it.
create table labels (
  id uuid primary key,
  name text not null constraint labels_name_key unique
);

create table card_labels (
  card_id uuid not null references cards(id),
  label_id uuid not null references labels(id),
  primary key (card_id, label_id)
);
create index card_labels_label_id_idx on card_labels(label_id);

create table accounts (
  id uuid primary key,
  name text not null constraint accounts_name_key unique,
  password_hash text not null,
  created_at timestamptz not null
);

-- What someone is working on, and a board's done cards newest first.
create index cards_assignee_idx on cards(assignee) where assignee is not null;
create index cards_finished_idx on cards(board_id, finished_at desc) where stage = 'done';
