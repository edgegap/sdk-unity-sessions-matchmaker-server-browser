using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Edgegap;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace EdgegapSdkTests
{
    /// <summary>
    /// Beacons without a measurement are left out, UDP and TCP fall back after ICMP.
    /// </summary>
    public class PingTests : SdkTestBase
    {
        private static BeaconDTO Beacon(string city, string ip, int udpPort = 0, int tcpPort = 0) =>
            new BeaconDTO
            {
                PublicIP = ip,
                UdpPort = udpPort,
                TcpPort = tcpPort,
                Location = new LocationDTO { City = city },
            };

        [Test]
        public void UnansweredBeaconsAreLeftOut()
        {
            Dictionary<string, float> latencies = Edgegap.Ping.BuildLatencies(
                new[]
                {
                    Beacon("Montreal", "192.0.2.1"),
                    Beacon("Dubai", "192.0.2.2"),
                    Beacon("Montreal", "192.0.2.3"),
                    Beacon("Tokyo", "192.0.2.4"),
                },
                new[] { 12.345d, -1d, 8.5d, -1d }
            );

            CollectionAssert.AreEquivalent(
                new Dictionary<string, float> { ["Montreal"] = 8.5f },
                latencies,
                "unanswered beacons must be omitted, the lowest beacon per city kept"
            );
        }

        [UnityTest]
        public IEnumerator UdpThenTcpMeasureOnlyUnansweredBeacons()
        {
            using UdpEcho echo = new UdpEcho();
            using TcpAcceptor acceptor = new TcpAcceptor();
            int closedPort = MockServer.FreePort();

            Edgegap.Ping ping = new Edgegap.Ping(Runner) { SocketTimeoutSeconds = 0.5f };
            Dictionary<string, float> latencies = null;
            Runner.StartCoroutine(
                ping.MeasureBeacons(
                    new[]
                    {
                        // answers UDP, so it's never measured with TCP
                        Beacon("Udp", "127.0.0.1", echo.Port, acceptor.Port),
                        // no UDP echo, measured with TCP
                        Beacon("Tcp", "127.0.0.1", closedPort, acceptor.Port),
                        Beacon("Dead", "127.0.0.1", closedPort, closedPort),
                        Beacon("NoAddress", "not an ip", echo.Port, acceptor.Port),
                    },
                    result => latencies = result,
                    2,
                    new[] { PingProtocol.UDP, PingProtocol.TCP }
                )
            );

            yield return WaitFor(() => latencies is not null, 10, "beacons measured");
            CollectionAssert.AreEquivalent(new[] { "Udp", "Tcp" }, latencies.Keys);
            Assert.GreaterOrEqual(latencies["Udp"], 0f);
            Assert.GreaterOrEqual(latencies["Tcp"], 0f);
            Assert.AreEqual(2, echo.Echoed, "UDP samples");
            yield return WaitFor(() => acceptor.Accepted >= 2, 2, "TCP samples");
            yield return Wait(0.2f);
            Assert.AreEqual(2, acceptor.Accepted, "TCP measured a beacon that answered UDP");
        }

        [UnityTest]
        public IEnumerator NoProtocolAnsweredGivesEmptyResult()
        {
            Edgegap.Ping ping = new Edgegap.Ping(Runner) { SocketTimeoutSeconds = 0.3f };
            Dictionary<string, float> latencies = null;
            Runner.StartCoroutine(
                ping.MeasureBeacons(
                    // TEST-NET-1 is never routed, so every sample times out
                    new[] { Beacon("Nowhere", "192.0.2.1", 9, 9) },
                    result => latencies = result,
                    1,
                    new[] { PingProtocol.UDP, PingProtocol.TCP }
                )
            );

            yield return WaitFor(() => latencies is not null, 5, "beacons measured");
            Assert.IsEmpty(latencies, "a beacon without answer must not be reported as 0");
        }

        /// <summary>
        /// Echoes every datagram back to its sender, like a ping beacon.
        /// </summary>
        private class UdpEcho : IDisposable
        {
            public readonly int Port;
            public int Echoed => echoed;

            private readonly UdpClient Client = new UdpClient(
                new IPEndPoint(IPAddress.Loopback, 0)
            );
            private int echoed;

            public UdpEcho()
            {
                Port = ((IPEndPoint)Client.Client.LocalEndPoint).Port;
                new Thread(Loop) { IsBackground = true }.Start();
            }

            private void Loop()
            {
                try
                {
                    while (true)
                    {
                        IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = Client.Receive(ref sender);
                        Client.Send(data, data.Length, sender);
                        Interlocked.Increment(ref echoed);
                    }
                }
                catch (Exception) { }
            }

            public void Dispose() => Client.Close();
        }

        /// <summary>
        /// Accepts and counts TCP connections.
        /// </summary>
        private class TcpAcceptor : IDisposable
        {
            public readonly int Port;
            public int Accepted => accepted;

            private readonly TcpListener Listener = new TcpListener(IPAddress.Loopback, 0);
            private int accepted;

            public TcpAcceptor()
            {
                Listener.Start();
                Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
                new Thread(Loop) { IsBackground = true }.Start();
            }

            private void Loop()
            {
                try
                {
                    while (true)
                    {
                        Listener.AcceptSocket().Close();
                        Interlocked.Increment(ref accepted);
                    }
                }
                catch (Exception) { }
            }

            public void Dispose() => Listener.Stop();
        }
    }
}
