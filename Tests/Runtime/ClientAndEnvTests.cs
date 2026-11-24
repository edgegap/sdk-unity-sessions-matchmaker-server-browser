using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Edgegap;
using Edgegap.Matchmaking;
using Edgegap.ServerBrowser;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace EdgegapSdkTests
{
    /// <summary>
    /// Server Browser ClientAgent fixes #75 #98 #99 #100 #107, environment fixes #96 #97.
    /// </summary>
    public class ClientAndEnvTests : SdkTestBase
    {
        private ClientAgent<SimpleInstanceMetadataDTO, SimpleSlotMetadataDTO> Client;
        private List<string> Events;

        private const string InstanceJson =
            "{\"request_id\":\"r1\",\"metadata\":{\"name\":\"a\"},\"slots\":[{\"name\":\"default\",\"available_seats\":3,\"metadata\":{}}]}";

        private void StartClient()
        {
            Events = new List<string>();
            Server.Handler = req =>
            {
                if (req.Path == "/monitor")
                {
                    return MockServer.Response.Json("{\"status\":\"healthy\"}");
                }
                if (req.Method == "GET" && req.Path == "/server-instances")
                {
                    return MockServer.Response.Json(
                        $"{{\"items\":[{InstanceJson}],\"pagination\":{{\"page_size\":20,\"count\":1,\"next_cursor\":null}}}}"
                    );
                }
                if (req.Method == "GET" && req.Path == "/server-instances/r1")
                {
                    return MockServer.Response.Json(InstanceJson);
                }
                return MockServer.Response.Json("{}", 418);
            };
            Client = new ClientAgent<SimpleInstanceMetadataDTO, SimpleSlotMetadataDTO>(
                Runner,
                Server.BaseUrl,
                "token"
            );
            Client.Initialize((o, t, m) => { }, (o, t, m) => Events.Add($"{t}:{m}"));
        }

        [UnityTest]
        public IEnumerator NextPagePastLastPageSendsNothing_98()
        {
            StartClient();
            Client.ListInstances(null, "", 20, null);
            yield return WaitFor(
                () => Client.Instances.Current?.ServerInstances.Count == 1,
                5,
                "listed"
            );
            Client.GetNextPage();
            yield return Wait(1f);
            Assert.AreEqual(
                1,
                Server
                    .RequestsTo("GET", "/server-instances")
                    .Count(r => r.Path == "/server-instances")
            );
            Assert.AreEqual(
                1,
                Client.Instances.Current.ServerInstances.Count,
                "first page appended again"
            );
            Assert.IsTrue(Events.Any(e => e.Contains("last page reached")));

            Client.Instances._Update(null, "cache deleted");
            Assert.DoesNotThrow(() => Client.GetNextPage(), "#98 empty cache");
        }

        [UnityTest]
        public IEnumerator InstanceDetailsWithEmptyCacheAndCopies_99_100()
        {
            StartClient();
            Client.Instances._Update(null, "cache deleted");
            Client.GetInstanceDetails("r1");
            yield return WaitFor(
                () => Client.Instances.Current?.ServerInstances?.Count == 1,
                5,
                "#99 details cached"
            );

            Client.GetInstanceDetails("r1");
            yield return WaitFor(
                () => Events.Count(e => e.Contains("instance details retrieved")) == 1,
                5,
                "second details"
            );
            Assert.AreNotSame(
                Client.Instances.Previous,
                Client.Instances.Current,
                "#100 same list emitted"
            );
            Assert.AreEqual(1, Client.Instances.Current.ServerInstances.Count);
        }

        [UnityTest]
        public IEnumerator FilterValuesAreEscapedAndEncoded_75_107()
        {
            StartClient();
            var filter = new FilterCompiler(
                new List<FilterBase>
                {
                    new StringFilter
                    {
                        Field = "metadata.name",
                        Operator = StringOperator._Equals,
                        Value = "Bob's a&b #1+2",
                    },
                }
            );
            Client.ListInstances(filter, "metadata.name asc", 20, "cursor/+=");
            yield return WaitFor(
                () => Server.Requests.Any(r => r.Path == "/server-instances"),
                5,
                "list request"
            );
            var raw = Server.Requests.First(r => r.Path == "/server-instances").RawQuery;
            var query = raw.Split('&')
                .Select(p => p.Split(new[] { '=' }, 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Assert.AreEqual("metadata.name eq 'Bob''s a&b #1+2'", query["$filter"], raw);
            Assert.AreEqual("metadata.name asc", query["$orderby"], raw);
            Assert.AreEqual("cursor/+=", query["cursor"], raw);
        }

        [Test]
        public void EmptyEnvironmentWarnsInsteadOfThrowing_96()
        {
            DeploymentEnvironmentDTO env = null;
            Assert.DoesNotThrow(() =>
                env = new DeploymentEnvironmentDTO(new System.Collections.Hashtable())
            );
            Assert.IsNotNull(env.PortMapping);
            var warning = LogsContaining("Missing variables");
            Assert.AreEqual(1, warning.Count, AllLogs());
            StringAssert.Contains("ARBITRIUM_PORTS_MAPPING", warning[0]);
        }

        [Test]
        public void MalformedTicketIsSkipped_97()
        {
            var env = new System.Collections.Hashtable
            {
                ["MM_TICKET_good"] = "{\"id\":\"good\",\"group_id\":\"g\",\"attributes\":{}}",
                ["MM_TICKET_bad"] = "{not json",
            };
            MatchEnvironmentDTO<JObject> match = null;
            Assert.DoesNotThrow(() => match = new MatchEnvironmentDTO<JObject>(env));
            CollectionAssert.AreEqual(new[] { "good" }, match.Tickets.Keys.ToArray());
        }
    }
}
