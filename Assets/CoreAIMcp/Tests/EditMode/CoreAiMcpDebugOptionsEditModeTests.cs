using System;
using System.Collections.Generic;
using CoreAI.Mcp.Server;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The switches of the MCP debug start-up: off by default, the command line wins over the page URL, which wins
    /// over the environment; the bridge key is read from the URL fragment only; host admin is on unless withheld;
    /// an editor batch run (the test runner) honours only an explicit command-line switch.
    /// </summary>
    public sealed class CoreAiMcpDebugOptionsEditModeTests
    {
        [Test]
        public void NothingAsked_IsOff_AndStartsNothing()
        {
            CoreAiMcpDebugOptions options = CoreAiMcpDebugOptions.Read(new[] { "Game.exe", "-screen-width", "800" },
                Environment(), "http://localhost:8777/index.html?lang=en");

            Assert.AreEqual(CoreAiMcpDebugMode.Off, options.Mode);
            Assert.IsFalse(options.ShouldStart(false, false, false));
            Assert.IsFalse(options.ShouldStart(true, false, false));
        }

        [TestCase(new[] { "Game.exe", "-coreaiMcp" }, 8590)]
        [TestCase(new[] { "Game.exe", "-coreaiMcp", "9100" }, 9100)]
        [TestCase(new[] { "Game.exe", "-coreaiMcp", "-batchmode" }, 8590)]
        [TestCase(new[] { "Game.exe", "-coreaiMcp", "notaport" }, 8590)]
        public void CommandLineSwitch_StartsTheServer(string[] commandLine, int port)
        {
            CoreAiMcpDebugOptions options = CoreAiMcpDebugOptions.Read(commandLine, null, null);

            Assert.AreEqual(CoreAiMcpDebugMode.Server, options.Mode);
            Assert.AreEqual(port, options.Port);
            Assert.IsTrue(options.HostAdmin, "the debug start-up grants mod management unless told not to");
            Assert.AreEqual("command line", options.Source);
        }

        [TestCase("1", 8590)]
        [TestCase("true", 8590)]
        [TestCase("9200", 9200)]
        public void EnvironmentVariable_StartsTheServer(string value, int port)
        {
            CoreAiMcpDebugOptions options = CoreAiMcpDebugOptions.Read(null,
                Environment(("COREAI_MCP", value)), null);

            Assert.AreEqual(CoreAiMcpDebugMode.Server, options.Mode);
            Assert.AreEqual(port, options.Port);
            Assert.AreEqual("environment", options.Source);
        }

        [TestCase("0")]
        [TestCase("false")]
        [TestCase("")]
        public void EnvironmentVariable_OffValues_StartNothing(string value)
        {
            Assert.IsFalse(CoreAiMcpDebugOptions.Read(null, Environment(("COREAI_MCP", value)), null).Requested);
        }

        [Test]
        public void WebGlUrl_AsksForTheBridge_WithTheKeyFromTheFragmentOnly()
        {
            CoreAiMcpDebugOptions options = CoreAiMcpDebugOptions.Read(null, null,
                "http://localhost:8777/index.html?coreaiMcp=8700#coreaiMcpKey=abc%2Bdef");

            Assert.AreEqual(CoreAiMcpDebugMode.Bridge, options.Mode);
            Assert.AreEqual(8700, options.Port);
            Assert.AreEqual("abc+def", options.BridgeKey);
            Assert.AreEqual("url", options.Source);
            Assert.IsFalse(options.BridgeKeyInQueryIgnored);
        }

        [Test]
        public void BridgeKeyInTheQuery_IsIgnoredAndFlagged()
        {
            CoreAiMcpDebugOptions options = CoreAiMcpDebugOptions.Read(null, null,
                "http://localhost:8777/?coreaiMcp&coreaiMcpKey=leaked");

            Assert.AreEqual(CoreAiMcpDebugMode.Bridge, options.Mode);
            Assert.AreEqual(CoreAiMcpDebugOptions.DefaultBridgePort, options.Port);
            Assert.IsNull(options.BridgeKey, "a key in the query string reaches the page server and its logs");
            Assert.IsTrue(options.BridgeKeyInQueryIgnored);
        }

        [Test]
        public void CommandLine_WinsOverUrlAndEnvironment()
        {
            CoreAiMcpDebugOptions options = CoreAiMcpDebugOptions.Read(
                new[] { "Game.exe", "-coreaiMcpBridge", "8800" },
                Environment(("COREAI_MCP", "1"), ("COREAI_MCP_BRIDGE_KEY", "envkey")),
                "http://localhost/?coreaiMcp=8700");

            Assert.AreEqual(CoreAiMcpDebugMode.Bridge, options.Mode);
            Assert.AreEqual(8800, options.Port);
            Assert.AreEqual("envkey", options.BridgeKey);
        }

        [Test]
        public void HostAdmin_CanBeWithheldFromEverySource()
        {
            Assert.IsFalse(CoreAiMcpDebugOptions.Read(new[] { "-coreaiMcp", "-coreaiMcpNoAdmin" }, null, null).HostAdmin);
            Assert.IsFalse(CoreAiMcpDebugOptions.Read(null, Environment(("COREAI_MCP", "1"), ("COREAI_MCP_ADMIN", "0")),
                null).HostAdmin);
            Assert.IsFalse(CoreAiMcpDebugOptions.Read(null, null, "http://localhost/?coreaiMcp=8591&coreaiMcpNoAdmin")
                .HostAdmin);
        }

        [Test]
        public void EditorBatchRun_HonoursOnlyTheCommandLine()
        {
            CoreAiMcpDebugOptions fromEnvironment = CoreAiMcpDebugOptions.Read(null, Environment(("COREAI_MCP", "1")), null);
            CoreAiMcpDebugOptions fromCommandLine = CoreAiMcpDebugOptions.Read(new[] { "Unity.exe", "-coreaiMcp" }, null, null);
            CoreAiMcpDebugOptions none = CoreAiMcpDebugOptions.Read(null, null, null);

            Assert.IsFalse(fromEnvironment.ShouldStart(isEditor: true, isBatchMode: true, editorToggle: false));
            Assert.IsFalse(none.ShouldStart(isEditor: true, isBatchMode: true, editorToggle: true));
            Assert.IsTrue(fromCommandLine.ShouldStart(isEditor: true, isBatchMode: true, editorToggle: false));
            Assert.IsTrue(fromEnvironment.ShouldStart(isEditor: true, isBatchMode: false, editorToggle: false));
            Assert.IsTrue(none.ShouldStart(isEditor: true, isBatchMode: false, editorToggle: true));
            Assert.IsFalse(none.ShouldStart(isEditor: false, isBatchMode: false, editorToggle: true),
                "the editor toggle means nothing in a player");
            Assert.IsTrue(fromEnvironment.ShouldStart(isEditor: false, isBatchMode: true, editorToggle: false),
                "a headless player started with the variable still starts");
        }

        [Test]
        public void Token_IsMaskedInTheLogUnlessTheDeveloperAsks()
        {
            Assert.IsFalse(CoreAiMcpDebugOptions.Read(new[] { "-coreaiMcp" }, null, null).PrintToken);
            Assert.IsTrue(CoreAiMcpDebugOptions.Read(new[] { "-coreaiMcp", "-coreaiMcpPrintToken" }, null, null).PrintToken);
            Assert.IsTrue(CoreAiMcpDebugOptions.Read(null, Environment(("COREAI_MCP", "1"), ("COREAI_MCP_PRINT_TOKEN", "1")),
                null).PrintToken);
        }

        [Test]
        public void BridgeHost_ComesFromTheEnvironment_OrTheHub()
        {
            CoreAiMcpDebugOptions fromEnvironment = CoreAiMcpDebugOptions.Read(null,
                Environment(("COREAI_MCP_BRIDGE", "8591"), ("COREAI_MCP_BRIDGE_HOST", "192.168.1.20")), null);
            CoreAiMcpDebugOptions fromHub = CoreAiMcpDebugOptions.ForBridge(" 10.0.0.5 ", 9000, " key ", false);

            Assert.AreEqual("192.168.1.20", fromEnvironment.BridgeHost);
            Assert.AreEqual(CoreAiMcpDebugMode.Bridge, fromHub.Mode);
            Assert.AreEqual("10.0.0.5", fromHub.BridgeHost);
            Assert.AreEqual(9000, fromHub.Port);
            Assert.AreEqual("key", fromHub.BridgeKey);
            Assert.IsFalse(fromHub.HostAdmin);
            Assert.AreEqual(CoreAiMcpDebugOptions.DefaultBridgePort, CoreAiMcpDebugOptions.ForBridge(null, 0, null, true).Port);
        }

        private static Func<string, string> Environment(params (string Name, string Value)[] variables)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach ((string name, string value) in variables) values[name] = value;
            return name => values.TryGetValue(name, out string value) ? value : null;
        }
    }
}
