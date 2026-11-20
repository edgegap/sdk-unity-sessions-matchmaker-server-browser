using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Edgegap.ServerBrowser;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace EdgegapSdkTests
{
    /// <summary>
    /// Server Browser ServerAgent fixes: #77 #101 #102 #103 #104 #106.
    /// </summary>
    public class ServerBrowserServerTests : SdkTestBase
    {
        private JObject StoredInstance;
        private int FailNextSlotPatches;
        private int NotFoundNextHeartbeats;

        // NUnit reuses the fixture instance across tests
        [SetUp]
        public void ResetState()
        {
            StoredInstance = null;
            FailNextSlotPatches = 0;
            NotFoundNextHeartbeats = 0;
        }

        private ServerAgent<CompetitiveInstanceMetadataDTO, CompetitiveSlotMetadataDTO> Agent;

        private JObject Slot(string name) =>
            (JObject)((JArray)StoredInstance["slots"]).First(s => (string)s["name"] == name);

        private MockServer.Response Handle(MockServer.Request req)
        {
            string p = req.Path;
            if (p == "/monitor")
            {
                return MockServer.Response.Json("{\"status\":\"healthy\"}");
            }
            if (req.Method == "POST" && p == "/server-instances")
            {
                StoredInstance = JObject.Parse(req.Body);
                return MockServer.Response.Json(StoredInstance.ToString());
            }
            if (req.Method == "POST" && p.EndsWith("/keep-alive"))
            {
                if (NotFoundNextHeartbeats > 0 || StoredInstance is null)
                {
                    NotFoundNextHeartbeats--;
                    StoredInstance = null;
                    return MockServer.Response.Json("{\"message\":\"not found\"}", 404);
                }
                return MockServer.Response.Json(
                    $"{{\"expires_at\":\"{DateTime.UtcNow.AddSeconds(30):o}\"}}"
                );
            }
            if (req.Method == "POST" && p.EndsWith("/reservations/confirm"))
            {
                var ids = (JArray)JObject.Parse(req.Body)["user_ids"];
                var res = new JObject
                {
                    ["unknown_user_ids"] = new JArray(),
                    ["slots"] = new JArray(
                        new JObject
                        {
                            ["name"] = "default",
                            ["accepted_user_ids"] = ids,
                            ["expired_user_ids"] = new JArray(),
                        }
                    ),
                };
                return MockServer.Response.Json(res.ToString());
            }
            if (req.Method == "PATCH" && p.Contains("/slots/"))
            {
                if (FailNextSlotPatches > 0)
                {
                    FailNextSlotPatches--;
                    return MockServer.Response.Json("{\"message\":\"bad request\"}", 400);
                }
                string name = p.Substring(p.LastIndexOf('/') + 1);
                var body = JObject.Parse(req.Body);
                var slot = Slot(name);
                slot["available_seats"] = body["available_seats"];
                if (body["metadata"] is JObject meta)
                {
                    slot["metadata"] = meta;
                }
                return MockServer.Response.Json(slot.ToString());
            }
            if (req.Method == "PATCH" && p.StartsWith("/server-instances/"))
            {
                StoredInstance["metadata"] = JObject.Parse(req.Body)["metadata"];
                var res = (JObject)StoredInstance.DeepClone();
                res.Remove("slots");
                return MockServer.Response.Json(res.ToString());
            }
            if (req.Method == "GET" && p.StartsWith("/server-instances/"))
            {
                return MockServer.Response.Json(StoredInstance.ToString());
            }
            return MockServer.Response.Json("{\"message\":\"unexpected\"}", 418);
        }

        private IEnumerator StartAgent(UpdateMode mode = UpdateMode.Heartbeat)
        {
            Server.Handler = Handle;
            Agent = new ServerAgent<CompetitiveInstanceMetadataDTO, CompetitiveSlotMetadataDTO>(
                Runner,
                Server.BaseUrl,
                "token",
                updateMode: mode,
                heartbeatIntervalSeconds: 1f
            );
            Agent.Initialize((o, t, m) => { }, (o, t, m) => { });
            Agent.DiscoverInstance(
                new InstanceDTO<CompetitiveInstanceMetadataDTO, CompetitiveSlotMetadataDTO>()
                {
                    RequestID = "req-1",
                    Metadata = new CompetitiveInstanceMetadataDTO
                    {
                        Name = "server",
                        MaxPlayers = 10,
                        IsRanked = true,
                    },
                    Slots = new List<SlotDTO<CompetitiveSlotMetadataDTO>>
                    {
                        new SlotDTO<CompetitiveSlotMetadataDTO>
                        {
                            Name = "default",
                            AvailableSeats = 10,
                            Metadata = new CompetitiveSlotMetadataDTO
                            {
                                MaxPlayers = 10,
                                AvgRank = 1000,
                            },
                        },
                    },
                }
            );
            yield return WaitFor(
                () => Agent.Instance.Current?.RequestID == "req-1",
                5,
                "discovered"
            );
        }

        private int ServerSeats => (int)Slot("default")["available_seats"];

        [UnityTest]
        public IEnumerator ConfirmReservationsWithCustomSlotMetadata_102_104()
        {
            yield return StartAgent();
            Agent.ConfirmReservation("user-1");
            yield return WaitFor(() => ServerSeats == 9, 6, "seat deducted after confirmation");
            Assert.AreEqual(
                10,
                (int)Slot("default")["metadata"]["max_players"],
                "slot metadata lost"
            );
            Assert.AreEqual(
                1000,
                (int)Slot("default")["metadata"]["avg_rank"],
                "slot metadata lost"
            );

            // a second confirmation must not be blocked by a stuck flush
            Agent.ConfirmReservation("user-2");
            yield return WaitFor(() => ServerSeats == 8, 6, "second confirmation flushed");
        }

        [UnityTest]
        public IEnumerator FailedSlotUpdateRetriesTheDelta_103()
        {
            yield return StartAgent(UpdateMode.Greedy);
            FailNextSlotPatches = 1;
            Agent.UpdateSlot(new SlotUpdateDTO<CompetitiveSlotMetadataDTO>("default", -1));
            yield return WaitFor(
                () => Server.RequestsTo("PATCH", "/server-instances/req-1/slots").Count >= 2,
                8,
                "retried slot update"
            );
            yield return WaitFor(() => ServerSeats != 10, 3, "seats updated");
            Assert.AreEqual(9, ServerSeats, "retry double counted seats");
            yield return Wait(1.5f);
            Assert.AreEqual(9, ServerSeats);
        }

        [UnityTest]
        public IEnumerator InstanceMetadataIsSentWithoutSlotUpdate_101_77()
        {
            yield return StartAgent(UpdateMode.Greedy);
            Agent.UpdateInstance(new CompetitiveInstanceMetadataDTO { Name = "renamed" });
            yield return WaitFor(
                () => (string)StoredInstance["metadata"]["name"] == "renamed",
                5,
                "instance metadata PATCHed"
            );
            Assert.AreEqual(10, (int)StoredInstance["metadata"]["max_players"], "#77 int reset");
            Assert.AreEqual(true, (bool)StoredInstance["metadata"]["is_ranked"], "#77 bool reset");
        }

        [UnityTest]
        public IEnumerator RediscoveryAppliesPendingUpdates_106()
        {
            yield return StartAgent();
            yield return WaitFor(
                () => Server.RequestsTo("POST", "/server-instances/req-1/keep-alive").Count >= 1,
                4,
                "first heartbeat"
            );
            NotFoundNextHeartbeats = 1;
            Agent.UpdateSlot(new SlotUpdateDTO<CompetitiveSlotMetadataDTO>("default", -2));
            yield return WaitFor(
                () =>
                    Server
                        .RequestsTo("POST", "/server-instances")
                        .Count(r => r.Path == "/server-instances") == 2,
                6,
                "rediscovery"
            );
            var rediscovery = Server
                .RequestsTo("POST", "/server-instances")
                .Last(r => r.Path == "/server-instances");
            int seats = (int)JObject.Parse(rediscovery.Body)["slots"][0]["available_seats"];
            Assert.AreEqual(8, seats, "rediscovered with stale seats");
            yield return Wait(2.5f);
            Assert.AreEqual(8, ServerSeats, "pending update applied twice after rediscovery");
        }
    }
}
