-- A card's stage is stored the way Plasm stores a union with fields: the
-- case's name in the field's own column (stage), and each case's facts in
-- columns named after it. The check keeps the columns to combinations the
-- union can express; Plasm also refuses to read any other combination.
alter table cards rename column assignee to stage_assignee;
alter table cards rename column started_at to stage_since;
alter table cards rename column resolution to stage_resolution;
alter table cards rename column drop_reason to stage_resolution_reason;
alter table cards rename column finished_at to stage_at;
alter table cards rename constraint cards_stage_facts_check to cards_stage_check;
alter index cards_assignee_idx rename to cards_stage_assignee_idx;
alter index cards_finished_idx rename to cards_stage_at_idx;
