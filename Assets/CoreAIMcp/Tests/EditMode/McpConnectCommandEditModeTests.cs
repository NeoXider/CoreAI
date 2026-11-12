using CoreAI.Mcp.Server;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>The client command a status surface offers, and the masked form it draws.</summary>
    public sealed class McpConnectCommandEditModeTests
    {
        [Test]
        public void Build_CarriesTheTokenHeader_OnlyWithAToken()
        {
            Assert.AreEqual(
                "claude mcp add --transport http coreai http://127.0.0.1:8590/mcp --header \"Authorization: Bearer abc\"",
                McpConnectCommand.Build("http://127.0.0.1:8590/mcp", "abc"));
            Assert.AreEqual("claude mcp add --transport http coreai http://127.0.0.1:8591/mcp",
                McpConnectCommand.Build("http://127.0.0.1:8591/mcp", null));
        }

        [TestCase("Qk3xSECRETtoken0123456789", "Qk3x****")]
        [TestCase("12345678", "****")]
        [TestCase("", "")]
        [TestCase(null, null)]
        public void Mask_KeepsAtMostTheFirstFourCharacters(string token, string expected)
        {
            Assert.AreEqual(expected, McpConnectCommand.Mask(token));
        }

        [Test]
        public void BuildMasked_NeverContainsTheToken()
        {
            const string token = "Qk3xSECRETtoken0123456789";
            string masked = McpConnectCommand.BuildMasked("http://127.0.0.1:8590/mcp", token);
            StringAssert.DoesNotContain(token, masked);
            StringAssert.EndsWith("Bearer Qk3x****\"", masked);
        }
    }
}
