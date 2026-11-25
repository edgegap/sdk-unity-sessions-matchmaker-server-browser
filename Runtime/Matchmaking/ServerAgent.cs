using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

namespace Edgegap.Matchmaking
{
    using L = Logger;

    public class ServerAgent<A>
    {
        private Api MatchmakingApi;

        public MonoBehaviour Handler;
        public string Profile;
        public BackfillAttributes BackfillAttributes;
        public int TargetTeamSize;

        // BaseUrl may only be set with constructor
        public string BaseUrl { get; }
        public string AuthToken { private get; set; }

        public int RequestTimeoutSeconds;
        public float PollingBackoffSeconds;
        public int MaxConsecutivePollingErrors;
        public float ConnectionGracePeriodSeconds;

        public bool LogBackfillUpdates;
        public bool LogPollingUpdates;

        public Observable<MonitorResponseDTO> Monitor { get; private set; } =
            new Observable<MonitorResponseDTO>() { };

        public Observable<Dictionary<string, BackfillResponseDTO<A>>> Backfills
        {
            get;
            private set;
        } = new Observable<Dictionary<string, BackfillResponseDTO<A>>>() { };

        public Dictionary<string, BackfillAssignedTicket<A>> Assignments { get; private set; } =
            new Dictionary<string, BackfillAssignedTicket<A>>() { };

        private protected bool Polling = false;
        private protected bool BackfillRunning = false;
        private protected bool BackfillPending = false;

        // backfill creations sent but not answered yet, counted toward capacity
        private protected int CreatingBackfills = 0;

        // true between AddBackfills and AbandonAllBackfills, no backfill is created or refilled otherwise
        private protected bool BackfillEnabled = false;

        // incremented when a polling loop starts, so only the latest loop keeps polling
        private protected int PollingSession = 0;

        public ServerAgent(
            MonoBehaviour handler,
            string baseUrl,
            string authToken,
            string profile,
            BackfillAttributes attributes,
            int targetTeamSize = -1,
            int requestTimeoutSeconds = 3,
            float pollingBackoffSeconds = 1f,
            int maxConsecutivePollingErrors = 10,
            float connectionGracePeriodSeconds = 60f,
            bool logBackfillUpdates = true,
            bool logPollingUpdates = false
        )
        {
            if (handler == null)
            {
                throw new Exception("MatchmakingServer Handler not assigned.");
            }

            Handler = handler;

            BaseUrl = baseUrl;
            AuthToken = authToken;
            Profile = profile;
            BackfillAttributes = attributes;
            TargetTeamSize = targetTeamSize;

            RequestTimeoutSeconds = requestTimeoutSeconds;
            PollingBackoffSeconds = pollingBackoffSeconds;
            MaxConsecutivePollingErrors = maxConsecutivePollingErrors;
            ConnectionGracePeriodSeconds = connectionGracePeriodSeconds;

            LogBackfillUpdates = logBackfillUpdates;
            LogPollingUpdates = logPollingUpdates;
        }

        public bool AbandonPlayer(string ticketID, bool expiredGrace = false)
        {
            if (Assignments.Remove(ticketID))
            {
                L.Log(
                    $"MM | Backfill - ticket abandoned{(expiredGrace ? " (connection grace period expired)" : "")} [{ticketID}]"
                );
                FillSeats();
                return true;
            }

            L.Warn($"MM | Backfill - ticket abandon failed [{ticketID}]");
            return false;
        }

        public BackfillAssignedTicket<A> PlayerConnected(string ticketID)
        {
            if (Assignments.ContainsKey(ticketID))
            {
                if (Assignments[ticketID].ConnectedAt is null)
                {
                    Assignments[ticketID].ConnectedAt = DateTime.Now;
                }

                return Assignments[ticketID];
            }

            return null;
        }

        #region Server API

        public void Status()
        {
            MatchmakingApi.GetMonitor(
                (MonitorResponseDTO monitor, UnityWebRequest request) =>
                {
                    if (monitor.Status.ToLower() == "healthy")
                    {
                        Monitor._Update(monitor, "healthy");
                    }
                    else
                    {
                        Monitor._Update(monitor, "unhealthy");
                    }
                },
                (string error, UnityWebRequest request) =>
                {
                    Monitor._Error($"get monitor failed (unexpected error)\n{error}", null);
                }
            );
        }

        /// <summary>
        /// Enable backfill and create backfills for every open seat. Seats freed later (AbandonPlayer,
        /// connection grace period expiry) are refilled until AbandonAllBackfills.
        /// </summary>
        public void AddBackfills()
        {
            BackfillEnabled = true;
            FillSeats();
        }

        public void AbandonBackfill(string backfillID, Action onCompletedDelegate = null)
        {
            if (!Backfills.Current.ContainsKey(backfillID))
            {
                Backfills._Notify(
                    $"abandon failed (not found) [{backfillID}]",
                    ObservableActionType.Warn
                );
                return;
            }

            MatchmakingApi.DeleteBackfill(
                backfillID,
                (UnityWebRequest request) =>
                {
                    Backfills._Notify($"abandoned [{backfillID}]");

                    RemoveBackfill(Backfills.Current, backfillID, onCompletedDelegate);
                },
                (string error, UnityWebRequest request) =>
                {
                    if (request.responseCode == 404)
                    {
                        Backfills._Notify(
                            $"abandon failed (not found) [{backfillID}]",
                            ObservableActionType.Warn
                        );
                    }
                    else
                    {
                        Backfills._Notify(
                            $"abandon failed [{backfillID}]\n{error}",
                            ObservableActionType.Warn
                        );
                    }

                    RemoveBackfill(Backfills.Current, backfillID, onCompletedDelegate);
                }
            );
        }

        /// <summary>
        /// Disable backfill, stop polling and delete every backfill, for example when the match starts.
        /// Backfills still being created are deleted once created, so none is left behind.
        /// </summary>
        public void AbandonAllBackfills(Action onCompletedDelegate = null)
        {
            BackfillEnabled = false;
            Polling = false;

            if (CreatingBackfills > 0)
            {
                Handler.StartCoroutine(AbandonAllBackfillsWhenCreated(onCompletedDelegate));
            }
            else
            {
                AbandonCurrentBackfills(onCompletedDelegate);
            }
        }
        #endregion

        #region Initialization
        public void Initialize(
            Dictionary<string, InjectedTicketDTO<A>> tickets,
            UnityAction<
                Observable<MonitorResponseDTO>,
                ObservableActionType,
                string
            > onMonitorUpdate,
            UnityAction<
                Observable<Dictionary<string, BackfillResponseDTO<A>>>,
                ObservableActionType,
                string
            > onBackfillUpdate
        )
        {
            if (string.IsNullOrEmpty(BaseUrl.Trim()))
            {
                throw new Exception("BaseUrl not declared.");
            }

            if (string.IsNullOrEmpty(AuthToken.Trim()))
            {
                throw new Exception("AuthToken not declared.");
            }

            MatchmakingApi = new Api(Handler, AuthToken, BaseUrl, RequestTimeoutSeconds);

            L.SubscribeLogger(Monitor, "MM", "Monitor");
            Monitor.Subscribe(onMonitorUpdate);

            L.SubscribeLogger(Backfills, "MM", "Backfill", LogBackfillUpdates);
            Backfills.Subscribe(onBackfillUpdate);

            foreach (InjectedTicketDTO<A> ticket in tickets.Values)
            {
                Assignments[ticket.ID] = new BackfillAssignedTicket<A>()
                {
                    ID = ticket.ID,
                    CreatedAt = ticket.CreatedAt,
                    PlayerIP = ticket.PlayerIP,
                    GroupID = ticket.GroupID,
                    Attributes = ticket.Attributes,
                };

                Handler.StartCoroutine(CheckTicketConnection(ticket.ID));
            }

            Status();
        }
        #endregion

        #region Internals

        internal void FillSeats()
        {
            if (!BackfillEnabled)
            {
                return;
            }

            if (BackfillRunning)
            {
                BackfillPending = true;
                return;
            }

            BackfillRunning = true;

            try
            {
                do
                {
                    BackfillPending = false;
                    int plannedBackfills =
                        TargetTeamSize
                        - (Assignments.Count + Backfills.Current.Count + CreatingBackfills);

                    for (int i = 0; i < plannedBackfills; ++i)
                    {
                        StartNewBackfill();
                    }
                } while (BackfillPending);
            }
            finally
            {
                BackfillRunning = false;
                BackfillPending = false;
            }
        }

        internal IEnumerator AbandonAllBackfillsWhenCreated(Action onCompletedDelegate)
        {
            // a creation landing after the deletes would be left behind and restart polling
            yield return new WaitUntil(() => CreatingBackfills == 0);
            Polling = false;
            AbandonCurrentBackfills(onCompletedDelegate);
        }

        internal void AbandonCurrentBackfills(Action onCompletedDelegate)
        {
            ConcurrentQueue<string> requestsFinished = new ConcurrentQueue<string>();
            int requestsPending = Backfills.Current.Count;

            foreach (KeyValuePair<string, BackfillResponseDTO<A>> b in Backfills.Current)
            {
                AbandonBackfill(
                    b.Key,
                    () =>
                    {
                        requestsFinished.Enqueue(b.Key);
                    }
                );
            }

            if (onCompletedDelegate is not null)
            {
                Handler.StartCoroutine(
                    WaitForRequests(requestsFinished, requestsPending, onCompletedDelegate)
                );
            }
        }

        internal void RemoveBackfill(
            Dictionary<string, BackfillResponseDTO<A>> backfills,
            string backfillID,
            Action onCompletedDelegate = null
        )
        {
            Dictionary<string, BackfillResponseDTO<A>> newBackfills = new Dictionary<
                string,
                BackfillResponseDTO<A>
            >(backfills);

            if (newBackfills.Remove(backfillID))
            {
                Backfills._Update(newBackfills, $"removed [{backfillID}]");
            }
            else
            {
                Backfills._Notify(
                    $"remove failed (not found) [{backfillID}]",
                    ObservableActionType.Warn
                );
            }

            if (onCompletedDelegate is not null)
            {
                onCompletedDelegate();
            }
        }

        internal void StartNewBackfill()
        {
            if (!BackfillEnabled)
            {
                return;
            }

            if (Assignments.Count + Backfills.Current.Count + CreatingBackfills >= TargetTeamSize)
            {
                Backfills._Error("maximum capacity currently reached");
                return;
            }

            BackfillRequestDTO<A> backfill = new BackfillRequestDTO<A>(
                Profile,
                BackfillAttributes,
                Assignments
            );

            CreatingBackfills++;
            MatchmakingApi.CreateBackfill(
                backfill,
                (BackfillResponseDTO<A> backfillRes, UnityWebRequest request) =>
                {
                    CreatingBackfills--;
                    backfillRes.CreatedAt = DateTime.Now;

                    Dictionary<string, BackfillResponseDTO<A>> newBackfills = new Dictionary<
                        string,
                        BackfillResponseDTO<A>
                    >(Backfills.Current);

                    newBackfills[backfillRes.ID] = backfillRes;
                    Backfills._Update(newBackfills, $"created [{backfillRes.ID}]");

                    // after AbandonAllBackfills, polling stays stopped and this backfill is deleted
                    if (BackfillEnabled && !Polling && Backfills.Current.Count > 0)
                    {
                        Polling = true;
                        int session = ++PollingSession;
                        Handler.StartCoroutine(
                            DelayMethod(() => StartPollingBackfills(0, null, session))
                        );
                    }
                },
                (string error, UnityWebRequest request) =>
                {
                    CreatingBackfills--;
                    Backfills._Error($"create failed\n{error}");
                }
            );
        }

        internal void StartPollingBackfills(
            int consecutiveErrors = 0,
            List<string> failedBackfills = null,
            int? session = null
        )
        {
            int pollingSession = session ?? PollingSession;
            if (pollingSession != PollingSession)
            {
                // a newer polling loop was started
                return;
            }

            if (!Polling)
            {
                Backfills._Notify($"polling stopped");
                return;
            }

            if (Backfills.Current.Count == 0)
            {
                // creating a backfill starts polling again
                Polling = false;
                Backfills._Notify($"polling stopped");
                return;
            }

            if (consecutiveErrors > MaxConsecutivePollingErrors)
            {
                Polling = false;
                if (failedBackfills is null || failedBackfills.Count == 0)
                {
                    Backfills._Error($"polling failed (reached maximum retries)");
                }
                else
                {
                    foreach (string failed in failedBackfills)
                    {
                        Backfills._Error($"polling failed (reached maximum retries) [{failed}]");
                    }
                }
                AbandonAllBackfills();
                return;
            }

            if (LogPollingUpdates)
            {
                Backfills._Notify(
                    $"polling [{consecutiveErrors + 1}/{MaxConsecutivePollingErrors}]"
                );
            }

            ConcurrentQueue<bool> requestsFinished = new ConcurrentQueue<bool>();
            ConcurrentQueue<string> requestsFailed = new ConcurrentQueue<string>();
            int requestsPending = Backfills.Current.Count;

            foreach (BackfillResponseDTO<A> b in Backfills.Current.Values)
            {
                MatchmakingApi.GetBackfill<A>(
                    b.ID,
                    (BackfillResponseDTO<A> backfill, UnityWebRequest request) =>
                    {
                        requestsFinished.Enqueue(true);

                        if (backfill.Status == "ASSIGNED")
                        {
                            InjectedTicketDTO<A> ticket = backfill.AssignedTicket;

                            Assignments[ticket.ID] = new BackfillAssignedTicket<A>()
                            {
                                ID = ticket.ID,
                                CreatedAt = ticket.CreatedAt,
                                PlayerIP = ticket.PlayerIP,
                                GroupID = ticket.GroupID,
                                Attributes = ticket.Attributes,
                            };

                            Dictionary<string, BackfillResponseDTO<A>> assignedBackfills =
                                new Dictionary<string, BackfillResponseDTO<A>>(Backfills.Current);
                            assignedBackfills[backfill.ID] = backfill;
                            Backfills._Update(assignedBackfills, $"assigned [{backfill.ID}]");
                            RemoveBackfill(Backfills.Current, backfill.ID);
                            Handler.StartCoroutine(CheckTicketConnection(ticket.ID));
                        }
                    },
                    (string error, UnityWebRequest request) =>
                    {
                        requestsFailed.Enqueue(b.ID);
                        requestsFinished.Enqueue(false);

                        if (request.responseCode == 404)
                        {
                            Backfills._Notify(
                                $"polling failed (not found) [{b.ID}]",
                                ObservableActionType.Warn
                            );

                            RemoveBackfill(Backfills.Current, b.ID, StartNewBackfill);
                        }
                        else if (request.responseCode != 429 && request.responseCode < 500)
                        {
                            Backfills._Error($"polling failed [{b.ID}]\n{error}");
                            AbandonBackfill(b.ID, StartNewBackfill);
                        }
                    }
                );
            }

            Handler.StartCoroutine(
                WaitForRequests(
                    requestsFinished,
                    requestsPending,
                    () =>
                    {
                        if (requestsFinished.Contains(false))
                        {
                            consecutiveErrors += 1;
                        }
                        else
                        {
                            consecutiveErrors = 0;
                        }

                        List<string> failed = new List<string>(requestsFailed);
                        Handler.StartCoroutine(
                            DelayMethod(() =>
                                StartPollingBackfills(consecutiveErrors, failed, pollingSession)
                            )
                        );
                    }
                )
            );
        }

        internal IEnumerator CheckTicketConnection(string ticketID)
        {
            yield return new WaitForSecondsRealtime(ConnectionGracePeriodSeconds);

            if (Assignments.ContainsKey(ticketID) && Assignments[ticketID].ConnectedAt is null)
            {
                AbandonPlayer(ticketID, true);
            }
        }

        internal IEnumerator DelayMethod(Action onDelayFinished, float? delaySeconds = null)
        {
            // jitter scales with the backoff so servers that start together drift apart
            delaySeconds ??= PollingBackoffSeconds * Random.Range(0.8f, 1.2f);

            yield return new WaitForSeconds((float)delaySeconds);
            onDelayFinished();
        }

        internal IEnumerator WaitForRequests<T>(
            ConcurrentQueue<T> requests,
            int expectedCount,
            Action onComplete
        )
        {
            yield return new WaitUntil(() => requests.Count == expectedCount);
            onComplete.Invoke();
        }
        #endregion
    }
}
