using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// Residency (Native vs Dynamic) and broker tests for <see cref="McpToolRegistry"/>:
    /// hiding tools from <c>tools/list</c> is a context optimisation, never access control.
    /// </summary>
    public sealed class McpDynamicToolsEditModeTests
    {
        private sealed class StubExecutor : LuaTool.ILuaExecutor
        {
            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
            {
                return Task.FromResult(new LuaTool.LuaResult { Success = true });
            }
        }

        /// <summary>Test double with a realistically large schema, so the context-win test is meaningful.</summary>
        private sealed class BigFakeMcpTool : IMcpTool
        {
            public BigFakeMcpTool(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public string Description =>
                $"Fake tool {Name} with a long description spelling out every parameter, edge case, " +
                $"and usage example in exhaustive detail so the listing payload stays large, like a real tool.";

            public string InputSchemaJson { get; } =
                "{\"type\":\"object\",\"properties\":{" +
                string.Join(",", Enumerable.Range(0, 20)
                    .Select(i => $"\"param_{i:00}\":{{\"type\":\"string\"," +
                                  $"\"description\":\"Parameter number {i} with a long-winded explanation of " +
                                  "its purpose, valid values, defaults, and interactions with other parameters.\"}}")) +
                "}}";

            public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
            {
                return Task.FromResult(McpToolResult.Text($"{Name}:ok"));
            }
        }

        private static McpToolRegistry RegistryWithDynamic(
            IEnumerable<IMcpTool> tools, params string[] dynamicNames)
        {
            HashSet<string> dynamic = new(dynamicNames, StringComparer.Ordinal);
            return new McpToolRegistry(tools,
                new FuncMcpToolResidencyPolicy(tool =>
                    tool != null && dynamic.Contains(tool.Name)
                        ? McpToolResidency.Dynamic
                        : McpToolResidency.Native));
        }

        private static JObject BrokerCallArgs(string action, string tool = null, string extra = null)
        {
            JObject args = new() { ["action"] = action };
            if (tool != null)
            {
                args["tool"] = tool;
            }

            if (extra != null)
            {
                foreach (KeyValuePair<string, JToken> pair in JObject.Parse(extra))
                {
                    args[pair.Key] = pair.Value;
                }
            }

            return args;
        }

        private static string ResultText(McpToolResult result)
        {
            return result.Content[0].Text;
        }

        private static HashSet<string> ListNames(JArray list)
        {
            return new HashSet<string>(list.Select(entry => entry["name"]!.ToString()));
        }

        private static Func<string, string> ReadVars(Dictionary<string, string> variables)
        {
            return key => variables.TryGetValue(key, out string value) ? value : null;
        }

        [Test]
        public void DefaultComposition_EveryToolNative_ListMatchesTodaySet()
        {
            McpToolRegistry registry = new(new IMcpTool[] { new FakeMcpTool("a"), new FakeMcpTool("b") });
            JArray list = registry.ToListJson();

            Assert.AreEqual(new HashSet<string> { "a", "b" }, ListNames(list),
                "no policy means today's exact set, no broker, nothing hidden.");
            Assert.AreEqual(McpToolResidency.Native, registry.ResidencyOf("a"));
            Assert.AreEqual(McpToolResidency.Native, registry.ResidencyOf("b"));
            Assert.IsNotNull(registry.Find("a"), "direct lookup still resolves every tool.");
        }

        [Test]
        public void DefaultProviderComposition_AllNative_PlusBroker()
        {
            List<string> warnings = new();
            McpToolRegistry registry = CoreAiMcpToolProvider.Build(
                new StubExecutor(), null, null, null, LuaCapabilities.All, null, null, null, null, null,
                null, null, warnings.Add);

            Assert.IsEmpty(warnings);
            Assert.IsTrue(registry.Contains("execute_lua"));
            Assert.IsTrue(registry.Contains(CoreAiToolsBrokerMcpTool.ToolName));
            Assert.AreEqual(McpToolResidency.Native, registry.ResidencyOf("execute_lua"));

            JArray list = registry.ToListJson();
            Assert.AreEqual(new HashSet<string> { "execute_lua", CoreAiToolsBrokerMcpTool.ToolName },
                ListNames(list), "default provider composition is today's set plus the broker.");
        }

        [Test]
        public async Task Policy_MarksToolDynamic_HiddenFromList_BrokerServesIt()
        {
            FakeMcpTool dyn = new("dyn");
            McpToolRegistry registry = RegistryWithDynamic(
                new IMcpTool[] { new FakeMcpTool("keep"), dyn }, "dyn");

            JArray list = registry.ToListJson();
            Assert.AreEqual(new HashSet<string> { "keep", CoreAiToolsBrokerMcpTool.ToolName }, ListNames(list));
            Assert.AreEqual(McpToolResidency.Dynamic, registry.ResidencyOf("dyn"));

            IMcpTool broker = registry.Find(CoreAiToolsBrokerMcpTool.ToolName);
            Assert.IsNotNull(broker, "the broker appears exactly when a tool is dynamic.");

            JObject listed = JObject.Parse(ResultText(
                await broker.InvokeAsync(BrokerCallArgs("list"), CancellationToken.None)));
            Assert.IsTrue(listed["success"]!.Value<bool>());
            Assert.AreEqual("dyn", listed["tools"]![0]!["name"]!.ToString());
            Assert.IsNull(listed["tools"]![0]!["inputSchema"], "broker list carries no schemas.");

            JObject filtered = JObject.Parse(ResultText(await broker.InvokeAsync(
                BrokerCallArgs("list", extra: "{\"query\":\"dyn\"}"), CancellationToken.None)));
            Assert.AreEqual(1, ((JArray)filtered["tools"]).Count);
            JObject noMatch = JObject.Parse(ResultText(await broker.InvokeAsync(
                BrokerCallArgs("list", extra: "{\"query\":\"zzz-no-such-tool\"}"), CancellationToken.None)));
            Assert.AreEqual(0, ((JArray)noMatch["tools"]).Count);

            JObject described = JObject.Parse(ResultText(await broker.InvokeAsync(
                BrokerCallArgs("describe", "dyn"), CancellationToken.None)));
            Assert.IsTrue(described["success"]!.Value<bool>());
            Assert.IsNotNull(described["inputSchema"]!["properties"]!["echo"],
                "describe returns the tool's full JSON Schema.");

            McpToolResult called = await broker.InvokeAsync(
                BrokerCallArgs("call", "dyn", "{\"arguments_json\":\"{\\\"echo\\\":\\\"hi\\\"}\"}"),
                CancellationToken.None);
            Assert.IsFalse(called.IsError);
            Assert.AreEqual("dyn:hi", ResultText(called), "broker call returns the result verbatim.");
            Assert.AreEqual(1, dyn.InvocationCount);
        }

        [Test]
        public void EnvironmentVariable_MovesToolDynamic_NativeWins()
        {
            Dictionary<string, string> variables = new()
            {
                [McpToolResidencyPolicies.DynamicVariableName] = "execute_lua",
            };
            List<string> warnings = new();
            McpToolRegistry dynamicRegistry = CoreAiMcpToolProvider.Build(
                new StubExecutor(), null, null, null, LuaCapabilities.All, null, null, null, null, null,
                null, ReadVars(variables), warnings.Add);

            Assert.IsEmpty(warnings);
            Assert.AreEqual(McpToolResidency.Dynamic, dynamicRegistry.ResidencyOf("execute_lua"));
            Assert.IsFalse(ListNames(dynamicRegistry.ToListJson()).Contains("execute_lua"));

            variables[McpToolResidencyPolicies.NativeVariableName] = "execute_lua";
            McpToolRegistry nativeRegistry = CoreAiMcpToolProvider.Build(
                new StubExecutor(), null, null, null, LuaCapabilities.All, null, null, null, null, null,
                null, ReadVars(variables), warnings.Add);

            Assert.IsEmpty(warnings);
            Assert.AreEqual(McpToolResidency.Native, nativeRegistry.ResidencyOf("execute_lua"),
                "explicit NATIVE wins over explicit DYNAMIC for the same name.");
            Assert.IsTrue(ListNames(nativeRegistry.ToListJson()).Contains("execute_lua"));
        }

        [Test]
        public void EnvironmentVariable_HostPolicyAppliesToUnlistedTools()
        {
            Dictionary<string, string> variables = new()
            {
                [McpToolResidencyPolicies.DynamicVariableName] = "b",
            };
            McpToolRegistry registry = new(
                new IMcpTool[] { new FakeMcpTool("a"), new FakeMcpTool("b") },
                McpToolResidencyPolicies.FromEnvironment(
                    new[] { "a", "b" },
                    new FuncMcpToolResidencyPolicy(tool =>
                        tool.Name == "a" ? McpToolResidency.Dynamic : McpToolResidency.Native),
                    ReadVars(variables),
                    _ => { }));

            Assert.AreEqual(McpToolResidency.Dynamic, registry.ResidencyOf("a"),
                "unlisted tools fall back to the host policy.");
            Assert.AreEqual(McpToolResidency.Dynamic, registry.ResidencyOf("b"),
                "variable-listed tools override the host policy.");
        }

        [Test]
        public void EnvironmentVariable_UnknownName_WarnsOnce_ChangesNothing()
        {
            List<string> warnings = new();
            Dictionary<string, string> variables = new()
            {
                [McpToolResidencyPolicies.DynamicVariableName] = "typo_tool",
            };
            McpToolRegistry registry = CoreAiMcpToolProvider.Build(
                new StubExecutor(), null, null, null, LuaCapabilities.All, null, null, null, null, null,
                null, ReadVars(variables), warnings.Add);

            Assert.AreEqual(1, warnings.Count, "an unknown name warns exactly once.");
            StringAssert.Contains("typo_tool", warnings[0]);

            McpToolRegistry clean = CoreAiMcpToolProvider.Build(
                new StubExecutor(), null, null, null, LuaCapabilities.All, null, null, null, null, null);
            Assert.AreEqual(
                clean.ToListJson().ToString(Formatting.None),
                registry.ToListJson().ToString(Formatting.None),
                "an unknown name changes nothing.");
        }

        [Test]
        public async Task DynamicTool_CalledDirectlyByName_StillWorks()
        {
            FakeMcpTool dyn = new("dyn");
            McpToolRegistry registry = RegistryWithDynamic(
                new IMcpTool[] { new FakeMcpTool("keep"), dyn }, "dyn");
            McpRpcDispatcher dispatcher = new(registry, new McpSessionStore(), new InlineMainThreadDispatcher());

            JObject request = new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = McpMethods.ToolsCall,
                ["params"] = new JObject
                {
                    ["name"] = "dyn",
                    ["arguments"] = new JObject { ["echo"] = "direct" },
                },
            };
            McpDispatchResult result = await dispatcher.DispatchAsync(request, CancellationToken.None);

            Assert.AreEqual(1, dyn.InvocationCount, "hiding is not access control: direct calls still run.");
            JObject callResult = (JObject)result.Response["result"];
            Assert.IsFalse(callResult["isError"]!.Value<bool>());
            Assert.AreEqual("dyn:direct", callResult["content"]![0]!["text"]!.ToString());
        }

        [Test]
        public async Task Broker_Call_UnknownTool_Fails_ListingAvailableNames()
        {
            McpToolRegistry registry = RegistryWithDynamic(
                new IMcpTool[] { new FakeMcpTool("keep"), new FakeMcpTool("dyn") }, "dyn");
            IMcpTool broker = registry.Find(CoreAiToolsBrokerMcpTool.ToolName);

            McpToolResult result = await broker.InvokeAsync(
                BrokerCallArgs("call", "missing", "{\"arguments_json\":\"{}\"}"), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains("missing", ResultText(result));
            StringAssert.Contains("dyn", ResultText(result),
                "the failure must list the available dynamic names so the model can recover.");
        }

        [Test]
        public async Task Broker_Call_NativeTool_StillWorks()
        {
            McpToolRegistry registry = RegistryWithDynamic(
                new IMcpTool[] { new FakeMcpTool("keep"), new FakeMcpTool("dyn") }, "dyn");
            IMcpTool broker = registry.Find(CoreAiToolsBrokerMcpTool.ToolName);

            McpToolResult result = await broker.InvokeAsync(
                BrokerCallArgs("call", "keep", "{\"arguments_json\":\"{\\\"echo\\\":\\\"v\\\"}\"}"),
                CancellationToken.None);

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("keep:v", ResultText(result));
        }

        [Test]
        public void Broker_IsAlwaysNative_EvenWhenPolicyMarksAllDynamic()
        {
            McpToolRegistry registry = new(
                new IMcpTool[] { new FakeMcpTool("a") },
                new FuncMcpToolResidencyPolicy(_ => McpToolResidency.Dynamic));

            Assert.AreEqual(McpToolResidency.Native, registry.ResidencyOf(CoreAiToolsBrokerMcpTool.ToolName));
            Assert.IsTrue(ListNames(registry.ToListJson()).Contains(CoreAiToolsBrokerMcpTool.ToolName));
        }

        [Test]
        public void ToolsList_Payload_IsStrictlySmaller_WhenDynamic()
        {
            IMcpTool[] tools = { new BigFakeMcpTool("tool_a"), new BigFakeMcpTool("tool_b"), new BigFakeMcpTool("tool_c") };
            string allNative = new McpToolRegistry(tools, null, true).ToListJson().ToString(Formatting.None);
            string allDynamic = new McpToolRegistry(
                tools, new FuncMcpToolResidencyPolicy(_ => McpToolResidency.Dynamic)).ToListJson()
                .ToString(Formatting.None);

            Assert.Less(allDynamic.Length, allNative.Length,
                $"dynamic list ({allDynamic.Length} chars) must be strictly smaller than " +
                $"native list ({allNative.Length} chars).");
        }
    }
}
