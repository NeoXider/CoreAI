using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Tests for the <c>manage_mods</c> LLM tool: inspection, mutation, capability grant, and
    /// the read-only gating, plus the DI wiring that attaches Lua tools to the Programmer role.
    /// </summary>
    public sealed class LuaModsLlmToolEditModeTests
    {
        private CoreAISettingsAsset _settings;
        private SynchronizationContext _savedContext;
        private RecordingLog _log;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _log = new RecordingLog();

            // The Lua-CSharp runtime bridges its async VM to a synchronous call site; detaching the Unity
            // SynchronizationContext for the duration of each test lets those continuations complete on the
            // thread pool instead of deadlocking the blocked main thread (see LuaCsModRuntimeEditModeTests).
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
            Object.DestroyImmediate(_settings);
        }

        private LuaModsLlmTool CreateTool(
            LuaCsModRuntime runtime,
            LuaCapabilities granted = LuaCapabilities.All,
            bool allowManagement = true)
        {
            return new LuaModsLlmTool(runtime, _settings, NullLog.Instance, granted, allowManagement);
        }

        private static async Task<JObject> ExecuteAsync(LuaModsLlmTool tool, string action, string modId = null,
            string code = null)
        {
            string json = await tool.ExecuteAsync(action, modId, code);
            return JObject.Parse(json);
        }

        [Test]
        public async Task List_WithoutMods_ReturnsEmptySuccess()
        {
            LuaCsModRuntime runtime = new();
            JObject result = await ExecuteAsync(CreateTool(runtime), "list");

            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual(0, ((JArray)result["data"]).Count);
        }

        [Test]
        public async Task Load_List_GetSource_Roundtrip()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime);
            const string code = "hooks_on('ping', function() end)";

            JObject load = await ExecuteAsync(tool, "load", "demo_mod", code);
            Assert.IsTrue(load.Value<bool>("success"), load.ToString());

            JObject list = await ExecuteAsync(tool, "list");
            JArray mods = (JArray)list["data"];
            Assert.AreEqual(1, mods.Count);
            Assert.AreEqual("demo_mod", mods[0].Value<string>("id"));

            JObject source = await ExecuteAsync(tool, "get_source", "demo_mod");
            Assert.IsTrue(source.Value<bool>("success"));
            Assert.AreEqual(code, source.Value<string>("data"));
        }

        [Test]
        public async Task List_QuarantinedMod_SurfacesQuarantineFlagAndHint()
        {
            LuaCsModRuntime runtime = new(maxErrorsBeforeQuarantine: 1);
            LuaModsLlmTool tool = CreateTool(runtime);
            runtime.LoadMod("broken", "hooks_on('boom', function() error('boom') end)");

            runtime.EmitEvent("boom", "");
            runtime.Tick(0);

            // The repairing AI must SEE 'quarantined' in list instead of a missing mod.
            JObject list = await ExecuteAsync(tool, "list");
            JArray mods = (JArray)list["data"];
            Assert.AreEqual(1, mods.Count, "A quarantined mod must still be listed.");
            Assert.IsTrue(mods[0].Value<bool>("quarantined"), "list must surface quarantined=true.");
            StringAssert.Contains("quarantined", list.Value<string>("message"),
                "The list message must call out quarantined mods and the reload path.");

            JObject diagnostics = await ExecuteAsync(tool, "diagnostics", "broken");
            StringAssert.Contains("quarantine", diagnostics.Value<string>("message"),
                "diagnostics must teach that reload clears the quarantine.");
        }

        [Test]
        public async Task Reload_ReplacesSource_AndUnloadRemoves()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime);
            await ExecuteAsync(tool, "load", "m1", "local a = 1");

            JObject reload = await ExecuteAsync(tool, "reload", "m1", "local b = 2");
            Assert.IsTrue(reload.Value<bool>("success"), reload.ToString());
            Assert.IsTrue(runtime.TryGetModSource("m1", out string source));
            Assert.AreEqual("local b = 2", source);

            JObject unload = await ExecuteAsync(tool, "unload", "m1");
            Assert.IsTrue(unload.Value<bool>("success"));
            Assert.IsFalse(runtime.IsLoaded("m1"));
        }

        [Test]
        public async Task Load_UsesHostGrantedCapabilities_NotModelControlled()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime, LuaCapabilities.Read);

            JObject load = await ExecuteAsync(tool, "load", "ro_mod", "local x = 1");
            Assert.IsTrue(load.Value<bool>("success"), load.ToString());

            IReadOnlyList<LuaModInfo> mods = runtime.ListMods();
            Assert.AreEqual(LuaCapabilities.Read, mods[0].Capabilities);
        }

        [Test]
        public async Task ReadOnlyTool_BlocksMutations_ButAllowsInspection()
        {
            LuaCsModRuntime runtime = new();
            runtime.LoadMod("existing", "local x = 1");
            LuaModsLlmTool tool = CreateTool(runtime, allowManagement: false);

            Assert.IsTrue((await ExecuteAsync(tool, "list")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(tool, "get_source", "existing")).Value<bool>("success"));

            Assert.IsFalse((await ExecuteAsync(tool, "load", "new_mod", "local y = 2")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(tool, "reload", "existing", "local y = 2")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(tool, "unload", "existing")).Value<bool>("success"));
            Assert.IsTrue(runtime.IsLoaded("existing"));
        }

        [Test]
        public async Task InvalidInput_ReturnsFailure_NotException()
        {
            LuaCsModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime);

            Assert.IsFalse((await ExecuteAsync(tool, "explode")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(tool, "get_source")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(tool, "load", "no_code")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(tool, "get_source", "missing")).Value<bool>("success"));
            Assert.IsFalse((await ExecuteAsync(tool, "load", "bad", "this is not lua ((")).Value<bool>("success"));
        }

        [Test]
        public void TryGetModSource_MissingMod_ReturnsFalse()
        {
            LuaCsModRuntime runtime = new();
            Assert.IsFalse(runtime.TryGetModSource("nope", out string source));
            Assert.AreEqual("", source);
        }

        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        [Test]
        public void RegisterCoreAiMods_AttachesLuaTools_ToProgrammerRole()
        {
            // WHY: this container has no RbxWorldHost, which the mods factory reports as an error because
            // in a shipped game it means Instance.new renders nothing. The tool wiring under test does not
            // need a host, so headless is deliberate here.
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(_log);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.RegisterInstance<ICoreAISettings>(_settings);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);

                builder.RegisterWorldCommands(registry);
                builder.RegisterCoreAiMods();

                using IObjectResolver container = builder.Build();
                AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer);

                Assert.IsTrue(HasTool(tools, "execute_lua"), "Programmer must get execute_lua");
                Assert.IsTrue(HasTool(tools, "manage_mods"), "Programmer must get manage_mods");
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public async Task RegisterCoreAiMods_FullAccess_ManageModsGrantsFullLua()
        {
            // WHY: see RegisterCoreAiMods_AttachesLuaTools_ToProgrammerRole — headless is deliberate here.
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject probe = new("FullManageModsProbe");
            LuaModsLlmTool modsTool = null;
            IObjectResolver container = null;
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(_log);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.RegisterInstance<ICoreAISettings>(_settings);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);

                builder.RegisterWorldCommands(registry, enableFullLuaAccess: true);
                builder.RegisterCoreAiMods(enableFullLuaAccess: true);

                container = builder.Build();
                AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                foreach (ILlmTool tool in policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer))
                {
                    if (tool is LuaModsLlmTool luaMods)
                    {
                        modsTool = luaMods;
                        break;
                    }
                }

                Assert.IsNotNull(modsTool, "Programmer must get manage_mods as LuaModsLlmTool.");
                JObject load = await ExecuteAsync(modsTool, "load", "full_probe",
                    "local id = unity_find('FullManageModsProbe')\nif id == 0 then error('Full unity_find missing') end");
                Assert.IsTrue(load.Value<bool>("success"), load.ToString());

                IReadOnlyList<LuaModInfo> loaded = container.Resolve<ILuaModRuntime>().ListMods();
                Assert.AreEqual(LuaCapabilities.All | LuaCapabilities.Full, loaded[0].Capabilities);
            }
            finally
            {
                if (modsTool != null)
                {
                    JObject cleanup = await ExecuteAsync(modsTool, "forget", "full_probe");
                    Assert.IsTrue(cleanup.Value<bool>("success"), cleanup.ToString());
                }

                container?.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void RegisterWorldCommands_WithoutPolicyServices_StillBuilds()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(_log);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();

                builder.RegisterWorldCommands(registry);

                Assert.DoesNotThrow(() =>
                {
                    using IObjectResolver container = builder.Build();
                    container.Resolve<ICoreAiPrefabRegistry>();
                });
            }
            finally
            {
                Object.DestroyImmediate(registry);
            }
        }

        private static bool HasTool(IReadOnlyList<ILlmTool> tools, string name)
        {
            foreach (ILlmTool tool in tools)
            {
                if (tool != null && tool.Name == name)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
