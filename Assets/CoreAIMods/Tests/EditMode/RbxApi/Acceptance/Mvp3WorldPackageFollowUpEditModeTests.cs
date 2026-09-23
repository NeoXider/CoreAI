using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>MVP3 follow-up: dangling PrimaryPart, autosave load, pre-load safety, reserved names.</summary>
    [TestFixture]
    public sealed class Mvp3WorldPackageFollowUpEditModeTests
    {
        private const string WorldId = "mvp3-followup";

        private static readonly DateTime CapturedAtUtc =
            new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        private readonly List<RbxDataModel> _games = new();
        private readonly List<string> _temporaryDirectories = new();
        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (RbxDataModel game in _games)
            {
                if (game != null && !game.IsDestroyed)
                {
                    game.Destroy();
                }
            }

            foreach (string directory in _temporaryDirectories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void Capture_DanglingPrimaryPart_DropsInSnapshotAndEmitsDiagnostic_LiveTreeUntouched()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxModel durableModel = (RbxModel)registry.Create("Model");
            durableModel.Name = "DurableModel";
            durableModel.Parent = registry.WorldRoot;
            RbxInstance ephemeralPart = registry.Create(
                "Part",
                "active-builder",
                OriginTag.FromMod("active-builder"));
            ephemeralPart.Name = "EphemeralPrimaryPart";
            ephemeralPart.Parent = durableModel;
            durableModel.SetPrimaryPart(ephemeralPart);
            PartProperties properties = PartProperties.CreateDefault();
            partSink.SetPartProperties(ephemeralPart.Id, in properties);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    partSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));

            InstanceSnapshot capturedModel = FindNode(payload, durableModel.Id.Value);
            Assert.AreEqual(0UL, capturedModel.Model.PrimaryPartId);
            Assert.AreEqual(1, payload.Diagnostics.Count);
            Assert.AreEqual(durableModel.Id.Value, payload.Diagnostics[0].ModelId);
            Assert.AreEqual(ephemeralPart.Id.Value, payload.Diagnostics[0].DroppedPrimaryPartId);
            StringAssert.Contains("mod-ephemeral", payload.Diagnostics[0].Reason);
            Assert.IsNotNull(durableModel.PrimaryPart);
            Assert.AreEqual(ephemeralPart.Id, durableModel.PrimaryPart.Id);

            byte[] bytes = RbxWorldPackageSerializer.WritePackage(payload);
            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(bytes);
            Assert.AreEqual(1, decoded.Diagnostics.Count);
            Assert.AreEqual(payload.Diagnostics[0].ModelId, decoded.Diagnostics[0].ModelId);
            Assert.AreEqual(payload.Diagnostics[0].DroppedPrimaryPartId, decoded.Diagnostics[0].DroppedPrimaryPartId);

            RbxWorldPackagePayload second = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    partSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc.AddSeconds(1d)));
            Assert.AreEqual(1, second.Diagnostics.Count);

            byte[] oldBytes = CreateOldPackageWithoutDiagnostics(CapturedAtUtc);
            RbxWorldPackagePayload oldPayload = RbxWorldPackageSerializer.ReadPackage(oldBytes);
            Assert.AreEqual(0, oldPayload.Diagnostics.Count);
        }

        [Test]
        public void Capture_DanglingMissingPrimaryPart_DropsAndDiagnosticsReasonMissing()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxModel durableModel = (RbxModel)registry.Create("Model");
            durableModel.Name = "DurableModel";
            durableModel.Parent = registry.WorldRoot;
            RbxInstance durablePart = registry.Create("Part");
            durablePart.Name = "DurablePart";
            durablePart.Parent = durableModel;
            PartProperties props = PartProperties.CreateDefault();
            partSink.SetPartProperties(durablePart.Id, in props);
            durableModel.SetPrimaryPart(durablePart);
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(game);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.Id == durableModel.Id.Value)
                {
                    node.Model.PrimaryPartId = 999999UL;
                    break;
                }
            }

            RbxWorldPackagePayload payload = BuildPayloadFromSnapshot(
                snapshot,
                registry,
                partSink,
                CapturedAtUtc);
            InstanceSnapshot capturedModel = FindNode(payload, durableModel.Id.Value);
            Assert.AreEqual(0UL, capturedModel.Model.PrimaryPartId);
            Assert.AreEqual(1, payload.Diagnostics.Count);
            StringAssert.Contains("missing", payload.Diagnostics[0].Reason);
        }

        [Test]
        public async Task Capture_WithGatedExecutor_NextExecuteLuaNotBlockedByDanglingPrimaryPart()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxModel durableModel = (RbxModel)registry.Create("Model");
            durableModel.Name = "DurableModel";
            durableModel.Parent = registry.WorldRoot;
            RbxInstance ephemeralPart = registry.Create(
                "Part",
                "active-builder",
                OriginTag.FromMod("active-builder"));
            ephemeralPart.Name = "EphemeralPrimaryPart";
            ephemeralPart.Parent = durableModel;
            durableModel.SetPrimaryPart(ephemeralPart);
            PartProperties props = PartProperties.CreateDefault();
            partSink.SetPartProperties(ephemeralPart.Id, in props);

            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(
                    RbxWorldPackageSerializer.Capture(
                        new RbxWorldPackageCaptureContext(
                            registry,
                            game,
                            partSink,
                            NewSettings(),
                            capturedAtUtc: CapturedAtUtc))),
                store);
            int executions = 0;
            RbxWorldPackagePayload captureBefore = await gate.ExecuteAsync(
                "execute_lua",
                async cancellationToken =>
                {
                    executions++;
                    return RbxWorldPackageSerializer.Capture(
                        new RbxWorldPackageCaptureContext(
                            registry,
                            game,
                            partSink,
                            NewSettings(),
                            capturedAtUtc: CapturedAtUtc));
                },
                CancellationToken.None);

            Assert.AreEqual(1, executions);
            Assert.IsNotNull(captureBefore);
        }

        [Test]
        public void ValidateName_ReservedWindowsNames_AreRejected()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            string[] reserved =
            {
                "CON", "con", "PRN", "AUX", "NUL",
                "COM1", "com9", "LPT1", "LPT9",
                "CON.txt", "prn.lua", "AUX.world", "nul.bin",
                "COM1.txt", "LPT9.dat"
            };

            foreach (string name in reserved)
            {
                try
                {
                    store.CreateManualAsync(name, payload).GetAwaiter().GetResult();
                    Assert.Fail("Reserved name '" + name + "' was not rejected.");
                }
                catch (ArgumentException ex)
                {
                    StringAssert.Contains("reserved device name", ex.Message.ToLowerInvariant());
                }
            }

            RbxWorldPackageWriteResult ok = store.CreateManualAsync("valid-slot_1", payload).GetAwaiter().GetResult();
            Assert.IsTrue(ok.Success);
        }

        [Test]
        public async Task ListAutoSaves_ReturnsMetadata_And_RequestAutoLoadRequiresConfirmation()
        {
            string root = NewTemporaryDirectory();
            DateTime fixedNow = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => fixedNow);
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            RbxWorldPackageWriteResult autosave = await fileStore.CreateAutoAsync("execute_lua", payload);
            Assert.IsTrue(autosave.Success);
            IReadOnlyList<RbxAutoSaveInfo> infos = fileStore.ListAutoSaves();
            Assert.AreEqual(1, infos.Count);
            Assert.IsTrue(infos[0].FileName.EndsWith(".world", StringComparison.Ordinal));
            Assert.AreEqual("20260902T120000000Z-0000-execute_lua.world", infos[0].FileName);
            Assert.AreEqual(Path.GetFileName(autosave.Path), infos[0].FileName);
            Assert.AreEqual("execute_lua", infos[0].Trigger);
            Assert.IsTrue(infos[0].SizeBytes > 0L);
            Assert.AreEqual(new FileInfo(autosave.Path).Length, infos[0].SizeBytes);
            Assert.AreEqual(fixedNow, infos[0].TimestampUtc);
            Assert.AreEqual(DateTimeKind.Utc, infos[0].TimestampUtc.Kind);

            InMemoryAutosaveStore stubStore = new(payload, infos[0]);
            HeadlessRbxWorldSessionHost host = CreateHeadlessHost();
            DelegateModSourceStore sourceStore = new();
            RbxWorldRuntimeSessionController controller = CreateController(host, stubStore, sourceStore);
            LocalActorIdentityProvider identity = new("autosave-actor");
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            RbxWorldLoadRequest request = await controller.RequestAutoLoadAsync(actor, infos[0].FileName, CancellationToken.None);
            Assert.IsTrue(request.PlayerConfirmationRequired);
            Assert.IsFalse(string.IsNullOrWhiteSpace(request.RequestId));
            IReadOnlyList<RbxAutoSaveInfo> listFromService = controller.ListAutoSaves();
            Assert.AreEqual(1, listFromService.Count);
            Assert.AreEqual(infos[0].FileName, listFromService[0].FileName);

            RbxWorldLoadResult rejected = await controller.ConfirmManualLoadAsync(request.RequestId, false, CancellationToken.None);
            Assert.IsFalse(rejected.Success);
            Assert.AreEqual(0, controller.GetPendingManualLoads().Count);

            RbxWorldLoadRequest request2 = await controller.RequestAutoLoadAsync(actor, infos[0].FileName, CancellationToken.None);
            Assert.AreNotEqual(request.RequestId, request2.RequestId, "A consumed request id is never reissued.");
            IReadOnlyList<RbxPendingWorldLoadRequest> pending = controller.GetPendingManualLoads();
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(request2.RequestId, pending[0].RequestId);
            Assert.AreEqual(infos[0].FileName, pending[0].Slot);
            RbxWorldLoadResult expired = await controller.ConfirmManualLoadAsync("unknown-id", true, CancellationToken.None);
            Assert.IsFalse(expired.Success);
            Assert.AreEqual(0, expired.ActiveModsStarted);
            Assert.AreEqual(
                1,
                controller.GetPendingManualLoads().Count,
                "An unknown id must neither consume nor evict another pending request.");
            Assert.AreEqual(request2.RequestId, controller.GetPendingManualLoads()[0].RequestId);
            Assert.AreSame(
                host.Registry,
                controller.CurrentRbxApi.Registry,
                "Without a player confirmation the live world must not be swapped.");
        }

        [Test]
        public async Task LoadConfirmedAsync_WritesSafetyAutosave_BeforeSwap_OnFailureLiveWorldUntouched()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore durableStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
            RbxWorldPackagePayload firstPayload = CreateMinimalPayload(CapturedAtUtc);
            HeadlessRbxWorldSessionHost host = CreateHeadlessHost();
            FailingAutoStore failingStore = new(durableStore, true);
            DelegateModSourceStore sourceStore = new();
            RbxWorldRuntimeSessionController controller = CreateController(host, failingStore, sourceStore);
            RbxWorldPackagePayload nextPayload = CreateMinimalPayload(CapturedAtUtc.AddSeconds(10d));
            RbxWorldLoadResult failed = await controller.LoadConfirmedAsync(nextPayload, CancellationToken.None);
            Assert.IsFalse(failed.Success);
            StringAssert.Contains("load_world-pre", failed.Error.ToLowerInvariant());
            Assert.AreEqual(1, failingStore.CreateAutoAttempts);
            Assert.AreEqual("load_world-pre", failingStore.LastTrigger);

            FailingAutoStore successStore = new(durableStore, false);
            RbxWorldRuntimeSessionController controller2 = CreateController(host, successStore, sourceStore);
            RbxWorldLoadResult success = await controller2.LoadConfirmedAsync(nextPayload, CancellationToken.None);
            if (!success.Success)
            {
                Assert.Fail("Expected success but got: " + success.Error);
            }

            Assert.IsTrue(success.Success);
            Assert.AreEqual("load_world-pre", successStore.LastTrigger);
        }

        private HeadlessRbxWorldSessionHost CreateHeadlessHost()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            RbxWorldSettings settings = NewSettings();
            return new HeadlessRbxWorldSessionHost(registry, game, settings);
        }

        private RbxWorldRuntimeSessionController CreateController(
            HeadlessRbxWorldSessionHost host,
            IRbxWorldPackageStore packageStore,
            DelegateModSourceStore sourceStore)
        {
            IRbxWorldPackageStore storeForController = packageStore;
            LuaCsRbxApiBindings initialRbxApi = new(host.Registry, host.Game);
            LuaCsModStack initialStack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new Mvp1AcceptanceNullLogger(),
                ModStore = new Mvp1AcceptanceMemoryStore(),
                ModSourceStore = sourceStore,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = initialRbxApi
            });

            return new RbxWorldRuntimeSessionController(
                host,
                storeForController,
                sourceStore,
                initialStack,
                initialRbxApi,
                (candidate, network) => new LuaCsRbxApiBindings(
                    candidate.Registry,
                    candidate.Game,
                    partSink: candidate.PartSink,
                    cameraRig: candidate.CameraRig),
                (rbxApi, srcStore, modStore, verStore) => LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new Mvp1AcceptanceNullLogger(),
                    ModStore = modStore,
                    ModSourceStore = srcStore,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = rbxApi
                }),
                (stack, rbxApi) => { },
                new NullNetworkBridge(),
                LuaCapabilities.All,
                false,
                new Mvp1AcceptanceMemoryStore(),
                new MemoryLuaScriptVersionStore());
        }

        private RbxWorldPackagePayload CreateMinimalPayload(DateTime capturedAtUtc)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            return RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: capturedAtUtc));
        }

        private static RbxWorldSettings NewSettings()
        {
            return new RbxWorldSettings
            {
                WorldId = WorldId,
                MetersPerStud = 0.35f,
                GravityStudsPerSecondSquared = 144.5d,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            };
        }

        private static InstanceSnapshot FindNode(RbxWorldPackagePayload payload, ulong id)
        {
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.Id == id)
                {
                    return node;
                }
            }

            return null;
        }

        private string NewTemporaryDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "CoreAI-Mvp3FollowUp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _temporaryDirectories.Add(directory);
            return directory;
        }

        private static byte[] CreateOldPackageWithoutDiagnostics(DateTime capturedAtUtc)
        {
            RbxWorldPackagePayload payload = CreateStaticMinimalPayload(capturedAtUtc);
            byte[] bytes = RbxWorldPackageSerializer.WritePackage(payload);
            return bytes;
        }

        private static RbxWorldPackagePayload CreateStaticMinimalPayload(DateTime capturedAtUtc)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            return RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: capturedAtUtc));
        }

        private static RbxWorldPackagePayload BuildPayloadFromSnapshot(
            InstanceTreeSnapshot snapshot,
            InstanceRegistry registry,
            IPartPropertySink partSink,
            DateTime capturedAtUtc)
        {
            InstanceTreeSnapshot projected = new()
            {
                WorldAclVersion = snapshot.WorldAclVersion
            };
            HashSet<ulong> excluded = new();
            HashSet<ulong> retained = new();
            List<RbxWorldPackageDiagnostic> diagnostics = new();
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                bool excludedByParent = node.ParentId != 0UL && excluded.Contains(node.ParentId);
                bool runtimeInfrastructure = registry.TryGetRecord(
                    new InstanceId(node.Id), out InstanceRecord record)
                    && record.IsRuntimeInfrastructure;
                if (node.OwnerModId != null || runtimeInfrastructure || excludedByParent)
                {
                    excluded.Add(node.Id);
                    continue;
                }

                if (node.ParentId != 0UL && !retained.Contains(node.ParentId))
                {
                    throw new RbxWorldPackageException("missing parent");
                }

                projected.Instances.Add(node);
                retained.Add(node.Id);
            }

            foreach (InstanceSnapshot node in projected.Instances)
            {
                if (node.Model != null && node.Model.PrimaryPartId != 0UL && !retained.Contains(node.Model.PrimaryPartId))
                {
                    string classification = excluded.Contains(node.Model.PrimaryPartId) ? "mod-ephemeral" : "missing";
                    diagnostics.Add(new RbxWorldPackageDiagnostic(node.Id, node.Model.PrimaryPartId, classification));
                    node.Model.PrimaryPartId = 0UL;
                }
            }

            Dictionary<InstanceId, PartProperties> parts = new();
            foreach (InstanceSnapshot node in projected.Instances)
            {
                if (registry.Catalog.IsA(node.ClassName, "BasePart"))
                {
                    if (partSink.TryGetPartProperties(new InstanceId(node.Id), out PartProperties props))
                    {
                        parts.Add(new InstanceId(node.Id), props);
                    }
                }
            }

            return new RbxWorldPackagePayload(
                capturedAtUtc,
                NewSettings(),
                projected,
                parts,
                null,
                Array.Empty<RbxWorldModSource>(),
                diagnostics);
        }

        private sealed class DelegateModSourceStore : ILuaModSourceStore, IRbxWorldModSourceStore
        {
            private readonly Dictionary<string, RbxWorldModSource> _sources = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _sources[id] = new RbxWorldModSource(manifest, source);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_sources.TryGetValue(id, out RbxWorldModSource entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> result = new();
                foreach (RbxWorldModSource entry in _sources.Values)
                {
                    result.Add(entry.Manifest);
                }

                return result;
            }

            public void SetActive(string id, bool active)
            {
            }

            public void Delete(string id)
            {
                _sources.Remove(id);
            }

            public UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
                IReadOnlyList<RbxWorldModSource> mods,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult<IRbxWorldModSourceReplacement>(new NoopReplacement(this));
            }

            private sealed class NoopReplacement : IRbxWorldModSourceReplacement
            {
                public NoopReplacement(DelegateModSourceStore owner)
                {
                    SourceStore = owner;
                }

                public ILuaModSourceStore SourceStore { get; }

                public void Activate()
                {
                }

                public UniTask CompleteAsync(CancellationToken cancellationToken = default)
                {
                    return UniTask.CompletedTask;
                }

                public void Dispose()
                {
                }

                public UniTask RollbackAsync(CancellationToken cancellationToken = default)
                {
                    return UniTask.CompletedTask;
                }
            }
        }

        private sealed class FailingAutoStore : IRbxWorldPackageStore
        {
            private readonly IRbxWorldPackageStore _inner;
            private readonly bool _shouldFail;

            public FailingAutoStore(IRbxWorldPackageStore inner, bool shouldFail)
            {
                _inner = inner;
                _shouldFail = shouldFail;
            }

            public int CreateAutoAttempts { get; private set; }

            public string LastTrigger { get; private set; } = "";

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(string slot, RbxWorldPackagePayload payload, CancellationToken cancellationToken = default)
            {
                return _inner.CreateManualAsync(slot, payload, cancellationToken);
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(string trigger, RbxWorldPackagePayload payload, CancellationToken cancellationToken = default)
            {
                CreateAutoAttempts++;
                LastTrigger = trigger;
                if (_shouldFail)
                {
                    return UniTask.FromResult(new RbxWorldPackageWriteResult(false, "", "Injected durability refusal."));
                }

                return _inner.CreateAutoAsync(trigger, payload, cancellationToken);
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot, CancellationToken cancellationToken = default)
            {
                return _inner.LoadManualAsync(slot, cancellationToken);
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName, CancellationToken cancellationToken = default)
            {
                return _inner.LoadAutoAsync(fileName, cancellationToken);
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return _inner.ListManualSlots();
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                return _inner.ListAutoFiles();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return _inner.ListAutoSaves();
            }
        }

        private sealed class InMemoryAutosaveStore : IRbxWorldPackageStore
        {
            private readonly RbxWorldPackagePayload _payload;
            private readonly RbxAutoSaveInfo _info;

            public InMemoryAutosaveStore(RbxWorldPackagePayload payload, RbxAutoSaveInfo info)
            {
                _payload = payload;
                _info = info;
            }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(string slot, RbxWorldPackagePayload payload, CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(false, "", "not used"));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(string trigger, RbxWorldPackagePayload payload, CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, trigger + ".world", ""));
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName, CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(_payload);
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                return new[] { _info.FileName };
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return new[] { _info };
            }
        }

        // ==================== World tools refuse an invalid name before the service ====================

        private const string OverlongSlot =
            "abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>Counts service calls; every call is a test failure when the tool should have refused first.</summary>
        private sealed class RecordingRuntimeService : IRbxWorldRuntimeService
        {
            public List<string> SavedSlots { get; } = new();

            public List<string> RequestedSlots { get; } = new();

            public List<string> RequestedAutoFiles { get; } = new();

            public int Calls => SavedSlots.Count + RequestedSlots.Count + RequestedAutoFiles.Count;

            /// <summary>When set, <see cref="RequestAutoLoadAsync"/> completes faulted with this exception.</summary>
            public Exception AutoLoadFault { get; set; }

            public event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested
            {
                add { }
                remove { }
            }

            public RbxWorldPackagePayload CaptureCurrent()
            {
                return null;
            }

            public IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads()
            {
                return Array.Empty<RbxPendingWorldLoadRequest>();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }

            public UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                SavedSlots.Add(slot);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, slot + ".world", ""));
            }

            public UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                RequestedSlots.Add(slot);
                return UniTask.FromResult(new RbxWorldLoadRequest(
                    "request-" + RequestedSlots.Count, slot, "world", CapturedAtUtc, CapturedAtUtc.AddMinutes(1d)));
            }

            public UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(
                ActorContext caller,
                string autoFileName,
                CancellationToken cancellationToken = default)
            {
                RequestedAutoFiles.Add(autoFileName);
                if (AutoLoadFault != null)
                {
                    return UniTask.FromException<RbxWorldLoadRequest>(AutoLoadFault);
                }

                return UniTask.FromResult(new RbxWorldLoadRequest(
                    "request-" + RequestedAutoFiles.Count, autoFileName, "world", CapturedAtUtc,
                    CapturedAtUtc.AddMinutes(1d)));
            }

            public UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(
                string requestId,
                bool playerConfirmed,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private static IActorIdentityProvider ToolIdentity()
        {
            return new LocalActorIdentityProvider("world-tool-actor");
        }

        /// <summary>
        /// The store validates the slot by THROWING, from inside the tool body; the tool used to pass a
        /// blank or malformed slot straight through, so the model got an exception crossing the invocation
        /// boundary (traced as possibly executed, retries suppressed). Now the tool refuses first.
        /// </summary>
        [TestCase((string)null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("a/b")]
        [TestCase("..\\up")]
        [TestCase("CON")]
        [TestCase(OverlongSlot)]
        public async Task SaveWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService(string slot)
        {
            RecordingRuntimeService service = new();
            SaveWorldLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(slot));

            Assert.IsFalse((bool)json["success"]);
            Assert.AreEqual("", (string)json["path"]);
            StringAssert.Contains("Parameter 'slot' is invalid", (string)json["error"]);
            StringAssert.Contains("manual slot", (string)json["error"]);
            StringAssert.Contains("NOT executed", (string)json["error"]);
            Assert.AreEqual(0, service.Calls, "an invalid slot must never reach the service");
        }

        [TestCase((string)null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("a/b")]
        [TestCase("..\\up")]
        [TestCase("CON")]
        [TestCase(OverlongSlot)]
        public async Task LoadWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService(string slot)
        {
            RecordingRuntimeService service = new();
            LoadWorldLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(slot));

            Assert.IsFalse((bool)json["success"]);
            Assert.AreEqual("invalid_argument", (string)json["status"]);
            Assert.IsFalse((bool)json["player_confirmation_required"]);
            Assert.AreEqual("", (string)json["request_id"]);
            Assert.AreEqual(slot ?? "", (string)json["slot"], "The refused slot is echoed verbatim.");
            Assert.AreEqual("", (string)json["world_id"]);
            StringAssert.Contains("Parameter 'slot' is invalid", (string)json["error"]);
            StringAssert.Contains("NOT executed", (string)json["error"]);
            Assert.AreEqual(0, service.Calls, "an invalid slot must never reach the service");
        }

        [TestCase((string)null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("nested/save.world")]
        [TestCase("save.txt")]
        [TestCase("save")]
        public async Task LoadAutoSave_InvalidName_IsRefusedAsResult_WithoutCallingService(string name)
        {
            RecordingRuntimeService service = new();
            LoadAutoSaveLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(name));

            Assert.IsFalse((bool)json["success"]);
            Assert.AreEqual("invalid_argument", (string)json["status"]);
            Assert.IsFalse((bool)json["player_confirmation_required"]);
            Assert.AreEqual("", (string)json["request_id"]);
            Assert.AreEqual(name ?? "", (string)json["slot"], "The refused name is echoed verbatim.");
            Assert.AreEqual("", (string)json["world_id"]);
            StringAssert.Contains("Parameter 'name' is invalid", (string)json["error"]);
            StringAssert.Contains(".world file name without a path", (string)json["error"]);
            StringAssert.Contains("NOT executed", (string)json["error"]);
            Assert.AreEqual(0, service.Calls, "an invalid autosave name must never reach the service");
        }

        /// <summary>The check is the store's own rule, no stricter: a valid name still reaches the service.</summary>
        [Test]
        public async Task WorldTools_ValidNames_ReachTheService()
        {
            RecordingRuntimeService service = new();
            IActorIdentityProvider identity = ToolIdentity();

            JObject saved = JObject.Parse(await new SaveWorldLlmTool(service, identity, BuiltInAgentRoleIds.Programmer)
                .ExecuteAsync("valid-slot_1"));
            JObject load = JObject.Parse(await new LoadWorldLlmTool(service, identity, BuiltInAgentRoleIds.Programmer)
                .ExecuteAsync("valid-slot_1"));
            JObject auto = JObject.Parse(await new LoadAutoSaveLlmTool(service, identity, BuiltInAgentRoleIds.Programmer)
                .ExecuteAsync("20260902T120000000Z-0000-execute_lua.world"));

            Assert.IsTrue((bool)saved["success"]);
            Assert.AreEqual("player_confirmation_required", (string)load["status"]);
            Assert.AreEqual("player_confirmation_required", (string)auto["status"]);
            CollectionAssert.AreEqual(new[] { "valid-slot_1" }, service.SavedSlots);
            CollectionAssert.AreEqual(new[] { "valid-slot_1" }, service.RequestedSlots);
            CollectionAssert.AreEqual(new[] { "20260902T120000000Z-0000-execute_lua.world" }, service.RequestedAutoFiles);
        }

        /// <summary>The store keeps throwing the same messages for callers that bypass the tools.</summary>
        [Test]
        public void Store_StillThrowsSameMessages_ForInvalidNames()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);

            ArgumentException blank = Assert.Throws<ArgumentException>(
                () => store.CreateManualAsync("   ", payload).GetAwaiter().GetResult());
            StringAssert.Contains("manual slot must contain 1-64 characters.", blank.Message);

            ArgumentException path = Assert.Throws<ArgumentException>(
                () => store.CreateManualAsync("a/b", payload).GetAwaiter().GetResult());
            StringAssert.Contains("manual slot may contain only letters, digits, '-' and '_'.", path.Message);

            ArgumentException reserved = Assert.Throws<ArgumentException>(
                () => store.LoadManualAsync("CON").GetAwaiter().GetResult());
            StringAssert.Contains("manual slot 'CON' is a reserved device name.", reserved.Message);

            ArgumentException extension = Assert.Throws<ArgumentException>(
                () => store.LoadAutoAsync("save.txt").GetAwaiter().GetResult());
            StringAssert.Contains("Auto package name must be one .world file name without a path.", extension.Message);
        }

        [Test]
        public void PackageNames_TrimAndNameTheField()
        {
            Assert.IsTrue(RbxWorldPackageNames.TryValidateManualSlot("  slot-1 ", "manual slot",
                out string normalized, out string error), error);
            Assert.AreEqual("slot-1", normalized);

            Assert.IsFalse(RbxWorldPackageNames.TryValidateManualSlot("lpt1.world", "backup slot",
                out normalized, out error));
            Assert.IsNull(normalized);
            Assert.AreEqual("backup slot 'lpt1.world' is a reserved device name.", error);

            Assert.IsTrue(RbxWorldPackageNames.TryValidateAutoFileName("x.WORLD", out error), error);
            Assert.IsFalse(RbxWorldPackageNames.TryValidateAutoFileName(null, out error));
            Assert.AreEqual("Auto package name must be one .world file name without a path.", error);
        }

        private const string ValidAutoName = "20260902T120000000Z-0000-execute_lua.world";

        private static readonly DateTime AutosaveClockUtc = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// save_world through the real tool, runtime controller and file store: the second call to the same
        /// slot carries a DIFFERENT world, so an overwriting store would change the bytes on disk.
        /// </summary>
        [Test]
        public async Task SaveWorldTool_SecondSaveToSameSlot_IsRefusedAsResultAndKeepsFirstBytes()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => AutosaveClockUtc);
            HeadlessRbxWorldSessionHost host = CreateHeadlessHost();
            RbxWorldRuntimeSessionController controller =
                CreateController(host, fileStore, new DelegateModSourceStore());
            SaveWorldLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);
            string expectedPath = Path.Combine(Path.GetFullPath(root), "Manual", "golden-slot.world");

            JObject first = JObject.Parse(await tool.ExecuteAsync("golden-slot"));

            Assert.IsTrue((bool)first["success"], first.ToString());
            Assert.AreEqual(expectedPath, (string)first["path"]);
            byte[] original = File.ReadAllBytes(expectedPath);
            RbxInstance marker = host.Registry.Create("Folder");
            marker.Name = "AddedAfterFirstSave";
            marker.Parent = host.Registry.WorldRoot;
            CollectionAssert.AreNotEqual(
                original,
                RbxWorldPackageSerializer.WritePackage(controller.CaptureCurrent()),
                "The second save must carry a different world, or an overwrite would leave identical bytes.");

            JObject second = JObject.Parse(await tool.ExecuteAsync("golden-slot"));

            Assert.IsFalse((bool)second["success"], second.ToString());
            Assert.AreEqual(expectedPath, (string)second["path"]);
            StringAssert.Contains("already exists and cannot be overwritten", (string)second["error"]);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(expectedPath));
            CollectionAssert.AreEqual(
                new[] { expectedPath },
                Directory.GetFiles(Path.GetDirectoryName(expectedPath)),
                "A refused save must leave no second file or temporary behind.");
            CollectionAssert.AreEqual(new[] { "golden-slot" }, fileStore.ListManualSlots());
        }

        /// <summary>
        /// Pins "no delete path for AI tools": neither persistence seam nor any world tool exposes a member
        /// named Delete*/Overwrite*/Remove*/Replace* or a parameter that could ask for one.
        /// </summary>
        [Test]
        public void WorldPersistenceSurface_ExposesNoDeleteOverwriteRemoveOrReplacePath()
        {
            Type[] surfaces =
            {
                typeof(IRbxWorldPackageStore),
                typeof(IRbxWorldRuntimeService),
                typeof(SaveWorldLlmTool),
                typeof(LoadWorldLlmTool),
                typeof(ListAutoSavesLlmTool),
                typeof(LoadAutoSaveLlmTool)
            };
            List<string> violations = new();
            foreach (Type surface in surfaces)
            {
                violations.AddRange(FindDestructiveMembers(surface));
            }

            CollectionAssert.IsEmpty(violations, string.Join(", ", violations));
            CollectionAssert.IsNotEmpty(
                FindDestructiveMembers(typeof(ILuaModSourceStore)),
                "The scan must detect a real delete member (ILuaModSourceStore.Delete), or it proves nothing.");
        }

        [Test]
        public void WorldTools_SchemasExposeOnlyTheirNameArgument()
        {
            RecordingRuntimeService service = new();
            IActorIdentityProvider identity = ToolIdentity();

            AssertToolSchema(
                new SaveWorldLlmTool(service, identity, BuiltInAgentRoleIds.Programmer), "save_world", "slot");
            AssertToolSchema(
                new LoadWorldLlmTool(service, identity, BuiltInAgentRoleIds.Programmer), "load_world", "slot");
            AssertToolSchema(
                new ListAutoSavesLlmTool(service, identity, BuiltInAgentRoleIds.Programmer), "list_autosaves");
            AssertToolSchema(
                new LoadAutoSaveLlmTool(service, identity, BuiltInAgentRoleIds.Programmer), "load_autosave", "name");
        }

        [Test]
        public async Task ListAutoSaves_HyphenatedTriggers_RoundTripExactly()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => AutosaveClockUtc);
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            Assert.AreEqual("manage_mods-load", LuaModsLlmTool.LoadBackupTrigger);
            Assert.AreEqual("manage_mods-revert", LuaModsLlmTool.RevertBackupTrigger);
            string[] triggers =
            {
                LuaModsLlmTool.LoadBackupTrigger,
                "load_world-pre",
                LuaCsGameToolExecutor.ExecuteLuaBackupTrigger,
                LuaModsLlmTool.RevertBackupTrigger
            };
            foreach (string trigger in triggers)
            {
                RbxWorldPackageWriteResult result = await store.CreateAutoAsync(trigger, payload);
                Assert.IsTrue(result.Success, result.Error);
            }

            IReadOnlyList<RbxAutoSaveInfo> infos = store.ListAutoSaves();

            string[] expectedNames =
            {
                "20260902T120000000Z-0000-manage_mods-load.world",
                "20260902T120000000Z-0001-load_world-pre.world",
                "20260902T120000000Z-0002-execute_lua.world",
                "20260902T120000000Z-0003-manage_mods-revert.world"
            };
            string[] expectedTriggers = { "manage_mods-load", "load_world-pre", "execute_lua", "manage_mods-revert" };
            Assert.AreEqual(expectedNames.Length, infos.Count);
            for (int index = 0; index < expectedNames.Length; index++)
            {
                Assert.AreEqual(expectedNames[index], infos[index].FileName);
                Assert.AreEqual(expectedTriggers[index], infos[index].Trigger);
                Assert.AreEqual(AutosaveClockUtc, infos[index].TimestampUtc);
            }
        }

        [Test]
        public async Task ListAutoSavesTool_ReturnsExactNameTriggerTimestampAndSize()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => AutosaveClockUtc);
            RbxWorldPackageWriteResult first = await fileStore.CreateAutoAsync(
                "execute_lua", CreateMinimalPayload(CapturedAtUtc));
            RbxWorldPackageWriteResult second = await fileStore.CreateAutoAsync(
                "manage_mods-load", CreateMinimalPayload(CapturedAtUtc.AddSeconds(1d)));
            Assert.IsTrue(first.Success, first.Error);
            Assert.IsTrue(second.Success, second.Error);
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
            ListAutoSavesLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = ParseJsonLiteral(await tool.ExecuteAsync());

            CollectionAssert.AreEqual(new[] { "autosaves" }, PropertyNames(json));
            JArray autosaves = (JArray)json["autosaves"];
            Assert.AreEqual(2, autosaves.Count);
            AssertAutosaveRow(
                autosaves[0],
                "20260902T120000000Z-0000-execute_lua.world",
                "execute_lua",
                "2026-09-02T12:00:00.0000000Z",
                new FileInfo(first.Path).Length);
            AssertAutosaveRow(
                autosaves[1],
                "20260902T120000000Z-0001-manage_mods-load.world",
                "manage_mods-load",
                "2026-09-02T12:00:00.0000000Z",
                new FileInfo(second.Path).Length);
        }

        [Test]
        public async Task ListAutoSavesTool_StoreFailure_IsReturnedAsJsonFailure()
        {
            ThrowingListStore failingStore = new(new IOException("Injected listing failure."));
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), failingStore, new DelegateModSourceStore());
            ListAutoSavesLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync());

            Assert.IsFalse((bool)json["success"], json.ToString());
            Assert.AreEqual("list_failed", (string)json["status"]);
            CollectionAssert.IsEmpty((JArray)json["autosaves"]);
            StringAssert.Contains("Injected listing failure.", (string)json["error"]);
            StringAssert.Contains("No autosave was changed", (string)json["error"]);
            Assert.AreEqual(1, failingStore.ListCalls);
        }

        [Test]
        public void ListAutoSavesTool_StoreCancellation_PropagatesInsteadOfBecomingAResult()
        {
            ThrowingListStore cancelledStore = new(new OperationCanceledException("Injected listing cancellation."));
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), cancelledStore, new DelegateModSourceStore());
            ListAutoSavesLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            Assert.CatchAsync<OperationCanceledException>(async () => await tool.ExecuteAsync());
            Assert.AreEqual(1, cancelledStore.ListCalls);
        }

        [Test]
        public async Task LoadAutoSaveTool_RotatedAwayName_IsRefusedAsNotFoundResult()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore fileStore = new(
                root,
                1,
                cancellationToken => UniTask.FromResult(true),
                () => AutosaveClockUtc);
            RbxWorldPackageWriteResult rotated = await fileStore.CreateAutoAsync(
                "execute_lua", CreateMinimalPayload(CapturedAtUtc));
            RbxWorldPackageWriteResult kept = await fileStore.CreateAutoAsync(
                "manage_mods-load", CreateMinimalPayload(CapturedAtUtc.AddSeconds(1d)));
            Assert.IsTrue(rotated.Success, rotated.Error);
            Assert.IsTrue(kept.Success, kept.Error);
            string rotatedName = Path.GetFileName(rotated.Path);
            CollectionAssert.AreEqual(
                new[] { Path.GetFileName(kept.Path) },
                fileStore.ListAutoFiles(),
                "Precondition: the first autosave was rotated out of the ring.");
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
            LoadAutoSaveLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(rotatedName));

            AssertUnloadableAutosave(json, rotatedName, "not_found");
            StringAssert.Contains("rotated out of the autosave ring", (string)json["error"]);
            StringAssert.Contains("list_autosaves", (string)json["error"]);
            Assert.AreEqual(0, controller.GetPendingManualLoads().Count);
        }

        [Test]
        public async Task LoadAutoSaveTool_PackageAboveReadLimit_IsRefusedAsInvalidPackageResult()
        {
            string root = NewTemporaryDirectory();
            string autoDirectory = Path.Combine(root, "Auto");
            Directory.CreateDirectory(autoDirectory);
            const string oversizedName = "20260902T120000000Z-0000-oversized.world";
            using (FileStream stream = new(
                       Path.Combine(autoDirectory, oversizedName),
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength((long)RbxWorldPackageSerializer.MaximumPackageBytes + 1L);
            }

            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
            LoadAutoSaveLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(oversizedName));

            AssertUnloadableAutosave(json, oversizedName, "invalid_package");
            StringAssert.Contains("format version 1 limit", (string)json["error"]);
            Assert.AreEqual(0, controller.GetPendingManualLoads().Count);
        }

        /// <summary>
        /// A coroutine because the store yields one PlayerLoop frame after reading a package, before
        /// decoding it; the intact autosave in the same ring is the negative twin.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator LoadAutoSaveTool_CorruptOrTruncatedPackage_IsRefusedAsInvalidPackageResult()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = NewTemporaryDirectory();
                FileRbxWorldPackageStore fileStore = new(
                    root,
                    persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                    utcNow: () => AutosaveClockUtc);
                RbxWorldPackageWriteResult intact = await fileStore.CreateAutoAsync(
                    "execute_lua", CreateMinimalPayload(CapturedAtUtc));
                Assert.IsTrue(intact.Success, intact.Error);
                byte[] intactBytes = File.ReadAllBytes(intact.Path);
                byte[] truncatedBytes = new byte[intactBytes.Length / 2];
                Array.Copy(intactBytes, truncatedBytes, truncatedBytes.Length);
                string autoDirectory = Path.GetDirectoryName(intact.Path);
                Dictionary<string, byte[]> corrupt = new(StringComparer.Ordinal)
                {
                    ["20260902T120000000Z-0001-truncated.world"] = truncatedBytes,
                    ["20260902T120000000Z-0002-empty.world"] = Array.Empty<byte>(),
                    ["20260902T120000000Z-0003-garbage.world"] =
                        System.Text.Encoding.UTF8.GetBytes("this is not a world package")
                };
                foreach (KeyValuePair<string, byte[]> entry in corrupt)
                {
                    File.WriteAllBytes(Path.Combine(autoDirectory, entry.Key), entry.Value);
                }

                RbxWorldRuntimeSessionController controller =
                    CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
                LoadAutoSaveLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

                foreach (string corruptName in corrupt.Keys)
                {
                    JObject json = JObject.Parse(await tool.ExecuteAsync(corruptName));
                    AssertUnloadableAutosave(json, corruptName, "invalid_package");
                    StringAssert.Contains("is not a loadable world package", (string)json["error"]);
                }

                Assert.AreEqual(0, controller.GetPendingManualLoads().Count);
                JObject control = JObject.Parse(await tool.ExecuteAsync(Path.GetFileName(intact.Path)));
                Assert.AreEqual("player_confirmation_required", (string)control["status"], control.ToString());
                Assert.AreEqual(1, controller.GetPendingManualLoads().Count);
            });
        }

        [TestCase("file-not-found", "not_found")]
        [TestCase("directory-not-found", "not_found")]
        [TestCase("invalid-package", "invalid_package")]
        [TestCase("io", "read_failed")]
        [TestCase("unauthorized", "read_failed")]
        public async Task LoadAutoSaveTool_ReadPhaseFailures_AreReturnedAsJsonResults(
            string fault,
            string expectedStatus)
        {
            RecordingRuntimeService service = new() { AutoLoadFault = CreateReadFault(fault) };
            LoadAutoSaveLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(ValidAutoName));

            AssertUnloadableAutosave(json, ValidAutoName, expectedStatus);
            if (!string.Equals(expectedStatus, "not_found", StringComparison.Ordinal))
            {
                StringAssert.Contains("Injected", (string)json["error"], "The cause is reported to the model.");
            }

            CollectionAssert.AreEqual(new[] { ValidAutoName }, service.RequestedAutoFiles);
        }

        [Test]
        public void LoadAutoSaveTool_ServiceCancellation_PropagatesInsteadOfBecomingAResult()
        {
            RecordingRuntimeService service = new()
            {
                AutoLoadFault = new OperationCanceledException("Injected cancellation.")
            };
            LoadAutoSaveLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            Assert.CatchAsync<OperationCanceledException>(async () => await tool.ExecuteAsync(ValidAutoName));
        }

        [Test]
        public async Task LoadAutoSaveTool_CancelledToken_PropagatesThroughTheFileStore()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => AutosaveClockUtc);
            RbxWorldPackageWriteResult autosave = await fileStore.CreateAutoAsync(
                "execute_lua", CreateMinimalPayload(CapturedAtUtc));
            Assert.IsTrue(autosave.Success, autosave.Error);
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
            LoadAutoSaveLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(
                async () => await tool.ExecuteAsync(Path.GetFileName(autosave.Path), cancellation.Token));
            Assert.AreEqual(0, controller.GetPendingManualLoads().Count);
        }

        private static Exception CreateReadFault(string fault)
        {
            switch (fault)
            {
                case "file-not-found":
                    return new FileNotFoundException("Injected missing package.", ValidAutoName);
                case "directory-not-found":
                    return new DirectoryNotFoundException("Injected missing directory.");
                case "invalid-package":
                    return new RbxWorldPackageException("Injected corrupt package.");
                case "io":
                    return new IOException("Injected read failure.");
                case "unauthorized":
                    return new UnauthorizedAccessException("Injected permission failure.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unknown injected fault.");
            }
        }

        private static void AssertUnloadableAutosave(JObject json, string name, string status)
        {
            Assert.IsFalse((bool)json["success"], json.ToString());
            Assert.AreEqual(status, (string)json["status"]);
            Assert.IsFalse((bool)json["player_confirmation_required"]);
            Assert.AreEqual("", (string)json["request_id"]);
            Assert.AreEqual(name, (string)json["slot"]);
            Assert.AreEqual("", (string)json["world_id"]);
            StringAssert.Contains("'" + name + "'", (string)json["error"], "The error names the file.");
            StringAssert.Contains("The tool was NOT executed.", (string)json["error"]);
        }

        private static void AssertAutosaveRow(JToken row, string name, string trigger, string timestamp, long size)
        {
            CollectionAssert.AreEquivalent(new[] { "name", "trigger", "timestamp", "size" }, PropertyNames(row));
            Assert.AreEqual(name, (string)row["name"]);
            Assert.AreEqual(trigger, (string)row["trigger"]);
            Assert.AreEqual(JTokenType.String, row["timestamp"].Type);
            Assert.AreEqual(timestamp, (string)row["timestamp"]);
            Assert.AreEqual(JTokenType.Integer, row["size"].Type);
            Assert.AreEqual(size, (long)row["size"]);
            Assert.IsTrue(size > 0L);
        }

        private static void AssertToolSchema(ILlmTool tool, string name, params string[] parameters)
        {
            Assert.AreEqual(name, tool.Name);
            JObject schema = JObject.Parse(tool.ParametersSchema);
            CollectionAssert.AreEqual(
                parameters,
                PropertyNames(schema["properties"]),
                name + " must take no argument that could overwrite, delete or force a save.");
        }

        private static List<string> FindDestructiveMembers(Type surface)
        {
            string[] forbiddenPrefixes = { "Delete", "Overwrite", "Remove", "Replace" };
            string[] forbiddenParameterWords = { "delete", "overwrite", "remove", "replace", "force" };
            List<string> violations = new();
            foreach (MemberInfo member in surface.GetMembers(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                MethodBase method = member as MethodBase;
                // WHY: accessor methods (add_/remove_ of an event, get_ of a property) and constructors are
                // WHY: named by the compiler; the event or property they belong to is scanned by its own name.
                if (method == null || !method.IsSpecialName)
                {
                    foreach (string prefix in forbiddenPrefixes)
                    {
                        if (member.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add(surface.Name + "." + member.Name);
                        }
                    }
                }

                if (method == null)
                {
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    foreach (string word in forbiddenParameterWords)
                    {
                        if (parameter.Name != null
                            && parameter.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            violations.Add(surface.Name + "." + member.Name + "(" + parameter.Name + ")");
                        }
                    }
                }
            }

            return violations;
        }

        /// <summary>Parses JSON without turning ISO-8601 strings into dates, so literal text compares exactly.</summary>
        private static JObject ParseJsonLiteral(string json)
        {
            using StringReader text = new(json);
            using Newtonsoft.Json.JsonTextReader reader = new(text)
            {
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None
            };
            return JObject.Load(reader);
        }

        private static List<string> PropertyNames(JToken token)
        {
            Assert.AreEqual(JTokenType.Object, token.Type);
            List<string> names = new();
            foreach (JProperty property in ((JObject)token).Properties())
            {
                names.Add(property.Name);
            }

            return names;
        }

        /// <summary>A package store whose ring listing fails with a chosen exception.</summary>
        private sealed class ThrowingListStore : IRbxWorldPackageStore
        {
            private readonly Exception _fault;

            public ThrowingListStore(Exception fault)
            {
                _fault = fault;
            }

            public int ListCalls { get; private set; }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
                string slot,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
                string trigger,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(
                string slot,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(
                string fileName,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                ListCalls++;
                throw _fault;
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                ListCalls++;
                throw _fault;
            }
        }
    }
}
