-- Dayline: one row per to-do or calendar event, private to the account that owns it.
-- Run ONCE: Supabase Dashboard > SQL Editor > New query > paste all of this > Run.
-- Safe to run again. It adds one table and one function, and changes nothing that Paraplay uses.
-- If it shows an error, copy the message back to Claude.

create table if not exists public.dayline_items (
  user_id    uuid        not null default auth.uid() references auth.users(id) on delete cascade,
  id         text        not null check (char_length(id) between 1 and 64),
  kind       text        not null check (kind in ('todo', 'event')),
  data       jsonb       not null check (pg_column_size(data) < 20000),
  deleted    boolean     not null default false,
  updated_at timestamptz not null default now(),
  primary key (user_id, id)
);
create index if not exists dayline_items_changed_idx on public.dayline_items (user_id, updated_at);

-- Stamps every write with the server's clock (devices ask for "everything changed since ..."),
-- and never lets an older edit from one device replace a newer edit from another.
create or replace function public.dayline_touch() returns trigger
language plpgsql set search_path = '' as $$
begin
  if tg_op = 'INSERT' then
    if (select count(*) from public.dayline_items i where i.user_id = new.user_id) >= 5000 then
      raise exception 'This account has reached the limit of 5000 items.';
    end if;
  elsif coalesce((new.data->>'updated')::numeric, 0) < coalesce((old.data->>'updated')::numeric, 0) then
    return old;
  end if;
  new.updated_at := clock_timestamp();
  return new;
end $$;

drop trigger if exists dayline_items_touch on public.dayline_items;
create trigger dayline_items_touch before insert or update on public.dayline_items
  for each row execute function public.dayline_touch();

-- Each signed-in account can read and write its own rows and nobody else's.
alter table public.dayline_items enable row level security;
drop policy if exists dayline_items_own on public.dayline_items;
create policy dayline_items_own on public.dayline_items for all to authenticated
  using ((select auth.uid()) = user_id) with check ((select auth.uid()) = user_id);
revoke all on public.dayline_items from anon;
grant select, insert, update, delete on public.dayline_items to authenticated;

notify pgrst, 'reload schema';
