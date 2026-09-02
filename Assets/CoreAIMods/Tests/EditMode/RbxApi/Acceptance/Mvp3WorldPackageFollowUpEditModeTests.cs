using System;
using System.Collections.Generic;
using System.IO;
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
            StringAssert.Contains("execute_lua", infos[0].Trigger);
            Assert.IsTrue(infos[0].SizeBytes > 0L);
            Assert.AreNotEqual(default(DateTime), infos[0].TimestampUtc);

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
            RbxWorldLoadResult expired = await controller.ConfirmManualLoadAsync("unknown-id", true, CancellationToken.None);
            Assert.IsFalse(expired.Success);
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
    }
}
