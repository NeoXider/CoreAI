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
    /// Version negotiation per the handshake-based MCP revisions: a requested version the server supports
    /// MUST be echoed; anything else gets the server's default. Also the <c>instructions</c> field an MCP
    /// client hands to its model. Engine-free.
    /// </summary>
    public sealed class McpProtocolNegotiationEditModeTests
    {
        private static async Task<JObject> InitializeAsync(McpRpcDispatcher dispatcher, string requestedVersion)
        {
            JObject parameters = new() { ["capabilities"] = new JObject() };
            if (requestedVersion != null) parameters["protocolVersion"] = requestedVersion;
            McpDispatchResult result = await dispatcher.DispatchAsync(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = McpMethods.Initialize, ["params"] = parameters
            }, CancellationToken.None);
            return (JObject)result.Response["result"];
        }

        private static McpRpcDispatcher NewDispatcher()
        {
            return new McpRpcDispatcher(new McpToolRegistry(null), new McpSessionStore(), new InlineMainThreadDispatcher());
        }

        [TestCase("2025-11-25")]
        [TestCase("2025-06-18")]
        public async Task Initialize_EchoesEveryNegotiatedVersion(string requested)
        {
            JObject result = await InitializeAsync(NewDispatcher(), requested);

            Assert.AreEqual(requested, (string)result["protocolVersion"],
                "a supported requested version MUST be answered unchanged.");
        }

        [TestCase("2025-03-26")]
        [TestCase("2024-11-05")]
        [TestCase("2026-07-28")]
        [TestCase(null)]
        public async Task Initialize_UnnegotiatedVersion_GetsTheDefault(string requested)
        {
            JObject result = await InitializeAsync(NewDispatcher(), requested);

            Assert.AreEqual(McpServerInfo.DefaultProtocolVersion, (string)result["protocolVersion"],
                "2025-03-26 is not negotiated because this server does not receive JSON-RPC batches.");
        }

        [Test]
        public async Task Initialize_NonStringVersion_GetsTheDefault()
        {
            McpDispatchResult result = await NewDispatcher().DispatchAsync(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = McpMethods.Initialize,
                ["params"] = new JObject { ["protocolVersion"] = 20250618 }
            }, CancellationToken.None);

            Assert.AreEqual(McpServerInfo.DefaultProtocolVersion, (string)result.Response["result"]["protocolVersion"]);
        }

        [TestCase("2025-11-25", true)]
        [TestCase("2025-06-18", true)]
        [TestCase("2025-03-26", true)]
        [TestCase("2026-07-28", false)]
        [TestCase("2024-11-05", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void ProtocolVersionHeader_AcceptsOnlyServedRevisions(string header, bool accepted)
        {
            Assert.AreEqual(accepted, McpServerInfo.IsAcceptedProtocolVersionHeader(header));
        }

        [Test]
        public async Task Initialize_ReturnsConfiguredInstructions()
        {
            McpRpcDispatcher dispatcher = NewDispatcher();
            dispatcher.Instructions = McpServerInfo.DefaultInstructions;

            JObject result = await InitializeAsync(dispatcher, "2025-06-18");

            Assert.AreEqual(McpServerInfo.DefaultInstructions, (string)result["instructions"]);
            StringAssert.Contains("read_skill", McpServerInfo.DefaultInstructions);
            StringAssert.Contains("coreai_tools", McpServerInfo.DefaultInstructions);
        }

        [TestCase(null)]
        [TestCase("   ")]
        public async Task Initialize_BlankInstructions_AreOmitted(string instructions)
        {
            McpRpcDispatcher dispatcher = NewDispatcher();
            dispatcher.Instructions = instructions;

            JObject result = await InitializeAsync(dispatcher, "2025-06-18");

            Assert.IsNull(result["instructions"]);
        }
    }
}
