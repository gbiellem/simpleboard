import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Resolves steam_ids stored in our DB to Steam persona names via Steam's Web API,
// keeping STEAM_WEB_API_KEY server-side. Steamids are handled as decimal strings
// throughout -- SteamID64 exceeds Number.MAX_SAFE_INTEGER, and Steam's own API
// already encodes them as JSON strings for exactly this reason.

const STEAM_WEB_API_KEY = Deno.env.get("STEAM_WEB_API_KEY");
const MAX_IDS_PER_REQUEST = 100; // Steam's GetPlayerSummaries limit
const STEAM_ID_PATTERN = /^\d{1,20}$/;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST") {
    return jsonResponse({ error: "Method not allowed" }, 405);
  }

  if (!STEAM_WEB_API_KEY) {
    console.error("Missing STEAM_WEB_API_KEY secret");
    return jsonResponse({ error: "Server misconfigured" }, 500);
  }

  let payload: { steam_ids?: unknown };
  try {
    payload = await req.json();
  } catch {
    return jsonResponse({ error: "Invalid JSON body" }, 400);
  }

  const { steam_ids } = payload;

  if (!Array.isArray(steam_ids) || steam_ids.length === 0) {
    return jsonResponse({ error: "steam_ids must be a non-empty array of decimal-string steamids" }, 400);
  }
  if (!steam_ids.every((id) => typeof id === "string" && STEAM_ID_PATTERN.test(id))) {
    return jsonResponse({ error: "steam_ids must be an array of decimal-string steamids" }, 400);
  }

  const uniqueIds = Array.from(new Set(steam_ids as string[]));
  if (uniqueIds.length > MAX_IDS_PER_REQUEST) {
    return jsonResponse({ error: `Too many steam_ids (max ${MAX_IDS_PER_REQUEST})` }, 400);
  }

  const url = new URL("https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/");
  url.searchParams.set("key", STEAM_WEB_API_KEY);
  url.searchParams.set("steamids", uniqueIds.join(","));

  const steamRes = await fetch(url.toString());
  if (!steamRes.ok) {
    console.error("Steam API error", steamRes.status, await steamRes.text());
    return jsonResponse({ error: "Failed to fetch player names from Steam" }, 502);
  }

  const steamBody = await steamRes.json();
  const players = steamBody?.response?.players ?? [];

  const result = players.map((p: Record<string, unknown>) => ({
    steam_id: String(p.steamid),
    persona_name: p.personaname,
  }));

  return jsonResponse(result);
});
