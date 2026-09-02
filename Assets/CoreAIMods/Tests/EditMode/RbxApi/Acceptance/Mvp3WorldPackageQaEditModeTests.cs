using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// Adversarial MVP3 coverage driven through production composition: the one-off executor behind
    /// the shared confirmed-backup gate, the headless session controller the installer builds when no
    /// scene host exists, and the file store's autosave ring under a non-monotonic clock.
    /// </summary>
    [TestFixture]
    public sealed class Mvp3WorldPackageQaEditModeTests
    {
        private const string WorldId = "mvp3-qa-world";

        // WHY: the Gameplay tier eagerly binds delegates to UnityEngine.Input ICALLs, which cannot be
        // resolved outside a Unity player; the world package never touches that tier, so the
        // off-device production composition grants every other standard tier.
        private const LuaCapabilities QaCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        private readonly List<RbxDataModel> _games = new();
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

            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public async Task ExecuteLua_BareInstanceNewPart_KeepsConfirmedBackupGateOpenAndCapturesDefaults()
        {
            HeadlessWorld world = new(WorldId);
            RecordingPackageStore packageStore = new();
            ConfirmedWorldMutationGate gate = new(
                _ => UniTask.FromResult(world.Capture()),
                packageStore);
            LuaCsModStack stack = world.CreateStack(gate);

            LuaTool.LuaResult created = await stack.ToolExecutor.ExecuteAsync(
                "local p = Instance.new('Part') p.Name = 'BarePart' p.Parent = workspace",
                CancellationToken.None);
            Assert.IsTrue(created.Success, created.Error);
            RbxInstance part = world.Registry.WorldRoot.FindFirstChild("BarePart");
            Assert.IsNotNull(part);

            LuaTool.LuaResult followUp = await stack.ToolExecutor.ExecuteAsync(
                "return workspace.BarePart.Size.X",
                CancellationToken.None);

            Assert.IsTrue(
                followUp.Success,
                "A bare Instance.new('Part') must not lock the AI out of every gated tool: "
                + followUp.Error);
            Assert.AreEqual("4", followUp.Output);
            CollectionAssert.AreEqual(
                new[]
                {
                    LuaCsGameToolExecutor.ExecuteLuaBackupTrigger,
                    LuaCsGameToolExecutor.ExecuteLuaBackupTrigger
                },
                packageStore.AutoTriggers);
            Assert.IsTrue(
                world.PartSink.TryGetPartProperties(part.Id, out PartProperties liveState),
                "Instance.new must leave a readable durable Part bundle in the production sink.");
            PartProperties defaults = PartProperties.CreateDefault();
            AssertPartPropertiesEqual(in defaults, in liveState);

            RbxWorldPackagePayload payload = packageStore.LastAutoPayload;
            Assert.IsNotNull(payload);
            Assert.IsTrue(payload.Parts.TryGetValue(part.Id, out PartProperties captured));
            AssertPartPropertiesEqual(in defaults, in captured);

            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                payload,
                new RbxWorldPackageRestoreOptions { CameraRig = new InMemoryCameraRig() });
            _games.Add(restored.Game);
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(part.Id, out PartProperties reloaded));
            AssertPartPropertiesEqual(in defaults, in reloaded);
        }

        [Test]
        public async Task HeadlessSessionController_ConfirmedLoadOfOwnCapture_RestoresCameraState()
        {
            using HeadlessSession session = new(WorldId);
            LuaTool.LuaResult prepared = await session.Controller.Executor.ExecuteAsync(
                "workspace.CurrentCamera.CFrame = CFrame.new(10, 5, -4) * CFrame.Angles(0.1, 0.2, 0.3)",
                CancellationToken.None);
            Assert.IsTrue(prepared.Success, prepared.Error);
            RbxWorldPackagePayload payload = session.Controller.CaptureCurrent();
            Assert.IsTrue(payload.CameraCFrame.HasValue, "the headless capture carries camera state");

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(payload);

            Assert.IsTrue(
                loaded.Success,
                "A headless player must be able to load the package it just captured: " + loaded.Error);
            CollectionAssert.AreEqual(
                payload.CameraCFrame.Value.GetComponents(),
                session.Controller.CurrentRbxApi.CameraRig.GetCFrame().GetComponents());
            RbxWorldPackagePayload recaptured = session.Controller.CaptureCurrent();
            Assert.IsTrue(recaptured.CameraCFrame.HasValue);
            CollectionAssert.AreEqual(
                payload.CameraCFrame.Value.GetComponents(),
                recaptured.CameraCFrame.Value.GetComponents());
        }

        [Test]
        public async Task HeadlessSessionController_ConfirmedLoad_KeepsPartStateVisibleToLuaAndRecapture()
        {
            using HeadlessSession session = new(WorldId);
            LuaTool.LuaResult prepared = await session.Controller.Executor.ExecuteAsync(
                @"local p = Instance.new('Part')
p.Name = 'SavedPart'
p.CFrame = CFrame.new(2, 3, -4) * CFrame.Angles(0.2, -0.4, 0.6)
p.Size = Vector3.new(7, 8, 9)
p.Color = Color3.fromRGB(12, 34, 56)
p.Anchored = true
p.Transparency = 0.375
p.CanCollide = false
p.Parent = workspace",
                CancellationToken.None);
            Assert.IsTrue(prepared.Success, prepared.Error);
            RbxInstance sourcePart = session.Controller.CurrentRbxApi.Registry.WorldRoot
                .FindFirstChild("SavedPart");
            Assert.IsNotNull(sourcePart);
            RbxWorldPackagePayload captured = session.Controller.CaptureCurrent();
            Assert.IsTrue(captured.Parts.TryGetValue(sourcePart.Id, out PartProperties expected));
            RbxWorldPackagePayload cameraless = new(
                captured.CapturedAtUtc,
                captured.Settings,
                captured.Tree,
                captured.Parts,
                null,
                captured.Mods);

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(cameraless);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.IsTrue(
                session.Controller.CurrentRbxApi.PartSink.TryGetPartProperties(
                    sourcePart.Id, out PartProperties restored),
                "the published session must read the restored Part bundle through its own sink");
            AssertPartPropertiesEqual(in expected, in restored);
            LuaTool.LuaResult sizeRead = await session.Controller.Executor.ExecuteAsync(
                "return workspace.SavedPart.Size.X",
                CancellationToken.None);
            Assert.IsTrue(sizeRead.Success, sizeRead.Error);
            Assert.AreEqual("7", sizeRead.Output);
            RbxWorldPackagePayload recaptured = session.Controller.CaptureCurrent();
            Assert.IsTrue(recaptured.Parts.TryGetValue(sourcePart.Id, out PartProperties recapturedPart));
            AssertPartPropertiesEqual(in expected, in recapturedPart);
        }

        [Test]
        public async Task FileStore_AutosaveRotationUnderClockRegression_NeverDropsTheJustConfirmedBackup()
        {
            MemoryFileSystem fileSystem = new();
            Queue<DateTime> clock = new(new[]
            {
                new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc)
            });
            FileRbxWorldPackageStore store = new(
                Path.Combine(Path.GetTempPath(), "CoreAI-QaRing-" + Guid.NewGuid().ToString("N")),
                1,
                _ => UniTask.FromResult(true),
                () => clock.Dequeue(),
                fileSystem);
            RbxWorldPackagePayload payload = CreateMinimalPayload();

            RbxWorldPackageWriteResult first = await store.CreateAutoAsync("execute_lua", payload);
            Assert.IsTrue(first.Success, first.Error);
            RbxWorldPackageWriteResult second = await store.CreateAutoAsync("execute_lua", payload);

            Assert.IsTrue(second.Success, second.Error);
            Assert.IsTrue(
                fileSystem.FileExists(second.Path),
                "a confirmed autosave must exist at the path the store reports: " + second.Path);
            CollectionAssert.AreEqual(
                new[] { Path.GetFileName(second.Path) },
                store.ListAutoFiles(),
                "the ring keeps the just-confirmed backup and rotates the other one out");
        }

        private RbxWorldPackagePayload CreateMinimalPayload()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                registry,
                game,
                new InMemoryPartPropertySink(),
                NewSettings(WorldId)));
        }

        private static RbxWorldSettings NewSettings(string worldId)
        {
            return new RbxWorldSettings
            {
                WorldId = worldId,
                MetersPerStud = RbxWorldSettings.DefaultMetersPerStud,
                GravityStudsPerSecondSquared = RbxWorldSettings.DefaultGravityStudsPerSecondSquared,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            };
        }

        private static void AssertPartPropertiesEqual(
            in PartProperties expected,
            in PartProperties actual)
        {
            Assert.AreEqual(expected.Shape, actual.Shape);
            Assert.AreEqual(expected.Material.Name, actual.Material.Name);
            Assert.AreEqual(expected.Material.Value, actual.Material.Value);
            CollectionAssert.AreEqual(expected.CFrame.GetComponents(), actual.CFrame.GetComponents());
            Assert.AreEqual(expected.Size, actual.Size);
            Assert.AreEqual(expected.Color, actual.Color);
            Assert.AreEqual(expected.ColorWasExplicitlySet, actual.ColorWasExplicitlySet);
            Assert.AreEqual(expected.Anchored, actual.Anchored);
            Assert.AreEqual(expected.Transparency, actual.Transparency);
            Assert.AreEqual(expected.CanCollide, actual.CanCollide);
        }

        /// <summary>Engine-free Rbx world composed exactly like the installer's headless branch.</summary>
        private sealed class HeadlessWorld
        {
            public HeadlessWorld(string worldId)
            {
                Registry = new InstanceRegistry(worldId: worldId);
                Game = DataModelBootstrap.CreateGame(Registry);
                SourceStore = new MemoryTransactionalSourceStore();
                RbxApi = new LuaCsRbxApiBindings(Registry, Game);
                Settings = NewSettings(worldId);
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public MemoryTransactionalSourceStore SourceStore { get; }

            public LuaCsRbxApiBindings RbxApi { get; }

            public IPartPropertySink PartSink => RbxApi.PartSink;

            public RbxWorldSettings Settings { get; }

            public LuaCsModStack CreateStack(IConfirmedWorldMutationGate gate)
            {
                return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new Mvp1AcceptanceNullLogger(),
                    ModStore = new Mvp1AcceptanceMemoryStore(),
                    ModSourceStore = SourceStore,
                    Capabilities = QaCapabilities,
                    OneOffCapabilities = QaCapabilities,
                    RbxApi = RbxApi,
                    WorldMutationGate = gate,
                    RegisterWorldEditBuildBindings = false
                });
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry,
                    Game,
                    RbxApi.PartSink,
                    Settings,
                    RbxApi.CameraRig,
                    SourceStore));
            }
        }

        /// <summary>
        /// The production session controller over the engine-free host adapter, wired the way
        /// CoreAiModsInstaller composes it when no RbxWorldHost is present.
        /// </summary>
        private sealed class HeadlessSession : IDisposable
        {
            private readonly HeadlessWorld _world;

            public HeadlessSession(string worldId)
            {
                _world = new HeadlessWorld(worldId);
                LuaCsModStack initialStack = _world.CreateStack(null);
                WireSessionTeardown(initialStack, _world.RbxApi);
                Controller = new RbxWorldRuntimeSessionController(
                    new HeadlessRbxWorldSessionHost(
                        _world.RbxApi.Registry,
                        _world.RbxApi.Game,
                        partSink: _world.RbxApi.PartSink,
                        cameraRig: _world.RbxApi.CameraRig),
                    new RecordingPackageStore(),
                    _world.SourceStore,
                    initialStack,
                    _world.RbxApi,
                    (candidate, stagedNetwork) => new LuaCsRbxApiBindings(
                        candidate.Registry,
                        candidate.Game,
                        partSink: candidate.PartSink,
                        cameraRig: candidate.CameraRig,
                        inputSource: candidate.InputSource,
                        pickSource: candidate.PickSource,
                        networkBridge: stagedNetwork),
                    (rbxApi, sourceStore, modStore, versionStore) =>
                        LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                        {
                            Logger = new Mvp1AcceptanceNullLogger(),
                            LuaScriptVersions = versionStore,
                            ModStore = modStore,
                            ModSourceStore = sourceStore,
                            Capabilities = QaCapabilities,
                            OneOffCapabilities = QaCapabilities,
                            RbxApi = rbxApi,
                            RegisterWorldEditBuildBindings = false
                        }),
                    WireSessionTeardown,
                    null,
                    QaCapabilities,
                    false,
                    new Mvp1AcceptanceMemoryStore(),
                    null,
                    message => Diagnostics.Add(message));
            }

            public RbxWorldRuntimeSessionController Controller { get; }

            public List<string> Diagnostics { get; } = new();

            public void Dispose()
            {
                Controller.Dispose();
            }

            private static void WireSessionTeardown(LuaCsModStack stack, LuaCsRbxApiBindings rbxApi)
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
        }

        private sealed class RecordingPackageStore : IRbxWorldPackageStore
        {
            public List<string> AutoTriggers { get; } = new();

            public RbxWorldPackagePayload LastAutoPayload { get; private set; }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
                string slot,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    false, "", "Manual slots are outside this test seam."));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
                string trigger,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                AutoTriggers.Add(trigger);
                LastAutoPayload = payload;
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    true, "memory://auto/" + AutoTriggers.Count.ToString(CultureInfo.InvariantCulture), ""));
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
                return Array.Empty<string>();
            }
        }

        private sealed class MemoryTransactionalSourceStore : ILuaModSourceStore, IRbxWorldModSourceStore
        {
            private readonly Dictionary<string, RbxWorldModSource> _mods = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                string key = id?.Trim() ?? "";
                LuaModManifest stored = CloneManifest(manifest);
                stored.Id = key;
                _mods[key] = new RbxWorldModSource(stored, source ?? "");
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_mods.TryGetValue(id?.Trim() ?? "", out RbxWorldModSource stored))
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

                manifests.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
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

            public UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
                IReadOnlyList<RbxWorldModSource> mods,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MemoryTransactionalSourceStore staged = new();
                foreach (RbxWorldModSource mod in mods)
                {
                    staged.Save(mod.Manifest.Id, mod.Source, mod.Manifest);
                }

                return UniTask.FromResult<IRbxWorldModSourceReplacement>(
                    new MemoryReplacement(this, staged));
            }

            private static LuaModManifest CloneManifest(LuaModManifest manifest)
            {
                return JsonConvert.DeserializeObject<LuaModManifest>(
                    JsonConvert.SerializeObject(manifest ?? new LuaModManifest()));
            }

            private sealed class MemoryReplacement : IRbxWorldModSourceReplacement
            {
                private readonly MemoryTransactionalSourceStore _owner;
                private readonly MemoryTransactionalSourceStore _staged;

                public MemoryReplacement(
                    MemoryTransactionalSourceStore owner,
                    MemoryTransactionalSourceStore staged)
                {
                    _owner = owner;
                    _staged = staged;
                }

                public ILuaModSourceStore SourceStore => _staged;

                public void Activate()
                {
                    _owner._mods.Clear();
                    foreach (KeyValuePair<string, RbxWorldModSource> entry in _staged._mods)
                    {
                        _owner._mods.Add(entry.Key, entry.Value);
                    }
                }

                public UniTask CompleteAsync(CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return UniTask.CompletedTask;
                }

                public UniTask RollbackAsync(CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return UniTask.CompletedTask;
                }

                public void Dispose()
                {
                }
            }
        }

        private sealed class MemoryFileSystem : IRbxWorldPackageFileSystem
        {
            private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
            private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

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
                return _files.ContainsKey(Normalize(path));
            }

            public long GetFileLength(string path)
            {
                return _files[Normalize(path)].LongLength;
            }

            public UniTask WriteAllBytesCreateNewAsync(
                string path,
                byte[] bytes,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = Normalize(path);
                if (_files.ContainsKey(normalized))
                {
                    throw new IOException("File already exists: " + normalized);
                }

                _files.Add(normalized, (byte[])bytes.Clone());
                string directory = Path.GetDirectoryName(normalized);
                if (!string.IsNullOrEmpty(directory))
                {
                    _directories.Add(directory);
                }

                return UniTask.CompletedTask;
            }

            public UniTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return UniTask.FromResult((byte[])_files[Normalize(path)].Clone());
            }

            public void MoveCreateNew(string sourcePath, string destinationPath)
            {
                string source = Normalize(sourcePath);
                string destination = Normalize(destinationPath);
                if (!_files.TryGetValue(source, out byte[] bytes))
                {
                    throw new FileNotFoundException("Missing source file.", source);
                }

                if (_files.ContainsKey(destination))
                {
                    throw new IOException("File already exists: " + destination);
                }

                _files.Remove(source);
                _files.Add(destination, bytes);
            }

            public void DeleteFile(string path)
            {
                _files.Remove(Normalize(path));
            }

            public IReadOnlyList<string> GetFiles(string directory, string extension)
            {
                string normalizedDirectory = Normalize(directory);
                List<string> files = new();
                foreach (string path in _files.Keys)
                {
                    if (string.Equals(
                            Path.GetDirectoryName(path),
                            normalizedDirectory,
                            StringComparison.Ordinal)
                        && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }

                return files;
            }

            private static string Normalize(string path)
            {
                return Path.GetFullPath(path);
            }
        }
    }
}
