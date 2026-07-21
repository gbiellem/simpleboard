import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "jsr:@supabase/supabase-js@2";

// Verifies a Steam auth ticket (from ISteamUser::GetAuthTicketForWebApi) against
// the Steamworks Web API, then upserts the score under the *verified* steam_id.
// This is the only write path into the scores table -- see submit_score() in the DB,
// which is grantable to service_role only.

const STEAM_WEB_API_KEY = Deno.env.get("STEAM_WEB_API_KEY");
const STEAM_APP_ID = Deno.env.get("STEAM_APP_ID");
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const supabaseAdmin = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

type SteamVerification = { steamId: string } | { error: string };

async function verifySteamTicket(ticketHex: string): Promise<SteamVerification> {
  const url = new URL("https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/");
  url.searchParams.set("key", STEAM_WEB_API_KEY!);
  url.searchParams.set("appid", STEAM_APP_ID!);
  url.searchParams.set("ticket", ticketHex);
  url.searchParams.set("format", "json");

  const res = await fetch(url.toString());
  if (!res.ok) {
    return { error: `Steam API returned HTTP ${res.status}` };
  }

  const body = await res.json();
  const params = body?.response?.params;
  const apiError = body?.response?.error;

  if (apiError) {
    return { error: apiError.errordesc ?? "Steam ticket rejected" };
  }
  if (!params || params.result !== "OK") {
    return { error: "Steam ticket rejected" };
  }
  if (params.vacbanned || params.publisherbanned) {
    return { error: "Banned Steam account" };
  }

  return { steamId: String(params.steamid) };
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST") {
    return jsonResponse({ error: "Method not allowed" }, 405);
  }

  if (!STEAM_WEB_API_KEY || !STEAM_APP_ID) {
    console.error("Missing STEAM_WEB_API_KEY or STEAM_APP_ID secret");
    return jsonResponse({ error: "Server misconfigured" }, 500);
  }

  let payload: { ticket?: unknown; seed_id?: unknown; score?: unknown };
  try {
    payload = await req.json();
  } catch {
    return jsonResponse({ error: "Invalid JSON body" }, 400);
  }

  const { ticket, seed_id, score } = payload;

  if (typeof ticket !== "string" || ticket.length === 0) {
    return jsonResponse({ error: "ticket is required" }, 400);
  }
  if (typeof seed_id !== "number" || !Number.isInteger(seed_id) || seed_id < 1 || seed_id > 9999) {
    return jsonResponse({ error: "seed_id must be an integer between 1 and 9999" }, 400);
  }
  if (typeof score !== "number" || !Number.isInteger(score) || score < 0) {
    return jsonResponse({ error: "score must be a non-negative integer" }, 400);
  }

  const verification = await verifySteamTicket(ticket);
  if ("error" in verification) {
    return jsonResponse({ error: verification.error }, 401);
  }

  const { data, error } = await supabaseAdmin
    .rpc("submit_score", {
      p_steam_id: verification.steamId,
      p_seed_id: seed_id,
      p_score: score,
    })
    .single();

  if (error) {
    console.error("submit_score RPC failed", error);
    return jsonResponse({ error: "Failed to submit score" }, 500);
  }

  return jsonResponse(data);
});
