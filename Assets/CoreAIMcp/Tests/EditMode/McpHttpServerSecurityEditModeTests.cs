using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// End-to-end proof that the loopback endpoint refuses the attacks loopback binding does NOT stop:
    /// a browser page POSTing cross-origin (CSRF), a rebound hostname, a simple-CORS content type, an
    /// unauthenticated local process, and an oversized body. Self-skips when the OS refuses the loopback
    /// URL reservation, like the sibling integration suite.
    /// </summary>
    [Category("Integration")]
    public sealed class McpHttpServerSecurityEditModeTests
    {
        private const string Token = "test-token-0123456789";

        private McpHttpServer _server;
        private int _port;

        [SetUp]
        public void SetUp()
        {
            _port = FreeLoopbackPort();
            McpToolRegistry registry = new(new[] { new FakeMcpTool("echo_tool") });
            McpRpcDispatcher dispatcher =
                new(registry, new McpSessionStore(), new InlineMainThreadDispatcher());
            _server = new McpHttpServer(_port, dispatcher, authToken: Token);

            try
            {
                _server.Start();
            }
            catch (HttpListenerException ex)
            {
                _server = null;
                Assert.Ignore($"HttpListener could not bind loopback (needs a URL ACL): {ex.Message}");
            }
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            _server = null;
        }

        private string Url => $"http://127.0.0.1:{_port}/mcp";

        [Test]
        public void Server_ReportsThatItRequiresAuth()
        {
            Assert.IsTrue(_server.RequiresAuth);
        }

        [Test]
        public async Task Post_WithForeignOrigin_IsForbidden()
        {
            using HttpClient client = new();
            HttpResponseMessage response =
                await PostAsync(client, ToolsListBody(), Token, "https://evil.example");

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "a web page must not be able to reach the endpoint even with a valid token.");
        }

        [Test]
        public async Task Post_WithLoopbackOrigin_IsAccepted()
        {
            using HttpClient client = new();
            HttpResponseMessage response =
                await PostAsync(client, ToolsListBody(), Token, $"http://127.0.0.1:{_port}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [Test]
        public async Task Post_WithoutToken_IsUnauthorized_AndAdvertisesBearer()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await PostAsync(client, ToolsListBody(), null);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.IsTrue(response.Headers.Contains("WWW-Authenticate"));
        }

        [Test]
        public async Task Post_WithWrongToken_IsUnauthorized()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await PostAsync(client, ToolsListBody(), "not-the-token");

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Test]
        public async Task Post_WithCorrectToken_Succeeds()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await PostAsync(client, ToolsListBody(), Token);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            JObject payload = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("echo_tool", payload["result"]!["tools"]![0]!["name"]!.ToString());
        }

        [Test]
        public async Task Post_AsTextPlain_IsUnsupportedMediaType()
        {
            using HttpClient client = new();
            HttpRequestMessage request = new(HttpMethod.Post, Url)
            {
                // The exact framing a page uses to POST cross-origin without a CORS preflight.
                Content = new StringContent(ToolsListBody(), Encoding.UTF8, "text/plain")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

            HttpResponseMessage response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        [Test]
        public async Task Post_OversizedBody_IsRefused()
        {
            _server.MaxRequestBodyBytes = 256;

            using HttpClient client = new();
            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"pad\":\"" +
                          new string('x', 2048) + "\"}";
            HttpResponseMessage response = await PostAsync(client, body, Token);

            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        [Test]
        public async Task Get_RequiresEventStreamAccept_ForAnAuthorizedClient()
        {
            using HttpClient client = new();
            HttpRequestMessage request = new(HttpMethod.Get, Url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

            HttpResponseMessage response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
        }

        [Test]
        public async Task ChunkedUnicodeBody_LimitCountsUtf8Bytes_NotCharacters()
        {
            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"pad\":\"" + new string('ж', 150) + "\"}";
            _server.MaxRequestBodyBytes = body.Length + 20;
            Assert.Greater(Encoding.UTF8.GetByteCount(body), _server.MaxRequestBodyBytes);
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpRequestMessage request = new(HttpMethod.Post, Url) { Content = new ChunkedContent(body) };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task IncompleteBody_IsBounded_ForAcceptedAndRejectedRequests(bool authorized)
        {
            _server.BodyReadTimeout = TimeSpan.FromMilliseconds(200);
            using TcpClient socket = new();
            await socket.ConnectAsync(IPAddress.Loopback, _port);
            using NetworkStream stream = socket.GetStream();
            string headers = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\nContent-Type: application/json\r\nContent-Length: 100\r\n" +
                (authorized ? $"Authorization: Bearer {Token}\r\n" : "") + "\r\n{";
            byte[] bytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            byte[] response = new byte[1024];
            Task<int> read = stream.ReadAsync(response, 0, response.Length);
            Assert.AreSame(read, await Task.WhenAny(read, Task.Delay(3000)), "A partial body must not stall rejection or occupy a worker indefinitely.");
            string status = Encoding.ASCII.GetString(response, 0, await read);
            StringAssert.Contains(authorized ? "408" : "401", status);
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage healthy = await PostAsync(client, ToolsListBody(), Token);
            Assert.AreEqual(HttpStatusCode.OK, healthy.StatusCode);
        }

        private sealed class ChunkedContent : HttpContent
        {
            private readonly byte[] _bytes;
            public ChunkedContent(string body)
            {
                _bytes = Encoding.UTF8.GetBytes(body);
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
                => stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        private Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string token,
            string origin = null)
        {
            HttpRequestMessage request = new(HttpMethod.Post, Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (token != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            }

            if (origin != null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            return client.SendAsync(request);
        }

        private static string ToolsListBody()
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";
        }

        private static int FreeLoopbackPort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
