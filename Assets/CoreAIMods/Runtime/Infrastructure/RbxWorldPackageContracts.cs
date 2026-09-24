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

    /// <summary>Diagnostic emitted when capture adjusts the snapshot instead of failing (the live world is untouched).</summary>
    public sealed class RbxWorldPackageDiagnostic
    {
        public RbxWorldPackageDiagnostic(ulong modelId, ulong droppedPrimaryPartId, string reason)
            : this(modelId, droppedPrimaryPartId, reason, null)
        {
        }

        public RbxWorldPackageDiagnostic(ulong modelId, ulong droppedPrimaryPartId, string reason,
            string member)
        {
            ModelId = modelId;
            DroppedPrimaryPartId = droppedPrimaryPartId;
            Reason = reason ?? "";
            Member = member;
        }

        /// <summary>The Model of a dropped PrimaryPart, or the instance whose member was adjusted.</summary>
        public ulong ModelId { get; }

        public ulong DroppedPrimaryPartId { get; }

        public string Reason { get; }

        /// <summary>Member or attribute name the snapshot adjusted; null for a dropped PrimaryPart.</summary>
        public string Member { get; }
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
            : this(capturedAtUtc, settings, tree, parts, cameraCFrame, mods, null)
        {
        }

        public RbxWorldPackagePayload(
            DateTime capturedAtUtc,
            RbxWorldSettings settings,
            InstanceTreeSnapshot tree,
            IReadOnlyDictionary<InstanceId, PartProperties> parts,
            RbxCFrame? cameraCFrame,
            IReadOnlyList<RbxWorldModSource> mods,
            IReadOnlyList<RbxWorldPackageDiagnostic> diagnostics)
        {
            CapturedAtUtc = capturedAtUtc;
            Settings = settings;
            Tree = tree;
            Parts = parts;
            CameraCFrame = cameraCFrame;
            Mods = mods;
            Diagnostics = diagnostics ?? Array.Empty<RbxWorldPackageDiagnostic>();
        }

        public DateTime CapturedAtUtc { get; }

        public RbxWorldSettings Settings { get; }

        public InstanceTreeSnapshot Tree { get; }

        public IReadOnlyDictionary<InstanceId, PartProperties> Parts { get; }

        public RbxCFrame? CameraCFrame { get; }

        public IReadOnlyList<RbxWorldModSource> Mods { get; }

        public IReadOnlyList<RbxWorldPackageDiagnostic> Diagnostics { get; }
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

        /// <summary>
        /// Durable actor id whose single server-generated mutation applies the restored tree. Null or
        /// blank uses the composition's local host id (<see cref="LocalActorIdentityProvider.DefaultActorId"/>).
        /// </summary>
        public string HostActorId { get; set; }
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
    public class RbxWorldPackageException : Exception
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

    /// <summary>
    /// A world-load request the live session refused under its own rules before anything was queued.
    /// <see cref="Status"/> is the machine-readable reason an AI tool reports.
    /// </summary>
    /// <remarks>
    /// WHY it derives from <see cref="RbxWorldPackageException"/>: every caller that already turns a
    /// package it cannot load into an ordinary result keeps doing so for a session-rule refusal,
    /// instead of letting a new exception type cross a tool invocation boundary.
    /// </remarks>
    public sealed class RbxWorldLoadRefusedException : RbxWorldPackageException
    {
        /// <summary>The package is valid but cannot open in this session (for example a legacy ACL downgrade).</summary>
        public const string IncompatiblePackageStatus = "invalid_package";

        /// <summary>The live world has network sessions that cannot be handed to another world yet.</summary>
        public const string NetworkSessionsActiveStatus = "network_sessions_active";

        public RbxWorldLoadRefusedException(string status, string message)
            : base(message)
        {
            Status = status ?? "";
        }

        public string Status { get; }
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

        /// <summary>The port backing the currently published world; null where the host has no
        /// physics backend (e.g. headless). Reflects the post-publish port once a commit
        /// finishes, so a caller reading it after <see cref="IRbxWorldSessionCandidate.Commit"/>
        /// observes the port the newly published world actually owns.</summary>
        IRbxPhysicsPort PhysicsPort { get; }

        IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload);
    }

    /// <summary>Outcome returned by a confirmed production world replacement.</summary>
    public sealed class RbxWorldLoadResult
    {
        internal RbxWorldLoadResult(bool success, string error, int activeModsStarted)
            : this(success, error, activeModsStarted, false, "")
        {
        }

        internal RbxWorldLoadResult(
            bool success,
            string error,
            int activeModsStarted,
            bool startupSelectionPersisted,
            string startupSelectionError)
        {
            Success = success;
            Error = error ?? "";
            ActiveModsStarted = activeModsStarted;
            StartupSelectionPersisted = startupSelectionPersisted;
            StartupSelectionError = startupSelectionError ?? "";
        }

        public bool Success { get; }

        public string Error { get; }

        public int ActiveModsStarted { get; }

        /// <summary>
        /// True only when this player-confirmed load was also durably recorded as the world that opens
        /// on the next start. A successful load with false stays live; the next start opens the
        /// previous startup selection instead.
        /// </summary>
        public bool StartupSelectionPersisted { get; }

        /// <summary>Why a successful confirmed load was not recorded for the next start; empty otherwise.</summary>
        public string StartupSelectionError { get; }
    }

    /// <summary>What the boot-time restore of the durable startup selection did.</summary>
    public enum RbxWorldStartupRestoreOutcome
    {
        /// <summary>Nothing is selected, or the player chose the default world; the default world stays.</summary>
        NotSelected,

        /// <summary>The selected package is now the live world.</summary>
        Restored,

        /// <summary>A package is selected but could not be restored; the default world stays live.</summary>
        FellBack
    }

    /// <summary>Result of <see cref="IRbxWorldStartupSelection.RestoreStartupSelectionAsync"/>.</summary>
    public sealed class RbxWorldStartupRestoreResult
    {
        internal RbxWorldStartupRestoreResult(
            RbxWorldStartupRestoreOutcome outcome,
            int sequence,
            string worldId,
            int activeModsStarted,
            string error)
        {
            Outcome = outcome;
            Sequence = sequence;
            WorldId = worldId ?? "";
            ActiveModsStarted = activeModsStarted;
            Error = error ?? "";
        }

        public RbxWorldStartupRestoreOutcome Outcome { get; }

        /// <summary>Sequence of the startup entry that was read; 0 when none exists.</summary>
        public int Sequence { get; }

        public string WorldId { get; }

        public int ActiveModsStarted { get; }

        /// <summary>Why a selected package was not restored; empty otherwise.</summary>
        public string Error { get; }
    }

    /// <summary>
    /// Trusted host surface for the world that opens on the next start. Deliberately separate from
    /// <see cref="IRbxWorldRuntimeService"/>: no AI tool is given it, so only the player (through the
    /// Hub) and the startup composition reach the durable selection.
    /// </summary>
    public interface IRbxWorldStartupSelection
    {
        /// <summary>
        /// Makes the durable startup selection the live world, or keeps the default world. Never throws;
        /// every failure is reported as <see cref="RbxWorldStartupRestoreOutcome.FellBack"/>.
        /// </summary>
        UniTask<RbxWorldStartupRestoreResult> RestoreStartupSelectionAsync(
            CancellationToken cancellationToken = default);

        /// <summary>Records that the next start opens the default world; the live world is unchanged.</summary>
        UniTask<RbxWorldPackageWriteResult> ClearStartupSelectionAsync(
            CancellationToken cancellationToken = default);

        /// <summary>Reads the current selection's kind and metadata without decoding its package.</summary>
        UniTask<RbxWorldStartupSelection> ReadStartupSelectionAsync(
            CancellationToken cancellationToken = default);
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

    /// <summary>Metadata for one autosave file without exposing its bytes.</summary>
    public sealed class RbxAutoSaveInfo
    {
        public RbxAutoSaveInfo(string fileName, string trigger, DateTime timestampUtc, long sizeBytes)
        {
            FileName = fileName ?? "";
            Trigger = trigger ?? "";
            TimestampUtc = timestampUtc;
            SizeBytes = sizeBytes;
        }

        public string FileName { get; }

        public string Trigger { get; }

        public DateTime TimestampUtc { get; }

        public long SizeBytes { get; }
    }

    /// <summary>Production save/load seam shared by AI requests and confirmed host actions.</summary>
    public interface IRbxWorldRuntimeService
    {
        event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested;

        RbxWorldPackagePayload CaptureCurrent();

        IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads();

        IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves();

        UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default);

        UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(
            ActorContext caller,
            string autoFileName,
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
            // WHY first: the store validates the slot too, but by throwing - from inside the tool body,
            // where the policy must treat the call as possibly executed. Refused here it is an ordinary
            // result the model can correct and retry.
            if (!RbxWorldPackageNames.TryValidateManualSlot(slot, "manual slot", out _, out string slotError))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    path = "",
                    error = RbxWorldPackageNames.DescribeInvalidArgument("slot", slotError)
                });
            }

            ActorContext actor = _identityProvider.GetActorContext(_roleId);
            RbxWorldPackageWriteResult result;
            // WHY: the store turns every write failure into a result itself, so what still throws here is
            // the capture of the live world (or a session that is gone), raised before any byte is
            // written. Thrown, it crossed the tool invocation boundary and was traced as possibly
            // executed; as a result the model can correct the world and retry. Cancellation propagates.
            try
            {
                result = await _service.SaveManualAsync(
                    actor,
                    slot,
                    cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    status = RbxWorldPackageNames.CaptureFailedStatus,
                    path = "",
                    error = "The live world could not be captured into slot '" + slot + "': " + ex.Message
                            + " Nothing was written. The tool was NOT executed. Fix the world and retry."
                });
            }

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
            // WHY first: see SaveWorldLlmTool - a slot the store would throw on is refused as a result.
            if (!RbxWorldPackageNames.TryValidateManualSlot(slot, "manual slot", out _, out string slotError))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    status = RbxWorldPackageNames.InvalidArgumentStatus,
                    player_confirmation_required = false,
                    request_id = "",
                    slot = slot ?? "",
                    world_id = "",
                    error = RbxWorldPackageNames.DescribeInvalidArgument("slot", slotError)
                });
            }

            ActorContext actor = _identityProvider.GetActorContext(_roleId);
            RbxWorldLoadRequest request;
            // WHY: only failures raised while the slot is read or checked against the live session are
            // converted. They happen before any request is queued, so "not executed" is true, and a
            // missing, corrupt, oversized or unreadable slot is an outcome the model can correct. Thrown,
            // it crossed the tool invocation boundary and was traced as possibly executed. Cancellation
            // propagates.
            try
            {
                request = await _service.RequestManualLoadAsync(
                    actor,
                    slot,
                    cancellationToken);
            }
            catch (RbxWorldLoadRefusedException ex)
            {
                return RefuseUnloadable(
                    slot,
                    ex.Status,
                    "Manual slot '" + slot + "' cannot be loaded into this session: " + ex.Message
                    + " The tool was NOT executed.");
            }
            catch (System.IO.FileNotFoundException)
            {
                return RefuseUnloadable(slot, RbxWorldPackageNames.NotFoundStatus, DescribeNotFound(slot));
            }
            catch (System.IO.DirectoryNotFoundException)
            {
                return RefuseUnloadable(slot, RbxWorldPackageNames.NotFoundStatus, DescribeNotFound(slot));
            }
            catch (RbxWorldPackageException ex)
            {
                return RefuseUnloadable(
                    slot,
                    RbxWorldPackageNames.InvalidPackageStatus,
                    "Manual slot '" + slot + "' is not a loadable world package: " + ex.Message
                    + " The tool was NOT executed. Pick another slot.");
            }
            catch (System.IO.IOException ex)
            {
                return RefuseUnloadable(slot, RbxWorldPackageNames.ReadFailedStatus, DescribeReadFailure(slot, ex));
            }
            catch (UnauthorizedAccessException ex)
            {
                return RefuseUnloadable(slot, RbxWorldPackageNames.ReadFailedStatus, DescribeReadFailure(slot, ex));
            }

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

        private static string DescribeNotFound(string slot)
        {
            return "Manual slot '" + slot + "' was not found. The tool was NOT executed."
                   + " Save it with save_world first, or retry with an existing 'slot'.";
        }

        private static string DescribeReadFailure(string slot, Exception exception)
        {
            return "Manual slot '" + slot + "' could not be read: " + exception.Message
                   + " The tool was NOT executed. Retry, or pick another slot.";
        }

        private static string RefuseUnloadable(string slot, string status, string error)
        {
            return JsonConvert.SerializeObject(new
            {
                success = false,
                status,
                player_confirmation_required = false,
                request_id = "",
                slot = slot ?? "",
                world_id = "",
                error
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

        public IRbxPhysicsPort PhysicsPort => _host.PhysicsPort;

        public IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            Camera sceneCamera = _host.SceneCamera;
            GameObject stagingRoot = new("CoreAI_RbxWorld_Staging");
            stagingRoot.transform.SetParent(_host.transform, false);
            stagingRoot.SetActive(false);
            InstanceGameObjectBinder stagedBinder = new(stagingRoot.transform);
            // WHY: capture always reads a camera pose (the Lua surface falls back to an in-memory
            // rig), so a camera-less scene must stage the same in-memory rig instead of refusing
            // the package it produced itself; publication skips the live camera when none exists.
            PublishableCameraRig stagedCamera = new(
                _host.CameraRig?.GetCFrame() ?? RbxCFrame.Identity);
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
        private IPartPropertySink _partSink;
        private IRbxCameraRig _cameraRig;
        private RbxWorldSettings _settings;

        public HeadlessRbxWorldSessionHost(
            InstanceRegistry registry,
            RbxDataModel game,
            RbxWorldSettings settings = null,
            IPartPropertySink partSink = null,
            IRbxCameraRig cameraRig = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _partSink = partSink;
            _cameraRig = cameraRig;
            _settings = CloneSettings(settings ?? new RbxWorldSettings
            {
                WorldId = registry.WorldId
            });
        }

        public InstanceRegistry Registry => _registry;

        public RbxDataModel Game => _game;

        /// <summary>Sink the published session reads Part state through; null until one is supplied or published.</summary>
        public IPartPropertySink PartSink => _partSink;

        /// <summary>Rig the published session reads camera state through; null until one is supplied or published.</summary>
        public IRbxCameraRig CameraRig => _cameraRig;

        public IInputSource InputSource => null;

        public IClickPickSource PickSource => null;

        public RbxWorldSettings Settings => CloneSettings(_settings);

        // WHY null: this host has no engine physics backend to bind, unlike the scene adapter over
        // RbxWorldHost. A staged/published raycast here answers through NullRbxPhysicsPort the same
        // way it always has; there is no port reference for a caller to re-attach to.
        public IRbxPhysicsPort PhysicsPort => null;

        public IRbxWorldSessionCandidate Stage(RbxWorldPackagePayload payload)
        {
            // WHY: the Lua surface reads Part and camera state through the sink and rig handed to
            // its bindings, so the candidate must expose the exact objects restore populated; a
            // fresh in-memory rig keeps packaged camera state loadable without an engine.
            InMemoryCameraRig stagedCamera = new();
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                payload,
                new RbxWorldPackageRestoreOptions
                {
                    CameraRig = stagedCamera
                });
            return new Candidate(this, restored, stagedCamera, CloneSettings(payload.Settings));
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
                IRbxCameraRig cameraRig,
                RbxWorldSettings settings)
            {
                _owner = owner;
                Registry = restored.Registry;
                Game = restored.Game;
                PartSink = restored.PartSink;
                CameraRig = cameraRig;
                Settings = CloneSettings(settings);
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
                _owner._registry = Registry;
                _owner._game = Game;
                _owner._partSink = PartSink;
                _owner._cameraRig = CameraRig;
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
    /// A player-confirmed load is also recorded as the world that opens on the next start when the
    /// package store keeps a startup selection (<see cref="IRbxWorldStartupStore"/>).
    /// </summary>
    public sealed class RbxWorldRuntimeSessionController
        : IRbxWorldRuntimeService, IRbxWorldStartupSelection, IDisposable
    {
        private const int MaximumPendingLoadRequests = 8;
        private static readonly TimeSpan DefaultPendingLoadTimeToLive = TimeSpan.FromMinutes(2d);
        private const string PreLoadAutosaveTrigger = "load_world-pre";
        private const string ManualSourceKind = "manual";
        private const string AutosaveSourceKind = "autosave";

        private readonly object _gate = new();
        private readonly IRbxWorldSessionHost _host;
        private readonly IRbxWorldPackageStore _packageStore;
        private readonly IRbxWorldStartupStore _startupStore;
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
        private readonly int? _worldAclFloor;
        private readonly Dictionary<string, PendingLoad> _pendingLoads =
            new(StringComparer.Ordinal);
        private Session _current;
        private bool _disposed;
        private bool _loadInProgress;
        private Func<DateTime> _utcNow = () => DateTime.UtcNow;
        private TimeSpan _pendingLoadTimeToLive = DefaultPendingLoadTimeToLive;
        private string _lastAdvanceFailure;
        private string _lastTickFailure;

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
            _startupStore = packageStore as IRbxWorldStartupStore;
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
            // WHY the composed ACL version is a floor: a package whose world.json omits the optional
            // world_acl_version would otherwise restore as a legacy world, silently switch off every
            // per-actor check, and persist that downgrade in every later save.
            _worldAclFloor = _current.RbxApi.Registry.WorldAclVersion;
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

        public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
        {
            DemandActive();
            return _packageStore.ListAutoSaves();
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

        /// <summary>
        /// Reads a manual slot and queues a one-use player confirmation for it. A slot this session
        /// would refuse to load (live network sessions, a legacy ACL downgrade) throws
        /// <see cref="RbxWorldLoadRefusedException"/> instead, so the player is never asked to confirm
        /// a package that cannot open.
        /// </summary>
        public async UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default)
        {
            DemandTrusted(caller);
            DemandNoLiveNetworkSessions();
            RbxWorldPackagePayload payload = await _packageStore.LoadManualAsync(
                slot, cancellationToken);
            DemandAclCompatible(payload);
            return QueuePendingLoad(caller, slot, ManualSourceKind, payload);
        }

        /// <summary>
        /// Reads an autosave and queues a one-use player confirmation for it, refusing exactly like
        /// <see cref="RequestManualLoadAsync"/>.
        /// </summary>
        public async UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(
            ActorContext caller,
            string autoFileName,
            CancellationToken cancellationToken = default)
        {
            DemandTrusted(caller);
            DemandNoLiveNetworkSessions();
            RbxWorldPackagePayload payload = await _packageStore.LoadAutoAsync(
                autoFileName, cancellationToken);
            DemandAclCompatible(payload);
            return QueuePendingLoad(caller, autoFileName, AutosaveSourceKind, payload);
        }

        private RbxWorldLoadRequest QueuePendingLoad(
            ActorContext caller,
            string name,
            string sourceKind,
            RbxWorldPackagePayload payload)
        {
            string requestId = Guid.NewGuid().ToString("N");
            RbxPendingWorldLoadRequest publicRequest;
            lock (_gate)
            {
                DemandActiveLocked();
                DateTime requestedAtUtc = _utcNow();
                RemoveExpiredPendingLoadsLocked(requestedAtUtc);
                RemovePendingSlotLocked(name);
                if (_pendingLoads.Count >= MaximumPendingLoadRequests)
                {
                    RemoveOldestPendingLoadLocked();
                }

                PendingLoad pending = new(
                    requestId,
                    caller.ActorId,
                    name,
                    sourceKind,
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

            return await LoadConfirmedCoreAsync(pending.Payload, true, pending, cancellationToken);
        }

        /// <summary>
        /// Trusted host load of an already confirmed payload. It never changes the world that opens on
        /// the next start; a host that wants that records it through its own startup store.
        /// </summary>
        public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            return LoadConfirmedCoreAsync(payload, true, null, cancellationToken);
        }

        public async UniTask<RbxWorldStartupRestoreResult> RestoreStartupSelectionAsync(
            CancellationToken cancellationToken = default)
        {
            if (_startupStore == null)
            {
                return new RbxWorldStartupRestoreResult(
                    RbxWorldStartupRestoreOutcome.NotSelected, 0, "", 0, "");
            }

            RbxWorldStartupSelection selection;
            try
            {
                DemandActive();
                selection = await _startupStore.ReadStartupAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return FallBackAtStartup(0, "", "the startup selection could not be read: " + ex.Message);
            }

            if (selection == null
                || selection.Kind == RbxWorldStartupSelectionKind.None
                || selection.Kind == RbxWorldStartupSelectionKind.Default)
            {
                return new RbxWorldStartupRestoreResult(
                    RbxWorldStartupRestoreOutcome.NotSelected, selection?.Sequence ?? 0, "", 0, "");
            }

            if (selection.Kind != RbxWorldStartupSelectionKind.Package || selection.Payload == null)
            {
                return FallBackAtStartup(
                    selection.Sequence,
                    selection.WorldId,
                    selection.Error.Length > 0 ? selection.Error : "the selected entry holds no package");
            }

            RbxWorldLoadResult loaded;
            try
            {
                // WHY no pre-load safety autosave: the outgoing world at boot is the reproducible
                // default world, and one autosave per boot would evict the real backups from the
                // bounded ring within a handful of restarts.
                loaded = await LoadConfirmedCoreAsync(selection.Payload, false, null, cancellationToken);
            }
            catch (Exception ex)
            {
                return FallBackAtStartup(selection.Sequence, selection.WorldId, ex.Message);
            }

            if (loaded == null || !loaded.Success)
            {
                return FallBackAtStartup(
                    selection.Sequence,
                    selection.WorldId,
                    loaded?.Error ?? "the staged restore returned no result");
            }

            return new RbxWorldStartupRestoreResult(
                RbxWorldStartupRestoreOutcome.Restored,
                selection.Sequence,
                selection.WorldId,
                loaded.ActiveModsStarted,
                "");
        }

        public async UniTask<RbxWorldPackageWriteResult> ClearStartupSelectionAsync(
            CancellationToken cancellationToken = default)
        {
            DemandActive();
            if (_startupStore == null)
            {
                return new RbxWorldPackageWriteResult(
                    false,
                    "",
                    "The configured world package store keeps no startup selection.");
            }

            return await _startupStore.ClearStartupAsync(cancellationToken);
        }

        public async UniTask<RbxWorldStartupSelection> ReadStartupSelectionAsync(
            CancellationToken cancellationToken = default)
        {
            DemandActive();
            if (_startupStore == null)
            {
                return RbxWorldStartupSelection.NoneSelected;
            }

            return await _startupStore.ReadStartupInfoAsync(cancellationToken);
        }

        /// <param name="writeSafetyAutosave">False only for the boot-time startup restore.</param>
        /// <param name="startupSource">
        /// The consumed player-confirmed request; non-null records the published payload as the
        /// world that opens on the next start.
        /// </param>
        private async UniTask<RbxWorldLoadResult> LoadConfirmedCoreAsync(
            RbxWorldPackagePayload payload,
            bool writeSafetyAutosave,
            PendingLoad startupSource,
            CancellationToken cancellationToken)
        {
            DemandActive();
            if (payload == null)
            {
                return new RbxWorldLoadResult(false, "World package payload is required.", 0);
            }

            string networkRefusal = DescribeLiveNetworkSessions();
            if (networkRefusal.Length > 0)
            {
                return new RbxWorldLoadResult(false, networkRefusal, 0);
            }

            string aclRefusal = DescribeAclDowngrade(payload);
            if (aclRefusal.Length > 0)
            {
                return new RbxWorldLoadResult(false, aclRefusal, 0);
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
                if (writeSafetyAutosave)
                {
                    RbxWorldPackagePayload currentPayload;
                    try
                    {
                        currentPayload = CaptureCurrent();
                    }
                    catch (Exception ex)
                    {
                        return new RbxWorldLoadResult(
                            false,
                            "Pre-load safety autosave capture failed: " + ex.Message,
                            0);
                    }

                    RbxWorldPackageWriteResult safetyAutosave = await _packageStore.CreateAutoAsync(
                        PreLoadAutosaveTrigger,
                        currentPayload,
                        cancellationToken);
                    if (safetyAutosave == null || !safetyAutosave.Success)
                    {
                        string reason = safetyAutosave == null || string.IsNullOrWhiteSpace(safetyAutosave.Error)
                            ? "durability not confirmed"
                            : safetyAutosave.Error;
                        return new RbxWorldLoadResult(
                            false,
                            "Pre-load safety autosave '" + PreLoadAutosaveTrigger + "' failed: " + reason,
                            0);
                    }
                }

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

                    // WHY attach here, after Commit, and not at Stage time: candidate.Commit() is
                    // what runs the host's PublishReplacement, which disposes the outgoing physics
                    // port and constructs a fresh one bound to the newly published binder. Attaching
                    // the staged WorldPhysics to the port that existed at Stage time wires it to a
                    // port that Commit is about to dispose (Dispose only clears contact callbacks —
                    // it never throws, so a disposed port answers every Raycast with a miss and never
                    // fires Touched/TouchEnded, silently) and, before that dispose, keeps relaying
                    // contacts tagged with instance ids from the OUTGOING world. Reading the host's
                    // PhysicsPort only now yields the port PublishReplacement just built for the
                    // incoming world.
                    if (_host.PhysicsPort != null && stagedRbxApi.WorldPhysics != null)
                    {
                        stagedRbxApi.WorldPhysics.AttachPort(_host.PhysicsPort);
                    }

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

                if (startupSource == null)
                {
                    return new RbxWorldLoadResult(true, "", started);
                }

                // WHY still inside the load: the next confirmed load cannot publish until this one's
                // selection is recorded, so the newest startup entry always names the newest live world.
                string selectionError = await RecordStartupSelectionAsync(startupSource);
                return new RbxWorldLoadResult(
                    true,
                    "",
                    started,
                    selectionError.Length == 0,
                    selectionError);
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

        /// <summary>
        /// Advances only the currently published scheduler and runtime. A throw from either one is
        /// reported through the diagnostics sink and does not skip the other.
        /// </summary>
        public void PumpFrame(ActorContext actorContext, float deltaSeconds)
        {
            Session session = Current;
            // WHY two containments: the scheduler and the mod runtime are independent frame owners. A
            // throw from one (an unobserved host fault the scheduler rethrows after its frame, or a
            // runtime that refuses the caller) used to skip the other, so mod timers and queued events
            // stopped for as long as the unrelated fault repeated.
            try
            {
                session.RbxApi.Scheduler.Advance(deltaSeconds);
                _lastAdvanceFailure = null;
            }
            catch (Exception ex)
            {
                ReportPumpFailure(ref _lastAdvanceFailure, "scheduler Advance", ex);
            }

            try
            {
                session.Stack.Runtime.Tick(actorContext, deltaSeconds);
                _lastTickFailure = null;
            }
            catch (Exception ex)
            {
                ReportPumpFailure(ref _lastTickFailure, "mod runtime Tick", ex);
            }
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
            ReportDiagnostic(message);
        }

        private void ReportDiagnostic(string message)
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

        private void ReportPumpFailure(ref string lastFailure, string phase, Exception exception)
        {
            string message = "Frame pump: the " + phase + " failed and the rest of the frame still ran: "
                             + exception.GetType().Name + ": " + exception.Message;
            // WHY once per distinct failure: the pump runs every frame, and a fault that repeats each
            // frame would otherwise bury the log under one identical warning per frame.
            if (string.Equals(lastFailure, message, StringComparison.Ordinal))
            {
                return;
            }

            lastFailure = message;
            ReportDiagnostic(message);
        }

        /// <summary>
        /// Records the just-published confirmed payload as the world that opens on the next start.
        /// Never throws: the live world is already published, so a failure is reported and returned
        /// as the reason, never rolled back.
        /// </summary>
        private async UniTask<string> RecordStartupSelectionAsync(PendingLoad source)
        {
            string reason;
            if (_startupStore == null)
            {
                reason = "the configured world package store keeps no startup selection";
            }
            else
            {
                try
                {
                    // WHY uncancelled: the player confirmed and the world is already live; abandoning the
                    // bounded selection write half-way would only make the next start disagree with it.
                    RbxWorldPackageWriteResult written = await _startupStore.SelectStartupAsync(
                        source.Payload,
                        source.SourceKind,
                        source.Slot,
                        CancellationToken.None);
                    if (written != null && written.Success)
                    {
                        return "";
                    }

                    reason = written == null || string.IsNullOrWhiteSpace(written.Error)
                        ? "durability not confirmed"
                        : written.Error;
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                }
            }

            ReportDiagnostic(
                "The confirmed " + source.SourceKind + " '" + source.Slot + "' (world '"
                + (source.Payload.Settings?.WorldId ?? "") + "') is live, but it was not recorded as the "
                + "world that opens on the next start: " + reason
                + " The next start opens the previous startup selection.");
            return reason;
        }

        private RbxWorldStartupRestoreResult FallBackAtStartup(int sequence, string worldId, string reason)
        {
            ReportDiagnostic(
                "The startup world selection #" + sequence.ToString(CultureInfo.InvariantCulture)
                + " ('" + (worldId ?? "") + "') could not be restored, so the default world stays live: "
                + reason + " The selection was kept; choose 'Start with the default world next time' on "
                + "the Hub World Loads page to clear it.");
            return new RbxWorldStartupRestoreResult(
                RbxWorldStartupRestoreOutcome.FellBack,
                sequence,
                worldId,
                0,
                reason);
        }

        /// <summary>
        /// Why a world load must be refused while the live session has network sessions; empty when
        /// there are none.
        /// </summary>
        /// <remarks>
        /// WHY registered bridge actors are the signal: a transport that admits a connection registers
        /// its actor on the bridge before the world creates the Player, and the world unregisters it
        /// when the connection is torn down, so a live remote session is always listed. The check is
        /// conservative: it also counts an actor registered for a non-host mod context whose socket
        /// is gone. The loopback (Solo) bridge is exempt because it has no connection to hand over.
        /// </remarks>
        private string DescribeLiveNetworkSessions()
        {
            RbxNetworkTopology topology;
            int actorCount;
            try
            {
                topology = _networkBridge.Topology;
                actorCount = _networkBridge.ActorIds?.Count ?? 0;
            }
            catch (Exception ex)
            {
                return "The network bridge could not report its sessions (" + ex.Message + "), so the "
                       + "world load was refused and the live world was not changed. Live network "
                       + "sessions cannot be handed to a new world until MVP11 session handoff exists; "
                       + "disconnect clients or stop the server first.";
            }

            if (topology == RbxNetworkTopology.Solo || actorCount == 0)
            {
                return "";
            }

            return "The live world has " + actorCount.ToString(CultureInfo.InvariantCulture)
                   + " connected network actor(s) on its " + topology + " bridge. Live network sessions "
                   + "cannot be handed to a new world until MVP11 session handoff exists, so the world "
                   + "load was refused and the live world was not changed; disconnect clients or stop "
                   + "the server first.";
        }

        private string DescribeAclDowngrade(RbxWorldPackagePayload payload)
        {
            if (!_worldAclFloor.HasValue || payload?.Tree == null || payload.Tree.WorldAclVersion.HasValue)
            {
                return "";
            }

            return "World package has no world ACL version (legacy compatibility mode), but this "
                   + "session was composed with world ACL version "
                   + _worldAclFloor.Value.ToString(CultureInfo.InvariantCulture)
                   + "; loading it would switch off per-actor access control. The live world was "
                   + "not changed; compose the session with worldAclVersion: null to open a "
                   + "legacy world.";
        }

        private void DemandNoLiveNetworkSessions()
        {
            string refusal = DescribeLiveNetworkSessions();
            if (refusal.Length > 0)
            {
                throw new RbxWorldLoadRefusedException(
                    RbxWorldLoadRefusedException.NetworkSessionsActiveStatus,
                    refusal);
            }
        }

        private void DemandAclCompatible(RbxWorldPackagePayload payload)
        {
            string refusal = DescribeAclDowngrade(payload);
            if (refusal.Length > 0)
            {
                throw new RbxWorldLoadRefusedException(
                    RbxWorldLoadRefusedException.IncompatiblePackageStatus,
                    refusal);
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
                string sourceKind,
                RbxWorldPackagePayload payload,
                DateTime requestedAtUtc,
                DateTime expiresAtUtc)
            {
                RequestId = requestId ?? "";
                ActorId = actorId;
                Slot = slot?.Trim() ?? "";
                SourceKind = sourceKind ?? "";
                Payload = payload;
                RequestedAtUtc = requestedAtUtc;
                ExpiresAtUtc = expiresAtUtc;
            }

            public string RequestId { get; }

            public string ActorId { get; }

            public string Slot { get; }

            /// <summary><c>manual</c> or <c>autosave</c>; kept for startup provenance and diagnostics.</summary>
            public string SourceKind { get; }

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

            /// <summary>Delegated: the payload ceiling belongs to the real transport underneath.</summary>
            public int MaxPayloadBytes => _inner.MaxPayloadBytes;

            /// <summary>Delegated: only the real transport can measure the server clock.</summary>
            public double ServerClockOffsetSeconds => _inner.ServerClockOffsetSeconds;

            /// <summary>
            /// Delegated with the offset: without it the staged world read every transport as already
            /// synchronized, and a client's first clock anchor looked like time running backwards.
            /// </summary>
            public bool IsServerClockSynchronized => _inner.IsServerClockSynchronized;

            /// <inheritdoc />
            /// <remarks>
            /// Forwarded rather than queued: a peer that dropped during staging has already gone, and
            /// replaying that after the swap would tear down a session that the new world never had.
            /// </remarks>
            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add => _inner.PeerDisconnected += value;
                remove => _inner.PeerDisconnected -= value;
            }

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

            /// <inheritdoc />
            /// <remarks>
            /// Queued like <see cref="UnregisterActor"/>, never forwarded ahead of it: a kick from
            /// a world that is not live yet must not close a connection the live world still
            /// serves, and after the swap it must land after the registrations queued before it.
            /// </remarks>
            public void DisconnectActor(string actorId)
            {
                QueueOrRun(() => _inner.DisconnectActor(actorId));
            }

            /// <inheritdoc />
            /// <remarks>
            /// Queued with its text exactly like <see cref="DisconnectActor(string)"/>. WHY
            /// implemented here and not left to the interface default: the default drops the text,
            /// and a world loaded from a package keeps this bridge for its whole life, so every kick
            /// in it would show the client the transport's default notice instead of the script's
            /// message.
            /// </remarks>
            public void DisconnectActor(string actorId, string message)
            {
                QueueOrRun(() => _inner.DisconnectActor(actorId, message));
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

            public Task<LuaTool.LuaResult> ExecuteAsync(
                string code,
                ActorContext actorContext,
                CancellationToken cancellationToken)
            {
                return _owner.Current.Stack.ToolExecutor.ExecuteAsync(
                    code, actorContext, cancellationToken);
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
