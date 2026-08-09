using CoreAI.Mcp.Server;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The loopback MCP dev server must never run in a WebGL player, where HttpListener cannot bind
    /// a socket from inside the browser sandbox. The WebGL branch itself cannot execute off-platform
    /// (<c>Application.platform</c> is read-only), so this pins the platform gate: a refactor that
    /// drops or inverts <see cref="CoreAiMcpServer.IsWebGlPlayer"/> fails here.
    /// </summary>
    public sealed class CoreAiMcpServerWebGlEditModeTests
    {
        [Test]
        public void IsWebGlPlayer_IsFalseInEditor()
        {
            Assert.IsFalse(CoreAiMcpServer.IsWebGlPlayer,
                "The editor must keep the MCP server available; only WebGL players are gated out.");
        }
    }
}
