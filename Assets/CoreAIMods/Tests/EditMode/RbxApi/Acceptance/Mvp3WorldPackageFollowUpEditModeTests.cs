using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Lua;
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

        // ==================== A1-02: mods restart in their load order ====================

        private const string CastleModId = "z-castle";
        private const string DoorModId = "a-door";

        private const string CastleModSource =
            "local castle = Instance.new('Folder'); castle.Name = 'Castle'; castle.Parent = workspace";

        private const string DoorModSource =
            "local door = Instance.new('Folder'); door.Name = 'Door'; door.Parent = workspace.Castle";

        /// <summary>
        /// A1-02: the door mod parents its instance under the castle the castle mod made at init, and the
        /// ids sort the other way. The package keeps its mods in id order (deterministic bytes) and each
        /// manifest carries the mod's load order, so a confirmed load of the world's own save starts them
        /// as they were loaded; it used to start them by id and fail all-or-nothing. The join snapshot is
        /// the same capture and carries the same order.
        /// </summary>
        [Test]
        public async Task WorldPackage_ModsThatNeedEachOtherAtInit_ReloadTheirOwnSave_InLoadOrder()
        {
            RbxWorldRuntimeSessionController controller = CreateController(
                CreateHeadlessHost(), NewSafetyAutosaveStore(), NewModSourceStore());
            ActorContext host = new LocalActorIdentityProvider().GetActorContext(BuiltInAgentRoleIds.Programmer);
            controller.Runtime.LoadMod(host, CastleModId, CastleModSource);
            controller.Runtime.LoadMod(host, DoorModId, DoorModSource);
            Assert.IsNotNull(
                controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Castle")?.FindFirstChild("Door"),
                "precondition: both mods ran in the live world");

            RbxWorldPackagePayload saved = RbxWorldPackageSerializer.ReadPackage(
                RbxWorldPackageSerializer.WritePackage(controller.CaptureCurrent()));
            RbxWorldPackagePayload joinSnapshot = RbxWorldPackageSerializer.ExportSnapshot(
                new RbxWorldPackageCaptureContext(
                    controller.CurrentRbxApi.Registry,
                    controller.CurrentRbxApi.Game,
                    controller.CurrentRbxApi.PartSink,
                    NewSettings(),
                    modSourceStore: controller.SourceStore));
            RbxWorldLoadResult result = await controller.LoadConfirmedAsync(saved, CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(2, result.ActiveModsStarted);
            Assert.IsNotNull(
                controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Castle")?.FindFirstChild("Door"),
                "the restored world runs both mods again, the castle first");
            CollectionAssert.AreEqual(new[] { DoorModId, CastleModId }, PayloadModIds(saved),
                "the package keeps its mods in id order");
            long castleOrder = PayloadLoadOrder(saved, CastleModId);
            long doorOrder = PayloadLoadOrder(saved, DoorModId);
            Assert.Greater(castleOrder, 0L);
            Assert.Greater(doorOrder, castleOrder);
            Assert.AreEqual(castleOrder, PayloadLoadOrder(joinSnapshot, CastleModId));
            Assert.AreEqual(doorOrder, PayloadLoadOrder(joinSnapshot, DoorModId));
            RbxWorldPackagePayload resaved = controller.CaptureCurrent();
            Assert.AreEqual(castleOrder, PayloadLoadOrder(resaved, CastleModId), "a restore never restamps");
            Assert.AreEqual(doorOrder, PayloadLoadOrder(resaved, DoorModId), "a restore never restamps");
        }

        /// <summary>
        /// A1-02: a package written before mods carried a load order has no LoadOrder key in its mod
        /// manifests. It still restores, its mods starting by ordinal id exactly as before.
        /// </summary>
        [Test]
        public async Task WorldPackage_WrittenWithoutLoadOrder_StillRestores_ModsStartByOrdinalId()
        {
            RbxWorldRuntimeSessionController controller = CreateController(
                CreateHeadlessHost(), NewSafetyAutosaveStore(), NewModSourceStore());
            RbxWorldPackagePayload legacy = CastleAndDoorPayload(
                ModManifest("a-castle", 0), ModManifest("b-door", 0));
            byte[] package = RbxWorldPackageSerializer.WritePackage(legacy);
            StringAssert.DoesNotContain("LoadOrder", ReadPackageEntry(package, "Mods/0000/manifest.json"),
                "a mod without a recorded order is written exactly as before the field existed");

            RbxWorldLoadResult result = await controller.LoadConfirmedAsync(
                RbxWorldPackageSerializer.ReadPackage(package), CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(2, result.ActiveModsStarted);
            Assert.IsNotNull(
                controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Castle")?.FindFirstChild("Door"));
            Assert.AreEqual(0L, PayloadLoadOrder(controller.CaptureCurrent(), "a-castle"),
                "a restore never stamps a legacy mod");
        }

        /// <summary>
        /// A1-02: a negative load order in an untrusted package is not rejected; it counts as no recorded
        /// order, so "b-door" (-3) starts after "a-castle" (0) by id instead of before it by value.
        /// </summary>
        [Test]
        public async Task WorldPackage_NegativeLoadOrder_IsNotRejected_AndCountsAsNoRecordedOrder()
        {
            RbxWorldRuntimeSessionController controller = CreateController(
                CreateHeadlessHost(), NewSafetyAutosaveStore(), NewModSourceStore());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(
                RbxWorldPackageSerializer.WritePackage(
                    CastleAndDoorPayload(ModManifest("a-castle", 0), ModManifest("b-door", -3))));
            Assert.AreEqual(-3L, PayloadLoadOrder(read, "b-door"));

            RbxWorldLoadResult result = await controller.LoadConfirmedAsync(read, CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(2, result.ActiveModsStarted);
            Assert.IsNotNull(
                controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Castle")?.FindFirstChild("Door"));
        }

        private FileLuaModSourceStore NewModSourceStore()
        {
            return new FileLuaModSourceStore(
                NewTemporaryDirectory(),
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));
        }

        private static IRbxWorldPackageStore NewSafetyAutosaveStore()
        {
            return new InMemoryAutosaveStore(
                null,
                new RbxAutoSaveInfo("unused.world", "unused", CapturedAtUtc, 0L));
        }

        private static LuaModManifest ModManifest(string id, long loadOrder)
        {
            return new LuaModManifest
            {
                Id = id,
                Name = id,
                Capabilities = LuaCapabilities.All.ToString(),
                Active = true,
                LoadOrder = loadOrder
            };
        }

        /// <summary>An empty world whose two mods are the castle and the door that needs it at init.</summary>
        private RbxWorldPackagePayload CastleAndDoorPayload(LuaModManifest castle, LuaModManifest door)
        {
            RbxWorldPackagePayload world = CreateMinimalPayload(CapturedAtUtc);
            return new RbxWorldPackagePayload(
                world.CapturedAtUtc,
                world.Settings,
                world.Tree,
                world.Parts,
                world.CameraCFrame,
                new[]
                {
                    new RbxWorldModSource(castle, CastleModSource),
                    new RbxWorldModSource(door, DoorModSource)
                });
        }

        private static List<string> PayloadModIds(RbxWorldPackagePayload payload)
        {
            List<string> ids = new(payload.Mods.Count);
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                ids.Add(mod.Manifest.Id);
            }

            return ids;
        }

        private static long PayloadLoadOrder(RbxWorldPackagePayload payload, string modId)
        {
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                if (string.Equals(mod.Manifest.Id, modId, StringComparison.Ordinal))
                {
                    return mod.Manifest.LoadOrder;
                }
            }

            Assert.Fail("The payload holds no mod '" + modId + "'.");
            return 0L;
        }

        private static string ReadPackageEntry(byte[] package, string entryName)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            ZipArchiveEntry entry = archive.GetEntry(entryName);
            Assert.IsNotNull(entry, "Missing package entry '" + entryName + "'.");
            using Stream stream = entry.Open();
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
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
            ILuaModSourceStore sourceStore)
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

            /// <summary>When set, <see cref="RequestManualLoadAsync"/> completes faulted with this exception.</summary>
            public Exception ManualLoadFault { get; set; }

            /// <summary>When set, <see cref="SaveManualAsync"/> completes faulted with this exception.</summary>
            public Exception SaveFault { get; set; }

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
                if (SaveFault != null)
                {
                    return UniTask.FromException<RbxWorldPackageWriteResult>(SaveFault);
                }

                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, slot + ".world", ""));
            }

            public UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                RequestedSlots.Add(slot);
                if (ManualLoadFault != null)
                {
                    return UniTask.FromException<RbxWorldLoadRequest>(ManualLoadFault);
                }

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

        /// <summary>Quiet settings for the manage_mods tool the startup tests drive.</summary>
        private sealed class StartupToolSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 0;

            public bool EnableMeaiDebugLogging => false;

            public float LlmRequestTimeoutSeconds => 30f;

            public int MaxLlmRequestRetries => 0;

            public bool EnableHttpDebugLogging => false;

            public bool LogTokenUsage => false;

            public bool LogLlmLatency => false;

            public bool LogLlmConnectionErrors => false;

            public int ContextWindowTokens => 4096;

            public string UniversalSystemPromptPrefix => "";

            public float Temperature => 0f;

            public int MaxToolCallRetries => 0;

            public bool LogToolCalls => false;

            public bool LogToolCallArguments => false;

            public bool LogToolCallResults => false;

            public bool LogMeaiToolCallingSteps => false;

            public bool AllowDuplicateToolCalls => false;

            public bool EnableStreaming => false;
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
        [TestCase("\"a.world\"")]
        [TestCase("a|b.world")]
        [TestCase("a\tb.world")]
        [TestCase("a\0b.world")]
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
        [TestCase("device-not-supported", "read_failed")]
        [TestCase("device-argument", "read_failed")]
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

        [TestCase("refused-acl", "invalid_package")]
        [TestCase("refused-network", "network_sessions_active")]
        public async Task LoadAutoSaveTool_SessionRefusals_ReportTheRefusalsOwnStatus(
            string fault,
            string expectedStatus)
        {
            RecordingRuntimeService service = new() { AutoLoadFault = CreateManualLoadFault(fault) };
            LoadAutoSaveLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync(ValidAutoName));

            AssertUnloadableAutosave(json, ValidAutoName, expectedStatus);
            StringAssert.Contains("cannot be loaded into this session", (string)json["error"],
                "A session-rule refusal is not reported as a corrupt package.");
            StringAssert.Contains("Injected", (string)json["error"], "The refusal's reason is reported to the model.");
            StringAssert.DoesNotContain("Pick another autosave", (string)json["error"],
                "Another autosave would be refused by the same session rule.");
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
                case "device-not-supported":
                    // WHY (B2-14): opening a Windows device name such as CON.world throws this or the
                    // next one instead of an IOException, and it used to escape the tool.
                    return new NotSupportedException("Injected device path refusal.");
                case "device-argument":
                    return new ArgumentException("Injected device path refusal.");
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

        [Test]
        public async Task SaveWorldTool_CaptureFailure_IsReturnedAsCaptureFailedResult()
        {
            RecordingRuntimeService service = new()
            {
                SaveFault = new RbxWorldPackageException("Injected capture failure.")
            };
            SaveWorldLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync("valid-slot_1"));

            Assert.IsFalse((bool)json["success"], json.ToString());
            Assert.AreEqual("capture_failed", (string)json["status"]);
            Assert.AreEqual("", (string)json["path"]);
            StringAssert.Contains("'valid-slot_1'", (string)json["error"]);
            StringAssert.Contains("Injected capture failure.", (string)json["error"]);
            StringAssert.Contains("The tool was NOT executed.", (string)json["error"]);
            CollectionAssert.AreEqual(new[] { "valid-slot_1" }, service.SavedSlots);
        }

        /// <summary>The real controller path: a session that is gone cannot be captured, and nothing is written.</summary>
        [Test]
        public async Task SaveWorldTool_DisposedSessionThroughController_IsCaptureFailedAndWritesNothing()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => AutosaveClockUtc);
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
            SaveWorldLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);
            controller.Dispose();

            JObject json = JObject.Parse(await tool.ExecuteAsync("never-written"));

            Assert.IsFalse((bool)json["success"], json.ToString());
            Assert.AreEqual("capture_failed", (string)json["status"]);
            StringAssert.Contains("Nothing was written.", (string)json["error"]);
            CollectionAssert.IsEmpty(fileStore.ListManualSlots());
        }

        /// <summary>
        /// A1-10 through the real tool and controller: a save past the manual-slot limit is an ordinary
        /// failed result the model can read, and nothing is written.
        /// </summary>
        [Test]
        public async Task SaveWorldTool_ManualSlotLimitReached_IsAnOrdinaryFailedResultAndWritesNothing()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore fileStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => AutosaveClockUtc,
                maximumManualSlots: 1);
            RbxWorldRuntimeSessionController controller =
                CreateController(CreateHeadlessHost(), fileStore, new DelegateModSourceStore());
            SaveWorldLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject first = JObject.Parse(await tool.ExecuteAsync("first-slot"));
            JObject second = JObject.Parse(await tool.ExecuteAsync("second-slot"));

            Assert.IsTrue((bool)first["success"], first.ToString());
            Assert.IsFalse((bool)second["success"], second.ToString());
            StringAssert.Contains("the limit is 1", (string)second["error"]);
            StringAssert.Contains("Nothing was written.", (string)second["error"]);
            CollectionAssert.AreEqual(new[] { "first-slot" }, fileStore.ListManualSlots());
        }

        [Test]
        public void SaveWorldTool_ServiceCancellation_PropagatesInsteadOfBecomingAResult()
        {
            RecordingRuntimeService service = new()
            {
                SaveFault = new OperationCanceledException("Injected cancellation.")
            };
            SaveWorldLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            Assert.CatchAsync<OperationCanceledException>(async () => await tool.ExecuteAsync("valid-slot_1"));
        }

        [TestCase("file-not-found", "not_found")]
        [TestCase("directory-not-found", "not_found")]
        [TestCase("invalid-package", "invalid_package")]
        [TestCase("io", "read_failed")]
        [TestCase("unauthorized", "read_failed")]
        [TestCase("refused-acl", "invalid_package")]
        [TestCase("refused-network", "network_sessions_active")]
        public async Task LoadWorldTool_ReadPhaseFailuresAndRefusals_AreReturnedAsJsonResults(
            string fault,
            string expectedStatus)
        {
            RecordingRuntimeService service = new() { ManualLoadFault = CreateManualLoadFault(fault) };
            LoadWorldLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject json = JObject.Parse(await tool.ExecuteAsync("valid-slot_1"));

            AssertUnloadableAutosave(json, "valid-slot_1", expectedStatus);
            if (!string.Equals(expectedStatus, "not_found", StringComparison.Ordinal))
            {
                StringAssert.Contains("Injected", (string)json["error"], "The cause is reported to the model.");
            }

            CollectionAssert.AreEqual(new[] { "valid-slot_1" }, service.RequestedSlots);
        }

        [Test]
        public void LoadWorldTool_ServiceCancellation_PropagatesInsteadOfBecomingAResult()
        {
            RecordingRuntimeService service = new()
            {
                ManualLoadFault = new OperationCanceledException("Injected cancellation.")
            };
            LoadWorldLlmTool tool = new(service, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            Assert.CatchAsync<OperationCanceledException>(async () => await tool.ExecuteAsync("valid-slot_1"));
        }

        [Test]
        public async Task LoadWorldTool_MissingOrOversizedSlotThroughTheFileStore_IsRefusedAsResult()
        {
            string root = NewTemporaryDirectory();
            string manualDirectory = Path.Combine(root, "Manual");
            Directory.CreateDirectory(manualDirectory);
            using (FileStream stream = new(
                       Path.Combine(manualDirectory, "oversized.world"),
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
            LoadWorldLlmTool tool = new(controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer);

            JObject missing = JObject.Parse(await tool.ExecuteAsync("never-saved"));
            JObject oversized = JObject.Parse(await tool.ExecuteAsync("oversized"));

            AssertUnloadableAutosave(missing, "never-saved", "not_found");
            StringAssert.Contains("save_world", (string)missing["error"]);
            AssertUnloadableAutosave(oversized, "oversized", "invalid_package");
            StringAssert.Contains("format version 1 limit", (string)oversized["error"]);
            Assert.AreEqual(0, controller.GetPendingManualLoads().Count);
        }

        private static Exception CreateManualLoadFault(string fault)
        {
            switch (fault)
            {
                case "refused-acl":
                    return new RbxWorldLoadRefusedException(
                        RbxWorldLoadRefusedException.IncompatiblePackageStatus,
                        "Injected ACL downgrade.");
                case "refused-network":
                    return new RbxWorldLoadRefusedException(
                        RbxWorldLoadRefusedException.NetworkSessionsActiveStatus,
                        "Injected live network sessions.");
                default:
                    return CreateReadFault(fault);
            }
        }

        #region Startup selection (W3.5): the world the player confirmed survives a restart

        private const string StartupNamespace = "followup-startup";
        private const string StartupActiveModId = "startup-active";
        private const string StartupDormantModId = "startup-dormant";
        private const string StartupExtraModId = "default-extra";
        private const string KeptPartName = "KeptPart";
        private const string ModStartFolderName = "ModStart";

        // WHY: the Gameplay tier eagerly binds UnityEngine.Input ICALLs and nothing on the startup path
        // needs it; every other standard tier is granted, as in the headless QA composition.
        private const LuaCapabilities StartupCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        private const string StartupActiveModSource = @"local marker = Instance.new('Folder')
marker.Name = 'ModStart'
marker.Parent = workspace
game:GetService('RunService').Heartbeat:Connect(function()
    local count = tonumber(store_get('heartbeat_count')) or 0
    store_set('heartbeat_count', tostring(count + 1))
end)";

        private static readonly DateTime StartupClockUtc = new(2026, 9, 3, 8, 30, 15, DateTimeKind.Utc);

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupStore_Select_NewStoreInstanceReadsExactConfirmedBytes()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = NewTemporaryDirectory();
                RbxWorldPackagePayload payload = CreateStartupPayload(
                    "selected-world",
                    new RbxWorldModSource(StartupManifest("packaged-mod", true), "return true"));
                FileRbxWorldPackageStore writer = NewStartupStore(root, StartupNamespace);

                RbxWorldPackageWriteResult selected = await writer.SelectStartupAsync(payload, "manual", "slot-a");

                Assert.IsTrue(selected.Success, selected.Error);
                string startupDirectory = Path.Combine(
                    Path.GetFullPath(root), "Startup", "Stores", StartupNamespace);
                Assert.AreEqual(startupDirectory, writer.StartupDirectoryForTests);
                Assert.AreEqual(Path.Combine(startupDirectory, "0000000001.world"), selected.Path);
                CollectionAssert.AreEqual(
                    new[] { "0000000001.json", "0000000001.world" },
                    FileNames(startupDirectory));
                byte[] expected = RbxWorldPackageSerializer.WritePackage(payload);
                CollectionAssert.AreEqual(expected, File.ReadAllBytes(selected.Path));

                FileRbxWorldPackageStore reader = NewStartupStore(root, StartupNamespace);
                RbxWorldStartupSelection read = await reader.ReadStartupAsync();

                Assert.AreEqual(RbxWorldStartupSelectionKind.Package, read.Kind, read.Error);
                Assert.AreEqual(1, read.Sequence);
                Assert.AreEqual("selected-world", read.WorldId);
                Assert.IsTrue(read.SelectedAtUtc.HasValue);
                Assert.AreEqual(StartupClockUtc, read.SelectedAtUtc.Value);
                Assert.AreEqual(DateTimeKind.Utc, read.SelectedAtUtc.Value.Kind);
                Assert.AreEqual("manual", read.SourceKind);
                Assert.AreEqual("slot-a", read.SourceName);
                CollectionAssert.AreEqual(expected, RbxWorldPackageSerializer.WritePackage(read.Payload));

                RbxWorldStartupSelection info = await reader.ReadStartupInfoAsync();

                Assert.AreEqual(RbxWorldStartupSelectionKind.Package, info.Kind);
                Assert.IsNull(info.Payload, "the metadata read never decodes the package");
                Assert.AreEqual(1, info.Sequence);
                Assert.AreEqual("selected-world", info.WorldId);
                Assert.AreEqual(StartupClockUtc, info.SelectedAtUtc.Value);
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupStore_SyncFalse_IsFailure_AndReloadKeepsPreviousSelection()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDurabilityFileSystem fileSystem = new();
                Queue<bool> outcomes = new(new[] { true, false, true });
                string root = Path.Combine(
                    Path.GetTempPath(),
                    "CoreAI-StartupDurability-" + Guid.NewGuid().ToString("N"));
                string startupDirectory = Path.Combine(
                    Path.GetFullPath(root), "Startup", "Stores", StartupNamespace);
                FileRbxWorldPackageStore store = NewDurabilityStartupStore(root, fileSystem, outcomes);
                RbxWorldPackagePayload first = CreateStartupPayload("first-world");

                RbxWorldPackageWriteResult kept = await store.SelectStartupAsync(first, "manual", "first");
                RbxWorldPackageWriteResult refused = await store.SelectStartupAsync(
                    CreateStartupPayload("second-world"), "autosave", "second.world");
                fileSystem.ReloadFromDurable();

                Assert.IsTrue(kept.Success, kept.Error);
                Assert.IsFalse(refused.Success, "a false durability answer is a failure, never a success");
                StringAssert.Contains("durable persistence was not confirmed", refused.Error);
                StringAssert.Contains("durably removed", refused.Error);
                Assert.AreEqual(0, outcomes.Count, "the kept write, the refused write and its cleanup each asked once");
                CollectionAssert.AreEqual(
                    new[] { "0000000001.json", "0000000001.world" },
                    fileSystem.FileNamesIn(startupDirectory),
                    "neither the refused package nor its metadata survives the reload");

                RbxWorldStartupSelection reloaded = await NewDurabilityStartupStore(
                    root, fileSystem, new Queue<bool>()).ReadStartupAsync();

                Assert.AreEqual(RbxWorldStartupSelectionKind.Package, reloaded.Kind, reloaded.Error);
                Assert.AreEqual(1, reloaded.Sequence);
                Assert.AreEqual("first-world", reloaded.WorldId);
                CollectionAssert.AreEqual(
                    RbxWorldPackageSerializer.WritePackage(first),
                    RbxWorldPackageSerializer.WritePackage(reloaded.Payload));
            });
        }

        [Test]
        public async Task StartupStore_Clear_WritesDefaultMarker_ReadReturnsDefault()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = NewStartupStore(root, StartupNamespace);
            string startupDirectory = store.StartupDirectoryForTests;

            RbxWorldPackageWriteResult nothingToClear = await store.ClearStartupAsync();

            Assert.IsTrue(nothingToClear.Success, nothingToClear.Error);
            Assert.IsFalse(Directory.Exists(startupDirectory), "clearing an empty selection writes nothing");

            RbxWorldPackageWriteResult selected = await store.SelectStartupAsync(
                CreateStartupPayload("chosen-world"), "manual", "chosen");
            RbxWorldPackageWriteResult cleared = await store.ClearStartupAsync();

            Assert.IsTrue(selected.Success, selected.Error);
            Assert.IsTrue(cleared.Success, cleared.Error);
            Assert.AreEqual(Path.Combine(startupDirectory, "0000000002.default"), cleared.Path);
            CollectionAssert.AreEqual(
                new[] { "0000000002.default" },
                FileNames(startupDirectory),
                "the older selection and its metadata are pruned");
            Assert.AreEqual(0L, new FileInfo(cleared.Path).Length, "the marker is empty");

            RbxWorldStartupSelection read = await NewStartupStore(root, StartupNamespace).ReadStartupAsync();

            Assert.AreEqual(RbxWorldStartupSelectionKind.Default, read.Kind, read.Error);
            Assert.AreEqual(2, read.Sequence);
            Assert.IsNull(read.Payload);

            RbxWorldPackageWriteResult again = await store.ClearStartupAsync();

            Assert.IsTrue(again.Success, again.Error);
            CollectionAssert.AreEqual(
                new[] { "0000000002.default" },
                FileNames(startupDirectory),
                "clearing an already default selection writes nothing");
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupStore_NewestCorruptOrOversized_ReadsInvalid_NotAnOlderSelection()
        {
            return UniTask.ToCoroutine(async () =>
            {
                string root = NewTemporaryDirectory();
                FileRbxWorldPackageStore store = NewStartupStore(root, StartupNamespace);
                RbxWorldPackageWriteResult older = await store.SelectStartupAsync(
                    CreateStartupPayload("older-world"), "manual", "older");
                Assert.IsTrue(older.Success, older.Error);
                string corruptPath = Path.Combine(store.StartupDirectoryForTests, "0000000002.world");
                File.WriteAllBytes(corruptPath, System.Text.Encoding.UTF8.GetBytes("this is not a world package"));

                RbxWorldStartupSelection corrupt = await store.ReadStartupAsync();

                Assert.AreEqual(RbxWorldStartupSelectionKind.Invalid, corrupt.Kind);
                Assert.AreEqual(2, corrupt.Sequence, "the newest entry is reported; an older one is never walked back to");
                Assert.IsNull(corrupt.Payload);
                StringAssert.Contains("0000000002.world is not a loadable world package", corrupt.Error);
                Assert.IsTrue(File.Exists(corruptPath), "a read never deletes the entry");

                string oversizedPath = Path.Combine(store.StartupDirectoryForTests, "0000000003.world");
                using (FileStream stream = new(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength((long)RbxWorldPackageSerializer.MaximumPackageBytes + 1L);
                }

                RbxWorldStartupSelection oversized = await store.ReadStartupAsync();

                Assert.AreEqual(RbxWorldStartupSelectionKind.Invalid, oversized.Kind);
                Assert.AreEqual(3, oversized.Sequence);
                StringAssert.Contains("format version 1 limit", oversized.Error);
            });
        }

        [Test]
        public async Task StartupStore_Prune_KeepsOnlyNewest_AndNeverTouchesManualOrAutoBytes()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = NewStartupStore(root, StartupNamespace);
            RbxWorldPackageWriteResult manual = await store.CreateManualAsync(
                "keep-me", CreateStartupPayload("manual-world"));
            RbxWorldPackageWriteResult auto = await store.CreateAutoAsync(
                "execute_lua", CreateStartupPayload("auto-world"));
            Assert.IsTrue(manual.Success, manual.Error);
            Assert.IsTrue(auto.Success, auto.Error);
            byte[] manualBytes = File.ReadAllBytes(manual.Path);
            byte[] autoBytes = File.ReadAllBytes(auto.Path);
            CollectionAssert.AreNotEqual(manualBytes, autoBytes, "different payloads, or the byte checks prove nothing");

            RbxWorldPackageWriteResult first = await store.SelectStartupAsync(
                CreateStartupPayload("first-choice"), "manual", "keep-me");
            RbxWorldPackageWriteResult second = await store.SelectStartupAsync(
                CreateStartupPayload("second-choice"), "autosave", Path.GetFileName(auto.Path));

            Assert.IsTrue(first.Success, first.Error);
            Assert.IsTrue(second.Success, second.Error);
            CollectionAssert.AreEqual(
                new[] { "0000000002.json", "0000000002.world" },
                FileNames(store.StartupDirectoryForTests));
            CollectionAssert.AreEqual(manualBytes, File.ReadAllBytes(manual.Path));
            CollectionAssert.AreEqual(autoBytes, File.ReadAllBytes(auto.Path));
            CollectionAssert.AreEqual(new[] { "keep-me" }, store.ListManualSlots());
            CollectionAssert.AreEqual(new[] { Path.GetFileName(auto.Path) }, store.ListAutoFiles());
            RbxWorldStartupSelection info = await store.ReadStartupInfoAsync();
            Assert.AreEqual(2, info.Sequence);
            Assert.AreEqual("second-choice", info.WorldId);
            Assert.AreEqual("autosave", info.SourceKind);
            Assert.AreEqual(Path.GetFileName(auto.Path), info.SourceName);
        }

        [Test]
        public async Task StartupStore_StoreIdNamespaces_Isolate()
        {
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore alpha = NewStartupStore(root, "alpha");
            FileRbxWorldPackageStore beta = NewStartupStore(root, "beta");
            FileRbxWorldPackageStore shared = NewStartupStore(root, null);
            string fullRoot = Path.GetFullPath(root);
            Assert.AreEqual(Path.Combine(fullRoot, "Startup", "Stores", "alpha"), alpha.StartupDirectoryForTests);
            Assert.AreEqual(Path.Combine(fullRoot, "Startup"), shared.StartupDirectoryForTests);

            Assert.IsTrue((await alpha.SelectStartupAsync(CreateStartupPayload("alpha-world"), "manual", "a")).Success);
            Assert.IsTrue((await shared.SelectStartupAsync(CreateStartupPayload("shared-world"), "manual", "s")).Success);
            Assert.IsTrue((await beta.ClearStartupAsync()).Success);
            Assert.IsTrue((await shared.ClearStartupAsync()).Success);

            RbxWorldStartupSelection alphaInfo = await alpha.ReadStartupInfoAsync();
            RbxWorldStartupSelection betaInfo = await beta.ReadStartupInfoAsync();
            RbxWorldStartupSelection sharedInfo = await shared.ReadStartupInfoAsync();
            Assert.AreEqual(RbxWorldStartupSelectionKind.Package, alphaInfo.Kind);
            Assert.AreEqual("alpha-world", alphaInfo.WorldId);
            Assert.AreEqual(1, alphaInfo.Sequence);
            Assert.AreEqual(RbxWorldStartupSelectionKind.None, betaInfo.Kind);
            Assert.AreEqual(RbxWorldStartupSelectionKind.Default, sharedInfo.Kind);
            Assert.AreEqual(2, sharedInfo.Sequence, "each namespace allocates its own sequence");
            CollectionAssert.AreEqual(
                new[] { "0000000001.json", "0000000001.world" },
                FileNames(alpha.StartupDirectoryForTests),
                "the shared namespace's prune never reaches into another namespace");
        }

        [Test]
        public async Task StartupStore_WriteIsSerializedWithAutosaveRotation()
        {
            StartupDurabilityFileSystem fileSystem = new();
            UniTaskCompletionSource<bool> blockedSync = new();
            int syncCalls = 0;
            string root = Path.Combine(
                Path.GetTempPath(),
                "CoreAI-StartupSerialized-" + Guid.NewGuid().ToString("N"));
            string startupDirectory = Path.Combine(
                Path.GetFullPath(root), "Startup", "Stores", StartupNamespace);
            FileRbxWorldPackageStore store = new(
                root,
                1,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    syncCalls++;
                    if (syncCalls == 2)
                    {
                        return blockedSync.Task;
                    }

                    fileSystem.Commit();
                    return UniTask.FromResult(true);
                },
                () => StartupClockUtc,
                fileSystem,
                StartupNamespace);
            RbxWorldPackageWriteResult older = await store.CreateAutoAsync("older", CreateStartupPayload("older"));
            Assert.IsTrue(older.Success, older.Error);

            UniTask<RbxWorldPackageWriteResult> rotating = store.CreateAutoAsync(
                "newer", CreateStartupPayload("newer"));
            UniTask<RbxWorldPackageWriteResult> selecting = store.SelectStartupAsync(
                CreateStartupPayload("selected"), "manual", "slot");

            Assert.AreEqual(2, syncCalls, "the selection waits while the autosave holds its durability phase");
            CollectionAssert.IsEmpty(
                fileSystem.FileNamesIn(startupDirectory),
                "no startup byte is written before the autosave rotation finished");
            fileSystem.Commit();
            blockedSync.TrySetResult(true);
            RbxWorldPackageWriteResult rotated = await rotating;
            RbxWorldPackageWriteResult selected = await selecting;
            fileSystem.ReloadFromDurable();

            Assert.IsTrue(rotated.Success, rotated.Error);
            Assert.IsTrue(selected.Success, selected.Error);
            Assert.AreEqual(4, syncCalls, "older, newer, its rotation, then the selection");
            CollectionAssert.AreEqual(new[] { Path.GetFileName(rotated.Path) }, store.ListAutoFiles());
            CollectionAssert.AreEqual(
                new[] { "0000000001.json", "0000000001.world" },
                fileSystem.FileNamesIn(startupDirectory));
        }

        /// <summary>
        /// The end-to-end restart: the player confirms a manual load in process A, and process B (a
        /// fresh default world over the same disk) opens exactly that world. This also closes the
        /// positive-confirm gap: a valid request confirmed with true on the real controller applies it.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_ConfirmedManualLoad_RestartRestoresSameTreeAndExactSources()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                ulong keptId;
                byte[] slotBytes;
                using (StartupProcess first = new(disk, "world-a"))
                {
                    keptId = first.AuthorKeptPart().Id.Value;
                    RbxWorldPackageWriteResult saved = await first.Controller.SaveManualAsync(first.Player, "chosen");
                    Assert.IsTrue(saved.Success, saved.Error);
                    slotBytes = File.ReadAllBytes(saved.Path);
                    disk.OpenDefaultSources().Save(
                        StartupExtraModId,
                        "return true",
                        StartupManifest(StartupExtraModId, false));
                    RbxWorldLoadRequest request = await first.Controller.RequestManualLoadAsync(first.Player, "chosen");
                    InstanceRegistry outgoing = first.Controller.CurrentRbxApi.Registry;

                    RbxWorldLoadResult confirmed = await first.Controller.ConfirmManualLoadAsync(request.RequestId, true);

                    Assert.IsTrue(confirmed.Success, confirmed.Error);
                    Assert.AreEqual(1, confirmed.ActiveModsStarted);
                    Assert.IsTrue(confirmed.StartupSelectionPersisted, confirmed.StartupSelectionError);
                    Assert.AreEqual("", confirmed.StartupSelectionError);
                    Assert.AreNotSame(
                        outgoing,
                        first.Controller.CurrentRbxApi.Registry,
                        "a valid request confirmed with true must apply the package");
                    Assert.IsTrue(outgoing.IsDetached);
                    Assert.AreEqual(
                        keptId,
                        first.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild(KeptPartName).Id.Value);
                    CollectionAssert.AreEqual(
                        new[] { "0000000001.json", "0000000001.world" },
                        FileNames(disk.StartupDirectory));
                    CollectionAssert.AreEqual(
                        slotBytes,
                        File.ReadAllBytes(Path.Combine(disk.StartupDirectory, "0000000001.world")),
                        "the startup entry is an exact copy of the confirmed package");
                    CollectionAssert.IsEmpty(first.Diagnostics);
                }

                using StartupProcess second = new(disk, "default-world");
                InstanceRegistry defaultRegistry = second.Controller.CurrentRbxApi.Registry;
                Assert.IsNull(defaultRegistry.WorldRoot.FindFirstChild(KeptPartName), "precondition: B boots the default world");
                IReadOnlyList<string> ringBefore = second.PackageStore.ListAutoFiles();
                int syncsBefore = disk.PackageSyncCalls;
                int defaultRehydrates = 0;

                RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                    second.Controller,
                    () => defaultRehydrates++,
                    second.Diagnostics.Add);

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, outcome, string.Join(" | ", second.Diagnostics));
                Assert.AreEqual(0, defaultRehydrates, "a restored start never rehydrates the default world");
                InstanceRegistry restored = second.Controller.CurrentRbxApi.Registry;
                Assert.AreNotSame(defaultRegistry, restored);
                RbxInstance kept = restored.WorldRoot.FindFirstChild(KeptPartName);
                Assert.IsNotNull(kept);
                Assert.AreEqual(keptId, kept.Id.Value, "the restored tree keeps its InstanceIds");
                Assert.IsTrue(second.Controller.CurrentRbxApi.PartSink.TryGetPartProperties(
                    kept.Id, out PartProperties keptState));
                AssertLiteralKeptPart(in keptState);
                Assert.AreEqual(1, CountNamed(restored, ModStartFolderName), "the active mod started exactly once");
                Assert.AreEqual(0, CountNamed(restored, "DormantStart"), "a dormant mod never runs");
                CollectionAssert.AreEqual(
                    new[] { StartupActiveModId, StartupDormantModId },
                    ModIds(second.Controller.SourceStore.List()),
                    "the live source set is exactly the package's");
                CollectionAssert.AreEqual(
                    new[] { StartupExtraModId, StartupActiveModId, StartupDormantModId },
                    ModIds(disk.OpenDefaultSources().List()),
                    "the default source store is unchanged");
                Assert.AreEqual("world-a", second.Controller.CaptureCurrent().Settings.WorldId);
                second.Controller.CurrentRbxApi.Scheduler.Advance(1d);
                Assert.AreEqual("1", second.ModData.Get(StartupActiveModId, "heartbeat_count"));
                CollectionAssert.AreEqual(ringBefore, second.PackageStore.ListAutoFiles(), "the restore writes no safety autosave");
                Assert.AreEqual(syncsBefore, disk.PackageSyncCalls, "the restore writes nothing to the package store");
                CollectionAssert.IsEmpty(second.Diagnostics);
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_ConfirmedAutosaveLoad_SurvivesTheAutosaveRotatingAway()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                ulong keptId;
                string autosaveName;
                using (StartupProcess first = new(disk, "world-a", autoBackupCapacity: 1))
                {
                    keptId = first.AuthorKeptPart().Id.Value;
                    RbxWorldPackageWriteResult autosave = await first.PackageStore.CreateAutoAsync(
                        "execute_lua", first.Controller.CaptureCurrent());
                    Assert.IsTrue(autosave.Success, autosave.Error);
                    autosaveName = Path.GetFileName(autosave.Path);
                    RbxWorldLoadRequest request = await first.Controller.RequestAutoLoadAsync(first.Player, autosaveName);

                    RbxWorldLoadResult confirmed = await first.Controller.ConfirmManualLoadAsync(request.RequestId, true);

                    Assert.IsTrue(confirmed.Success, confirmed.Error);
                    Assert.IsTrue(confirmed.StartupSelectionPersisted, confirmed.StartupSelectionError);
                    CollectionAssert.DoesNotContain(
                        first.PackageStore.ListAutoFiles(),
                        autosaveName,
                        "precondition: the confirmed autosave was rotated out by the pre-load safety autosave");
                    RbxWorldStartupSelection info = await first.Controller.ReadStartupSelectionAsync();
                    Assert.AreEqual(RbxWorldStartupSelectionKind.Package, info.Kind);
                    Assert.AreEqual("world-a", info.WorldId);
                    Assert.AreEqual("autosave", info.SourceKind);
                    Assert.AreEqual(autosaveName, info.SourceName);
                    Assert.AreEqual(StartupClockUtc, info.SelectedAtUtc.Value);
                }

                using StartupProcess second = new(disk, "default-world", autoBackupCapacity: 1);
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                Assert.AreEqual("world-a", restored.WorldId);
                Assert.AreEqual(1, restored.ActiveModsStarted);
                Assert.AreEqual(
                    keptId,
                    second.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild(KeptPartName).Id.Value);
            });
        }

        /// <summary>
        /// A1-03: after a confirmed load the startup entry stayed the package from request time, while
        /// the AI's later changes lived only in the session (its mod sources in a session version no
        /// start ever read and a later cleanup deleted), so a restart reopened the world without them
        /// although the Hub said it would reopen. Every gated change now records the world as it is as a
        /// new create-once startup entry.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_GatedAiChangesAfterAConfirmedLoad_ReopenAfterRestart()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                ulong keptId;
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    keptId = first.AuthorKeptPart().Id.Value;
                    await ConfirmStartupLoadAsync(first, "chosen");
                    CollectionAssert.AreEqual(
                        new[] { "0000000001.json", "0000000001.world" },
                        FileNames(disk.StartupDirectory),
                        "the confirmed load itself records the selection once");

                    LuaModsLlmTool manageMods = new(
                        first.Controller.Runtime,
                        new StartupToolSettings(),
                        CoreAI.Logging.NullLog.Instance,
                        StartupCapabilities,
                        true,
                        ToolIdentity(),
                        BuiltInAgentRoleIds.Programmer,
                        first.Gate);
                    JObject castle = JObject.Parse(await manageMods.ExecuteAsync(
                        "load",
                        "castle",
                        "local castle = Instance.new('Folder') castle.Name = 'CastleStart' castle.Parent = workspace"));
                    LuaTool.LuaResult marker = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'AfterLoadMarker' "
                        + "marker.Parent = workspace return true",
                        CancellationToken.None);

                    Assert.IsTrue(castle.Value<bool>("success"), castle.ToString());
                    Assert.IsTrue(marker.Success, marker.Error);
                    CollectionAssert.AreEqual(
                        new[] { "0000000003.json", "0000000003.world" },
                        FileNames(disk.StartupDirectory),
                        "each gated change recorded the live world, and the older entries were pruned");
                    RbxWorldStartupSelection info = await first.Controller.ReadStartupSelectionAsync();
                    Assert.AreEqual(RbxWorldStartupSelectionKind.Package, info.Kind);
                    Assert.AreEqual("world-a", info.WorldId);
                    Assert.AreEqual("manual", info.SourceKind);
                    Assert.AreEqual("chosen", info.SourceName);
                    CollectionAssert.IsEmpty(first.Diagnostics);
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                Assert.AreEqual(2, restored.ActiveModsStarted, "the packaged active mod and the castle the AI added");
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.AreEqual(keptId, live.WorldRoot.FindFirstChild(KeptPartName).Id.Value);
                Assert.AreEqual(1, CountNamed(live, "AfterLoadMarker"), "the execute_lua change reopened");
                Assert.AreEqual(1, CountNamed(live, "CastleStart"), "the mod the AI added after the load started again");
                CollectionAssert.AreEqual(
                    new[] { "castle", StartupActiveModId, StartupDormantModId },
                    ModIds(second.Controller.SourceStore.List()));
            });
        }

        /// <summary>
        /// A1-03, a refresh that is not durably confirmed (or a crash before it) leaves the previous
        /// entry, a whole world from one capture, and is reported: the next start opens the world as it
        /// was before that change, never a mix.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_UnconfirmedRefresh_KeepsThePreviousEntryAndReportsIt()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    first.AuthorKeptPart();
                    await ConfirmStartupLoadAsync(first, "chosen");
                    disk.PackageDurability.Enqueue(true);
                    disk.PackageDurability.Enqueue(false);

                    LuaTool.LuaResult marker = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'UnrecordedMarker' "
                        + "marker.Parent = workspace return true",
                        CancellationToken.None);

                    Assert.IsTrue(marker.Success, marker.Error);
                    CollectionAssert.AreEqual(
                        new[] { "0000000001.json", "0000000001.world" },
                        FileNames(disk.StartupDirectory),
                        "the unconfirmed entry was removed and the previous one kept");
                    Assert.AreEqual(1, first.Diagnostics.Count, string.Join(" | ", first.Diagnostics));
                    StringAssert.Contains("was not recorded for the next start", first.Diagnostics[0]);
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.IsNotNull(live.WorldRoot.FindFirstChild(KeptPartName));
                Assert.AreEqual(0, CountNamed(live, "UnrecordedMarker"));
            });
        }

        /// <summary>
        /// A1-03 twin: only the startup world is kept current. After the player chose the default world,
        /// or after a raw host load that never becomes the startup world, gated changes write no
        /// startup entry.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_ChangesToANonStartupWorld_NeverSelectIt()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    first.AuthorKeptPart();
                    await ConfirmStartupLoadAsync(first, "chosen");
                    RbxWorldPackageWriteResult cleared = await first.Controller.ClearStartupSelectionAsync();
                    Assert.IsTrue(cleared.Success, cleared.Error);

                    LuaTool.LuaResult afterClear = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'AfterClear' marker.Parent = workspace",
                        CancellationToken.None);
                    RbxWorldLoadResult raw = await first.Controller.LoadConfirmedAsync(first.Controller.CaptureCurrent());
                    LuaTool.LuaResult afterRaw = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'AfterRaw' marker.Parent = workspace",
                        CancellationToken.None);

                    Assert.IsTrue(afterClear.Success, afterClear.Error);
                    Assert.IsTrue(raw.Success, raw.Error);
                    Assert.IsTrue(afterRaw.Success, afterRaw.Error);
                    CollectionAssert.AreEqual(
                        new[] { "0000000002.default" },
                        FileNames(disk.StartupDirectory),
                        "the player's choice of the default world stands");
                    CollectionAssert.IsEmpty(first.Diagnostics);
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.NotSelected, restored.Outcome, restored.Error);
            });
        }

        /// <summary>
        /// B2-01: the startup restore refuses a world holding an active Full-capability mod (it cannot
        /// be isolated during a staged restore), yet a gated change recorded exactly such a world as the
        /// startup entry, so every later start fell back to the default world without a word. The
        /// refresh now runs the restore's own checks, keeps the previous entry, and says why in the
        /// result of the tool whose change was not recorded.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_ChangeWithAnActiveFullCapabilityMod_KeepsThePreviousEntryAndSaysWhy()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    first.AuthorKeptPart();
                    await ConfirmStartupLoadAsync(first, "chosen");
                    LuaModsLlmTool fullTierTool = new(
                        first.Controller.Runtime,
                        new StartupToolSettings(),
                        CoreAI.Logging.NullLog.Instance,
                        StartupCapabilities | LuaCapabilities.Full,
                        true,
                        ToolIdentity(),
                        BuiltInAgentRoleIds.Programmer,
                        first.Gate);

                    JObject fullLoad = JObject.Parse(await fullTierTool.ExecuteAsync("load", "fullmod", "local x = 1"));
                    LuaTool.LuaResult marker = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'AfterFull' marker.Parent = workspace "
                        + "return 'placed'",
                        CancellationToken.None);
                    LuaTool.LuaResult readOnly = await first.Controller.Executor.ExecuteAsync(
                        "return #workspace:GetChildren()",
                        CancellationToken.None);

                    Assert.IsTrue(fullLoad.Value<bool>("success"), fullLoad.ToString());
                    string loadNote = fullLoad.Value<string>(ConfirmedWorldMutationGate.StartupNoteField);
                    Assert.IsNotNull(loadNote, "the manage_mods result says the change was not recorded: " + fullLoad);
                    StringAssert.Contains("Startup world not updated", loadNote);
                    StringAssert.Contains("'fullmod'", loadNote);
                    Assert.IsTrue(marker.Success, marker.Error);
                    StringAssert.StartsWith("placed", marker.Output, "the chunk's own output stays first");
                    StringAssert.Contains("Startup world not updated", marker.Output,
                        "the execute_lua result says its change was not recorded either");
                    Assert.IsTrue(readOnly.Success, readOnly.Error);
                    StringAssert.DoesNotContain("Startup world not updated", readOnly.Output ?? "",
                        "a call that changed nothing since the last refused state repeats no note");
                    CollectionAssert.AreEqual(
                        new[] { "0000000001.json", "0000000001.world" },
                        FileNames(disk.StartupDirectory),
                        "the entry the player confirmed stays the startup world");
                    Assert.AreEqual(1, first.Diagnostics.Count, string.Join(" | ", first.Diagnostics));
                    StringAssert.Contains("'fullmod'", first.Diagnostics[0], "the refusal is logged once");
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.IsNotNull(live.WorldRoot.FindFirstChild(KeptPartName));
                Assert.AreEqual(0, CountNamed(live, "AfterFull"));
                CollectionAssert.AreEqual(
                    new[] { StartupActiveModId, StartupDormantModId },
                    ModIds(second.Controller.SourceStore.List()));
            });
        }

        /// <summary>
        /// B2-01 twin: once the Full-capability mod is forgotten the startup restore accepts the world
        /// again, so the next change is recorded as before and reopens after a restart.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_FullCapabilityModForgotten_ChangesAreRecordedAgain()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    first.AuthorKeptPart();
                    await ConfirmStartupLoadAsync(first, "chosen");
                    LuaModsLlmTool fullTierTool = new(
                        first.Controller.Runtime,
                        new StartupToolSettings(),
                        CoreAI.Logging.NullLog.Instance,
                        StartupCapabilities | LuaCapabilities.Full,
                        true,
                        ToolIdentity(),
                        BuiltInAgentRoleIds.Programmer,
                        first.Gate);
                    JObject fullLoad = JObject.Parse(await fullTierTool.ExecuteAsync("load", "fullmod", "local x = 1"));
                    JObject forgotten = JObject.Parse(await fullTierTool.ExecuteAsync("forget", "fullmod"));
                    LuaTool.LuaResult marker = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'AfterForget' marker.Parent = workspace",
                        CancellationToken.None);

                    Assert.IsTrue(fullLoad.Value<bool>("success"), fullLoad.ToString());
                    Assert.IsTrue(forgotten.Value<bool>("success"), forgotten.ToString());
                    Assert.IsNull(forgotten[ConfirmedWorldMutationGate.StartupNoteField], forgotten.ToString());
                    Assert.IsTrue(marker.Success, marker.Error);
                    StringAssert.DoesNotContain("Startup world not updated", marker.Output ?? "");
                    List<string> entries = FileNames(disk.StartupDirectory);
                    Assert.AreEqual(2, entries.Count, string.Join(", ", entries));
                    CollectionAssert.DoesNotContain(entries, "0000000001.world", "a newer entry was recorded");
                    Assert.AreEqual(1, first.Diagnostics.Count, "only the load of the Full mod was refused: "
                                                                + string.Join(" | ", first.Diagnostics));
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                Assert.AreEqual(1, CountNamed(second.Controller.CurrentRbxApi.Registry, "AfterForget"));
                CollectionAssert.AreEqual(
                    new[] { StartupActiveModId, StartupDormantModId },
                    ModIds(second.Controller.SourceStore.List()));
            });
        }

        /// <summary>
        /// B2-02: a mod change made without the shared gate on the startup world (the Hub Mods page and
        /// editor, host code calling the runtime facade) never reached the startup selection, so the
        /// player's own edits were gone after a restart. Every write to the world's mod sources now
        /// marks the startup world, and the next frame records the finished changes once.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_HubAndHostModChangesOnTheStartupWorld_ReopenAfterRestart()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    first.AuthorKeptPart();
                    await ConfirmStartupLoadAsync(first, "chosen");
                    ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                        .GetActorContext(BuiltInAgentRoleIds.Programmer);
                    CoreAI.Ai.Hub.LuaCsModRuntimeHubService hub = new(
                        first.Controller.Runtime,
                        host,
                        first.Controller.SourceStore,
                        StartupCapabilities);

                    first.Controller.Runtime.LoadMod(
                        host,
                        "hostmod",
                        "local f = Instance.new('Folder') f.Name = 'HostModStart' f.Parent = workspace",
                        StartupCapabilities);
                    hub.SaveOrReload(
                        "hubedit",
                        "--[[@coreai\ncapabilities: Read, WorldEdit\n]]\n"
                        + "local f = Instance.new('Folder') f.Name = 'HubEditStart' f.Parent = workspace");
                    hub.Disable(StartupActiveModId);
                    CollectionAssert.AreEqual(
                        new[] { "0000000001.json", "0000000001.world" },
                        FileNames(disk.StartupDirectory),
                        "nothing is recorded in the middle of a change");

                    first.Controller.PumpFrame(host, 1f / 60f);
                    await first.Controller.StartupRefreshForTests;

                    CollectionAssert.AreEqual(
                        new[] { "0000000002.json", "0000000002.world" },
                        FileNames(disk.StartupDirectory),
                        "one frame records all of the frame's changes as one entry");
                    first.Controller.PumpFrame(host, 1f / 60f);
                    await first.Controller.StartupRefreshForTests;
                    CollectionAssert.AreEqual(
                        new[] { "0000000002.json", "0000000002.world" },
                        FileNames(disk.StartupDirectory),
                        "a frame without a change records nothing");
                    CollectionAssert.IsEmpty(first.Diagnostics);
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                Assert.AreEqual(2, restored.ActiveModsStarted, "the host's mod and the Hub edit; the disabled mod stays dormant");
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.AreEqual(1, CountNamed(live, "HostModStart"));
                Assert.AreEqual(1, CountNamed(live, "HubEditStart"));
                Assert.AreEqual(0, CountNamed(live, ModStartFolderName), "the mod disabled on the Hub did not start");
                CollectionAssert.AreEqual(
                    new[] { "hostmod", "hubedit", StartupActiveModId, StartupDormantModId },
                    ModIds(second.Controller.SourceStore.List()));
            });
        }

        /// <summary>
        /// B2-02 twin: a gated change refreshes the selection itself before it lets the gate go, so the
        /// source writes it made are not recorded a second time on the next frame.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_GatedModLoad_IsRecordedOnce_NotAgainOnTheNextFrame()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using StartupProcess first = new(disk, "world-a", withMutationGate: true);
                first.AuthorKeptPart();
                await ConfirmStartupLoadAsync(first, "chosen");
                LuaModsLlmTool manageMods = new(
                    first.Controller.Runtime,
                    new StartupToolSettings(),
                    CoreAI.Logging.NullLog.Instance,
                    StartupCapabilities,
                    true,
                    ToolIdentity(),
                    BuiltInAgentRoleIds.Programmer,
                    first.Gate);

                JObject castle = JObject.Parse(await manageMods.ExecuteAsync(
                    "load",
                    "castle",
                    "local castle = Instance.new('Folder') castle.Name = 'CastleStart' castle.Parent = workspace"));
                first.Controller.PumpFrame(
                    CoreServicesInstaller.DefaultLocalHostIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer),
                    1f / 60f);
                await first.Controller.StartupRefreshForTests;

                Assert.IsTrue(castle.Value<bool>("success"), castle.ToString());
                CollectionAssert.AreEqual(
                    new[] { "0000000002.json", "0000000002.world" },
                    FileNames(disk.StartupDirectory));
                CollectionAssert.IsEmpty(first.Diagnostics);
            });
        }

        /// <summary>
        /// B2-09: every gated call, a read-only execute_lua included, recorded a whole new startup
        /// entry (capture, encode, write, sync, prune) while it held the gate. A world that did not
        /// change since its entry is not written again; a change is written exactly once.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_UnchangedWorldWritesNoEntry_AChangeWritesExactlyOne()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a", withMutationGate: true))
                {
                    first.AuthorKeptPart();
                    await ConfirmStartupLoadAsync(first, "chosen");
                    LuaTool.LuaResult changed = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'FirstChange' marker.Parent = workspace",
                        CancellationToken.None);
                    Dictionary<string, byte[]> afterChange = FileContents(disk.StartupDirectory);

                    LuaTool.LuaResult readOnly = await first.Controller.Executor.ExecuteAsync(
                        "return #workspace:GetChildren()",
                        CancellationToken.None);
                    LuaTool.LuaResult readOnlyAgain = await first.Controller.Executor.ExecuteAsync(
                        "local count = 0 for _ in pairs(workspace:GetChildren()) do count = count + 1 end return count",
                        CancellationToken.None);

                    Assert.IsTrue(changed.Success, changed.Error);
                    Assert.IsTrue(readOnly.Success, readOnly.Error);
                    Assert.IsTrue(readOnlyAgain.Success, readOnlyAgain.Error);
                    CollectionAssert.AreEqual(
                        new[] { "0000000002.json", "0000000002.world" },
                        FileNames(disk.StartupDirectory),
                        "the change was recorded once");
                    AssertSameFiles(afterChange, FileContents(disk.StartupDirectory));

                    LuaTool.LuaResult changedAgain = await first.Controller.Executor.ExecuteAsync(
                        "local marker = Instance.new('Folder') marker.Name = 'SecondChange' marker.Parent = workspace",
                        CancellationToken.None);

                    Assert.IsTrue(changedAgain.Success, changedAgain.Error);
                    CollectionAssert.AreEqual(
                        new[] { "0000000003.json", "0000000003.world" },
                        FileNames(disk.StartupDirectory),
                        "the next change is recorded exactly once");
                    CollectionAssert.IsEmpty(first.Diagnostics);
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.AreEqual(1, CountNamed(live, "FirstChange"));
                Assert.AreEqual(1, CountNamed(live, "SecondChange"));
            });
        }

        /// <summary>Saves the live world to <paramref name="slot"/> and confirms loading it, which records it for the next start.</summary>
        private static async UniTask ConfirmStartupLoadAsync(StartupProcess process, string slot)
        {
            RbxWorldPackageWriteResult saved = await process.Controller.SaveManualAsync(process.Player, slot);
            Assert.IsTrue(saved.Success, saved.Error);
            RbxWorldLoadRequest request = await process.Controller.RequestManualLoadAsync(process.Player, slot);
            RbxWorldLoadResult confirmed = await process.Controller.ConfirmManualLoadAsync(request.RequestId, true);
            Assert.IsTrue(confirmed.Success, confirmed.Error);
            Assert.IsTrue(confirmed.StartupSelectionPersisted, confirmed.StartupSelectionError);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_RejectedExpiredAndPendingRequests_DoNotChangeStartup_AndDieWithTheProcess()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                string pendingRequestId;
                using (StartupProcess first = new(disk, "world-a"))
                {
                    first.AuthorKeptPart();
                    Assert.IsTrue((await first.Controller.SaveManualAsync(first.Player, "chosen")).Success);
                    first.AddFolder("OnlyInOther");
                    Assert.IsTrue((await first.Controller.SaveManualAsync(first.Player, "other")).Success);
                    RbxWorldLoadRequest chosen = await first.Controller.RequestManualLoadAsync(first.Player, "chosen");
                    Assert.IsTrue((await first.Controller.ConfirmManualLoadAsync(chosen.RequestId, true)).Success);
                    Dictionary<string, byte[]> selectionBefore = FileContents(disk.StartupDirectory);

                    RbxWorldLoadRequest rejected = await first.Controller.RequestManualLoadAsync(first.Player, "other");
                    RbxWorldLoadResult rejectedResult = await first.Controller.ConfirmManualLoadAsync(rejected.RequestId, false);
                    RbxWorldLoadRequest expiring = await first.Controller.RequestManualLoadAsync(first.Player, "other");
                    first.Clock = first.Clock.AddMinutes(3d);
                    RbxWorldLoadResult expiredResult = await first.Controller.ConfirmManualLoadAsync(expiring.RequestId, true);
                    RbxWorldLoadRequest pending = await first.Controller.RequestManualLoadAsync(first.Player, "other");
                    pendingRequestId = pending.RequestId;

                    Assert.IsFalse(rejectedResult.Success);
                    Assert.IsFalse(rejectedResult.StartupSelectionPersisted);
                    Assert.IsFalse(expiredResult.Success);
                    StringAssert.Contains("expired", expiredResult.Error);
                    Assert.AreEqual(1, first.Controller.GetPendingManualLoads().Count);
                    AssertSameFiles(selectionBefore, FileContents(disk.StartupDirectory));
                }

                using StartupProcess second = new(disk, "default-world");
                InstanceRegistry defaultRegistry = second.Controller.CurrentRbxApi.Registry;

                Assert.AreEqual(0, second.Controller.GetPendingManualLoads().Count, "pending requests die with the process");
                RbxWorldLoadResult stale = await second.Controller.ConfirmManualLoadAsync(pendingRequestId, true);
                Assert.IsFalse(stale.Success);
                Assert.AreSame(defaultRegistry, second.Controller.CurrentRbxApi.Registry);

                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.IsNotNull(live.WorldRoot.FindFirstChild(KeptPartName));
                Assert.IsNull(live.WorldRoot.FindFirstChild("OnlyInOther"), "only the confirmed world opens");
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_SaveWorldAndRawHostLoad_DoNotChangeStartup()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                using StartupProcess process = new(disk, "world-a");
                process.AuthorKeptPart();
                Assert.IsTrue((await process.Controller.SaveManualAsync(process.Player, "chosen")).Success);
                RbxWorldLoadRequest request = await process.Controller.RequestManualLoadAsync(process.Player, "chosen");
                Assert.IsTrue((await process.Controller.ConfirmManualLoadAsync(request.RequestId, true)).Success);
                Dictionary<string, byte[]> selectionBefore = FileContents(disk.StartupDirectory);
                process.AddFolder("AfterTheLoad");

                JObject saved = JObject.Parse(await new SaveWorldLlmTool(
                    process.Controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer).ExecuteAsync("after-load"));
                RbxWorldLoadResult rawLoad = await process.Controller.LoadConfirmedAsync(process.Controller.CaptureCurrent());

                Assert.IsTrue((bool)saved["success"], saved.ToString());
                Assert.IsTrue(rawLoad.Success, rawLoad.Error);
                Assert.IsFalse(rawLoad.StartupSelectionPersisted, "the trusted host load never records a startup selection");
                AssertSameFiles(selectionBefore, FileContents(disk.StartupDirectory));
                CollectionAssert.AreEqual(new[] { "after-load", "chosen" }, process.PackageStore.ListManualSlots());
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupRestore_CorruptOversizedOrMissingEntry_KeepsDefaultWorld_NeverThrows()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                using (StartupProcess first = new(disk, "world-a"))
                {
                    first.AuthorKeptPart();
                    Assert.IsTrue((await first.Controller.SaveManualAsync(first.Player, "chosen")).Success);
                    RbxWorldLoadRequest request = await first.Controller.RequestManualLoadAsync(first.Player, "chosen");
                    Assert.IsTrue((await first.Controller.ConfirmManualLoadAsync(request.RequestId, true)).Success);
                }

                string corruptPath = Path.Combine(disk.StartupDirectory, "0000000002.world");
                File.WriteAllBytes(corruptPath, System.Text.Encoding.UTF8.GetBytes("truncated"));
                using (StartupProcess corrupt = new(disk, "default-world"))
                {
                    InstanceRegistry defaultRegistry = corrupt.Controller.CurrentRbxApi.Registry;
                    int rehydrates = 0;

                    RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                        corrupt.Controller, () => rehydrates++, corrupt.Diagnostics.Add);

                    Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, outcome);
                    Assert.AreEqual(1, rehydrates, "a failed restore still rehydrates the default world");
                    Assert.AreSame(defaultRegistry, corrupt.Controller.CurrentRbxApi.Registry);
                    Assert.IsNull(defaultRegistry.WorldRoot.FindFirstChild(KeptPartName), "an older entry is never walked back to");
                    Assert.AreEqual(1, corrupt.Diagnostics.Count, string.Join(" | ", corrupt.Diagnostics));
                    StringAssert.Contains("#2", corrupt.Diagnostics[0]);
                    StringAssert.Contains("default world stays live", corrupt.Diagnostics[0]);
                    StringAssert.Contains("is not a loadable world package", corrupt.Diagnostics[0]);
                    StringAssert.Contains("Start with the default world next time", corrupt.Diagnostics[0]);
                    Assert.IsTrue(File.Exists(corruptPath), "the selection is kept for inspection, never auto-cleared");
                }

                using (FileStream stream = new(
                           Path.Combine(disk.StartupDirectory, "0000000003.world"),
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.SetLength((long)RbxWorldPackageSerializer.MaximumPackageBytes + 1L);
                }

                using (StartupProcess oversized = new(disk, "default-world"))
                {
                    RbxWorldStartupRestoreResult result = await oversized.Controller.RestoreStartupSelectionAsync();

                    Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, result.Outcome);
                    Assert.AreEqual(3, result.Sequence);
                    StringAssert.Contains("format version 1 limit", result.Error);
                    Assert.AreEqual(1, oversized.Diagnostics.Count);
                }

                foreach (string entry in Directory.GetFiles(disk.StartupDirectory, "*.world"))
                {
                    File.Delete(entry);
                }

                using StartupProcess missing = new(disk, "default-world");
                int missingRehydrates = 0;

                RbxWorldStartupRestoreOutcome missingOutcome = await RbxWorldStartupSequence.RunAsync(
                    missing.Controller, () => missingRehydrates++, missing.Diagnostics.Add);

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.NotSelected, missingOutcome);
                Assert.AreEqual(1, missingRehydrates);
                CollectionAssert.IsEmpty(missing.Diagnostics, "a leftover metadata file is not a selection");
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupRestore_EntryVanishedAfterListing_FallsBackWithDiagnostic_NeverThrows()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                StartupDurabilityFileSystem packageFiles = new();
                using (StartupProcess first = new(disk, "world-a", packageFileSystem: packageFiles))
                {
                    first.AuthorKeptPart();
                    Assert.IsTrue((await first.PackageStore.SelectStartupAsync(
                        first.Controller.CaptureCurrent(), "manual", "planted")).Success);
                }

                packageFiles.Vanish(Path.Combine(disk.StartupDirectory, "0000000001.world"));
                using StartupProcess second = new(disk, "default-world", packageFileSystem: packageFiles);
                InstanceRegistry defaultRegistry = second.Controller.CurrentRbxApi.Registry;
                int rehydrates = 0;

                RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                    second.Controller, () => rehydrates++, second.Diagnostics.Add);

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, outcome);
                Assert.AreEqual(1, rehydrates);
                Assert.AreSame(defaultRegistry, second.Controller.CurrentRbxApi.Registry);
                Assert.AreEqual(1, second.Diagnostics.Count, string.Join(" | ", second.Diagnostics));
                StringAssert.Contains("#1", second.Diagnostics[0]);
                StringAssert.Contains("'world-a'", second.Diagnostics[0], "the metadata still names the world");
                StringAssert.Contains("disappeared before it could be read", second.Diagnostics[0]);
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupRestore_AclDowngradeSourceDurabilityOrStageFailure_FallsBackToDefaultWorld()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk legacyDisk = NewStartupDisk();
                using (StartupProcess legacy = new(legacyDisk, "legacy-world"))
                {
                    legacy.AuthorKeptPart();
                    RbxWorldPackagePayload legacyPayload = legacy.Controller.CaptureCurrent();
                    Assert.IsNull(legacyPayload.Tree.WorldAclVersion, "precondition: a legacy package");
                    Assert.IsTrue((await legacy.PackageStore.SelectStartupAsync(
                        legacyPayload, "manual", "legacy")).Success);
                }

                using (StartupProcess aclSession = new(
                           legacyDisk, "acl-default", worldAclVersion: InstanceRegistry.CurrentWorldAclVersion))
                {
                    RbxWorldStartupRestoreResult refused = await aclSession.Controller.RestoreStartupSelectionAsync();

                    Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, refused.Outcome);
                    StringAssert.Contains("compose the session with worldAclVersion: null", refused.Error);
                    Assert.AreSame(aclSession.Registry, aclSession.Controller.CurrentRbxApi.Registry);
                    Assert.AreEqual(1, aclSession.Diagnostics.Count);
                }

                using (StartupProcess undurable = new(legacyDisk, "default-world", sourcesDurable: false))
                {
                    RbxWorldStartupRestoreResult refused = await undurable.Controller.RestoreStartupSelectionAsync();

                    Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, refused.Outcome);
                    StringAssert.Contains("durable persistence was not confirmed", refused.Error);
                    Assert.AreSame(undurable.Registry, undurable.Controller.CurrentRbxApi.Registry);
                    Assert.AreEqual(1, undurable.Diagnostics.Count);
                }

                StartupDisk failingDisk = NewStartupDisk();
                using (StartupProcess author = new(failingDisk, "world-a"))
                {
                    author.AuthorKeptPart();
                    RbxWorldPackagePayload captured = author.Controller.CaptureCurrent();
                    RbxWorldPackagePayload failingStart = new(
                        captured.CapturedAtUtc,
                        captured.Settings,
                        captured.Tree,
                        captured.Parts,
                        captured.CameraCFrame,
                        new[]
                        {
                            new RbxWorldModSource(
                                StartupManifest("failing-start", true),
                                "error('injected startup failure')")
                        });
                    Assert.IsTrue((await author.PackageStore.SelectStartupAsync(
                        failingStart, "manual", "failing")).Success);
                }

                using StartupProcess staged = new(failingDisk, "default-world");
                RbxWorldStartupRestoreResult failed = await staged.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, failed.Outcome);
                Assert.AreSame(staged.Registry, staged.Controller.CurrentRbxApi.Registry);
                Assert.IsNull(staged.Registry.WorldRoot.FindFirstChild(KeptPartName));
                Assert.AreEqual(1, staged.Diagnostics.Count, string.Join(" | ", staged.Diagnostics));
                CollectionAssert.AreEqual(
                    new[] { "0000000001.json", "0000000001.world" },
                    FileNames(failingDisk.StartupDirectory),
                    "a failed restore never clears the selection");
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_DurabilityFalse_LoadStaysPublished_FlagFalse_RestartBootsPrevious()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SeedStartupMods(disk);
                using (StartupProcess first = new(disk, "world-a"))
                {
                    first.AuthorKeptPart();
                    Assert.IsTrue((await first.Controller.SaveManualAsync(first.Player, "first")).Success);
                    RbxWorldLoadRequest firstRequest = await first.Controller.RequestManualLoadAsync(first.Player, "first");
                    Assert.IsTrue((await first.Controller.ConfirmManualLoadAsync(firstRequest.RequestId, true)).Success);
                    first.AddFolder("OnlyInSecond");
                    Assert.IsTrue((await first.Controller.SaveManualAsync(first.Player, "second")).Success);
                    RbxWorldLoadRequest secondRequest = await first.Controller.RequestManualLoadAsync(first.Player, "second");
                    // WHY three answers: the pre-load safety autosave, the selection write, then the cleanup
                    // of the refused selection; the ring is below capacity, so no rotation asks in between.
                    disk.PackageDurability.Enqueue(true);
                    disk.PackageDurability.Enqueue(false);
                    disk.PackageDurability.Enqueue(true);

                    RbxWorldLoadResult loaded = await first.Controller.ConfirmManualLoadAsync(secondRequest.RequestId, true);

                    Assert.IsTrue(loaded.Success, loaded.Error);
                    Assert.AreEqual(1, loaded.ActiveModsStarted);
                    Assert.IsFalse(loaded.StartupSelectionPersisted);
                    StringAssert.Contains("durable persistence was not confirmed", loaded.StartupSelectionError);
                    Assert.AreEqual(0, disk.PackageDurability.Count, "the durability script matched the write order");
                    Assert.IsNotNull(
                        first.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("OnlyInSecond"),
                        "a failed selection write never rolls the live world back");
                    Assert.AreEqual(1, first.Diagnostics.Count, string.Join(" | ", first.Diagnostics));
                    StringAssert.Contains("not recorded as the world that opens on the next start", first.Diagnostics[0]);
                    CollectionAssert.AreEqual(
                        new[] { "0000000001.json", "0000000001.world" },
                        FileNames(disk.StartupDirectory));
                }

                using StartupProcess second = new(disk, "default-world");
                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                InstanceRegistry live = second.Controller.CurrentRbxApi.Registry;
                Assert.IsNotNull(live.WorldRoot.FindFirstChild(KeptPartName));
                Assert.IsNull(live.WorldRoot.FindFirstChild("OnlyInSecond"), "the previous selection opens");
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupRestore_WritesNoSafetyAutosave()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                using (StartupProcess first = new(disk, "world-a"))
                {
                    first.AuthorKeptPart();
                    Assert.IsTrue((await first.PackageStore.SelectStartupAsync(
                        first.Controller.CaptureCurrent(), "manual", "planted")).Success);
                }

                using StartupProcess second = new(disk, "default-world");
                int syncsBefore = disk.PackageSyncCalls;

                RbxWorldStartupRestoreResult restored = await second.Controller.RestoreStartupSelectionAsync();

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
                CollectionAssert.IsEmpty(second.PackageStore.ListAutoFiles(), "no load_world-pre autosave at boot");
                Assert.AreEqual(syncsBefore, disk.PackageSyncCalls);
                Assert.IsNotNull(second.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild(KeptPartName));
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupRestore_ActiveFullCapabilityMod_FallsBackToDefaultWorld()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                using (StartupProcess first = new(disk, "world-a"))
                {
                    first.AuthorKeptPart();
                    RbxWorldPackagePayload captured = first.Controller.CaptureCurrent();
                    RbxWorldPackagePayload fullMod = new(
                        captured.CapturedAtUtc,
                        captured.Settings,
                        captured.Tree,
                        captured.Parts,
                        captured.CameraCFrame,
                        new[]
                        {
                            new RbxWorldModSource(
                                StartupManifest("full-mod", true, LuaCapabilities.Read | LuaCapabilities.Full),
                                "return true")
                        });
                    Assert.IsTrue((await first.PackageStore.SelectStartupAsync(fullMod, "manual", "planted")).Success);
                }

                using StartupProcess second = new(disk, "default-world");
                InstanceRegistry defaultRegistry = second.Controller.CurrentRbxApi.Registry;
                int rehydrates = 0;

                RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                    second.Controller, () => rehydrates++, second.Diagnostics.Add);

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, outcome);
                Assert.AreEqual(1, rehydrates);
                Assert.AreSame(defaultRegistry, second.Controller.CurrentRbxApi.Registry);
                Assert.AreEqual(1, second.Diagnostics.Count, string.Join(" | ", second.Diagnostics));
                StringAssert.Contains("Full-capability mod 'full-mod'", second.Diagnostics[0]);
                Assert.IsFalse(
                    Directory.Exists(Path.Combine(disk.ModsRoot, ".world-sessions")),
                    "the refusal comes before any source version is prepared");
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StartupSelection_Clear_RestartBootsDefaultWorld()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                using (StartupProcess first = new(disk, "world-a"))
                {
                    first.AuthorKeptPart();
                    Assert.IsTrue((await first.Controller.SaveManualAsync(first.Player, "chosen")).Success);
                    RbxWorldLoadRequest request = await first.Controller.RequestManualLoadAsync(first.Player, "chosen");
                    Assert.IsTrue((await first.Controller.ConfirmManualLoadAsync(request.RequestId, true)).Success);
                    InstanceRegistry live = first.Controller.CurrentRbxApi.Registry;

                    RbxWorldPackageWriteResult cleared = await first.Controller.ClearStartupSelectionAsync();

                    Assert.IsTrue(cleared.Success, cleared.Error);
                    Assert.AreSame(live, first.Controller.CurrentRbxApi.Registry, "clearing never changes the live world");
                    Assert.AreEqual(
                        RbxWorldStartupSelectionKind.Default,
                        (await first.Controller.ReadStartupSelectionAsync()).Kind);
                    CollectionAssert.AreEqual(new[] { "0000000002.default" }, FileNames(disk.StartupDirectory));
                }

                using StartupProcess second = new(disk, "default-world");
                int rehydrates = 0;

                RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                    second.Controller, () => rehydrates++, second.Diagnostics.Add);

                Assert.AreEqual(RbxWorldStartupRestoreOutcome.NotSelected, outcome);
                Assert.AreEqual(1, rehydrates);
                Assert.IsNull(second.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild(KeptPartName));
                CollectionAssert.IsEmpty(second.Diagnostics);
            });
        }

        [TestCase(RbxWorldStartupRestoreOutcome.NotSelected, true)]
        [TestCase(RbxWorldStartupRestoreOutcome.FellBack, true)]
        [TestCase(RbxWorldStartupRestoreOutcome.Restored, false)]
        public async Task StartupSequence_RestoresFirst_AndRehydratesTheDefaultWorldUnlessRestored(
            RbxWorldStartupRestoreOutcome restoreOutcome,
            bool rehydratesDefault)
        {
            List<string> calls = new();
            List<string> diagnostics = new();

            RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                new ScriptedStartupSelection(calls, restoreOutcome),
                () => calls.Add("rehydrate"),
                diagnostics.Add);

            Assert.AreEqual(restoreOutcome, outcome);
            CollectionAssert.AreEqual(
                rehydratesDefault ? new[] { "restore", "rehydrate" } : new[] { "restore" },
                calls);
            CollectionAssert.IsEmpty(diagnostics);
        }

        [Test]
        public async Task StartupSequence_ThrowingRestoreOrRehydrate_IsReportedAndNeverThrows()
        {
            List<string> calls = new();
            List<string> diagnostics = new();

            RbxWorldStartupRestoreOutcome outcome = await RbxWorldStartupSequence.RunAsync(
                new ScriptedStartupSelection(calls, null),
                () =>
                {
                    calls.Add("rehydrate");
                    throw new InvalidOperationException("Injected rehydrate failure.");
                },
                diagnostics.Add);

            Assert.AreEqual(RbxWorldStartupRestoreOutcome.FellBack, outcome);
            CollectionAssert.AreEqual(new[] { "restore", "rehydrate" }, calls);
            Assert.AreEqual(2, diagnostics.Count, string.Join(" | ", diagnostics));
            StringAssert.Contains("Injected restore failure.", diagnostics[0]);
            StringAssert.Contains("Injected rehydrate failure.", diagnostics[1]);

            List<string> withoutSelection = new();
            RbxWorldStartupRestoreOutcome noSelection = await RbxWorldStartupSequence.RunAsync(
                null,
                () => withoutSelection.Add("rehydrate"),
                diagnostics.Add);

            Assert.AreEqual(RbxWorldStartupRestoreOutcome.NotSelected, noSelection);
            CollectionAssert.AreEqual(new[] { "rehydrate" }, withoutSelection);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator WorldLoadRequest_AclComposedSession_RefusesLegacyPackageBeforeAskingThePlayer()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                using StartupProcess process = new(
                    disk, "acl-world", worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
                RbxWorldPackagePayload legacy = CreateMinimalPayload(CapturedAtUtc);
                Assert.IsNull(legacy.Tree.WorldAclVersion, "precondition: a legacy package");
                Assert.IsTrue((await process.PackageStore.CreateManualAsync("legacy-slot", legacy)).Success);
                RbxWorldPackageWriteResult legacyAuto = await process.PackageStore.CreateAutoAsync("execute_lua", legacy);
                Assert.IsTrue(legacyAuto.Success, legacyAuto.Error);
                int confirmationEvents = 0;
                process.Controller.ManualLoadConfirmationRequested += _ => confirmationEvents++;

                RbxWorldLoadRefusedException manual = await CatchRefusal(async () =>
                    await process.Controller.RequestManualLoadAsync(process.Player, "legacy-slot"));
                RbxWorldLoadRefusedException auto = await CatchRefusal(async () =>
                    await process.Controller.RequestAutoLoadAsync(process.Player, Path.GetFileName(legacyAuto.Path)));
                JObject tool = JObject.Parse(await new LoadWorldLlmTool(
                    process.Controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer).ExecuteAsync("legacy-slot"));

                Assert.AreEqual("invalid_package", manual.Status);
                StringAssert.Contains("compose the session with worldAclVersion: null", manual.Message);
                Assert.AreEqual("invalid_package", auto.Status);
                AssertUnloadableAutosave(tool, "legacy-slot", "invalid_package");
                StringAssert.Contains("per-actor access control", (string)tool["error"]);
                Assert.AreEqual(0, process.Controller.GetPendingManualLoads().Count);
                Assert.AreEqual(0, confirmationEvents, "the player is never asked to confirm a package that would be refused");

                Assert.IsTrue((await process.PackageStore.CreateManualAsync(
                    "acl-slot", process.Controller.CaptureCurrent())).Success);
                RbxWorldLoadRequest accepted = await process.Controller.RequestManualLoadAsync(process.Player, "acl-slot");

                Assert.IsTrue(accepted.PlayerConfirmationRequired);
                Assert.AreEqual(1, process.Controller.GetPendingManualLoads().Count);
                Assert.AreEqual(1, confirmationEvents);
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator WorldLoad_LiveNetworkSessions_AreRefusedAtRequestConfirmAndRawLoad()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                SessionBridge bridge = new(RbxNetworkTopology.Host);
                using StartupProcess process = new(disk, "networked-world", networkBridge: bridge);
                process.AuthorKeptPart();
                Assert.IsTrue((await process.Controller.SaveManualAsync(process.Player, "chosen")).Success);
                RbxWorldLoadRequest beforeJoin = await process.Controller.RequestManualLoadAsync(process.Player, "chosen");
                bridge.RegisterActor("remote-client");
                InstanceRegistry live = process.Controller.CurrentRbxApi.Registry;

                RbxWorldLoadResult confirmed = await process.Controller.ConfirmManualLoadAsync(beforeJoin.RequestId, true);
                RbxWorldLoadRefusedException manual = await CatchRefusal(async () =>
                    await process.Controller.RequestManualLoadAsync(process.Player, "chosen"));
                RbxWorldLoadRefusedException auto = await CatchRefusal(async () =>
                    await process.Controller.RequestAutoLoadAsync(process.Player, ValidAutoName));
                RbxWorldLoadResult raw = await process.Controller.LoadConfirmedAsync(process.Controller.CaptureCurrent());
                JObject tool = JObject.Parse(await new LoadWorldLlmTool(
                    process.Controller, ToolIdentity(), BuiltInAgentRoleIds.Programmer).ExecuteAsync("chosen"));

                Assert.IsFalse(confirmed.Success);
                StringAssert.Contains("MVP11 session handoff", confirmed.Error);
                StringAssert.Contains("disconnect clients or stop the server first", confirmed.Error);
                Assert.IsFalse(confirmed.StartupSelectionPersisted);
                Assert.AreSame(live, process.Controller.CurrentRbxApi.Registry, "the live world is not changed");
                Assert.IsFalse(Directory.Exists(disk.StartupDirectory), "a refused load records no startup selection");
                Assert.AreEqual("network_sessions_active", manual.Status);
                StringAssert.Contains("MVP11 session handoff", manual.Message);
                Assert.AreEqual("network_sessions_active", auto.Status);
                Assert.IsFalse(raw.Success);
                StringAssert.Contains("MVP11 session handoff", raw.Error);
                AssertUnloadableAutosave(tool, "chosen", "network_sessions_active");
                Assert.AreEqual(0, process.Controller.GetPendingManualLoads().Count);

                bridge.UnregisterActor("remote-client");
                RbxWorldLoadRequest afterLeave = await process.Controller.RequestManualLoadAsync(process.Player, "chosen");
                RbxWorldLoadResult loaded = await process.Controller.ConfirmManualLoadAsync(afterLeave.RequestId, true);

                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.AreNotSame(live, process.Controller.CurrentRbxApi.Registry);
            });
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator WorldLoad_LoopbackActors_DoNotBlockALoad()
        {
            return UniTask.ToCoroutine(async () =>
            {
                StartupDisk disk = NewStartupDisk();
                NullNetworkBridge loopback = new();
                loopback.RegisterActor("local-player");
                using StartupProcess process = new(disk, "solo-world", networkBridge: loopback);
                process.AuthorKeptPart();
                Assert.IsTrue((await process.Controller.SaveManualAsync(process.Player, "chosen")).Success);

                RbxWorldLoadRequest request = await process.Controller.RequestManualLoadAsync(process.Player, "chosen");
                RbxWorldLoadResult loaded = await process.Controller.ConfirmManualLoadAsync(request.RequestId, true);

                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.IsTrue(loaded.StartupSelectionPersisted, loaded.StartupSelectionError);
            });
        }

        [Test]
        public void PumpFrame_TickThrows_AdvanceStillRuns_AndTheFaultIsReportedOnce()
        {
            StartupDisk disk = NewStartupDisk();
            using StartupProcess process = new(disk, "pump-world");
            double before = process.Controller.CurrentRbxApi.Scheduler.CurrentTime;

            Assert.DoesNotThrow(() => process.Controller.PumpFrame(process.Player, 0.25f));
            Assert.DoesNotThrow(() => process.Controller.PumpFrame(process.Player, 0.25f));

            Assert.AreEqual(
                before + 0.5d,
                process.Controller.CurrentRbxApi.Scheduler.CurrentTime,
                1e-9,
                "the scheduler advanced on both frames although the runtime Tick threw");
            Assert.AreEqual(1, process.Diagnostics.Count, string.Join(" | ", process.Diagnostics));
            StringAssert.Contains("mod runtime Tick", process.Diagnostics[0]);
            StringAssert.Contains("requires unrestricted host authority", process.Diagnostics[0]);
        }

        [Test]
        public void PumpFrame_AdvanceThrows_TickStillDeliversQueuedModEvents()
        {
            StartupDisk disk = NewStartupDisk();
            using StartupProcess process = new(disk, "pump-world");
            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            process.Controller.Runtime.LoadMod(
                host,
                "tick-probe",
                "hooks_on('probe', function() store_set('ticked', 'yes') end)",
                StartupCapabilities,
                persistToStore: false);
            process.Controller.Runtime.EmitEvent(host, "probe");
            Assert.AreEqual("", process.ModData.Get("tick-probe", "ticked"), "precondition: Tick delivers the event");
            process.Controller.CurrentRbxApi.Scheduler.PhaseReached +=
                (phase, deltaSeconds) => throw new InvalidOperationException("Injected phase fault.");
            // WHY a rethrowing observer too: Advance rethrows a contained host fault after its frame
            // whenever an observer fails, so the fault reaches PumpFrame whatever else observes it.
            process.Controller.CurrentRbxApi.Scheduler.HostFaulted += (source, fault) => throw fault;

            Assert.DoesNotThrow(() => process.Controller.PumpFrame(host, 0.1f));

            Assert.AreEqual("yes", process.ModData.Get("tick-probe", "ticked"), "Tick ran although Advance threw");
            Assert.AreEqual(1, process.Diagnostics.Count, string.Join(" | ", process.Diagnostics));
            StringAssert.Contains("scheduler Advance", process.Diagnostics[0]);
            StringAssert.Contains("Injected phase fault.", process.Diagnostics[0]);
        }

        private StartupDisk NewStartupDisk()
        {
            return new StartupDisk(NewTemporaryDirectory(), NewTemporaryDirectory());
        }

        private static void SeedStartupMods(StartupDisk disk)
        {
            FileLuaModSourceStore sources = disk.OpenDefaultSources();
            sources.Save(StartupActiveModId, StartupActiveModSource, StartupManifest(StartupActiveModId, true));
            sources.Save(
                StartupDormantModId,
                "local marker = Instance.new('Folder') marker.Name = 'DormantStart' marker.Parent = workspace",
                StartupManifest(StartupDormantModId, false));
        }

        private static LuaModManifest StartupManifest(
            string id,
            bool active,
            LuaCapabilities capabilities = StartupCapabilities)
        {
            return new LuaModManifest
            {
                Id = id,
                Name = id,
                Capabilities = capabilities.ToString(),
                Active = active
            };
        }

        private static FileRbxWorldPackageStore NewStartupStore(string root, string startupNamespace)
        {
            return new FileRbxWorldPackageStore(
                root,
                FileRbxWorldPackageStore.DefaultAutoBackupCapacity,
                cancellationToken => UniTask.FromResult(true),
                () => StartupClockUtc,
                null,
                startupNamespace);
        }

        private static FileRbxWorldPackageStore NewDurabilityStartupStore(
            string root,
            StartupDurabilityFileSystem fileSystem,
            Queue<bool> outcomes)
        {
            return new FileRbxWorldPackageStore(
                root,
                FileRbxWorldPackageStore.DefaultAutoBackupCapacity,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool outcome = outcomes.Count == 0 || outcomes.Dequeue();
                    if (outcome)
                    {
                        fileSystem.Commit();
                    }

                    return UniTask.FromResult(outcome);
                },
                () => StartupClockUtc,
                fileSystem,
                StartupNamespace);
        }

        /// <summary>A captured world with one literal Part, carrying exactly <paramref name="mods"/>.</summary>
        private RbxWorldPackagePayload CreateStartupPayload(string worldId, params RbxWorldModSource[] mods)
        {
            InstanceRegistry registry = new(worldId: worldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxInstance part = registry.Create("Part");
            part.Name = KeptPartName;
            part.Parent = registry.WorldRoot;
            PartProperties literal = LiteralKeptPart();
            partSink.SetPartProperties(part.Id, in literal);
            RbxWorldPackagePayload captured = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    partSink,
                    new RbxWorldSettings { WorldId = worldId },
                    capturedAtUtc: CapturedAtUtc));
            return new RbxWorldPackagePayload(
                captured.CapturedAtUtc,
                captured.Settings,
                captured.Tree,
                captured.Parts,
                captured.CameraCFrame,
                mods);
        }

        private static PartProperties LiteralKeptPart()
        {
            PartProperties literal = PartProperties.CreateDefault();
            literal.CFrame = RbxCFrame.FromPosition(2f, 3f, -4f);
            literal.Size = new RbxVector3(7f, 8f, 9f);
            literal.Color = RbxColor3.FromRGB(12f, 34f, 56f);
            literal.ColorWasExplicitlySet = true;
            literal.Anchored = true;
            literal.Transparency = 0.375f;
            literal.CanCollide = false;
            return literal;
        }

        private static void AssertLiteralKeptPart(in PartProperties actual)
        {
            CollectionAssert.AreEqual(
                RbxCFrame.FromPosition(2f, 3f, -4f).GetComponents(),
                actual.CFrame.GetComponents());
            Assert.AreEqual(new RbxVector3(7f, 8f, 9f), actual.Size);
            Assert.AreEqual(RbxColor3.FromRGB(12f, 34f, 56f), actual.Color);
            Assert.IsTrue(actual.ColorWasExplicitlySet);
            Assert.IsTrue(actual.Anchored);
            Assert.AreEqual(0.375f, actual.Transparency);
            Assert.IsFalse(actual.CanCollide);
        }

        private static List<string> FileNames(string directory)
        {
            List<string> names = new();
            if (!Directory.Exists(directory))
            {
                return names;
            }

            foreach (string path in Directory.GetFiles(directory))
            {
                names.Add(Path.GetFileName(path));
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static Dictionary<string, byte[]> FileContents(string directory)
        {
            Dictionary<string, byte[]> contents = new(StringComparer.Ordinal);
            foreach (string name in FileNames(directory))
            {
                contents.Add(name, File.ReadAllBytes(Path.Combine(directory, name)));
            }

            return contents;
        }

        private static void AssertSameFiles(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
        {
            CollectionAssert.AreEquivalent(expected.Keys, actual.Keys);
            foreach (KeyValuePair<string, byte[]> entry in expected)
            {
                CollectionAssert.AreEqual(entry.Value, actual[entry.Key], entry.Key + " changed");
            }
        }

        private static List<string> ModIds(IReadOnlyList<LuaModManifest> manifests)
        {
            List<string> ids = new(manifests.Count);
            foreach (LuaModManifest manifest in manifests)
            {
                ids.Add(manifest.Id);
            }

            return ids;
        }

        private static int CountNamed(InstanceRegistry registry, string name)
        {
            int count = 0;
            foreach (RbxInstance instance in registry.GetLiveInstances())
            {
                if (!instance.IsDestroyed && string.Equals(instance.Name, name, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static async UniTask<RbxWorldLoadRefusedException> CatchRefusal(Func<UniTask> request)
        {
            try
            {
                await request();
            }
            catch (RbxWorldLoadRefusedException refused)
            {
                return refused;
            }

            Assert.Fail("The world-load request was expected to be refused.");
            return null;
        }

        private static LuaCsModStack CreateStartupStack(
            LuaCsRbxApiBindings rbxApi,
            ILuaModSourceStore sourceStore,
            ILuaModStore modStore,
            ILuaScriptVersionStore versionStore,
            IConfirmedWorldMutationGate mutationGate = null)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new Mvp1AcceptanceNullLogger(),
                LuaScriptVersions = versionStore,
                ModStore = modStore,
                ModSourceStore = sourceStore,
                Capabilities = StartupCapabilities,
                OneOffCapabilities = StartupCapabilities,
                RbxApi = rbxApi,
                WorldMutationGate = mutationGate,
                RegisterWorldEditBuildBindings = false
            });
        }

        private static void WireStartupTeardown(LuaCsModStack stack, LuaCsRbxApiBindings rbxApi)
        {
            ModConnectionRegistry ownedConnections = rbxApi.Connections;
            InstanceRegistry ownedRegistry = rbxApi.Registry;
            stack.Runtime.ModTearingDown += (modId, reason) =>
            {
                if (reason == LuaModTeardownReason.Reload)
                {
                    rbxApi.KillOutgoingScheduledGenerations(modId);
                }
                else
                {
                    rbxApi.KillAllScheduledOwnedBy(modId);
                }

                ownedConnections.DisconnectOwnedBy(modId, reason == LuaModTeardownReason.Reload);
                if (reason != LuaModTeardownReason.Unload)
                {
                    return;
                }

                foreach (RbxInstance owned in ownedRegistry.GetTeardownOwnedBy(modId))
                {
                    owned?.Destroy();
                }
            };
        }

        /// <summary>The durable roots one simulated device keeps across process restarts.</summary>
        private sealed class StartupDisk
        {
            public StartupDisk(string packageRoot, string modsRoot)
            {
                PackageRoot = packageRoot;
                ModsRoot = modsRoot;
                StartupDirectory = Path.Combine(
                    Path.GetFullPath(packageRoot), "Startup", "Stores", StartupNamespace);
            }

            public string PackageRoot { get; }

            public string ModsRoot { get; }

            public string StartupDirectory { get; }

            /// <summary>Scripted answers of the package store's durability hook; true once the script runs out.</summary>
            public Queue<bool> PackageDurability { get; } = new();

            public int PackageSyncCalls { get; private set; }

            public UniTask<bool> SyncPackagesAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageSyncCalls++;
                return UniTask.FromResult(PackageDurability.Count == 0 || PackageDurability.Dequeue());
            }

            public FileLuaModSourceStore OpenDefaultSources()
            {
                return new FileLuaModSourceStore(
                    ModsRoot,
                    persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));
            }
        }

        /// <summary>
        /// One process lifetime over a <see cref="StartupDisk"/>: a fresh registry and initial stack,
        /// the real file package and source stores, and the production session controller wired the
        /// way the installer's headless branch wires it.
        /// </summary>
        private sealed class StartupProcess : IDisposable
        {
            public StartupProcess(
                StartupDisk disk,
                string worldId,
                int autoBackupCapacity = FileRbxWorldPackageStore.DefaultAutoBackupCapacity,
                INetworkBridge networkBridge = null,
                int? worldAclVersion = null,
                IRbxWorldPackageFileSystem packageFileSystem = null,
                bool sourcesDurable = true,
                bool withMutationGate = false)
            {
                Registry = new InstanceRegistry(worldAclVersion: worldAclVersion, worldId: worldId);
                Game = DataModelBootstrap.CreateGame(Registry);
                RbxApi = new LuaCsRbxApiBindings(Registry, Game, networkBridge: networkBridge);
                PackageStore = new FileRbxWorldPackageStore(
                    disk.PackageRoot,
                    autoBackupCapacity,
                    disk.SyncPackagesAsync,
                    () => Clock,
                    packageFileSystem,
                    StartupNamespace);
                SourceStore = new FileLuaModSourceStore(
                    disk.ModsRoot,
                    persistenceSyncAsync: cancellationToken => UniTask.FromResult(sourcesDurable));
                // WHY optional: the installer composes every session stack over one shared gate whose
                // capture is this controller; the tests that pin the gate-free load path keep it off.
                Gate = withMutationGate
                    ? new ConfirmedWorldMutationGate(
                        cancellationToken => UniTask.FromResult(Controller.CaptureCurrent()),
                        PackageStore)
                    : null;
                LuaCsModStack initialStack = CreateStartupStack(RbxApi, SourceStore, ModData, null, Gate);
                WireStartupTeardown(initialStack, RbxApi);
                Controller = new RbxWorldRuntimeSessionController(
                    new HeadlessRbxWorldSessionHost(
                        Registry,
                        Game,
                        new RbxWorldSettings { WorldId = worldId },
                        RbxApi.PartSink,
                        RbxApi.CameraRig),
                    PackageStore,
                    SourceStore,
                    initialStack,
                    RbxApi,
                    (candidate, stagedNetwork) => new LuaCsRbxApiBindings(
                        candidate.Registry,
                        candidate.Game,
                        partSink: candidate.PartSink,
                        cameraRig: candidate.CameraRig,
                        networkBridge: stagedNetwork),
                    (rbxApi, sourceStore, modStore, versionStore) =>
                        CreateStartupStack(rbxApi, sourceStore, modStore, versionStore, Gate),
                    WireStartupTeardown,
                    networkBridge,
                    StartupCapabilities,
                    false,
                    ModData,
                    null,
                    message => Diagnostics.Add(message));
                Controller.ConfigurePendingLoadClockForTests(() => Clock, TimeSpan.FromMinutes(2d));
            }

            /// <summary>The shared pre-mutation gate every session stack runs through; null when composed without one.</summary>
            public ConfirmedWorldMutationGate Gate { get; }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public LuaCsRbxApiBindings RbxApi { get; }

            public FileRbxWorldPackageStore PackageStore { get; }

            public FileLuaModSourceStore SourceStore { get; }

            public Mvp1AcceptanceMemoryStore ModData { get; } = new();

            public RbxWorldRuntimeSessionController Controller { get; }

            public List<string> Diagnostics { get; } = new();

            public DateTime Clock { get; set; } = StartupClockUtc;

            public ActorContext Player { get; } = new LocalActorIdentityProvider("startup-player")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            /// <summary>Authors the literal Part into the live world, with its durable property state.</summary>
            public RbxInstance AuthorKeptPart()
            {
                InstanceRegistry live = Controller.CurrentRbxApi.Registry;
                RbxInstance part = live.Create("Part");
                part.Name = KeptPartName;
                part.Parent = live.WorldRoot;
                PartProperties literal = LiteralKeptPart();
                Controller.CurrentRbxApi.PartSink.SetPartProperties(part.Id, in literal);
                return part;
            }

            public RbxInstance AddFolder(string name)
            {
                InstanceRegistry live = Controller.CurrentRbxApi.Registry;
                RbxInstance folder = live.Create("Folder");
                folder.Name = name;
                folder.Parent = live.WorldRoot;
                return folder;
            }

            public void Dispose()
            {
                Controller.Dispose();
            }
        }

        /// <summary>A startup surface whose restore answers a scripted outcome, or throws when none is given.</summary>
        private sealed class ScriptedStartupSelection : IRbxWorldStartupSelection
        {
            private readonly List<string> _calls;
            private readonly RbxWorldStartupRestoreOutcome? _outcome;

            public ScriptedStartupSelection(List<string> calls, RbxWorldStartupRestoreOutcome? outcome)
            {
                _calls = calls;
                _outcome = outcome;
            }

            public UniTask<RbxWorldStartupRestoreResult> RestoreStartupSelectionAsync(
                CancellationToken cancellationToken = default)
            {
                _calls.Add("restore");
                if (!_outcome.HasValue)
                {
                    return UniTask.FromException<RbxWorldStartupRestoreResult>(
                        new InvalidOperationException("Injected restore failure."));
                }

                return UniTask.FromResult(new RbxWorldStartupRestoreResult(
                    _outcome.Value, 1, "scripted-world", 0, ""));
            }

            public UniTask<RbxWorldPackageWriteResult> ClearStartupSelectionAsync(
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldStartupSelection> ReadStartupSelectionAsync(
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>A transport bridge whose registered actors stand for live remote sessions.</summary>
        private sealed class SessionBridge : INetworkBridge
        {
            private readonly List<string> _actorIds = new();

            public SessionBridge(RbxNetworkTopology topology)
            {
                Topology = topology;
            }

            public RbxNetworkTopology Topology { get; }

            public IReadOnlyList<string> ActorIds => _actorIds;

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public void RegisterActor(string actorId)
            {
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
            }

            public void SendRequest(RbxNetworkRequestMessage message, Action<RbxNetworkResponse> response)
            {
            }
        }

        /// <summary>
        /// Volatile files plus the durable image the last confirmed durability answer committed. A
        /// vanishing file is still listed but gone when opened: a deletion racing the listing.
        /// </summary>
        private sealed class StartupDurabilityFileSystem : IRbxWorldPackageFileSystem
        {
            private readonly Dictionary<string, byte[]> _volatileFiles = new(StringComparer.Ordinal);
            private readonly Dictionary<string, byte[]> _durableFiles = new(StringComparer.Ordinal);
            private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
            private readonly HashSet<string> _vanishing = new(StringComparer.Ordinal);

            public void Vanish(string path)
            {
                _vanishing.Add(Normalize(path));
            }

            public bool DirectoryExists(string path)
            {
                return _directories.Contains(Normalize(path));
            }

            public void CreateDirectory(string path)
            {
                _directories.Add(Normalize(path));
            }

            public bool FileExists(string path)
            {
                string normalized = Normalize(path);
                return _volatileFiles.ContainsKey(normalized) && !_vanishing.Contains(normalized);
            }

            public long GetFileLength(string path)
            {
                return _volatileFiles[Normalize(path)].LongLength;
            }

            public UniTask WriteAllBytesCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = Normalize(path);
                if (_volatileFiles.ContainsKey(normalized))
                {
                    throw new IOException("File already exists: " + normalized);
                }

                _volatileFiles.Add(normalized, (byte[])bytes.Clone());
                _directories.Add(Path.GetDirectoryName(normalized));
                return UniTask.CompletedTask;
            }

            public UniTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return UniTask.FromResult((byte[])_volatileFiles[Normalize(path)].Clone());
            }

            public void MoveCreateNew(string sourcePath, string destinationPath)
            {
                string source = Normalize(sourcePath);
                string destination = Normalize(destinationPath);
                if (!_volatileFiles.TryGetValue(source, out byte[] bytes))
                {
                    throw new FileNotFoundException("Missing source file.", source);
                }

                if (_volatileFiles.ContainsKey(destination))
                {
                    throw new IOException("File already exists: " + destination);
                }

                _volatileFiles.Remove(source);
                _volatileFiles.Add(destination, bytes);
            }

            public void DeleteFile(string path)
            {
                _volatileFiles.Remove(Normalize(path));
            }

            public IReadOnlyList<string> GetFiles(string directory, string extension)
            {
                string normalizedDirectory = Normalize(directory);
                List<string> files = new();
                foreach (string path in _volatileFiles.Keys)
                {
                    if (string.Equals(Path.GetDirectoryName(path), normalizedDirectory, StringComparison.Ordinal)
                        && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }

                return files;
            }

            public List<string> FileNamesIn(string directory)
            {
                string normalizedDirectory = Normalize(directory);
                List<string> names = new();
                foreach (string path in _volatileFiles.Keys)
                {
                    if (string.Equals(Path.GetDirectoryName(path), normalizedDirectory, StringComparison.Ordinal))
                    {
                        names.Add(Path.GetFileName(path));
                    }
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }

            public void Commit()
            {
                _durableFiles.Clear();
                foreach (KeyValuePair<string, byte[]> entry in _volatileFiles)
                {
                    _durableFiles.Add(entry.Key, (byte[])entry.Value.Clone());
                }
            }

            public void ReloadFromDurable()
            {
                _volatileFiles.Clear();
                foreach (KeyValuePair<string, byte[]> entry in _durableFiles)
                {
                    _volatileFiles.Add(entry.Key, (byte[])entry.Value.Clone());
                }
            }

            private static string Normalize(string path)
            {
                return Path.GetFullPath(path);
            }
        }

        #endregion
    }
}
