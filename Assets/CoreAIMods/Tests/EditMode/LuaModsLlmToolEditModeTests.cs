using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances.Scheduling;
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
            bool allowManagement = true,
            IActorIdentityProvider actorIdentityProvider = null)
        {
            return new LuaModsLlmTool(
                runtime,
                _settings,
                NullLog.Instance,
                granted,
                allowManagement,
                actorIdentityProvider ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider,
                BuiltInAgentRoleIds.Programmer);
        }

        private static async Task<JObject> ExecuteAsync(LuaModsLlmTool tool, string action, string modId = null,
            string code = null, int revision = -1)
        {
            string json = await tool.ExecuteAsync(action, modId, code, revision: revision);
            return JObject.Parse(json);
        }

        private static void AssertOwnershipDenied(JObject result, string callerActorId, string ownerActorId)
        {
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            string message = result.Value<string>("message");
            StringAssert.Contains($"actor '{callerActorId}'", message);
            StringAssert.Contains($"owned by actor '{ownerActorId}'", message);
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
        public async Task Actor_CanManageItsOwnMod()
        {
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            IActorIdentityProvider actorA = new LocalActorIdentityProvider(
                "actor-a", "session-a", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            LuaModsLlmTool tool = CreateTool(runtime, actorIdentityProvider: actorA);

            Assert.IsTrue((await ExecuteAsync(tool, "load", "owned", "local value = 1")).Value<bool>("success"));
            Assert.AreEqual("actor-a", runtime.GetModOwnerActorId("owned"));
            Assert.IsTrue((await ExecuteAsync(tool, "reload", "owned", "local value = 2")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(tool, "revert", "owned", revision: 0)).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(tool, "unload", "owned")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(tool, "load", "owned", "local value = 3")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(tool, "forget", "owned")).Value<bool>("success"));
        }

        [Test]
        public async Task CrossActorMutations_AreRefusedWithCallerAndOwnerReason()
        {
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            IActorIdentityProvider actorA = new LocalActorIdentityProvider(
                "actor-a", "session-a", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            IActorIdentityProvider actorB = new LocalActorIdentityProvider(
                "actor-b", "session-b", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            LuaModsLlmTool toolA = CreateTool(runtime, actorIdentityProvider: actorA);
            LuaModsLlmTool toolB = CreateTool(runtime, actorIdentityProvider: actorB);

            Assert.IsTrue((await ExecuteAsync(toolB, "load", "foreign", "local value = 1")).Value<bool>("success"));
            AssertOwnershipDenied(
                await ExecuteAsync(toolA, "load", "foreign", "local value = 2"), "actor-a", "actor-b");
            AssertOwnershipDenied(await ExecuteAsync(toolA, "unload", "foreign"), "actor-a", "actor-b");
            AssertOwnershipDenied(
                await ExecuteAsync(toolA, "reload", "foreign", "local value = 2"), "actor-a", "actor-b");
            AssertOwnershipDenied(
                await ExecuteAsync(toolA, "revert", "foreign", revision: 0), "actor-a", "actor-b");
            AssertOwnershipDenied(await ExecuteAsync(toolA, "forget", "foreign"), "actor-a", "actor-b");
            Assert.IsTrue(runtime.IsLoaded("foreign"));
        }

        [Test]
        public async Task CrossActorMetadataRemainsReadable_ButSensitiveReadsAreRefused()
        {
            LuaCsModRuntime runtime = new(
                versionStore: new MemoryLuaScriptVersionStore(),
                maxErrorsBeforeQuarantine: 2);
            IActorIdentityProvider actorA = new LocalActorIdentityProvider(
                "actor-a", "session-a", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            IActorIdentityProvider actorB = new LocalActorIdentityProvider(
                "actor-b", "session-b", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            LuaModsLlmTool toolA = CreateTool(runtime, actorIdentityProvider: actorA);
            LuaModsLlmTool toolB = CreateTool(runtime, actorIdentityProvider: actorB);

            Assert.IsTrue((await ExecuteAsync(
                toolB,
                "load",
                "foreign",
                "hooks_on('boom', function() error('private failure') end)")).Value<bool>("success"));
            runtime.EmitEvent("boom", "");
            runtime.Tick(0);

            JObject list = await ExecuteAsync(toolA, "list");
            Assert.IsTrue(list.Value<bool>("success"));
            JArray metadata = list["data"] as JArray;
            Assert.IsNotNull(metadata);
            Assert.AreEqual(1, metadata.Count, "Cross-actor mod metadata must remain discoverable.");
            Assert.AreEqual("foreign", metadata[0]?["id"]?.Value<string>());
            AssertOwnershipDenied(await ExecuteAsync(toolA, "get_source", "foreign"), "actor-a", "actor-b");
            AssertOwnershipDenied(await ExecuteAsync(toolA, "versions", "foreign"), "actor-a", "actor-b");
            AssertOwnershipDenied(await ExecuteAsync(toolA, "diagnostics", "foreign"), "actor-a", "actor-b");
            AssertOwnershipDenied(await ExecuteAsync(toolA, "export", "foreign"), "actor-a", "actor-b");
        }

        [Test]
        public async Task DefaultLocalAndHostPaths_RetainFullAccess()
        {
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            IActorIdentityProvider actorB = new LocalActorIdentityProvider(
                "actor-b", "session-b", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            LuaModsLlmTool toolB = CreateTool(runtime, actorIdentityProvider: actorB);
            LuaModsLlmTool host = CreateTool(runtime);

            Assert.IsTrue((await ExecuteAsync(toolB, "load", "foreign", "local value = 1")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "get_source", "foreign")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "versions", "foreign")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "diagnostics", "foreign")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "export", "foreign")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "reload", "foreign", "local value = 2")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "revert", "foreign", revision: 0)).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "unload", "foreign")).Value<bool>("success"));
            Assert.IsTrue((await ExecuteAsync(host, "forget", "foreign")).Value<bool>("success"));

            Assert.IsTrue((await ExecuteAsync(host, "load", "local-owned", "local value = 3")).Value<bool>("success"));
            Assert.AreEqual(LocalActorIdentityProvider.DefaultActorId, runtime.GetModOwnerActorId("local-owned"));
        }

        [Test]
        public void UnconfiguredComposition_DefaultLocalActorHasHostRuntimeAuthority()
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();

            using IObjectResolver container = builder.Build();
            ActorContext host = container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext ordinary = new LocalActorIdentityProvider("ordinary-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            LuaCsModRuntime runtime = new();
            string observedModId = null;
            System.Action<string, string, LuaCapabilities> listener =
                (string id, string source, LuaCapabilities capabilities) => observedModId = id;

            Assert.AreEqual(LocalActorIdentityProvider.DefaultActorId, host.ActorId);
            Assert.IsTrue(host.Grants.IsUnrestricted);
            Assert.IsFalse(ordinary.Grants.IsUnrestricted);
            runtime.AddModSourceLoadedListener(host, listener);
            try
            {
                runtime.LoadMod(ordinary, "ordinary-owned", "local value = 1", persistToStore: false);
                runtime.Tick(host, 0d);
                IReadOnlyList<LuaModHandlerError> diagnostics =
                    runtime.GetRecentHandlerErrors(host, "ordinary-owned");

                Assert.AreEqual("ordinary-owned", observedModId);
                Assert.IsNotNull(diagnostics);
            }
            finally
            {
                runtime.RemoveModSourceLoadedListener(host, listener);
            }
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

        private sealed class RuntimeObservability : IRbxRuntimeObservabilitySink
        {
            private long _guardedInstructionSteps;
            private long _threadResumes;
            private long _eventsDelivered;
            private long _completedOperations;

            public bool IsEnabled => true;

            public long GuardedInstructionSteps => Interlocked.Read(ref _guardedInstructionSteps);

            public long ThreadResumes => Interlocked.Read(ref _threadResumes);

            public long EventsDelivered => Interlocked.Read(ref _eventsDelivered);

            public long CompletedOperations => Interlocked.Read(ref _completedOperations);

            public void RecordGuardedInstructionSteps(long count)
            {
                Interlocked.Add(ref _guardedInstructionSteps, count);
            }

            public void RecordThreadResumes(long count)
            {
                Interlocked.Add(ref _threadResumes, count);
            }

            public void RecordEventsDelivered(long count)
            {
                Interlocked.Add(ref _eventsDelivered, count);
            }

            public void RecordCompletedOperations(long count)
            {
                Interlocked.Add(ref _completedOperations, count);
            }
        }

        [Test]
        public void RegisterCoreAiMods_ObservabilitySink_ReceivesProductionPathWork()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            try
            {
                RuntimeObservability observability = new();
                ContainerBuilder builder = new();
                builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
                builder.RegisterInstance<ILog>(_log);
                builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
                builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
                builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
                builder.RegisterInstance<ICoreAISettings>(_settings);
                builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
                builder.RegisterInstance<IRbxRuntimeObservabilitySink>(observability);

                builder.RegisterWorldCommands(registry);
                builder.RegisterCoreAiMods();

                using IObjectResolver container = builder.Build();
                LuaCsModStack stack = container.Resolve<LuaCsModStack>();
                LuaCsRbxApiBindings rbxApi = stack.GameplayBindings.RbxApi;
                stack.Runtime.LoadMod("observability-probe", @"
                    hooks_on('measure', function()
                        local total = 0
                        for index = 1, 32 do total = total + index end
                    end)
                    task.defer(function()
                        local total = 0
                        for index = 1, 32 do total = total + index end
                    end)", persistToStore: false);

                stack.Runtime.EmitEvent("measure", "");
                stack.Runtime.Tick(0d);
                rbxApi.Scheduler.Advance(0d);

                Assert.Greater(observability.GuardedInstructionSteps, 0);
                Assert.Greater(observability.ThreadResumes, 0);
                Assert.Greater(observability.EventsDelivered, 0);
                Assert.Greater(observability.CompletedOperations, 0);
            }
            finally
            {
                Object.DestroyImmediate(registry);
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
        public void RegisterCoreAiMods_RegistersLogService_AttachesGetModLogs_AndFeedsItFromTheRuntime()
        {
            // WHY: this container has no RbxWorldHost — headless is deliberate (see
            // RegisterCoreAiMods_AttachesLuaTools_ToProgrammerRole).
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

                ILuaLogService logService = container.Resolve<ILuaLogService>();
                Assert.IsInstanceOf<LuaLogService>(logService);
                Assert.AreSame(logService, container.Resolve<ILuaLogService>(),
                    "ILuaLogService must be a container singleton so the runtime, the LLM tool and the " +
                    "MCP tool all share one buffer.");

                AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer);
                Assert.IsTrue(HasTool(tools, "get_mod_logs"), "Programmer must get get_mod_logs");

                // WHY: end-to-end proof the DI graph is closed — a mod's print must land in the SAME
                // service instance the get_mod_logs tool reads. persistToStore: false keeps the shared
                // EditMode file store untouched.
                container.Resolve<LuaCsModRuntime>().LoadMod(
                    "log_probe", "print('hello-from-probe')", persistToStore: false);
                IReadOnlyList<LuaLogEntry> entries = logService.Query(new LuaLogQuery { ModId = "log_probe" });
                Assert.AreEqual(1, entries.Count);
                Assert.AreEqual(LuaLogLevel.Print, entries[0].Level);
                StringAssert.Contains("hello-from-probe", entries[0].Message);
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

                ActorContext actorContext = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                IReadOnlyList<LuaModInfo> loaded = container.Resolve<ILuaModRuntime>().ListMods(actorContext);
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
