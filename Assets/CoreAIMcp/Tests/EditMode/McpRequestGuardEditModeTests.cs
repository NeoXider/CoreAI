using CoreAI.Mcp.Server;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The admission rules that keep a browser page (CSRF / DNS rebinding) and a stray local process out
    /// of the loopback MCP endpoint. Pure - no sockets.
    /// </summary>
    public sealed class McpRequestGuardEditModeTests
    {
        private const int Port = 8590;

        [Test]
        public void Origin_Absent_IsAllowed()
        {
            // A real MCP client is not a browser and sends no Origin at all.
            Assert.IsTrue(McpRequestGuard.IsOriginAllowed(null, Port));
            Assert.IsTrue(McpRequestGuard.IsOriginAllowed("", Port));
        }

        [Test]
        public void Origin_Loopback_IsAllowed()
        {
            Assert.IsTrue(McpRequestGuard.IsOriginAllowed($"http://127.0.0.1:{Port}", Port));
            Assert.IsTrue(McpRequestGuard.IsOriginAllowed($"http://localhost:{Port}", Port));
            Assert.IsTrue(McpRequestGuard.IsOriginAllowed($"http://[::1]:{Port}", Port));
        }

        [Test]
        public void Origin_ForeignSite_IsRejected()
        {
            Assert.IsFalse(McpRequestGuard.IsOriginAllowed("https://evil.example", Port),
                "a web page must never be able to drive the game.");
            Assert.IsFalse(McpRequestGuard.IsOriginAllowed($"http://evil.example:{Port}", Port));
            Assert.IsFalse(McpRequestGuard.IsOriginAllowed("null", Port), "an opaque origin is a sandboxed page.");
        }

        [Test]
        public void Origin_LoopbackOnAnotherPort_IsRejected()
        {
            Assert.IsFalse(McpRequestGuard.IsOriginAllowed("http://127.0.0.1:9999", Port),
                "another local web app is still a foreign origin.");
        }

        [Test]
        public void Host_ReboundHostname_IsRejected()
        {
            // DNS rebinding: the socket is local, but the browser still sends the attacker's hostname.
            Assert.IsFalse(McpRequestGuard.IsHostAllowed($"evil.example:{Port}", Port));
            Assert.IsFalse(McpRequestGuard.IsHostAllowed(null, Port));
            Assert.IsTrue(McpRequestGuard.IsHostAllowed($"127.0.0.1:{Port}", Port));
            Assert.IsTrue(McpRequestGuard.IsHostAllowed($"localhost:{Port}", Port));
        }

        [Test]
        public void ContentType_SimpleCorsEncodings_AreRejected()
        {
            // text/plain and the form encodings are exactly what a page can POST without a preflight.
            Assert.IsFalse(McpRequestGuard.IsContentTypeAllowed("text/plain"));
            Assert.IsFalse(McpRequestGuard.IsContentTypeAllowed("application/x-www-form-urlencoded"));
            Assert.IsFalse(McpRequestGuard.IsContentTypeAllowed("multipart/form-data; boundary=x"));
        }

        [Test]
        public void ContentType_Json_IsAllowed()
        {
            Assert.IsTrue(McpRequestGuard.IsContentTypeAllowed("application/json"));
            Assert.IsTrue(McpRequestGuard.IsContentTypeAllowed("application/json; charset=utf-8"));
            Assert.IsTrue(McpRequestGuard.IsContentTypeAllowed("application/vnd.custom+json"));
            Assert.IsTrue(McpRequestGuard.IsContentTypeAllowed(null), "an absent type must not break clients.");
        }

        [Test]
        public void Authorization_WithoutConfiguredToken_AlwaysPasses()
        {
            Assert.IsTrue(McpRequestGuard.IsAuthorized(null, null));
            Assert.IsTrue(McpRequestGuard.IsAuthorized(null, ""));
        }

        [Test]
        public void Authorization_MatchesWithAndWithoutBearerPrefix()
        {
            Assert.IsTrue(McpRequestGuard.IsAuthorized("Bearer s3cret", "s3cret"));
            Assert.IsTrue(McpRequestGuard.IsAuthorized("bearer s3cret", "s3cret"));
            Assert.IsTrue(McpRequestGuard.IsAuthorized("s3cret", "s3cret"));
        }

        [Test]
        public void Authorization_WrongOrMissingToken_IsRejected()
        {
            Assert.IsFalse(McpRequestGuard.IsAuthorized(null, "s3cret"));
            Assert.IsFalse(McpRequestGuard.IsAuthorized("Bearer wrong", "s3cret"));
            Assert.IsFalse(McpRequestGuard.IsAuthorized("Bearer s3cre", "s3cret"), "a prefix must not pass.");
            Assert.IsFalse(McpRequestGuard.IsAuthorized("Bearer s3crett", "s3cret"));
        }

        [Test]
        public void GenerateToken_IsRandomAndUrlSafe()
        {
            string first = McpRequestGuard.GenerateToken();
            string second = McpRequestGuard.GenerateToken();

            Assert.AreNotEqual(first, second, "each start must get its own token.");
            Assert.GreaterOrEqual(first.Length, 24, "the token must carry real entropy.");
            foreach (char c in first)
            {
                Assert.IsTrue(char.IsLetterOrDigit(c) || c == '-' || c == '_',
                    $"token must be header/URL safe, found '{c}'.");
            }
        }
    }
}
