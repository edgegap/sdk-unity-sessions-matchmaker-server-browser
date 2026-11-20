using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Edgegap;
using Edgegap.Matchmaking;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace EdgegapSdkTests
{
    /// <summary>
    /// GroupClient fixes #88 #89 #90 #91, backfill ServerAgent fixes #92 #93 #94.
    /// </summary>
    public class MatchmakingTests : SdkTestBase
    {
        private int GroupCounter;
        private string PollStatus = "SEARCHING";
        private int DeleteStatus = 204;

        // NUnit reuses the fixture instance across tests
        [SetUp]
        public void ResetState()
        {
            GroupCounter = 0;
            PollStatus = "SEARCHING";
            DeleteStatus = 204;
            BackfillCounter = 0;
            AssignNext.Clear();
        }

        private MockServer.Response HandleGroups(MockServer.Request req)
        {
            if (req.Path == "/monitor")
            {
                return MockServer.Response.Json("{\"status\":\"HEALTHY\",\"message\":\"ok\"}");
            }
            if (req.Method == "POST" && req.Path == "/groups")
            {
                GroupCounter++;
                return MockServer.Response.Json(
                    $"{{\"group_id\":\"g{GroupCounter}\",\"member_id\":\"m{GroupCounter}\",\"status\":\"SEARCHING\",\"is_ready\":true}}"
                );
            }
            if (req.Method == "GET" && req.Path.StartsWith("/groups/"))
            {
                string[] parts = req.Path.Split('/');
                return MockServer.Response.Json(
                    $"{{\"group_id\":\"{parts[2]}\",\"member_id\":\"{parts[4]}\",\"status\":\"{PollStatus}\",\"is_ready\":true}}"
                );
            }
            if (req.Method == "DELETE" && req.Path.StartsWith("/groups/"))
            {
                return MockServer.Response.Json(DeleteStatus == 204 ? "" : "{}", DeleteStatus);
            }
            return MockServer.Response.Json("{}", 418);
        }

        private GroupClient<SimpleGroupUpRequestDTO, LatenciesAttributesDTO> Client;
        private List<string> GroupEvents;

        private void StartClient(float pollingBackoff = 1f, float removeGroupSeconds = 30f)
        {
            Server.Handler = HandleGroups;
            GroupEvents = new List<string>();
            Client = new GroupClient<SimpleGroupUpRequestDTO, LatenciesAttributesDTO>(
                Runner,
                Server.BaseUrl,
                "token",
                pollingBackoffSeconds: pollingBackoff,
                removeGroupSeconds: removeGroupSeconds
            );
            Client.Initialize((o, t, m) => { }, (o, t, m) => GroupEvents.Add($"{t}:{m}"));
        }

        private SimpleGroupUpRequestDTO Request() =>
            new SimpleGroupUpRequestDTO(new Dictionary<string, float> { ["Montreal"] = 10f }, true);

        [UnityTest]
        public IEnumerator CreateGroupFirstCallHasNoConflict_88()
        {
            StartClient();
            Client.CreateGroup(Request());
            yield return WaitFor(() => Client.Group.Current?.GroupID == "g1", 5, "group created");
            Assert.IsFalse(
                GroupEvents.Any(e => e.Contains("conflict")),
                string.Join("\n", GroupEvents)
            );
        }

        [UnityTest]
        public IEnumerator RestartRunsOnlyOnePollingLoop_89()
        {
            StartClient(pollingBackoff: 0.5f);
            Client.CreateGroup(Request());
            yield return WaitFor(() => Client.Group.Current?.GroupID == "g1", 5, "group created");
            yield return Wait(0.2f);
            Client.CreateGroup(Request(), abandon: true);
            yield return WaitFor(() => Client.Group.Current?.GroupID == "g2", 5, "second group");
            int before = Server.RequestsTo("GET", "/groups/").Count;
            yield return Wait(3f);
            int polls = Server.RequestsTo("GET", "/groups/").Count - before;
            // one loop at 0.5s x (0.8-1.2) polls ~5-7 times in 3s, two loops ~10+
            Assert.LessOrEqual(polls, 8, "more than one polling loop");
            Assert.GreaterOrEqual(polls, 3, "polling stopped");
            Assert.IsTrue(
                Server
                    .RequestsTo("GET", "/groups/")
                    .Skip(before)
                    .All(r => r.Path.StartsWith("/groups/g2/")),
                "still polling the abandoned group"
            );
        }

        [UnityTest]
        public IEnumerator StaleExpiryDoesNotRemoveNewGroup_90()
        {
            StartClient(pollingBackoff: 0.3f, removeGroupSeconds: 1.5f);
            Client.CreateGroup(Request());
            PollStatus = "HOST_ASSIGNED";
            yield return WaitFor(
                () => Client.Group.Current?.Status == "HOST_ASSIGNED",
                5,
                "assigned"
            );
            PollStatus = "SEARCHING";
            Client.CreateGroup(Request(), abandon: true);
            yield return WaitFor(() => Client.Group.Current?.GroupID == "g2", 5, "new group");
            yield return Wait(2f);
            Assert.AreEqual(
                "g2",
                Client.Group.Current?.GroupID,
                "stale expiry removed the new group"
            );
        }

        [UnityTest]
        public IEnumerator AbandonConflictReportsSkippedRestart_91()
        {
            StartClient();
            Client.CreateGroup(Request());
            yield return WaitFor(() => Client.Group.Current?.GroupID == "g1", 5, "group created");
            DeleteStatus = 409;
            Client.CreateGroup(Request(), abandon: true);
            yield return WaitFor(
                () => GroupEvents.Any(e => e.Contains("restart skipped (match found)")),
                5,
                "restart skipped warning"
            );
            Assert.AreEqual("g1", Client.Group.Current?.GroupID, "matched group must be kept");
        }

        // ---------------- backfill ----------------

        private int BackfillCounter;
        private readonly HashSet<string> AssignNext = new HashSet<string>();

        private MockServer.Response HandleBackfills(MockServer.Request req)
        {
            if (req.Path == "/monitor")
            {
                return MockServer.Response.Json("{\"status\":\"HEALTHY\",\"message\":\"ok\"}");
            }
            if (req.Method == "POST" && req.Path == "/backfills")
            {
                BackfillCounter++;
                return new MockServer.Response
                {
                    Body =
                        $"{{\"id\":\"b{BackfillCounter}\",\"profile\":\"p\",\"status\":\"SEARCHING\",\"tickets\":{{}}}}",
                    DelayMs = 500,
                };
            }
            if (req.Method == "GET" && req.Path.StartsWith("/backfills/"))
            {
                string id = req.Path.Substring("/backfills/".Length);
                if (AssignNext.Contains(id))
                {
                    return MockServer.Response.Json(
                        $"{{\"id\":\"{id}\",\"profile\":\"p\",\"status\":\"ASSIGNED\",\"tickets\":{{}},\"assigned_ticket\":{{\"id\":\"t-{id}\",\"group_id\":\"g\",\"attributes\":{{}}}}}}"
                    );
                }
                return MockServer.Response.Json(
                    $"{{\"id\":\"{id}\",\"profile\":\"p\",\"status\":\"SEARCHING\",\"tickets\":{{}}}}"
                );
            }
            if (req.Method == "DELETE")
            {
                return MockServer.Response.Json("", 204);
            }
            return MockServer.Response.Json("{}", 418);
        }

        private ServerAgent<JObject> Backfill;
        private List<string> BackfillEvents;
        private List<bool> AssignedUpdateDiffered;

        private void StartBackfill(int targetTeamSize, params string[] ticketIDs)
        {
            Server.Handler = HandleBackfills;
            BackfillEvents = new List<string>();
            AssignedUpdateDiffered = new List<bool>();
            Backfill = new ServerAgent<JObject>(
                Runner,
                Server.BaseUrl,
                "token",
                "p",
                new BackfillAttributes(new BackfillAssignment { RequestID = "req-1" }),
                targetTeamSize,
                pollingBackoffSeconds: 0.3f
            );
            var tickets = ticketIDs.ToDictionary(
                id => id,
                id => new InjectedTicketDTO<JObject>
                {
                    ID = id,
                    GroupID = "g",
                    Attributes = new JObject(),
                }
            );
            Backfill.Initialize(
                tickets,
                (o, t, m) => { },
                (o, t, m) =>
                {
                    BackfillEvents.Add($"{t}:{m}");
                    if (m.StartsWith("assigned"))
                    {
                        AssignedUpdateDiffered.Add(
                            !ReferenceEquals(o.Previous, o.Current)
                                && o.Current.Values.Any(b => b.Status == "ASSIGNED")
                        );
                    }
                }
            );
        }

        [UnityTest]
        public IEnumerator QuickAbandonsDoNotOverProvision_92()
        {
            StartBackfill(2, "t1", "t2");
            Backfill.AbandonPlayer("t1");
            Backfill.AbandonPlayer("t2");
            yield return Wait(2f);
            Assert.AreEqual(
                2,
                Server.RequestsTo("POST", "/backfills").Count,
                "backfills created for 2 open seats"
            );
            Assert.AreEqual(2, Backfill.Backfills.Current.Count);
        }

        [UnityTest]
        public IEnumerator AssignedUpdateAndPollingStops_93_94()
        {
            StartBackfill(1, "t1");
            Backfill.AbandonPlayer("t1");
            yield return WaitFor(
                () => Backfill.Backfills.Current.Count == 1,
                5,
                "backfill created"
            );
            AssignNext.Add("b1");
            yield return WaitFor(
                () => Backfill.Backfills.Current.Count == 0,
                5,
                "backfill assigned and removed"
            );
            CollectionAssert.AreEqual(
                new[] { true },
                AssignedUpdateDiffered,
                "#93 assigned update"
            );
            yield return WaitFor(
                () => BackfillEvents.Any(e => e.Contains("polling stopped")),
                3,
                "#94 polling stopped"
            );
            int polls = Server.RequestsTo("GET", "/backfills/").Count;
            yield return Wait(1.5f);
            Assert.AreEqual(
                polls,
                Server.RequestsTo("GET", "/backfills/").Count,
                "#94 still polling"
            );
        }
    }
}
