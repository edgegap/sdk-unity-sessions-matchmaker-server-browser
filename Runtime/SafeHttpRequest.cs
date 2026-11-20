using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]

namespace Edgegap
{
    using L = Logger;

    public class SafeHttpRequest
    {
        internal MonoBehaviour Parent;
        internal int TimeoutSeconds = 3;

        public SafeHttpRequest(MonoBehaviour parent, int timeoutSeconds = 0)
        {
            Parent = parent;
            TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : TimeoutSeconds;
        }

        public void Post(
            string url,
            string authToken,
            string body,
            Action<string, UnityWebRequest> onSuccessDelegate,
            Action<string, UnityWebRequest> onErrorDelegate,
            RetryParameters retryParameters = null
        )
        {
#if UNITY_2022_3_OR_NEWER
            UnityWebRequest request = UnityWebRequest.Post(url, body, "application/json");
#else
            // UnityWebRequest.Post(url, string) form-urlencodes the body before 2022.3,
            // so send the JSON as raw bytes instead
            UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/json");
#endif
            ProcessRetryableRequest(
                request,
                authToken,
                onSuccessDelegate,
                onErrorDelegate,
                (string error, UnityWebRequest req, RetryParameters retry) =>
                {
                    Post(url, authToken, body, onSuccessDelegate, onErrorDelegate, retry);
                },
                retryParameters
            );
        }

        public void Get(
            string url,
            string authToken,
            Action<string, UnityWebRequest> onSuccessDelegate,
            Action<string, UnityWebRequest> onErrorDelegate,
            RetryParameters retryParameters = null
        )
        {
            UnityWebRequest request = UnityWebRequest.Get(url);
            ProcessRetryableRequest(
                request,
                authToken,
                onSuccessDelegate,
                onErrorDelegate,
                (string error, UnityWebRequest req, RetryParameters retry) =>
                {
                    Get(url, authToken, onSuccessDelegate, onErrorDelegate, retry);
                },
                retryParameters
            );
        }

        public void Delete(
            string url,
            string authToken,
            Action<string, UnityWebRequest> onSuccessDelegate,
            Action<string, UnityWebRequest> onErrorDelegate,
            RetryParameters retryParameters = null
        )
        {
            UnityWebRequest request = UnityWebRequest.Delete(url);
            ProcessRetryableRequest(
                request,
                authToken,
                onSuccessDelegate,
                onErrorDelegate,
                (string error, UnityWebRequest req, RetryParameters retry) =>
                {
                    Delete(url, authToken, onSuccessDelegate, onErrorDelegate, retry);
                },
                retryParameters
            );
        }

        public void Patch(
            string url,
            string authToken,
            string body,
            Action<string, UnityWebRequest> onSuccessDelegate,
            Action<string, UnityWebRequest> onErrorDelegate,
            RetryParameters retryParameters = null
        )
        {
            UnityWebRequest request = UnityWebRequest.Put(url, body);
            // hack for UnityWebRequest not supporting PATCH
            request.method = "PATCH";

            ProcessRetryableRequest(
                request,
                authToken,
                onSuccessDelegate,
                onErrorDelegate,
                (string error, UnityWebRequest req, RetryParameters retry) =>
                {
                    Patch(url, authToken, body, onSuccessDelegate, onErrorDelegate, retry);
                },
                retryParameters
            );
        }

        public void ProcessRetryableRequest(
            UnityWebRequest request,
            string authToken,
            Action<string, UnityWebRequest> onSuccessDelegate,
            Action<string, UnityWebRequest> onErrorDelegate,
            Action<string, UnityWebRequest, RetryParameters> onRetryableDelegate,
            RetryParameters retryParameters = null
        )
        {
            request.SetRequestHeader("Authorization", authToken);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = TimeoutSeconds;
            retryParameters ??= new RetryParameters();

            Parent.StartCoroutine(
                SendRequestEnumerator(
                    request,
                    onSuccessDelegate,
                    (string error, UnityWebRequest req) =>
                    {
                        // no response at all (DNS failure, connection reset, timeout) is transient
                        bool noResponse =
                            req.responseCode == 0
                            || req.result == UnityWebRequest.Result.ConnectionError;
                        string retryAfter = GetHeader(req.GetResponseHeaders(), "Retry-After");
                        bool retryable =
                            noResponse
                            || retryAfter is not null
                            || req.responseCode == 429
                            || req.responseCode >= 500;

                        if (retryParameters.RemainingAttempts <= 0 || !retryable)
                        {
                            onErrorDelegate(error, req);
                            return;
                        }

                        L.Warn(
                            $"Retrying ({retryParameters.RemainingAttempts}/{retryParameters.MaxAttempts}) {request.method} {request.url}.\n{error}"
                        );
                        retryParameters.RemainingAttempts--;

                        // Retry-After applies to the next attempt only, custom backoff is kept
                        float? retryAfterSeconds = ParseRetryAfterSeconds(retryAfter);
                        retryParameters.NextDelaySeconds = retryAfterSeconds.HasValue
                            ? retryAfterSeconds.Value * Random.Range(1f, 1.2f)
                            : retryParameters.BackoffSeconds();
                        onRetryableDelegate(error, req, retryParameters);
                    },
                    retryParameters
                )
            );
        }

        internal static string GetHeader(Dictionary<string, string> headers, string name)
        {
            if (headers is null)
            {
                return null;
            }
            foreach (KeyValuePair<string, string> header in headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Parse Retry-After as delay-seconds or HTTP-date, returns null if absent or invalid.
        /// </summary>
        internal static float? ParseRetryAfterSeconds(string retryAfter, DateTimeOffset? now = null)
        {
            if (string.IsNullOrWhiteSpace(retryAfter))
            {
                return null;
            }
            if (
                float.TryParse(
                    retryAfter.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float seconds
                )
            )
            {
                return Math.Max(0f, seconds);
            }
            if (
                DateTimeOffset.TryParse(
                    retryAfter.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset date
                )
            )
            {
                // date in the past means retry right away
                return Math.Max(0f, (float)(date - (now ?? DateTimeOffset.UtcNow)).TotalSeconds);
            }
            L.Warn($"Couldn't parse Retry-After header [{retryAfter}], using default backoff.");
            return null;
        }

        public IEnumerator SendRequestEnumerator(
            UnityWebRequest request,
            Action<string, UnityWebRequest> onSuccessDelegate,
            Action<string, UnityWebRequest> onErrorDelegate,
            RetryParameters retryParameters = null
        )
        {
            // only actual retries wait, the first attempt is sent right away
            if (retryParameters?.NextDelaySeconds is float delaySeconds)
            {
                retryParameters.NextDelaySeconds = null;
                if (delaySeconds > 0f)
                {
                    yield return new WaitForSeconds(delaySeconds);
                }
            }

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onErrorDelegate(
                    $"HTTP {request.responseCode}: {request.error}\n{request.downloadHandler?.text}",
                    request
                );
            }
            else
            {
                string stringResponse =
                    request.downloadHandler == null ? "" : request.downloadHandler.text;
                onSuccessDelegate(stringResponse, request);
            }
            request.Dispose();
        }
    }

    public class RetryParameters
    {
        private uint? _RemainingAttempts;

        public uint MaxAttempts { get; set; } = 3;

        /// <summary>
        /// Follows MaxAttempts until set or decremented, so an object initializer
        /// like <c>new RetryParameters { MaxAttempts = 10 }</c> retries 10 times.
        /// </summary>
        public uint RemainingAttempts
        {
            get => _RemainingAttempts ?? MaxAttempts;
            set => _RemainingAttempts = value;
        }

        public Func<float> BackoffSeconds = DefaultBackoffSeconds;

        /// <summary>
        /// Delay before the next attempt, set only when retrying.
        /// </summary>
        internal float? NextDelaySeconds;

        public static readonly Func<float> DefaultBackoffSeconds = () =>
            1f * Random.Range(0.8f, 1.2f);

        public RetryParameters(uint attempts = 3, Func<float> backoffSeconds = null)
        {
            MaxAttempts = attempts;
            BackoffSeconds = backoffSeconds ?? DefaultBackoffSeconds;
        }
    }
}
