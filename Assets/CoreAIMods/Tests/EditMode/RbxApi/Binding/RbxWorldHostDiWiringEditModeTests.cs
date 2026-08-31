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
                Assert.IsTrue(host.Registry.TryGetRecord(part.Id, out InstanceRecord record));
                Assert.IsNull(record.OwnerActorId,
                    "the default local actor is the unrestricted host, not an owned-world actor");
                Assert.AreEqual(InstanceAccessScope.SharedWritable, record.AccessScope);
                InstanceId partId = part.Id;
                Assert.IsTrue(host.Binder.TryGetBoundObject(partId, out _),
                    "the part must be materialized as a GameObject while the mod is loaded");
                Assert.AreEqual(1, host.Registry.GetOwnedBy("leaker").Count,
                    "the created instance must be tagged as owned by the mod");
                Assert.AreEqual(3, host.Registry.GetTeardownOwnedBy("leaker").Count,
                    "teardown ownership must include the authored part plus the runtime Folder/Script proxy");

                // WHY: Unload routes through TeardownModEffects(Unload) -> ModTearingDown -> the installer's
                // ownership sweep, so the mod's instances (and their GameObjects) must be destroyed — no leak.
                Assert.IsTrue(runtime.UnloadMod(actorContext, "leaker"));

                Assert.IsFalse(host.Binder.TryGetBoundObject(partId, out _),
                    "unloading the mod must release the backing GameObject of the instance it owned");
                Assert.IsNull(host.Registry.WorldRoot.FindFirstChild("LeakPart"),
                    "the destroyed part must no longer be in the world tree after unload");
                Assert.AreEqual(0, host.Registry.GetTeardownOwnedBy("leaker").Count,
                    "unload must also remove the mod's runtime Folder/Script proxy");
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void UnloadSourceMod_DoesNotDestroyCloneOwnedByCloningMod()
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
                ActorContext sourceActor = Actor("clone-source-actor");
                ActorContext cloningActor = Actor("clone-owner-actor");
                runtime.LoadMod(sourceActor, "clone-source-mod", @"
                    local source = Instance.new('Folder')
                    source.Name = 'CloneSource'
                    local child = Instance.new('Part')
                    child.Parent = source
                    source.Parent = workspace", persistToStore: false);
                runtime.LoadMod(cloningActor, "clone-owner-mod", @"
                    local clone = workspace:FindFirstChild('CloneSource'):Clone()
                    clone.Name = 'SurvivingClone'
                    clone.Parent = workspace", persistToStore: false);

                RbxInstance clone = host.Registry.WorldRoot.FindFirstChild("SurvivingClone");
                Assert.IsNotNull(clone);
                Assert.IsTrue(host.Registry.TryGetRecord(clone.Id, out InstanceRecord cloneRecord));
                Assert.AreEqual("clone-owner-mod", cloneRecord.OwnerModId);
                Assert.AreEqual(OriginTag.FromMod("clone-owner-mod"), cloneRecord.OriginTag);
                Assert.AreEqual(cloningActor.ActorId, cloneRecord.OwnerActorId);

                Assert.IsTrue(runtime.UnloadMod(sourceActor, "clone-source-mod"));

                Assert.IsNull(host.Registry.WorldRoot.FindFirstChild("CloneSource"));
                Assert.AreSame(clone, host.Registry.WorldRoot.FindFirstChild("SurvivingClone"));
                Assert.IsFalse(clone.IsDestroyed);
                Assert.IsNotNull(clone.FindFirstChildOfClass("Part"));
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void ProductionRuntime_NewWorldAttributesOwnerAndEnforcesAclScopes()
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
                ActorContext actorA = Actor("production-actor-a");
                ActorContext actorB = Actor("production-actor-b");
                runtime.LoadMod(actorA, "production-owner-a", @"
                    local folder = Instance.new('Folder')
                    folder.Name = 'ProductionOwnedByA'
                    folder.Parent = workspace", persistToStore: false);

                RbxInstance owned = host.Registry.WorldRoot.FindFirstChild("ProductionOwnedByA");
                Assert.IsNotNull(owned);
                Assert.AreEqual(InstanceRegistry.CurrentWorldAclVersion, host.Registry.WorldAclVersion);
                Assert.IsTrue(host.Registry.TryGetRecord(owned.Id, out InstanceRecord ownedRecord));
                Assert.AreEqual(actorA.ActorId, ownedRecord.OwnerActorId);
                Assert.AreEqual(InstanceAccessScope.Owned, ownedRecord.AccessScope);

                RbxInstance camera = host.Registry.WorldRoot.FindFirstChildOfClass("Camera");
                Assert.IsTrue(host.Registry.TryGetRecord(camera.Id, out InstanceRecord cameraRecord));
                Assert.AreEqual(InstanceAccessScope.HostProtected, cameraRecord.AccessScope);
                System.Exception writeError = Assert.Catch(() => runtime.LoadMod(
                    actorB,
                    "production-writer-b",
                    "workspace:FindFirstChild('ProductionOwnedByA').Name = 'Stolen'",
                    persistToStore: false));
                StringAssert.Contains("Owned by actor 'production-actor-a'", writeError.ToString());
                Assert.AreEqual("ProductionOwnedByA", owned.Name);

                Assert.DoesNotThrow(() => runtime.LoadMod(
                    actorB,
                    "production-camera-b",
                    "workspace.CurrentCamera.CFrame = CFrame.new(1, 2, 3)",
                    persistToStore: false));
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void ProductionRuntime_LegacyWorldKeepsCrossActorMutationCompatibility()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();

            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry, worldAclVersion: null);
            builder.RegisterInstance(host);

            IObjectResolver container = builder.Build();
            try
            {
                ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
                ActorContext actorA = Actor("legacy-production-a");
                ActorContext actorB = Actor("legacy-production-b");
                runtime.LoadMod(actorA, "legacy-production-owner", @"
                    local folder = Instance.new('Folder')
                    folder.Name = 'LegacyProductionOwned'
                    folder.Parent = workspace", persistToStore: false);

                Assert.IsNull(host.Registry.WorldAclVersion);
                Assert.DoesNotThrow(() => runtime.LoadMod(
                    actorB,
                    "legacy-production-destroyer",
                    "workspace:FindFirstChild('LegacyProductionOwned'):Destroy()",
                    persistToStore: false));
                Assert.IsNull(host.Registry.WorldRoot.FindFirstChild("LegacyProductionOwned"));
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
        private static ActorContext Actor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private void RegisterMinimalModStack(ContainerBuilder builder,
            CoreAiPrefabRegistryAsset registry,
            int? worldAclVersion = InstanceRegistry.CurrentWorldAclVersion)
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
            builder.RegisterCoreAiMods(worldAclVersion: worldAclVersion);
        }

        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
