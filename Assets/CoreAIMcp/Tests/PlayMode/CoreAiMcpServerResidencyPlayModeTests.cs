using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// Residency survives the REAL server path: the <see cref="CoreAiMcpServer"/> composition
    /// (<c>BuildRegistry</c> over a real container) plus <see cref="McpRpcDispatcher"/>
    /// <c>tools/list</c> and <c>tools/call</c>, with the server itself marshalling calls on the
    /// player loop. EditMode proves the registry logic; this proves the server honours it.
    /// </summary>
    public sealed class CoreAiMcpServerResidencyPlayModeTests
    {
        private const string MissingWorldHostLog =
            "[CoreAI] [Core] [CoreAiMods] RbxWorldHost NOT resolved — mods run headless. " +
            "Instance.new / workspace mutations produce no GameObjects. " +
            "Check: (1) RbxWorldHost component exists in the scene, " +
            "(2) CoreAiModsLifetimeScope.robloxWorldHost is wired to it, " +
            "(3) link.xml preserves CoreAI.RbxApi.Binding assembly.";

        private const string DynamicToolName = "screenshot";

        private GameObject _host;
        private CoreAiMcpServer _server;
        private string _previousDynamic;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _host = new GameObject("CoreAiMcpServerResidencyTestHost");
            _server = _host.AddComponent<CoreAiMcpServer>();
            _previousDynamic = Environment.GetEnvironmentVariable(McpToolResidencyPolicies.DynamicVariableName);
            Environment.SetEnvironmentVariable(McpToolResidencyPolicies.DynamicVariableName, DynamicToolName);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Environment.SetEnvironmentVariable(
                McpToolResidencyPolicies.DynamicVariableName, _previousDynamic);
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }

            _host = null;
            _server = null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator ResidencySplit_SurvivesRealServerPath()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                ContainerBuilder builder = new ContainerBuilder();
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
                builder.RegisterCore();
                builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.RegisterCoreAiMods(
                    applicationIsPlayingProvider: () => true,
                    skillTextProvider: _ => null);
                LogAssert.Expect(LogType.Error, MissingWorldHostLog);
                container = builder.Build();

                // WHY: the variable channel is read at composition time inside the provider, so the
                // untouched server component honours it with no new knobs — this is the real path.
                McpToolRegistry registry = _server.BuildRegistry(container);
                Assert.AreEqual(McpToolResidency.Dynamic, registry.ResidencyOf(DynamicToolName));
                McpRpcDispatcher dispatcher =
                    new(registry, new McpSessionStore(), _server);

                Task<McpDispatchResult>[] slot = new Task<McpDispatchResult>[1];

                yield return Dispatch(dispatcher, ListRequest(), slot);
                JArray listed = (JArray)ResultOf(slot)["tools"];
                HashSet<string> names = new(listed.Select(entry => entry["name"]!.ToString()));
                Assert.IsFalse(names.Contains(DynamicToolName),
                    "a dynamic tool is absent from a live tools/list.");
                Assert.IsTrue(names.Contains(CoreAiToolsBrokerMcpTool.ToolName),
                    "a native tool (the broker) is still listed.");

                yield return BrokerDispatch(dispatcher, slot, "list", null);
                string brokerListed = ResultOf(slot)["content"]![0]!["text"]!.ToString();
                StringAssert.Contains(DynamicToolName, brokerListed,
                    "the broker lists the dynamic tool through the real tools/call path.");

                yield return BrokerDispatch(dispatcher, slot, "describe", DynamicToolName);
                JObject described = JObject.Parse(ResultOf(slot)["content"]![0]!["text"]!.ToString());
                Assert.IsNotNull(described["inputSchema"],
                    "the broker serves the dynamic tool's full schema through the real path.");

                yield return BrokerDispatch(dispatcher, slot, "call", DynamicToolName);
                JObject callResult = ResultOf(slot);
                Assert.IsNotNull(callResult["content"],
                    "the broker call returns a well-formed MCP result through the real path.");
                string callText = callResult["content"]![0]!["text"]!.ToString();
                StringAssert.Contains("screenshot:",
                    callText, "the broker forwards to the real tool and returns its result verbatim.");

                yield return Dispatch(dispatcher, CallRequest(DynamicToolName, new JObject()), slot);
                JObject directResult = ResultOf(slot);
                Assert.AreEqual(callText, directResult["content"]![0]!["text"]!.ToString(),
                    "hiding is not access control: the dynamic tool still answers a direct tools/call.");
            }
            finally
            {
                container?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator WithoutVariable_RealServerPathListsScreenshot()
        {
            Environment.SetEnvironmentVariable(McpToolResidencyPolicies.DynamicVariableName, null);

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            IObjectResolver container = null;
            try
            {
                ContainerBuilder builder = new ContainerBuilder();
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
                builder.RegisterCore();
                builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.RegisterCoreAiMods(
                    applicationIsPlayingProvider: () => true,
                    skillTextProvider: _ => null);
                LogAssert.Expect(LogType.Error, MissingWorldHostLog);
                container = builder.Build();

                McpToolRegistry registry = _server.BuildRegistry(container);
                Assert.AreEqual(McpToolResidency.Native, registry.ResidencyOf(DynamicToolName));
                Assert.IsTrue(
                    registry.ToListJson().Select(entry => entry["name"]!.ToString()).Contains(DynamicToolName),
                    "a composition supplying nothing lists every tool, exactly as before.");
            }
            finally
            {
                container?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }

            yield break;
        }

        private static JObject ListRequest()
        {
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = McpMethods.ToolsList };
        }

        private static JObject CallRequest(string name, JObject arguments)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = McpMethods.ToolsCall,
                ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments },
            };
        }

        // WHY: mirror the PlayModeTestAwait pattern (bounded frame wait, no time-based sleeps) without
        // dragging the CoreAiUnity test-shared assembly into this package: tools/call resolves only
        // when the player loop pumps the server's main-thread queue, so the test yields frames until
        // the dispatch task completes instead of assuming a fixed count. The finished task is handed
        // back through the single-element slot because an iterator method cannot return a value.
        private static IEnumerator Dispatch(
            McpRpcDispatcher dispatcher, JObject request, Task<McpDispatchResult>[] slot)
        {
            Task<McpDispatchResult> pending = dispatcher.DispatchAsync(request, CancellationToken.None);
            slot[0] = pending;
            int frames = 0;
            while (!pending.IsCompleted && frames < 600)
            {
                frames++;
                yield return null;
            }

            Assert.IsTrue(pending.IsCompleted,
                "the dispatcher did not answer: the server's Update never pumped the main-thread queue.");
            Assert.IsNull(pending.Exception, "dispatch threw: " + pending.Exception?.InnerException?.Message);
        }

        private static JObject ResultOf(Task<McpDispatchResult>[] slot)
        {
            return (JObject)slot[0].Result.Response["result"];
        }

        private static IEnumerator BrokerDispatch(
            McpRpcDispatcher dispatcher, Task<McpDispatchResult>[] slot, string action, string tool)
        {
            JObject arguments = new() { ["action"] = action };
            if (tool != null)
            {
                arguments["tool"] = tool;
            }

            if (action == "call")
            {
                arguments["arguments_json"] = "{}";
            }

            return Dispatch(dispatcher,
                CallRequest(CoreAiToolsBrokerMcpTool.ToolName, arguments), slot);
        }
    }
}
