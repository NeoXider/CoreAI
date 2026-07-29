using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// JSON-RPC / MCP framing tests for <see cref="McpRpcDispatcher"/>: initialize, tools/list,
    /// tools/call, notifications, and the error mappings (unknown method, unknown tool, malformed body).
    /// No sockets - the dispatcher is pure.
    /// </summary>
    public sealed class McpRpcDispatcherEditModeTests
    {
        private static McpRpcDispatcher NewDispatcher(params IMcpTool[] tools)
        {
            return new McpRpcDispatcher(new McpToolRegistry(tools), new McpSessionStore(),
                new InlineMainThreadDispatcher());
        }

        private static JObject Request(string method, JToken id, JObject parameters = null)
        {
            JObject obj = new() { ["jsonrpc"] = "2.0", ["method"] = method };
            if (id != null)
            {
                obj["id"] = id;
            }

            if (parameters != null)
            {
                obj["params"] = parameters;
            }

            return obj;
        }

        [Test]
        public async Task Initialize_EchoesProtocolVersion_AndReturnsCapabilitiesAndSession()
        {
            McpRpcDispatcher dispatcher = NewDispatcher();
            JObject request = Request(McpMethods.Initialize, 1,
                new JObject { ["protocolVersion"] = "2025-03-26" });

            McpDispatchResult result = await dispatcher.DispatchAsync(request, CancellationToken.None);

            Assert.IsFalse(result.IsNotification);
            Assert.IsNotNull(result.IssuedSessionId, "initialize must issue a session id.");
            JObject res = (JObject)result.Response["result"];
            Assert.AreEqual("2025-03-26", res["protocolVersion"]!.ToString(), "must echo the client's version.");
            Assert.IsNotNull(res["capabilities"]!["tools"], "capabilities.tools must be present.");
            Assert.AreEqual(McpServerInfo.Name, res["serverInfo"]!["name"]!.ToString());
        }

        [Test]
        public async Task Initialize_UnknownProtocolVersion_DoesNotCrash_AndEchoesIt()
        {
            McpRpcDispatcher dispatcher = NewDispatcher();
            JObject request = Request(McpMethods.Initialize, 7,
                new JObject { ["protocolVersion"] = "9999-99-99-experimental" });

            McpDispatchResult result = await dispatcher.DispatchAsync(request, CancellationToken.None);

            Assert.IsNull(result.Response["error"], "an unknown version must not become an error.");
            Assert.AreEqual("9999-99-99-experimental",
                result.Response["result"]!["protocolVersion"]!.ToString());
        }

        [Test]
        public async Task Initialize_NoProtocolVersion_FallsBackToDefault()
        {
            McpRpcDispatcher dispatcher = NewDispatcher();
            McpDispatchResult result =
                await dispatcher.DispatchAsync(Request(McpMethods.Initialize, 2), CancellationToken.None);

            Assert.AreEqual(McpServerInfo.DefaultProtocolVersion,
                result.Response["result"]!["protocolVersion"]!.ToString());
        }

        [Test]
        public async Task InitializedNotification_ProducesNoBody()
        {
            McpRpcDispatcher dispatcher = NewDispatcher();
            // No id -> notification.
            JObject request = Request(McpMethods.InitializedNotification, null);

            McpDispatchResult result = await dispatcher.DispatchAsync(request, CancellationToken.None);

            Assert.IsTrue(result.IsNotification);
            Assert.IsNull(result.Response);
        }

        [Test]
        public async Task ToolsList_ReturnsRegisteredToolsWithSchemas()
        {
            McpRpcDispatcher dispatcher = NewDispatcher(new FakeMcpTool("alpha"), new FakeMcpTool("beta"));

            McpDispatchResult result =
                await dispatcher.DispatchAsync(Request(McpMethods.ToolsList, 3), CancellationToken.None);

            JArray tools = (JArray)result.Response["result"]!["tools"];
            Assert.AreEqual(2, tools.Count);
            Assert.AreEqual("alpha", tools[0]["name"]!.ToString());
            Assert.IsNotNull(tools[0]["inputSchema"]!["type"], "each tool must expose a JSON Schema.");
        }

        [Test]
        public async Task ToolsCall_InvokesToolAndWrapsContent()
        {
            FakeMcpTool tool = new("echo_tool");
            McpRpcDispatcher dispatcher = NewDispatcher(tool);
            JObject parameters = new()
            {
                ["name"] = "echo_tool",
                ["arguments"] = new JObject { ["echo"] = "hi" }
            };

            McpDispatchResult result =
                await dispatcher.DispatchAsync(Request(McpMethods.ToolsCall, 4, parameters), CancellationToken.None);

            Assert.AreEqual(1, tool.InvocationCount);
            JObject callResult = (JObject)result.Response["result"];
            Assert.IsFalse(callResult["isError"]!.Value<bool>());
            Assert.AreEqual("echo_tool:hi", callResult["content"]![0]!["text"]!.ToString());
        }

        [Test]
        public async Task ToolsCall_UnknownTool_ReturnsInvalidParams()
        {
            McpRpcDispatcher dispatcher = NewDispatcher(new FakeMcpTool("known"));
            JObject parameters = new() { ["name"] = "missing" };

            McpDispatchResult result =
                await dispatcher.DispatchAsync(Request(McpMethods.ToolsCall, 5, parameters), CancellationToken.None);

            Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, result.Response["error"]!["code"]!.Value<int>());
        }

        [Test]
        public async Task UnknownMethod_ReturnsMethodNotFound()
        {
            McpRpcDispatcher dispatcher = NewDispatcher();

            McpDispatchResult result =
                await dispatcher.DispatchAsync(Request("does/not/exist", 6), CancellationToken.None);

            Assert.AreEqual(JsonRpcErrorCodes.MethodNotFound, result.Response["error"]!["code"]!.Value<int>());
        }

        [Test]
        public async Task ToolsCall_WhenTheMainThreadStalls_ReturnsAJsonRpcErrorNotAHang()
        {
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(new[] { new FakeMcpTool("echo_tool") }),
                new McpSessionStore(), new StallingMainThreadDispatcher());
            JObject parameters = new() { ["name"] = "echo_tool" };

            McpDispatchResult result =
                await dispatcher.DispatchAsync(Request(McpMethods.ToolsCall, 8, parameters), CancellationToken.None);

            Assert.AreEqual(JsonRpcErrorCodes.InternalError, result.Response["error"]!["code"]!.Value<int>());
            StringAssert.Contains("timed out", result.Response["error"]!["message"]!.ToString());
            StringAssert.Contains("paused", result.Response["error"]!["message"]!.ToString(),
                "the error must name the cause so the caller can fix it.");
        }

        /// <summary>Test double for a player loop that never drains the queue (paused game / disabled host).</summary>
        private sealed class StallingMainThreadDispatcher : IMainThreadDispatcher
        {
            public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work)
            {
                return Task.FromException<T>(new TimeoutException(
                    "the Unity main thread never drained the MCP queue within 30s - the game is paused, " +
                    "the CoreAiMcpServer component is disabled, or its GameObject is inactive."));
            }
        }

        [Test]
        public void MalformedJson_MapsToParseError()
        {
            bool ok = JsonRpc.TryParse("{ this is not json ", out JObject request, out JObject error);

            Assert.IsFalse(ok);
            Assert.IsNull(request);
            Assert.AreEqual(JsonRpcErrorCodes.ParseError, error["error"]!["code"]!.Value<int>());
        }

        [Test]
        public void NonObjectJson_MapsToInvalidRequest()
        {
            bool ok = JsonRpc.TryParse("[1,2,3]", out _, out JObject error);

            Assert.IsFalse(ok);
            Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, error["error"]!["code"]!.Value<int>());
        }
    }
}
