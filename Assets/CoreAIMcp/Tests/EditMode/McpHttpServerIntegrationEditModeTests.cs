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
            // WHY: this test holds three responses on one origin at once - the admitted stream, the
            // refused 409 and the POST - and a response returned with ResponseHeadersRead keeps its
            // connection until it is disposed. Under Unity's Mono, HttpClient runs on HttpWebRequest,
            // whose per-origin budget defaults to two and whose HttpClientHandler.MaxConnectionsPerServer
            // throws NotImplementedException; with the inherited budget the POST queued client-side until
            // its own timeout while the server sat idle (measured headless on the same Mono: the request
            // never reached the listener, a fresh raw socket was answered in 1 ms). The budget is stated
            // on this test's own loopback endpoint, so nothing global leaks to other tests.
            ServicePointManager.FindServicePoint(new Uri(Url)).ConnectionLimit = 3;
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage firstInit = await PostAsync(client, InitializeBody(), "application/json");
            using HttpResponseMessage secondInit = await PostAsync(client, InitializeBody(), "application/json");
            using HttpResponseMessage first = await OpenNotificationsAsync(client,
                firstInit.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            using HttpResponseMessage rejected = await OpenNotificationsAsync(client,
                secondInit.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);

            // WHY: both the admitted stream and the refused response stay open and unread here - the
            // property under test is that neither stops ordinary request handling.
            using HttpResponseMessage post = await PostAsync(client, InitializeBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
        }

        [Test]
        public async Task AbandonedNotificationStream_DoesNotBlockLaterRequests_OrCleanShutdown()
        {
            // WHY: a client that opens the notification stream and never reads it (crashed, killed, or a
            // client that only cares about the headers) must not wedge the transport - the open connection
            // itself, not "did anyone drain it", is what must never block ordinary request handling.
            _server.MaxNotificationStreams = 1;
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage init = await PostAsync(client, InitializeBody(), "application/json");
            HttpResponseMessage stream = await OpenNotificationsAsync(client,
                init.Headers.GetValues(McpServerInfo.SessionHeader).Single());
            Assert.AreEqual(HttpStatusCode.OK, stream.StatusCode);
            stream.Dispose();

            using HttpResponseMessage post = await PostAsync(client, InitializeBody(), "application/json");
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);

            _server.Stop();
            Assert.AreSame(_server.Completion, await Task.WhenAny(_server.Completion, Task.Delay(3000)),
                "Stop must release an abandoned notification stream's dedicated thread without hanging.");
            await _server.Completion;
        }

        [Test]
        public async Task Start_AfterStop_RebindsLoopbackPrefix_AndServes()
        {
            int port = FreeLoopbackPort();
            FakeMcpTool echo = new FakeMcpTool("echo_tool");
            McpToolRegistry registry = new(new[] { echo });
            McpSessionStore sessions = new();
            McpHttpServer server = new(port, new McpRpcDispatcher(registry, sessions, new InlineMainThreadDispatcher()));
            try
            {
                try { server.Start(); }
                catch (HttpListenerException ex)
                {
                    server.Dispose();
                    Assert.Ignore($"HttpListener could not bind loopback (needs a URL ACL): {ex.Message}");
                    return;
                }

                server.Stop();
                Assert.IsFalse(server.IsRunning, "Stop must release the listener.");

                server.Start();
                Assert.IsTrue(server.IsRunning, "A stopped real listener must be restartable on the same prefix.");

                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
                string url = $"http://127.0.0.1:{port}/mcp";
                using HttpResponseMessage response = await PostToUrlAsync(client, url, InitializeBody(), "application/json");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                server.Dispose();
            }
        }

        [Test]
        public async Task Stop_ReentrantCancellationCallback_ReleasesPrefixBeforeCallback_AndReplacementServes()
        {
            int port = FreeLoopbackPort();
            ReentrantCancellationTool tool = new ReentrantCancellationTool();
            McpToolRegistry registry = new(new[] { tool });
            McpSessionStore sessions = new();
            McpRpcDispatcher dispatcher = new(registry, sessions, new InlineMainThreadDispatcher());
            McpHttpServer oldServer = new(port, dispatcher);
            McpHttpServer replacementServer = null;
            tool.OnCancelled = () =>
            {
                McpHttpServer candidate = new(port, dispatcher);
                candidate.Start();
                replacementServer = candidate;
            };

            try
            {
                try { oldServer.Start(); }
                catch (HttpListenerException ex)
                {
                    oldServer.Dispose();
                    Assert.Ignore($"HttpListener could not bind loopback (needs a URL ACL): {ex.Message}");
                    return;
                }

                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
                string url = $"http://127.0.0.1:{port}/mcp";
                Task<JObject> request = SendRpcToUrlAsync(client, url,
                    new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = 1,
                        ["method"] = McpMethods.ToolsCall,
                        ["params"] = new JObject { ["name"] = tool.Name, ["arguments"] = new JObject() }
                    });

                Task startedWait = Task.WhenAny(tool.Started.Task, Task.Delay(5000));
                await startedWait;
                Assert.IsTrue(tool.Started.Task.IsCompleted, "The blocking request must reach the tool.");

                oldServer.Stop();

                Assert.IsNotNull(replacementServer, "The cancellation callback must install a replacement listener.");
                Assert.IsTrue(replacementServer.IsRunning, "The replacement must bind while the old stop is still completing.");

                Task completionWait = Task.WhenAny(oldServer.Completion, Task.Delay(5000));
                await completionWait;
                Assert.IsTrue(oldServer.Completion.IsCompleted, "The old transport must finish its late cleanup.");

                Assert.IsTrue(replacementServer.IsRunning, "Late old cleanup must not dispose the replacement.");

                string replacementUrl = $"http://127.0.0.1:{port}/mcp";
                using HttpResponseMessage response = await PostToUrlAsync(client, replacementUrl, InitializeBody(), "application/json");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "The replacement must serve real HTTP requests.");

                try { await request; } catch (Exception) { }
            }
            finally
            {
                replacementServer?.Dispose();
                oldServer.Dispose();
            }
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

        private Task<HttpResponseMessage> PostToUrlAsync(HttpClient client, string url, string body, string accept)
        {
            HttpRequestMessage request = new(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", accept);
            return client.SendAsync(request);
        }

        private static async Task<JObject> SendRpcToUrlAsync(HttpClient client, string url, JObject body)
        {
            using StringContent content = new(body.ToString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(url, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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

        private sealed class ReentrantCancellationTool : IMcpTool
        {
            public string Name => "blocking";
            public string Description => "Blocks until cancelled and restarts on cancellation.";
            public string InputSchemaJson => "{\"type\":\"object\"}";
            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Action OnCancelled { get; set; }

            public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() => OnCancelled?.Invoke());
                Started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return McpToolResult.Text("cancelled");
            }
        }
    }
}
