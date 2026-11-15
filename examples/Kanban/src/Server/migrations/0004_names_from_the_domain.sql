-- Columns are named after the fields of the domain's records, which are now
-- the types Plasm stores: Card.Board, Card.Created, Comment.Card, Comment.At.
alter table cards rename column board_id to board;
alter table cards rename constraint cards_board_id_fkey to cards_board_fkey;
alter table cards rename column created_at to created;
alter index cards_board_id_stage_idx rename to cards_board_stage_idx;

alter table comments rename column card_id to card;
alter table comments rename constraint comments_card_id_fkey to comments_card_fkey;
alter table comments rename column written_at to at;
alter index comments_card_id_idx rename to comments_card_idx;

-- When a board or an account was created is the database's to record; no
-- record in the application has a field for it.
alter table boards alter column created_at set default now();
alter table accounts alter column created_at set default now();
