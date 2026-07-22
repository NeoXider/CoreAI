using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>Presence/lookup logic for <see cref="McpToolRegistry"/>.</summary>
    public sealed class McpToolRegistryEditModeTests
    {
        [Test]
        public void Registry_IndexesToolsByName()
        {
            McpToolRegistry registry = new(new[] { new FakeMcpTool("a"), new FakeMcpTool("b") });

            Assert.AreEqual(2, registry.Count);
            Assert.IsTrue(registry.Contains("a"));
            Assert.IsTrue(registry.Contains("b"));
            Assert.IsFalse(registry.Contains("c"));
            Assert.IsNotNull(registry.Find("a"));
            Assert.IsNull(registry.Find("c"));
        }

        [Test]
        public void Registry_IgnoresDuplicateNames_FirstWins()
        {
            FakeMcpTool first = new("dup");
            McpToolRegistry registry = new(new[] { first, new FakeMcpTool("dup") });

            Assert.AreEqual(1, registry.Count);
            Assert.AreSame(first, registry.Find("dup"));
        }

        [Test]
        public void Registry_NullInput_IsEmpty()
        {
            McpToolRegistry registry = new(null);
            Assert.AreEqual(0, registry.Count);
            Assert.AreEqual(0, ((JArray)registry.ToListJson()).Count);
        }

        [Test]
        public void ToListJson_EmitsNameDescriptionAndSchema()
        {
            McpToolRegistry registry = new(new[] { new FakeMcpTool("t") });
            JArray list = registry.ToListJson();

            Assert.AreEqual("t", list[0]["name"]!.ToString());
            Assert.IsNotEmpty(list[0]["description"]!.ToString());
            Assert.AreEqual("object", list[0]["inputSchema"]!["type"]!.ToString());
        }
    }
}
