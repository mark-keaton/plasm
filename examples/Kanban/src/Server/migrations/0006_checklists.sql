-- Card.Checklist is a list of items kept as one JSON document in the card's
-- row ([<Json>] on the field). `repo.Ddl()` prints the column as
-- `checklist jsonb not null`; the cards there already get an empty list.
alter table cards add column checklist jsonb not null default '[]'::jsonb;
alter table cards alter column checklist drop default;
