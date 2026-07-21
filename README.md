# SimpleBoard

Steam leaderboard backend for a Unity game, built on Supabase (Postgres + Edge Functions). Players get one best score per random seed (1–9999); lower resubmissions are ignored server-side. Score identity is tied to a verified Steam auth ticket, not client-supplied input.

This repo is a template, not a hosted service: it has no default Supabase project. Each game deploys its own instance (see [Setup](#setup)) and points its own copy of the Unity client at it (see [Configuration](#configuration)).

## Architecture

```
Unity game (Steamworks.NET)
  |
  |-- reads (public, publishable key) -----> PostgREST RPC ---> Postgres functions
  |                                                                (joins persona_name from players)
  `-- submit score (Steam auth ticket) ----> Edge Function "submit-score"
                                                |-- verifies ticket against Steam Web API
                                                |-- looks up persona name (GetPlayerSummaries, best-effort)
                                                `-- calls submit_score() RPC as service_role
```

Two separate trust layers:
- **Supabase gateway auth** (`apikey`/`Authorization: Bearer <publishable key>`) — proves the caller holds the project's public key. Same key ships in every copy of the game; it is not a secret and grants no access by itself.
- **Player identity** — established only inside `submit-score`, by verifying the Steam auth ticket against Steamworks' `AuthenticateUserTicket` Web API. The resulting `steam_id` is what gets written, never a client-supplied value.

## Database

`public.scores`:

| column | type | notes |
|---|---|---|
| `steam_id` | `bigint` | SteamID64 |
| `seed_id` | `smallint` | `1`–`9999` (CHECK constraint) |
| `score` | `integer` | `>= 0` (CHECK constraint) |
| `created_at` / `updated_at` | `timestamptz` | |

`PRIMARY KEY (steam_id, seed_id)` — one row per player per seed.

`public.players`:

| column | type | notes |
|---|---|---|
| `steam_id` | `bigint` | `PRIMARY KEY` |
| `persona_name` | `text` | Steam display name as of the player's last score submission |
| `updated_at` | `timestamptz` | |

Populated only as a side effect of `submit_score()` — there's no standalone name-lookup endpoint. A player's name is whatever it was the last time they submitted a score to any seed; it doesn't update from a leaderboard view, and a score can exist with no matching `players` row if that submission's name lookup ever failed (the read functions `LEFT JOIN`, so this just shows up as `persona_name: null`, never a missing score).

**RLS**: both tables have RLS enabled, public `SELECT` only. No `INSERT`/`UPDATE`/`DELETE` grants for `anon`/`authenticated` on either — nothing is writable directly through the API. The only way in is `submit_score()`, and `EXECUTE` on that function is granted to `service_role` only (revoked from everyone else), so it's reachable exclusively from the `submit-score` Edge Function.

Four SQL functions (`supabase/migrations/`):

| function | purpose | callable by |
|---|---|---|
| `submit_score(p_steam_id, p_seed_id, p_score, p_persona_name = null)` | atomically upserts the score only if it's higher, and the player's persona name unconditionally (skipped if `p_persona_name` is null) | `service_role` only |
| `get_top_scores(p_limit = 10)` | top rows overall, across all seeds (same player can appear more than once) | `anon`, `authenticated` |
| `get_seed_top_scores(p_seed_id, p_limit = 10)` | top rows for one seed | `anon`, `authenticated` |
| `get_seed_rank(p_steam_id, p_seed_id)` | a player's `rank`/`score`/`total_players` for a seed; empty result if they haven't posted one | `anon`, `authenticated` |

All three read functions return `persona_name` alongside their other columns.

## API reference

Base URL: `https://<project-ref>.supabase.co`. All requests need `apikey` and `Authorization: Bearer <publishable key>` headers.

### Read (PostgREST RPC)

```
POST /rest/v1/rpc/get_top_scores          { "p_limit": 10 }
-> [{ "steam_id": ..., "seed_id": 42, "score": 1500, "rank": 1, "achieved_at": "...", "persona_name": "..." }, ...]

POST /rest/v1/rpc/get_seed_top_scores     { "p_seed_id": 42, "p_limit": 10 }
-> same shape as above, filtered to one seed

POST /rest/v1/rpc/get_seed_rank           { "p_steam_id": "76561197960287930", "p_seed_id": 42 }
-> [{ "steam_id": ..., "seed_id": 42, "score": 1500, "rank": 3, "total_players": 12, "persona_name": "..." }]
   (empty array if that player has no score for this seed)
```

### Write (Edge Function)

```
POST /functions/v1/submit-score
{ "ticket": "<hex Steam auth ticket>", "seed_id": 42, "score": 1500 }
-> { "steam_id": ..., "seed_id": 42, "best_score": 1500, "is_new_best": true }
```

The persona name shown in reads comes from here — `submit-score` looks it up via Steam's `GetPlayerSummaries` right after ticket verification and passes it to `submit_score()`. There's no separate name-lookup endpoint.

## Repo layout

```
supabase/
  config.toml
  migrations/            -- schema, functions, RLS (source of truth for the DB)
  functions/
    submit-score/         -- Steam ticket verification + score/persona-name upsert
unity-client/
  SimpleBoardClient.cs     -- HTTP client (UnityWebRequest + coroutines), no Steam dependency
  SimpleBoardSteamAuth.cs  -- Steamworks.NET ticket bridge, feeds SimpleBoardClient.SubmitScore
```

## Setup

Requires the [Supabase CLI](https://supabase.com/docs/guides/cli) and a GitHub-authenticated `gh` if you're managing the repo, but only the CLI is needed for deployment.

`.mcp.json` ships with a `<your-project-ref>` placeholder for the Supabase MCP server — replace it with your own project's ref if you use the MCP integration for development. It's independent of the CLI setup below.

```bash
supabase login
supabase link --project-ref <project-ref>

# Push schema/functions/RLS to the linked project
supabase db push

# Steam secrets — required before submit-score will work
supabase secrets set STEAM_WEB_API_KEY=<key> STEAM_APP_ID=<appid>

# Deploy the Edge Function
supabase functions deploy submit-score
```

Without the two Steam secrets set, `submit-score` responds `500 Server misconfigured` rather than failing silently.

## Unity client

Drop both files in `unity-client/` into your Unity project (e.g. `Assets/Scripts/`). Requires [Steamworks.NET](https://steamworks.github.io/) already integrated (`SteamManager.cs` present and initialized) for `SimpleBoardSteamAuth`; `SimpleBoardClient` alone has no Steam dependency.

```csharp
// Submit — Steam ticket handling is internal
SimpleBoardSteamAuth.Instance.SubmitScore(seedId: 42, score: 1500,
    onSuccess: result => Debug.Log($"Best: {result.best_score}, new best: {result.is_new_best}"),
    onError: err => Debug.LogError(err));

// Reads work without Steam being initialized at all -- entries[i].persona_name
// reflects whatever name that player had at their last submission, may be null
SimpleBoardClient.Instance.GetSeedTopScores(seedId: 42,
    onSuccess: entries => { /* ... */ },
    onError: err => Debug.LogError(err));
```

### Configuration

`SimpleBoardClient` ships with `supabaseUrl` and `publishableKey` blank — it refuses to run (logs an error, disables itself) until you set both on the component's Inspector fields, pointing at *your own* deployed project from [Setup](#setup):

- `supabaseUrl` — `https://<your-project-ref>.supabase.co`
- `publishableKey` — from Project Settings → API in your Supabase dashboard

The publishable key is safe to ship in a build (it identifies the project, not a permission grant), but it must be *your* project's key — pointing it at someone else's instance means your players' scores land in their database instead of yours, and vice versa. That's also why there's no shared default here: this repo is a template for standing up your own leaderboard, not a client for a single hosted one.

## Operations

- **Free-tier pausing**: Supabase pauses projects after 7 days with no API activity, and does not auto-resume — a paused project needs a manual "Restore" in the Dashboard. A cron job on a always-on Raspberry Pi pings `get_top_scores` every 3 days to prevent this (script lives on the Pi, not in this repo). If the leaderboard ever looks dead, check whether the project got paused before debugging further.
- **Security posture**: run `supabase db advisors` (or the `get_advisors` MCP tool) after any schema change — the project currently has zero open security/performance lints.
