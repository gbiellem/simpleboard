-- Enable pg_cron so Postgres can schedule its own recurring jobs.
create extension if not exists pg_cron with schema pg_catalog;

grant usage on schema cron to postgres;
grant all privileges on all tables in schema cron to postgres;

-- Daily wipe of the polling heartbeat table at midnight UTC. Runs as the
-- role that owns the job (postgres, a superuser), so it bypasses RLS --
-- no separate grant needed for the delete itself.
select cron.schedule(
  'polling-daily-cleanup',
  '0 0 * * *',
  $$delete from public.polling$$
);
