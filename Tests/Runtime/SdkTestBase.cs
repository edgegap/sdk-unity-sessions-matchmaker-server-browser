using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EdgegapSdkTests
{
    public class TestRunnerBehaviour : MonoBehaviour { }

    /// <summary>
    /// Allows plain HTTP to the localhost mock server while the tests run, then restores the setting.
    /// </summary>
    public class AllowLocalHttp : IPrebuildSetup, IPostBuildCleanup
    {
#if UNITY_EDITOR
        private const string Key = "EdgegapSdkTests.InsecureHttpOption";
#endif

        public void Setup()
        {
#if UNITY_EDITOR
            UnityEditor.SessionState.SetInt(
                Key,
                (int)UnityEditor.PlayerSettings.insecureHttpOption
            );
            UnityEditor.PlayerSettings.insecureHttpOption = UnityEditor
                .InsecureHttpOption
                .AlwaysAllowed;
#endif
        }

        public void Cleanup()
        {
#if UNITY_EDITOR
            UnityEditor.PlayerSettings.insecureHttpOption = (UnityEditor.InsecureHttpOption)
                UnityEditor.SessionState.GetInt(Key, 0);
#endif
        }
    }

    [PrebuildSetup(typeof(AllowLocalHttp))]
    [PostBuildCleanup(typeof(AllowLocalHttp))]
    public abstract class SdkTestBase
    {
        protected MockServer Server;
        protected MonoBehaviour Runner;
        protected List<string> Logs;
        private GameObject RunnerObject;

        [SetUp]
        public void BaseSetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            Logs = new List<string>();
            Application.logMessageReceived += OnLog;
            Server = new MockServer();
            RunnerObject = new GameObject("EdgegapSdkTestRunner");
            Runner = RunnerObject.AddComponent<TestRunnerBehaviour>();
        }

        [TearDown]
        public void BaseTearDown()
        {
            Application.logMessageReceived -= OnLog;
            if (Runner != null)
            {
                Runner.StopAllCoroutines();
            }
            UnityEngine.Object.Destroy(RunnerObject);
            Server.Dispose();
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (Logs)
            {
                Logs.Add($"[{type}] {condition}");
            }
        }

        protected List<string> LogsContaining(string text)
        {
            lock (Logs)
            {
                return Logs.Where(l => l.Contains(text)).ToList();
            }
        }

        protected string AllLogs()
        {
            lock (Logs)
            {
                return string.Join("\n", Logs);
            }
        }

        /// <summary>
        /// Yield until the condition holds, fail with the message after the timeout.
        /// </summary>
        protected IEnumerator WaitFor(Func<bool> condition, float timeoutSeconds, string message)
        {
            // the test body runs in its own log scope, SDK error logs are expected
            LogAssert.ignoreFailingMessages = true;
            float start = Time.realtimeSinceStartup;
            while (!condition())
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    Assert.Fail(
                        $"Timed out after {timeoutSeconds}s: {message}\nLogs:\n{AllLogs()}"
                    );
                }
                yield return null;
            }
        }

        protected IEnumerator Wait(float seconds)
        {
            LogAssert.ignoreFailingMessages = true;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                yield return null;
            }
        }
    }
}
