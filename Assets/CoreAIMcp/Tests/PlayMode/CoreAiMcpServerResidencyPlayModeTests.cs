using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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

        [UnityTest]
        public IEnumerator LiveCatalog_AddRemoveReplace_OverOneRunningHttpServerAndSession()
        {
            RuntimeStageTool keep = new("keep", "unrelated");
            McpToolRegistry registry = new(new IMcpTool[] { keep }, null, true);
            _server.StartListening(registry, FreeLoopbackPort());
            Assert.IsTrue(_server.IsRunning, "The real loopback server must start for the lifecycle proof.");
            Assert.AreSame(registry, _server.Registry);
            string url = _server.Url;
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _server.AuthToken);
            Task<JObject> initialize = SendRpcAsync(client, url,
                new JObject { ["jsonrpc"] = "2.0", ["id"] = 10, ["method"] = McpMethods.Initialize,
                    ["params"] = new JObject { ["protocolVersion"] = McpServerInfo.DefaultProtocolVersion } });
            yield return AwaitLiveRequest(initialize);
            string session = client.DefaultRequestHeaders.GetValues(McpServerInfo.SessionHeader).Single();

            RuntimeStageTool stage = new("stage", "phase-one");
            _server.Registry.AddOrReplace(stage);
            Task<JObject> listed = SendRpcAsync(client, url, ListRequest());
            yield return AwaitLiveRequest(listed);
            CollectionAssert.Contains(((JArray)listed.Result["result"]["tools"]).Select(tool => (string)tool["name"]), "stage");
            Task<JObject> first = SendRpcAsync(client, url, CallRequest("stage", new JObject()));
            yield return AwaitLiveRequest(first);
            Assert.AreEqual("phase-one", (string)first.Result["result"]["content"][0]["text"]);
            Assert.IsTrue(stage.CalledOnGameThread);

            RuntimeStageTool replacement = new("stage", "phase-two");
            _server.Registry.AddOrReplace(replacement);
            Task<JObject> replaced = SendRpcAsync(client, url, CallRequest("stage", new JObject()));
            yield return AwaitLiveRequest(replaced);
            Assert.AreEqual("phase-two", (string)replaced.Result["result"]["content"][0]["text"]);
            Assert.AreEqual(1, stage.Invocations);
            Assert.IsTrue(_server.Registry.Remove("stage"));
            Task<JObject> removedList = SendRpcAsync(client, url, ListRequest());
            yield return AwaitLiveRequest(removedList);
            CollectionAssert.DoesNotContain(((JArray)removedList.Result["result"]["tools"]).Select(tool => (string)tool["name"]), "stage");
            Task<JObject> stale = SendRpcAsync(client, url, CallRequest("stage", new JObject()));
            yield return AwaitLiveRequest(stale);
            Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, (int)stale.Result["error"]["code"]);
            Assert.AreEqual(1, replacement.Invocations);

            RuntimeStageTool next = new("next", "next-stage");
            _server.Registry.Replace(new IMcpTool[] { keep, next });
            Task<JObject> nextCall = SendRpcAsync(client, url, CallRequest("next", new JObject()));
            yield return AwaitLiveRequest(nextCall);
            Assert.AreEqual("next-stage", (string)nextCall.Result["result"]["content"][0]["text"]);
            Task<JObject> unaffected = SendRpcAsync(client, url, CallRequest("keep", new JObject()));
            yield return AwaitLiveRequest(unaffected);
            Assert.AreEqual("unrelated", (string)unaffected.Result["result"]["content"][0]["text"]);
            Assert.IsTrue(_server.IsRunning);
            Assert.AreEqual(url, _server.Url);
            Assert.AreSame(registry, _server.Registry);
            Assert.AreEqual(session, client.DefaultRequestHeaders.GetValues(McpServerInfo.SessionHeader).Single());
        }

        [UnityTest]
        public IEnumerator QueuedCall_RemovedBeforeGameFrame_DoesNotRunOldOrReplacementBody()
        {
            RuntimeStageTool previous = new("stage", "old");
            RuntimeStageTool replacement = new("stage", "new");
            McpToolRegistry registry = new(new IMcpTool[] { previous });
            McpRpcDispatcher dispatcher = new(registry, new McpSessionStore(), _server);
            Task<McpDispatchResult> queued = dispatcher.DispatchAsync(CallRequest("stage", new JObject()), CancellationToken.None);
            Assert.IsFalse(queued.IsCompleted, "The body must wait for the actual server player-loop pump.");
            registry.Remove("stage");
            registry.AddOrReplace(replacement);
            yield return AwaitLiveRequest(queued);
            Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, (int)queued.Result.Response["error"]["code"]);
            Assert.AreEqual(0, previous.Invocations);
            Assert.AreEqual(0, replacement.Invocations);
        }

        private static IEnumerator AwaitLiveRequest(Task pending)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (!pending.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue(pending.IsCompleted, "Live MCP request exceeded the frame-driven deadline.");
            Assert.IsFalse(pending.IsCanceled, "Live MCP request was cancelled.");
            Assert.IsNull(pending.Exception, pending.Exception?.ToString());
        }

        [UnityTest]
        public IEnumerator StopRestart_RetainsActualRunningLeaseAndServesUnrelatedCalls()
        {
            LifetimeTool oldTool = new(true);
            _server.StartListening(new McpToolRegistry(new[] { oldTool }), FreeLoopbackPort());
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _server.AuthToken);
            Task<JObject> oldRequest = SendRpcAsync(client, _server.Url, CallRequest(oldTool.Name, new JObject()));
            try
            {
                yield return AwaitLiveRequest(oldTool.Started.Task);
                _server.StopListening();
                yield return AwaitLiveRequest(oldTool.Cancelled.Task);
                Assert.AreEqual(1, _server.AdmittedMainThreadCalls);
                RuntimeStageTool next = new("next", "available");
                _server.StartListening(new McpToolRegistry(new[] { next }), FreeLoopbackPort());
                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _server.AuthToken);
                Task<JObject> nextRequest = SendRpcAsync(client, _server.Url, CallRequest("next", new JObject()));
                yield return AwaitLiveRequest(nextRequest);
                Assert.AreEqual("available", (string)nextRequest.Result["result"]["content"][0]["text"]);
                Assert.AreEqual(1, _server.AdmittedMainThreadCalls,
                    "Restart must not erase the lease of a body that ignored cancellation.");
            }
            finally { oldTool.Release.TrySetResult(true); }
            yield return AwaitLiveRequest(ObserveClosedTransportAsync(oldRequest));
            yield return AwaitAdmissionsReleased();
        }

        [UnityTest]
        public IEnumerator Stop_ForwardsLifetimeCancellationToAnAlreadyStartedTool()
        {
            LifetimeTool tool = new(false);
            _server.StartListening(new McpToolRegistry(new[] { tool }), FreeLoopbackPort());
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _server.AuthToken);
            Task<JObject> request = SendRpcAsync(client, _server.Url, CallRequest(tool.Name, new JObject()));
            try
            {
                yield return AwaitLiveRequest(tool.Started.Task);
                _server.StopListening();
                yield return AwaitLiveRequest(tool.Cancelled.Task);
                yield return AwaitAdmissionsReleased();
            }
            finally { tool.Release.TrySetResult(true); }
            yield return AwaitLiveRequest(ObserveClosedTransportAsync(request));
        }

        private IEnumerator AwaitAdmissionsReleased()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (_server.AdmittedMainThreadCalls != 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.AreEqual(0, _server.AdmittedMainThreadCalls, "Finished bodies must return their admission leases.");
        }

        private static async Task ObserveClosedTransportAsync(Task request)
        {
            try { await request; }
            catch (Exception) { }
        }

        private sealed class LifetimeTool : IMcpTool
        {
            private readonly bool _ignoreCancellation;
            public LifetimeTool(bool ignoreCancellation) { _ignoreCancellation = ignoreCancellation; }
            public string Name => "lifetime";
            public string Description => "Controlled long-running tool";
            public string InputSchemaJson => "{\"type\":\"object\"}";
            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() => Cancelled.TrySetResult(true));
                Started.TrySetResult(true);
                if (_ignoreCancellation) await Release.Task;
                else
                {
                    await Task.WhenAny(Release.Task, Cancelled.Task);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return McpToolResult.Text("finished");
            }
        }

        private static async Task<JObject> SendRpcAsync(HttpClient client, string url, JObject body)
        {
            using StringContent content = new(body.ToString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(url, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues(McpServerInfo.SessionHeader, out IEnumerable<string> sessions))
                client.DefaultRequestHeaders.TryAddWithoutValidation(McpServerInfo.SessionHeader, sessions.Single());
            return JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        private static int FreeLoopbackPort()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class RuntimeStageTool : IMcpTool
        {
            private readonly string _result;
            private readonly int _gameThread = Thread.CurrentThread.ManagedThreadId;
            public RuntimeStageTool(string name, string result) { Name = name; _result = result; }
            public string Name { get; }
            public string Description => "Runtime stage operation";
            public string InputSchemaJson => "{\"type\":\"object\"}";
            public int Invocations { get; private set; }
            public bool CalledOnGameThread { get; private set; }
            public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
            {
                Invocations++;
                CalledOnGameThread = Thread.CurrentThread.ManagedThreadId == _gameThread;
                return Task.FromResult(McpToolResult.Text(_result));
            }
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
