using System;
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

        [SetUp]
        public void SetUp()
        {
            _port = FreeLoopbackPort();
            McpToolRegistry registry = new(new[] { new FakeMcpTool("echo_tool") });
            McpSessionStore sessions = new();
            McpRpcDispatcher dispatcher = new(registry, sessions, new InlineMainThreadDispatcher());
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
            Assert.AreEqual("3000-01-01-weird", payload["result"]!["protocolVersion"]!.ToString());
        }

        [Test]
        public async Task Get_ReturnsMethodNotAllowed()
        {
            using HttpClient client = new();
            HttpResponseMessage response = await client.GetAsync(Url);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
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
