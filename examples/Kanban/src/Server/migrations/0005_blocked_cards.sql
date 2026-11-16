-- Card.Blocked is a value (a reason and since when) stored in the card's own
-- row: one column per part, both empty when the card is not blocked. The
-- names, and the constraint that there is no reason without a time, are the
-- ones `repo.Ddl()` prints for the model.
alter table cards add column blocked_reason text;
alter table cards add column blocked_since timestamp with time zone;

alter table cards
    add constraint cards_blocked_present
    check ((blocked_reason is null and blocked_since is null) or (blocked_reason is not null and blocked_since is not null));
