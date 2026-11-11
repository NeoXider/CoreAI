using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VContainer;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// A catalog built from a CoreAI scope must follow that scope. A scene load destroys the scope and
    /// disposes its services; the automatic host survives it, so its tools must be withdrawn at once and
    /// rebuilt from the next scope, instead of calling disposed services for the rest of the session.
    /// The scene search is replaced by <see cref="CoreAiMcpServer.CompositionResolverOverride"/>; the
    /// transport is in memory, so no socket is opened.
    /// </summary>
    public sealed class CoreAiMcpServerCompositionEditModeTests
    {
        private GameObject _host;
        private CoreAiMcpServer _server;
        private readonly List<IObjectResolver> _containers = new();

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("CoreAiMcpServerCompositionTestHost");
            _server = _host.AddComponent<CoreAiMcpServer>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_server != null) _server.StopListening();
            if (_host != null) Object.DestroyImmediate(_host);
            foreach (IObjectResolver container in _containers) container.Dispose();
            _containers.Clear();
        }

        [Test]
        public async Task DestroyedScope_WithdrawsToolsAtOnce_ThenRebindsTheSameCatalogToTheNextScope()
        {
            CountingExecutor first = new();
            CountingExecutor second = new();
            IObjectResolver current = Container(first);
            _server.CompositionResolverOverride = () => current;
            _server.StartListening(null, 49001, (_, _, _) => new InMemoryTransport());
            McpToolRegistry live = _server.Registry;
            Assert.IsTrue(_server.CatalogFollowsComposition);
            Assert.IsTrue(live.Contains("execute_lua"));
            long started = live.Revision;

            current = null;
            _server.MonitorComposition(10);

            Assert.IsFalse(live.Contains("execute_lua"), "no call may reach the services of a destroyed scope.");
            Assert.IsTrue(live.Contains(CoreAiToolsBrokerMcpTool.ToolName));
            Assert.AreEqual(started + 1, live.Revision, "the withdrawal is published as list_changed.");

            current = Container(second);
            _server.MonitorComposition(10 + CoreAiMcpServer.CompositionProbeIntervalSeconds / 2);
            Assert.IsFalse(live.Contains("execute_lua"), "the scene search is throttled.");
            _server.MonitorComposition(10 + CoreAiMcpServer.CompositionProbeIntervalSeconds);

            Assert.AreSame(live, _server.Registry, "clients keep one catalog and one session across the rebind.");
            Assert.IsTrue(live.Contains("execute_lua"));
            Assert.AreEqual(started + 2, live.Revision);
            McpToolResult result = await live.Find("execute_lua")
                .InvokeAsync(new JObject { ["code"] = "return 1" }, CancellationToken.None);
            Assert.IsFalse(result.IsError);
            Assert.AreEqual(0, first.Calls, "the old scope's executor must never run again.");
            Assert.AreEqual(1, second.Calls);
        }

        [Test]
        public void ToolsTheHostAddedToTheLiveCatalog_SurviveTheRebind()
        {
            IObjectResolver current = Container(new CountingExecutor());
            _server.CompositionResolverOverride = () => current;
            _server.StartListening(null, 49007, (_, _, _) => new InMemoryTransport());
            _server.Registry.AddOrReplace(new FakeMcpTool("stage_briefing"));

            current = null;
            _server.MonitorComposition(0);
            Assert.IsTrue(_server.Registry.Contains("stage_briefing"), "only the composition's own tools are withdrawn.");
            current = Container(new CountingExecutor());
            _server.MonitorComposition(CoreAiMcpServer.CompositionProbeIntervalSeconds);

            Assert.IsTrue(_server.Registry.Contains("stage_briefing"));
            Assert.IsTrue(_server.Registry.Contains("execute_lua"));
        }

        [Test]
        public void ContainerRebuiltInPlace_IsRebound_InOneMonitorPass()
        {
            CountingExecutor second = new();
            IObjectResolver current = Container(new CountingExecutor());
            _server.CompositionResolverOverride = () => current;
            _server.StartListening(null, 49002, (_, _, _) => new InMemoryTransport());
            long started = _server.Registry.Revision;

            current = Container(second);
            _server.MonitorComposition(0);

            Assert.AreEqual(started + 2, _server.Registry.Revision, "withdraw, then rebuild from the new container.");
            Assert.IsTrue(_server.Registry.Contains("execute_lua"));
        }

        [Test]
        public void CurrentScope_IsLeftAlone()
        {
            IObjectResolver current = Container(new CountingExecutor());
            _server.CompositionResolverOverride = () => current;
            _server.StartListening(null, 49003, (_, _, _) => new InMemoryTransport());
            long started = _server.Registry.Revision;

            for (int frame = 0; frame < 5; frame++) _server.MonitorComposition(frame);

            Assert.AreEqual(started, _server.Registry.Revision, "a live scope must not cause republication.");
        }

        [Test]
        public void HostSuppliedCatalog_IsNeverRebound()
        {
            FakeMcpTool hostTool = new("host_tool");
            McpToolRegistry hostCatalog = new(new IMcpTool[] { hostTool });
            _server.CompositionResolverOverride = () => null;
            _server.StartListening(hostCatalog, 49004, (_, _, _) => new InMemoryTransport());
            long started = hostCatalog.Revision;

            _server.MonitorComposition(0);
            _server.MonitorComposition(100);

            Assert.IsFalse(_server.CatalogFollowsComposition);
            Assert.AreEqual(started, hostCatalog.Revision);
            Assert.IsTrue(hostCatalog.Contains("host_tool"));
        }

        [Test]
        public void StopListening_EndsCompositionTracking()
        {
            IObjectResolver current = Container(new CountingExecutor());
            _server.CompositionResolverOverride = () => current;
            _server.StartListening(null, 49005, (_, _, _) => new InMemoryTransport());

            _server.StopListening();
            current = null;
            _server.MonitorComposition(0);

            Assert.IsFalse(_server.CatalogFollowsComposition);
            Assert.IsNull(_server.Registry);
        }

        [Test]
        public void Start_PassesTheConfiguredInstructionsToInitialize()
        {
            McpRpcDispatcher captured = null;
            _server.Instructions = "custom guidance";
            _server.StartListening(new McpToolRegistry(null), 49006, (_, dispatcher, _) =>
            {
                captured = dispatcher;
                return new InMemoryTransport();
            });

            Assert.AreEqual("custom guidance", captured.Instructions);
            GameObject fresh = new("CoreAiMcpServerDefaultInstructions");
            try
            {
                Assert.AreEqual(McpServerInfo.DefaultInstructions, fresh.AddComponent<CoreAiMcpServer>().Instructions,
                    "a new host starts with the default guidance.");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }

        private IObjectResolver Container(CountingExecutor executor)
        {
            ContainerBuilder builder = new();
            builder.RegisterInstance<LuaTool.ILuaExecutor>(executor);
            IObjectResolver container = builder.Build();
            _containers.Add(container);
            return container;
        }

        private sealed class CountingExecutor : LuaTool.ILuaExecutor
        {
            public int Calls { get; private set; }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new LuaTool.LuaResult { Success = true, Output = "1" });
            }
        }

        private sealed class InMemoryTransport : IMcpServerTransport
        {
            public bool IsRunning { get; private set; }
            public string Url => "http://127.0.0.1:0/mcp";
            public void Start() => IsRunning = true;
            public void Dispose() => IsRunning = false;
        }
    }
}
