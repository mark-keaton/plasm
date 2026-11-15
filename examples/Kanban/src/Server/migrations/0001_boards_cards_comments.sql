create table boards (
  id uuid primary key,
  slug text not null constraint boards_slug_key unique,
  name text not null,
  wip_limit int not null constraint boards_wip_limit_check check (wip_limit between 1 and 20),
  created_at timestamptz not null
);

-- A card's stage is a union in the domain (Backlog | Doing of who * since |
-- Done of how * at). Here it is a tag and the facts of every stage side by
-- side; the check keeps the combinations to the ones the union can express.
create table cards (
  id uuid primary key,
  board_id uuid not null references boards(id),
  title text not null,
  stage text not null,
  assignee text,
  started_at timestamptz,
  resolution text,
  drop_reason text,
  finished_at timestamptz,
  created_at timestamptz not null,
  version int not null default 1,
  constraint cards_stage_facts_check check (
    (stage = 'backlog' and assignee is null and started_at is null
       and resolution is null and drop_reason is null and finished_at is null)
    or (stage = 'doing' and assignee is not null and started_at is not null
       and resolution is null and drop_reason is null and finished_at is null)
    or (stage = 'done' and assignee is null and started_at is null and finished_at is not null
       and ((resolution = 'shipped' and drop_reason is null)
         or (resolution = 'dropped' and drop_reason is not null)))
  )
);
create index cards_board_id_stage_idx on cards(board_id, stage);

create table comments (
  id uuid primary key,
  card_id uuid not null references cards(id),
  author text not null,
  body text not null,
  written_at timestamptz not null
);
create index comments_card_id_idx on comments(card_id);
