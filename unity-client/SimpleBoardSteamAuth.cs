using System;
using Steamworks;
using UnityEngine;

namespace SimpleBoard
{
    /// <summary>
    /// Bridges Steamworks.NET auth tickets into SimpleBoardClient.SubmitScore, so game
    /// code never touches Steamworks directly for score submission.
    /// Requires SteamAPI to already be initialized -- e.g. via the SteamManager.cs
    /// that ships with the Steamworks.NET package -- before any method here is called.
    /// </summary>
    public class SimpleBoardSteamAuth : MonoBehaviour
    {
        public static SimpleBoardSteamAuth Instance { get; private set; }

        // Valve does not validate this string against anything server-side for
        // AuthenticateUserTicket; it just tags the ticket's intended use.
        private const string TicketIdentity = "simpleboard";

        private Callback<GetTicketForWebApiResponse_t> _ticketResponseCallback;
        private HAuthTicket _activeTicket = default;
        private Action<byte[], uint> _onTicketReady;
        private Action<string> _onTicketError;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // Created in Start (not Awake) so SteamManager has had a chance to run
            // SteamAPI.Init() first if both scripts are set up on the same frame.
            _ticketResponseCallback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicketForWebApiResponse);
        }

        /// <summary>Current player's SteamID64, e.g. for SimpleBoardClient.GetSeedRank.</summary>
        public ulong LocalSteamId => SteamUser.GetSteamID().m_SteamID;

        /// <summary>
        /// Requests a fresh Steam ticket, submits the score through SimpleBoardClient,
        /// then cancels the ticket. This is the one call most game code needs.
        /// </summary>
        public void SubmitScore(int seedId, int score, Action<SubmitScoreResult> onSuccess, Action<string> onError)
        {
            if (!SteamManager.Initialized)
            {
                onError?.Invoke("Steam is not initialized");
                return;
            }

            if (!Equals(_activeTicket, default(HAuthTicket)))
            {
                onError?.Invoke("A ticket request is already in progress");
                return;
            }

            RequestTicket(
                (ticketBytes, ticketLength) =>
                {
                    SimpleBoardClient.Instance.SubmitScore(ticketBytes, ticketLength, seedId, score,
                        result =>
                        {
                            CancelActiveTicket();
                            onSuccess?.Invoke(result);
                        },
                        err =>
                        {
                            CancelActiveTicket();
                            onError?.Invoke(err);
                        });
                },
                err =>
                {
                    CancelActiveTicket();
                    onError?.Invoke(err);
                });
        }

        private void RequestTicket(Action<byte[], uint> onTicketReady, Action<string> onError)
        {
            _onTicketReady = onTicketReady;
            _onTicketError = onError;
            _activeTicket = SteamUser.GetAuthTicketForWebApi(TicketIdentity);

            if (Equals(_activeTicket, default(HAuthTicket)))
            {
                _onTicketReady = null;
                var err = _onTicketError;
                _onTicketError = null;
                err?.Invoke("Steam refused to issue an auth ticket");
            }
        }

        private void OnTicketForWebApiResponse(GetTicketForWebApiResponse_t callback)
        {
            if (!Equals(callback.m_hAuthTicket, _activeTicket))
                return; // response for a ticket we're no longer tracking

            var onReady = _onTicketReady;
            var onError = _onTicketError;
            _onTicketReady = null;
            _onTicketError = null;

            if (callback.m_eResult != EResult.k_EResultOK)
            {
                _activeTicket = default;
                onError?.Invoke($"Steam ticket request failed: {callback.m_eResult}");
                return;
            }

            onReady?.Invoke(callback.m_rgubTicket, (uint)callback.m_cubTicket);
        }

        private void CancelActiveTicket()
        {
            if (!Equals(_activeTicket, default(HAuthTicket)))
            {
                SteamUser.CancelAuthTicket(_activeTicket);
                _activeTicket = default;
            }
        }

        private void OnDestroy()
        {
            CancelActiveTicket();
        }
    }
}
