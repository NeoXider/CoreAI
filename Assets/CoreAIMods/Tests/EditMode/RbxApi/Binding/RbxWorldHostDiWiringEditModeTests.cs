using System.Threading;
using System.Reflection;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Hub;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.TestTools;
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
        public void ModsAssembly_ReferencesUnitySpatialAssemblyDirectly()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string asmdefPath = Path.Combine(
                projectRoot,
                "Assets",
                "CoreAIMods",
                "Runtime",
                "CoreAI.Mods.asmdef");
            JObject asmdef = JObject.Parse(File.ReadAllText(asmdefPath));
            JArray references = (JArray)asmdef["references"];
            bool found = false;
            foreach (JToken reference in references)
            {
                if (string.Equals(
                        (string)reference,
                        "CoreAI.RbxApi.Unity",
                        System.StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            Assert.IsTrue(
                found,
                "CoreAI.Mods uses RbxSpace and Unity/Bee requires its assembly as a direct reference");
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
        public void NoHost_UnloadDestroysOwnedInstancesAndReleasesRegistryCount()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry);
            IObjectResolver container = builder.Build();
            try
            {
                LuaCsModStack stack = container.Resolve<LuaCsModStack>();
                ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
                InstanceRegistry headlessRegistry = stack.GameplayBindings.RbxApi.Registry;
                ActorContext actor = Actor("headless-unload-actor");
                int authoredBefore = headlessRegistry.AuthoredCount;
                runtime.LoadMod(actor, "headless-leaker", @"
                    local folder = Instance.new('Folder')
                    folder.Name = 'HeadlessLeak'
                    folder.Parent = workspace", persistToStore: false);

                Assert.IsNotNull(headlessRegistry.WorldRoot.FindFirstChild("HeadlessLeak"));
                Assert.AreEqual(3, headlessRegistry.GetTeardownOwnedBy("headless-leaker").Count);
                Assert.Greater(headlessRegistry.AuthoredCount, authoredBefore);

                Assert.IsTrue(runtime.UnloadMod(actor, "headless-leaker"));

                Assert.IsNull(headlessRegistry.WorldRoot.FindFirstChild("HeadlessLeak"));
                Assert.AreEqual(0, headlessRegistry.GetTeardownOwnedBy("headless-leaker").Count);
                Assert.AreEqual(authoredBefore, headlessRegistry.AuthoredCount);
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
            GameObject cameraGo = new("ProductionCamera");
            Camera unityCamera = cameraGo.AddComponent<Camera>();
            FieldInfo cameraField = typeof(RbxWorldHost).GetField(
                "_camera", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cameraField);
            cameraField.SetValue(host, unityCamera);
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

                RbxInstance rbxCamera = host.Registry.WorldRoot.FindFirstChildOfClass("Camera");
                Assert.IsTrue(host.Registry.TryGetRecord(rbxCamera.Id, out InstanceRecord cameraRecord));
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
                Assert.IsInstanceOf<UnityCameraRig>(host.CameraRig);
                Assert.AreEqual(0.28f, unityCamera.transform.position.x, Epsilon);
                Assert.AreEqual(0.56f, unityCamera.transform.position.y, Epsilon);
                Assert.AreEqual(-0.84f, unityCamera.transform.position.z, Epsilon);
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(cameraGo);
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

        [Test]
        public void ConfirmedPackageLoad_SwapsEveryFacadeAndRestartsOnlyActiveModsOnce()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            string storeId = "w33-" + System.Guid.NewGuid().ToString("N");
            RecordingNetworkBridge network = new();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: storeId,
                networkBridge: network);
            builder.RegisterInstance(host);

            IObjectResolver container = builder.Build();
            try
            {
                RbxWorldRuntimeSessionController controller =
                    container.Resolve<RbxWorldRuntimeSessionController>();
                IRbxWorldRuntimeService service = container.Resolve<IRbxWorldRuntimeService>();
                ILuaModRuntime stableRuntime = container.Resolve<ILuaModRuntime>();
                LuaTool.ILuaExecutor stableExecutor = container.Resolve<LuaTool.ILuaExecutor>();
                LuaCsModStack stableStack = container.Resolve<LuaCsModStack>();
                LuaCsLogicSlots stableSlots = container.Resolve<LuaCsLogicSlots>();
                stableSlots.DeclareSlot("restored-formula");
                ILuaModSourceStore stableSources = container.Resolve<ILuaModSourceStore>();
                ILuaModStore modData = container.Resolve<ILuaModStore>();
                ConfirmedWorldMutationGate concreteMutationGate =
                    container.Resolve<ConfirmedWorldMutationGate>();
                IConfirmedWorldMutationGate mutationGate =
                    container.Resolve<IConfirmedWorldMutationGate>();
                Assert.AreSame(concreteMutationGate, mutationGate);
                FieldInfo executorGateField = typeof(LuaCsGameToolExecutor).GetField(
                    "_worldMutationGate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(executorGateField);
                Assert.AreSame(
                    mutationGate,
                    executorGateField.GetValue(stableStack.ToolExecutor));
                AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                LuaModsLlmTool manageModsTool = null;
                foreach (ILlmTool tool in policy.GetToolsForRole(BuiltInAgentRoleIds.Programmer))
                {
                    if (tool is LuaModsLlmTool candidateTool)
                    {
                        manageModsTool = candidateTool;
                        break;
                    }
                }

                Assert.IsNotNull(manageModsTool);
                FieldInfo manageModsGateField = typeof(LuaModsLlmTool).GetField(
                    "_worldMutationGate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(manageModsGateField);
                Assert.AreSame(mutationGate, manageModsGateField.GetValue(manageModsTool));
                LuaCsModRuntime outgoingRuntime = controller.CurrentConcreteRuntime;
                LuaCsRbxApiBindings outgoingRbxApi = controller.CurrentRbxApi;
                InstanceRegistry outgoingRegistry = controller.CurrentRbxApi.Registry;
                ActorContext actor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                LuaCsModRuntimeHubService hubService = new(
                    stableRuntime,
                    actor,
                    stableSources);
                RbxWorldPackagePayload captured = service.CaptureCurrent();
                RbxWorldModSource active = new(
                    new LuaModManifest
                    {
                        Id = "active-restored",
                        Name = "active-restored",
                        Capabilities = LuaCapabilities.All.ToString(),
                        Active = true
                    },
                    @"local marker = Instance.new('Folder')
marker.Name = 'ActiveStart'
marker.Parent = workspace
logic_define('restored-formula', function() return 42 end)
hooks_on('probe', function()
    local callback = Instance.new('Folder')
    callback.Name = 'NewCallback'
    callback.Parent = workspace
end)
game:GetService('RunService').Heartbeat:Connect(function()
    local count = tonumber(store_get('heartbeat_count')) or 0
    store_set('heartbeat_count', tostring(count + 1))
end)");
                RbxWorldModSource dormant = new(
                    new LuaModManifest
                    {
                        Id = "dormant-restored",
                        Name = "dormant-restored",
                        Capabilities = LuaCapabilities.All.ToString(),
                        Active = false
                    },
                    "local marker = Instance.new('Folder') marker.Name = 'DormantStart' marker.Parent = workspace");
                RbxWorldPackagePayload package = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[] { active, dormant });

                outgoingRuntime.LoadMod(
                    actor,
                    "outgoing-listener",
                    @"hooks_on('probe', function()
    local callback = Instance.new('Folder')
    callback.Name = 'OldCallback'
    callback.Parent = workspace
end)
game:GetService('RunService').Heartbeat:Connect(function()
    store_set('old_heartbeat', 'fired')
end)
task.delay(0, function()
    store_set('old_delay', 'fired')
end)
local outgoing_remote = Instance.new('RemoteEvent')
outgoing_remote.Name = 'OutgoingRemote'
outgoing_remote.Parent = workspace
outgoing_remote.OnServerEvent:Connect(function()
    store_set('old_remote', 'fired')
end)",
                    persistToStore: false);
                RbxInstance outgoingRemote = outgoingRegistry.WorldRoot.FindFirstChild(
                    "OutgoingRemote");
                Assert.IsNotNull(outgoingRemote);

                RbxWorldLoadResult result = service.LoadConfirmedAsync(package)
                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.Success, result.Error);
                Assert.AreEqual(1, result.ActiveModsStarted);
                Assert.IsTrue(outgoingRegistry.IsDetached);
                Assert.AreNotSame(outgoingRuntime, controller.CurrentConcreteRuntime);
                Assert.AreSame(stableRuntime, container.Resolve<ILuaModRuntime>());
                Assert.AreSame(stableExecutor, container.Resolve<LuaTool.ILuaExecutor>());
                Assert.AreSame(stableStack, container.Resolve<LuaCsModStack>());
                Assert.AreSame(stableSlots, container.Resolve<LuaCsLogicSlots>());
                Assert.AreSame(stableSources, container.Resolve<ILuaModSourceStore>());
                Assert.AreSame(host.Registry, stableStack.GameplayBindings.RbxApi.Registry);
                Assert.IsNotNull(host.Registry.WorldRoot.FindFirstChild("ActiveStart"));
                Assert.IsNull(host.Registry.WorldRoot.FindFirstChild("DormantStart"));
                int activeMarkers = 0;
                foreach (RbxInstance owned in host.Registry.GetTeardownOwnedBy("active-restored"))
                {
                    if (owned.Name == "ActiveStart")
                    {
                        activeMarkers++;
                    }
                }

                Assert.AreEqual(1, activeMarkers);
                Assert.AreEqual(0, host.Registry.GetTeardownOwnedBy("dormant-restored").Count);
                Assert.AreEqual(2, stableSources.List().Count);
                Assert.IsTrue(stableSources.TryLoad(
                    "active-restored",
                    out string exactSource,
                    out LuaModManifest exactManifest));
                Assert.AreEqual(active.Source, exactSource);
                Assert.IsTrue(exactManifest.Active);
                bool restoredInvoked = stableSlots.TryInvokeNumber(
                    "restored-formula",
                    out double restoredFormula);
                Assert.IsTrue(
                    restoredInvoked,
                    "declared=" + string.Join(",", stableSlots.DeclaredSlots)
                    + "; overridden=" + stableSlots.IsOverridden("restored-formula")
                    + "; lastError=" + stableSlots.LastError);
                Assert.AreEqual(42d, restoredFormula);
                Assert.IsTrue(hubService.IsLoaded("active-restored"));
                Assert.DoesNotThrow(() => hubService.RecentErrors("active-restored"));

                outgoingRuntime.EmitEvent(actor, "probe");
                outgoingRuntime.Tick(actor, 1d);
                Assert.DoesNotThrow(() => outgoingRbxApi.Scheduler.Advance(1d));
                Assert.DoesNotThrow(() => outgoingRbxApi.PumpHeartbeat(1f));
                network.RegisterActor("network-teardown-client");
                network.EmitEvent(new RbxNetworkEventMessage(
                    outgoingRemote.Id,
                    RbxNetworkDirection.ClientToServer,
                    RbxNetworkReliability.ReliableOrdered,
                    "network-teardown-client",
                    null,
                    System.Text.Encoding.UTF8.GetBytes("[]")));
                Assert.DoesNotThrow(() => outgoingRbxApi.Scheduler.Advance(1d));
                Assert.IsNull(host.Registry.WorldRoot.FindFirstChild("OldCallback"));
                Assert.AreEqual("", modData.Get("outgoing-listener", "old_heartbeat"));
                Assert.AreEqual("", modData.Get("outgoing-listener", "old_delay"));
                Assert.AreEqual("", modData.Get("outgoing-listener", "old_remote"));
                Assert.Throws<System.ObjectDisposedException>(() => outgoingRuntime.LoadMod(
                    actor,
                    "rejected-stale-runtime",
                    "return true",
                    persistToStore: false));
                controller.CurrentRbxApi.PumpHeartbeat(1f);
                controller.CurrentRbxApi.Scheduler.Advance(0d);
                Assert.AreEqual("1", modData.Get("active-restored", "heartbeat_count"));
                stableRuntime.EmitEvent(actor, "probe");
                stableRuntime.Tick(actor, 0d);
                Assert.IsNotNull(host.Registry.WorldRoot.FindFirstChild("NewCallback"));
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void StableRuntime_DisposeThenUnsubscribeIsIdempotent_WhileOperationsStillReject()
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
                RbxWorldRuntimeSessionController controller =
                    container.Resolve<RbxWorldRuntimeSessionController>();
                ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
                ActorContext actor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                System.Action<string, string, LuaCapabilities> sourceListener =
                    (modId, source, capabilities) => { };
                System.Action<string, string, string> eventListener =
                    (modId, name, payload) => { };
                runtime.AddModSourceLoadedListener(actor, sourceListener);
                runtime.AddModEventEmittedListener(actor, eventListener);

                controller.Dispose();

                Assert.DoesNotThrow(() =>
                    runtime.RemoveModSourceLoadedListener(actor, sourceListener));
                Assert.DoesNotThrow(() =>
                    runtime.RemoveModEventEmittedListener(actor, eventListener));
                Assert.DoesNotThrow(() =>
                    runtime.RemoveModSourceLoadedListener(actor, sourceListener));
                Assert.DoesNotThrow(() =>
                    runtime.RemoveModEventEmittedListener(actor, eventListener));
                Assert.Throws<System.ObjectDisposedException>(() => runtime.ListMods(actor));
                Assert.Throws<System.ObjectDisposedException>(() =>
                    runtime.AddModEventEmittedListener(actor, eventListener));
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void CustomTransactionalSourceBackend_DrivesRuntimeCaptureLoadAndHubFacade()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            TransactionalMemorySourceStore backend = new();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-custom-source-" + System.Guid.NewGuid().ToString("N"),
                worldSessionSourceStore: backend);
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
                ILuaModSourceStore stableSources = container.Resolve<ILuaModSourceStore>();
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                ActorContext actor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                runtime.LoadMod(
                    actor,
                    "custom-backed",
                    "local marker = Instance.new('Folder') marker.Name = 'CustomBackendStart' marker.Parent = workspace",
                    persistToStore: true);
                Assert.AreEqual(1, backend.SaveCalls);
                RbxWorldPackagePayload captured = worlds.CaptureCurrent();
                Assert.AreEqual(1, captured.Mods.Count);
                Assert.AreEqual("custom-backed", captured.Mods[0].Manifest.Id);
                string replacementSource = captured.Mods[0].Source
                    + "\nlocal replacement = Instance.new('Folder') "
                    + "replacement.Name = 'CustomBackendReplacement' replacement.Parent = workspace";
                RbxWorldPackagePayload replacementPackage = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[]
                    {
                        new RbxWorldModSource(
                            captured.Mods[0].Manifest,
                            replacementSource)
                    });
                backend.ThrowOnComplete = true;

                RbxWorldLoadResult loaded = worlds.LoadConfirmedAsync(replacementPackage)
                    .GetAwaiter().GetResult();

                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.AreEqual(1, backend.PrepareCalls);
                Assert.AreEqual(1, backend.ActivateCalls);
                Assert.AreEqual(1, backend.CompleteCalls);
                Assert.AreEqual(0, backend.ReplacementDisposeCalls);
                Assert.IsTrue(stableSources.TryLoad(
                    "custom-backed",
                    out string source,
                    out LuaModManifest manifest));
                Assert.AreEqual(replacementSource, source);
                Assert.IsTrue(manifest.Active);
                LuaCsModRuntimeHubService hub = new(runtime, actor, stableSources);
                Assert.IsTrue(hub.IsLoaded("custom-backed"));
                RbxWorldPackagePayload recaptured = worlds.CaptureCurrent();
                Assert.AreEqual(1, recaptured.Mods.Count);
                Assert.AreEqual(replacementSource, recaptured.Mods[0].Source);
                Assert.Throws<VContainerException>(() =>
                    container.Resolve<FileLuaModSourceStore>());
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void ConfirmedPackageLoad_CaseDistinctActiveModsKeepDataIsolatedAcrossRestartAndClear()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            string storeId = "w33-case-data-" + System.Guid.NewGuid().ToString("N");
            ContainerBuilder builder = new();
            RegisterMinimalModStack(builder, registry, modStoreId: storeId);
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                RbxWorldPackagePayload captured = worlds.CaptureCurrent();
                RbxWorldPackagePayload package = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[]
                    {
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "Case",
                                Name = "Case",
                                Capabilities = LuaCapabilities.All.ToString(),
                                Active = true
                            },
                            "store_set('shared', 'upper')"),
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "case",
                                Name = "case",
                                Capabilities = LuaCapabilities.All.ToString(),
                                Active = true
                            },
                            "store_set('shared', 'lower')")
                    });

                RbxWorldLoadResult loaded = worlds.LoadConfirmedAsync(package)
                    .GetAwaiter().GetResult();

                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.AreEqual(2, loaded.ActiveModsStarted);
                ILuaModStore liveStore = container.Resolve<ILuaModStore>();
                Assert.AreEqual("upper", liveStore.Get("Case", "shared"));
                Assert.AreEqual("lower", liveStore.Get("case", "shared"));

                FileLuaModStore restarted = new(storeId: storeId);
                try
                {
                    Assert.AreEqual("upper", restarted.Get("Case", "shared"));
                    Assert.AreEqual("lower", restarted.Get("case", "shared"));
                    restarted.Clear("Case");
                    Assert.AreEqual("", restarted.Get("Case", "shared"));
                    Assert.AreEqual("lower", restarted.Get("case", "shared"));
                }
                finally
                {
                    restarted.Dispose();
                }

                FileLuaModStore afterClearRestart = new(storeId: storeId);
                try
                {
                    Assert.AreEqual("", afterClearRestart.Get("Case", "shared"));
                    Assert.AreEqual("lower", afterClearRestart.Get("case", "shared"));
                    afterClearRestart.Clear("case");
                }
                finally
                {
                    afterClearRestart.Dispose();
                }
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void SourceActivateAndHostPrepublishFailures_RollBackCandidateAndKeepOutgoingUsable()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            TransactionalMemorySourceStore backend = new();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-hostile-phases-" + System.Guid.NewGuid().ToString("N"),
                worldSessionSourceStore: backend);
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                ILuaModRuntime runtime = container.Resolve<ILuaModRuntime>();
                ActorContext actor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                runtime.LoadMod(
                    actor,
                    "outgoing-usable",
                    "return true",
                    persistToStore: true);
                RbxWorldPackagePayload package = worlds.CaptureCurrent();
                InstanceRegistry outgoing = host.Registry;
                backend.ThrowOnActivate = true;

                RbxWorldLoadResult activateFailure = worlds.LoadConfirmedAsync(package)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(activateFailure.Success);
                StringAssert.Contains("source activation", activateFailure.Error);
                Assert.AreSame(outgoing, host.Registry);
                Assert.IsFalse(outgoing.IsDetached);
                Assert.AreEqual(1, backend.RollbackCalls);
                Assert.IsNull(hostGo.transform.Find("CoreAI_RbxWorld_Staging"));
                Assert.DoesNotThrow(() => runtime.EmitEvent(actor, "after-activate-reject"));

                backend.ThrowOnActivate = false;
                RbxWorldRuntimeSessionController controller =
                    container.Resolve<RbxWorldRuntimeSessionController>();
                FieldInfo hostField = typeof(RbxWorldRuntimeSessionController).GetField(
                    "_host",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                RbxWorldSessionHostAdapter sessionHost =
                    (RbxWorldSessionHostAdapter)hostField.GetValue(controller);
                sessionHost.BeforePublishForTests = () =>
                    throw new System.InvalidOperationException("injected host prepublish failure");

                RbxWorldLoadResult commitFailure = worlds.LoadConfirmedAsync(package)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(commitFailure.Success);
                StringAssert.Contains("host prepublish", commitFailure.Error);
                Assert.AreSame(outgoing, host.Registry);
                Assert.IsFalse(outgoing.IsDetached);
                Assert.AreEqual(2, backend.RollbackCalls);
                Assert.IsNull(hostGo.transform.Find("CoreAI_RbxWorld_Staging"));
                Assert.DoesNotThrow(() => runtime.LoadMod(
                    actor,
                    "after-host-reject",
                    "return true",
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
        public void ActiveFullCapabilityPackage_IsRejectedBeforeItCanMutateLiveUnityObjects()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject liveTarget = new("W33LiveFullTarget");
            liveTarget.transform.position = new Vector3(2f, 3f, 4f);
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-full-reject-" + System.Guid.NewGuid().ToString("N"));
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                RbxWorldPackagePayload captured = worlds.CaptureCurrent();
                RbxWorldPackagePayload hostile = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[]
                    {
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "active-full-hostile",
                                Name = "active-full-hostile",
                                Capabilities = (LuaCapabilities.All | LuaCapabilities.Full).ToString(),
                                Active = true
                            },
                            "local id = unity_find('W33LiveFullTarget') unity_destroy(id)")
                    });
                InstanceRegistry outgoing = host.Registry;

                RbxWorldLoadResult result = worlds.LoadConfirmedAsync(hostile)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.Success);
                StringAssert.Contains("cannot be isolated", result.Error);
                Assert.IsNotNull(liveTarget);
                Assert.AreEqual(new Vector3(2f, 3f, 4f), liveTarget.transform.position);
                Assert.AreSame(outgoing, host.Registry);
                Assert.IsFalse(outgoing.IsDetached);
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
                Object.DestroyImmediate(liveTarget);
            }
        }

        [Test]
        public void StagedNetworkBridge_RejectionIsSilentAndSuccessReplaysExactlyOnce()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            RecordingNetworkBridge network = new();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-network-stage-" + System.Guid.NewGuid().ToString("N"),
                networkBridge: network);
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService worlds = container.Resolve<IRbxWorldRuntimeService>();
                RbxWorldPackagePayload captured = worlds.CaptureCurrent();
                RbxWorldModSource sender = new(
                    new LuaModManifest
                    {
                        Id = "a-network-sender",
                        Name = "a-network-sender",
                        OwnerActorId = "network-stage-client",
                        Capabilities = LuaCapabilities.All.ToString(),
                        Active = true
                    },
                    @"local remote = Instance.new('RemoteEvent')
remote.Name = 'StagedRemote'
remote.Parent = workspace
remote:FireServer('queued')");
                RbxWorldModSource laterFailure = new(
                    new LuaModManifest
                    {
                        Id = "z-network-failure",
                        Name = "z-network-failure",
                        Capabilities = LuaCapabilities.Read.ToString(),
                        Active = true
                    },
                    "local network_failure = nil network_failure()");
                int baselineEventSubscribers = network.EventSubscriberCount;
                int baselineRequestSubscribers = network.RequestSubscriberCount;
                RbxWorldPackagePayload rejected = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[] { sender, laterFailure });

                RbxWorldLoadResult rejectedResult = worlds.LoadConfirmedAsync(rejected)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(rejectedResult.Success);
                Assert.AreEqual(0, network.RegisterCalls);
                Assert.AreEqual(0, network.SendEventCalls);
                Assert.AreEqual(baselineEventSubscribers, network.EventSubscriberCount);
                Assert.AreEqual(baselineRequestSubscribers, network.RequestSubscriberCount);

                RbxWorldPackagePayload accepted = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[] { sender });
                RbxWorldLoadResult acceptedResult = worlds.LoadConfirmedAsync(accepted)
                    .GetAwaiter().GetResult();
                Assert.IsTrue(acceptedResult.Success, acceptedResult.Error);
                Assert.AreEqual(1, network.RegisterCalls);
                Assert.AreEqual(1, network.SendEventCalls);
                Assert.AreEqual(baselineEventSubscribers, network.EventSubscriberCount);
                Assert.AreEqual(baselineRequestSubscribers, network.RequestSubscriberCount);

                network.ThrowOnRequestSubscriptionAdd = true;
                InstanceRegistry published = host.Registry;
                RbxWorldLoadResult subscriptionRejected = worlds.LoadConfirmedAsync(accepted)
                    .GetAwaiter().GetResult();
                Assert.IsFalse(subscriptionRejected.Success);
                Assert.AreSame(published, host.Registry);
                Assert.IsFalse(published.IsDetached);
                Assert.AreEqual(1, network.RegisterCalls);
                Assert.AreEqual(1, network.SendEventCalls);
                Assert.AreEqual(baselineEventSubscribers, network.EventSubscriberCount);
                Assert.AreEqual(baselineRequestSubscribers, network.RequestSubscriberCount);
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void RejectedStagedLoad_PreservesOutgoingSessionSourcesAndModData()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-reject-" + System.Guid.NewGuid().ToString("N"));
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService service = container.Resolve<IRbxWorldRuntimeService>();
                RbxWorldRuntimeSessionController controller =
                    container.Resolve<RbxWorldRuntimeSessionController>();
                ILuaModRuntime stableRuntime = container.Resolve<ILuaModRuntime>();
                LuaTool.ILuaExecutor stableExecutor = container.Resolve<LuaTool.ILuaExecutor>();
                ILuaModSourceStore stableSources = container.Resolve<ILuaModSourceStore>();
                ILuaModStore modData = container.Resolve<ILuaModStore>();
                InstanceRegistry outgoingRegistry = host.Registry;
                LuaCsLogicSlots stableSlots = container.Resolve<LuaCsLogicSlots>();
                FieldInfo hostField = typeof(RbxWorldRuntimeSessionController).GetField(
                    "_host",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(hostField);
                RbxWorldSessionHostAdapter sessionHost =
                    (RbxWorldSessionHostAdapter)hostField.GetValue(controller);
                stableSlots.DeclareSlot("existing-formula");
                stableSlots.DeclareSlot("failing-formula");
                int overrideFailures = 0;
                stableSlots.OverrideFailed += (_, _, _) => overrideFailures++;
                ActorContext actor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                stableRuntime.LoadMod(
                    actor,
                    "outgoing-formulas",
                    @"logic_define('existing-formula', function() return 7 end)
logic_define('failing-formula', function() error('old-formula-failure') end)",
                    persistToStore: false);
                RbxWorldPackagePayload captured = service.CaptureCurrent();
                RbxWorldPackagePayload rejectedByHost = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[]
                    {
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "host-stage-rejected",
                                Name = "host-stage-rejected",
                                Capabilities = LuaCapabilities.Read.ToString(),
                                Active = false
                            },
                            "return true")
                    });
                sessionHost.BeforePublishForTests = () =>
                    throw new System.InvalidOperationException("injected host rejection");
                RbxWorldLoadResult hostRejected = service.LoadConfirmedAsync(rejectedByHost)
                    .GetAwaiter().GetResult();
                Assert.IsFalse(hostRejected.Success);
                StringAssert.Contains("injected host rejection", hostRejected.Error);
                Assert.AreSame(outgoingRegistry, host.Registry);
                Assert.IsFalse(outgoingRegistry.IsDetached);
                Assert.IsFalse(stableSources.TryLoad("host-stage-rejected", out _, out _));
                sessionHost.BeforePublishForTests = null;
                RbxWorldPackagePayload broken = new(
                    captured.CapturedAtUtc,
                    captured.Settings,
                    captured.Tree,
                    captured.Parts,
                    captured.CameraCFrame,
                    new[]
                    {
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "broken-staged",
                                Name = "broken-staged",
                                Capabilities = LuaCapabilities.All.ToString(),
                                Active = true
                            },
                            @"store_set('written-before-failure', 'bad')
logic_define('existing-formula', function() return 99 end)
logic_define('failing-formula', function() return 101 end)
local after_logic_definition = nil
after_logic_definition()")
                    });

                RbxWorldLoadResult result = service.LoadConfirmedAsync(broken)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.Success);
                StringAssert.Contains("after_logic_definition", result.Error);
                StringAssert.DoesNotContain("not declared", result.Error);
                Assert.AreSame(outgoingRegistry, host.Registry);
                Assert.IsFalse(outgoingRegistry.IsDetached);
                Assert.AreSame(stableRuntime, container.Resolve<ILuaModRuntime>());
                Assert.AreSame(stableExecutor, container.Resolve<LuaTool.ILuaExecutor>());
                Assert.AreSame(stableSources, container.Resolve<ILuaModSourceStore>());
                Assert.AreEqual("", modData.Get("broken-staged", "written-before-failure"));
                Assert.IsFalse(stableSources.TryLoad("broken-staged", out _, out _));
                Assert.IsTrue(stableSlots.TryInvokeNumber(
                    "existing-formula",
                    out double outgoingFormula));
                Assert.AreEqual(7d, outgoingFormula);
                Assert.IsFalse(stableSlots.TryInvokeNumber("failing-formula", out _));
                Assert.AreEqual(1, overrideFailures);
                Assert.IsFalse(stableSlots.TryInvokeNumber("failing-formula", out _));
                Assert.AreEqual(1, overrideFailures);
                LuaTool.LuaResult usable = stableExecutor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'AfterRejected' marker.Parent = workspace",
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.IsTrue(usable.Success, usable.Error);
                Assert.IsNotNull(host.Registry.WorldRoot.FindFirstChild("AfterRejected"));
                Assert.AreSame(controller.CurrentRbxApi.Registry, host.Registry);
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void RejectedStagedCameraMutation_PreservesOutgoingPoseFollowerAndScale()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject cameraObject = new("W33Camera");
            Camera sceneCamera = cameraObject.AddComponent<Camera>();
            GameObject followTarget = new("OutgoingFollowTarget");
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            typeof(RbxWorldHost).GetField(
                    "_camera",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, sceneCamera);
            host.Initialize();
            MemoryLuaScriptVersionStore versions = new();
            versions.SeedOriginal("existing-script", "return 'old'");
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-camera-reject-" + System.Guid.NewGuid().ToString("N"),
                versionStore: versions);
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService service = container.Resolve<IRbxWorldRuntimeService>();
                cameraObject.transform.SetPositionAndRotation(
                    new Vector3(3f, 4f, 5f),
                    Quaternion.Euler(7f, 11f, 13f));
                RbxCameraFollower follower = cameraObject.GetComponent<RbxCameraFollower>();
                follower.Target = followTarget.transform;
                follower.Offset = new Vector3(1f, 2f, 3f);
                follower.enabled = true;
                Vector3 outgoingPosition = cameraObject.transform.position;
                Quaternion outgoingRotation = cameraObject.transform.rotation;
                Vector3 outgoingOffset = follower.Offset;
                float outgoingScale = RbxSpace.MetersPerStud;
                RbxWorldPackagePayload captured = service.CaptureCurrent();
                RbxWorldPackagePayload broken = new(
                    captured.CapturedAtUtc,
                    new RbxWorldSettings
                    {
                        WorldId = "staged-camera-world",
                        MetersPerStud = 0.5f,
                        GravityStudsPerSecondSquared = captured.Settings.GravityStudsPerSecondSquared,
                        SignalBehavior = captured.Settings.SignalBehavior
                    },
                    captured.Tree,
                    captured.Parts,
                    RbxCFrame.FromPosition(100f, 200f, 300f),
                    new[]
                    {
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "a-camera-mutation",
                                Name = "a-camera-mutation",
                                Capabilities = LuaCapabilities.All.ToString(),
                                Active = true
                            },
                            @"local p = Instance.new('Part')
p.Name = 'StagedCameraTarget'
p.Parent = workspace
camera_set_cframe(CFrame.new(20, 30, 40))
camera_follow(p)"),
                        new RbxWorldModSource(
                            new LuaModManifest
                            {
                                Id = "z-camera-failure",
                                Name = "z-camera-failure",
                                Capabilities = LuaCapabilities.All.ToString(),
                                Active = true
                            },
                            "local staged_camera_failure = nil staged_camera_failure()")
                    });

                RbxWorldLoadResult result = service.LoadConfirmedAsync(broken)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(result.Success);
                Assert.AreEqual(outgoingPosition, cameraObject.transform.position);
                Assert.AreEqual(outgoingRotation, cameraObject.transform.rotation);
                Assert.IsTrue(follower.enabled);
                Assert.AreSame(followTarget.transform, follower.Target);
                Assert.AreEqual(outgoingOffset, follower.Offset);
                Assert.AreEqual(outgoingScale, RbxSpace.MetersPerStud);
                Assert.AreEqual(1, versions.GetKnownKeys().Count);
                Assert.AreEqual("existing-script", versions.GetKnownKeys()[0]);
                Assert.IsFalse(versions.TryGetSnapshot(
                    LuaCsModRuntime.VersionKeyPrefix + "a-camera-mutation",
                    out _));

                RbxWorldPackagePayload accepted = new(
                    broken.CapturedAtUtc,
                    broken.Settings,
                    broken.Tree,
                    broken.Parts,
                    broken.CameraCFrame,
                    new[] { broken.Mods[0] });
                RbxWorldLoadResult acceptedResult = service.LoadConfirmedAsync(accepted)
                    .GetAwaiter().GetResult();
                Assert.IsTrue(acceptedResult.Success, acceptedResult.Error);
                Assert.AreEqual(
                    RbxCFrame.FromPosition(20f, 30f, 40f),
                    host.CameraRig.GetCFrame());
                RbxCameraFollower acceptedFollower =
                    cameraObject.GetComponent<RbxCameraFollower>();
                Assert.IsTrue(acceptedFollower.enabled);
                Assert.IsNotNull(acceptedFollower.Target);
                Assert.AreEqual("StagedCameraTarget", acceptedFollower.Target.name);
                Assert.AreEqual(0.5f, RbxSpace.MetersPerStud);
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(followTarget);
            }
        }

        [Test]
        public void PostPublicationRetiredRootDestroyFailure_DoesNotRollBackPublishedSession()
        {
            CoreAiPrefabRegistryAsset registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
            GameObject hostGo = new("RbxWorldHost");
            RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
            host.Initialize();
            ContainerBuilder builder = new();
            RegisterMinimalModStack(
                builder,
                registry,
                modStoreId: "w33-destroy-tail-" + System.Guid.NewGuid().ToString("N"));
            builder.RegisterInstance(host);
            IObjectResolver container = builder.Build();
            try
            {
                IRbxWorldRuntimeService service = container.Resolve<IRbxWorldRuntimeService>();
                RbxWorldLoadResult first = service.LoadConfirmedAsync(service.CaptureCurrent())
                    .GetAwaiter().GetResult();
                Assert.IsTrue(first.Success, first.Error);
                InstanceRegistry outgoing = host.Registry;
                PropertyInfo destroyer = typeof(RbxWorldHost).GetProperty(
                    "RetiredRootDestroyerForTests",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(destroyer);
                destroyer.SetValue(
                    host,
                    new System.Action<UnityEngine.Object>(_ =>
                        throw new System.InvalidOperationException(
                            "injected retired-root destroy failure")));

                RbxWorldLoadResult second = service.LoadConfirmedAsync(service.CaptureCurrent())
                    .GetAwaiter().GetResult();

                Assert.IsTrue(second.Success, second.Error);
                Assert.IsTrue(outgoing.IsDetached);
                Assert.AreNotSame(outgoing, host.Registry);
                Assert.AreSame(
                    host.Registry,
                    container.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi.Registry);
            }
            finally
            {
                container.Dispose();
                Object.DestroyImmediate(registry);
                Object.DestroyImmediate(hostGo);
            }
        }

        [UnityTest]
        public IEnumerator LoadWorldTool_RequiresPlayerConfirmationAndDoesNotApplyPackage()
        {
            return UniTask.ToCoroutine(async () =>
            {
                CoreAiPrefabRegistryAsset registry =
                    ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
                GameObject hostGo = new("RbxWorldHost");
                RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
                host.Initialize();
                ContainerBuilder builder = new();
                RegisterMinimalModStack(
                    builder,
                    registry,
                    modStoreId: "w33-tool-" + System.Guid.NewGuid().ToString("N"));
                builder.RegisterInstance(host);
                IObjectResolver container = builder.Build();
                string savedPath = "";
                try
                {
                    IRbxWorldRuntimeService service =
                        container.Resolve<IRbxWorldRuntimeService>();
                    InstanceRegistry outgoingRegistry = host.Registry;
                    IActorIdentityProvider identity =
                        CoreServicesInstaller.DefaultLocalHostIdentityProvider;
                    ActorContext actor = identity.GetActorContext(
                        BuiltInAgentRoleIds.Programmer);
                    string slot = "w33-tool-" + System.Guid.NewGuid().ToString("N");
                    RbxWorldPackageWriteResult saved =
                        await service.SaveManualAsync(actor, slot);
                    savedPath = saved.Path;
                    Assert.IsTrue(saved.Success, saved.Error);
                    LoadWorldLlmTool tool = new(
                        service,
                        identity,
                        BuiltInAgentRoleIds.Programmer);

                    string response = await tool.ExecuteAsync(slot);
                    JObject json = JObject.Parse(response);

                    Assert.AreEqual("player_confirmation_required", (string)json["status"]);
                    Assert.IsTrue((bool)json["player_confirmation_required"]);
                    Assert.IsNotEmpty((string)json["request_id"]);
                    Assert.AreSame(outgoingRegistry, host.Registry);
                    Assert.IsFalse(outgoingRegistry.IsDetached);
                    RbxWorldLoadResult rejected = await service.ConfirmManualLoadAsync(
                        (string)json["request_id"],
                        false);
                    Assert.IsFalse(rejected.Success);
                    Assert.AreSame(outgoingRegistry, host.Registry);
                }
                finally
                {
                    container.Dispose();
                    if (savedPath.Length > 0 && File.Exists(savedPath))
                    {
                        File.Delete(savedPath);
                    }

                    Object.DestroyImmediate(registry);
                    Object.DestroyImmediate(hostGo);
                }
            });
        }

        [UnityTest]
        public IEnumerator PendingManualLoads_ReplacePerSlotExpireAndEvictOldestSafely()
        {
            return UniTask.ToCoroutine(async () =>
            {
                CoreAiPrefabRegistryAsset registry =
                    ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
                GameObject hostGo = new("RbxWorldHost");
                RbxWorldHost host = hostGo.AddComponent<RbxWorldHost>();
                host.Initialize();
                ContainerBuilder builder = new();
                RegisterMinimalModStack(
                    builder,
                    registry,
                    modStoreId: "w33-pending-" + System.Guid.NewGuid().ToString("N"));
                builder.RegisterInstance(host);
                IObjectResolver container = builder.Build();
                List<string> savedPaths = new();
                try
                {
                    IRbxWorldRuntimeService service =
                        container.Resolve<IRbxWorldRuntimeService>();
                    RbxWorldRuntimeSessionController controller =
                        container.Resolve<RbxWorldRuntimeSessionController>();
                    System.DateTime utcNow = new(
                        2030,
                        1,
                        1,
                        0,
                        0,
                        0,
                        System.DateTimeKind.Utc);
                    controller.ConfigurePendingLoadClockForTests(
                        () => utcNow,
                        System.TimeSpan.FromMinutes(2d));
                    ActorContext actor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                        .GetActorContext(BuiltInAgentRoleIds.Programmer);
                    string prefix = "w33-pending-" + System.Guid.NewGuid().ToString("N");
                    for (int index = 0; index < 9; index++)
                    {
                        RbxWorldPackageWriteResult write = await service.SaveManualAsync(
                            actor,
                            prefix + "-" + index);
                        Assert.IsTrue(write.Success, write.Error);
                        savedPaths.Add(write.Path);
                    }

                    int eventCount = 0;
                    service.ManualLoadConfirmationRequested += _ =>
                        throw new System.InvalidOperationException("host listener failure");
                    service.ManualLoadConfirmationRequested += request =>
                    {
                        eventCount++;
                        Assert.Less(request.RequestedAtUtc, request.ExpiresAtUtc);
                    };
                    RbxWorldLoadRequest replaced = await service.RequestManualLoadAsync(
                        actor,
                        prefix + "-0");
                    utcNow = utcNow.AddSeconds(1d);
                    RbxWorldLoadRequest replacement = await service.RequestManualLoadAsync(
                        actor,
                        prefix + "-0");
                    Assert.AreEqual(1, service.GetPendingManualLoads().Count);
                    Assert.AreEqual(
                        replacement.RequestId,
                        service.GetPendingManualLoads()[0].RequestId);
                    RbxWorldLoadResult replacedResult = await service.ConfirmManualLoadAsync(
                        replaced.RequestId,
                        true);
                    Assert.IsFalse(replacedResult.Success);

                    for (int index = 1; index < 9; index++)
                    {
                        utcNow = utcNow.AddSeconds(1d);
                        await service.RequestManualLoadAsync(actor, prefix + "-" + index);
                    }

                    IReadOnlyList<RbxPendingWorldLoadRequest> bounded =
                        service.GetPendingManualLoads();
                    Assert.AreEqual(8, bounded.Count);
                    RbxWorldLoadResult evictedResult = await service.ConfirmManualLoadAsync(
                        replacement.RequestId,
                        true);
                    Assert.IsFalse(evictedResult.Success);
                    Assert.AreEqual(10, eventCount);

                    string expiredRequest = bounded[bounded.Count - 1].RequestId;
                    utcNow = utcNow.AddMinutes(3d);
                    Assert.AreEqual(0, service.GetPendingManualLoads().Count);
                    RbxWorldLoadResult expired = await service.ConfirmManualLoadAsync(
                        expiredRequest,
                        true);
                    Assert.IsFalse(expired.Success);
                    StringAssert.Contains("expired", expired.Error);
                    Assert.AreSame(host.Registry, controller.CurrentRbxApi.Registry);
                }
                finally
                {
                    container.Dispose();
                    for (int index = 0; index < savedPaths.Count; index++)
                    {
                        if (File.Exists(savedPaths[index]))
                        {
                            File.Delete(savedPaths[index]);
                        }
                    }

                    Object.DestroyImmediate(registry);
                    Object.DestroyImmediate(hostGo);
                }
            });
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
            int? worldAclVersion = InstanceRegistry.CurrentWorldAclVersion,
            string modStoreId = null,
            ILuaScriptVersionStore versionStore = null,
            ILuaModSourceStore worldSessionSourceStore = null,
            INetworkBridge networkBridge = null)
        {
            builder.RegisterInstance<IGameLogger>(GameLoggerUnscopedFallback.Instance);
            // WHY: an explicit recorder, not the ambient Log.Instance — the factory's diagnostics are then
            // assertable regardless of what the rest of the run did to the global logger and log filter.
            builder.RegisterInstance<ILog>(_log);
            builder.Register<NoopSink>(Lifetime.Singleton).As<IAiGameCommandSink>();
            if (versionStore != null)
            {
                builder.RegisterInstance(versionStore).As<ILuaScriptVersionStore>();
            }
            else
            {
                builder.Register<NullLuaScriptVersionStore>(Lifetime.Singleton)
                    .As<ILuaScriptVersionStore>();
            }
            builder.Register<NullDataOverlayVersionStore>(Lifetime.Singleton).As<IDataOverlayVersionStore>();
            builder.Register<AgentMemoryPolicy>(Lifetime.Singleton);
            builder.RegisterInstance<ICoreAISettings>(_settings);
            builder.Register(_ => new LuaGenerationRateLimiter(), Lifetime.Singleton);
            if (networkBridge != null)
            {
                builder.RegisterInstance(networkBridge).As<INetworkBridge>();
            }

            builder.RegisterWorldCommands(registry);
            builder.RegisterCoreAiMods(
                worldAclVersion: worldAclVersion,
                modStoreId: modStoreId,
                worldSessionSourceStore: worldSessionSourceStore);
        }

        private sealed class NoopSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class TransactionalMemorySourceStore :
            ILuaModSourceStore,
            IRbxWorldModSourceStore
        {
            private readonly Dictionary<string, RbxWorldModSource> _mods =
                new(System.StringComparer.Ordinal);

            public int SaveCalls { get; private set; }

            public int PrepareCalls { get; private set; }

            public int ActivateCalls { get; private set; }

            public int RollbackCalls { get; private set; }

            public int CompleteCalls { get; private set; }

            public int ReplacementDisposeCalls { get; private set; }

            public bool ThrowOnActivate { get; set; }

            public bool ThrowOnComplete { get; set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                string key = id?.Trim() ?? "";
                LuaModManifest stored = CloneManifest(manifest);
                stored.Id = key;
                _mods[key] = new RbxWorldModSource(stored, source ?? "");
                SaveCalls++;
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                string key = id?.Trim() ?? "";
                if (_mods.TryGetValue(key, out RbxWorldModSource stored))
                {
                    source = stored.Source;
                    manifest = CloneManifest(stored.Manifest);
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new(_mods.Count);
                foreach (RbxWorldModSource stored in _mods.Values)
                {
                    manifests.Add(CloneManifest(stored.Manifest));
                }

                manifests.Sort((left, right) =>
                    string.CompareOrdinal(left.Id, right.Id));
                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                string key = id?.Trim() ?? "";
                if (_mods.TryGetValue(key, out RbxWorldModSource stored))
                {
                    LuaModManifest manifest = CloneManifest(stored.Manifest);
                    manifest.Active = active;
                    _mods[key] = new RbxWorldModSource(manifest, stored.Source);
                }
            }

            public void Delete(string id)
            {
                _mods.Remove(id?.Trim() ?? "");
            }

            public Cysharp.Threading.Tasks.UniTask<IRbxWorldModSourceReplacement>
                PrepareExactReplacementAsync(
                    IReadOnlyList<RbxWorldModSource> mods,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransactionalMemorySourceStore staged = new();
                for (int index = 0; index < mods.Count; index++)
                {
                    RbxWorldModSource mod = mods[index];
                    staged.Save(mod.Manifest.Id, mod.Source, mod.Manifest);
                }

                PrepareCalls++;
                return Cysharp.Threading.Tasks.UniTask.FromResult<IRbxWorldModSourceReplacement>(
                    new MemoryReplacement(this, staged));
            }

            private static LuaModManifest CloneManifest(LuaModManifest manifest)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<LuaModManifest>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(manifest));
            }

            private sealed class MemoryReplacement : IRbxWorldModSourceReplacement
            {
                private readonly TransactionalMemorySourceStore _owner;

                public MemoryReplacement(
                    TransactionalMemorySourceStore owner,
                    ILuaModSourceStore sourceStore)
                {
                    _owner = owner;
                    SourceStore = sourceStore;
                }

                public ILuaModSourceStore SourceStore { get; }

                public void Activate()
                {
                    _owner.ActivateCalls++;
                    if (_owner.ThrowOnActivate)
                    {
                        throw new System.InvalidOperationException(
                            "injected source activation failure");
                    }

                    TransactionalMemorySourceStore staged =
                        (TransactionalMemorySourceStore)SourceStore;
                    _owner._mods.Clear();
                    foreach (KeyValuePair<string, RbxWorldModSource> entry in staged._mods)
                    {
                        _owner._mods.Add(
                            entry.Key,
                            new RbxWorldModSource(
                                CloneManifest(entry.Value.Manifest),
                                entry.Value.Source));
                    }
                }

                public Cysharp.Threading.Tasks.UniTask CompleteAsync(
                    CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _owner.CompleteCalls++;
                    if (_owner.ThrowOnComplete)
                    {
                        throw new System.InvalidOperationException(
                            "injected source completion failure");
                    }

                    return Cysharp.Threading.Tasks.UniTask.CompletedTask;
                }

                public Cysharp.Threading.Tasks.UniTask RollbackAsync(
                    CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _owner.RollbackCalls++;
                    return Cysharp.Threading.Tasks.UniTask.CompletedTask;
                }

                public void Dispose()
                {
                    _owner.ReplacementDisposeCalls++;
                }
            }
        }

        private sealed class RecordingNetworkBridge : INetworkBridge
        {
            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event System.Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            private System.Action<RbxNetworkEventMessage> _eventReceived;
            private System.Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> _requestReceived;

            public RbxNetworkTopology Topology => RbxNetworkTopology.Host;

            private readonly List<string> _actorIds = new();

            public IReadOnlyList<string> ActorIds => _actorIds;

            public int EventSubscriberCount { get; private set; }

            public int RequestSubscriberCount { get; private set; }

            public int RegisterCalls { get; private set; }

            public int SendEventCalls { get; private set; }

            public bool ThrowOnRequestSubscriptionAdd { get; set; }

            public event System.Action<RbxNetworkEventMessage> EventReceived
            {
                add
                {
                    _eventReceived += value;
                    EventSubscriberCount++;
                }
                remove
                {
                    _eventReceived -= value;
                    EventSubscriberCount--;
                }
            }

            public event System.Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder>
                RequestReceived
            {
                add
                {
                    if (ThrowOnRequestSubscriptionAdd)
                    {
                        throw new System.InvalidOperationException(
                            "injected request subscription failure");
                    }

                    _requestReceived += value;
                    RequestSubscriberCount++;
                }
                remove
                {
                    _requestReceived -= value;
                    RequestSubscriberCount--;
                }
            }

            public void RegisterActor(string actorId)
            {
                RegisterCalls++;
                if (!_actorIds.Contains(actorId))
                {
                    _actorIds.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actorIds.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
                SendEventCalls++;
            }

            public void SendRequest(
                RbxNetworkRequestMessage message,
                System.Action<RbxNetworkResponse> response)
            {
            }

            public void EmitEvent(RbxNetworkEventMessage message)
            {
                _eventReceived?.Invoke(message);
            }
        }
    }
}
