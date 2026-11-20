using System;
using System.Collections;
using System.Linq;
using Edgegap;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace EdgegapSdkTests
{
    /// <summary>
    /// SafeHttpRequest and Api fixes: #76 #82 #83 #84 #86 #87.
    /// </summary>
    public class HttpTests : SdkTestBase
    {
        private int Successes;
        private int Errors;
        private string LastError;

        // NUnit reuses the fixture instance across tests
        [SetUp]
        public void ResetState()
        {
            Successes = 0;
            Errors = 0;
            LastError = null;
        }

        private void Send(SafeHttpRequest http, string path, RetryParameters retry = null)
        {
            http.Get(
                $"{Server.BaseUrl}{path}",
                "token",
                (string body, UnityWebRequest req) => Successes++,
                (string error, UnityWebRequest req) =>
                {
                    Errors++;
                    LastError = error;
                },
                retry
            );
        }

        [UnityTest]
        public IEnumerator FirstRequestIsNotDelayed_87()
        {
            Server.Handler = _ => MockServer.Response.Json("{}");
            float start = Time.realtimeSinceStartup;
            Send(new SafeHttpRequest(Runner), "/ok", new RetryParameters { MaxAttempts = 10 });
            yield return WaitFor(() => Successes == 1, 5, "success");
            Assert.Less(Time.realtimeSinceStartup - start, 0.6f, "first attempt waited a backoff");
        }

        [UnityTest]
        public IEnumerator InitializerMaxAttemptsRetriesThatManyTimes_87()
        {
            Server.Handler = _ => MockServer.Response.Json("{}", 500);
            Send(
                new SafeHttpRequest(Runner),
                "/fail",
                new RetryParameters { MaxAttempts = 5, BackoffSeconds = () => 0.05f }
            );
            yield return WaitFor(() => Errors == 1, 10, "error after retries");
            Assert.AreEqual(6, Server.RequestsTo("GET", "/fail").Count, "1 attempt + 5 retries");
        }

        [UnityTest]
        public IEnumerator CustomBackoffIsUsed_82()
        {
            int calls = 0;
            Server.Handler = _ => MockServer.Response.Json("{}", ++calls < 3 ? 503 : 200);
            float start = Time.realtimeSinceStartup;
            Send(new SafeHttpRequest(Runner), "/flaky", new RetryParameters(3, () => 0.05f));
            yield return WaitFor(() => Successes == 1, 5, "success after 2 retries");
            Assert.AreEqual(3, Server.RequestsTo("GET", "/flaky").Count);
            Assert.Less(Time.realtimeSinceStartup - start, 1.0f, "default 1s backoff used instead");
        }

        [UnityTest]
        public IEnumerator RetryAfterSecondsIsHonoredForNextAttemptOnly_82()
        {
            int calls = 0;
            Server.Handler = _ =>
            {
                calls++;
                var res = MockServer.Response.Json("{}", calls < 3 ? 429 : 200);
                if (calls == 1)
                {
                    res.Headers["Retry-After"] = "1";
                }
                return res;
            };
            Send(new SafeHttpRequest(Runner), "/limited", new RetryParameters(3, () => 0.05f));
            yield return WaitFor(() => Successes == 1, 6, "success");
            var reqs = Server.RequestsTo("GET", "/limited");
            Assert.AreEqual(3, reqs.Count);
            double firstGap = (reqs[1].ReceivedAt - reqs[0].ReceivedAt).TotalSeconds;
            double secondGap = (reqs[2].ReceivedAt - reqs[1].ReceivedAt).TotalSeconds;
            Assert.GreaterOrEqual(firstGap, 0.95, "retried before Retry-After");
            Assert.Less(secondGap, 0.6, "Retry-After stuck for later retries");
        }

        [UnityTest]
        public IEnumerator RetryAfterHttpDateIsParsed_86()
        {
            int calls = 0;
            Server.Handler = _ =>
            {
                calls++;
                var res = MockServer.Response.Json("{}", calls == 1 ? 503 : 200);
                if (calls == 1)
                {
                    res.Headers["Retry-After"] = DateTime.UtcNow.AddSeconds(3).ToString("R");
                }
                return res;
            };
            Send(new SafeHttpRequest(Runner), "/date", new RetryParameters(3, () => 0.05f));
            yield return WaitFor(() => Successes == 1, 8, "success");
            var reqs = Server.RequestsTo("GET", "/date");
            double gap = (reqs[1].ReceivedAt - reqs[0].ReceivedAt).TotalSeconds;
            // HTTP-date has 1s resolution, so the wait is 2-3s (x1.0-1.2 jitter)
            Assert.GreaterOrEqual(gap, 1.9, "HTTP-date Retry-After treated as 0");
        }

        [UnityTest]
        public IEnumerator NetworkFailuresAreRetried_84()
        {
            int closedPort = MockServer.FreePort();
            var http = new SafeHttpRequest(Runner, 1);
            http.Get(
                $"http://localhost:{closedPort}/nothing",
                "token",
                (string body, UnityWebRequest req) => Successes++,
                (string error, UnityWebRequest req) => Errors++,
                new RetryParameters(2, () => 0.05f)
            );
            yield return WaitFor(() => Errors == 1, 10, "error after retries");
            Assert.AreEqual(2, LogsContaining("Retrying").Count, AllLogs());
        }

        [UnityTest]
        public IEnumerator MatchmakingApiHonorsRequestTimeout_83()
        {
            Server.Handler = _ => new MockServer.Response { Body = "{}", DelayMs = 6000 };
            var api = new Edgegap.Matchmaking.Api(Runner, "token", Server.BaseUrl, 1);
            float start = Time.realtimeSinceStartup;
            float? elapsed = null;
            api.GetMonitor(
                (monitor, req) => elapsed = -1,
                (error, req) => elapsed = Time.realtimeSinceStartup - start
            );
            // 4 attempts x 1s timeout + 3 x ~1s backoff ~= 7s, with the old 3s default ~= 15s
            yield return WaitFor(() => elapsed.HasValue, 14, "timeout error");
            Assert.Less(elapsed.Value, 10f, "timeout not applied");
        }

        [UnityTest]
        public IEnumerator ParseFailureCallsErrorDelegate_76()
        {
            Server.Handler = _ => MockServer.Response.Json("this is not json");
            var api = new Edgegap.Matchmaking.Api(Runner, "token", Server.BaseUrl);
            string error = null;
            bool success = false;
            api.GetMonitor((monitor, req) => success = true, (e, req) => error = e);
            yield return WaitFor(() => error != null || success, 5, "a callback");
            Assert.IsFalse(success);
            StringAssert.Contains("Couldn't parse monitor", error);
        }
    }
}
