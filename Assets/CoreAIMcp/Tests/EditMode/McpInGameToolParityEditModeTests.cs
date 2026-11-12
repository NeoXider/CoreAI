using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The parity gate: over a composed CoreAI + mods scope, every function any in-game agent role can call (its
    /// tools and its skills' tools) is reachable over MCP - in <c>tools/list</c> or through <c>coreai_tools</c> - or
    /// named in <see cref="McpInGameTools.NotExposed"/> with a reason. A tool added to a role later fails here until it
    /// is reachable or listed.
    /// </summary>
    public sealed class McpInGameToolParityEditModeTests
    {
        private GameObject _host;
        private CoreAiMcpServer _server;
        private CoreAISettingsAsset _settings;
        private CoreAiPrefabRegistryAsset _prefabs;
        private IObjectResolver _container;
        private bool _ignoreFailingMessages;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailingMessages = LogAssert.ignoreFailingMessages;
            // WHY: the composition logs its headless-world errors; this gate is about the catalog, not the scene.
            LogAssert.ignoreFailingMessages = true;
            _host = new GameObject("McpInGameToolParityHost");
            _server = _host.AddComponent<CoreAiMcpServer>();
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _prefabs = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(_settings);
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            builder.RegisterWorldCommands(_prefabs);
            builder.RegisterCoreAiMods(applicationIsPlayingProvider: () => false, skillTextProvider: _ => null);
            _container = builder.Build();
        }

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
            if (_host != null) Object.DestroyImmediate(_host);
            if (_settings != null) Object.DestroyImmediate(_settings);
            if (_prefabs != null) Object.DestroyImmediate(_prefabs);
            LogAssert.ignoreFailingMessages = _ignoreFailingMessages;
        }

        [Test]
        public void EveryInGameFunction_IsReachableOverMcp_OrListedWithAReason()
        {
            _server.ConfigureHostAdminModManagementFromComposition();
            McpToolRegistry mcp = _server.BuildRegistry(_container);
            AgentMemoryPolicy policy = _container.Resolve<AgentMemoryPolicy>();

            List<string> missing = new();
            HashSet<string> reported = new();
            int checkedFunctions = 0;
            foreach (ILlmTool tool in CoreAiMcpServer.CollectInGameTools(policy))
            {
                foreach (InGameFunction function in McpInGameTools.Expand(tool))
                {
                    checkedFunctions++;
                    if (mcp.Contains(function.Name) || McpInGameTools.NotExposed.ContainsKey(function.Name)) continue;
                    if (reported.Add(function.Name)) missing.Add(function.Name + " (tool " + tool.Name + ")");
                }
            }

            Assert.Greater(checkedFunctions, 5, "the composed scope gives its roles tools");
            Assert.IsEmpty(missing,
                "In-game functions neither reachable over MCP nor in McpInGameTools.NotExposed:\n" +
                string.Join("\n", missing));
        }

        [Test]
        public void TheProgrammersCameraAndWorldFunctions_AreBrokered_WithTheInGameSchema()
        {
            _server.ConfigureHostAdminModManagementFromComposition();
            McpToolRegistry mcp = _server.BuildRegistry(_container);

            foreach (string name in new[] { "camera_capture", "camera_look", "camera_orbit", "camera_list" })
            {
                Assert.IsTrue(mcp.Contains(name), name);
                Assert.AreEqual(McpToolResidency.Dynamic, mcp.ResidencyOf(name), name + " is reached through coreai_tools");
            }

            Assert.AreEqual(McpToolResidency.Native, mcp.ResidencyOf("screenshot"), "MCP screenshot keeps its own tool");
            StringAssert.Contains("cameraName", mcp.Find("camera_capture").InputSchemaJson,
                "the in-game arguments, unchanged");
        }

        [Test]
        public void WithoutHostAdmin_TheInGameCopiesOfManageModsAndSaveWorld_StayUnreachable()
        {
            McpToolRegistry mcp = _server.BuildRegistry(_container);

            Assert.IsFalse(mcp.Contains("manage_mods"));
            Assert.IsFalse(mcp.Contains("save_world"));
        }

        [Test]
        public void InGameTools_CanBeKeptOffMcp()
        {
            _server.ExposeInGameTools = false;
            McpToolRegistry mcp = _server.BuildRegistry(_container);

            Assert.IsFalse(mcp.Contains("camera_look"));
            Assert.IsTrue(mcp.Contains("screenshot"));
        }
    }
}
