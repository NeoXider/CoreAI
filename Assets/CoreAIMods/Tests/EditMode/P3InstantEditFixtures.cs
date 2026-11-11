using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Tests.EditMode.RbxApi.Acceptance;
using Cysharp.Threading.Tasks;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Engine-free fixtures shared by the P3 instant edit loop tests (gate, store, undo, limiter).</summary>
    internal static class P3Fixtures
    {
        public const string WorldId = "p3-world";

        public const LuaCapabilities SessionCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        public static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 9, 30, 0, DateTimeKind.Utc);

        public static RbxWorldSettings NewSettings()
        {
            return new RbxWorldSettings
            {
                WorldId = WorldId,
                MetersPerStud = 0.28f,
                GravityStudsPerSecondSquared = 196.2d,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            };
        }

        /// <summary>A captured world whose workspace holds one Folder per name.</summary>
        public static RbxWorldPackagePayload CapturePayload(params string[] folderNames)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            foreach (string name in folderNames)
            {
                RbxInstance folder = registry.Create("Folder");
                folder.Name = name;
                folder.Parent = registry.WorldRoot;
            }

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));
            game.Destroy();
            return payload;
        }

        /// <summary>The package bytes of <paramref name="payload"/> with the capture time left out: equal worlds give equal bytes.</summary>
        public static byte[] TimelessPackage(RbxWorldPackagePayload payload)
        {
            return RbxWorldPackageSerializer.WritePackage(new RbxWorldPackagePayload(
                DateTime.MinValue,
                payload.Settings,
                payload.Tree,
                payload.Parts,
                payload.CameraCFrame,
                payload.Mods,
                payload.Diagnostics));
        }

        public static string NewTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "CoreAI-P3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// An in-memory package store with every face the gate and the session use: durable-first autosaves,
    /// the two-phase write-behind autosave, and an optional startup selection. Every call is recorded in
    /// <see cref="Log"/>, and each phase can be held open or made to fail.
    /// </summary>
    internal sealed class P3MemoryPackageStore
        : IRbxWorldPackageStore, IRbxWorldAutoSaveWriteBehindStore, IRbxWorldStartupStore
    {
        private readonly List<RbxMissingAutoSave> _missing = new();
        private UniTaskCompletionSource<bool> _heldCommit;

        public P3MemoryPackageStore(RbxWorldPackagePayload startupSelection = null)
        {
            StartupSelection = startupSelection;
        }

        /// <summary>Every call in order: "prepare:t", "commit:t", "commit-done:t", "durable:t", "select".</summary>
        public List<string> Log { get; } = new();

        public List<RbxWorldPackagePayload> DurableAutoPayloads { get; } = new();

        public List<byte[]> CommittedPackages { get; } = new();

        public List<RbxWorldPackagePayload> SelectedPayloads { get; } = new();

        public RbxWorldPackagePayload StartupSelection { get; set; }

        /// <summary>When set, prepare answers that the payload cannot be encoded, with this reason.</summary>
        public string EncodingRefusal { get; set; }

        /// <summary>When set, the next commit stays pending until <see cref="ReleaseHeldCommit"/>.</summary>
        public bool HoldNextCommit { get; set; }

        /// <summary>When set, every commit fails as unconfirmed durability.</summary>
        public bool FailCommits { get; set; }

        /// <summary>When set, every durable-first autosave fails as unconfirmed durability.</summary>
        public bool FailDurableAutoSaves { get; set; }

        public bool HasHeldCommit => _heldCommit != null;

        public IReadOnlyList<RbxMissingAutoSave> MissingAutoSavesAtOpen => _missing;

        public int Acknowledged { get; private set; }

        public void AddMissingAutoSave(RbxMissingAutoSave missing)
        {
            _missing.Add(missing);
        }

        public void AcknowledgeMissingAutoSaves()
        {
            Acknowledged++;
        }

        public void ReleaseHeldCommit()
        {
            UniTaskCompletionSource<bool> held = _heldCommit
                ?? throw new InvalidOperationException("No commit is held.");
            _heldCommit = null;
            held.TrySetResult(true);
        }

        public UniTask<RbxPreparedAutoSave> PrepareAutoAsync(
            string trigger,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Add("prepare:" + trigger);
            if (EncodingRefusal != null)
            {
                return UniTask.FromResult(RbxPreparedAutoSave.Unencodable(trigger, P3Fixtures.CapturedAtUtc, EncodingRefusal));
            }

            return UniTask.FromResult(RbxPreparedAutoSave.Encoded(
                trigger, P3Fixtures.CapturedAtUtc, RbxWorldPackageSerializer.WritePackage(payload)));
        }

        public async UniTask<RbxWorldPackageWriteResult> CommitAutoAsync(
            RbxPreparedAutoSave prepared,
            CancellationToken cancellationToken = default)
        {
            Log.Add("commit:" + prepared.Trigger);
            if (HoldNextCommit)
            {
                HoldNextCommit = false;
                _heldCommit = new UniTaskCompletionSource<bool>();
                await _heldCommit.Task;
            }

            Log.Add("commit-done:" + prepared.Trigger);
            if (FailCommits)
            {
                return new RbxWorldPackageWriteResult(false, "", "Injected write-behind durability refusal.");
            }

            CommittedPackages.Add(prepared.PackageBytes);
            return new RbxWorldPackageWriteResult(true, prepared.Trigger + ".world", "");
        }

        public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
            string trigger,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            Log.Add("durable:" + trigger);
            DurableAutoPayloads.Add(payload);
            if (EncodingRefusal != null)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(false, "", EncodingRefusal, true));
            }

            return UniTask.FromResult(FailDurableAutoSaves
                ? new RbxWorldPackageWriteResult(false, "", "Injected durable-first durability refusal.")
                : new RbxWorldPackageWriteResult(true, trigger + ".world", ""));
        }

        public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
            string slot,
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            return UniTask.FromResult(new RbxWorldPackageWriteResult(false, "", "Manual slots are outside this fixture."));
        }

        public UniTask<RbxWorldPackagePayload> LoadManualAsync(string slot, CancellationToken cancellationToken = default)
        {
            throw new FileNotFoundException("No manual payload in this fixture.", slot);
        }

        public UniTask<RbxWorldPackagePayload> LoadAutoAsync(string fileName, CancellationToken cancellationToken = default)
        {
            throw new FileNotFoundException("No autosave payload in this fixture.", fileName);
        }

        public IReadOnlyList<string> ListManualSlots()
        {
            return Array.Empty<string>();
        }

        public IReadOnlyList<string> ListAutoFiles()
        {
            return Array.Empty<string>();
        }

        public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
        {
            return Array.Empty<RbxAutoSaveInfo>();
        }

        public UniTask<RbxWorldPackageWriteResult> SelectStartupAsync(
            RbxWorldPackagePayload payload,
            string sourceKind,
            string sourceName,
            CancellationToken cancellationToken = default)
        {
            Log.Add("select");
            SelectedPayloads.Add(payload);
            return UniTask.FromResult(new RbxWorldPackageWriteResult(true, "startup.world", ""));
        }

        public UniTask<RbxWorldPackageWriteResult> ClearStartupAsync(CancellationToken cancellationToken = default)
        {
            StartupSelection = null;
            return UniTask.FromResult(new RbxWorldPackageWriteResult(true, "startup.default", ""));
        }

        public UniTask<RbxWorldStartupSelection> ReadStartupAsync(CancellationToken cancellationToken = default)
        {
            return UniTask.FromResult(StartupSelection == null
                ? RbxWorldStartupSelection.NoneSelected
                : new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package, 1, StartupSelection, P3Fixtures.WorldId,
                    P3Fixtures.CapturedAtUtc, "manual", "p3-slot", ""));
        }

        public UniTask<RbxWorldStartupSelection> ReadStartupInfoAsync(CancellationToken cancellationToken = default)
        {
            return UniTask.FromResult(StartupSelection == null
                ? RbxWorldStartupSelection.NoneSelected
                : new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package, 1, null, P3Fixtures.WorldId,
                    P3Fixtures.CapturedAtUtc, "manual", "p3-slot", ""));
        }
    }

    /// <summary>A mod source store that can replace its whole source set, as world loads require.</summary>
    internal sealed class P3MemorySourceStore : ILuaModSourceStore, IRbxWorldModSourceStore
    {
        private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries =
            new(StringComparer.Ordinal);

        public void Save(string id, string source, LuaModManifest manifest)
        {
            _entries[id] = (source, Clone(manifest ?? new LuaModManifest { Id = id, Name = id }));
        }

        public bool TryLoad(string id, out string source, out LuaModManifest manifest)
        {
            if (id != null && _entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
            {
                source = entry.Source;
                manifest = Clone(entry.Manifest);
                return true;
            }

            source = "";
            manifest = null;
            return false;
        }

        public IReadOnlyList<LuaModManifest> List()
        {
            List<LuaModManifest> manifests = new(_entries.Count);
            foreach ((string Source, LuaModManifest Manifest) entry in _entries.Values)
            {
                manifests.Add(Clone(entry.Manifest));
            }

            return manifests;
        }

        public void SetActive(string id, bool active)
        {
            if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
            {
                entry.Manifest.Active = active;
            }
        }

        public void Delete(string id)
        {
            _entries.Remove(id);
        }

        public UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
            IReadOnlyList<RbxWorldModSource> mods,
            CancellationToken cancellationToken = default)
        {
            P3MemorySourceStore staged = new();
            foreach (RbxWorldModSource mod in mods)
            {
                staged.Save(mod.Manifest.Id, mod.Source, mod.Manifest);
            }

            return UniTask.FromResult<IRbxWorldModSourceReplacement>(new Replacement(staged));
        }

        private static LuaModManifest Clone(LuaModManifest source)
        {
            return new LuaModManifest
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                Version = source.Version,
                Category = source.Category,
                Tags = source.Tags,
                Origin = source.Origin,
                SeededVersion = source.SeededVersion,
                SeededHash = source.SeededHash,
                Author = source.Author,
                OwnerActorId = source.OwnerActorId,
                Capabilities = source.Capabilities,
                Active = source.Active,
                UpdateAvailable = source.UpdateAvailable,
                Entry = source.Entry
            };
        }

        private sealed class Replacement : IRbxWorldModSourceReplacement
        {
            public Replacement(ILuaModSourceStore staged)
            {
                SourceStore = staged;
            }

            public ILuaModSourceStore SourceStore { get; }

            public void Activate()
            {
            }

            public UniTask CompleteAsync(CancellationToken cancellationToken = default)
            {
                return UniTask.CompletedTask;
            }

            public UniTask RollbackAsync(CancellationToken cancellationToken = default)
            {
                return UniTask.CompletedTask;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The production session controller over the headless host, its stacks sharing one gate over the
    /// controller's own capture, plus the undo service observing that gate: the graph the installer and
    /// the instant edit installer compose, without a scene.
    /// </summary>
    internal sealed class P3HeadlessSession : IDisposable
    {
        public P3HeadlessSession(
            IRbxWorldPackageStore packageStore,
            ILuaModSourceStore sourceStore = null,
            bool writeBehind = true,
            int undoCapacity = RbxWorldUndoService.DefaultCapacity)
        {
            SourceStore = sourceStore ?? new P3MemorySourceStore();
            InstanceRegistry registry = new(worldId: P3Fixtures.WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            HeadlessRbxWorldSessionHost host = new(
                registry,
                game,
                P3Fixtures.NewSettings(),
                new InMemoryPartPropertySink(),
                new InMemoryCameraRig());
            LuaCsRbxApiBindings rbxApi = new(
                host.Registry,
                host.Game,
                partSink: host.PartSink,
                cameraRig: host.CameraRig);
            Gate = new ConfirmedWorldMutationGate(
                cancellationToken => UniTask.FromResult(Controller.CaptureCurrent()),
                packageStore,
                writeBehind);
            Controller = new RbxWorldRuntimeSessionController(
                host,
                packageStore,
                SourceStore,
                CreateStack(rbxApi, SourceStore, new Mvp1AcceptanceMemoryStore(), null),
                rbxApi,
                (candidate, stagedNetwork) => new LuaCsRbxApiBindings(
                    candidate.Registry,
                    candidate.Game,
                    partSink: candidate.PartSink,
                    cameraRig: candidate.CameraRig,
                    networkBridge: stagedNetwork),
                CreateStack,
                (stack, stagedApi) => { },
                null,
                P3Fixtures.SessionCapabilities,
                false,
                new Mvp1AcceptanceMemoryStore(),
                null,
                message => Diagnostics.Add(message));
            Undo = new RbxWorldUndoService(Gate, Controller, undoCapacity);
        }

        public ConfirmedWorldMutationGate Gate { get; }

        public RbxWorldRuntimeSessionController Controller { get; }

        public RbxWorldUndoService Undo { get; }

        public ILuaModSourceStore SourceStore { get; }

        public List<string> Diagnostics { get; } = new();

        /// <summary>Runs <paramref name="code"/> as <paramref name="actor"/>'s execute_lua, as the production tool does.</summary>
        public System.Threading.Tasks.Task<LuaTool.LuaResult> ExecuteAsAsync(CoreAI.Authority.ActorContext actor, string code)
        {
            return ((LuaTool.IMutationExecutor)Controller.Executor).ExecuteAsync(code, actor, CancellationToken.None);
        }

        public void Dispose()
        {
            Undo.Dispose();
            Controller.Dispose();
        }

        private LuaCsModStack CreateStack(
            LuaCsRbxApiBindings rbxApi,
            ILuaModSourceStore sourceStore,
            ILuaModStore modStore,
            ILuaScriptVersionStore versionStore)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new Mvp1AcceptanceNullLogger(),
                LuaScriptVersions = versionStore,
                ModStore = modStore,
                ModSourceStore = sourceStore,
                Capabilities = P3Fixtures.SessionCapabilities,
                OneOffCapabilities = P3Fixtures.SessionCapabilities,
                RbxApi = rbxApi,
                WorldMutationGate = Gate,
                RegisterWorldEditBuildBindings = false
            });
        }
    }

    /// <summary>Gameplay bindings whose <c>mutate_world()</c> changes a recorded state; for executor-level gate tests.</summary>
    internal sealed class P3RecordingBindings : ILuaCsGameRuntimeBindings
    {
        private readonly Action _onMutation;

        public P3RecordingBindings(Action onMutation = null)
        {
            _onMutation = onMutation;
        }

        public string TreeState { get; private set; } = "old-tree";

        public int Mutations { get; private set; }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("mutate_world", new Action(MutateWorld));
        }

        private void MutateWorld()
        {
            TreeState = "new-tree";
            Mutations++;
            _onMutation?.Invoke();
        }
    }

    /// <summary>
    /// The real file system, except that a write of a temporary file stops half-way and never completes:
    /// what a store sees when the process dies while an autosave is being written.
    /// </summary>
    internal sealed class P3CrashingFileSystem : IRbxWorldPackageFileSystem
    {
        private readonly IRbxWorldPackageFileSystem _inner = new SystemRbxWorldPackageFileSystem();

        public bool DirectoryExists(string path)
        {
            return _inner.DirectoryExists(path);
        }

        public void CreateDirectory(string path)
        {
            _inner.CreateDirectory(path);
        }

        public bool FileExists(string path)
        {
            return _inner.FileExists(path);
        }

        public long GetFileLength(string path)
        {
            return _inner.GetFileLength(path);
        }

        public UniTask WriteAllBytesCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            if (!path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                return _inner.WriteAllBytesCreateNewAsync(path, bytes, cancellationToken);
            }

            byte[] half = new byte[bytes.Length / 2];
            Array.Copy(bytes, half, half.Length);
            File.WriteAllBytes(path, half);
            return new UniTaskCompletionSource().Task;
        }

        public UniTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            return _inner.ReadAllBytesAsync(path, cancellationToken);
        }

        public void MoveCreateNew(string sourcePath, string destinationPath)
        {
            _inner.MoveCreateNew(sourcePath, destinationPath);
        }

        public void DeleteFile(string path)
        {
            _inner.DeleteFile(path);
        }

        public IReadOnlyList<string> GetFiles(string directory, string extension)
        {
            return _inner.GetFiles(directory, extension);
        }
    }
}
