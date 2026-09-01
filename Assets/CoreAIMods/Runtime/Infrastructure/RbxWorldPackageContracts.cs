using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Spatial;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using Microsoft.Extensions.AI;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>World settings carried independently from the instance tree.</summary>
    public sealed class RbxWorldSettings
    {
        public const float DefaultMetersPerStud = RbxSpace.DefaultMetersPerStud;
        public const double DefaultGravityStudsPerSecondSquared = 196.2d;
        public const string DeferredSignalBehavior = "Deferred";

        public string WorldId { get; set; } = "";

        public float MetersPerStud { get; set; } = DefaultMetersPerStud;

        public double GravityStudsPerSecondSquared { get; set; } =
            DefaultGravityStudsPerSecondSquared;

        public string SignalBehavior { get; set; } = DeferredSignalBehavior;
    }

    /// <summary>Exact source and portable metadata for one mod in a world package.</summary>
    public sealed class RbxWorldModSource
    {
        public RbxWorldModSource(LuaModManifest manifest, string source)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public LuaModManifest Manifest { get; }

        public string Source { get; }
    }

    /// <summary>Canonical in-memory payload shared by package files and future join snapshots.</summary>
    public sealed class RbxWorldPackagePayload
    {
        public RbxWorldPackagePayload(
            DateTime capturedAtUtc,
            RbxWorldSettings settings,
            InstanceTreeSnapshot tree,
            IReadOnlyDictionary<InstanceId, PartProperties> parts,
            RbxCFrame? cameraCFrame,
            IReadOnlyList<RbxWorldModSource> mods)
        {
            CapturedAtUtc = capturedAtUtc;
            Settings = settings;
            Tree = tree;
            Parts = parts;
            CameraCFrame = cameraCFrame;
            Mods = mods;
        }

        public DateTime CapturedAtUtc { get; }

        public RbxWorldSettings Settings { get; }

        public InstanceTreeSnapshot Tree { get; }

        public IReadOnlyDictionary<InstanceId, PartProperties> Parts { get; }

        public RbxCFrame? CameraCFrame { get; }

        public IReadOnlyList<RbxWorldModSource> Mods { get; }
    }

    /// <summary>Inputs used to capture the running Rbx composition without depending on file I/O.</summary>
    public sealed class RbxWorldPackageCaptureContext
    {
        public RbxWorldPackageCaptureContext(
            InstanceRegistry registry,
            RbxDataModel game,
            IPartPropertySink partSink,
            RbxWorldSettings settings,
            IRbxCameraRig cameraRig = null,
            ILuaModSourceStore modSourceStore = null,
            DateTime? capturedAtUtc = null)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Game = game ?? throw new ArgumentNullException(nameof(game));
            PartSink = partSink;
            CameraRig = cameraRig;
            ModSourceStore = modSourceStore;
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            CapturedAtUtc = capturedAtUtc;
        }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; }

        public IPartPropertySink PartSink { get; }

        public IRbxCameraRig CameraRig { get; }

        public ILuaModSourceStore ModSourceStore { get; }

        public RbxWorldSettings Settings { get; }

        public DateTime? CapturedAtUtc { get; }
    }

    /// <summary>Fresh-world adapters used while materializing a validated package payload.</summary>
    public sealed class RbxWorldPackageRestoreOptions
    {
        public IInstanceBackingBinder BackingBinder { get; set; }

        public IPartPropertySink PartSink { get; set; }

        public IRbxCameraRig CameraRig { get; set; }

        public ClassCatalog ClassCatalog { get; set; }

        /// <summary>
        /// Optional host scale transaction. It applies the validated scale before materialization and
        /// returns a rollback action used when restore fails. A null delegate keeps headless restore
        /// engine-free; a live Unity host supplies an adapter around its RbxSpace session policy.
        /// </summary>
        public Func<float, Action> BeginMetersPerStudRestore { get; set; }
    }

    /// <summary>A freshly restored DataModel plus the exact mod sources that must restart once.</summary>
    public sealed class RbxWorldPackageRestoreResult
    {
        internal RbxWorldPackageRestoreResult(
            InstanceRegistry registry,
            RbxDataModel game,
            IPartPropertySink partSink,
            IReadOnlyList<RbxWorldModSource> mods)
        {
            Registry = registry;
            Game = game;
            PartSink = partSink;
            Mods = mods;
        }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; }

        public IPartPropertySink PartSink { get; }

        public IReadOnlyList<RbxWorldModSource> Mods { get; }
    }

    /// <summary>Named format/validation failure suitable for user-facing load diagnostics.</summary>
    public sealed class RbxWorldPackageException : Exception
    {
        public RbxWorldPackageException(string message)
            : base(message)
        {
        }

        public RbxWorldPackageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>A disposable world candidate that remains invisible until commit.</summary>
    public interface IRbxWorldSessionCandidate : IDisposable
    {
        InstanceRegistry Registry { get; }

        RbxDataModel Game { get; }

        IPartPropertySink PartSink { get; }

        IRbxCameraRig CameraRig { get; }

        IInputSource InputSource { get; }

        IClickPickSource PickSource { get; }

        RbxWorldSettings Settings { get; }

        /// <summary>Publishes the already validated candidate without yielding.</summary>
        void Commit();
    }

    /// <summary>Host adapter that stages restored trees away from the published world.</summary>
    public interface IRbxWorldSessionHost
    {
        InstanceRegistry Registry { get; }

        RbxDataModel Game { get; }

        IPartPropertySink PartSink { get; }

        IRbxCameraRig CameraRig { get; }

        IInputSource InputSource { get; }

        IClickPickSource PickSource { get; }

        RbxWorldSettings Settings { get; }

        IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload);
    }

    /// <summary>Outcome returned by a confirmed production world replacement.</summary>
    public sealed class RbxWorldLoadResult
    {
        internal RbxWorldLoadResult(bool success, string error, int activeModsStarted)
        {
            Success = success;
            Error = error ?? "";
            ActiveModsStarted = activeModsStarted;
        }

        public bool Success { get; }

        public string Error { get; }

        public int ActiveModsStarted { get; }
    }

    /// <summary>Fail-closed AI restore request consumable only after player confirmation.</summary>
    public sealed class RbxWorldLoadRequest
    {
        internal RbxWorldLoadRequest(
            string requestId,
            string slot,
            string worldId,
            DateTime requestedAtUtc,
            DateTime expiresAtUtc)
        {
            RequestId = requestId ?? "";
            Slot = slot ?? "";
            WorldId = worldId ?? "";
            RequestedAtUtc = requestedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string RequestId { get; }

        public string Slot { get; }

        public string WorldId { get; }

        public DateTime RequestedAtUtc { get; }

        public DateTime ExpiresAtUtc { get; }

        public bool PlayerConfirmationRequired => true;
    }

    /// <summary>Host-facing metadata for one pending player confirmation; never exposes payload bytes.</summary>
    public sealed class RbxPendingWorldLoadRequest
    {
        internal RbxPendingWorldLoadRequest(
            string requestId,
            string slot,
            string worldId,
            DateTime requestedAtUtc,
            DateTime expiresAtUtc)
        {
            RequestId = requestId ?? "";
            Slot = slot ?? "";
            WorldId = worldId ?? "";
            RequestedAtUtc = requestedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string RequestId { get; }

        public string Slot { get; }

        public string WorldId { get; }

        public DateTime RequestedAtUtc { get; }

        public DateTime ExpiresAtUtc { get; }
    }

    /// <summary>Production save/load seam shared by AI requests and confirmed host actions.</summary>
    public interface IRbxWorldRuntimeService
    {
        event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested;

        RbxWorldPackagePayload CaptureCurrent();

        IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads();

        UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(
            string requestId,
            bool playerConfirmed,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Prepared exact source set that can roll back until world publication.</summary>
    public interface IRbxWorldModSourceReplacement : IDisposable
    {
        ILuaModSourceStore SourceStore { get; }

        void Activate();

        UniTask CompleteAsync(CancellationToken cancellationToken = default);

        UniTask RollbackAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Durable store capable of an all-or-nothing world source-set preparation.</summary>
    public interface IRbxWorldModSourceStore
    {
        UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
            IReadOnlyList<RbxWorldModSource> mods,
            CancellationToken cancellationToken = default);
    }

    /// <summary>AI tool that writes a create-once manual world package through the runtime service.</summary>
    public sealed class SaveWorldLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly IRbxWorldRuntimeService _service;
        private readonly IActorIdentityProvider _identityProvider;
        private readonly string _roleId;

        public SaveWorldLlmTool(
            IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider,
            string roleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _identityProvider = identityProvider
                ?? throw new ArgumentNullException(nameof(identityProvider));
            _roleId = roleId ?? BuiltInAgentRoleIds.Programmer;
        }

        public override string Name => "save_world";

        public override string Description =>
            "Save the current runtime world tree, exact mod sources, settings, parts, and camera "
            + "to a create-once manual slot.";

        public override string ParametersSchema => JsonParams(
            ("slot", "string", true, "Create-once manual save slot name."));

        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> function = ExecuteAsync;
            return AIFunctionFactory.Create(function, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        public async Task<string> ExecuteAsync(
            [Description("Create-once manual save slot name.")] string slot,
            CancellationToken cancellationToken = default)
        {
            ActorContext actor = _identityProvider.GetActorContext(_roleId);
            RbxWorldPackageWriteResult result = await _service.SaveManualAsync(
                actor,
                slot,
                cancellationToken);
            return JsonConvert.SerializeObject(new
            {
                success = result.Success,
                path = result.Path,
                error = result.Error
            });
        }
    }

    /// <summary>AI tool that stages a manual load request without applying it.</summary>
    public sealed class LoadWorldLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly IRbxWorldRuntimeService _service;
        private readonly IActorIdentityProvider _identityProvider;
        private readonly string _roleId;

        public LoadWorldLlmTool(
            IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider,
            string roleId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _identityProvider = identityProvider
                ?? throw new ArgumentNullException(nameof(identityProvider));
            _roleId = roleId ?? BuiltInAgentRoleIds.Programmer;
        }

        public override string Name => "load_world";

        public override string Description =>
            "Request loading a manual world package. This never applies the package directly; "
            + "the player must confirm the returned request in host UI.";

        public override string ParametersSchema => JsonParams(
            ("slot", "string", true, "Existing manual save slot name."));

        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> function = ExecuteAsync;
            return AIFunctionFactory.Create(function, new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
        }

        public async Task<string> ExecuteAsync(
            [Description("Existing manual save slot name.")] string slot,
            CancellationToken cancellationToken = default)
        {
            ActorContext actor = _identityProvider.GetActorContext(_roleId);
            RbxWorldLoadRequest request = await _service.RequestManualLoadAsync(
                actor,
                slot,
                cancellationToken);
            return JsonConvert.SerializeObject(new
            {
                success = false,
                status = "player_confirmation_required",
                player_confirmation_required = request.PlayerConfirmationRequired,
                request_id = request.RequestId,
                slot = request.Slot,
                world_id = request.WorldId
            });
        }
    }

    /// <summary>Mods-assembly adapter that stages package trees around a scene RbxWorldHost.</summary>
    public sealed class RbxWorldSessionHostAdapter : IRbxWorldSessionHost
    {
        private readonly RbxWorldHost _host;
        private RbxWorldSettings _settings;

        internal Action BeforePublishForTests { get; set; }

        public RbxWorldSessionHostAdapter(RbxWorldHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _host.Initialize();
            _settings = new RbxWorldSettings
            {
                WorldId = _host.Registry.WorldId,
                MetersPerStud = _host.MetersPerStud
            };
        }

        public InstanceRegistry Registry => _host.Registry;

        public RbxDataModel Game => _host.Game;

        public IPartPropertySink PartSink => _host.Binder;

        public IRbxCameraRig CameraRig => _host.CameraRig;

        public IInputSource InputSource => _host.InputSource;

        public IClickPickSource PickSource => _host.PickSource;

        public RbxWorldSettings Settings => CloneSettings(_settings);

        public IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            Camera sceneCamera = _host.SceneCamera;
            if (payload.CameraCFrame.HasValue && sceneCamera == null)
            {
                throw new RbxWorldPackageException(
                    "The staged world contains camera state but the RbxWorldHost has no camera.");
            }

            GameObject stagingRoot = new("CoreAI_RbxWorld_Staging");
            stagingRoot.transform.SetParent(_host.transform, false);
            stagingRoot.SetActive(false);
            InstanceGameObjectBinder stagedBinder = new(stagingRoot.transform);
            PublishableCameraRig stagedCamera = sceneCamera != null
                ? new PublishableCameraRig(
                    _host.CameraRig?.GetCFrame() ?? RbxCFrame.Identity)
                : null;
            Action rollbackScale = null;
            try
            {
                RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                    payload,
                    new RbxWorldPackageRestoreOptions
                    {
                        BackingBinder = stagedBinder,
                        PartSink = stagedBinder,
                        CameraRig = stagedCamera,
                        BeginMetersPerStudRestore = metersPerStud =>
                        {
                            rollbackScale = RbxSpace.BeginSessionReplacement(metersPerStud);
                            return rollbackScale;
                        }
                    });
                IClickPickSource pickSource = sceneCamera != null
                    ? new UnityClickPickSource(sceneCamera, stagedBinder)
                    : null;
                return new Candidate(
                    this,
                    stagingRoot,
                    stagedBinder,
                    restored.Registry,
                    restored.Game,
                    stagedCamera,
                    new UnityNewInputSource(),
                    pickSource,
                    CloneSettings(payload.Settings),
                    rollbackScale);
            }
            catch
            {
                DisposeCandidate(
                    stagingRoot,
                    stagedBinder,
                    null,
                    null,
                    rollbackScale);
                throw;
            }
        }

        private void Commit(Candidate candidate)
        {
            CameraSnapshot outgoingCamera = CameraSnapshot.Capture(_host.SceneCamera);
            try
            {
                candidate.Root.name = "CoreAI_RbxWorld_Active";
                candidate.Root.SetActive(true);
                if (_host.SceneCamera != null && candidate.PublishableCamera != null)
                {
                    UnityCameraRig liveCamera = new(
                        _host.SceneCamera.transform,
                        candidate.Binder);
                    candidate.PublishableCamera.Publish(liveCamera);
                }

                BeforePublishForTests?.Invoke();

                _host.PublishReplacement(
                    candidate.Root,
                    candidate.Binder,
                    candidate.Registry,
                    candidate.Game,
                    candidate.CameraRig,
                    candidate.InputSource,
                    candidate.PickSource,
                    candidate.Settings.MetersPerStud);
                _settings = CloneSettings(candidate.Settings);
                candidate.MarkCommitted();
            }
            catch
            {
                candidate.Root.SetActive(false);
                outgoingCamera.Restore();
                throw;
            }
        }

        private static void DisposeCandidate(
            GameObject root,
            InstanceGameObjectBinder binder,
            InstanceRegistry registry,
            RbxDataModel game,
            Action rollbackScale)
        {
            try
            {
                binder?.BeginHostTeardown();
            }
            catch
            {
            }

            try
            {
                registry?.MarkDetached();
            }
            catch
            {
            }

            try
            {
                game?.Destroy();
            }
            catch
            {
            }

            try
            {
                rollbackScale?.Invoke();
            }
            catch
            {
            }

            DestroyUnityObject(root);
        }

        private static RbxWorldSettings CloneSettings(RbxWorldSettings source)
        {
            return new RbxWorldSettings
            {
                WorldId = source.WorldId,
                MetersPerStud = source.MetersPerStud,
                GravityStudsPerSecondSquared = source.GravityStudsPerSecondSquared,
                SignalBehavior = source.SignalBehavior
            };
        }

        private static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private sealed class PublishableCameraRig : IRbxCameraRig
        {
            private readonly InMemoryCameraRig _staged = new();
            private IRbxCameraRig _published;

            public PublishableCameraRig(RbxCFrame initialCFrame)
            {
                _staged.SetCFrame(in initialCFrame);
            }

            public RbxCFrame GetCFrame()
            {
                return (_published ?? _staged).GetCFrame();
            }

            public void SetCFrame(in RbxCFrame cframe)
            {
                (_published ?? _staged).SetCFrame(in cframe);
            }

            public bool Follow(InstanceId id)
            {
                return (_published ?? _staged).Follow(id);
            }

            public void StopFollowing()
            {
                (_published ?? _staged).StopFollowing();
            }

            public void Publish(IRbxCameraRig live)
            {
                RbxCFrame cframe = _staged.GetCFrame();
                live.SetCFrame(in cframe);
                InstanceId? followTarget = _staged.FollowTarget;
                if (followTarget.HasValue && !live.Follow(followTarget.Value))
                {
                    throw new InvalidOperationException(
                        "The staged camera follow target has no live backing object.");
                }

                if (!followTarget.HasValue)
                {
                    live.StopFollowing();
                }

                _published = live;
            }
        }

        private readonly struct CameraSnapshot
        {
            private readonly Camera _camera;
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;
            private readonly RbxCameraFollower _follower;
            private readonly bool _followerEnabled;
            private readonly Transform _followTarget;
            private readonly Vector3 _followOffset;

            private CameraSnapshot(
                Camera camera,
                RbxCameraFollower follower)
            {
                _camera = camera;
                _position = camera.transform.position;
                _rotation = camera.transform.rotation;
                _follower = follower;
                _followerEnabled = follower != null && follower.enabled;
                _followTarget = follower != null ? follower.Target : null;
                _followOffset = follower != null ? follower.Offset : Vector3.zero;
            }

            public static CameraSnapshot Capture(Camera camera)
            {
                return camera == null
                    ? default
                    : new CameraSnapshot(
                        camera,
                        camera.GetComponent<RbxCameraFollower>());
            }

            public void Restore()
            {
                if (_camera == null)
                {
                    return;
                }

                _camera.transform.SetPositionAndRotation(_position, _rotation);
                RbxCameraFollower follower = _follower
                    ?? _camera.GetComponent<RbxCameraFollower>();
                if (follower == null)
                {
                    return;
                }

                follower.Target = _followTarget;
                follower.Offset = _followOffset;
                follower.enabled = _followerEnabled;
            }
        }

        private sealed class Candidate : IRbxWorldSessionCandidate
        {
            private readonly RbxWorldSessionHostAdapter _owner;
            private Action _rollbackScale;
            private bool _committed;
            private bool _disposed;

            public Candidate(
                RbxWorldSessionHostAdapter owner,
                GameObject root,
                InstanceGameObjectBinder binder,
                InstanceRegistry registry,
                RbxDataModel game,
                PublishableCameraRig cameraRig,
                IInputSource inputSource,
                IClickPickSource pickSource,
                RbxWorldSettings settings,
                Action rollbackScale)
            {
                _owner = owner;
                Root = root;
                Binder = binder;
                Registry = registry;
                Game = game;
                PublishableCamera = cameraRig;
                InputSource = inputSource;
                PickSource = pickSource;
                Settings = settings;
                _rollbackScale = rollbackScale;
            }

            public GameObject Root { get; }

            public InstanceGameObjectBinder Binder { get; }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public IPartPropertySink PartSink => Binder;

            public IRbxCameraRig CameraRig => PublishableCamera;

            public PublishableCameraRig PublishableCamera { get; }

            public IInputSource InputSource { get; }

            public IClickPickSource PickSource { get; }

            public RbxWorldSettings Settings { get; }

            public void Commit()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(Candidate));
                }

                if (!_committed)
                {
                    _owner.Commit(this);
                }
            }

            public void Dispose()
            {
                if (_disposed || _committed)
                {
                    return;
                }

                _disposed = true;
                DisposeCandidate(Root, Binder, Registry, Game, _rollbackScale);
                _rollbackScale = null;
            }

            public void MarkCommitted()
            {
                _committed = true;
                _rollbackScale = null;
            }
        }
    }

    /// <summary>Engine-free session host used by headless players and composition tests.</summary>
    public sealed class HeadlessRbxWorldSessionHost : IRbxWorldSessionHost
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private RbxWorldSettings _settings;

        public HeadlessRbxWorldSessionHost(
            InstanceRegistry registry,
            RbxDataModel game,
            RbxWorldSettings settings = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _settings = CloneSettings(settings ?? new RbxWorldSettings
            {
                WorldId = registry.WorldId
            });
        }

        public InstanceRegistry Registry => _registry;

        public RbxDataModel Game => _game;

        public IPartPropertySink PartSink => null;

        public IRbxCameraRig CameraRig => null;

        public IInputSource InputSource => null;

        public IClickPickSource PickSource => null;

        public RbxWorldSettings Settings => CloneSettings(_settings);

        public IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload)
        {
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(payload);
            return new Candidate(this, restored, CloneSettings(payload.Settings));
        }

        private static RbxWorldSettings CloneSettings(RbxWorldSettings source)
        {
            return new RbxWorldSettings
            {
                WorldId = source.WorldId,
                MetersPerStud = source.MetersPerStud,
                GravityStudsPerSecondSquared = source.GravityStudsPerSecondSquared,
                SignalBehavior = source.SignalBehavior
            };
        }

        private sealed class Candidate : IRbxWorldSessionCandidate
        {
            private readonly HeadlessRbxWorldSessionHost _owner;
            private bool _committed;
            private bool _disposed;

            public Candidate(
                HeadlessRbxWorldSessionHost owner,
                RbxWorldPackageRestoreResult restored,
                RbxWorldSettings settings)
            {
                _owner = owner;
                Registry = restored.Registry;
                Game = restored.Game;
                Settings = CloneSettings(settings);
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public IPartPropertySink PartSink => null;

            public IRbxCameraRig CameraRig => null;

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
                _owner._registry = Registry;
                _owner._game = Game;
                _owner._settings = CloneSettings(Settings);
                _committed = true;
                try
                {
                    outgoingRegistry.MarkDetached();
                    outgoingGame.Destroy();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                if (_disposed || _committed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    Registry.MarkDetached();
                    Game.Destroy();
                }
                catch
                {
                }
            }
        }
    }
}
namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// Owns the mutable production world/Lua session. A load restores an isolated tree, builds a
    /// fresh VM stack, starts every active source strictly once, atomically replaces the durable
    /// source set, publishes the candidate, then permanently tears down the outgoing runtime.
    /// </summary>
    public sealed class RbxWorldRuntimeSessionController : IRbxWorldRuntimeService, IDisposable
    {
        private const int MaximumPendingLoadRequests = 8;
        private static readonly TimeSpan DefaultPendingLoadTimeToLive = TimeSpan.FromMinutes(2d);

        private readonly object _gate = new();
        private readonly IRbxWorldSessionHost _host;
        private readonly IRbxWorldPackageStore _packageStore;
        private readonly IRbxWorldModSourceStore _transactionalSourceStore;
        private readonly Func<IRbxWorldSessionCandidate, INetworkBridge, LuaCsRbxApiBindings>
            _rbxApiFactory;
        private readonly Func<LuaCsRbxApiBindings, ILuaModSourceStore, ILuaModStore,
            ILuaScriptVersionStore, LuaCsModStack>
            _stackFactory;
        private readonly Action<LuaCsModStack, LuaCsRbxApiBindings> _wireTeardown;
        private readonly INetworkBridge _networkBridge;
        private readonly LuaCapabilities _hostGrant;
        private readonly bool _allowFull;
        private readonly ILuaModStore _modStore;
        private readonly ILuaScriptVersionStore _versionStore;
        private readonly Action<string> _diagnostics;
        private readonly Dictionary<string, PendingLoad> _pendingLoads =
            new(StringComparer.Ordinal);
        private Session _current;
        private bool _disposed;
        private bool _loadInProgress;
        private Func<DateTime> _utcNow = () => DateTime.UtcNow;
        private TimeSpan _pendingLoadTimeToLive = DefaultPendingLoadTimeToLive;

        public RbxWorldRuntimeSessionController(
            IRbxWorldSessionHost host,
            IRbxWorldPackageStore packageStore,
            ILuaModSourceStore durableSourceStore,
            LuaCsModStack initialStack,
            LuaCsRbxApiBindings initialRbxApi,
            Func<IRbxWorldSessionCandidate, INetworkBridge, LuaCsRbxApiBindings> rbxApiFactory,
            Func<LuaCsRbxApiBindings, ILuaModSourceStore, ILuaModStore,
                ILuaScriptVersionStore, LuaCsModStack> stackFactory,
            Action<LuaCsModStack, LuaCsRbxApiBindings> wireTeardown,
            INetworkBridge networkBridge,
            LuaCapabilities hostGrant,
            bool allowFull,
            ILuaModStore modStore = null,
            ILuaScriptVersionStore versionStore = null,
            Action<string> diagnostics = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _packageStore = packageStore ?? throw new ArgumentNullException(nameof(packageStore));
            ILuaModSourceStore initialSourceStore = durableSourceStore
                ?? throw new ArgumentNullException(nameof(durableSourceStore));
            _transactionalSourceStore = initialSourceStore as IRbxWorldModSourceStore;
            _rbxApiFactory = rbxApiFactory
                ?? throw new ArgumentNullException(nameof(rbxApiFactory));
            _stackFactory = stackFactory ?? throw new ArgumentNullException(nameof(stackFactory));
            _wireTeardown = wireTeardown ?? throw new ArgumentNullException(nameof(wireTeardown));
            _networkBridge = networkBridge ?? new NullNetworkBridge();
            _hostGrant = hostGrant;
            _allowFull = allowFull;
            _modStore = modStore;
            _versionStore = versionStore ?? new NullLuaScriptVersionStore();
            _diagnostics = diagnostics;
            _current = new Session(
                initialStack ?? throw new ArgumentNullException(nameof(initialStack)),
                initialRbxApi ?? throw new ArgumentNullException(nameof(initialRbxApi)),
                initialSourceStore,
                null,
                null);
            Runtime = new ActiveLuaModRuntime(this);
            Executor = new ActiveLuaExecutor(this);
            Stack = new LuaCsModStack(() => Current.Stack);
            LogicSlots = new LuaCsLogicSlots(() => Current.Stack.GameplayBindings.LogicSlots);
            SourceStore = new ActiveLuaModSourceStore(this);
        }

        public event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested;

        /// <summary>Stable facade that routes every call to the currently published runtime.</summary>
        public ILuaModRuntime Runtime { get; }

        /// <summary>Stable facade that routes one-off and mutation-envelope execution to the active world.</summary>
        public LuaTool.ILuaExecutor Executor { get; }

        /// <summary>Stable stack view whose properties resolve from the active session.</summary>
        public LuaCsModStack Stack { get; }

        /// <summary>Stable logic-slot view used by scene consumers across world replacement.</summary>
        public LuaCsLogicSlots LogicSlots { get; }

        /// <summary>Stable source-store facade routed to the currently published session.</summary>
        public ILuaModSourceStore SourceStore { get; }

        public LuaCsModRuntime CurrentConcreteRuntime => Current.Stack.Runtime;

        public LuaCsRbxApiBindings CurrentRbxApi => Current.RbxApi;

        public RbxWorldPackagePayload CaptureCurrent()
        {
            DemandActive();
            Session session = Current;
            return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                session.RbxApi.Registry,
                session.RbxApi.Game,
                session.RbxApi.PartSink,
                _host.Settings,
                session.RbxApi.CameraRig,
                session.SourceStore));
        }

        public IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads()
        {
            lock (_gate)
            {
                DemandActiveLocked();
                RemoveExpiredPendingLoadsLocked(_utcNow());
                List<RbxPendingWorldLoadRequest> pending = new(_pendingLoads.Count);
                foreach (PendingLoad request in _pendingLoads.Values)
                {
                    pending.Add(request.ToPublicRequest());
                }

                pending.Sort((left, right) =>
                    left.RequestedAtUtc.CompareTo(right.RequestedAtUtc));
                return pending;
            }
        }

        internal void ConfigurePendingLoadClockForTests(
            Func<DateTime> utcNow,
            TimeSpan timeToLive)
        {
            if (utcNow == null)
            {
                throw new ArgumentNullException(nameof(utcNow));
            }

            if (timeToLive <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeToLive));
            }

            lock (_gate)
            {
                _utcNow = utcNow;
                _pendingLoadTimeToLive = timeToLive;
            }
        }

        public async UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default)
        {
            DemandTrusted(caller);
            RbxWorldPackagePayload payload = CaptureCurrent();
            return await _packageStore.CreateManualAsync(slot, payload, cancellationToken);
        }

        public async UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default)
        {
            DemandTrusted(caller);
            RbxWorldPackagePayload payload = await _packageStore.LoadManualAsync(
                slot, cancellationToken);
            string requestId = Guid.NewGuid().ToString("N");
            RbxPendingWorldLoadRequest publicRequest;
            lock (_gate)
            {
                DemandActiveLocked();
                DateTime requestedAtUtc = _utcNow();
                RemoveExpiredPendingLoadsLocked(requestedAtUtc);
                RemovePendingSlotLocked(slot);
                if (_pendingLoads.Count >= MaximumPendingLoadRequests)
                {
                    RemoveOldestPendingLoadLocked();
                }

                PendingLoad pending = new(
                    requestId,
                    caller.ActorId,
                    slot,
                    payload,
                    requestedAtUtc,
                    requestedAtUtc + _pendingLoadTimeToLive);
                _pendingLoads.Add(requestId, pending);
                publicRequest = pending.ToPublicRequest();
            }

            RaiseManualLoadConfirmationRequested(publicRequest);
            return new RbxWorldLoadRequest(
                publicRequest.RequestId,
                publicRequest.Slot,
                publicRequest.WorldId,
                publicRequest.RequestedAtUtc,
                publicRequest.ExpiresAtUtc);
        }

        public async UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(
            string requestId,
            bool playerConfirmed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingLoad pending;
            lock (_gate)
            {
                DemandActiveLocked();
                RemoveExpiredPendingLoadsLocked(_utcNow());
                string normalized = requestId?.Trim() ?? "";
                if (!_pendingLoads.TryGetValue(normalized, out pending))
                {
                    return new RbxWorldLoadResult(
                        false,
                        "World-load confirmation token is unknown, expired, or already consumed.",
                        0);
                }

                _pendingLoads.Remove(normalized);
            }

            if (!playerConfirmed)
            {
                return new RbxWorldLoadResult(
                    false,
                    "Player confirmation is required; the live world was not changed.",
                    0);
            }

            return await LoadConfirmedAsync(pending.Payload, cancellationToken);
        }

        public async UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            DemandActive();
            if (payload == null)
            {
                return new RbxWorldLoadResult(false, "World package payload is required.", 0);
            }

            string activeFullMod = FindActiveFullCapabilityMod(payload.Mods);
            if (activeFullMod.Length > 0)
            {
                return new RbxWorldLoadResult(
                    false,
                    "Active Full-capability mod '" + activeFullMod
                        + "' cannot be isolated during staged world restore.",
                    0);
            }

            if (_transactionalSourceStore == null)
            {
                return new RbxWorldLoadResult(
                    false,
                    "The configured mod source store cannot atomically replace a world source set.",
                    0);
            }

            lock (_gate)
            {
                if (_loadInProgress)
                {
                    return new RbxWorldLoadResult(
                        false, "Another confirmed world load is already in progress.", 0);
                }

                _loadInProgress = true;
            }

            IRbxWorldSessionCandidate candidate = null;
            StagedNetworkBridge stagedNetwork = null;
            LuaCsRbxApiBindings stagedRbxApi = null;
            LuaCsModStack stagedStack = null;
            IRbxWorldModSourceReplacement sourceReplacement = null;
            BufferedLuaModStore stagedModStore = new(_modStore);
            DeferredLuaScriptVersionStore stagedVersions = new(_versionStore);
            bool published = false;
            try
            {
                sourceReplacement = await _transactionalSourceStore.PrepareExactReplacementAsync(
                    payload.Mods, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                candidate = _host.Stage(payload);
                stagedNetwork = new StagedNetworkBridge(_networkBridge);
                stagedRbxApi = _rbxApiFactory(candidate, stagedNetwork);
                stagedStack = _stackFactory(
                    stagedRbxApi,
                    sourceReplacement.SourceStore,
                    stagedModStore,
                    stagedVersions);
                _wireTeardown(stagedStack, stagedRbxApi);
                CopyDeclaredLogicSlots(
                    Current.Stack.GameplayBindings.LogicSlots,
                    stagedStack.GameplayBindings.LogicSlots);
                int started = stagedStack.Runtime.RehydrateExactOrThrow(
                    _hostGrant, _allowFull);
                int expected = CountActive(payload.Mods);
                if (started != expected)
                {
                    throw new InvalidOperationException(
                        "The staged runtime started " + started.ToString(CultureInfo.InvariantCulture)
                        + " active mods; expected " + expected.ToString(CultureInfo.InvariantCulture) + ".");
                }

                stagedNetwork.PrepareActivation();
                sourceReplacement.Activate();

                Session outgoing;
                Session incoming = new(
                    stagedStack,
                    stagedRbxApi,
                    sourceReplacement.SourceStore,
                    candidate,
                    stagedNetwork);
                LuaCsLogicSlots previousSlots;
                LuaCsLogicSlots nextSlots = stagedStack.GameplayBindings.LogicSlots;
                lock (_gate)
                {
                    DemandActiveLocked();
                    outgoing = _current;
                    previousSlots = outgoing.Stack.GameplayBindings.LogicSlots;
                    candidate.Commit();
                    _current = incoming;
                    published = true;
                }

                IRbxWorldModSourceReplacement publishedSourceReplacement = sourceReplacement;
                sourceReplacement = null;

                ReportDegradedActivation(stagedModStore.ActivateAfterPublication());
                ReportDegradedActivation(stagedVersions.ActivateAfterPublication());
                try
                {
                    LogicSlots.OnActiveTargetChanging(previousSlots, nextSlots);
                    ((ActiveLuaModRuntime)Runtime).OnSessionChanging(
                        outgoing.Stack.Runtime, incoming.Stack.Runtime);
                }
                catch (Exception ex)
                {
                    ReportDegradedActivation(
                        "Published stable-facade retargeting was degraded: " + ex.Message);
                }
                ReportDegradedActivation(stagedNetwork.ActivateAfterPublication());
                candidate = null;
                stagedNetwork = null;
                stagedRbxApi = null;
                stagedStack = null;
                ShutdownOutgoing(outgoing);
                try
                {
                    await publishedSourceReplacement.CompleteAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    ReportDegradedActivation(
                        "Published world source completion failed; the live source version "
                            + "was retained and was not disposed: " + ex.Message);
                }

                return new RbxWorldLoadResult(true, "", started);
            }
            catch (Exception ex)
            {
                if (!published)
                {
                    ShutdownStaged(stagedStack, stagedRbxApi, stagedNetwork, candidate);
                    if (sourceReplacement != null)
                    {
                        try
                        {
                            await sourceReplacement.RollbackAsync(CancellationToken.None);
                        }
                        catch (Exception rollbackException)
                        {
                            return new RbxWorldLoadResult(
                                false,
                                ex.Message + " Source rollback also failed: "
                                    + rollbackException.Message,
                                0);
                        }
                    }
                }

                return new RbxWorldLoadResult(false, ex.Message, 0);
            }
            finally
            {
                sourceReplacement?.Dispose();
                lock (_gate)
                {
                    _loadInProgress = false;
                }
            }
        }

        /// <summary>Advances only the currently published scheduler and runtime.</summary>
        public void PumpFrame(ActorContext actorContext, float deltaSeconds)
        {
            Session session = Current;
            session.RbxApi.Scheduler.Advance(deltaSeconds);
            session.Stack.Runtime.Tick(actorContext, deltaSeconds);
        }

        public void Dispose()
        {
            Session outgoing;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _pendingLoads.Clear();
                outgoing = _current;
            }

            ShutdownOutgoing(outgoing);
        }

        private Session Current
        {
            get
            {
                lock (_gate)
                {
                    DemandActiveLocked();
                    return _current;
                }
            }
        }

        private void DemandActive()
        {
            lock (_gate)
            {
                DemandActiveLocked();
            }
        }

        private void RemoveListenerFromActiveRuntime(Action<LuaCsModRuntime> remove)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                remove(_current.Stack.Runtime);
            }
        }

        private void DemandActiveLocked()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RbxWorldRuntimeSessionController));
            }
        }

        private static void DemandTrusted(ActorContext caller)
        {
            if (!caller.IsTrusted)
            {
                throw new InvalidOperationException(
                    "Actor context was not issued by an identity provider.");
            }
        }

        private void RemoveExpiredPendingLoadsLocked(DateTime utcNow)
        {
            List<string> expired = new();
            foreach (KeyValuePair<string, PendingLoad> pair in _pendingLoads)
            {
                if (pair.Value.ExpiresAtUtc <= utcNow)
                {
                    expired.Add(pair.Key);
                }
            }

            for (int index = 0; index < expired.Count; index++)
            {
                _pendingLoads.Remove(expired[index]);
            }
        }

        private void RemovePendingSlotLocked(string slot)
        {
            string normalizedSlot = slot?.Trim() ?? "";
            List<string> replaced = new();
            foreach (KeyValuePair<string, PendingLoad> pair in _pendingLoads)
            {
                if (string.Equals(
                        pair.Value.Slot,
                        normalizedSlot,
                        StringComparison.Ordinal))
                {
                    replaced.Add(pair.Key);
                }
            }

            for (int index = 0; index < replaced.Count; index++)
            {
                _pendingLoads.Remove(replaced[index]);
            }
        }

        private void RemoveOldestPendingLoadLocked()
        {
            PendingLoad oldest = null;
            foreach (PendingLoad pending in _pendingLoads.Values)
            {
                if (oldest == null || pending.RequestedAtUtc < oldest.RequestedAtUtc)
                {
                    oldest = pending;
                }
            }

            if (oldest != null)
            {
                _pendingLoads.Remove(oldest.RequestId);
            }
        }

        private void RaiseManualLoadConfirmationRequested(
            RbxPendingWorldLoadRequest request)
        {
            Action<RbxPendingWorldLoadRequest> handlers =
                ManualLoadConfirmationRequested;
            if (handlers == null)
            {
                return;
            }

            Delegate[] subscriptions = handlers.GetInvocationList();
            for (int index = 0; index < subscriptions.Length; index++)
            {
                try
                {
                    ((Action<RbxPendingWorldLoadRequest>)subscriptions[index])(request);
                }
                catch
                {
                }
            }
        }

        private void ReportDegradedActivation(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || _diagnostics == null)
            {
                return;
            }

            try
            {
                _diagnostics(message);
            }
            catch
            {
            }
        }

        private static int CountActive(IReadOnlyList<RbxWorldModSource> mods)
        {
            int count = 0;
            for (int index = 0; index < mods.Count; index++)
            {
                if (mods[index].Manifest.Active)
                {
                    count++;
                }
            }

            return count;
        }

        private static string FindActiveFullCapabilityMod(
            IReadOnlyList<RbxWorldModSource> mods)
        {
            if (mods == null)
            {
                return "";
            }

            for (int index = 0; index < mods.Count; index++)
            {
                LuaModManifest manifest = mods[index]?.Manifest;
                if (manifest == null || !manifest.Active)
                {
                    continue;
                }

                if (Enum.TryParse(
                        manifest.Capabilities,
                        ignoreCase: true,
                        out LuaCapabilities declared)
                    && (declared & LuaCapabilities.Full) != 0)
                {
                    return manifest.Id?.Trim() ?? "<unknown>";
                }
            }

            return "";
        }

        private static void CopyDeclaredLogicSlots(
            LuaCsLogicSlots outgoing,
            LuaCsLogicSlots staged)
        {
            IReadOnlyCollection<string> declarations = outgoing.DeclaredSlots;
            foreach (string declaration in declarations)
            {
                staged.DeclareSlot(declaration);
            }
        }

        private static void ShutdownOutgoing(Session outgoing)
        {
            try
            {
                outgoing.Stack.Runtime.ShutdownWithoutPersistence();
            }
            catch
            {
            }

            try
            {
                outgoing.RbxApi.Dispose();
            }
            catch
            {
            }

            try
            {
                outgoing.Network?.Dispose();
            }
            catch
            {
            }
        }

        private static void ShutdownStaged(
            LuaCsModStack stack,
            LuaCsRbxApiBindings rbxApi,
            StagedNetworkBridge network,
            IRbxWorldSessionCandidate candidate)
        {
            try
            {
                stack?.Runtime.ShutdownWithoutPersistence();
            }
            catch
            {
            }

            try
            {
                rbxApi?.Dispose();
            }
            catch
            {
            }

            try
            {
                network?.Dispose();
            }
            catch
            {
            }

            try
            {
                candidate?.Dispose();
            }
            catch
            {
            }
        }

        private sealed class Session
        {
            public Session(
                LuaCsModStack stack,
                LuaCsRbxApiBindings rbxApi,
                ILuaModSourceStore sourceStore,
                IRbxWorldSessionCandidate candidate,
                StagedNetworkBridge network)
            {
                Stack = stack;
                RbxApi = rbxApi;
                SourceStore = sourceStore;
                Candidate = candidate;
                Network = network;
            }

            public LuaCsModStack Stack { get; }

            public LuaCsRbxApiBindings RbxApi { get; }

            public ILuaModSourceStore SourceStore { get; }

            public IRbxWorldSessionCandidate Candidate { get; }

            public StagedNetworkBridge Network { get; }
        }

        private sealed class PendingLoad
        {
            public PendingLoad(
                string requestId,
                string actorId,
                string slot,
                RbxWorldPackagePayload payload,
                DateTime requestedAtUtc,
                DateTime expiresAtUtc)
            {
                RequestId = requestId ?? "";
                ActorId = actorId;
                Slot = slot?.Trim() ?? "";
                Payload = payload;
                RequestedAtUtc = requestedAtUtc;
                ExpiresAtUtc = expiresAtUtc;
            }

            public string RequestId { get; }

            public string ActorId { get; }

            public string Slot { get; }

            public RbxWorldPackagePayload Payload { get; }

            public DateTime RequestedAtUtc { get; }

            public DateTime ExpiresAtUtc { get; }

            public RbxPendingWorldLoadRequest ToPublicRequest()
            {
                return new RbxPendingWorldLoadRequest(
                    RequestId,
                    Slot,
                    Payload.Settings.WorldId,
                    RequestedAtUtc,
                    ExpiresAtUtc);
            }
        }

        private sealed class DeferredLuaScriptVersionStore : ILuaScriptVersionStore
        {
            private readonly ILuaScriptVersionStore _inner;
            private readonly List<Action<ILuaScriptVersionStore>> _mutations = new();
            private bool _active;

            public DeferredLuaScriptVersionStore(ILuaScriptVersionStore inner)
            {
                _inner = inner ?? new NullLuaScriptVersionStore();
            }

            public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
            {
                return _inner.TryGetSnapshot(scriptKey, out snapshot);
            }

            public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
            {
                Mutate(store => store.RecordSuccessfulExecution(scriptKey, executedLuaSource));
            }

            public void SeedOriginal(
                string scriptKey,
                string originalLuaSource,
                bool overwriteExistingOriginal = false)
            {
                Mutate(store => store.SeedOriginal(
                    scriptKey,
                    originalLuaSource,
                    overwriteExistingOriginal));
            }

            public void ResetToOriginal(string scriptKey)
            {
                Mutate(store => store.ResetToOriginal(scriptKey));
            }

            public void ResetToRevision(string scriptKey, int revisionIndex)
            {
                Mutate(store => store.ResetToRevision(scriptKey, revisionIndex));
            }

            public void ResetAllToOriginal()
            {
                Mutate(store => store.ResetAllToOriginal());
            }

            public IReadOnlyList<string> GetKnownKeys()
            {
                return _inner.GetKnownKeys();
            }

            public string BuildProgrammerPromptSection(string scriptKey)
            {
                return _inner.BuildProgrammerPromptSection(scriptKey);
            }

            public string ActivateAfterPublication()
            {
                if (_active)
                {
                    return "";
                }

                List<string> failures = new();
                for (int index = 0; index < _mutations.Count; index++)
                {
                    try
                    {
                        _mutations[index](_inner);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex.Message);
                    }
                }

                _mutations.Clear();
                _active = true;
                return failures.Count == 0
                    ? ""
                    : "Published Lua revision replay was degraded: "
                        + string.Join("; ", failures);
            }

            private void Mutate(Action<ILuaScriptVersionStore> mutation)
            {
                if (_active)
                {
                    mutation(_inner);
                    return;
                }

                _mutations.Add(mutation);
            }
        }

        private sealed class BufferedLuaModStore : ILuaModStore
        {
            private readonly ILuaModStore _inner;
            private readonly List<StoreOperation> _operations = new();
            private readonly Dictionary<(string ModId, string Key), string> _values = new();
            private readonly HashSet<string> _clearedMods = new(StringComparer.Ordinal);
            private bool _active;

            public BufferedLuaModStore(ILuaModStore inner)
            {
                _inner = inner;
            }

            public string Get(string modId, string key)
            {
                if (_active)
                {
                    return _inner?.Get(modId, key) ?? "";
                }

                (string ModId, string Key) lookup = (modId ?? "", key ?? "");
                if (_values.TryGetValue(lookup, out string value))
                {
                    return value ?? "";
                }

                if (_clearedMods.Contains(lookup.ModId))
                {
                    return "";
                }

                return _inner?.Get(modId, key) ?? "";
            }

            public void Set(string modId, string key, string value)
            {
                if (_active)
                {
                    _inner?.Set(modId, key, value);
                    return;
                }

                (string ModId, string Key) lookup = (modId ?? "", key ?? "");
                _values[lookup] = value;
                _operations.Add(new StoreOperation(false, lookup.ModId, lookup.Key, value));
            }

            public void Clear(string modId)
            {
                if (_active)
                {
                    _inner?.Clear(modId);
                    return;
                }

                string normalized = modId ?? "";
                _clearedMods.Add(normalized);
                List<(string ModId, string Key)> keys = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, normalized, StringComparison.Ordinal))
                    {
                        keys.Add(key);
                    }
                }

                for (int index = 0; index < keys.Count; index++)
                {
                    _values.Remove(keys[index]);
                }

                _operations.Add(new StoreOperation(true, normalized, "", null));
            }

            public string ActivateAfterPublication()
            {
                if (_active)
                {
                    return "";
                }

                List<string> failures = new();
                if (_inner != null)
                {
                    for (int index = 0; index < _operations.Count; index++)
                    {
                        StoreOperation operation = _operations[index];
                        try
                        {
                            if (operation.Clear)
                            {
                                _inner.Clear(operation.ModId);
                            }
                            else
                            {
                                _inner.Set(operation.ModId, operation.Key, operation.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex.Message);
                        }
                    }
                }

                _operations.Clear();
                _values.Clear();
                _clearedMods.Clear();
                _active = true;
                return failures.Count == 0
                    ? ""
                    : "Published mod-data replay was degraded: "
                        + string.Join("; ", failures);
            }

            private readonly struct StoreOperation
            {
                public StoreOperation(bool clear, string modId, string key, string value)
                {
                    Clear = clear;
                    ModId = modId;
                    Key = key;
                    Value = value;
                }

                public bool Clear { get; }

                public string ModId { get; }

                public string Key { get; }

                public string Value { get; }
            }
        }

        private sealed class StagedNetworkBridge : INetworkBridge, IDisposable
        {
            private const int MaximumQueuedOperations = 256;

            private readonly INetworkBridge _inner;
            private readonly List<Action> _queued = new();
            private bool _active;
            private bool _subscribed;
            private bool _disposed;

            public StagedNetworkBridge(INetworkBridge inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public RbxNetworkTopology Topology => _inner.Topology;

            public IReadOnlyList<string> ActorIds => _inner.ActorIds;

            public event Action<RbxNetworkEventMessage> EventReceived;

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived;

            public void RegisterActor(string actorId)
            {
                QueueOrRun(() => _inner.RegisterActor(actorId));
            }

            public void UnregisterActor(string actorId)
            {
                QueueOrRun(() => _inner.UnregisterActor(actorId));
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
                QueueOrRun(() => _inner.SendEvent(message));
            }

            public void SendRequest(
                RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
                QueueOrRun(() => _inner.SendRequest(message, response));
            }

            public void PrepareActivation()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(StagedNetworkBridge));
                }

                if (_subscribed)
                {
                    return;
                }

                _inner.EventReceived += RelayEvent;
                try
                {
                    _inner.RequestReceived += RelayRequest;
                }
                catch
                {
                    _inner.EventReceived -= RelayEvent;
                    throw;
                }

                _subscribed = true;
            }

            public string ActivateAfterPublication()
            {
                if (_disposed)
                {
                    return "";
                }

                List<string> failures = new();
                _active = true;
                for (int index = 0; index < _queued.Count; index++)
                {
                    try
                    {
                        _queued[index]();
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex.Message);
                    }
                }

                _queued.Clear();
                return failures.Count == 0
                    ? ""
                    : "Published network replay was degraded: "
                        + string.Join("; ", failures);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_subscribed)
                {
                    _inner.EventReceived -= RelayEvent;
                    _inner.RequestReceived -= RelayRequest;
                }

                _queued.Clear();
                EventReceived = null;
                RequestReceived = null;
            }

            private void QueueOrRun(Action operation)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(StagedNetworkBridge));
                }

                if (_active)
                {
                    operation();
                    return;
                }

                if (_queued.Count >= MaximumQueuedOperations)
                {
                    throw new InvalidOperationException(
                        "The staged network operation limit was exceeded before world commit.");
                }

                _queued.Add(operation);
            }

            private void RelayEvent(RbxNetworkEventMessage message)
            {
                EventReceived?.Invoke(message);
            }

            private void RelayRequest(
                RbxNetworkRequestMessage message,
                RbxNetworkRequestResponder responder)
            {
                RequestReceived?.Invoke(message, responder);
            }
        }

        private sealed class ActiveLuaModSourceStore : ILuaModSourceStore
        {
            private readonly RbxWorldRuntimeSessionController _owner;

            public ActiveLuaModSourceStore(RbxWorldRuntimeSessionController owner)
            {
                _owner = owner;
            }

            private ILuaModSourceStore Inner => _owner.Current.SourceStore;

            public void Save(string id, string source, LuaModManifest manifest)
            {
                Inner.Save(id, source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                return Inner.TryLoad(id, out source, out manifest);
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return Inner.List();
            }

            public void SetActive(string id, bool active)
            {
                Inner.SetActive(id, active);
            }

            public void Delete(string id)
            {
                Inner.Delete(id);
            }
        }

        private sealed class ActiveLuaExecutor : LuaTool.ILuaExecutor, LuaTool.IMutationExecutor
        {
            private readonly RbxWorldRuntimeSessionController _owner;

            public ActiveLuaExecutor(RbxWorldRuntimeSessionController owner)
            {
                _owner = owner;
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(
                string code,
                CancellationToken cancellationToken)
            {
                return _owner.Current.Stack.ToolExecutor.ExecuteAsync(code, cancellationToken);
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(
                string code,
                ActorContext actorContext,
                MutationEnvelope mutationEnvelope,
                CancellationToken cancellationToken)
            {
                return _owner.Current.Stack.ToolExecutor.ExecuteAsync(
                    code, actorContext, mutationEnvelope, cancellationToken);
            }
        }

        private sealed class ActiveLuaModRuntime : ILuaModRuntime
        {
            private readonly RbxWorldRuntimeSessionController _owner;
            private readonly List<HandlerListener> _handlerListeners = new();
            private readonly List<SourceListener> _loadedListeners = new();
            private readonly List<SourceListener> _unloadedListeners = new();
            private readonly List<EventListener> _eventListeners = new();
            private readonly List<ReportListener> _reportListeners = new();

            public ActiveLuaModRuntime(RbxWorldRuntimeSessionController owner)
            {
                _owner = owner;
            }

            private LuaCsModRuntime Inner => _owner.Current.Stack.Runtime;

            private InstanceRegistry Registry => _owner.Current.RbxApi.Registry;

            public IReadOnlyList<LuaModInfo> ListMods(ActorContext caller)
            {
                return Inner.ListMods(caller);
            }

            public bool TryGetModSource(ActorContext caller, string id, out string source)
            {
                return Inner.TryGetModSource(caller, id, out source);
            }

            public void LoadMod(
                ActorContext caller,
                string id,
                string luaCode,
                LuaCapabilities capabilities = LuaCapabilities.All,
                bool persistToStore = true)
            {
                string modId = Normalize(id);
                AttributionSnapshot snapshot = Capture(modId);
                PrepareNewOwner(caller, modId);
                try
                {
                    Inner.LoadMod(caller, id, luaCode, capabilities, persistToStore);
                }
                catch
                {
                    Restore(modId, snapshot);
                    throw;
                }
            }

            public string GetModOwnerActorId(ActorContext caller, string id)
            {
                return Inner.GetModOwnerActorId(caller, id);
            }

            public void ReloadMod(ActorContext caller, string id, string luaCode)
            {
                string modId = Normalize(id);
                PrepareExistingOwner(caller, modId);
                Inner.ReloadMod(caller, id, luaCode);
            }

            public bool UnloadMod(ActorContext caller, string id)
            {
                return Inner.UnloadMod(caller, id);
            }

            public string ExportMod(ActorContext caller, string id)
            {
                return Inner.ExportMod(caller, id);
            }

            public bool ImportMod(
                ActorContext caller,
                string bundleJson,
                LuaCapabilities hostGrant,
                bool allowFull = false)
            {
                string modId = ReadBundleModId(bundleJson);
                AttributionSnapshot snapshot = Capture(modId);
                PrepareNewOwner(caller, modId);
                try
                {
                    bool imported = Inner.ImportMod(caller, bundleJson, hostGrant, allowFull);
                    if (!imported)
                    {
                        Restore(modId, snapshot);
                    }

                    return imported;
                }
                catch
                {
                    Restore(modId, snapshot);
                    throw;
                }
            }

            public bool ForgetMod(ActorContext caller, string id)
            {
                bool forgotten = Inner.ForgetMod(caller, id);
                if (forgotten)
                {
                    string modId = Normalize(id);
                    if (modId.Length > 0)
                    {
                        Registry.ClearActorAttribution(modId, OriginTag.FromMod(modId));
                    }
                }

                return forgotten;
            }

            public IReadOnlyList<LuaScriptRevision> ListModVersions(ActorContext caller, string id)
            {
                return Inner.ListModVersions(caller, id);
            }

            public bool TryRevertMod(
                ActorContext caller,
                string id,
                int revisionIndex,
                out string restoredSource)
            {
                string modId = Normalize(id);
                PrepareExistingOwner(caller, modId);
                return Inner.TryRevertMod(caller, id, revisionIndex, out restoredSource);
            }

            public IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(
                ActorContext caller,
                string modId = null)
            {
                return Inner.GetRecentHandlerErrors(caller, modId);
            }

            public IReadOnlyList<LuaModReport> GetRecentReports(
                ActorContext caller,
                string modId = null)
            {
                return Inner.GetRecentReports(caller, modId);
            }

            public int ClearRecentHandlerErrors(ActorContext caller, string modId = null)
            {
                return Inner.ClearRecentHandlerErrors(caller, modId);
            }

            public int ClearRecentReports(ActorContext caller, string modId = null)
            {
                return Inner.ClearRecentReports(caller, modId);
            }

            public void Tick(ActorContext caller, double deltaSeconds)
            {
                Inner.Tick(caller, deltaSeconds);
            }

            public void EmitEvent(ActorContext caller, string name, string payload = "")
            {
                Inner.EmitEvent(caller, name, payload);
            }

            public bool IsLoaded(ActorContext caller, string id)
            {
                return Inner.IsLoaded(caller, id);
            }

            public bool GetModReportLoggingEnabled(ActorContext caller, string id)
            {
                return Inner.GetModReportLoggingEnabled(caller, id);
            }

            public bool SetModReportLoggingEnabled(ActorContext caller, string id, bool enabled)
            {
                return Inner.SetModReportLoggingEnabled(caller, id, enabled);
            }

            public void AddModHandlerErroredListener(
                ActorContext caller,
                Action<string, string, int> listener)
            {
                Inner.AddModHandlerErroredListener(caller, listener);
                _handlerListeners.Add(new HandlerListener(caller, listener));
            }

            public void RemoveModHandlerErroredListener(
                ActorContext caller,
                Action<string, string, int> listener)
            {
                _owner.RemoveListenerFromActiveRuntime(runtime =>
                    runtime.RemoveModHandlerErroredListener(caller, listener));
                _handlerListeners.RemoveAll(item => item.Matches(caller, listener));
            }

            public void AddModSourceLoadedListener(
                ActorContext caller,
                Action<string, string, LuaCapabilities> listener)
            {
                Inner.AddModSourceLoadedListener(caller, listener);
                _loadedListeners.Add(new SourceListener(caller, listener));
            }

            public void RemoveModSourceLoadedListener(
                ActorContext caller,
                Action<string, string, LuaCapabilities> listener)
            {
                _owner.RemoveListenerFromActiveRuntime(runtime =>
                    runtime.RemoveModSourceLoadedListener(caller, listener));
                _loadedListeners.RemoveAll(item => item.Matches(caller, listener));
            }

            public void AddModSourceUnloadedListener(
                ActorContext caller,
                Action<string, string, LuaCapabilities> listener)
            {
                Inner.AddModSourceUnloadedListener(caller, listener);
                _unloadedListeners.Add(new SourceListener(caller, listener));
            }

            public void RemoveModSourceUnloadedListener(
                ActorContext caller,
                Action<string, string, LuaCapabilities> listener)
            {
                _owner.RemoveListenerFromActiveRuntime(runtime =>
                    runtime.RemoveModSourceUnloadedListener(caller, listener));
                _unloadedListeners.RemoveAll(item => item.Matches(caller, listener));
            }

            public void AddModEventEmittedListener(
                ActorContext caller,
                Action<string, string, string> listener)
            {
                Inner.AddModEventEmittedListener(caller, listener);
                _eventListeners.Add(new EventListener(caller, listener));
            }

            public void RemoveModEventEmittedListener(
                ActorContext caller,
                Action<string, string, string> listener)
            {
                _owner.RemoveListenerFromActiveRuntime(runtime =>
                    runtime.RemoveModEventEmittedListener(caller, listener));
                _eventListeners.RemoveAll(item => item.Matches(caller, listener));
            }

            public void AddModReportEmittedListener(
                ActorContext caller,
                Action<string, string> listener)
            {
                Inner.AddModReportEmittedListener(caller, listener);
                _reportListeners.Add(new ReportListener(caller, listener));
            }

            public void RemoveModReportEmittedListener(
                ActorContext caller,
                Action<string, string> listener)
            {
                _owner.RemoveListenerFromActiveRuntime(runtime =>
                    runtime.RemoveModReportEmittedListener(caller, listener));
                _reportListeners.RemoveAll(item => item.Matches(caller, listener));
            }

            public void OnSessionChanging(LuaCsModRuntime previous, LuaCsModRuntime next)
            {
                for (int index = 0; index < _handlerListeners.Count; index++)
                {
                    HandlerListener item = _handlerListeners[index];
                    previous.RemoveModHandlerErroredListener(item.Caller, item.Listener);
                    next.AddModHandlerErroredListener(item.Caller, item.Listener);
                }

                MigrateSourceListeners(previous, next, _loadedListeners, true);
                MigrateSourceListeners(previous, next, _unloadedListeners, false);
                for (int index = 0; index < _eventListeners.Count; index++)
                {
                    EventListener item = _eventListeners[index];
                    previous.RemoveModEventEmittedListener(item.Caller, item.Listener);
                    next.AddModEventEmittedListener(item.Caller, item.Listener);
                }

                for (int index = 0; index < _reportListeners.Count; index++)
                {
                    ReportListener item = _reportListeners[index];
                    previous.RemoveModReportEmittedListener(item.Caller, item.Listener);
                    next.AddModReportEmittedListener(item.Caller, item.Listener);
                }
            }

            private static void MigrateSourceListeners(
                LuaCsModRuntime previous,
                LuaCsModRuntime next,
                List<SourceListener> listeners,
                bool loaded)
            {
                for (int index = 0; index < listeners.Count; index++)
                {
                    SourceListener item = listeners[index];
                    if (loaded)
                    {
                        previous.RemoveModSourceLoadedListener(item.Caller, item.Listener);
                        next.AddModSourceLoadedListener(item.Caller, item.Listener);
                    }
                    else
                    {
                        previous.RemoveModSourceUnloadedListener(item.Caller, item.Listener);
                        next.AddModSourceUnloadedListener(item.Caller, item.Listener);
                    }
                }
            }

            private void PrepareNewOwner(ActorContext caller, string modId)
            {
                DemandTrusted(caller);
                if (modId.Length == 0)
                {
                    return;
                }

                string originTag = OriginTag.FromMod(modId);
                if (caller.Grants.IsUnrestricted)
                {
                    Registry.ClearActorAttribution(modId, originTag);
                    return;
                }

                Registry.BindActorAttribution(modId, originTag, caller.ActorId);
            }

            private void PrepareExistingOwner(ActorContext caller, string modId)
            {
                DemandTrusted(caller);
                if (modId.Length == 0)
                {
                    return;
                }

                string ownerActorId = Inner.GetModOwnerActorId(caller, modId)?.Trim() ?? "";
                string originTag = OriginTag.FromMod(modId);
                if (ownerActorId.Length == 0
                    || (caller.Grants.IsUnrestricted
                        && string.Equals(ownerActorId, caller.ActorId, StringComparison.Ordinal)))
                {
                    Registry.ClearActorAttribution(modId, originTag);
                    return;
                }

                Registry.BindActorAttribution(modId, originTag, ownerActorId);
            }

            private AttributionSnapshot Capture(string modId)
            {
                if (modId.Length == 0)
                {
                    return new AttributionSnapshot(false, null);
                }

                bool found = Registry.TryGetActorAttribution(
                    modId, OriginTag.FromMod(modId), out string actorId);
                return new AttributionSnapshot(found, actorId);
            }

            private void Restore(string modId, AttributionSnapshot snapshot)
            {
                if (modId.Length == 0)
                {
                    return;
                }

                string originTag = OriginTag.FromMod(modId);
                if (snapshot.Found)
                {
                    Registry.BindActorAttribution(modId, originTag, snapshot.ActorId);
                    return;
                }

                Registry.ClearActorAttribution(modId, originTag);
            }

            private static string Normalize(string value)
            {
                return value?.Trim() ?? "";
            }

            private static string ReadBundleModId(string bundleJson)
            {
                try
                {
                    AttributionBundle bundle = JsonConvert.DeserializeObject<AttributionBundle>(bundleJson);
                    return bundle?.Manifest?.Id?.Trim() ?? "";
                }
                catch (JsonException)
                {
                    return "";
                }
            }

            private readonly struct AttributionSnapshot
            {
                public AttributionSnapshot(bool found, string actorId)
                {
                    Found = found;
                    ActorId = actorId;
                }

                public bool Found { get; }

                public string ActorId { get; }
            }

            private sealed class AttributionBundle
            {
                public LuaModManifest Manifest = new();
            }

            private readonly struct HandlerListener
            {
                public HandlerListener(ActorContext caller, Action<string, string, int> listener)
                {
                    Caller = caller;
                    Listener = listener;
                }

                public ActorContext Caller { get; }

                public Action<string, string, int> Listener { get; }

                public bool Matches(ActorContext caller, Action<string, string, int> listener)
                {
                    return Caller.Equals(caller) && Listener == listener;
                }
            }

            private readonly struct SourceListener
            {
                public SourceListener(
                    ActorContext caller,
                    Action<string, string, LuaCapabilities> listener)
                {
                    Caller = caller;
                    Listener = listener;
                }

                public ActorContext Caller { get; }

                public Action<string, string, LuaCapabilities> Listener { get; }

                public bool Matches(
                    ActorContext caller,
                    Action<string, string, LuaCapabilities> listener)
                {
                    return Caller.Equals(caller) && Listener == listener;
                }
            }

            private readonly struct EventListener
            {
                public EventListener(ActorContext caller, Action<string, string, string> listener)
                {
                    Caller = caller;
                    Listener = listener;
                }

                public ActorContext Caller { get; }

                public Action<string, string, string> Listener { get; }

                public bool Matches(ActorContext caller, Action<string, string, string> listener)
                {
                    return Caller.Equals(caller) && Listener == listener;
                }
            }

            private readonly struct ReportListener
            {
                public ReportListener(ActorContext caller, Action<string, string> listener)
                {
                    Caller = caller;
                    Listener = listener;
                }

                public ActorContext Caller { get; }

                public Action<string, string> Listener { get; }

                public bool Matches(ActorContext caller, Action<string, string> listener)
                {
                    return Caller.Equals(caller) && Listener == listener;
                }
            }
        }

        private static LuaModManifest CloneManifest(LuaModManifest source)
        {
            return JsonConvert.DeserializeObject<LuaModManifest>(
                JsonConvert.SerializeObject(source))
                ?? throw new InvalidOperationException("The mod manifest could not be cloned.");
        }
    }
}
