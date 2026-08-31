using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// Proves the CoreAiModsInstaller DI seam end-to-end: when a <see cref="RbxWorldHost"/> is
    /// registered in the container, <c>RegisterCoreAiMods</c>' LuaCsModStack factory resolves it and
    /// builds the Rbx Lua surface over the host's registry/game with the host binder as part sink, so
    /// one-off <c>execute_lua</c> Instance.new('Part') materializes a real GameObject under the host
    /// transform (golden 0.28 pose). Without a host the same Lua stays headless in-memory.
    /// </summary>
    [TestFixture]
    public sealed class RbxWorldHostDiWiringEditModeTests
    {
        private const float Epsilon = 1e-4f;

        // WHY: the Lua-CSharp runtime bridges its async VM to a synchronous call site; detaching Unity's
        // SynchronizationContext lets VM continuations complete on the thread pool instead of deadlocking
        // the blocked main thread (mirrors RbxApiLuaBindingsEditModeTests).
        private SynchronizationContext _savedContext;
        private CoreAISettingsAsset _settings;
        private RecordingLog _log;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _log = new RecordingLog();
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
            Object.DestroyImmediate(_settings);
            RbxSpace.ResetForTests();
        }

        [Test]
        public void HostRegistered_LuaPartMaterializesGameObject()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);

            IObjectResolver container = builder.Build();
            try
            {
                LuaTool.ILuaExecutor executor = container.Resolve<LuaTool.ILuaExecutor>();
                LuaTool.LuaResult result = executor.ExecuteAsync(
                        "local p = Instance.new('Part') p.Parent = workspace p.Position = Vector3.new(0, 5, 0)",
                        CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.Success, result.Error);

                RbxInstance part = host.Registry.WorldRoot.FindFirstChild("Part");
                Assert.IsNotNull(part, "Lua Instance.new('Part') must exist in the host registry");
                Assert.IsTrue(host.Binder.TryGetBoundObject(part.Id, out GameObject partGo),
                    "a registered host must materialize the Lua part as a GameObject");

                Assert.IsTrue(partGo.transform.IsChildOf(hostGo.transform),
                    "materialized part must live under the host transform");

                // Golden 0.28 pose (D3): Roblox (0, 5, 0) studs -> Unity (0, 1.4, 0), z mirrored.
                // Reuses InstanceGameObjectBinderEditModeTests.PositionGolden_At028 math.
                Vector3 position = partGo.transform.position;
                Assert.AreEqual(0f, position.x, Epsilon);
                Assert.AreEqual(1.4f, position.y, Epsilon);
                Assert.AreEqual(0f, position.z, Epsilon, "mod-space z = -Unity z (D2)");
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(hostGo);
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void NoHost_StaysHeadless()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            // WHY: no RegisterInstance(host) — ResolveOrDefault<RbxWorldHost>() yields null, so the
            // factory builds an in-memory Rbx surface with no part sink and nothing materializes.
            IObjectResolver container = builder.Build();
            try
            {
                LuaTool.ILuaExecutor executor = container.Resolve<LuaTool.ILuaExecutor>();
                LuaTool.LuaResult result = executor.ExecuteAsync(
                        "local p = Instance.new('Part') p.Parent = workspace p.Position = Vector3.new(0, 5, 0)",
                        CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.Success, result.Error);
                Assert.IsNull(GameObject.Find("Part"),
                    "without a registered host the world stays headless — no GameObject is created");

                // A shipped game must learn that Instance.new will render nothing, so the missing host is
                // reported as an error rather than silently tolerated.
                Assert.IsTrue(_log.HasError("RbxWorldHost NOT resolved"),
                    "the factory must report the missing host so a misconfigured game is not left guessing");
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
            }
        }

        [Test]
        public void UnloadMod_DestroysInstancesTheModOwned()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            builder.RegisterInstance(host);

            IObjectResolver container = builder.Build();
            try
            {
                ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
                ActorContext actorContext = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                runtime.LoadMod(actorContext, "leaker",
                    "local p = Instance.new('Part') p.Name = 'LeakPart' p.Parent = workspace");

                RbxInstance part = host.Registry.WorldRoot.FindFirstChild("LeakPart");
                Assert.IsNotNull(part, "the mod's Instance.new('Part') must exist while the mod is loaded");
                InstanceId partId = part.Id;
                Assert.IsTrue(host.Binder.TryGetBoundObject(partId, out _),
                    "the part must be materialized as a GameObject while the mod is loaded");
                Assert.AreEqual(1, host.Registry.GetOwnedBy("leaker").Count,
                    "the created instance must be tagged as owned by the mod");

                // WHY: Unload routes through TeardownModEffects(Unload) -> ModTearingDown -> the installer's
                // ownership sweep, so the mod's instances (and their GameObjects) must be destroyed — no leak.
                Assert.IsTrue(runtime.UnloadMod(actorContext, "leaker"));

                Assert.IsFalse(host.Binder.TryGetBoundObject(partId, out _),
                    "unloading the mod must release the backing GameObject of the instance it owned");
                Assert.IsNull(host.Registry.WorldRoot.FindFirstChild("LeakPart"),
                    "the destroyed part must no longer be in the world tree after unload");
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        /// <summary>Minimal registrations the LuaCsModStack factory resolves (mirrors the proven
        /// bootstrap in LuaModsLlmToolEditModeTests): required IGameLogger + IAiGameCommandSink, plus
        /// the ResolveOrDefault conveniences and the world-command executors the installer expects.</summary>
        private void RegisterMinimalModStack(ContainerBuilder builder, CoreAiPrefabRegistryAsset registry)
        {
            builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
            // WHY: an explicit recorder, not the ambient Log.Instance — the factory's diagnostics are then
            // assertable regardless of what the rest of the run did to the global logger and log filter.
            builder.RegisterInstance<ILog>(_log);
            builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton).As<ILuaScriptVersionStore>();
            builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.RegisterInstance<ICoreAISettings>(_settings);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);

            builder.RegisterWorldCommands(registry);
            builder.RegisterCoreAiMods();
        }

        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
