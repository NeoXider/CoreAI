using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The adapter that offers an in-game agent's tool over MCP unchanged (same schema, same binding, same result,
    /// images as image content, success/ok false as isError), and the wrapping rules: dedicated adapters and the
    /// not-exposed table are never wrapped, the first tool with a name wins.
    /// </summary>
    public sealed class McpInGameToolsEditModeTests
    {
        [Test]
        public async Task Adapter_KeepsTheSchema_AndBindsTheArgumentsLikeTheModelCall()
        {
            AIFunction function = AIFunctionFactory.Create((Func<string, int, string>)Greet,
                new AIFunctionFactoryOptions { Name = "greet", Description = "Greets." });
            LlmFunctionMcpTool tool = new(function);

            McpToolResult result = await tool.InvokeAsync(new JObject { ["who"] = "Ann", ["times"] = 2 },
                CancellationToken.None);

            Assert.AreEqual("greet", tool.Name);
            Assert.AreEqual("Greets.", tool.Description);
            JObject schema = JObject.Parse(tool.InputSchemaJson);
            Assert.AreEqual("object", (string)schema["type"]);
            Assert.IsNotNull(schema["properties"]["who"]);
            Assert.AreEqual("integer", (string)schema["properties"]["times"]["type"]);
            Assert.IsFalse(result.IsError);
            Assert.AreEqual("hi Ann hi Ann", result.Content[0].Text);
        }

        [TestCase("{\"success\":false,\"error\":\"x\"}", true)]
        [TestCase("{\"ok\":false,\"error\":\"rate_limited\"}", true)]
        [TestCase("{\"ok\":true}", false)]
        [TestCase("plain text", false)]
        [TestCase("  ", false)]
        public void Result_FailureFlagsBecomeIsError_AndTheTextStays(string text, bool isError)
        {
            McpToolResult result = LlmFunctionMcpTool.ToResult(text);
            Assert.AreEqual(isError, result.IsError);
            Assert.AreEqual(text, result.Content[0].Text);
        }

        [Test]
        public void Result_WithImages_IsTextThenImageContent()
        {
            byte[] png = { 1, 2, 3 };
            McpToolResult result = LlmFunctionMcpTool.ToResult(
                new LlmToolImageResult("{\"ok\":true,\"camera\":\"Main\"}", AiAttachment.Image(png, "image/png")));

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("text", result.Content[0].Type);
            Assert.AreEqual("image", result.Content[1].Type);
            Assert.AreEqual(Convert.ToBase64String(png), result.Content[1].Data);
            Assert.AreEqual("image/png", result.Content[1].MimeType);
        }

        [TestCase("{}", "{\"type\":\"object\"}")]
        [TestCase("", "{\"type\":\"object\"}")]
        [TestCase("not json", "{\"type\":\"object\"}")]
        [TestCase("{\"type\":\"string\"}", "{\"type\":\"object\"}")]
        public void Schema_ThatMcpCannotTake_BecomesTheEmptyObjectSchema(string schema, string expected)
        {
            Assert.AreEqual(expected, LlmFunctionMcpTool.ObjectSchema(schema));
        }

        [Test]
        public void Wrap_SkipsDedicatedAndNotExposedNames_AndTheFirstToolWithANameWins()
        {
            FakeTool first = new("set_a", "camera_look", "manage_mods", "memory");
            FakeTool second = new("set_b", "camera_look", "get_inventory");

            IReadOnlyList<IMcpTool> wrapped = McpInGameTools.Wrap(new ILlmTool[] { first, second },
                new[] { "game_status" });

            CollectionAssert.AreEqual(new[] { "camera_look", "get_inventory" }, Names(wrapped));
            Assert.AreEqual("set_a camera_look", wrapped[0].Description, "the first tool's function is the one kept");
        }

        [Test]
        public void Wrap_NeverOffersTheInGameCopiesOfTheHostAdminTools()
        {
            FakeTool programmer = new("lua", "manage_mods", "save_world", "execute_lua");

            Assert.IsEmpty(McpInGameTools.Wrap(new ILlmTool[] { programmer }, null),
                "manage_mods and save_world stay behind the MCP host-admin admission");
        }

        [Test]
        public async Task Wrap_JsonInvocableToolWithoutAFunction_IsCalledWithTheArgumentsText()
        {
            JsonOnlyTool tool = new();

            IReadOnlyList<IMcpTool> wrapped = McpInGameTools.Wrap(new ILlmTool[] { tool }, null);
            McpToolResult result = await wrapped[0].InvokeAsync(new JObject { ["q"] = 1 }, CancellationToken.None);

            Assert.AreEqual("{\"q\":1}", tool.LastArguments);
            Assert.AreEqual("done", result.Content[0].Text);
        }

        [Test]
        public void NotExposed_EveryEntryHasAReason_AndIsNotAlsoADedicatedAdapter()
        {
            foreach (KeyValuePair<string, string> entry in McpInGameTools.NotExposed)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Value), entry.Key);
                Assert.IsFalse(McpInGameTools.HasDedicatedAdapter(entry.Key), entry.Key);
            }
        }

        [Test]
        public void Instructions_CarryTheProgrammersAuthoringRules_AndStaySmall()
        {
            string instructions = McpServerInfo.DefaultInstructions;

            foreach (string rule in new[]
                     {
                         "read_skill", "'Lua Modding'", "'Rbx API'", "Instance.new('Part')", "GameObject.Find",
                         "manage_mods load", "get_source", "next_from_line", "old_text", "since_reload=true",
                         "screen=true", "coreai_tools", "isError"
                     })
            {
                StringAssert.Contains(rule, instructions);
            }

            Assert.LessOrEqual(instructions.Length, 1300, "initialize is paid once per session; keep it a rule list");
        }

        private static string Greet([System.ComponentModel.Description("Who.")] string who,
            [System.ComponentModel.Description("How often.")] int times)
        {
            List<string> parts = new();
            for (int index = 0; index < times; index++) parts.Add("hi " + who);
            return string.Join(" ", parts);
        }

        private static List<string> Names(IReadOnlyList<IMcpTool> tools)
        {
            List<string> names = new();
            foreach (IMcpTool tool in tools) names.Add(tool.Name);
            return names;
        }

        private sealed class FakeTool : IAIFunctionsLlmTool
        {
            private readonly string[] _functions;

            public FakeTool(string name, params string[] functions)
            {
                Name = name;
                _functions = functions;
            }

            public string Name { get; }
            public string Description => Name;
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => false;

            public IEnumerable<AIFunction> CreateAIFunctions()
            {
                foreach (string function in _functions)
                {
                    string label = Name + " " + function;
                    yield return AIFunctionFactory.Create((Func<string>)(() => label),
                        new AIFunctionFactoryOptions { Name = function, Description = label });
                }
            }
        }

        private sealed class JsonOnlyTool : IJsonInvocableLlmTool
        {
            public string Name => "host_query";
            public string Description => "A host tool invoked from JSON.";
            public string ParametersSchema => "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"integer\"}}}";
            public bool AllowDuplicates => true;
            public string LastArguments { get; private set; }

            public Task<object> InvokeJsonAsync(string argumentsJson, CancellationToken cancellationToken = default)
            {
                LastArguments = argumentsJson;
                return Task.FromResult<object>("done");
            }
        }
    }
}
