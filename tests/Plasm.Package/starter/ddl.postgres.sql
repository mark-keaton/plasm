create table bookings (
  id uuid not null primary key,
  equipment uuid not null,
  who text not null,
  "from" timestamp with time zone not null,
  until timestamp with time zone not null
);
create table equipment (
  id uuid not null primary key,
  name text not null,
  condition text not null,
  condition_reason text,
  condition_since timestamp with time zone,
  version integer not null default 1,
  constraint equipment_name_key unique (name)
);
alter table bookings add constraint bookings_equipment_fkey foreign key (equipment) references equipment (id);
create index bookings_equipment_idx on bookings (equipment);
