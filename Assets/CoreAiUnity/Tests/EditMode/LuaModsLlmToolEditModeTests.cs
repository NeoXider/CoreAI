#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Sandbox;
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

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_settings);
        }

        private LuaModsLlmTool CreateTool(
            LuaModRuntime runtime,
            LuaCapabilities granted = LuaCapabilities.All,
            bool allowManagement = true)
        {
            return new LuaModsLlmTool(runtime, _settings, NullLog.Instance, granted, allowManagement);
        }

        private static JObject Execute(LuaModsLlmTool tool, string action, string modId = null, string code = null)
        {
            string json = tool.ExecuteAsync(action, modId, code).GetAwaiter().GetResult();
            return JObject.Parse(json);
        }

        [Test]
        public void List_WithoutMods_ReturnsEmptySuccess()
        {
            LuaModRuntime runtime = new();
            JObject result = Execute(CreateTool(runtime), "list");

            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual(0, ((JArray)result["data"]).Count);
        }

        [Test]
        public void Load_List_GetSource_Roundtrip()
        {
            LuaModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime);
            const string code = "hooks_on('ping', function() end)";

            JObject load = Execute(tool, "load", "demo_mod", code);
            Assert.IsTrue(load.Value<bool>("success"), load.ToString());

            JObject list = Execute(tool, "list");
            JArray mods = (JArray)list["data"];
            Assert.AreEqual(1, mods.Count);
            Assert.AreEqual("demo_mod", mods[0].Value<string>("id"));

            JObject source = Execute(tool, "get_source", "demo_mod");
            Assert.IsTrue(source.Value<bool>("success"));
            Assert.AreEqual(code, source.Value<string>("data"));
        }

        [Test]
        public void Reload_ReplacesSource_AndUnloadRemoves()
        {
            LuaModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime);
            Execute(tool, "load", "m1", "local a = 1");

            JObject reload = Execute(tool, "reload", "m1", "local b = 2");
            Assert.IsTrue(reload.Value<bool>("success"), reload.ToString());
            Assert.IsTrue(runtime.TryGetModSource("m1", out string source));
            Assert.AreEqual("local b = 2", source);

            JObject unload = Execute(tool, "unload", "m1");
            Assert.IsTrue(unload.Value<bool>("success"));
            Assert.IsFalse(runtime.IsLoaded("m1"));
        }

        [Test]
        public void Load_UsesHostGrantedCapabilities_NotModelControlled()
        {
            LuaModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime, LuaCapabilities.Read);

            JObject load = Execute(tool, "load", "ro_mod", "local x = 1");
            Assert.IsTrue(load.Value<bool>("success"), load.ToString());

            IReadOnlyList<LuaModInfo> mods = runtime.ListMods();
            Assert.AreEqual(LuaCapabilities.Read, mods[0].Capabilities);
        }

        [Test]
        public void ReadOnlyTool_BlocksMutations_ButAllowsInspection()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("existing", "local x = 1");
            LuaModsLlmTool tool = CreateTool(runtime, allowManagement: false);

            Assert.IsTrue(Execute(tool, "list").Value<bool>("success"));
            Assert.IsTrue(Execute(tool, "get_source", "existing").Value<bool>("success"));

            Assert.IsFalse(Execute(tool, "load", "new_mod", "local y = 2").Value<bool>("success"));
            Assert.IsFalse(Execute(tool, "reload", "existing", "local y = 2").Value<bool>("success"));
            Assert.IsFalse(Execute(tool, "unload", "existing").Value<bool>("success"));
            Assert.IsTrue(runtime.IsLoaded("existing"));
        }

        [Test]
        public void InvalidInput_ReturnsFailure_NotException()
        {
            LuaModRuntime runtime = new();
            LuaModsLlmTool tool = CreateTool(runtime);

            Assert.IsFalse(Execute(tool, "explode").Value<bool>("success"));
            Assert.IsFalse(Execute(tool, "get_source").Value<bool>("success"));
            Assert.IsFalse(Execute(tool, "load", "no_code").Value<bool>("success"));
            Assert.IsFalse(Execute(tool, "get_source", "missing").Value<bool>("success"));
            Assert.IsFalse(Execute(tool, "load", "bad", "this is not lua ((").Value<bool>("success"));
        }

        [Test]
        public void TryGetModSource_MissingMod_ReturnsFalse()
        {
            LuaModRuntime runtime = new();
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
        public void RegisterWorldCommands_AttachesLuaTools_ToProgrammerRole()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(Log.Instance);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.RegisterInstance<ICoreAISettings>(_settings);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.Register<SecureLuaEnvironment>(Lifetime.Singleton);

                builder.RegisterWorldCommands(registry);

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
        public void RegisterWorldCommands_FullAccess_ManageModsGrantsFullLua()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject probe = new("FullManageModsProbe");
            try
            {
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(Log.Instance);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.RegisterInstance<ICoreAISettings>(_settings);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.Register<SecureLuaEnvironment>(Lifetime.Singleton);

                builder.RegisterWorldCommands(registry, enableFullLuaAccess: true);

                using IObjectResolver container = builder.Build();
                AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                LuaModsLlmTool modsTool = null;
                foreach (ILlmTool tool in policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer))
                {
                    if (tool is LuaModsLlmTool luaMods)
                    {
                        modsTool = luaMods;
                        break;
                    }
                }

                Assert.IsNotNull(modsTool, "Programmer must get manage_mods as LuaModsLlmTool.");
                JObject load = Execute(modsTool, "load", "full_probe",
                    "local id = unity_find('FullManageModsProbe')\nif id == 0 then error('Full unity_find missing') end");
                Assert.IsTrue(load.Value<bool>("success"), load.ToString());

                IReadOnlyList<LuaModInfo> loaded = container.Resolve<LuaModRuntime>().ListMods();
                Assert.AreEqual(LuaCapabilities.All | LuaCapabilities.Full, loaded[0].Capabilities);
            }
            finally
            {
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
                builder.RegisterInstance<ILog>(Log.Instance);
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
#endif