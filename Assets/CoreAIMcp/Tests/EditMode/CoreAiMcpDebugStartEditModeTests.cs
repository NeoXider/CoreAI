using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Diagnostics;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
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
    /// What the debug start-up relies on in the server: listening before any CoreAI scope exists and publishing the
    /// tools once one is built; host-admin mod management granted to the composition's own unrestricted actor; the
    /// debug tools in the composed catalog; the transport facts game_status reports; the token kept out of the
    /// captured Unity log. In-memory transport, no socket.
    /// </summary>
    public sealed class CoreAiMcpDebugStartEditModeTests
    {
        private const string MissingWorldHostLog =
            "[CoreAI] [Core] [CoreAiMods] RbxWorldHost NOT resolved — mods run headless. " +
            "Instance.new / workspace mutations produce no GameObjects. " +
            "Check: (1) RbxWorldHost component exists in the scene, " +
            "(2) CoreAiModsLifetimeScope.robloxWorldHost is wired to it, " +
            "(3) link.xml preserves CoreAI.RbxApi.Binding assembly.";

        private GameObject _host;
        private CoreAiMcpServer _server;
        private readonly List<IObjectResolver> _containers = new();

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("CoreAiMcpDebugStartTestHost");
            _server = _host.AddComponent<CoreAiMcpServer>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_server != null) _server.StopListening();
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            foreach (IObjectResolver container in _containers) container.Dispose();
            _containers.Clear();
        }

        [Test]
        public void WithoutWaiting_NoScopeMeansNoStart()
        {
            _server.CompositionResolverOverride = () => null;

            _server.StartListening(null, 49101, (_, _, _) => new InMemoryTransport());

            Assert.IsFalse(_server.IsRunning);
        }

        [Test]
        public void WaitingForComposition_ListensAtOnce_ThenPublishesTheToolsOfTheFirstScope()
        {
            IObjectResolver current = null;
            _server.CompositionResolverOverride = () => current;
            _server.WaitForComposition = true;

            _server.StartListening(null, 49102, (_, _, _) => new InMemoryTransport());

            Assert.IsTrue(_server.IsRunning, "a client can connect before the first scene composed CoreAI");
            Assert.IsTrue(_server.CatalogFollowsComposition);
            Assert.IsFalse(_server.Registry.Contains("game_status"));

            RuntimeLogBuffer logs = new();
            current = Container(builder => builder.RegisterInstance<IRuntimeLogSource>(logs));
            _server.MonitorComposition(1);

            Assert.IsTrue(_server.Registry.Contains("game_status"));
            Assert.IsTrue(_server.Registry.Contains("get_unity_logs"));
            Assert.IsTrue(_server.Registry.Contains("screenshot"));
            Assert.IsFalse(_server.Registry.Contains("get_performance"), "no performance source in this scope");
        }

        [Test]
        public void DescribeTransport_ReportsTheLinkAndItsCounters()
        {
            _server.StartListening(new McpToolRegistry(null), 49103, (_, _, _) => new InMemoryTransport());

            JObject facts = _server.DescribeTransport();

            Assert.AreEqual("http", (string)facts["transport"]);
            Assert.IsTrue((bool)facts["auth"]);
            Assert.AreEqual(0, (long)facts["tool_calls"]);
            Assert.IsNotNull(_server.Dispatcher);
            _server.StopListening();
            Assert.AreEqual("stopped", (string)_server.DescribeTransport()["transport"]);
            Assert.IsNull(_server.Dispatcher);
        }

        [Test]
        public void Start_RegistersTheTokenForRedactionInTheCapturedUnityLog()
        {
            _server.StartListening(new McpToolRegistry(null), 49104, (_, _, _) => new InMemoryTransport());
            string token = _server.AuthToken;

            RuntimeLogEntry line = UnityRuntimeLogCapture.Shared.Buffer.Append(RuntimeLogLevel.Info,
                "[CoreAI MCP] Auth token: " + token);

            Assert.IsNotNull(token);
            StringAssert.DoesNotContain(token, line.Message,
                "in-game agents read this log and may send it to a cloud provider");
            StringAssert.Contains(RuntimeLogBuffer.RedactedText, line.Message);
        }

        [Test]
        public void HostAdminFromComposition_GrantsManageModsToTheComposedUnrestrictedActor()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                IObjectResolver container = ModsContainer(settings, null);
                Assert.IsFalse(_server.BuildRegistry(container).Contains("manage_mods"));

                _server.ConfigureHostAdminModManagementFromComposition();

                Assert.IsTrue(_server.HostAdminModManagementEnabled);
                Assert.IsTrue(_server.BuildRegistry(container).Contains("manage_mods"),
                    "the debug start-up must be able to make and edit mods");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void HostAdminFromComposition_RefusesARestrictedComposedActor()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                IObjectResolver container = ModsContainer(settings, new LocalActorIdentityProvider("restricted-mcp"));
                _server.ConfigureHostAdminModManagementFromComposition();

                Assert.IsFalse(_server.BuildRegistry(container).Contains("manage_mods"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void HostAdminFromComposition_IsRefusedWhileRunning()
        {
            _server.StartListening(new McpToolRegistry(null), 49105, (_, _, _) => new InMemoryTransport());
            Assert.Throws<InvalidOperationException>(() => _server.ConfigureHostAdminModManagementFromComposition());
        }

        private IObjectResolver ModsContainer(CoreAISettingsAsset settings, IActorIdentityProvider identity)
        {
            ContainerBuilder builder = new();
            if (identity != null) builder.RegisterInstance(identity);
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            builder.RegisterCoreAiMods(applicationIsPlayingProvider: () => false, skillTextProvider: _ => null);
            LogAssert.Expect(LogType.Error, MissingWorldHostLog);
            IObjectResolver container = builder.Build();
            _containers.Add(container);
            return container;
        }

        private IObjectResolver Container(Action<ContainerBuilder> configure)
        {
            ContainerBuilder builder = new();
            configure(builder);
            IObjectResolver container = builder.Build();
            _containers.Add(container);
            return container;
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
