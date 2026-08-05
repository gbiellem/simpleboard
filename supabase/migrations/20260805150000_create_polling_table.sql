-- Polling heartbeat table: one row inserted per hourly ping (see the Azure
-- Function under azure-functions/). Keeps the project's API active so
-- Supabase's free-tier auto-pause never kicks in. Rows are wiped daily by a
-- pg_cron job (see next migration) -- only the act of polling matters, not
-- the history.
create table public.polling (
  id bigint generated always as identity primary key,
  polled_at timestamptz not null default now()
);

comment on table public.polling is
  'Heartbeat rows written hourly by an external Azure Function poller (keep-alive against Supabase free-tier auto-pause). Cleared daily by pg_cron.';

alter table public.polling enable row level security;

-- Public read+insert, matching the Azure Function's use of the publishable
-- key for both operations (no service_role secret in the Function's config).
-- No update/delete for anon/authenticated -- rows are only ever added or
-- wiped wholesale by the daily cron job.
revoke update, delete on public.polling from anon, authenticated;
grant select, insert on public.polling to anon, authenticated;

create policy polling_select_all
  on public.polling
  for select
  to anon, authenticated
  using (true);

create policy polling_insert_all
  on public.polling
  for insert
  to anon, authenticated
  with check (true);
