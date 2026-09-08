using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// End-to-end HTTP round trips through the real <see cref="McpHttpServer"/> on a loopback port:
    /// JSON and SSE framing, session-optional behavior, and version tolerance. Marked Integration and
    /// self-skips when the OS refuses the loopback URL reservation, so it never flakes in CI.
    /// </summary>
    [Category("Integration")]
    public sealed class McpHttpServerIntegrationEditModeTests
    {
        private McpHttpServer _server;
        private int _port;
        private McpToolRegistry _registry;
        private McpSessionStore _sessions;
        private FakeMcpTool _echo;
        private long _sessionClockOffsetTicks;

        [SetUp]
        public void SetUp()
        {
            _port = FreeLoopbackPort();
            _echo = new FakeMcpTool("echo_tool");
            _registry = new(new[] { _echo });
            _sessionClockOffsetTicks = 0;
            _sessions = new McpSessionStore(clock: () => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _sessionClockOffsetTicks)));
            McpRpcDispatcher dispatcher = new(_registry, _sessions, new InlineMainThreadDispatcher());
            _server = new McpHttpServer(_port, dispatcher);

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
        public async Task Initialize_OverPlainJson_ReturnsResult_AndSessionHeader()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await PostAsync(client, InitializeBody(), "application/json");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains("application/json", response.Content.Headers.ContentType!.MediaType);
            Assert.IsTrue(response.Headers.Contains(McpServerInfo.SessionHeader),
                "initialize must return an Mcp-Session-Id header.");

            JObject payload = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(McpServerInfo.Name, payload["result"]!["serverInfo"]!["name"]!.ToString());
        }

        [Test]
        public async Task ToolsList_WithoutSessionId_StillWorks()
        {
            using HttpClient client = new();
            // Deliberately send NO Mcp-Session-Id header.
            string body = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}";
            HttpResponseMessage response = await PostAsync(client, body, "application/json");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            JObject payload = JObject.Parse(await response.Content.ReadAsStringAsync());
            JArray tools = (JArray)payload["result"]!["tools"];
            Assert.AreEqual("echo_tool", tools[0]["name"]!.ToString());
        }

        [Test]
        public async Task Initialize_OverEventStream_ReturnsSseFramedResponse()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await PostAsync(client, InitializeBody(), "text/event-stream");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains("text/event-stream", response.Content.Headers.ContentType!.MediaType);

            string raw = await response.Content.ReadAsStringAsync();
            StringAssert.Contains("event: message", raw);
            StringAssert.Contains("data: ", raw);

            // The data line must carry a valid JSON-RPC response.
            string dataLine = ExtractSseData(raw);
            JObject payload = JObject.Parse(dataLine);
            Assert.AreEqual("2.0", payload["jsonrpc"]!.ToString());
            Assert.IsNotNull(payload["result"]!["protocolVersion"]);
        }

        [Test]
        public async Task Initialize_UnknownProtocolVersion_DoesNotCrash()
        {
            using HttpClient client = new();
            string body =
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"initialize\"," +
                "\"params\":{\"protocolVersion\":\"3000-01-01-weird\"}}";
            HttpResponseMessage response = await PostAsync(client, body, "application/json");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            JObject payload = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.IsNull(payload["error"]);
            Assert.AreEqual(McpServerInfo.DefaultProtocolVersion, payload["result"]!["protocolVersion"]!.ToString());
        }

        [Test]
        public async Task Get_WithoutEventStreamAccept_ReturnsNotAcceptable()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await client.GetAsync(Url);
            Assert.AreEqual(HttpStatusCode.NotAcceptable, response.StatusCode);
        }

        [Test]
        public async Task LiveCatalogChanges_NotifySameSession_AndReconnectGetsCurrentRevision()
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage initialize = await PostAsync(client, InitializeBody(), "application/json");
            JObject initialized = JObject.Parse(await initialize.Content.ReadAsStringAsync());
            Assert.IsTrue((bool)initialized["result"]["capabilities"]["tools"]["listChanged"]);
            string session = initialize.Headers.GetValues(McpServerInfo.SessionHeader).Single();
            using HttpResponseMessage stream = await OpenNotificationsAsync(client, session);
            Assert.AreEqual(HttpStatusCode.OK, stream.StatusCode);
            using StreamReader reader = new(await stream.Content.ReadAsStreamAsync());
            long initial = await ReadRevisionAsync(reader);
            _registry.AddOrReplace(new FakeMcpTool("stage"));
            long added = await ReadRevisionAsync(reader);
            Assert.Greater(added, initial);
            using HttpResponseMessage listed = await PostAsync(client,
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", "application/json");
            JObject payload = JObject.Parse(await listed.Content.ReadAsStringAsync());
            CollectionAssert.Contains(((JArray)payload["result"]["tools"]).Select(tool => (string)tool["name"]), "stage");
            _registry.Remove("stage");
            Assert.Greater(await ReadRevisionAsync(reader), added);
            using HttpResponseMessage stale = await PostAsync(client,
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"stage\"}}", "application/json");
            Assert.IsNotNull(JObject.Parse(await stale.Content.ReadAsStringAsync())["error"]);
            _registry.Replace(new[] { new FakeMcpTool("next"), new FakeMcpTool("echo_tool") });
            using HttpResponseMessage reconnect = await OpenNotificationsAsync(client, session);
            Assert.AreEqual(HttpStatusCode.OK, reconnect.StatusCode);
            using StreamReader nextReader = new(await reconnect.Content.ReadAsStreamAsync());
            Assert.AreEqual(_registry.Revision, await ReadRevisionAsync(nextReader));
            _server.Stop();
            Assert.AreSame(_server.Completion, await Task.WhenAny(_server.Completion, Task.Delay(3000)),
                "Stop must release open SSE handlers without a Unity main-thread wait.");
            await _server.Completion;
        }

        [Test]
        public async Task NotificationCapacity_IsBounded_AndPostContinuesWorking()
        {
            _server.MaxNotificationStreams = 1;
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage firstInit = await PostAsync(client, InitializeBody(), "application/json");
            using HttpResponseMessage secondInit = await PostAsync(client, InitializeBody(), "application/json");
            using HttpResponseMessage first = await OpenNotificationsAsync(client,
                firstInit.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            using HttpResponseMessage rejected = await OpenNotificationsAsync(client,
                secondInit.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
            using HttpResponseMessage post = await PostAsync(client, InitializeBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
        }

        private Task<HttpResponseMessage> OpenNotificationsAsync(HttpClient client, string session)
        {
            HttpRequestMessage request = new(HttpMethod.Get, Url);
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            request.Headers.TryAddWithoutValidation(McpServerInfo.SessionHeader, session);
            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Post_WithExpiredOrEvictedSession_RejectsBodyAndAllowsFreshInitialize(bool evict)
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage initialized = await PostAsync(client, InitializeBody(), "application/json");
            string session = initialized.Headers.GetValues(McpServerInfo.SessionHeader).Single();
            if (evict)
                for (int index = 0; index < McpSessionStore.DefaultMaxSessions; index++) _sessions.Issue();
            else
                Interlocked.Exchange(ref _sessionClockOffsetTicks, (McpSessionStore.DefaultTimeToLive + TimeSpan.FromSeconds(1)).Ticks);
            client.DefaultRequestHeaders.TryAddWithoutValidation(McpServerInfo.SessionHeader, session);
            using HttpResponseMessage rejected = await PostAsync(client, EchoCallBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.NotFound, rejected.StatusCode);
            Assert.AreEqual(0, _echo.InvocationCount);
            using HttpResponseMessage staleInitialize = await PostAsync(client, InitializeBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.NotFound, staleInitialize.StatusCode);

            client.DefaultRequestHeaders.Remove(McpServerInfo.SessionHeader);
            using HttpResponseMessage fresh = await PostAsync(client, InitializeBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.OK, fresh.StatusCode);
            client.DefaultRequestHeaders.TryAddWithoutValidation(McpServerInfo.SessionHeader,
                fresh.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            using HttpResponseMessage accepted = await PostAsync(client, EchoCallBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            Assert.AreEqual(1, _echo.InvocationCount);
        }

        [TestCase("invalid")]
        [TestCase("2099-01-01")]
        public async Task UnsupportedProtocolHeader_RejectsBeforeToolExecution(string version)
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("MCP-Protocol-Version", version);
            using HttpResponseMessage response = await PostAsync(client, EchoCallBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual(0, _echo.InvocationCount);
        }

        [TestCase(null, false)]
        [TestCase(null, true)]
        [TestCase(McpServerInfo.DefaultProtocolVersion, true)]
        [TestCase("2025-03-26", false)]
        public async Task SupportedOrInferredProtocol_StillRunsTheRequestedTool(string version, bool withSession)
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            if (withSession)
            {
                using HttpResponseMessage initialized = await PostAsync(client, InitializeBody(), "application/json");
                client.DefaultRequestHeaders.TryAddWithoutValidation(McpServerInfo.SessionHeader,
                    initialized.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            }
            if (version != null) client.DefaultRequestHeaders.TryAddWithoutValidation("MCP-Protocol-Version", version);
            using HttpResponseMessage response = await PostAsync(client, EchoCallBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, _echo.InvocationCount);
            Assert.IsNull(JObject.Parse(await response.Content.ReadAsStringAsync())["error"]);
        }

        private static string EchoCallBody() =>
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"echo_tool\",\"arguments\":{\"echo\":\"hello\"}}}";

        private static async Task<long> ReadRevisionAsync(StreamReader reader)
        {
            while (true)
            {
                Task<string> read = reader.ReadLineAsync();
                Assert.AreSame(read, await Task.WhenAny(read, Task.Delay(3000)), "Expected a catalog notification before the deadline.");
                string line = await read;
                Assert.IsNotNull(line, "SSE closed before the catalog notification.");
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                JObject notification = JObject.Parse(line.Substring(6));
                Assert.AreEqual(McpMethods.ToolsListChangedNotification, (string)notification["method"]);
                return (long)notification["params"]["_meta"]["coreai/catalogRevision"];
            }
        }

        private Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string accept)
        {
            HttpRequestMessage request = new(HttpMethod.Post, Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", accept);
            return client.SendAsync(request);
        }

        private static string InitializeBody()
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"," +
                   "\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}";
        }

        private static string ExtractSseData(string raw)
        {
            foreach (string line in raw.Split('\n'))
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    return line.Substring("data: ".Length).Trim();
                }
            }

            return "{}";
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
