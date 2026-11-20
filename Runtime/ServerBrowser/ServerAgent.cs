using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

namespace Edgegap.ServerBrowser
{
    using L = Logger;

    public class ServerAgent<ServerInstanceMetadata, SlotMetadata>
        where ServerInstanceMetadata : MetadataDTO, new()
        where SlotMetadata : MetadataDTO, new()
    {
        private Api<ServerInstanceMetadata, SlotMetadata> Api;

        public MonoBehaviour Handler { get; private set; }

        // BaseUrl may only be set with constructor
        public string BaseUrl { get; }
        public string AuthToken { private get; set; }

        public bool AcceptExpiredReservations = false;
        public UpdateMode UpdateMode = UpdateMode.Heartbeat;
        public int RequestTimeoutSeconds;
        public float HeartbeatIntervalSeconds;
        public int HeartbeatMaxConsecutiveErrors;

        public Observable<MonitorResponseDTO> Monitor { get; private set; } =
            new Observable<MonitorResponseDTO> { };
        public Observable<InstanceDTO<ServerInstanceMetadata, SlotMetadata>> Instance
        {
            get;
            private set;
        } = new Observable<InstanceDTO<ServerInstanceMetadata, SlotMetadata>>() { };
        public Observable<ConfirmReservationsResponseDTO> Confirmations { get; private set; } =
            new Observable<ConfirmReservationsResponseDTO>() { };

        public ConcurrentDictionary<string, byte> PendingConfirmations { get; private set; } =
            new ConcurrentDictionary<string, byte>();
        public ConcurrentQueue<SlotUpdateDTO<SlotMetadata>> PendingSlotUpdates
        {
            get;
            private set;
        } = new ConcurrentQueue<SlotUpdateDTO<SlotMetadata>>();
        public ConcurrentQueue<ServerInstanceMetadata> PendingInstanceUpdates
        {
            get;
            private set;
        } = new ConcurrentQueue<ServerInstanceMetadata>();

        private bool FlushingUpdates = false;

        public ServerAgent(
            MonoBehaviour handler,
            string baseUrl,
            string authToken,
            bool acceptExpiredReservations = false,
            UpdateMode updateMode = UpdateMode.Heartbeat,
            int requestTimeoutSeconds = 10,
            float heartbeatIntervalSeconds = 10f,
            int heartbeatMaxConsecutiveErrors = 10
        )
        {
            if (handler == null)
            {
                throw new Exception("ServerAgent Handler not assigned.");
            }

            Handler = handler;

            BaseUrl = baseUrl;
            AuthToken = authToken;

            AcceptExpiredReservations = acceptExpiredReservations;
            UpdateMode = updateMode;
            RequestTimeoutSeconds = requestTimeoutSeconds;
            HeartbeatIntervalSeconds = heartbeatIntervalSeconds;
            HeartbeatMaxConsecutiveErrors = heartbeatMaxConsecutiveErrors;
        }

        public void Initialize(
            UnityAction<
                Observable<MonitorResponseDTO>,
                ObservableActionType,
                string
            > onMonitorUpdate,
            UnityAction<
                Observable<InstanceDTO<ServerInstanceMetadata, SlotMetadata>>,
                ObservableActionType,
                string
            > onInstanceUpdate,
            UnityAction<
                Observable<ConfirmReservationsResponseDTO>,
                ObservableActionType,
                string
            > onConfirmationsUpdate = null
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

            Api = new Api<ServerInstanceMetadata, SlotMetadata>(
                Handler,
                AuthToken,
                BaseUrl,
                RequestTimeoutSeconds
            );

            L.SubscribeLogger(Monitor, "SB", "Monitor");
            Monitor.Subscribe(onMonitorUpdate);

            L.SubscribeLogger(Instance, "SB", "Instance");
            Instance.Subscribe(onInstanceUpdate);

            L.SubscribeLogger(Confirmations, "SB", "Confirmations");
            if (onConfirmationsUpdate is not null)
            {
                Confirmations.Subscribe(onConfirmationsUpdate);
            }

            if (HeartbeatIntervalSeconds < RequestTimeoutSeconds)
            {
                RequestTimeoutSeconds = (int)HeartbeatIntervalSeconds;
                Monitor._Notify(
                    $"request timeout clamped to heartbeat [{RequestTimeoutSeconds}]",
                    ObservableActionType.Warn
                );
            }

            Status();
        }

        #region Agent API
        public void Status()
        {
            Api.GetMonitor(
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
                    Monitor._Error($"get monitor failed\n{error}", null);
                }
            );
        }

        public void DiscoverInstance(InstanceDTO<ServerInstanceMetadata, SlotMetadata> instance)
        {
            DiscoverInstance(instance, null);
        }

        internal void DiscoverInstance(
            InstanceDTO<ServerInstanceMetadata, SlotMetadata> instance,
            Action onFailed
        )
        {
            Api.CreateServerInstance(
                instance,
                (
                    InstanceDTO<ServerInstanceMetadata, SlotMetadata> response,
                    UnityWebRequest request
                ) =>
                {
                    Instance._Update(response, "discovered");
                    Handler.StartCoroutine(DelayedHeartbeat());
                },
                (string error, UnityWebRequest request) =>
                {
                    if (request.responseCode == 409)
                    {
                        Instance._Error("discovery duplicate");
                    }
                    else
                    {
                        Instance._Error($"discovery failed\n{error}");
                    }
                    onFailed?.Invoke();
                }
            );
        }

        public void DeleteInstance()
        {
            if (Instance.Current is null)
            {
                Instance._Update(null, "instance deleted");
                return;
            }

            Api.DeleteServerInstance(
                Instance.Current.RequestID,
                (UnityWebRequest request) =>
                {
                    Instance._Update(null, "instance deleted");
                },
                (string error, UnityWebRequest request) =>
                {
                    if (request.responseCode == 404)
                    {
                        Instance._Update(null, "instance delete failed (not found)");
                    }
                    else
                    {
                        Instance._Error($"instance delete failed\n{error}", null);
                    }
                }
            );
        }

        public void ConfirmReservation(string pending)
        {
            if (!PendingConfirmations.TryAdd(pending, 1))
            {
                Confirmations._Notify($"duplicate [{pending}]", ObservableActionType.Warn);
                return;
            }

            Confirmations._Notify($"enqueued [{pending}]");

            if (UpdateMode == UpdateMode.Greedy)
            {
                FlushConfirmations();
            }
        }

        public void UpdateSlot(SlotUpdateDTO<SlotMetadata> update)
        {
            SlotDTO<SlotMetadata> slot = Instance.Current.Slots.Find(s => s.Name == update.Name);

            if (slot is null)
            {
                Instance._Error($"slot update failed (not found) [{update.Name}]");
                return;
            }

            if (slot.AvailableSeats + update.AvailableSeats < 0)
            {
                Instance._Error($"slot update failed (not enough seats) [{update.Name}]");
                return;
            }

            PendingSlotUpdates.Enqueue(update);
            Instance._Notify($"slot update enqueued [{update.Name}]");

            if (UpdateMode == UpdateMode.Greedy)
            {
                FlushConfirmations();
            }
        }

        public void UpdateInstance(ServerInstanceMetadata metadata)
        {
            PendingInstanceUpdates.Enqueue(metadata);
            Instance._Notify($"instance update enqueued");

            if (UpdateMode == UpdateMode.Greedy)
            {
                FlushConfirmations();
            }
        }
        #endregion

        #region Internals

        internal void Heartbeat(int consecutiveErrors = 0)
        {
            if (consecutiveErrors > HeartbeatMaxConsecutiveErrors)
            {
                DeleteInstance();
                return;
            }
            else if (Instance.Current is null)
            {
                return;
            }

            Api.KeepAliveServerInstance(
                Instance.Current.RequestID,
                (KeepAliveResponseDTO keepalive, UnityWebRequest request) =>
                {
                    Instance._Notify($"heartbeat ok");
                    Handler.StartCoroutine(DelayedHeartbeat());
                    if (!FlushingUpdates)
                    {
                        FlushConfirmations();
                    }
                },
                (string error, UnityWebRequest request) =>
                {
                    if (request.responseCode == 404)
                    {
                        Rediscover();
                    }
                    else
                    {
                        Instance._Notify(
                            $"heartbeat failed [{consecutiveErrors}/{HeartbeatMaxConsecutiveErrors}]\n{error}",
                            ObservableActionType.Warn
                        );
                        Handler.StartCoroutine(DelayedHeartbeat(consecutiveErrors + 1));
                    }
                }
            );
        }

        internal IEnumerator DelayedHeartbeat(int consecutiveErrors = 0)
        {
            // jitter scales with the interval and never delays a heartbeat past it
            yield return new WaitForSeconds(HeartbeatIntervalSeconds * Random.Range(0.8f, 1f));
            Heartbeat(consecutiveErrors);
        }

        /// <summary>
        /// Rediscover an expired instance with pending slot and instance updates applied,
        /// so the service doesn't get stale seat counts. Updates are re-enqueued on failure.
        /// </summary>
        internal void Rediscover()
        {
            InstanceDTO<ServerInstanceMetadata, SlotMetadata> current = Instance.Current;
            if (current is null)
            {
                return;
            }

            List<SlotUpdateDTO<SlotMetadata>> slotUpdates = new List<SlotUpdateDTO<SlotMetadata>>();
            while (PendingSlotUpdates.TryDequeue(out SlotUpdateDTO<SlotMetadata> slotUpdate))
            {
                slotUpdates.Add(slotUpdate);
            }
            List<ServerInstanceMetadata> instanceUpdates = new List<ServerInstanceMetadata>();
            while (PendingInstanceUpdates.TryDequeue(out ServerInstanceMetadata instanceUpdate))
            {
                instanceUpdates.Add(instanceUpdate);
            }

            ServerInstanceMetadata metadata = current.Metadata;
            foreach (ServerInstanceMetadata update in instanceUpdates)
            {
                metadata = metadata is null ? update : metadata.Merge(update);
            }

            List<SlotDTO<SlotMetadata>> slots = current.Slots is null
                ? new List<SlotDTO<SlotMetadata>>()
                : new List<SlotDTO<SlotMetadata>>(current.Slots);
            foreach (SlotUpdateDTO<SlotMetadata> update in slotUpdates)
            {
                int index = slots.FindIndex(s => s.Name == update.Name);
                if (index < 0)
                {
                    Instance._Error($"slot update failed (not found) [{update.Name}]");
                    continue;
                }
                slots[index] = slots[index].ApplyUpdate(update);
            }

            DiscoverInstance(
                new InstanceDTO<ServerInstanceMetadata, SlotMetadata>()
                {
                    RequestID = current.RequestID,
                    CreatedAt = current.CreatedAt,
                    UpdatedAt = current.UpdatedAt,
                    TotalAvailableSeats = current.TotalAvailableSeats,
                    TotalJoinableSeats = current.TotalJoinableSeats,
                    TotalReservedSeats = current.TotalReservedSeats,
                    Metadata = metadata,
                    Server = current.Server,
                    Slots = slots,
                },
                () =>
                {
                    slotUpdates.ForEach(PendingSlotUpdates.Enqueue);
                    instanceUpdates.ForEach(PendingInstanceUpdates.Enqueue);
                }
            );
        }

        internal void FlushConfirmations()
        {
            if (PendingConfirmations.IsEmpty)
            {
                if (!PendingSlotUpdates.IsEmpty)
                {
                    FlushSlotUpdates();
                }
                else if (!PendingInstanceUpdates.IsEmpty)
                {
                    FlushInstanceUpdates();
                }
                return;
            }

            List<string> staged = new List<string>();
            // simulate concurrent HashSet by removing from concurrent dictionary
            foreach (var (key, _) in PendingConfirmations)
            {
                if (PendingConfirmations.TryRemove(key, out _))
                {
                    staged.Add(key);
                }
            }

            Api.ConfirmReservations(
                Instance.Current.RequestID,
                new ConfirmReservationsDTO { UserIDs = staged },
                (ConfirmReservationsResponseDTO response, UnityWebRequest request) =>
                {
                    Confirmations._Update(response, "confirmed");
                    response.Slots.ForEach(slot =>
                    {
                        int allocatedSeats = (
                            AcceptExpiredReservations
                                ? slot.AcceptedUserIDs.Count + slot.ExpiredUserIDs.Count
                                : slot.AcceptedUserIDs.Count
                        );
                        if (allocatedSeats > 0)
                        {
                            PendingSlotUpdates.Enqueue(
                                new SlotUpdateDTO<SlotMetadata>(slot.Name, allocatedSeats * -1)
                            );
                            Instance._Notify($"slot update enqueued [{slot.Name}]");
                        }
                    });
                    FlushSlotUpdates();
                },
                (string error, UnityWebRequest request) =>
                {
                    foreach (var pending in staged)
                    {
                        PendingConfirmations.TryAdd(pending, 1);
                    }
                    Confirmations._Error($"failed\n{error}");
                }
            );
        }

        internal void FlushSlotUpdates()
        {
            if (FlushingUpdates)
            {
                Instance._Notify(
                    "agent throttled concurrent slot update",
                    ObservableActionType.Warn
                );
                return;
            }

            FlushingUpdates = true;
            Dictionary<string, SlotUpdateDTO<SlotMetadata>> mergedUpdates =
                new Dictionary<string, SlotUpdateDTO<SlotMetadata>>();
            // sum of queued seat deltas per slot, re-enqueued if the update fails
            Dictionary<string, int> mergedDeltas = new Dictionary<string, int>();

            try
            {
                while (PendingSlotUpdates.TryDequeue(out SlotUpdateDTO<SlotMetadata> update))
                {
                    if (!mergedUpdates.ContainsKey(update.Name))
                    {
                        SlotDTO<SlotMetadata> slot = Instance.Current?.Slots?.Find(s =>
                            s.Name == update.Name
                        );
                        if (slot is null)
                        {
                            Instance._Error($"slot update failed (not found) [{update.Name}]");
                            continue;
                        }

                        mergedUpdates[update.Name] = new SlotUpdateDTO<SlotMetadata>(
                            slot.Name,
                            (int)slot.AvailableSeats,
                            slot.Metadata
                        );
                        mergedDeltas[update.Name] = 0;
                    }
                    SlotUpdateDTO<SlotMetadata> merged = mergedUpdates[update.Name];
                    mergedDeltas[update.Name] += update.AvailableSeats;
                    mergedUpdates[update.Name] = new SlotUpdateDTO<SlotMetadata>(
                        update.Name,
                        merged.AvailableSeats + update.AvailableSeats,
                        merged.Metadata is null
                            ? update.Metadata
                            : merged.Metadata.Merge(update.Metadata)
                    );
                }
            }
            catch (Exception e)
            {
                FlushingUpdates = false;
                Instance._Error($"slot update failed\n{e}");
                return;
            }

            ConcurrentQueue<string> updatesFinished = new ConcurrentQueue<string>();
            foreach (var update in mergedUpdates.Values)
            {
                int delta = mergedDeltas[update.Name];
                Api.UpdateSlot(
                    Instance.Current.RequestID,
                    update,
                    (SlotDTO<SlotMetadata> slot, UnityWebRequest request) =>
                    {
                        try
                        {
                            int index = Instance.Current.Slots.FindIndex(s =>
                                s.Name == update.Name
                            );
                            if (index >= 0)
                            {
                                Instance.Current.Slots[index] = slot;
                            }
                            Instance._Update(Instance.Current, $"slot updated [{update.Name}]");
                        }
                        finally
                        {
                            updatesFinished.Enqueue(update.Name);
                        }
                    },
                    (string error, UnityWebRequest request) =>
                    {
                        try
                        {
                            Instance._Error(
                                $"slot update failed, enqueuing for retry [{update.Name}]\n{error}"
                            );
                            // re-enqueue the seat delta, not the absolute seat count
                            PendingSlotUpdates.Enqueue(
                                new SlotUpdateDTO<SlotMetadata>(update.Name, delta, update.Metadata)
                            );
                        }
                        finally
                        {
                            updatesFinished.Enqueue(update.Name);
                        }
                    }
                );
            }
            Handler.StartCoroutine(
                WaitForUpdates(
                    updatesFinished,
                    mergedUpdates.Count,
                    () => FlushInstanceUpdates(continuingFlush: true)
                )
            );
        }

        internal IEnumerator WaitForUpdates(
            ConcurrentQueue<string> updates,
            int expectedCount,
            Action onComplete
        )
        {
            yield return new WaitUntil(() => updates.Count == expectedCount);
            onComplete.Invoke();
        }

        /// <summary>
        /// PATCH queued instance metadata, or refresh the instance with a GET when there's none.
        /// </summary>
        /// <param name="continuingFlush">true when called after slot updates of the same flush</param>
        internal void FlushInstanceUpdates(bool continuingFlush = false)
        {
            if (!continuingFlush)
            {
                if (FlushingUpdates)
                {
                    Instance._Notify(
                        "agent throttled concurrent instance update",
                        ObservableActionType.Warn
                    );
                    return;
                }
                FlushingUpdates = true;
            }

            try
            {
                if (PendingInstanceUpdates.IsEmpty)
                {
                    RefreshInstance();
                }
                else
                {
                    PatchInstance();
                }
            }
            catch (Exception e)
            {
                FlushingUpdates = false;
                Instance._Error($"instance update failed\n{e}");
            }
        }

        private void RefreshInstance()
        {
            Api.GetServerInstance(
                Instance.Current.RequestID,
                (
                    InstanceDTO<ServerInstanceMetadata, SlotMetadata> instance,
                    UnityWebRequest request
                ) =>
                {
                    try
                    {
                        Instance._Update(instance, "instance updated");
                    }
                    finally
                    {
                        FlushingUpdates = false;
                    }
                },
                (string error, UnityWebRequest request) =>
                {
                    try
                    {
                        Instance._Error($"instance update failed, enqueuing for retry\n{error}");
                        if (PendingInstanceUpdates.IsEmpty)
                        {
                            PendingInstanceUpdates.Enqueue(Instance.Current.Metadata);
                        }
                    }
                    finally
                    {
                        FlushingUpdates = false;
                    }
                }
            );
        }

        private void PatchInstance()
        {
            ServerInstanceMetadata mergedUpdate = Instance.Current.Metadata;
            while (PendingInstanceUpdates.TryDequeue(out ServerInstanceMetadata update))
            {
                mergedUpdate = mergedUpdate is null ? update : mergedUpdate.Merge(update);
            }

            Api.UpdateServerInstance(
                Instance.Current.RequestID,
                new InstanceUpdateDTO<ServerInstanceMetadata>() { Metadata = mergedUpdate },
                (
                    InstanceDTO<ServerInstanceMetadata, SlotMetadata> response,
                    UnityWebRequest request
                ) =>
                {
                    try
                    {
                        response.Slots = Instance.Current.Slots; // patch doesn't return slots in SB 1.0.0
                        Instance._Update(response, "instance updated");
                    }
                    finally
                    {
                        FlushingUpdates = false;
                    }
                },
                (string error, UnityWebRequest request) =>
                {
                    try
                    {
                        Instance._Error($"instance update failed, enqueuing for retry\n{error}");
                        PendingInstanceUpdates.Enqueue(mergedUpdate);
                    }
                    finally
                    {
                        FlushingUpdates = false;
                    }
                }
            );
        }
        #endregion
    }

    public enum UpdateMode
    {
        Heartbeat,
        Greedy,
    }
}
