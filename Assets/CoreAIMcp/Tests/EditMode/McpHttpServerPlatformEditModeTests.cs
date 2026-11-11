using System;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// A WebGL player has no listening sockets. Starting the HTTP transport there must refuse at once with a
    /// message that says why and what to do instead, not fail somewhere inside the socket layer. The WebGL
    /// define cannot be set in the editor, so the refusal is exercised through the per-instance switch the
    /// define feeds, and the editor value of that define is pinned separately.
    /// </summary>
    public sealed class McpHttpServerPlatformEditModeTests
    {
        private static McpHttpServer NewServer()
        {
            McpRpcDispatcher dispatcher = new(new McpToolRegistry(null), new McpSessionStore(),
                new InlineMainThreadDispatcher());
            return new McpHttpServer(1, dispatcher);
        }

        [Test]
        public void Start_WithoutListenerSupport_RefusesWithAnActionableReason()
        {
            using McpHttpServer server = NewServer();
            server.PlatformSupportsListener = false;

            PlatformNotSupportedException refusal = Assert.Throws<PlatformNotSupportedException>(server.Start);

            StringAssert.Contains("WebGL", refusal.Message);
            StringAssert.Contains("editor or desktop build", refusal.Message);
            Assert.IsFalse(server.IsRunning, "a refused start must not leave a listener behind.");
        }

        [Test]
        public void ListenerSupport_IsOnOutsideAWebGlPlayer()
        {
            using McpHttpServer server = NewServer();

            Assert.IsTrue(McpHttpServer.IsSupportedOnThisPlatform);
            Assert.IsTrue(server.PlatformSupportsListener);
        }
    }
}
