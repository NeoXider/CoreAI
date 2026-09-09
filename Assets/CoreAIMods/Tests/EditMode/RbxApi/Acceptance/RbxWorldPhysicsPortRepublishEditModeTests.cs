using System;
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
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// Closes the coverage gap around PublishReplacement + PhysicsPort/WorldPhysics: no test
    /// exercised a staged-then-committed world replacement together with the physics port before.
    /// </summary>
    /// <remarks>
    /// WHY a fake host that swaps its own port at Commit, not a real RbxWorldHost: the bug is in
    /// WHEN the session controller reads and re-attaches IRbxWorldSessionHost.PhysicsPort relative
    /// to candidate.Commit(), not in Unity's physics adapter itself. A fake that reproduces exactly
    /// RbxWorldHost.PublishReplacement's dispose-then-replace sequence proves the controller-level
    /// fix without needing a live scene.
    /// </remarks>
    [TestFixture]
    public sealed class RbxWorldPhysicsPortRepublishEditModeTests
    {
        private const string WorldId = "physics-port-republish";

        private static readonly DateTime CapturedAtUtc =
            new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

        private string _temporaryDirectory;

        [TearDown]
        public void TearDown()
        {
            if (_temporaryDirectory != null && Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, true);
            }
        }

        [Test]
        public async Task LoadConfirmedAsync_RebindsWorldPhysicsToPostPublishPort_NotTheDisposedStagePort()
        {
            InstanceRegistry outgoingRegistry = new(worldId: WorldId);
            RbxDataModel outgoingGame = DataModelBootstrap.CreateGame(outgoingRegistry);
            FakePhysicsSessionHost host = new(outgoingRegistry, outgoingGame);
            FakePhysicsPort outgoingPort = host.PhysicsPort;

            LuaCsRbxApiBindings initialRbxApi = new(host.Registry, host.Game);
            LuaCsModStack initialStack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new Mvp1AcceptanceNullLogger(),
                ModStore = new Mvp1AcceptanceMemoryStore(),
                ModSourceStore = new StubModSourceStore(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = initialRbxApi
            });

            StubModSourceStore sourceStore = new();
            RbxWorldRuntimeSessionController controller = new(
                host,
                CreatePackageStore(),
                sourceStore,
                initialStack,
                initialRbxApi,
                (candidate, network) =>
                {
                    LuaCsRbxApiBindings stagedApi = new(
                        candidate.Registry,
                        candidate.Game,
                        partSink: candidate.PartSink,
                        cameraRig: candidate.CameraRig);

                    // WHY attach the CURRENT host port here, exactly like the pre-fix
                    // CoreAiModsInstaller rbxApiFactory did: this is the pattern the finding
                    // flagged as wrong (STAGE-time attach to a port Commit is about to dispose).
                    // Reproducing it here, in the factory the test controls, isolates the fix to
                    // where it actually lives now: the session controller's post-commit re-attach.
                    if (host.PhysicsPort != null)
                    {
                        stagedApi.WorldPhysics.AttachPort(host.PhysicsPort);
                    }

                    return stagedApi;
                },
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
                new CoreAI.Ai.MemoryLuaScriptVersionStore());

            RbxWorldPackagePayload payload = CreateMinimalPayload();

            RbxWorldLoadResult result = await controller.LoadConfirmedAsync(payload, CancellationToken.None);
            Assert.IsTrue(result.Success, result.Error);

            FakePhysicsPort postPublishPort = host.PhysicsPort;
            Assert.IsTrue(outgoingPort.Disposed, "PublishReplacement always disposes the outgoing port.");
            Assert.AreNotSame(outgoingPort, postPublishPort,
                "Commit must have built a fresh port, mirroring RbxWorldHost.PublishReplacement.");

            LuaCsRbxApiBindings incomingApi = controller.CurrentRbxApi;
            Assert.AreSame(postPublishPort, incomingApi.WorldPhysics.Port,
                "WorldPhysics must end up bound to the port PublishReplacement just built, "
                    + "not the one that existed (and was disposed) at Stage time.");

            // Prove it with an actual raycast: only the post-publish port is told about a hit, and
            // only the incoming registry knows the marker part. If the disposed/outgoing port were
            // still attached, this raycast would silently return a miss (null), exactly like the
            // consequence described by the finding.
            RbxInstance markerPart = incomingApi.Registry.Create("Part");
            markerPart.Parent = incomingApi.Registry.WorldRoot;
            postPublishPort.HitInstanceId = markerPart.Id;

            RbxRaycastResult raycastResult = incomingApi.WorldPhysics.Raycast(
                RbxVector3.Zero, new RbxVector3(0f, -10f, 0f), null);

            Assert.IsNotNull(raycastResult, "Raycast reached a stale/disposed port instead of the "
                + "post-publish one and silently missed.");
            Assert.AreSame(markerPart, raycastResult.Instance);
        }

        private static RbxWorldPackagePayload CreateMinimalPayload()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            try
            {
                return RbxWorldPackageSerializer.Capture(
                    new RbxWorldPackageCaptureContext(
                        registry,
                        game,
                        new InMemoryPartPropertySink(),
                        new RbxWorldSettings { WorldId = WorldId },
                        capturedAtUtc: CapturedAtUtc));
            }
            finally
            {
                game.Destroy();
            }
        }

        private FileRbxWorldPackageStore CreatePackageStore()
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "CoreAI-PhysicsPortRepublish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            return new FileRbxWorldPackageStore(
                _temporaryDirectory,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
        }

        /// <summary>Physics port test double whose only behavior is what the test configures.</summary>
        private sealed class FakePhysicsPort : IRbxPhysicsPort
        {
            public event Action<InstanceId, InstanceId> ContactBegan;

            public event Action<InstanceId, InstanceId> ContactEnded;

            public bool Disposed { get; private set; }

            public InstanceId? HitInstanceId { get; set; }

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
                Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
            {
                if (HitInstanceId.HasValue && isEligible(HitInstanceId.Value))
                {
                    hit = new RbxPhysicsRaycastHit(
                        HitInstanceId.Value, RbxVector3.Zero, RbxVector3.Zero, RbxMaterialId.Plastic, 1d);
                    return true;
                }

                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
            }

            public void Dispose()
            {
                Disposed = true;
            }

            /// <summary>Kept so ContactBegan/ContactEnded aren't unused-event warnings if a future
            /// test wants to raise a contact through this fake instead of a raycast.</summary>
            internal void RaiseContactBegan(InstanceId first, InstanceId second)
            {
                ContactBegan?.Invoke(first, second);
            }
        }

        /// <summary>
        /// Fake IRbxWorldSessionHost that reproduces RbxWorldHost.PublishReplacement's port
        /// lifecycle (dispose the outgoing port, construct a fresh one) without a Unity scene.
        /// </summary>
        private sealed class FakePhysicsSessionHost : IRbxWorldSessionHost
        {
            private InstanceRegistry _registry;
            private RbxDataModel _game;
            private RbxWorldSettings _settings;

            public FakePhysicsSessionHost(InstanceRegistry registry, RbxDataModel game)
            {
                _registry = registry ?? throw new ArgumentNullException(nameof(registry));
                _game = game ?? throw new ArgumentNullException(nameof(game));
                _settings = new RbxWorldSettings { WorldId = registry.WorldId };
                PhysicsPort = new FakePhysicsPort();
            }

            public InstanceRegistry Registry => _registry;

            public RbxDataModel Game => _game;

            public IPartPropertySink PartSink => null;

            public IRbxCameraRig CameraRig => null;

            public IInputSource InputSource => null;

            public IClickPickSource PickSource => null;

            public RbxWorldSettings Settings => _settings;

            public FakePhysicsPort PhysicsPort { get; private set; }

            IRbxPhysicsPort IRbxWorldSessionHost.PhysicsPort => PhysicsPort;

            public IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload)
            {
                InMemoryCameraRig stagedCamera = new();
                RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                    payload,
                    new RbxWorldPackageRestoreOptions
                    {
                        CameraRig = stagedCamera
                    });
                return new Candidate(this, restored, stagedCamera, payload.Settings);
            }

            private sealed class Candidate : IRbxWorldSessionCandidate
            {
                private readonly FakePhysicsSessionHost _owner;
                private bool _committed;
                private bool _disposed;

                public Candidate(
                    FakePhysicsSessionHost owner,
                    RbxWorldPackageRestoreResult restored,
                    IRbxCameraRig cameraRig,
                    RbxWorldSettings settings)
                {
                    _owner = owner;
                    Registry = restored.Registry;
                    Game = restored.Game;
                    PartSink = restored.PartSink;
                    CameraRig = cameraRig;
                    Settings = settings;
                }

                public InstanceRegistry Registry { get; }

                public RbxDataModel Game { get; }

                public IPartPropertySink PartSink { get; }

                public IRbxCameraRig CameraRig { get; }

                public IInputSource InputSource => null;

                public IClickPickSource PickSource => null;

                public RbxWorldSettings Settings { get; }

                public void Commit()
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(Candidate));
                    }

                    if (_committed)
                    {
                        return;
                    }

                    InstanceRegistry outgoingRegistry = _owner._registry;
                    RbxDataModel outgoingGame = _owner._game;
                    FakePhysicsPort outgoingPort = _owner.PhysicsPort;

                    _owner._registry = Registry;
                    _owner._game = Game;
                    _owner._settings = Settings;

                    // WHY dispose-then-replace here: mirrors RbxWorldHost.PublishReplacement exactly
                    // ("PhysicsPort?.Dispose(); PhysicsPort = new UnityRbxPhysicsPort(binder);") so the
                    // fake reproduces the real ordering hazard instead of a simplified stand-in.
                    outgoingPort.Dispose();
                    _owner.PhysicsPort = new FakePhysicsPort();

                    _committed = true;
                    outgoingRegistry.MarkDetached();
                    outgoingGame.Destroy();
                }

                public void Dispose()
                {
                    if (_disposed || _committed)
                    {
                        return;
                    }

                    _disposed = true;
                    Registry.MarkDetached();
                    Game.Destroy();
                }
            }
        }

        private sealed class StubModSourceStore : ILuaModSourceStore, IRbxWorldModSourceStore
        {
            public void Save(string id, string source, LuaModManifest manifest)
            {
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                source = "";
                manifest = null;
                return false;
            }

            public System.Collections.Generic.IReadOnlyList<LuaModManifest> List()
            {
                return Array.Empty<LuaModManifest>();
            }

            public void SetActive(string id, bool active)
            {
            }

            public void Delete(string id)
            {
            }

            public UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
                System.Collections.Generic.IReadOnlyList<RbxWorldModSource> mods,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult<IRbxWorldModSourceReplacement>(new NoopReplacement(this));
            }

            private sealed class NoopReplacement : IRbxWorldModSourceReplacement
            {
                public NoopReplacement(StubModSourceStore owner)
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
    }
}
