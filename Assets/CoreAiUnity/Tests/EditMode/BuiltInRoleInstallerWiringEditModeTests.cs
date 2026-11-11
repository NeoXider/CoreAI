using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Unity;
using NUnit.Framework;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The real installers (<c>WorldCommandsInstaller</c> and <c>CoreAiModsInstaller</c>) register into tool
    /// profiles, and the shipped role definitions turn that into exactly the per-role tool wiring the installers
    /// produced when they called <c>AddToolForRole(Programmer, ...)</c> directly (recorded below). A role
    /// definition asset then changes a role's tools with no code.
    /// </summary>
    public sealed class BuiltInRoleInstallerWiringEditModeTests
    {
        private const string RecordedWiring =
            "Creator|tools=memory,world_command|skills=\n" +
            "Builder|tools=memory,world_command|skills=\n" +
            "Analyzer|tools=memory|skills=\n" +
            "Programmer|tools=memory,camera,execute_lua,manage_mods,get_mod_logs,save_world,load_world,list_autosaves,load_autosave,read_skill,call_skill_tool|skills=Lua Modding,Rbx API,Full Lua\n" +
            "AINpc|tools=memory|skills=\n" +
            "CoreMechanicAI|tools=memory|skills=\n" +
            "PlainChat|tools=|skills=\n" +
            "SmartChat|tools=memory|skills=\n" +
            "Merchant|tools=memory|skills=";

        private CoreAISettingsAsset _settings;
        private SynchronizationContext _savedContext;
        private readonly List<Object> _created = new();

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            // WHY: the Lua-CSharp runtime completes its async VM calls on the thread pool; the Unity context would
            // deadlock the blocked main thread (see LuaModsLlmToolEditModeTests).
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
            Object.DestroyImmediate(_settings);
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        private IObjectResolver BuildContainer(Action<IContainerBuilder> extra = null)
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            _created.Add(registry);
            ContainerBuilder builder = new();
            builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
            // WHY a silent log: the container has no RbxWorldHost, which the mods factory reports through ILog;
            // the tool wiring under test does not need a host.
            builder.RegisterInstance<ILog>(NullLog.Instance);
            builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
            builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.RegisterInstance<ICoreAISettings>(_settings);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            extra?.Invoke(builder);
            builder.RegisterWorldCommands(registry);
            builder.RegisterCoreAiMods();
            return builder.Build();
        }

        private static string Describe(AgentMemoryPolicy policy)
        {
            List<string> lines = new();
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                lines.Add(roleId + "|tools=" +
                          string.Join(",", policy.GetToolsForRole(roleId).Select(tool => tool.Name)) +
                          "|skills=" + string.Join(",", policy.GetSkillsForRole(roleId).Select(skill => skill.Name)));
            }

            return string.Join("\n", lines);
        }

        [Test]
        public void ShippedInstallers_ReproduceTheRecordedPerRoleWiring()
        {
            using IObjectResolver container = BuildContainer();
            AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();

            Assert.AreEqual(RecordedWiring, Describe(policy));
            CollectionAssert.AreEqual(
                new[] { "execute_lua", "manage_mods", "get_mod_logs", "save_world", "load_world", "list_autosaves", "load_autosave" },
                policy.GetToolProfile(BuiltInToolProfileIds.LuaBuilder).ToolNames);
        }

        [Test]
        public void InventoryProvider_WiresGetInventory_ForTheMerchant()
        {
            using IObjectResolver container = BuildContainer(builder =>
                builder.RegisterInstance<InventoryTool.IInventoryProvider>(new EmptyInventory()));
            AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();

            CollectionAssert.AreEqual(new[] { "memory", "get_inventory" },
                policy.GetToolsForRole(BuiltInAgentRoleIds.Merchant).Select(tool => tool.Name).ToArray());
            CollectionAssert.DoesNotContain(
                policy.GetToolsForRole(BuiltInAgentRoleIds.SmartChat).Select(tool => tool.Name).ToArray(),
                "get_inventory");
        }

        [Test]
        public void RoleDefinitionAsset_GivesTheProgrammerTheNpcProfile_WithoutCode()
        {
            AgentRoleDefinitionAsset asset = ScriptableObject.CreateInstance<AgentRoleDefinitionAsset>();
            _created.Add(asset);
            RoleDefinition programmer = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.Programmer);
            programmer.ToolProfiles = new[] { BuiltInToolProfileIds.Npc };
            asset.CopyFrom(programmer);

            using IObjectResolver container = BuildContainer(builder =>
                builder.RegisterAgentRoleDefinitions(new[] { asset }));
            AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();

            string[] tools = policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer).Select(tool => tool.Name).ToArray();
            CollectionAssert.DoesNotContain(tools, "execute_lua");
            CollectionAssert.DoesNotContain(tools, "camera");
            Assert.IsEmpty(policy.GetSkillsForRole(BuiltInAgentRoleIds.Programmer));
        }

        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class EmptyInventory : InventoryTool.IInventoryProvider
        {
            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new List<InventoryTool.InventoryItem>());
            }
        }
    }
}
