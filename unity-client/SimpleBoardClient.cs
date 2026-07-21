using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SimpleBoard
{
    /// <summary>
    /// Attach to a persistent GameObject and access via SimpleBoardClient.Instance.
    /// Reads (leaderboards, rank) go straight to PostgREST with the publishable key.
    /// Score submission goes through the submit-score Edge Function, which verifies
    /// the Steam auth ticket server-side before writing anything.
    /// </summary>
    public class SimpleBoardClient : MonoBehaviour
    {
        public static SimpleBoardClient Instance { get; private set; }

        // This is a template with no default target -- every deployment points at
        // its own Supabase project. Set both fields on the Inspector before use;
        // see the "Configuration" section in the repo README for how to deploy
        // your own instance.
        [Header("Supabase project (required -- see README)")]
        [Tooltip("Your project's API URL, e.g. https://<project-ref>.supabase.co")]
        [SerializeField] private string supabaseUrl = "";
        // Publishable key is safe to ship in a client build -- it identifies the
        // project, it does not grant access. Row/function grants do that. Still
        // must be YOUR project's key, not left blank or copied from another deploy.
        [Tooltip("Your project's publishable (anon) key")]
        [SerializeField] private string publishableKey = "";

        private string FunctionsBase => $"{supabaseUrl}/functions/v1";
        private string RpcBase => $"{supabaseUrl}/rest/v1/rpc";

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(publishableKey))
            {
                Debug.LogError("SimpleBoardClient: supabaseUrl/publishableKey are not set. " +
                    "Deploy your own Supabase project and configure this component's Inspector fields -- see README.", this);
                enabled = false;
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ---------------------------------------------------------------
        // Write: submit a score for a seed. Ignored server-side if it's
        // not higher than the player's existing best for that seed.
        // ---------------------------------------------------------------

        /// <param name="steamTicket">Buffer from ISteamUser.GetAuthTicketForWebApi (or GetAuthSessionTicket).</param>
        /// <param name="steamTicketLength">Actual ticket length -- the buffer is oversized, trailing bytes must be trimmed.</param>
        public void SubmitScore(byte[] steamTicket, uint steamTicketLength, int seedId, int score,
            Action<SubmitScoreResult> onSuccess, Action<string> onError)
        {
            SubmitScore(BytesToHex(steamTicket, (int)steamTicketLength), seedId, score, onSuccess, onError);
        }

        public void SubmitScore(string steamTicketHex, int seedId, int score,
            Action<SubmitScoreResult> onSuccess, Action<string> onError)
        {
            if (!IsValidSeedId(seedId))
            {
                onError?.Invoke($"seed_id must be between 1 and 9999 (got {seedId})");
                return;
            }

            var body = new SubmitScoreRequest { ticket = steamTicketHex, seed_id = seedId, score = score };
            StartCoroutine(PostJson(
                $"{FunctionsBase}/submit-score",
                JsonUtility.ToJson(body),
                json => onSuccess?.Invoke(JsonUtility.FromJson<SubmitScoreResult>(json)),
                onError));
        }

        // ---------------------------------------------------------------
        // Reads
        // ---------------------------------------------------------------

        public void GetTopScores(Action<ScoreEntry[]> onSuccess, Action<string> onError, int limit = 10)
        {
            var body = new LimitRequest { p_limit = limit };
            StartCoroutine(PostJson(
                $"{RpcBase}/get_top_scores",
                JsonUtility.ToJson(body),
                json => onSuccess?.Invoke(JsonHelper.FromJson<ScoreEntry>(json)),
                onError));
        }

        public void GetSeedTopScores(int seedId, Action<ScoreEntry[]> onSuccess, Action<string> onError, int limit = 10)
        {
            if (!IsValidSeedId(seedId))
            {
                onError?.Invoke($"seed_id must be between 1 and 9999 (got {seedId})");
                return;
            }

            var body = new SeedLimitRequest { p_seed_id = seedId, p_limit = limit };
            StartCoroutine(PostJson(
                $"{RpcBase}/get_seed_top_scores",
                JsonUtility.ToJson(body),
                json => onSuccess?.Invoke(JsonHelper.FromJson<ScoreEntry>(json)),
                onError));
        }

        /// <summary>onSuccess receives null if the player has no score for that seed yet.</summary>
        public void GetSeedRank(ulong steamId, int seedId, Action<SeedRank> onSuccess, Action<string> onError)
        {
            if (!IsValidSeedId(seedId))
            {
                onError?.Invoke($"seed_id must be between 1 and 9999 (got {seedId})");
                return;
            }

            var body = new SeedRankRequest { p_steam_id = steamId, p_seed_id = seedId };
            StartCoroutine(PostJson(
                $"{RpcBase}/get_seed_rank",
                JsonUtility.ToJson(body),
                json =>
                {
                    var results = JsonHelper.FromJson<SeedRank>(json);
                    onSuccess?.Invoke(results.Length > 0 ? results[0] : null);
                },
                onError));
        }

        // ---------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------

        private static bool IsValidSeedId(int seedId) => seedId >= 1 && seedId <= 9999;

        private IEnumerator PostJson(string url, string jsonBody, Action<string> onSuccess, Action<string> onError)
        {
            using (var request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("apikey", publishableKey);
                request.SetRequestHeader("Authorization", "Bearer " + publishableKey);

                yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool failed = request.result != UnityWebRequest.Result.Success;
#else
                bool failed = request.isNetworkError || request.isHttpError;
#endif
                if (failed)
                {
                    onError?.Invoke(ExtractErrorMessage(request.downloadHandler?.text) ?? request.error);
                    yield break;
                }

                onSuccess?.Invoke(request.downloadHandler.text);
            }
        }

        private static string ExtractErrorMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            // submit-score Edge Function uses {"error": "..."}
            try
            {
                var e = JsonUtility.FromJson<EdgeFunctionError>(body);
                if (!string.IsNullOrEmpty(e?.error)) return e.error;
            }
            catch { /* not this shape */ }

            // PostgREST uses {"message": "...", "code": "...", ...}
            try
            {
                var e = JsonUtility.FromJson<PostgrestError>(body);
                if (!string.IsNullOrEmpty(e?.message)) return e.message;
            }
            catch { /* not this shape */ }

            return body;
        }

        private static string BytesToHex(byte[] bytes, int length)
        {
            var sb = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        [Serializable] private class SubmitScoreRequest { public string ticket; public int seed_id; public int score; }
        [Serializable] private class LimitRequest { public int p_limit; }
        [Serializable] private class SeedLimitRequest { public int p_seed_id; public int p_limit; }
        [Serializable] private class SeedRankRequest { public ulong p_steam_id; public int p_seed_id; }
        [Serializable] private class EdgeFunctionError { public string error; }
        [Serializable] private class PostgrestError { public string message; }
    }

    [Serializable]
    public class ScoreEntry
    {
        public ulong steam_id;
        public int seed_id;
        public int score;
        public long rank;
        public string achieved_at;
        /// <summary>Persona name as of this player's last score submission; null if never captured.</summary>
        public string persona_name;
    }

    [Serializable]
    public class SeedRank
    {
        public ulong steam_id;
        public int seed_id;
        public int score;
        public long rank;
        public long total_players;
        /// <summary>Persona name as of this player's last score submission; null if never captured.</summary>
        public string persona_name;
    }

    [Serializable]
    public class SubmitScoreResult
    {
        public ulong steam_id;
        public int seed_id;
        public int best_score;
        public bool is_new_best;
    }

    /// <summary>JsonUtility can't parse a top-level JSON array; wrap it first.</summary>
    internal static class JsonHelper
    {
        public static T[] FromJson<T>(string json)
        {
            string wrapped = "{\"items\":" + json + "}";
            var wrapper = JsonUtility.FromJson<Wrapper<T>>(wrapped);
            return wrapper.items ?? Array.Empty<T>();
        }

        [Serializable]
        private class Wrapper<T>
        {
            public T[] items;
        }
    }
}
