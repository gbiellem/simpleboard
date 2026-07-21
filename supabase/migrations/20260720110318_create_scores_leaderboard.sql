-- Scores table: one best score per (steam_id, seed_id)
create table public.scores (
  steam_id bigint not null,
  seed_id smallint not null,
  score integer not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint scores_pkey primary key (steam_id, seed_id),
  constraint scores_seed_id_range check (seed_id between 1 and 9999),
  constraint scores_score_non_negative check (score >= 0)
);

comment on table public.scores is
  'One best score per (steam_id, seed_id). Lower resubmissions are ignored by submit_score().';

-- Leaderboard query indexes
create index scores_score_desc_idx
  on public.scores (score desc, updated_at asc);

create index scores_seed_score_desc_idx
  on public.scores (seed_id, score desc, updated_at asc);

-- RLS: public can read, nobody but service_role can write
alter table public.scores enable row level security;

revoke insert, update, delete on public.scores from anon, authenticated;
grant select on public.scores to anon, authenticated;

create policy scores_select_all
  on public.scores
  for select
  to anon, authenticated
  using (true);

-- Write path: atomic upsert-if-higher, callable only by service_role
-- (Edge Function verifies the Steam identity before calling this.)
create or replace function public.submit_score(
  p_steam_id bigint,
  p_seed_id smallint,
  p_score integer
)
returns table (
  steam_id bigint,
  seed_id smallint,
  best_score integer,
  is_new_best boolean
)
language sql
security invoker
as $$
  with upsert as (
    insert into public.scores as s (steam_id, seed_id, score)
    values (p_steam_id, p_seed_id, p_score)
    on conflict (steam_id, seed_id) do update
      set score = excluded.score, updated_at = now()
      where excluded.score > s.score
    returning s.steam_id, s.seed_id, s.score, true as is_new_best
  )
  select * from upsert
  union all
  select s.steam_id, s.seed_id, s.score, false
  from public.scores s
  where s.steam_id = p_steam_id
    and s.seed_id = p_seed_id
    and not exists (select 1 from upsert)
  limit 1;
$$;

comment on function public.submit_score(bigint, smallint, integer) is
  'Upserts a score only if it beats the existing one for that (steam_id, seed_id). service_role only.';

revoke all on function public.submit_score(bigint, smallint, integer) from public, anon, authenticated;
grant execute on function public.submit_score(bigint, smallint, integer) to service_role;

-- Read path 1: top 10 (or N) scores of all time across all seeds
create or replace function public.get_top_scores(p_limit integer default 10)
returns table (
  steam_id bigint,
  seed_id smallint,
  score integer,
  rank bigint,
  achieved_at timestamptz
)
language sql
stable
security invoker
as $$
  select s.steam_id, s.seed_id, s.score,
         rank() over (order by s.score desc, s.updated_at asc) as rank,
         s.updated_at as achieved_at
  from public.scores s
  order by s.score desc, s.updated_at asc
  limit least(greatest(p_limit, 1), 100);
$$;

revoke execute on function public.get_top_scores(integer) from public;
grant execute on function public.get_top_scores(integer) to anon, authenticated;

-- Read path 2: top 10 (or N) scores for a specific seed
create or replace function public.get_seed_top_scores(p_seed_id smallint, p_limit integer default 10)
returns table (
  steam_id bigint,
  seed_id smallint,
  score integer,
  rank bigint,
  achieved_at timestamptz
)
language sql
stable
security invoker
as $$
  select s.steam_id, s.seed_id, s.score,
         rank() over (order by s.score desc, s.updated_at asc) as rank,
         s.updated_at as achieved_at
  from public.scores s
  where s.seed_id = p_seed_id
  order by s.score desc, s.updated_at asc
  limit least(greatest(p_limit, 1), 100);
$$;

revoke execute on function public.get_seed_top_scores(smallint, integer) from public;
grant execute on function public.get_seed_top_scores(smallint, integer) to anon, authenticated;

-- Read path 3: a player's current rank within a specific seed's leaderboard
create or replace function public.get_seed_rank(p_steam_id bigint, p_seed_id smallint)
returns table (
  steam_id bigint,
  seed_id smallint,
  score integer,
  rank bigint,
  total_players bigint
)
language sql
stable
security invoker
as $$
  with ranked as (
    select s.steam_id, s.seed_id, s.score,
           rank() over (order by s.score desc, s.updated_at asc) as rank,
           count(*) over () as total_players
    from public.scores s
    where s.seed_id = p_seed_id
  )
  select * from ranked r where r.steam_id = p_steam_id;
$$;

revoke execute on function public.get_seed_rank(bigint, smallint) from public;
grant execute on function public.get_seed_rank(bigint, smallint) to anon, authenticated;
