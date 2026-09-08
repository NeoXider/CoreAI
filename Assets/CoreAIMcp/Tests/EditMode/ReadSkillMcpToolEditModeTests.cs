using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// <see cref="ReadSkillMcpTool"/> serves the same skill text the in-game agent reads, and fails
    /// loudly on an unknown name.
    /// </summary>
    public sealed class ReadSkillMcpToolEditModeTests
    {
        private static ReadSkillMcpTool NewTool()
        {
            List<SkillSet> skills = new()
            {
                SkillSet.FromTextContent("Lua Modding", "Lua modding reference", "LUA REFERENCE BODY"),
                SkillSet.FromTextContent("Rbx API", "Roblox API reference", "RBX REFERENCE BODY")
            };
            return new ReadSkillMcpTool(skills);
        }

        [Test]
        public async Task ReturnsNonEmptyInstructions_ForBothBuiltInSkills()
        {
            ReadSkillMcpTool tool = NewTool();

            foreach ((string name, string expected) in new[]
                     {
                         ("Lua Modding", "LUA REFERENCE BODY"),
                         ("Rbx API", "RBX REFERENCE BODY")
                     })
            {
                McpToolResult result = await tool.InvokeAsync(new JObject { ["name"] = name }, CancellationToken.None);

                Assert.IsFalse(result.IsError, $"'{name}' should resolve.");
                JObject payload = JObject.Parse(result.Content[0].Text);
                Assert.IsTrue(payload["success"]!.Value<bool>());
                Assert.AreEqual(name, payload["skill"]!.ToString());
                Assert.AreEqual(expected, payload["instructions"]!.ToString());
                Assert.IsNotEmpty(payload["instructions"]!.ToString());
            }
        }

        [Test]
        public async Task IsCaseInsensitive()
        {
            ReadSkillMcpTool tool = NewTool();

            McpToolResult result = await tool.InvokeAsync(
                new JObject { ["name"] = "lua modding" }, CancellationToken.None);

            Assert.IsFalse(result.IsError);
        }

        [Test]
        public async Task UnknownSkill_ReturnsLoudError_ListingAvailable()
        {
            ReadSkillMcpTool tool = NewTool();

            McpToolResult result = await tool.InvokeAsync(
                new JObject { ["name"] = "Nonexistent" }, CancellationToken.None);

            Assert.IsTrue(result.IsError);
            JObject payload = JObject.Parse(result.Content[0].Text);
            Assert.IsFalse(payload["success"]!.Value<bool>());
            Assert.IsNotEmpty(payload["error"]!.ToString());
            StringAssert.Contains("Lua Modding", payload["available"]!.ToString());
        }

        [Test]
        public async Task MissingName_ReturnsError()
        {
            ReadSkillMcpTool tool = NewTool();

            McpToolResult result = await tool.InvokeAsync(new JObject(), CancellationToken.None);

            Assert.IsTrue(result.IsError);
        }

        [TestCase(null, false)]
        [TestCase("references/api.md", false)]
        [TestCase(null, true)]
        public async Task MultiFileRead_UsesSameContractAsInGameAgent(string section, bool all)
        {
            SkillSet skill = SkillSet.FromTextParts("portable", "", new[]
            {
                new KeyValuePair<string, string>("SKILL.md", "complete entry\nlast entry line"),
                new KeyValuePair<string, string>("references/api.md", "reference body")
            });
            ReadSkillMcpTool tool = new(new[] { skill });
            JObject arguments = new() { ["name"] = skill.Name, ["all"] = all };
            if (section != null) arguments["section"] = section;
            McpToolResult result = await tool.InvokeAsync(arguments, CancellationToken.None);
            Assert.IsFalse(result.IsError);
            JObject expected = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(new[] { skill }, skill.Name, section, all));
            JObject actual = JObject.Parse(result.Content[0].Text);
            Assert.That(actual["usage"].Value<string>(), Does.Not.Contain("call_skill_tool("));
            expected.Remove("usage");
            actual.Remove("usage");
            Assert.IsTrue(JToken.DeepEquals(expected, actual));
        }

        [TestCase("{\"name\":12}")]
        [TestCase("{\"name\":\"Lua Modding\",\"all\":\"true\"}")]
        [TestCase("{\"name\":\"Lua Modding\",\"section\":[]}")]
        [TestCase("{\"name\":\"Lua Modding\",\"section\":\"ref.md\",\"all\":true}")]
        public async Task MalformedRead_ReturnsToolFailure(string json)
        {
            McpToolResult result = await NewTool().InvokeAsync(JObject.Parse(json), CancellationToken.None);
            Assert.IsTrue(result.IsError);
            Assert.IsFalse(JObject.Parse(result.Content[0].Text)["success"].Value<bool>());
        }

        [Test]
        public void PreCancelledRead_DoesNotReturnSuccess()
        {
            Assert.Throws<OperationCanceledException>(() => NewTool().InvokeAsync(
                new JObject { ["name"] = "Lua Modding" }, new CancellationToken(true)));
        }
    }
}
