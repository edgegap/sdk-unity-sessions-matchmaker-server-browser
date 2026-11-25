using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Edgegap
{
    using L = Logger;

    /// <summary>
    /// Protocols used to measure round-trip time to ping beacons.
    /// </summary>
    public enum PingProtocol
    {
        /// <summary>ICMP echo (UnityEngine.Ping).</summary>
        ICMP,

        /// <summary>UDP echo to the beacon's udp_port.</summary>
        UDP,

        /// <summary>TCP connect time to the beacon's tcp_port.</summary>
        TCP,
    }

    public class Ping
    {
        /// <summary>
        /// Default protocol order: ICMP, then UDP and TCP for beacons that didn't answer.
        /// </summary>
        public static readonly PingProtocol[] DefaultProtocols = new PingProtocol[]
        {
            PingProtocol.ICMP,
            PingProtocol.UDP,
            PingProtocol.TCP,
        };

        internal MonoBehaviour Parent;
        internal int TimeoutSeconds = 3;

        /// <summary>
        /// Protocols to try, in order. A beacon is measured with the first one it answers.
        /// </summary>
        public PingProtocol[] Protocols = DefaultProtocols;

        /// <summary>
        /// Seconds to wait for each UDP or TCP sample.
        /// </summary>
        public float SocketTimeoutSeconds = 1f;

#if UNITY_WEBGL && !UNITY_EDITOR
        private static bool WarnedWebGL = false;
#endif

        public Ping(MonoBehaviour parent)
        {
            Parent = parent;
        }

        public Ping(MonoBehaviour parent, int timeoutSeconds)
        {
            Parent = parent;
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>
        /// Measure round-trip time to beacons in milliseconds, keeping the lowest beacon per city.
        /// Each protocol (default ICMP, UDP, TCP) only measures beacons that didn't answer an
        /// earlier one. Beacons that never answer are left out of the result, never reported as 0.
        /// In WebGL builds, which can't ping, the result is empty.
        /// </summary>
        public IEnumerator MeasureBeacons(
            BeaconDTO[] beacons,
            Action<Dictionary<string, float>> onCompleteDelegate,
            int requests = 3,
            PingProtocol[] protocols = null
        )
        {
            BeaconDTO[] targets = (beacons ?? new BeaconDTO[0])
                .Where(beacon => beacon is not null)
                .ToArray();
            double[] averages = Enumerable.Repeat(-1d, targets.Length).ToArray();
            requests = Math.Max(requests, 1);

#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WarnedWebGL && targets.Length > 0)
            {
                WarnedWebGL = true;
                L.Warn(
                    "Ping | WebGL builds can't send ICMP, UDP or TCP pings, leaving beacon latencies out."
                );
            }
            onCompleteDelegate(new Dictionary<string, float>());
            yield break;
#else
            List<int> pending = Enumerable.Range(0, targets.Length).ToList();
            foreach (PingProtocol protocol in protocols ?? Protocols ?? DefaultProtocols)
            {
                if (pending.Count == 0)
                {
                    break;
                }

                double[] measured;
                if (protocol == PingProtocol.ICMP)
                {
                    double[] icmp = new double[pending.Count];
                    int finished = 0;
                    for (int j = 0; j < pending.Count; j++)
                    {
                        int index = j;
                        Parent.StartCoroutine(
                            GetAverageRoundTripTime(
                                targets[pending[j]].PublicIP,
                                (double rtt) =>
                                {
                                    icmp[index] = rtt;
                                    finished++;
                                },
                                requests
                            )
                        );
                    }
                    yield return new WaitUntil(() => finished == icmp.Length);
                    measured = icmp;
                }
                else
                {
                    BeaconDTO[] subset = pending.Select(i => targets[i]).ToArray();
                    bool udp = protocol == PingProtocol.UDP;
                    float timeout = SocketTimeoutSeconds;
                    // sockets block, so they run off the main thread
                    Task<double[]> task = Task.Run(() =>
                        udp
                            ? SampleUdp(subset, requests, timeout)
                            : SampleTcp(subset, requests, timeout)
                    );
                    yield return new WaitUntil(() => task.IsCompleted);
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        measured = task.Result;
                    }
                    else
                    {
                        L.Warn($"Ping | {protocol} failed\n{task.Exception}");
                        measured = Enumerable.Repeat(-1d, subset.Length).ToArray();
                    }
                }

                List<int> stillPending = new List<int>();
                for (int j = 0; j < pending.Count; j++)
                {
                    if (measured[j] >= 0)
                    {
                        averages[pending[j]] = measured[j];
                    }
                    else
                    {
                        stillPending.Add(pending[j]);
                    }
                }
                pending = stillPending;
            }

            onCompleteDelegate(BuildLatencies(targets, averages));
#endif
        }

        /// <summary>
        /// Average ICMP round-trip time to ip in milliseconds, or -1 when no request was answered.
        /// </summary>
        public IEnumerator GetAverageRoundTripTime(
            string ip,
            Action<double> onCompleteDelegate,
            int requests
        )
        {
            List<int> pings = new List<int>();
            for (int i = 0; i < requests; i++)
            {
                Parent.StartCoroutine(_IcmpPing(ip, (int rtt) => pings.Add(rtt)));
            }

            yield return new WaitUntil(() => pings.Count == requests);

            List<int> finishedPings = pings.Where((int p) => p > 0).ToList();
            onCompleteDelegate(
                finishedPings.Count() > 0 ? Math.Round(finishedPings.Average(), 2) : -1d
            );
        }

        public IEnumerator _IcmpPing(string ip, Action<int> onCompleteDelegate)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // browsers can't send ICMP, so UnityEngine.Ping doesn't exist in WebGL builds
            onCompleteDelegate(-1);
            yield break;
#else
            if (!IPAddress.TryParse(ip ?? "", out _))
            {
                onCompleteDelegate(-1);
                yield break;
            }

            UnityEngine.Ping ping = new UnityEngine.Ping(ip);
            double start = Time.realtimeSinceStartupAsDouble;

            yield return new WaitUntil(() =>
                ping.isDone || Time.realtimeSinceStartupAsDouble - start > TimeoutSeconds
            );

            onCompleteDelegate(ping.isDone ? ping.time : -1);
            ping.DestroyPing();
#endif
        }

        /// <summary>
        /// Latency per beacon city in ms, rounded to 0.01, keeping the lowest beacon per city.
        /// Beacons without a measurement (negative average) or without a city are left out.
        /// </summary>
        public static Dictionary<string, float> BuildLatencies(
            BeaconDTO[] beacons,
            double[] averages
        )
        {
            Dictionary<string, float> latencies = new Dictionary<string, float>();
            List<string> failed = new List<string>();

            for (int i = 0; i < beacons.Length; i++)
            {
                string city = beacons[i]?.Location?.City;
                double average = i < averages.Length ? averages[i] : -1d;
                if (average < 0 || string.IsNullOrEmpty(city))
                {
                    failed.Add($"{city} ({beacons[i]?.PublicIP})");
                    continue;
                }

                float ms = (float)Math.Round(average, 2);
                latencies[city] = latencies.TryGetValue(city, out float lowest)
                    ? Math.Min(lowest, ms)
                    : ms;
            }

            if (failed.Count > 0)
            {
                L.Warn(
                    $"Ping | No answer from beacons {string.Join(", ", failed)}, leaving them out."
                );
            }

            return latencies;
        }

        /// <summary>
        /// Send a small datagram to each beacon's udp_port in parallel, samples times, and time the
        /// beacon's echo. Returns the average round-trip time per beacon in ms, or -1 when it never
        /// answered. Blocks the calling thread.
        /// </summary>
        public static double[] SampleUdp(BeaconDTO[] beacons, int samples, float timeoutSeconds)
        {
            double[] sums = new double[beacons.Length];
            int[] counts = new int[beacons.Length];
            Socket[] sockets = new Socket[beacons.Length];
            System.Random random = new System.Random();
            byte[] buffer = new byte[1500];

            try
            {
                for (int i = 0; i < beacons.Length; i++)
                {
                    if (
                        beacons[i].UdpPort <= 0
                        || !IPAddress.TryParse(beacons[i].PublicIP ?? "", out IPAddress address)
                    )
                    {
                        continue;
                    }

                    try
                    {
                        sockets[i] = new Socket(
                            address.AddressFamily,
                            SocketType.Dgram,
                            ProtocolType.Udp
                        );
                        sockets[i].Connect(new IPEndPoint(address, beacons[i].UdpPort));
                    }
                    catch (Exception)
                    {
                        sockets[i]?.Close();
                        sockets[i] = null;
                    }
                }

                for (int sample = 0; sample < samples; sample++)
                {
                    Dictionary<Socket, int> pending = new Dictionary<Socket, int>();
                    byte[][] payloads = new byte[beacons.Length][];
                    long[] sentAt = new long[beacons.Length];

                    for (int i = 0; i < sockets.Length; i++)
                    {
                        if (sockets[i] is null)
                        {
                            continue;
                        }

                        // a new payload per sample, so late echoes of earlier samples don't match
                        payloads[i] = Encoding.ASCII.GetBytes($"EG{random.Next():x8}");
                        try
                        {
                            sentAt[i] = Stopwatch.GetTimestamp();
                            sockets[i].Send(payloads[i]);
                            pending[sockets[i]] = i;
                        }
                        catch (SocketException) { }
                    }

                    long deadline = Stopwatch.GetTimestamp() + SecondsToTicks(timeoutSeconds);
                    while (pending.Count > 0)
                    {
                        int waitMicroseconds = TicksToMicroseconds(
                            deadline - Stopwatch.GetTimestamp()
                        );
                        if (waitMicroseconds <= 0)
                        {
                            break;
                        }

                        List<Socket> readable = pending.Keys.ToList();
                        Socket.Select(readable, null, null, waitMicroseconds);
                        long receivedAt = Stopwatch.GetTimestamp();

                        foreach (Socket socket in readable)
                        {
                            int i = pending[socket];
                            try
                            {
                                while (socket.Poll(0, SelectMode.SelectRead))
                                {
                                    int length = socket.Receive(buffer);
                                    if (
                                        length == payloads[i].Length
                                        && buffer.Take(length).SequenceEqual(payloads[i])
                                    )
                                    {
                                        sums[i] += TicksToMilliseconds(receivedAt - sentAt[i]);
                                        counts[i]++;
                                        pending.Remove(socket);
                                        break;
                                    }
                                }
                            }
                            catch (SocketException)
                            {
                                // ICMP port unreachable surfaces as a reset on a connected UDP socket
                                pending.Remove(socket);
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (Socket socket in sockets)
                {
                    socket?.Close();
                }
            }

            return Averages(sums, counts);
        }

        /// <summary>
        /// Time a TCP connect to each beacon's tcp_port in parallel, samples times. Returns the
        /// average connect time per beacon in ms, or -1 when it never connected. Blocks the calling
        /// thread.
        /// </summary>
        public static double[] SampleTcp(BeaconDTO[] beacons, int samples, float timeoutSeconds)
        {
            double[] sums = new double[beacons.Length];
            int[] counts = new int[beacons.Length];

            for (int sample = 0; sample < samples; sample++)
            {
                Dictionary<Socket, int> pending = new Dictionary<Socket, int>();
                long[] startedAt = new long[beacons.Length];

                try
                {
                    for (int i = 0; i < beacons.Length; i++)
                    {
                        if (
                            beacons[i].TcpPort <= 0
                            || !IPAddress.TryParse(beacons[i].PublicIP ?? "", out IPAddress address)
                        )
                        {
                            continue;
                        }

                        Socket socket = new Socket(
                            address.AddressFamily,
                            SocketType.Stream,
                            ProtocolType.Tcp
                        );
                        try
                        {
                            socket.Blocking = false;
                            startedAt[i] = Stopwatch.GetTimestamp();
                            socket.Connect(new IPEndPoint(address, beacons[i].TcpPort));
                            // connected right away, for example to localhost
                            sums[i] += TicksToMilliseconds(Stopwatch.GetTimestamp() - startedAt[i]);
                            counts[i]++;
                            socket.Close();
                        }
                        catch (SocketException e)
                            when (e.SocketErrorCode == SocketError.WouldBlock
                                || e.SocketErrorCode == SocketError.InProgress
                                || e.SocketErrorCode == SocketError.AlreadyInProgress
                            )
                        {
                            pending[socket] = i;
                        }
                        catch (Exception)
                        {
                            socket.Close();
                        }
                    }

                    long deadline = Stopwatch.GetTimestamp() + SecondsToTicks(timeoutSeconds);
                    while (pending.Count > 0)
                    {
                        int waitMicroseconds = TicksToMicroseconds(
                            deadline - Stopwatch.GetTimestamp()
                        );
                        if (waitMicroseconds <= 0)
                        {
                            break;
                        }

                        List<Socket> writable = pending.Keys.ToList();
                        List<Socket> failed = pending.Keys.ToList();
                        Socket.Select(null, writable, failed, waitMicroseconds);
                        long connectedAt = Stopwatch.GetTimestamp();

                        foreach (Socket socket in failed)
                        {
                            pending.Remove(socket);
                            socket.Close();
                        }

                        foreach (Socket socket in writable)
                        {
                            if (!pending.TryGetValue(socket, out int i))
                            {
                                continue;
                            }

                            pending.Remove(socket);
                            // a failed connect is writable too on some platforms, with SO_ERROR set
                            if (
                                socket.GetSocketOption(
                                    SocketOptionLevel.Socket,
                                    SocketOptionName.Error
                                )
                                    is int error
                                && error == 0
                            )
                            {
                                sums[i] += TicksToMilliseconds(connectedAt - startedAt[i]);
                                counts[i]++;
                            }
                            socket.Close();
                        }
                    }
                }
                finally
                {
                    foreach (Socket socket in pending.Keys)
                    {
                        socket.Close();
                    }
                }
            }

            return Averages(sums, counts);
        }

        private static double[] Averages(double[] sums, int[] counts)
        {
            double[] averages = new double[sums.Length];
            for (int i = 0; i < sums.Length; i++)
            {
                averages[i] = counts[i] > 0 ? sums[i] / counts[i] : -1d;
            }
            return averages;
        }

        private static long SecondsToTicks(float seconds) => (long)(seconds * Stopwatch.Frequency);

        private static int TicksToMicroseconds(long ticks) =>
            (int)Math.Min(int.MaxValue, Math.Max(0, ticks * 1_000_000 / Stopwatch.Frequency));

        private static double TicksToMilliseconds(long ticks) =>
            ticks * 1000d / Stopwatch.Frequency;
    }
}
