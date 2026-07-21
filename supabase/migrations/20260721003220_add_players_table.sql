-- Players lookup: captures each player's Steam persona name as of their most
-- recent score submission (refreshed by submit_score(), see below). A score can
-- exist with no matching row yet if that submission's name lookup ever failed.
create table public.players (
  steam_id bigint primary key,
  persona_name text not null,
  updated_at timestamptz not null default now()
);

comment on table public.players is
  'Steam persona name as of each player''s most recent score submission. Written only by service_role, via submit_score().';

alter table public.players enable row level security;

revoke insert, update, delete on public.players from anon, authenticated;
grant select on public.players to anon, authenticated;

create policy players_select_all
  on public.players
  for select
  to anon, authenticated
  using (true);

-- submit_score now also captures the player's persona name, atomically with the
-- score upsert. p_persona_name is optional -- if the caller couldn't resolve it
-- (e.g. Steam API hiccup), the score still gets recorded and the players row is
-- left as whatever it already was.
drop function if exists public.submit_score(bigint, smallint, integer);
create function public.submit_score(
  p_steam_id bigint,
  p_seed_id smallint,
  p_score integer,
  p_persona_name text default null
)
returns table (
  steam_id bigint,
  seed_id smallint,
  best_score integer,
  is_new_best boolean
)
language sql
security invoker
set search_path = ''
as $$
  with player_upsert as (
    insert into public.players as pl (steam_id, persona_name, updated_at)
    select p_steam_id, p_persona_name, now()
    where p_persona_name is not null
    on conflict (steam_id) do update
      set persona_name = excluded.persona_name, updated_at = excluded.updated_at
    returning pl.steam_id
  ),
  score_upsert as (
    insert into public.scores as s (steam_id, seed_id, score)
    values (p_steam_id, p_seed_id, p_score)
    on conflict (steam_id, seed_id) do update
      set score = excluded.score, updated_at = now()
      where excluded.score > s.score
    returning s.steam_id, s.seed_id, s.score, true as is_new_best
  )
  select score_upsert.steam_id, score_upsert.seed_id, score_upsert.score, score_upsert.is_new_best
  from score_upsert
  left join player_upsert on true
  union all
  select s.steam_id, s.seed_id, s.score, false
  from public.scores s
  left join player_upsert on true
  where s.steam_id = p_steam_id
    and s.seed_id = p_seed_id
    and not exists (select 1 from score_upsert)
  limit 1;
$$;

comment on function public.submit_score(bigint, smallint, integer, text) is
  'Upserts a score only if it beats the existing one, and the player''s persona name unconditionally. service_role only.';

revoke all on function public.submit_score(bigint, smallint, integer, text) from public, anon, authenticated;
grant execute on function public.submit_score(bigint, smallint, integer, text) to service_role;

-- Read functions: add persona_name via left join (never drops a score row for a
-- player whose name hasn't been captured yet).

drop function if exists public.get_top_scores(integer);
create function public.get_top_scores(p_limit integer default 10)
returns table (
  steam_id bigint,
  seed_id smallint,
  score integer,
  rank bigint,
  achieved_at timestamptz,
  persona_name text
)
language sql
stable
security invoker
set search_path = ''
as $$
  select s.steam_id, s.seed_id, s.score,
         rank() over (order by s.score desc, s.updated_at asc) as rank,
         s.updated_at as achieved_at,
         p.persona_name
  from public.scores s
  left join public.players p on p.steam_id = s.steam_id
  order by s.score desc, s.updated_at asc
  limit least(greatest(p_limit, 1), 100);
$$;

revoke execute on function public.get_top_scores(integer) from public;
grant execute on function public.get_top_scores(integer) to anon, authenticated;

drop function if exists public.get_seed_top_scores(smallint, integer);
create function public.get_seed_top_scores(p_seed_id smallint, p_limit integer default 10)
returns table (
  steam_id bigint,
  seed_id smallint,
  score integer,
  rank bigint,
  achieved_at timestamptz,
  persona_name text
)
language sql
stable
security invoker
set search_path = ''
as $$
  select s.steam_id, s.seed_id, s.score,
         rank() over (order by s.score desc, s.updated_at asc) as rank,
         s.updated_at as achieved_at,
         p.persona_name
  from public.scores s
  left join public.players p on p.steam_id = s.steam_id
  where s.seed_id = p_seed_id
  order by s.score desc, s.updated_at asc
  limit least(greatest(p_limit, 1), 100);
$$;

revoke execute on function public.get_seed_top_scores(smallint, integer) from public;
grant execute on function public.get_seed_top_scores(smallint, integer) to anon, authenticated;

drop function if exists public.get_seed_rank(bigint, smallint);
create function public.get_seed_rank(p_steam_id bigint, p_seed_id smallint)
returns table (
  steam_id bigint,
  seed_id smallint,
  score integer,
  rank bigint,
  total_players bigint,
  persona_name text
)
language sql
stable
security invoker
set search_path = ''
as $$
  with ranked as (
    select s.steam_id, s.seed_id, s.score,
           rank() over (order by s.score desc, s.updated_at asc) as rank,
           count(*) over () as total_players
    from public.scores s
    where s.seed_id = p_seed_id
  )
  select r.steam_id, r.seed_id, r.score, r.rank, r.total_players, p.persona_name
  from ranked r
  left join public.players p on p.steam_id = r.steam_id
  where r.steam_id = p_steam_id;
$$;

revoke execute on function public.get_seed_rank(bigint, smallint) from public;
grant execute on function public.get_seed_rank(bigint, smallint) to anon, authenticated;
