using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgegapSdkTests
{
    /// <summary>
    /// Minimal HTTP server on localhost for driving the SDK against scripted responses.
    /// </summary>
    public class MockServer : IDisposable
    {
        public class Request
        {
            public string Method;
            public string Path; // without query
            public string RawQuery; // as sent, still encoded
            public string Body;
            public DateTime ReceivedAt;
        }

        public class Response
        {
            public int Status = 200;
            public string Body = "{}";
            public Dictionary<string, string> Headers = new Dictionary<string, string>();
            public int DelayMs = 0;

            public static Response Json(string body, int status = 200) =>
                new Response { Status = status, Body = body };
        }

        public readonly string BaseUrl;
        public Func<Request, Response> Handler = _ => new Response();

        private readonly HttpListener Listener = new HttpListener();
        private readonly List<Request> Received = new List<Request>();
        private readonly object Lock = new object();
        private volatile bool Running = true;

        public MockServer()
        {
            int port = FreePort();
            BaseUrl = $"http://localhost:{port}";
            Listener.Prefixes.Add($"{BaseUrl}/");
            Listener.Start();
            Task.Run(Loop);
        }

        public static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public List<Request> Requests
        {
            get
            {
                lock (Lock)
                {
                    return Received.ToList();
                }
            }
        }

        public List<Request> RequestsTo(string method, string pathPrefix) =>
            Requests.Where(r => r.Method == method && r.Path.StartsWith(pathPrefix)).ToList();

        private async Task Loop()
        {
            while (Running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await Listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            string body;
            using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }
            string rawUrl = ctx.Request.RawUrl ?? "/";
            int q = rawUrl.IndexOf('?');
            Request req = new Request
            {
                Method = ctx.Request.HttpMethod,
                Path = q >= 0 ? rawUrl.Substring(0, q) : rawUrl,
                RawQuery = q >= 0 ? rawUrl.Substring(q + 1) : "",
                Body = body,
                ReceivedAt = DateTime.UtcNow,
            };
            lock (Lock)
            {
                Received.Add(req);
            }

            Response res;
            try
            {
                lock (Lock)
                {
                    res = Handler(req);
                }
            }
            catch (Exception e)
            {
                res = new Response { Status = 599, Body = e.ToString() };
            }

            if (res.DelayMs > 0)
            {
                await Task.Delay(res.DelayMs);
            }

            try
            {
                ctx.Response.StatusCode = res.Status;
                foreach (var h in res.Headers)
                {
                    ctx.Response.Headers[h.Key] = h.Value;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(res.Body ?? "");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch
            {
                // client gave up (timeout)
            }
        }

        public void Dispose()
        {
            Running = false;
            try
            {
                Listener.Stop();
                Listener.Close();
            }
            catch { }
        }
    }
}
