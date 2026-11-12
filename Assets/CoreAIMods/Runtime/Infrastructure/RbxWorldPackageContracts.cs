using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Folders;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Terrain;
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

    /// <summary>
    /// The optional blocks of a world package beside the instance tree, the Part state and the mods. A null
    /// member is an absent block, which a reader takes as the default; every block a later release adds
    /// goes here, so code that copies a payload keeps it.
    /// </summary>
    public sealed class RbxWorldPackageOptionalState
    {
        /// <summary>
        /// The package format version the payload is written as (MVP4 SC-7): 1, in which no instance carries a
        /// script binding and every active mod starts at the legacy placement, or 2, in which every script instance
        /// carries its <c>script</c> block and a source without a bound instance is detached. 1 by default.
        /// </summary>
        public int FormatVersion { get; set; } = RbxWorldPackageSerializer.LegacyScriptPlacementFormatVersion;

        /// <summary>The Workspace Terrain's cells, or null for a world without any.</summary>
        public TerrainSnapshot Terrain { get; set; }

        /// <summary>The properties of every PointLight in the tree, by instance id; null or missing ids are class defaults.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackagePointLight> PointLights { get; set; }

        /// <summary>The Lighting service's day-cycle state when the world drives its lighting; null keeps the scene's own.</summary>
        public RbxLightingDurableState? Lighting { get; set; }

        /// <summary>The StarterPlayer service's control and camera settings; null means the class defaults.</summary>
        public RbxWorldPackageStarterPlayer? StarterPlayer { get; set; }

        /// <summary>
        /// The persisted mod folders (P20): folders that exist without a mod in them, by canonical path; null or
        /// empty when there are none. A mod's own folder travels in its manifest's category.
        /// </summary>
        public IReadOnlyList<string> ModFolders { get; set; }

        /// <summary>
        /// The properties of every world-owned GUI node in the tree (ScreenGui, GuiObject and UI layout objects), by
        /// instance id; null or missing ids are class defaults.
        /// </summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageGui> Gui { get; set; }

        /// <summary>The StarterGui service's recorded switches; null means the class defaults.</summary>
        public RbxWorldPackageStarterGui? StarterGui { get; set; }

        /// <summary>The properties of every world-owned TextChatCommand, by instance id; missing ids are class defaults.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageTextChatCommand> TextChatCommands { get; set; }

        /// <summary>The properties of every world-owned SelectionBox, by instance id; missing ids are class defaults.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageSelectionBox> SelectionBoxes { get; set; }

        /// <summary>The properties of every world-owned Highlight, by instance id; missing ids are class defaults.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageHighlight> Highlights { get; set; }

        internal RbxWorldPackageOptionalState Clone()
        {
            return new RbxWorldPackageOptionalState
            {
                FormatVersion = FormatVersion,
                Terrain = Terrain,
                PointLights = PointLights,
                Lighting = Lighting,
                StarterPlayer = StarterPlayer,
                ModFolders = ModFolders,
                Gui = Gui,
                StarterGui = StarterGui,
                TextChatCommands = TextChatCommands,
                SelectionBoxes = SelectionBoxes,
                Highlights = Highlights
            };
        }
    }

    /// <summary>The durable settings of the <c>StarterPlayer</c> service (the package's <c>starter_player</c> block).</summary>
    public readonly struct RbxWorldPackageStarterPlayer : IEquatable<RbxWorldPackageStarterPlayer>
    {
        public RbxWorldPackageStarterPlayer(
            RbxDevComputerMovementMode devComputerMovementMode,
            RbxCameraMode cameraMode,
            double cameraMaxZoomDistance,
            double cameraMinZoomDistance)
        {
            DevComputerMovementMode = devComputerMovementMode;
            CameraMode = cameraMode;
            CameraMaxZoomDistance = cameraMaxZoomDistance;
            CameraMinZoomDistance = cameraMinZoomDistance;
        }

        public RbxDevComputerMovementMode DevComputerMovementMode { get; }

        public RbxCameraMode CameraMode { get; }

        /// <summary>Studs; never below <see cref="CameraMinZoomDistance"/>.</summary>
        public double CameraMaxZoomDistance { get; }

        /// <summary>Studs; within [0, <see cref="CameraMaxZoomDistance"/>].</summary>
        public double CameraMinZoomDistance { get; }

        /// <summary>The class defaults, which a package leaves out.</summary>
        public static RbxWorldPackageStarterPlayer Defaults => new(
            RbxDevComputerMovementMode.UserChoice,
            RbxCameraMode.Classic,
            RbxStarterPlayer.DefaultCameraMaxZoomDistance,
            RbxStarterPlayer.DefaultCameraMinZoomDistance);

        /// <summary>The settings of <paramref name="starterPlayer"/> as they are now.</summary>
        public static RbxWorldPackageStarterPlayer Of(RbxStarterPlayer starterPlayer)
        {
            if (starterPlayer == null)
            {
                throw new ArgumentNullException(nameof(starterPlayer));
            }

            return new RbxWorldPackageStarterPlayer(
                starterPlayer.DevComputerMovementMode,
                starterPlayer.CameraMode,
                starterPlayer.CameraMaxZoomDistance,
                starterPlayer.CameraMinZoomDistance);
        }

        /// <summary>
        /// Puts these settings on <paramref name="starterPlayer"/> through its public setters, in an order the
        /// zoom clamps cannot alter (the minimum to zero first, then the maximum, then the minimum).
        /// </summary>
        public void ApplyTo(RbxStarterPlayer starterPlayer)
        {
            if (starterPlayer == null)
            {
                throw new ArgumentNullException(nameof(starterPlayer));
            }

            starterPlayer.DevComputerMovementMode = DevComputerMovementMode;
            starterPlayer.CameraMode = CameraMode;
            starterPlayer.CameraMinZoomDistance = 0d;
            starterPlayer.CameraMaxZoomDistance = CameraMaxZoomDistance;
            starterPlayer.CameraMinZoomDistance = CameraMinZoomDistance;
        }

        public bool Equals(RbxWorldPackageStarterPlayer other)
        {
            return DevComputerMovementMode == other.DevComputerMovementMode && CameraMode == other.CameraMode
                   && CameraMaxZoomDistance.Equals(other.CameraMaxZoomDistance)
                   && CameraMinZoomDistance.Equals(other.CameraMinZoomDistance);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackageStarterPlayer other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ((int)DevComputerMovementMode * 397) ^ (int)CameraMode ^ CameraMaxZoomDistance.GetHashCode()
                   ^ (CameraMinZoomDistance.GetHashCode() * 31);
        }
    }

    /// <summary>The durable properties of one <c>PointLight</c> (the package's <c>light</c> block).</summary>
    public readonly struct RbxWorldPackagePointLight : IEquatable<RbxWorldPackagePointLight>
    {
        public RbxWorldPackagePointLight(float brightness, RbxColor3 color, bool enabled, bool shadows, float range)
        {
            Brightness = brightness;
            Color = color;
            Enabled = enabled;
            Shadows = shadows;
            Range = range;
        }

        public float Brightness { get; }

        public RbxColor3 Color { get; }

        public bool Enabled { get; }

        public bool Shadows { get; }

        /// <summary>Studs, within [0, <see cref="RbxPointLight.MaxRange"/>].</summary>
        public float Range { get; }

        /// <summary>The state of <paramref name="light"/> as it is now.</summary>
        public static RbxWorldPackagePointLight Of(RbxPointLight light)
        {
            if (light == null)
            {
                throw new ArgumentNullException(nameof(light));
            }

            return new RbxWorldPackagePointLight(light.Brightness, light.Color, light.Enabled, light.Shadows, light.Range);
        }

        /// <summary>Puts this state on <paramref name="light"/> through its public setters.</summary>
        public void ApplyTo(RbxPointLight light)
        {
            if (light == null)
            {
                throw new ArgumentNullException(nameof(light));
            }

            light.SetBrightness(Brightness);
            light.SetColor(Color);
            light.SetEnabled(Enabled);
            light.SetShadows(Shadows);
            light.SetRange(Range);
        }

        public bool Equals(RbxWorldPackagePointLight other)
        {
            return Brightness.Equals(other.Brightness) && Color == other.Color && Enabled == other.Enabled
                   && Shadows == other.Shadows && Range.Equals(other.Range);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPackagePointLight other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Brightness.GetHashCode() ^ (Range.GetHashCode() * 397) ^ (Enabled ? 1 : 0) ^ (Shadows ? 2 : 0);
        }
    }

    /// <summary>Canonical in-memory payload shared by package files and future join snapshots.</summary>
    public sealed class RbxWorldPackagePayload
    {
        private static readonly IReadOnlyDictionary<InstanceId, RbxWorldPackagePointLight> EmptyPointLights =
            new Dictionary<InstanceId, RbxWorldPackagePointLight>();

        private static readonly IReadOnlyDictionary<InstanceId, RbxWorldPackageTextChatCommand> EmptyTextChatCommands =
            new Dictionary<InstanceId, RbxWorldPackageTextChatCommand>();

        private static readonly IReadOnlyDictionary<InstanceId, RbxWorldPackageSelectionBox> EmptySelectionBoxes =
            new Dictionary<InstanceId, RbxWorldPackageSelectionBox>();

        private static readonly IReadOnlyDictionary<InstanceId, RbxWorldPackageHighlight> EmptyHighlights =
            new Dictionary<InstanceId, RbxWorldPackageHighlight>();

        private static readonly IReadOnlyDictionary<InstanceId, RbxWorldPackageGui> EmptyGui =
            new Dictionary<InstanceId, RbxWorldPackageGui>();

        private readonly RbxWorldPackageOptionalState _optional;

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
            : this(capturedAtUtc, settings, tree, parts, cameraCFrame, mods, diagnostics, null)
        {
        }

        /// <param name="optionalState">The optional blocks; copied, so a later change to it does not reach the payload.</param>
        public RbxWorldPackagePayload(
            DateTime capturedAtUtc,
            RbxWorldSettings settings,
            InstanceTreeSnapshot tree,
            IReadOnlyDictionary<InstanceId, PartProperties> parts,
            RbxCFrame? cameraCFrame,
            IReadOnlyList<RbxWorldModSource> mods,
            IReadOnlyList<RbxWorldPackageDiagnostic> diagnostics,
            RbxWorldPackageOptionalState optionalState)
        {
            CapturedAtUtc = capturedAtUtc;
            Settings = settings;
            Tree = tree;
            Parts = parts;
            CameraCFrame = cameraCFrame;
            Mods = mods;
            Diagnostics = diagnostics ?? Array.Empty<RbxWorldPackageDiagnostic>();
            _optional = optionalState?.Clone() ?? new RbxWorldPackageOptionalState();
        }

        public DateTime CapturedAtUtc { get; }

        public RbxWorldSettings Settings { get; }

        public InstanceTreeSnapshot Tree { get; }

        public IReadOnlyDictionary<InstanceId, PartProperties> Parts { get; }

        public RbxCFrame? CameraCFrame { get; }

        public IReadOnlyList<RbxWorldModSource> Mods { get; }

        public IReadOnlyList<RbxWorldPackageDiagnostic> Diagnostics { get; }

        /// <summary>
        /// The package format version this payload is written as: 1 without script bindings, 2 with them
        /// (<see cref="RbxWorldPackageOptionalState.FormatVersion"/>).
        /// </summary>
        public int FormatVersion => _optional.FormatVersion;

        /// <summary>The Workspace Terrain's cells, or null for a world without any.</summary>
        public TerrainSnapshot Terrain => _optional.Terrain;

        /// <summary>The properties of every PointLight the package stores, by instance id; never null.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackagePointLight> PointLights =>
            _optional.PointLights ?? EmptyPointLights;

        /// <summary>The Lighting service's day-cycle state, or null when the world does not drive its lighting.</summary>
        public RbxLightingDurableState? Lighting => _optional.Lighting;

        /// <summary>The StarterPlayer settings, or null when they are the class defaults.</summary>
        public RbxWorldPackageStarterPlayer? StarterPlayer => _optional.StarterPlayer;

        /// <summary>The persisted mod folders, sorted canonical paths; never null.</summary>
        public IReadOnlyList<string> ModFolders => _optional.ModFolders ?? Array.Empty<string>();

        /// <summary>The properties of every GUI node the package stores, by instance id; never null.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageGui> Gui => _optional.Gui ?? EmptyGui;

        /// <summary>The StarterGui switches, or null when they are the class defaults.</summary>
        public RbxWorldPackageStarterGui? StarterGui => _optional.StarterGui;

        /// <summary>The properties of every TextChatCommand the package stores, by instance id; never null.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageTextChatCommand> TextChatCommands =>
            _optional.TextChatCommands ?? EmptyTextChatCommands;

        /// <summary>The properties of every SelectionBox the package stores, by instance id; never null.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageSelectionBox> SelectionBoxes =>
            _optional.SelectionBoxes ?? EmptySelectionBoxes;

        /// <summary>The properties of every Highlight the package stores, by instance id; never null.</summary>
        public IReadOnlyDictionary<InstanceId, RbxWorldPackageHighlight> Highlights =>
            _optional.Highlights ?? EmptyHighlights;

        /// <summary>A copy of the optional blocks, for a caller that builds a payload from this one.</summary>
        public RbxWorldPackageOptionalState OptionalState => _optional.Clone();

        /// <summary>This payload with another capture time, every block kept.</summary>
        public RbxWorldPackagePayload WithCapturedAtUtc(DateTime capturedAtUtc)
        {
            return new RbxWorldPackagePayload(
                capturedAtUtc, Settings, Tree, Parts, CameraCFrame, Mods, Diagnostics, _optional);
        }
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
            DateTime? capturedAtUtc = null,
            IModFolderListStore modFolderListStore = null)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Game = game ?? throw new ArgumentNullException(nameof(game));
            PartSink = partSink;
            CameraRig = cameraRig;
            ModSourceStore = modSourceStore;
            ModFolderListStore = modFolderListStore;
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            CapturedAtUtc = capturedAtUtc;
        }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; }

        public IPartPropertySink PartSink { get; }

        public IRbxCameraRig CameraRig { get; }

        public ILuaModSourceStore ModSourceStore { get; }

        /// <summary>Where the world's persisted mod folders are kept; null captures none.</summary>
        public IModFolderListStore ModFolderListStore { get; }

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

        /// <summary>
        /// The authority of the registry the restore builds (MVP4 SC-16): <see cref="RegistryAuthority.Authoritative"/>
        /// (the default) for a server or solo world; <see cref="RegistryAuthority.Replica"/> for a client's copy of
        /// the server's world, restored inside one replication-apply scope so it keeps the server's ids and
        /// revisions and nothing is marked diverged.
        /// </summary>
        public RegistryAuthority RegistryAuthority { get; set; } = RegistryAuthority.Authoritative;
    }

    /// <summary>A freshly restored DataModel plus the exact mod sources that must restart once.</summary>
    public sealed class RbxWorldPackageRestoreResult
    {
        internal RbxWorldPackageRestoreResult(
            InstanceRegistry registry,
            RbxDataModel game,
            IPartPropertySink partSink,
            IReadOnlyList<RbxWorldModSource> mods,
            bool legacyScriptPlacement)
        {
            Registry = registry;
            Game = game;
            PartSink = partSink;
            Mods = mods;
            LegacyScriptPlacement = legacyScriptPlacement;
        }

        /// <summary>
        /// True for a format version 1 payload (MVP4 SC-7): no restored instance is bound to a source, so every active
        /// mod starts at the legacy placement (<c>ServerScriptService/&lt;id&gt;/&lt;id&gt;</c>), exactly as before
        /// script instances existed. False for version 2, whose script instances were restored with their bindings.
        /// </summary>
        public bool LegacyScriptPlacement { get; }

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

    /// <summary>
    /// The live world exceeds a limit of the world-package format itself (for example more mod
    /// sources than one package holds), so it cannot be captured until something is removed.
    /// </summary>
    /// <remarks>
    /// WHY a type of its own: the pre-mutation backup gate lets forget, the one action that brings the
    /// world back under the limit, run without its backup when capture fails for exactly this reason;
    /// any other capture failure still blocks every mutation. Without that exception the world could
    /// never be brought back under the limit.
    /// </remarks>
    public sealed class RbxWorldPackageFormatLimitException : RbxWorldPackageException
    {
        public RbxWorldPackageFormatLimitException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// The refusal of a new mod source past the world-package mod limit, worded the same wherever
        /// it is raised: by the session before a mod runs and by the source store that refuses to keep it.
        /// </summary>
        public static string DescribeModSourceLimit(string modId, int storedSources)
        {
            return "Mod '" + modId + "' cannot be added: this world already stores "
                   + storedSources.ToString(CultureInfo.InvariantCulture) + " mod sources, the most one world "
                   + "package holds (" + RbxWorldPackageSerializer.MaximumMods.ToString(CultureInfo.InvariantCulture)
                   + "). An unloaded mod keeps its source; forget a mod that is no longer needed with "
                   + "manage_mods action 'forget', then retry.";
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

        internal RbxWorldLoadResult(bool success, string error, int activeModsStarted, string status)
            : this(success, error, activeModsStarted, false, "", status)
        {
        }

        internal RbxWorldLoadResult(
            bool success,
            string error,
            int activeModsStarted,
            bool startupSelectionPersisted,
            string startupSelectionError,
            string status = "")
        {
            Success = success;
            Error = error ?? "";
            ActiveModsStarted = activeModsStarted;
            StartupSelectionPersisted = startupSelectionPersisted;
            StartupSelectionError = startupSelectionError ?? "";
            Status = status ?? "";
        }

        public bool Success { get; }

        public string Error { get; }

        /// <summary>
        /// Machine-readable reason of a load the session refused under its own rules
        /// (<see cref="RbxWorldLoadRefusedException.NetworkSessionsActiveStatus"/>,
        /// <see cref="RbxWorldLoadRefusedException.IncompatiblePackageStatus"/>); empty otherwise.
        /// </summary>
        public string Status { get; }

        public int ActiveModsStarted { get; }

        /// <summary>
        /// True only when this player-confirmed load was also durably recorded as the world that opens
        /// on the next start. A successful load with false stays live; the next start opens the
        /// previous startup selection instead.
        /// </summary>
        public bool StartupSelectionPersisted { get; }

        /// <summary>Why a successful confirmed load was not recorded for the next start; empty otherwise.</summary>
        public string StartupSelectionError { get; }

        /// <summary>
        /// Non-empty when the load ran without its pre-load safety autosave, because the outgoing world
        /// could not be encoded as a package at all (a format limit or a platform budget); says why.
        /// The outgoing world is then not recoverable from an autosave.
        /// </summary>
        public string BackupWarning { get; private set; } = "";

        /// <summary>
        /// The active mods of the loaded world that did not start, each as <c>id (reason)</c> (MVP4 step C1-4):
        /// <c>detached</c> for a declared source of a format version 2 world whose script instance the world does not
        /// hold (rule R3), <c>module</c> for a ModuleScript source, which only <c>require</c> runs. Empty otherwise.
        /// </summary>
        public IReadOnlyList<string> NotStartedMods { get; private set; } = Array.Empty<string>();

        /// <summary>A copy of this result that carries <paramref name="warning"/> as its <see cref="BackupWarning"/>.</summary>
        internal RbxWorldLoadResult WithBackupWarning(string warning)
        {
            return new RbxWorldLoadResult(
                Success, Error, ActiveModsStarted, StartupSelectionPersisted, StartupSelectionError, Status)
            {
                BackupWarning = warning ?? "",
                NotStartedMods = NotStartedMods
            };
        }

        /// <summary>A copy of this result that names <paramref name="notStarted"/> as its <see cref="NotStartedMods"/>.</summary>
        internal RbxWorldLoadResult WithNotStartedMods(IReadOnlyList<string> notStarted)
        {
            return new RbxWorldLoadResult(
                Success, Error, ActiveModsStarted, StartupSelectionPersisted, StartupSelectionError, Status)
            {
                BackupWarning = BackupWarning,
                NotStartedMods = notStarted ?? Array.Empty<string>()
            };
        }
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

    /// <summary>
    /// A mod source store that refuses, in <see cref="ILuaModSourceStore.Save"/>, a new mod id past the
    /// world-package mod limit, and answers the same question before anything runs. A world session asks
    /// it before a mod that adds a source is loaded or imported, and refuses the mod with the store's own
    /// refusal instead of running it. A host store implements it with the rule its Save applies: the
    /// built-in stores keep an id they already hold, and a new one only while they hold fewer than
    /// <see cref="RbxWorldPackageSerializer.MaximumMods"/> ids, and refuse with
    /// <see cref="RbxWorldPackageFormatLimitException.DescribeModSourceLimit"/> (Save throws
    /// <see cref="RbxWorldPackageFormatLimitException"/> with that text). A store without it is judged by
    /// its <see cref="ILuaModSourceStore.List"/> count, and a mod its Save then refuses is undone after
    /// its load.
    /// </summary>
    /// <remarks>
    /// WHY one rule for both sides: the session counted the manifests the store could list while the
    /// file store counted the folders holding a manifest file, so one unreadable manifest let the session
    /// admit a mod whose source the store then refused to keep. The mod ran, and was missing from every
    /// save and from the next start.
    /// </remarks>
    public interface ILuaModSourceAdmission
    {
        /// <summary>
        /// True when <see cref="ILuaModSourceStore.Save"/> would keep the source of
        /// <paramref name="modId"/> now; otherwise <paramref name="refusal"/> is the refusal that save
        /// raises. May throw when the store cannot answer (the session then refuses the load).
        /// </summary>
        bool CanAdmit(string modId, out string refusal);
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

            // WHY two shapes: a success carried "error":null, noise a small model reasoned over.
            return result.Success
                ? JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = result.Path,
                    message = "World saved to slot '" + slot + "'; load_world with this slot asks the player to load it."
                })
                : JsonConvert.SerializeObject(new
                {
                    success = false,
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
            // missing, corrupt, oversized or unreadable slot, or a session that was already shut down,
            // is an outcome the model can report or correct. Thrown, it crossed the tool invocation
            // boundary and was traced as possibly executed. Cancellation propagates.
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
            catch (ObjectDisposedException)
            {
                return RefuseUnloadable(
                    slot,
                    RbxWorldPackageNames.SessionUnavailableStatus,
                    RbxWorldPackageNames.DescribeSessionUnavailable("Manual slot '" + slot + "'"));
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
        private readonly RegistryAuthority _stagedAuthority;
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
            : this(registry, game, settings, partSink, cameraRig, RegistryAuthority.Authoritative)
        {
        }

        /// <summary>A host whose every staged world is a registry of <paramref name="stagedAuthority"/> (see <see cref="ReplicaRbxWorldSessionHost"/>).</summary>
        internal HeadlessRbxWorldSessionHost(
            InstanceRegistry registry,
            RbxDataModel game,
            RbxWorldSettings settings,
            IPartPropertySink partSink,
            IRbxCameraRig cameraRig,
            RegistryAuthority stagedAuthority)
        {
            _stagedAuthority = stagedAuthority;
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
                    CameraRig = stagedCamera,
                    RegistryAuthority = _stagedAuthority
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
    /// package store keeps a startup selection (<see cref="IRbxWorldStartupStore"/>), and while that
    /// world stays live every mutation through the shared gate records it again as it is.
    /// </summary>
    public sealed class RbxWorldRuntimeSessionController
        : IRbxWorldRuntimeService, IRbxWorldStartupSelection, IRbxWorldUndoHost, IDisposable
    {
        private const int MaximumPendingLoadRequests = 8;
        private static readonly TimeSpan DefaultPendingLoadTimeToLive = TimeSpan.FromMinutes(2d);
        /// <summary>The backup trigger of the safety autosave a confirmed load writes of the outgoing world.</summary>
        internal const string PreLoadAutosaveTrigger = "load_world-pre";
        private const string ManualSourceKind = "manual";
        private const string AutosaveSourceKind = "autosave";
        private const string NonTransactionalSourceStoreRefusal =
            "The configured mod source store cannot atomically replace a world source set.";
        private const string SourceChangeTrigger = "mod source change";
        private const string WorldChangeTrigger = "world change";

        /// <summary>The default of <see cref="StartupRefreshInterval"/>.</summary>
        public static readonly TimeSpan DefaultStartupRefreshInterval = TimeSpan.FromSeconds(5d);

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
        private readonly IConfirmedWorldMutationGate _worldMutationGate;
        private readonly SemaphoreSlim _startupSelectionGate = new(1, 1);
        private readonly Dictionary<string, PendingLoad> _pendingLoads =
            new(StringComparer.Ordinal);
        private Session _current;
        private StartupSource _liveStartupSource;
        private bool _startupRefreshPending;
        private bool _startupRefreshRunning;
        private bool _worldRefreshDeferred;
        private DateTime? _lastWorldRefreshUtc;
        private TimeSpan _startupRefreshInterval = DefaultStartupRefreshInterval;
        private long _sourceChanges;
        private long _sourceChangesAtMutationStart;
        private InstanceRegistry _watchedRegistry;
        private bool _mutationWatchActive;
        private int _worldChangedDuringMutation;
        private ulong _valuesAtMutationStart;
        private string _startupSelectionNote = "";
        private Task _startupRefreshAfterSourceChanges = Task.CompletedTask;
        private byte[] _lastRefusedStartupContent;
        private string _lastReportedStartupRefusal = "";
        private bool _disposed;
        private bool _loadInProgress;
        private Func<DateTime> _utcNow = () => DateTime.UtcNow;
        private TimeSpan _pendingLoadTimeToLive = DefaultPendingLoadTimeToLive;
        private string _lastAdvanceFailure;
        private string _lastTickFailure;
        private Action<string> _mutationStartingHook;
        private Func<string, CancellationToken, UniTask<string>> _afterMutationHook;
        private TimeSpan _startupRefreshDebounce = TimeSpan.Zero;
        private bool _debouncedRefreshPending;
        private bool _debouncedWorldOnly = true;
        private DateTime _debounceFirstChangeUtc;
        private DateTime _debounceLastChangeUtc;
        private string _deliveredStartupNote = "";
        private bool _forceStartupRecordAfterMutation;
        private readonly IReadOnlyList<RbxMissingAutoSave> _missingAutoSavesAtStart;
        private IRbxWorldAutoSaveWriteBehindStore _missingAutoSavesToAcknowledge;
        private bool _keepWorld;
        private IReadOnlyList<CoreAI.Infrastructure.Lua.IBundledModSource> _bundledForRestore =
            Array.Empty<CoreAI.Infrastructure.Lua.IBundledModSource>();
        private RbxTerrain _watchedTerrain;
        private RbxInstance _watchedWorkspace;
        private InstanceRegistry _watchedKeptRegistry;
        private Action<TerrainChangeSet> _onWatchedTerrainChanged;
        private Action<InstanceRecord> _onKeptInstanceAddedOrRemoved;
        private object _lastWorldEdits;
        private bool _worldEditsSeen;
        private string _lastKeptRecord = "";
        private readonly List<RbxWorldPackageEncodeUrgency> _recordEncodes = new();
        private RbxTerrain _editCountedTerrain;
        private RbxInstance _editCountedWorkspace;
        private Action<TerrainChangeSet> _onEditCountedTerrainChanged;
        private long _terrainEdits;

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
            // WHY the gate is read off the initial stack: every session stack of one composition runs
            // its execute_lua and manage_mods mutations through the same shared gate, so a load that
            // takes it is serialized with them, and the gate reports each finished mutation back here
            // to keep the startup selection current. A stack composed without a gate has neither.
            _worldMutationGate = initialStack.ToolExecutor.WorldMutationGate;
            if (_worldMutationGate is IStartupAwareWorldMutationGate startupAwareGate)
            {
                _mutationStartingHook = OnGatedMutationStarting;
                _afterMutationHook = RefreshStartupSelectionAfterMutationAsync;
                startupAwareGate.MutationStarting = _mutationStartingHook;
                startupAwareGate.AfterMutationAsync = _afterMutationHook;
            }
            else if (_worldMutationGate != null && _startupStore != null)
            {
                // WHY said at once: the fallback loses gated world changes at restart, and a host that
                // composed its own gate otherwise finds out only after one.
                ReportDiagnostic(
                    "The shared world mutation gate (" + _worldMutationGate.GetType().Name + ") does not "
                    + "implement IStartupAwareWorldMutationGate, so the world that opens on the next start "
                    + "follows mod source changes only: gated changes to the world tree are not recorded "
                    + "for the next start, the startup restore runs without the gate, and a source change "
                    + "may be recorded in the middle of a gated mutation. Implement it, or forward it to "
                    + "the ConfirmedWorldMutationGate the host gate wraps.");
            }

            IRbxWorldAutoSaveWriteBehindStore writeBehindStore = packageStore as IRbxWorldAutoSaveWriteBehindStore;
            _missingAutoSavesAtStart = writeBehindStore?.MissingAutoSavesAtOpen ?? Array.Empty<RbxMissingAutoSave>();
            string missingBackups = RbxMissingAutoSave.Describe(_missingAutoSavesAtStart);
            if (missingBackups.Length > 0)
            {
                // WHY at construction: this is the first moment of the next start that has a sink, and a
                // backup lost to a crash must be said once, plainly, instead of looking like a corrupt ring.
                // WHY acknowledged only by the first frame: a composition that never runs a frame (an
                // EditMode test over the real save root) must not consume the report the game still owes.
                ReportDiagnostic(missingBackups);
                _missingAutoSavesToAcknowledge = writeBehindStore;
            }

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

        /// <summary>
        /// The least wall-clock time between two records of the startup world that only gated changes to
        /// the world tree call for (an execute_lua build); 5 s by default, zero records each at once. A
        /// change inside the interval is recorded by the frame pump once the interval has passed, together
        /// with every change made meanwhile. A change to the mod sources is always recorded at once.
        /// </summary>
        /// <remarks>
        /// WHY an interval: each record is a whole package written, synced and pruned while the gate is
        /// held (on WebGL up to 4 MB handed to the browser), and an AI that builds in many small calls
        /// asked for one per call. WHY the first change after a quiet interval is recorded at once: a
        /// single change reopens after a restart as soon as its call returns. A change made within the
        /// interval before the process ends is not recorded.
        /// </remarks>
        public TimeSpan StartupRefreshInterval
        {
            get
            {
                lock (_gate)
                {
                    return _startupRefreshInterval;
                }
            }
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "The interval cannot be negative.");
                }

                lock (_gate)
                {
                    _startupRefreshInterval = value;
                }
            }
        }

        /// <summary>
        /// The workspace attribute a world generator sets to true around its own Terrain writes: cells that come back
        /// from a seed need no save, so while it is true a Terrain write does not mark a kept world for its next record.
        /// </summary>
        public const string TerrainGeneratingAttribute = "CoreAITerrainGenerating";

        /// <summary>The source kind of the startup entry a kept default world is recorded as.</summary>
        public const string KeptWorldSourceKind = "kept";

        /// <summary>
        /// True once <see cref="KeepWorldBetweenStarts"/> was called: the default world is recorded for the next start
        /// as it changes, and a restored world takes a strictly newer bundled version of a seeded mod.
        /// </summary>
        public bool KeepsWorldBetweenStarts
        {
            get
            {
                lock (_gate)
                {
                    return _keepWorld;
                }
            }
        }

        /// <summary>What the last record of a kept world held and cost, in words; empty before the first.</summary>
        public string LastKeptWorldRecord
        {
            get
            {
                lock (_gate)
                {
                    return _lastKeptRecord;
                }
            }
        }

        /// <summary>
        /// Keeps the world between starts (a play world setting, off by default; call before the startup restore):
        /// a restored world takes a strictly newer version of a mod <paramref name="bundled"/> seeded, with the
        /// seeder's rule, and once <see cref="AdoptDefaultWorldIfKept"/> made the default world the startup world, the
        /// player's and the AI's changes to it are recorded as they happen: a change to the mod sources at once, a
        /// Terrain write outside <see cref="TerrainGeneratingAttribute"/> or a gated change to the world tree at most
        /// once per <see cref="StartupRefreshInterval"/>.
        /// </summary>
        public void KeepWorldBetweenStarts(IReadOnlyList<CoreAI.Infrastructure.Lua.IBundledModSource> bundled)
        {
            lock (_gate)
            {
                _keepWorld = true;
                _bundledForRestore = bundled ?? Array.Empty<CoreAI.Infrastructure.Lua.IBundledModSource>();
                // WHY a debounce unless the host chose its own: a record captures and encodes the whole world (about
                // 120 ms of main-thread work at radius 8 in the editor, the encode spread over frames), so it waits
                // until the player stopped changing the world for a moment, and one who never stops gets a record a
                // minute; /save records at once.
                if (_startupRefreshInterval == DefaultStartupRefreshInterval)
                {
                    _startupRefreshInterval = KeptWorldRefreshInterval;
                }

                // WHY at least the quiet time: the instant edit loop sets a shorter debounce for the AI's bursts, and a
                // kept world's player must have stopped building before the hitch of a record.
                if (_startupRefreshDebounce < KeptWorldQuietTime)
                {
                    _startupRefreshDebounce = KeptWorldQuietTime;
                }
            }
        }

        /// <summary>
        /// The longest a kept world's change waits for its record while the world keeps changing: 60 s (the
        /// <see cref="StartupRefreshInterval"/> unless the host set its own).
        /// </summary>
        public static readonly TimeSpan KeptWorldRefreshInterval = TimeSpan.FromSeconds(60d);

        /// <summary>
        /// How long a kept world must stay unchanged before its changes are recorded: 3 s (the
        /// <see cref="StartupRefreshDebounce"/> of a kept world is at least this).
        /// </summary>
        public static readonly TimeSpan KeptWorldQuietTime = TimeSpan.FromSeconds(3d);

        /// <summary>
        /// A number on the Workspace a mod raises after a change the world should keep that is neither a Terrain write
        /// nor an instance of the world (a torch a mod rebuilds from its own attribute): a kept world is then recorded
        /// like after a block.
        /// </summary>
        public const string WorldEditsAttribute = "CoreAIWorldEdits";

        /// <summary>
        /// After a start that restored nothing: makes the default world the world that opens on the next start, when
        /// the world is kept and the package store keeps a startup selection. Its first record follows its first change.
        /// </summary>
        public void AdoptDefaultWorldIfKept()
        {
            lock (_gate)
            {
                if (!_keepWorld || _disposed || _startupStore == null || _liveStartupSource != null)
                {
                    return;
                }

                SetLiveStartupSourceLocked(new StartupSource(KeptWorldSourceKind, "default", null));
            }
        }

        /// <summary>
        /// Records a kept world now (the <c>/save</c> chat command) and answers in one sentence what was saved, or why
        /// nothing was. Never throws.
        /// </summary>
        public async UniTask<string> SaveKeptWorldNowAsync()
        {
            bool kept;
            lock (_gate)
            {
                kept = _keepWorld && _startupStore != null && _liveStartupSource != null && !_disposed;
            }

            if (!kept)
            {
                return "This world is not kept between starts, so there is nothing to save; its mods are saved as they change.";
            }

            string note;
            try
            {
                lock (_gate)
                {
                    // WHY forced: /save answers for the world as it is now, even when the last record already holds it.
                    _forceStartupRecordAfterMutation = false;
                }

                note = await RefreshStartupSelectionAsync("save", false, true);
            }
            catch (Exception ex)
            {
                note = ex.Message;
            }

            if (note.Length > 0)
            {
                return "The world was not saved: " + note;
            }

            return "Saved " + LastKeptWorldRecord + "."
#if UNITY_WEBGL && !UNITY_EDITOR
                   + " The browser stores it a moment later."
#endif
                ;
        }

        /// <summary>Follows the current session's Terrain, so a write outside generation marks a kept world.</summary>
        private void WatchTerrainOf(Session session)
        {
            if (!_keepWorld)
            {
                return;
            }

            RbxInstance workspace = session?.RbxApi?.Game?.FindFirstChildOfClass("Workspace");
            WatchWorldEditsOf(workspace);
            WatchInstancesOf(session?.RbxApi?.Registry);
            RbxTerrain terrain = workspace != null ? RbxTerrainBootstrap.Find(workspace) : null;
            if (ReferenceEquals(terrain, _watchedTerrain))
            {
                return;
            }

            _onWatchedTerrainChanged ??= OnWatchedTerrainChanged;
            if (_watchedTerrain != null)
            {
                _watchedTerrain.Store.Changed -= _onWatchedTerrainChanged;
            }

            _watchedTerrain = terrain;
            _watchedWorkspace = workspace;
            if (terrain != null)
            {
                terrain.Store.Changed += _onWatchedTerrainChanged;
            }
        }

        private void OnWatchedTerrainChanged(TerrainChangeSet changes)
        {
            // WHY read at the write: a generator marks its own writes, and Lua runs one handler at a time, so a write
            // made while the attribute is true is the generator's.
            if (Equals(_watchedWorkspace?.GetAttribute(TerrainGeneratingAttribute), true))
            {
                return;
            }

            DebounceStartupRefresh(true);
        }

        /// <summary>Marks a kept world when <see cref="WorldEditsAttribute"/> changed since the last frame.</summary>
        private void WatchWorldEditsOf(RbxInstance workspace)
        {
            object edits = workspace?.GetAttribute(WorldEditsAttribute);
            if (!_worldEditsSeen || !ReferenceEquals(workspace, _watchedWorkspace))
            {
                _worldEditsSeen = true;
                _lastWorldEdits = edits;
                return;
            }

            if (!Equals(edits, _lastWorldEdits))
            {
                _lastWorldEdits = edits;
                DebounceStartupRefresh(true);
            }
        }

        /// <summary>Follows the registry, so an instance of the world added or removed outside the gate marks it.</summary>
        private void WatchInstancesOf(InstanceRegistry registry)
        {
            if (ReferenceEquals(registry, _watchedKeptRegistry))
            {
                return;
            }

            _onKeptInstanceAddedOrRemoved ??= OnKeptInstanceAddedOrRemoved;
            if (_watchedKeptRegistry != null)
            {
                _watchedKeptRegistry.Registered -= _onKeptInstanceAddedOrRemoved;
                _watchedKeptRegistry.Unregistered -= _onKeptInstanceAddedOrRemoved;
            }

            _watchedKeptRegistry = registry;
            if (registry != null)
            {
                registry.Registered += _onKeptInstanceAddedOrRemoved;
                registry.Unregistered += _onKeptInstanceAddedOrRemoved;
            }
        }

        private void OnKeptInstanceAddedOrRemoved(InstanceRecord record)
        {
            // WHY only the world's own instances: a mod's are rebuilt by the mod and never stored in the package.
            if (record != null && IsWorldOwned(record) && !(record.Instance is RbxTerrain))
            {
                DebounceStartupRefresh(true);
            }
        }

        /// <summary>
        /// A restored package with every seeded mod a strictly newer bundle replaces (the seeder's rule); the payload
        /// itself when none is newer. The replaced source stays in the mod's revision history.
        /// </summary>
        private RbxWorldPackagePayload WithNewerBundledMods(RbxWorldPackagePayload payload, out int replaced)
        {
            replaced = 0;
            IReadOnlyList<CoreAI.Infrastructure.Lua.IBundledModSource> sources;
            lock (_gate)
            {
                sources = _keepWorld ? _bundledForRestore : null;
            }

            if (sources == null || sources.Count == 0 || payload?.Mods == null || payload.Mods.Count == 0)
            {
                return payload;
            }

            Dictionary<string, (CoreAI.Infrastructure.Lua.BundledMod Mod, string Origin)> bundled =
                new(StringComparer.Ordinal);
            foreach (CoreAI.Infrastructure.Lua.IBundledModSource source in sources)
            {
                IReadOnlyList<CoreAI.Infrastructure.Lua.BundledMod> mods;
                try
                {
                    mods = source?.Load();
                }
                catch (Exception ex)
                {
                    ReportDiagnostic("A bundled mod source failed at the startup restore: " + ex.Message);
                    continue;
                }

                if (mods == null)
                {
                    continue;
                }

                string origin = string.IsNullOrWhiteSpace(source.Origin) ? "bundled" : source.Origin.Trim();
                foreach (CoreAI.Infrastructure.Lua.BundledMod mod in mods)
                {
                    string id = (mod.Id ?? "").Trim();
                    if (id.Length > 0 && !bundled.ContainsKey(id))
                    {
                        bundled[id] = (mod, origin);
                    }
                }
            }

            List<RbxWorldModSource> updated = new(payload.Mods.Count);
            foreach (RbxWorldModSource packaged in payload.Mods)
            {
                if (packaged?.Manifest != null
                    && bundled.TryGetValue((packaged.Manifest.Id ?? "").Trim(),
                        out (CoreAI.Infrastructure.Lua.BundledMod Mod, string Origin) newer)
                    && CoreAI.Infrastructure.Lua.BundledModSeeder.TryUpdateInstalled(newer.Mod, newer.Origin,
                        packaged.Source, packaged.Manifest, out string source, out LuaModManifest manifest))
                {
                    // WHY recorded before the load: the load records the new source as the current revision, so the
                    // packaged one stays the revision before it and /revert can bring it back.
                    _versionStore.RecordSuccessfulExecution(LuaCsModRuntime.VersionKeyPrefix + manifest.Id, packaged.Source);
                    updated.Add(new RbxWorldModSource(manifest, source));
                    replaced++;
                    continue;
                }

                updated.Add(packaged);
            }

            return replaced == 0
                ? payload
                : new RbxWorldPackagePayload(payload.CapturedAtUtc, payload.Settings, payload.Tree, payload.Parts,
                    payload.CameraCFrame, updated, payload.Diagnostics, payload.OptionalState);
        }

        /// <summary>
        /// Quiet time after a gated change before the world that opens on the next start is recorded:
        /// zero (the default) records it inside the gated call, as before; a positive value lets the frame
        /// pump record a burst of AI edits once, when no gated change came for this long, and at the latest
        /// <see cref="StartupRefreshInterval"/> (or this value, if longer) after the first change of the
        /// burst. A record that then fails is reported through <see cref="StartupSelectionNote"/> and in the
        /// result of the next gated call.
        /// </summary>
        /// <remarks>
        /// WHY: each record is a whole package written while the gate is held, so a burst of AI edits paid
        /// one full write per call in front of its tool result. Changes made within the debounce before the
        /// process ends are not recorded for the next start; their autosaves still are.
        /// </remarks>
        public TimeSpan StartupRefreshDebounce
        {
            get
            {
                lock (_gate)
                {
                    return _startupRefreshDebounce;
                }
            }
            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "The debounce cannot be negative.");
                }

                lock (_gate)
                {
                    _startupRefreshDebounce = value;
                }
            }
        }

        /// <summary>
        /// The autosaves written behind a mutation that a crash interrupted before the store opened at this
        /// start (reported once through the diagnostics sink); empty for a store without write-behind support.
        /// </summary>
        public IReadOnlyList<RbxMissingAutoSave> MissingAutoSavesAtStart => _missingAutoSavesAtStart;

        /// <summary>
        /// Why the latest change to the live startup world was not recorded for the next start (the
        /// startup restore would refuse the world, or the record was not durably written), whichever path
        /// made the change, gated tool or Hub; empty once a later record succeeded or when nothing failed.
        /// A gated tool also carries the note in its result; this is where a Hub or a host reads it for
        /// a change it made outside the gate.
        /// </summary>
        public string StartupSelectionNote
        {
            get
            {
                lock (_gate)
                {
                    return _startupSelectionNote;
                }
            }
        }

        /// <summary>
        /// The refresh of the startup selection that <see cref="PumpFrame"/> last started after mod
        /// sources changed outside the shared gate; completed when none is running. For tests.
        /// </summary>
        internal Task StartupRefreshForTests
        {
            get
            {
                lock (_gate)
                {
                    return _startupRefreshAfterSourceChanges;
                }
            }
        }

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
                session.SourceStore,
                modFolderListStore: ModFolderListStore));
        }

        /// <summary>
        /// The shared list of persisted mod folders of this world's mod store (the one the Hub page and
        /// <c>manage_mods</c> use); null keeps no folder in any capture. Capture reads it, and a confirmed load or a
        /// restore writes the package's folders into it before the staged start and puts the previous list back
        /// when the load does not publish. Set it before the first capture.
        /// </summary>
        public IModFolderListStore ModFolderListStore { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// WHY the Terrain counted by its edits: a world generator streams land in while the player looks around (a
        /// Block World writes a column every second or so until its whole radius is made), and every one of those
        /// writes moved the store's revision, so an undo asked for a few frames after an AI change was refused as if
        /// somebody had edited the world. A write made while the generator marks it
        /// (<see cref="TerrainGeneratingAttribute"/>) comes back from the seed: the undo restores the snapshot and the
        /// generator makes that land again. A block the player, a mod or the Hub changes still counts.
        /// </remarks>
        public RbxWorldStateMark MarkPublishedWorld()
        {
            InstanceRegistry registry;
            long sourceChanges;
            Session session;
            lock (_gate)
            {
                DemandActiveLocked();
                session = _current;
                registry = session.RbxApi.Registry;
                sourceChanges = _sourceChanges;
            }

            return RbxWorldStateMark.Of(registry, sourceChanges, FollowTerrainEditsOf(session));
        }

        /// <summary>
        /// Counts, from the first mark on, the writes to <paramref name="session"/>'s Terrain that a world generator did
        /// not mark as its own; answers the count.
        /// </summary>
        private long FollowTerrainEditsOf(Session session)
        {
            RbxInstance workspace = session?.RbxApi?.Game?.FindFirstChildOfClass("Workspace");
            RbxTerrain terrain = workspace != null ? RbxTerrainBootstrap.Find(workspace) : null;
            lock (_gate)
            {
                if (!ReferenceEquals(terrain, _editCountedTerrain))
                {
                    _onEditCountedTerrainChanged ??= OnEditCountedTerrainChanged;
                    if (_editCountedTerrain != null)
                    {
                        _editCountedTerrain.Store.Changed -= _onEditCountedTerrainChanged;
                    }

                    _editCountedTerrain = terrain;
                    _editCountedWorkspace = workspace;
                    if (terrain != null && !_disposed)
                    {
                        terrain.Store.Changed += _onEditCountedTerrainChanged;
                    }
                }

                return Interlocked.Read(ref _terrainEdits);
            }
        }

        private void OnEditCountedTerrainChanged(TerrainChangeSet changes)
        {
            // WHY read at the write, as the kept world does: Lua runs one handler at a time, so a write made while the
            // attribute is true is the generator's.
            if (Equals(Volatile.Read(ref _editCountedWorkspace)?.GetAttribute(TerrainGeneratingAttribute), true))
            {
                return;
            }

            Interlocked.Increment(ref _terrainEdits);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The same staged replacement as a confirmed load, refused by the same session rules, but with no
        /// safety autosave of its own (the caller's gated call wrote one) and without leaving the startup
        /// world: when the live world is the one that opens on the next start, the restored world is
        /// recorded as it. Must run inside the shared gate; taking it here would wait for itself.
        /// </remarks>
        public async UniTask<RbxWorldLoadResult> RestoreSnapshotInPlaceAsync(
            RbxWorldPackagePayload payload,
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
                return new RbxWorldLoadResult(
                    false, networkRefusal, 0, RbxWorldLoadRefusedException.NetworkSessionsActiveStatus);
            }

            string packageRefusal = DescribeUnloadablePackage(payload);
            if (packageRefusal.Length > 0)
            {
                return new RbxWorldLoadResult(
                    false, packageRefusal, 0, RbxWorldLoadRefusedException.IncompatiblePackageStatus);
            }

            if (_transactionalSourceStore == null)
            {
                return new RbxWorldLoadResult(
                    false,
                    NonTransactionalSourceStoreRefusal,
                    0,
                    RbxWorldLoadRefusedException.IncompatiblePackageStatus);
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

            try
            {
                StartupSource keep = ReadLiveStartupSource();
                RbxWorldLoadResult replaced = await ReplaceWorldAsync(payload, null, keep, cancellationToken);
                if (replaced != null && replaced.Success && keep != null)
                {
                    lock (_gate)
                    {
                        _forceStartupRecordAfterMutation = true;
                    }
                }

                return replaced;
            }
            finally
            {
                lock (_gate)
                {
                    _loadInProgress = false;
                }
            }
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
        /// would refuse to load at confirmation (live network sessions, a legacy ACL downgrade, an
        /// active Full-capability mod, a source store that cannot replace a source set) throws
        /// <see cref="RbxWorldLoadRefusedException"/> instead, so the player is never asked to confirm
        /// a package that cannot open. A session that was shut down throws
        /// <see cref="ObjectDisposedException"/> before anything is read.
        /// </summary>
        public async UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
            ActorContext caller,
            string slot,
            CancellationToken cancellationToken = default)
        {
            DemandTrusted(caller);
            DemandActive();
            DemandNoLiveNetworkSessions();
            DemandTransactionalSourceStore();
            RbxWorldPackagePayload payload = await _packageStore.LoadManualAsync(
                slot, cancellationToken);
            DemandLoadablePackage(payload);
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
            DemandActive();
            DemandNoLiveNetworkSessions();
            DemandTransactionalSourceStore();
            RbxWorldPackagePayload payload = await _packageStore.LoadAutoAsync(
                autoFileName, cancellationToken);
            DemandLoadablePackage(payload);
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

            return await LoadConfirmedCoreAsync(pending.Payload, true, pending, null, cancellationToken);
        }

        /// <summary>
        /// Trusted host load of an already confirmed payload. It never changes the world that opens on
        /// the next start; a host that wants that records it through its own startup store.
        /// </summary>
        public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken = default)
        {
            return LoadConfirmedCoreAsync(payload, true, null, null, cancellationToken);
        }

        public async UniTask<RbxWorldStartupRestoreResult> RestoreStartupSelectionAsync(
            CancellationToken cancellationToken = default)
        {
            if (_startupStore == null)
            {
                return new RbxWorldStartupRestoreResult(
                    RbxWorldStartupRestoreOutcome.NotSelected, 0, "", 0, "");
            }

            if (!(_worldMutationGate is IStartupAwareWorldMutationGate startupAwareGate))
            {
                return await RestoreStartupSelectionCoreAsync(cancellationToken);
            }

            // WHY the whole restore holds the shared gate, as a confirmed load does: an execute_lua or
            // manage_mods call that arrives during boot otherwise backed up the default world and then
            // changed the restored one, and the change missed the startup selection because the restored
            // world was not yet marked as the startup world. Held here, it waits and runs against the
            // restored world, which is by then the startup world its refresh records.
            try
            {
                return await startupAwareGate.ExecuteWithoutBackupAsync(
                    token => RestoreStartupSelectionCoreAsync(token).AsTask(),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return FallBackAtStartup(
                    0, "", "the startup restore could not take the world mutation gate: " + ex.Message);
            }
        }

        private async UniTask<RbxWorldStartupRestoreResult> RestoreStartupSelectionCoreAsync(
            CancellationToken cancellationToken)
        {
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
            int replacedByBundles = 0;
            try
            {
                // WHY a kept world takes newer bundles: its package holds the exact sources of the last start, and
                // without the seeder's rule a returning player would never get a sample's next version.
                RbxWorldPackagePayload payload = WithNewerBundledMods(selection.Payload, out replacedByBundles);
                // WHY no pre-load safety autosave: the outgoing world at boot is the reproducible
                // default world, and one autosave per boot would evict the real backups from the
                // bounded ring within a handful of restarts.
                loaded = await LoadConfirmedCoreAsync(
                    payload,
                    false,
                    null,
                    new StartupSource(
                        selection.SourceKind,
                        selection.SourceName,
                        ComputeStartupContentOrNull(selection.Payload)),
                    cancellationToken);
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

            if (replacedByBundles > 0)
            {
                lock (_gate)
                {
                    // WHY recorded on the next frame: the entry on disk still holds the replaced sources.
                    _startupRefreshPending = true;
                }
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

            // WHY under the startup gate: a refresh of the selection that is already running would
            // otherwise land after this marker and quietly undo the player's choice.
            await _startupSelectionGate.WaitAsync(cancellationToken);
            try
            {
                RbxWorldPackageWriteResult cleared = await _startupStore.ClearStartupAsync(cancellationToken);
                if (cleared != null && cleared.Success)
                {
                    SetLiveStartupSource(null);
                }

                return cleared;
            }
            finally
            {
                _startupSelectionGate.Release();
            }
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
        /// <param name="restoredStartup">
        /// The startup entry the boot-time restore publishes; non-null marks the published world as
        /// the startup world without recording it again.
        /// </param>
        private async UniTask<RbxWorldLoadResult> LoadConfirmedCoreAsync(
            RbxWorldPackagePayload payload,
            bool writeSafetyAutosave,
            PendingLoad startupSource,
            StartupSource restoredStartup,
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
                return new RbxWorldLoadResult(
                    false,
                    networkRefusal,
                    0,
                    RbxWorldLoadRefusedException.NetworkSessionsActiveStatus);
            }

            string packageRefusal = DescribeUnloadablePackage(payload);
            if (packageRefusal.Length > 0)
            {
                return new RbxWorldLoadResult(
                    false,
                    packageRefusal,
                    0,
                    RbxWorldLoadRefusedException.IncompatiblePackageStatus);
            }

            if (_transactionalSourceStore == null)
            {
                return new RbxWorldLoadResult(
                    false,
                    NonTransactionalSourceStoreRefusal,
                    0,
                    RbxWorldLoadRefusedException.IncompatiblePackageStatus);
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

            try
            {
                if (!writeSafetyAutosave)
                {
                    return await ReplaceWorldAsync(payload, startupSource, restoredStartup, cancellationToken);
                }

                if (_worldMutationGate == null)
                {
                    (string safetyFailure, string backupWarning) =
                        await WriteSafetyAutosaveAsync(cancellationToken);
                    if (safetyFailure.Length > 0)
                    {
                        return new RbxWorldLoadResult(false, safetyFailure, 0);
                    }

                    RbxWorldLoadResult replaced =
                        await ReplaceWorldAsync(payload, startupSource, restoredStartup, cancellationToken);
                    return backupWarning.Length == 0 ? replaced : replaced.WithBackupWarning(backupWarning);
                }

                // WHY the whole replacement runs inside the shared gate: execute_lua and manage_mods
                // take the same gate, so no AI mutation can land between the safety capture and the
                // publish. Captured outside it, a mutation that finished in between was in neither the
                // safety autosave nor the new world, and was lost without a trace.
                try
                {
                    return await _worldMutationGate.ExecuteAsync(
                        PreLoadAutosaveTrigger,
                        token => ReplaceWorldAsync(payload, startupSource, restoredStartup, token).AsTask(),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    return new RbxWorldLoadResult(
                        false,
                        "Pre-load safety autosave '" + PreLoadAutosaveTrigger
                            + "' did not complete, so the live world was not changed: " + ex.Message,
                        0);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _loadInProgress = false;
                }
            }
        }

        /// <summary>
        /// The safety autosave of a load composed without a shared gate. Returns why it failed (empty
        /// once the store confirmed it), and a backup warning when the outgoing world cannot be encoded
        /// as a package at all, so the load runs without the autosave, as it does through the gate.
        /// </summary>
        private async UniTask<(string Failure, string BackupWarning)> WriteSafetyAutosaveAsync(
            CancellationToken cancellationToken)
        {
            RbxWorldPackagePayload currentPayload;
            try
            {
                currentPayload = CaptureCurrent();
            }
            catch (RbxWorldPackageFormatLimitException ex)
            {
                return ("", ConfirmedWorldMutationGate.DescribeSkippedBackup(PreLoadAutosaveTrigger, ex.Message));
            }
            catch (Exception ex)
            {
                return ("Pre-load safety autosave capture failed: " + ex.Message, "");
            }

            RbxWorldPackageWriteResult safetyAutosave;
            try
            {
                safetyAutosave = await _packageStore.CreateAutoAsync(
                    PreLoadAutosaveTrigger,
                    currentPayload,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return ("Pre-load safety autosave '" + PreLoadAutosaveTrigger + "' failed: " + ex.Message, "");
            }

            if (safetyAutosave == null || !safetyAutosave.Success)
            {
                string reason = safetyAutosave == null || string.IsNullOrWhiteSpace(safetyAutosave.Error)
                    ? "durability not confirmed"
                    : safetyAutosave.Error;
                if (safetyAutosave != null && safetyAutosave.PackageCannotBeEncoded)
                {
                    return ("", ConfirmedWorldMutationGate.DescribeSkippedBackup(PreLoadAutosaveTrigger, reason));
                }

                return ("Pre-load safety autosave '" + PreLoadAutosaveTrigger + "' failed: " + reason, "");
            }

            return ("", "");
        }

        /// <summary>
        /// Stages, starts and publishes <paramref name="payload"/> after its safety autosave. Never
        /// throws: a failure before publication rolls the staged world and its sources back and is
        /// returned as the result.
        /// </summary>
        private async UniTask<RbxWorldLoadResult> ReplaceWorldAsync(
            RbxWorldPackagePayload payload,
            PendingLoad startupSource,
            StartupSource restoredStartup,
            CancellationToken cancellationToken)
        {
            IRbxWorldSessionCandidate candidate = null;
            StagedNetworkBridge stagedNetwork = null;
            LuaCsRbxApiBindings stagedRbxApi = null;
            LuaCsModStack stagedStack = null;
            IRbxWorldModSourceReplacement sourceReplacement = null;
            BufferedLuaModStore stagedModStore = new(_modStore);
            DeferredLuaScriptVersionStore stagedVersions = new(_versionStore);
            bool published = false;
            IModFolderListStore folderStore = ModFolderListStore;
            IReadOnlyList<string> previousFolders = null;
            try
            {
                sourceReplacement = await _transactionalSourceStore.PrepareExactReplacementAsync(
                    payload.Mods, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (folderStore != null)
                {
                    previousFolders = folderStore.Load();
                    folderStore.Save(payload.ModFolders);
                }

                candidate = _host.Stage(payload);
                stagedNetwork = new StagedNetworkBridge(_networkBridge);
                stagedRbxApi = _rbxApiFactory(candidate, stagedNetwork);
                SessionSourceStore stagedSources = new(this, sourceReplacement.SourceStore);
                stagedStack = _stackFactory(
                    stagedRbxApi,
                    stagedSources,
                    stagedModStore,
                    stagedVersions);
                _wireTeardown(stagedStack, stagedRbxApi);
                CopyDeclaredLogicSlots(
                    Current.Stack.GameplayBindings.LogicSlots,
                    stagedStack.GameplayBindings.LogicSlots);
                // WHY the package's format decides: in a version 1 world every active mod starts at its legacy
                // placement or gets its script instance there, while in version 2 a declared mod whose instance the
                // world does not hold is detached and does not start (MVP4 rule R3).
                // WHY a kept world's startup keeps going past a failing mod: the player's world is worth more than
                // one mod, which stays stopped and is reported; a load the player confirmed stays all or nothing.
                bool keepGoing = restoredStartup != null && KeepsWorldBetweenStarts;
                int started = stagedStack.Runtime.RehydrateExactOrThrow(
                    _hostGrant, _allowFull,
                    payload.FormatVersion < RbxWorldPackageSerializer.ScriptBindingsFormatVersion,
                    keepGoing,
                    out IReadOnlyList<string> notStarted);
                if (keepGoing)
                {
                    foreach (string skipped in notStarted)
                    {
                        if (skipped.Contains("(failed: "))
                        {
                            ReportDiagnostic("The kept world opened without mod " + skipped
                                             + "; the rest of the world runs.");
                        }
                    }
                }
                int expected = CountActive(payload.Mods) - notStarted.Count;
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
                    stagedSources,
                    candidate,
                    stagedNetwork);
                LuaCsLogicSlots previousSlots;
                LuaCsLogicSlots nextSlots = stagedStack.GameplayBindings.LogicSlots;
                lock (_gate)
                {
                    DemandActiveLocked();

                    // WHY the session rules are checked again at the last moment: staging, the safety
                    // autosave and the mod restarts span frames, and a client that joined meanwhile
                    // would otherwise have its world replaced under it. Refused here, the load rolls
                    // back through the not-published path below like any other staging failure.
                    string lateNetworkRefusal = DescribeLiveNetworkSessions();
                    if (lateNetworkRefusal.Length > 0)
                    {
                        throw new RbxWorldLoadRefusedException(
                            RbxWorldLoadRefusedException.NetworkSessionsActiveStatus,
                            lateNetworkRefusal);
                    }

                    string lateAclRefusal = DescribeAclDowngrade(payload);
                    if (lateAclRefusal.Length > 0)
                    {
                        throw new RbxWorldLoadRefusedException(
                            RbxWorldLoadRefusedException.IncompatiblePackageStatus,
                            lateAclRefusal);
                    }

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
                    // WHY the pending mark is kept (C2-02): a Hub or host write to the new world's
                    // sources marks it from here on, also while this load still records its selection,
                    // and the refresh after the record picks it up instead of losing it.
                    SetLiveStartupSourceLocked(restoredStartup);
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

                if (notStarted.Count > 0)
                {
                    ReportDiagnostic("The loaded world did not start " + notStarted.Count.ToString(CultureInfo.InvariantCulture)
                                     + " active mod(s): " + string.Join(", ", notStarted) + ".");
                }

                if (startupSource == null)
                {
                    return new RbxWorldLoadResult(true, "", started).WithNotStartedMods(notStarted);
                }

                // WHY still inside the load: the next confirmed load cannot publish until this one's
                // selection is recorded, so the newest startup entry always names the newest live world.
                string selectionError = await RecordStartupSelectionAsync(startupSource);
                return new RbxWorldLoadResult(
                    true,
                    "",
                    started,
                    selectionError.Length == 0,
                    selectionError).WithNotStartedMods(notStarted);
            }
            catch (Exception ex)
            {
                string status = ex is RbxWorldLoadRefusedException refused ? refused.Status : "";
                if (!published)
                {
                    ShutdownStaged(stagedStack, stagedRbxApi, stagedNetwork, candidate);
                    if (folderStore != null && previousFolders != null)
                    {
                        try
                        {
                            folderStore.Save(previousFolders);
                        }
                        catch (Exception folderException)
                        {
                            ReportDiagnostic("Putting the previous mod folders back after a failed load failed: "
                                             + folderException.Message);
                        }
                    }

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
                                0,
                                status);
                        }
                    }
                }

                return new RbxWorldLoadResult(false, ex.Message, 0, status);
            }
            finally
            {
                DisposeSourceReplacement(sourceReplacement);
            }
        }

        private void DisposeSourceReplacement(IRbxWorldModSourceReplacement sourceReplacement)
        {
            if (sourceReplacement == null)
            {
                return;
            }

            try
            {
                sourceReplacement.Dispose();
            }
            catch (Exception ex)
            {
                ReportDiagnostic("Releasing an unpublished world source version failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Advances only the currently published scheduler and runtime. A throw from either one is
        /// reported through the diagnostics sink and does not skip the other. When mod sources of the
        /// live startup world changed outside the shared gate since the last frame, it also starts one
        /// refresh of the startup selection.
        /// </summary>
        public void PumpFrame(ActorContext actorContext, float deltaSeconds)
        {
            Session session;
            lock (_gate)
            {
                // WHY quiet: a host frame driver can outlive the controller by a frame or more, and a
                // throw here was logged by the engine on every frame after shutdown.
                if (_disposed)
                {
                    return;
                }

                session = _current;
            }

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

            EndAbandonedMutationWatch();
            WatchTerrainOf(session);
            StartPendingStartupRefresh();
            AcknowledgeReportedMissingAutoSaves();
        }

        private void AcknowledgeReportedMissingAutoSaves()
        {
            IRbxWorldAutoSaveWriteBehindStore store = Interlocked.Exchange(ref _missingAutoSavesToAcknowledge, null);
            if (store == null)
            {
                return;
            }

            try
            {
                store.AcknowledgeMissingAutoSaves();
            }
            catch (Exception ex)
            {
                ReportDiagnostic("Clearing the record of the missing autosaves failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Session outgoing;
            InstanceRegistry watched;
            RbxTerrain editCounted;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _pendingLoads.Clear();
                outgoing = _current;
                watched = _watchedRegistry;
                _watchedRegistry = null;
                _mutationWatchActive = false;
                editCounted = _editCountedTerrain;
                _editCountedTerrain = null;
            }

            if (editCounted != null)
            {
                editCounted.Store.Changed -= _onEditCountedTerrainChanged;
            }

            Unwatch(watched);
            DetachGateHooks();
            // WHY before the shutdown: a record or a backup still encoding a frame budget at a time would wait for
            // frames that a closed scene, a quitting player or an unloaded page may never run again.
            FinishWritesInFlightAtOnce();
            ShutdownOutgoing(outgoing);
        }

        /// <summary>
        /// Finishes every write of this play world whose encode is spread over frames in one step now: each record of
        /// the world that opens on the next start and the autosave written behind the last gated mutation. The encode
        /// then runs to its end inside this call; the file write that follows still yields a frame per 64 KiB written,
        /// as every package write does. <see cref="Dispose"/> calls it; a host calls it too when it is about to lose
        /// its frames (an application pause or quit). Never throws.
        /// </summary>
        public void FinishWritesInFlightAtOnce()
        {
            RbxWorldPackageEncodeUrgency[] records;
            lock (_gate)
            {
                records = _recordEncodes.ToArray();
            }

            foreach (RbxWorldPackageEncodeUrgency record in records)
            {
                record.Request();
            }

            if (_worldMutationGate is ConfirmedWorldMutationGate gate)
            {
                gate.FinishPendingBackupAtOnce();
            }
        }

        /// <summary>
        /// The request a record's encode listens to, kept until <see cref="EndRecordEncode"/>; already requested once
        /// the play world stopped, so a record that only starts then is finished at once too.
        /// </summary>
        private RbxWorldPackageEncodeUrgency BeginRecordEncode()
        {
            RbxWorldPackageEncodeUrgency urgency = new();
            bool stopped;
            lock (_gate)
            {
                stopped = _disposed;
                if (!stopped)
                {
                    _recordEncodes.Add(urgency);
                }
            }

            if (stopped)
            {
                urgency.Request();
            }

            return urgency;
        }

        private void EndRecordEncode(RbxWorldPackageEncodeUrgency urgency)
        {
            lock (_gate)
            {
                _recordEncodes.Remove(urgency);
            }
        }

        /// <summary>True once <see cref="Dispose"/> ran; a frame pump then has nothing to drive.</summary>
        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        /// <summary>
        /// Clears the hooks this controller set on the shared gate, but only while they still point at
        /// this controller.
        /// </summary>
        /// <remarks>
        /// WHY: the gate outlives a controller that a host disposes and replaces; left set, every later
        /// gated mutation called into the disposed controller, and a successor's hooks must survive.
        /// </remarks>
        private void DetachGateHooks()
        {
            if (!(_worldMutationGate is IStartupAwareWorldMutationGate startupAwareGate))
            {
                return;
            }

            try
            {
                if (_mutationStartingHook != null && startupAwareGate.MutationStarting == _mutationStartingHook)
                {
                    startupAwareGate.MutationStarting = null;
                }

                if (_afterMutationHook != null && startupAwareGate.AfterMutationAsync == _afterMutationHook)
                {
                    startupAwareGate.AfterMutationAsync = null;
                }
            }
            catch (Exception ex)
            {
                ReportDiagnostic("Detaching the world session hooks from the shared gate failed: " + ex.Message);
            }
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

        /// <summary>True when <paramref name="session"/> is no longer the published session.</summary>
        private bool WasReplaced(Session session)
        {
            lock (_gate)
            {
                return !ReferenceEquals(session, _current);
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
                await _startupSelectionGate.WaitAsync(CancellationToken.None);
                RbxWorldPackageSerializer.IncrementalEncoder encoding = null;
                RbxWorldPackageEncodeUrgency urgency = BeginRecordEncode();
                try
                {
                    // WHY the content first and its entries reused: the record then encodes the world once,
                    // a frame budget at a time; a payload whose digest fails is still written, as before.
                    try
                    {
                        encoding = await DigestStartupContentAsync(source.Payload, urgency);
                    }
                    catch (Exception)
                    {
                        encoding = null;
                    }

                    // WHY uncancelled: the player confirmed and the world is already live; abandoning the
                    // bounded selection write half-way would only make the next start disagree with it.
                    RbxWorldPackageWriteResult written = _startupStore is FileRbxWorldPackageStore fileStore
                        ? await fileStore.SelectStartupAsync(
                            source.Payload, source.SourceKind, source.Slot, CancellationToken.None, encoding, urgency)
                        : await _startupStore.SelectStartupAsync(
                            source.Payload,
                            source.SourceKind,
                            source.Slot,
                            CancellationToken.None);
                    if (written != null && written.Success)
                    {
                        SetLiveStartupSource(new StartupSource(
                            source.SourceKind,
                            source.Slot,
                            encoding?.EntryDigest));
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
                finally
                {
                    EndRecordEncode(urgency);
                    encoding?.Dispose();
                    _startupSelectionGate.Release();
                }
            }

            ReportDiagnostic(
                "The confirmed " + source.SourceKind + " '" + source.Slot + "' (world '"
                + (source.Payload.Settings?.WorldId ?? "") + "') is live, but it was not recorded as the "
                + "world that opens on the next start: " + reason
                + " The next start opens the previous startup selection.");
            return reason;
        }

        /// <summary>
        /// After a gated mutation of a live world that is also the startup selection, records the
        /// world as it is now as the new startup selection when the mutation changed what an entry
        /// holds: at once for a change to the mod sources, and for a change to the world tree alone at
        /// most once per <see cref="StartupRefreshInterval"/> (a later one is left to the frame pump).
        /// A call that changed neither writes nothing. Never throws; a failure is reported and the
        /// previous selection stays. Answers the note the mutation's tool result carries: why the
        /// change could not be recorded, or empty.
        /// </summary>
        /// <remarks>
        /// WHY a whole new create-once entry per change: the entry is one self-contained package
        /// (tree and exact mod sources from one capture), so a crash at any point leaves either the
        /// previous state or the new one to open, never a tree from one moment with sources from
        /// another. Before this, the entry stayed the package confirmed at load time, so every later
        /// AI change, including mods whose sources lived only in the session's source version, was
        /// gone after a restart although the Hub promised the world would reopen.
        /// </remarks>
        private UniTask<string> RefreshStartupSelectionAfterMutationAsync(
            string trigger,
            CancellationToken cancellationToken)
        {
            // WHY the load's own trigger is skipped: a confirmed load records its selection itself,
            // and the gate reports the load only after the new world is published.
            if (string.Equals(trigger, PreLoadAutosaveTrigger, StringComparison.Ordinal))
            {
                return UniTask.FromResult("");
            }

            MutationChange change = EndWatchingMutation();
            bool forced;
            TimeSpan debounce;
            lock (_gate)
            {
                forced = _forceStartupRecordAfterMutation;
                _forceStartupRecordAfterMutation = false;
                debounce = _startupRefreshDebounce;
            }

            if (forced)
            {
                // WHY: an undo or redo republished the world in place; its watch saw the outgoing registry,
                // so only this flag says that what the startup entry holds has changed.
                change = MutationChange.Sources;
            }

            if (change == MutationChange.None)
            {
                return UniTask.FromResult(debounce > TimeSpan.Zero ? TakeUndeliveredStartupNote() : "");
            }

            if (debounce > TimeSpan.Zero)
            {
                DebounceStartupRefresh(change == MutationChange.World);
                return UniTask.FromResult(TakeUndeliveredStartupNote());
            }

            if (change == MutationChange.World && DeferWorldRefreshIfTooSoon())
            {
                return UniTask.FromResult("");
            }

            return RefreshStartupSelectionAsync(trigger, change == MutationChange.World);
        }

        /// <summary>Leaves a gated change to the frame pump, which records the burst once it is quiet.</summary>
        private void DebounceStartupRefresh(bool worldOnly)
        {
            lock (_gate)
            {
                if (_disposed || _liveStartupSource == null)
                {
                    return;
                }

                DateTime now = _utcNow();
                if (!_debouncedRefreshPending)
                {
                    _debouncedRefreshPending = true;
                    _debouncedWorldOnly = true;
                    _debounceFirstChangeUtc = now;
                }

                _debounceLastChangeUtc = now;
                if (!worldOnly)
                {
                    _debouncedWorldOnly = false;
                }
            }
        }

        private bool IsDebouncedRefreshDueLocked()
        {
            if (!_debouncedRefreshPending)
            {
                return false;
            }

            DateTime now = _utcNow();
            TimeSpan longest = _startupRefreshInterval > _startupRefreshDebounce
                ? _startupRefreshInterval
                : _startupRefreshDebounce;
            return now - _debounceLastChangeUtc >= _startupRefreshDebounce
                   || now - _debounceFirstChangeUtc >= longest;
        }

        /// <summary>The startup note once per distinct note, for the next gated result after a pump record.</summary>
        private string TakeUndeliveredStartupNote()
        {
            lock (_gate)
            {
                if (string.Equals(_startupSelectionNote, _deliveredStartupNote, StringComparison.Ordinal))
                {
                    return "";
                }

                _deliveredStartupNote = _startupSelectionNote;
                return _startupSelectionNote;
            }
        }

        /// <summary>What a gated mutation changed of what a startup entry holds.</summary>
        private enum MutationChange
        {
            /// <summary>Neither the mod sources nor the world-owned tree.</summary>
            None,

            /// <summary>The world-owned tree (instances, parents, properties, attributes, tags) only.</summary>
            World,

            /// <summary>The mod sources, or unknown because the gate did not report the mutation's start.</summary>
            Sources
        }

        /// <summary>
        /// Starts watching what the gated mutation about to run changes: every write to the published
        /// session's mod sources is counted anyway, and the published registry's instance events tell
        /// whether it changed a world-owned instance.
        /// </summary>
        /// <remarks>
        /// WHY only while a gated mutation runs (C2-03): a startup entry holds the mod sources and the
        /// world-owned tree, and the refresh after a call compared a digest of a whole capture, which
        /// also holds the camera and every part's pose, so in a live world, where both move all the
        /// time, every gated call, a read-only one included, wrote a whole entry. Physics and the
        /// player's camera raise no registry events, and the events of the mods' own instances, which no
        /// entry holds, are ignored.
        /// WHY subscribed only for the call: a registry with a revision subscriber queues every
        /// property write of every script for it, which the rest of the frame need not pay for.
        /// </remarks>
        private void OnGatedMutationStarting(string trigger)
        {
            InstanceRegistry registry = null;
            InstanceRegistry previous;
            lock (_gate)
            {
                previous = _watchedRegistry;
                _watchedRegistry = null;
                _mutationWatchActive = false;
                // WHY nothing is watched for a load or without a startup world: a confirmed load records
                // its selection itself, and without a startup world the refresh records nothing.
                if (!_disposed
                    && _startupStore != null
                    && _liveStartupSource != null
                    && !string.Equals(trigger, PreLoadAutosaveTrigger, StringComparison.Ordinal))
                {
                    registry = _current.RbxApi.Registry;
                    _watchedRegistry = registry;
                    _mutationWatchActive = true;
                    _sourceChangesAtMutationStart = _sourceChanges;
                    Volatile.Write(ref _worldChangedDuringMutation, 0);
                }
            }

            Unwatch(previous);
            if (registry == null)
            {
                return;
            }

            // WHY a value fingerprint as well as the events: a PointLight, GUI or StarterPlayer setter raises no
            // registry event, so an AI edit of only those would never reach the next start.
            _valuesAtMutationStart = RbxWorldStateMark.Of(registry, 0L).Values;
            registry.Registered += OnWatchedInstanceRegisteredOrRemoved;
            registry.Unregistered += OnWatchedInstanceRegisteredOrRemoved;
            registry.RevisionAdvanced += OnWatchedRevisionAdvanced;
        }

        /// <summary>Stops the watch <see cref="OnGatedMutationStarting"/> began and says what the mutation changed.</summary>
        private MutationChange EndWatchingMutation()
        {
            InstanceRegistry registry;
            bool watched;
            bool sourcesChanged;
            lock (_gate)
            {
                registry = _watchedRegistry;
                watched = _mutationWatchActive;
                sourcesChanged = _sourceChanges != _sourceChangesAtMutationStart;
                _watchedRegistry = null;
                _mutationWatchActive = false;
            }

            Unwatch(registry);
            if (!watched || sourcesChanged)
            {
                return MutationChange.Sources;
            }

            return Volatile.Read(ref _worldChangedDuringMutation) != 0
                   || (registry != null && RbxWorldStateMark.Of(registry, 0L).Values != _valuesAtMutationStart)
                ? MutationChange.World
                : MutationChange.None;
        }

        /// <summary>
        /// Ends the watch of a gated mutation that threw (the gate reports no end then) once the gate is
        /// free, and leaves a world change it saw to the pump; its source writes are pending already.
        /// </summary>
        private void EndAbandonedMutationWatch()
        {
            if (!(_worldMutationGate is IStartupAwareWorldMutationGate gate))
            {
                return;
            }

            InstanceRegistry registry;
            lock (_gate)
            {
                // WHY the held check under the lock: a mutation that begins after it can only start its
                // own watch once this lock is released, and one that began before it holds the gate.
                if (!_mutationWatchActive || gate.IsHeld)
                {
                    return;
                }

                registry = _watchedRegistry;
                _watchedRegistry = null;
                _mutationWatchActive = false;
                if (Volatile.Read(ref _worldChangedDuringMutation) != 0)
                {
                    _worldRefreshDeferred = true;
                }
            }

            Unwatch(registry);
        }

        private void Unwatch(InstanceRegistry registry)
        {
            if (registry == null)
            {
                return;
            }

            registry.Registered -= OnWatchedInstanceRegisteredOrRemoved;
            registry.Unregistered -= OnWatchedInstanceRegisteredOrRemoved;
            registry.RevisionAdvanced -= OnWatchedRevisionAdvanced;
        }

        private void OnWatchedInstanceRegisteredOrRemoved(InstanceRecord record)
        {
            if (record == null || IsWorldOwned(record))
            {
                Volatile.Write(ref _worldChangedDuringMutation, 1);
            }
        }

        private void OnWatchedRevisionAdvanced(InstanceId id, long revision, string member)
        {
            InstanceRegistry registry = Volatile.Read(ref _watchedRegistry);
            if (registry == null
                || !registry.TryGetRecord(id, out InstanceRecord record)
                || IsWorldOwned(record))
            {
                Volatile.Write(ref _worldChangedDuringMutation, 1);
            }
        }

        /// <summary>
        /// True for an instance a startup entry holds: neither a mod's own nor runtime infrastructure, or the
        /// Terrain, whose node is rebuilt by its bootstrap but whose cells travel in their own entry.
        /// </summary>
        private static bool IsWorldOwned(InstanceRecord record)
        {
            return string.IsNullOrEmpty(record.OwnerModId)
                   && (!record.IsRuntimeInfrastructure || record.Instance is RbxTerrain);
        }

        /// <summary>
        /// Leaves a world-only change to the frame pump when the last world-only record was less than
        /// <see cref="StartupRefreshInterval"/> ago; true when it did.
        /// </summary>
        private bool DeferWorldRefreshIfTooSoon()
        {
            lock (_gate)
            {
                if (IsWorldRefreshDueLocked())
                {
                    return false;
                }

                _worldRefreshDeferred = true;
                return true;
            }
        }

        private bool IsWorldRefreshDueLocked()
        {
            return !_lastWorldRefreshUtc.HasValue
                   || _utcNow() - _lastWorldRefreshUtc.Value >= _startupRefreshInterval;
        }

        /// <summary>
        /// Records the live startup world as it is now, unless the newest entry already holds exactly
        /// that world or the startup restore would refuse it. Serialized on the startup gate; never
        /// throws. Answers the note for a caller's tool result: why the change was not recorded, because
        /// the restore refuses the world (once per refused state) or the record was not durably written,
        /// or empty; <see cref="StartupSelectionNote"/> keeps it for a change made outside the gate.
        /// </summary>
        /// <param name="worldOnly">
        /// True when only the world tree changed; writing its entry starts a new <see cref="StartupRefreshInterval"/>.
        /// </param>
        private async UniTask<string> RefreshStartupSelectionAsync(string trigger, bool worldOnly, bool keepStats = false)
        {
            if (_startupStore == null || ReadLiveStartupSource() == null)
            {
                return "";
            }

            await _startupSelectionGate.WaitAsync(CancellationToken.None);
            RbxWorldPackageEncodeUrgency urgency = BeginRecordEncode();
            try
            {
                StartupSource source = TakeLiveStartupSourceForRefresh();
                if (source == null)
                {
                    return "";
                }

                string reason;
                try
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    RbxWorldPackagePayload captured = CaptureCurrent();
                    double captureMs = watch.Elapsed.TotalMilliseconds;
                    // WHY the digest a frame budget at a time, and its entries kept for the write: a record of a
                    // whole world is tens to hundreds of milliseconds of encoding (about 135 ms at a Block World's
                    // radius 8 on CoreCLR), which in one block was a hitch after every burst of building; one encode
                    // now serves the comparison and the package.
                    using RbxWorldPackageSerializer.IncrementalEncoder encoding =
                        await DigestStartupContentAsync(captured, urgency);
                    byte[] content = encoding.EntryDigest;

                    // WHY still compared: a call that changed the sources or the world-owned tree may
                    // have left them as the newest entry holds them (a folder created and destroyed
                    // again), and each recorded entry is a whole package written, synced and pruned
                    // while the gate is held.
                    if (source.Holds(content))
                    {
                        if (keepStats || _keepWorld)
                        {
                            NoteKeptRecord(captured, captureMs, -1d);
                        }

                        SetStartupSelectionNote("");
                        return "";
                    }

                    // WHY the restore's own checks: an entry the startup restore refuses makes every
                    // later start fall back to the default world without a word, which is worse than
                    // keeping the previous entry and saying so now.
                    string refusal = DescribeUnloadablePackage(captured);
                    if (refusal.Length > 0)
                    {
                        return ReportStartupRefusal(content, refusal);
                    }

                    if (worldOnly)
                    {
                        StartWorldRefreshInterval();
                    }

                    RbxWorldPackageWriteResult written = _startupStore is FileRbxWorldPackageStore fileStore
                        ? await fileStore.SelectStartupAsync(
                            captured, source.Kind, source.Name, CancellationToken.None, encoding, urgency)
                        : await _startupStore.SelectStartupAsync(
                            captured,
                            source.Kind,
                            source.Name,
                            CancellationToken.None);
                    if (written != null && written.Success)
                    {
                        ReplaceLiveStartupSource(source, new StartupSource(source.Kind, source.Name, content));
                        if (keepStats || _keepWorld)
                        {
                            NoteKeptRecord(captured, captureMs, watch.Elapsed.TotalMilliseconds);
                        }

                        SetStartupSelectionNote("");
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

                ReportDiagnostic(
                    "The live world changed ('" + trigger + "'), but the change was not recorded for the "
                    + "next start: " + reason + " The next start opens the world as it was before it.");
                // WHY a note as well (C2-06): the caller whose change will not reopen is the one reader
                // who can act on it, as with a refusal.
                string note = "Startup world not updated: " + reason.TrimEnd('.')
                              + ". This change will not reopen after a restart; the next start opens the "
                              + "world as it was before it.";
                SetStartupSelectionNote(note);
                return note;
            }
            finally
            {
                EndRecordEncode(urgency);
                _startupSelectionGate.Release();
            }
        }

        /// <summary>Keeps what a record of a kept world held and cost, for <see cref="LastKeptWorldRecord"/>.</summary>
        private void NoteKeptRecord(RbxWorldPackagePayload captured, double captureMs, double totalMs)
        {
            string line = (captured.Mods?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " mods, "
                          + (captured.Terrain?.ChunkCount ?? 0).ToString(CultureInfo.InvariantCulture) + " Terrain chunks and "
                          + (captured.Tree?.Instances?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " instances ("
                          + captureMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms to capture, "
                          + (totalMs < 0d
                              ? "already on disk"
                              : totalMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms with the write")
                          + ")";
            lock (_gate)
            {
                _lastKeptRecord = line;
            }
        }

        private void SetStartupSelectionNote(string note)
        {
            lock (_gate)
            {
                _startupSelectionNote = note ?? "";
            }
        }

        /// <summary>
        /// Reports once per refused world state that the startup selection keeps its previous entry
        /// because the startup restore would refuse the live world, and logs each distinct refusal once.
        /// </summary>
        private string ReportStartupRefusal(byte[] content, string refusal)
        {
            if (_lastRefusedStartupContent != null && SameContent(_lastRefusedStartupContent, content))
            {
                return "";
            }

            _lastRefusedStartupContent = content;
            string note = "Startup world not updated: " + refusal.TrimEnd('.')
                          + ". The startup restore refuses such a world, so the previous startup world is "
                          + "kept and this change will not reopen after a restart.";
            if (!string.Equals(_lastReportedStartupRefusal, refusal, StringComparison.Ordinal))
            {
                _lastReportedStartupRefusal = refusal;
                ReportDiagnostic(note);
            }

            SetStartupSelectionNote(note);
            return note;
        }

        /// <summary>
        /// Starts one refresh of the startup selection when mod sources of the live startup world
        /// changed outside the shared gate, or a gated world change was left for later
        /// (<see cref="StartupRefreshInterval"/>) and is now due, none is running, and no gated mutation
        /// or world load holds the gate (a gated mutation refreshes the selection itself before it lets
        /// go).
        /// </summary>
        /// <remarks>
        /// WHY from the frame pump and not from the change itself: one Hub action writes the sources
        /// several times (the runtime persists the load, then the Hub its own manifest; an unload and
        /// then the dormant flag), and a refresh started by the first write would record a state the
        /// action had not finished. The pump records the finished action once, and a frame is the
        /// longest a change waits.
        /// </remarks>
        private void StartPendingStartupRefresh()
        {
            bool worldOnly;
            lock (_gate)
            {
                bool debouncedDue = IsDebouncedRefreshDueLocked();
                if (_startupRefreshRunning
                    || _disposed
                    || _liveStartupSource == null
                    || (!_startupRefreshPending
                        && !(_worldRefreshDeferred && IsWorldRefreshDueLocked())
                        && !debouncedDue)
                    || (_worldMutationGate is IStartupAwareWorldMutationGate gate && gate.IsHeld))
                {
                    return;
                }

                worldOnly = !_startupRefreshPending && !(debouncedDue && !_debouncedWorldOnly);
                _startupRefreshRunning = true;
            }

            Task refresh = RefreshAfterSourceChangesAsync(worldOnly).AsTask();
            lock (_gate)
            {
                _startupRefreshAfterSourceChanges = refresh;
            }
        }

        private async UniTask RefreshAfterSourceChangesAsync(bool worldOnly)
        {
            try
            {
                await RefreshStartupSelectionAsync(worldOnly ? WorldChangeTrigger : SourceChangeTrigger, worldOnly);
            }
            catch (Exception ex)
            {
                ReportDiagnostic("Recording a mod source change for the next start failed: " + ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _startupRefreshRunning = false;
                }
            }
        }

        /// <summary>
        /// Called by a session's source store after every write: marks the startup world for a refresh
        /// on the next frame when the write reached the published session's sources, and counts it for
        /// the gated mutation that may be running.
        /// </summary>
        /// <remarks>
        /// WHY whether or not the world is the startup world yet (C2-02): a confirmed load publishes its
        /// world before it records it, and a Hub or host change made in between marked nothing and was
        /// gone at restart. The pump starts a refresh only once there is a startup world to record.
        /// </remarks>
        private void OnSessionSourcesChanged(ILuaModSourceStore sessionSources)
        {
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_current.SourceStore, sessionSources))
                {
                    _startupRefreshPending = true;
                    _sourceChanges++;
                }
            }
        }

        private StartupSource ReadLiveStartupSource()
        {
            lock (_gate)
            {
                return _disposed ? null : _liveStartupSource;
            }
        }

        /// <summary>
        /// The live startup source for a refresh that is about to capture the world, clearing the
        /// pending source-change mark and the deferred world change that capture now covers.
        /// </summary>
        private StartupSource TakeLiveStartupSourceForRefresh()
        {
            lock (_gate)
            {
                _startupRefreshPending = false;
                _worldRefreshDeferred = false;
                _debouncedRefreshPending = false;
                return _disposed ? null : _liveStartupSource;
            }
        }

        /// <summary>Starts a new <see cref="StartupRefreshInterval"/>: an entry for a world-only change is being written now.</summary>
        private void StartWorldRefreshInterval()
        {
            lock (_gate)
            {
                _lastWorldRefreshUtc = _utcNow();
            }
        }

        private void SetLiveStartupSource(StartupSource source)
        {
            lock (_gate)
            {
                SetLiveStartupSourceLocked(source);
            }
        }

        private void SetLiveStartupSourceLocked(StartupSource source)
        {
            _liveStartupSource = source;
            _lastRefusedStartupContent = null;
            _lastReportedStartupRefusal = "";
            _startupSelectionNote = "";
        }

        /// <summary>
        /// Replaces the live startup source after a refresh recorded a new entry, unless a load, a
        /// restore or a clear replaced the source meanwhile.
        /// </summary>
        private void ReplaceLiveStartupSource(StartupSource refreshed, StartupSource recorded)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_liveStartupSource, refreshed))
                {
                    SetLiveStartupSourceLocked(recorded);
                }
            }
        }

        /// <summary>
        /// A digest of everything a startup entry of <paramref name="payload"/> restores: the entries of its
        /// package with the capture time left out, which differs on every capture of the same world. The
        /// digest is only ever compared with another one of this process.
        /// </summary>
        /// <remarks>
        /// WHY the entries and not the encoded package: the same entries always deflate to the same
        /// package, and skipping the deflate removed about half the cost of every startup record check.
        /// </remarks>
        private static byte[] ComputeStartupContent(RbxWorldPackagePayload payload)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WHY: the digest encodes the entries in one frame, so it keeps to the same bounded work
            // the package store's own WebGL encoder accepts, and a larger world is refused before it.
            FileRbxWorldPackageStore.ValidateWebGlWorkBudget(payload);
#endif
            return RbxWorldPackageSerializer.ComputeEntryDigest(payload.WithCapturedAtUtc(DateTime.MinValue));
        }

        /// <summary>
        /// <see cref="ComputeStartupContent"/> a frame budget at a time (the file store's budget and frame wait, or the
        /// defaults for another store), as an encode whose <c>EntryDigest</c> is the content and which can go on to the
        /// package. A refusal throws what <see cref="ComputeStartupContent"/> throws.
        /// </summary>
        /// <param name="urgency">The record's request to finish at once (<see cref="FinishWritesInFlightAtOnce"/>).</param>
        private async UniTask<RbxWorldPackageSerializer.IncrementalEncoder> DigestStartupContentAsync(
            RbxWorldPackagePayload payload,
            RbxWorldPackageEncodeUrgency urgency)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            FileRbxWorldPackageStore.ValidateWebGlWorkBudget(payload);
#endif
            RbxWorldPackageSerializer.IncrementalEncoder encoder =
                RbxWorldPackageSerializer.IncrementalEncoder.ForEntryDigest(payload, DateTime.MinValue);
            FileRbxWorldPackageStore fileStore = _startupStore as FileRbxWorldPackageStore;
            await RbxWorldPackageEncodeFrames.RunAsync(
                encoder,
                fileStore?.EncodeFrameBudget ?? RbxWorldPackageEncodeFrames.DefaultFrameBudget,
                fileStore != null
                    ? fileStore.WaitForNextEncodeFrameAsync
                    : (Func<CancellationToken, UniTask>)RbxWorldPackageEncodeFrames.WaitForNextFrameAsync,
                CancellationToken.None,
                urgency: urgency,
                clock: fileStore?.EncodeClock);
            return encoder;
        }

        /// <summary>
        /// <see cref="ComputeStartupContent"/> of a package that was just recorded or restored, or
        /// null when it cannot be encoded, in which case the next change is recorded unconditionally.
        /// </summary>
        private static byte[] ComputeStartupContentOrNull(RbxWorldPackagePayload payload)
        {
            try
            {
                return payload == null ? null : ComputeStartupContent(payload);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SameContent(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
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
                       + "sessions cannot be handed to a new world because session handoff is unavailable; "
                       + "disconnect clients or stop the server first.";
            }

            if (topology == RbxNetworkTopology.Solo || actorCount == 0)
            {
                return "";
            }

            return "The live world has " + actorCount.ToString(CultureInfo.InvariantCulture)
                   + " connected network actor(s) on its " + topology + " bridge. Live network sessions "
                   + "cannot be handed to a new world because session handoff is unavailable, so the world "
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

        /// <summary>
        /// Why a confirmed load, and so the boot-time startup restore, refuses
        /// <paramref name="payload"/> in this session: a legacy ACL downgrade or an active
        /// Full-capability mod. Empty when it would open.
        /// </summary>
        private string DescribeUnloadablePackage(RbxWorldPackagePayload payload)
        {
            string refusal = DescribeAclDowngrade(payload);
            if (refusal.Length > 0)
            {
                return refusal;
            }

            string activeFullMod = FindActiveFullCapabilityMod(payload?.Mods);
            return activeFullMod.Length > 0 ? DescribeActiveFullCapabilityMod(activeFullMod) : "";
        }

        /// <summary>
        /// Refuses, before the player is asked, a package the confirmed load would refuse: a legacy ACL
        /// downgrade or an active Full-capability mod.
        /// </summary>
        private void DemandLoadablePackage(RbxWorldPackagePayload payload)
        {
            string refusal = DescribeUnloadablePackage(payload);
            if (refusal.Length > 0)
            {
                throw new RbxWorldLoadRefusedException(
                    RbxWorldLoadRefusedException.IncompatiblePackageStatus,
                    refusal);
            }
        }

        private void DemandTransactionalSourceStore()
        {
            if (_transactionalSourceStore == null)
            {
                throw new RbxWorldLoadRefusedException(
                    RbxWorldLoadRefusedException.IncompatiblePackageStatus,
                    NonTransactionalSourceStoreRefusal);
            }
        }

        private static string DescribeActiveFullCapabilityMod(string modId)
        {
            return "Active Full-capability mod '" + modId
                   + "' cannot be isolated during staged world restore.";
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

        /// <summary>
        /// The source store of a session this controller staged: forwards every call to the source
        /// version the load prepared and reports each write back to the controller.
        /// </summary>
        /// <remarks>
        /// WHY here and not on the runtime's load events: every change to a world's mod set ends in a
        /// write to its source store, whoever made it: a gated tool, the Hub Mods page (whose delete,
        /// enable and disable also write the store directly), or host code calling the runtime
        /// facade. Before this, only a change made through the shared gate reached the startup
        /// selection, and a player's own Hub edits on the startup world were lost at restart.
        /// </remarks>
        private sealed class SessionSourceStore : ILuaModSourceStore, ILuaModSourceAdmission,
            ILuaModSourceStrictWriter, ILuaModSourceStrictRemover, ILuaModSourceStrictCapabilities
        {
            private readonly RbxWorldRuntimeSessionController _owner;
            private readonly ILuaModSourceStore _inner;

            public SessionSourceStore(RbxWorldRuntimeSessionController owner, ILuaModSourceStore inner)
            {
                _owner = owner;
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                try
                {
                    _inner.Save(id, source, manifest);
                }
                finally
                {
                    _owner.OnSessionSourcesChanged(this);
                }
            }

            public void SaveStrict(string id, string source, LuaModManifest manifest)
            {
                if (!(_inner is ILuaModSourceStrictWriter strictWriter) ||
                    !LuaModSourceStoreCapabilities.CanSaveStrict(_inner))
                {
                    throw new NotSupportedException(
                        "The current world source store has no strict save acknowledgment.");
                }

                try
                {
                    strictWriter.SaveStrict(id, source, manifest);
                }
                finally
                {
                    _owner.OnSessionSourcesChanged(this);
                }
            }

            public bool SupportsStrictSave =>
                LuaModSourceStoreCapabilities.CanSaveStrict(_inner);

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                return _inner.TryLoad(id, out source, out manifest);
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return _inner.List();
            }

            public void SetActive(string id, bool active)
            {
                try
                {
                    _inner.SetActive(id, active);
                }
                finally
                {
                    _owner.OnSessionSourcesChanged(this);
                }
            }

            public void Delete(string id)
            {
                try
                {
                    _inner.Delete(id);
                }
                finally
                {
                    _owner.OnSessionSourcesChanged(this);
                }
            }

            public void DeleteStrict(string id)
            {
                if (!(_inner is ILuaModSourceStrictRemover strictRemover) ||
                    !LuaModSourceStoreCapabilities.CanDeleteStrict(_inner))
                {
                    throw new NotSupportedException(
                        "The current world source store has no strict removal acknowledgment.");
                }

                try
                {
                    strictRemover.DeleteStrict(id);
                }
                finally
                {
                    _owner.OnSessionSourcesChanged(this);
                }
            }

            public bool SupportsStrictDelete =>
                LuaModSourceStoreCapabilities.CanDeleteStrict(_inner);

            public bool CanAdmit(string modId, out string refusal)
            {
                return CanAdmitSource(_inner, modId, out refusal);
            }
        }

        /// <summary>
        /// Whether <paramref name="sources"/> would keep a source for <paramref name="modId"/> without
        /// passing the world-package mod limit: the store's own rule when it has one, otherwise a mod it
        /// already holds or a listing below the limit. Throws when the store cannot answer.
        /// </summary>
        private static bool CanAdmitSource(ILuaModSourceStore sources, string modId, out string refusal)
        {
            if (sources is ILuaModSourceAdmission admission)
            {
                return admission.CanAdmit(modId, out refusal);
            }

            refusal = "";
            if (sources.TryLoad(modId, out _, out _))
            {
                return true;
            }

            int stored = sources.List()?.Count ?? 0;
            if (stored < RbxWorldPackageSerializer.MaximumMods)
            {
                return true;
            }

            refusal = RbxWorldPackageFormatLimitException.DescribeModSourceLimit(modId, stored);
            return false;
        }

        /// <summary>
        /// Where the live world's startup selection came from, reused for each refresh of it, and the
        /// <see cref="ComputeStartupContent"/> digest of what its newest entry holds (null when unknown).
        /// </summary>
        private sealed class StartupSource
        {
            private readonly byte[] _recordedContent;

            public StartupSource(string kind, string name, byte[] recordedContent)
            {
                Kind = kind ?? "";
                Name = name ?? "";
                _recordedContent = recordedContent;
            }

            public string Kind { get; }

            public string Name { get; }

            /// <summary>True when the newest entry is known to hold exactly <paramref name="content"/>.</summary>
            public bool Holds(byte[] content)
            {
                return SameContent(_recordedContent, content);
            }
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

            /// <summary>
            /// Delegated with the offset: left to the interface default (false), a client world loaded
            /// from a package slewed on through every hold of the server's clock (B1-01, A4-08).
            /// </summary>
            public bool IsServerClockHeld => _inner.IsServerClockHeld;

            /// <inheritdoc />
            /// <remarks>
            /// Queued like a registration: a world that is not live yet must not displace the clock
            /// the live world handed the transport, and one whose load fails never reaches it. WHY
            /// implemented here and not left to the interface default: a world loaded from a package
            /// keeps this bridge for its whole life, and the default dropped its clock, so the
            /// transport sent clients its raw wall clock with no hold (B1-01).
            /// </remarks>
            public void AttachServerClock(RbxServerClockReader serverClock)
            {
                QueueOrRun(() => _inner.AttachServerClock(serverClock));
            }

            /// <inheritdoc />
            /// <remarks>
            /// Forwarded at once, even after <see cref="Dispose"/>: the transport takes back only the
            /// clock it is handed, so a staged world that never went live clears nothing, and a world
            /// disposed after this bridge still takes its own clock back.
            /// </remarks>
            public void DetachServerClock(RbxServerClockReader serverClock)
            {
                _inner.DetachServerClock(serverClock);
            }

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

        private sealed class ActiveLuaModSourceStore : ILuaModSourceStore,
            ILuaModSourceStrictWriter, ILuaModSourceStrictRemover, ILuaModSourceStrictCapabilities
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

            public void SaveStrict(string id, string source, LuaModManifest manifest)
            {
                ILuaModSourceStore current = Inner;
                if (!(current is ILuaModSourceStrictWriter strictWriter) ||
                    !LuaModSourceStoreCapabilities.CanSaveStrict(current))
                {
                    throw new NotSupportedException(
                        "The current world source store has no strict save acknowledgment.");
                }

                strictWriter.SaveStrict(id, source, manifest);
            }

            public bool SupportsStrictSave =>
                LuaModSourceStoreCapabilities.CanSaveStrict(Inner);

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

            public void DeleteStrict(string id)
            {
                ILuaModSourceStore current = Inner;
                if (!(current is ILuaModSourceStrictRemover strictRemover) ||
                    !LuaModSourceStoreCapabilities.CanDeleteStrict(current))
                {
                    throw new NotSupportedException(
                        "The current world source store has no strict removal acknowledgment.");
                }

                strictRemover.DeleteStrict(id);
            }

            public bool SupportsStrictDelete =>
                LuaModSourceStoreCapabilities.CanDeleteStrict(Inner);
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
                Session session = _owner.Current;
                return ReportReplacedWorldAsync(
                    session,
                    session.Stack.ToolExecutor.ExecuteAsync(code, cancellationToken));
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(
                string code,
                ActorContext actorContext,
                MutationEnvelope mutationEnvelope,
                CancellationToken cancellationToken)
            {
                Session session = _owner.Current;
                return ReportReplacedWorldAsync(
                    session,
                    session.Stack.ToolExecutor.ExecuteAsync(
                        code, actorContext, mutationEnvelope, cancellationToken));
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(
                string code,
                ActorContext actorContext,
                CancellationToken cancellationToken)
            {
                Session session = _owner.Current;
                return ReportReplacedWorldAsync(
                    session,
                    session.Stack.ToolExecutor.ExecuteAsync(code, actorContext, cancellationToken));
            }

            /// <summary>
            /// Explains the failure of a chunk whose world was replaced by a confirmed load while it
            /// waited or ran: a failure caused by the replaced world is replaced by the explanation, and
            /// any other failure keeps its own error with the explanation after it.
            /// </summary>
            /// <remarks>
            /// WHY replaced: such a chunk runs against the outgoing world, so its first write fails with
            /// WORLD_DETACHED or INSTANCE_DESTROYED, whose text tells the reader that the scene host was
            /// destroyed and to reload the mods; neither is true after a world load, and the mods of the
            /// new world are already running.
            /// WHY only those: the chunk's own error (a syntax or runtime error) is what the caller has
            /// to fix, and replacing it made the model retry the same broken chunk before it saw why.
            /// </remarks>
            private async Task<LuaTool.LuaResult> ReportReplacedWorldAsync(
                Session served,
                Task<LuaTool.LuaResult> execution)
            {
                LuaTool.LuaResult result = await execution;
                if (result == null || result.Success || !_owner.WasReplaced(served))
                {
                    return result;
                }

                string explanation = "execute_lua ran against the previous world: a confirmed world load "
                                     + "replaced the live world while this call was waiting or running, so "
                                     + "none of its changes reached the live world. The mods of the loaded "
                                     + "world are already running and nothing needs to be reloaded; inspect "
                                     + "the loaded world and retry the change there.";
                return new LuaTool.LuaResult
                {
                    Success = false,
                    Output = result.Output,
                    Printed = result.Printed,
                    Error = IsReplacedWorldFailure(result.Error)
                        ? explanation
                        : (result.Error ?? "").TrimEnd() + "\n" + explanation
                };
            }

            /// <summary>True for the failures a write to a world that was replaced raises.</summary>
            private static bool IsReplacedWorldFailure(string error)
            {
                return !string.IsNullOrEmpty(error)
                       && (error.IndexOf(
                               RbxError.ToWireName(RbxErrorCode.WorldDetached), StringComparison.Ordinal) >= 0
                           || error.IndexOf(
                               RbxError.ToWireName(RbxErrorCode.InstanceDestroyed), StringComparison.Ordinal) >= 0);
            }
        }

        private sealed class ActiveLuaModRuntime : ILuaModRuntime, IBudgetedLuaModRuntime,
            ILuaModScriptInstanceReleaser
        {
            private readonly RbxWorldRuntimeSessionController _owner;
            private readonly AsyncLocal<InvocationPolicy> _invocationPolicy = new();
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

            public IDisposable BeginInvocation(LuaModInvocationLimits limits, CancellationToken cancellationToken = default)
            {
                if (limits == null) throw new ArgumentNullException(nameof(limits));
                cancellationToken.ThrowIfCancellationRequested();
                _owner.DemandActive();
                InvocationPolicy policy = new(limits, cancellationToken, _invocationPolicy.Value);
                _invocationPolicy.Value = policy;
                return new InvocationScope(this, policy);
            }

            private IDisposable EnterInvocation(LuaCsModRuntime runtime)
            {
                // WHY: The live world can change after the editor opens its scope. Apply its immutable
                // policy to the concrete VM captured for this dispatch, rather than the outgoing VM.
                InvocationPolicy policy = _invocationPolicy.Value;
                return policy == null ? null : runtime.BeginInvocation(policy.Limits, policy.Token);
            }

            private sealed class InvocationPolicy
            {
                public InvocationPolicy(LuaModInvocationLimits limits, CancellationToken token, InvocationPolicy previous)
                {
                    Limits = limits;
                    Token = token;
                    Previous = previous;
                }

                public LuaModInvocationLimits Limits { get; }
                public CancellationToken Token { get; }
                public InvocationPolicy Previous { get; }
            }

            private sealed class InvocationScope : IDisposable
            {
                private readonly ActiveLuaModRuntime _owner;
                private readonly InvocationPolicy _policy;
                private bool _disposed;

                public InvocationScope(ActiveLuaModRuntime owner, InvocationPolicy policy)
                {
                    _owner = owner;
                    _policy = policy;
                }

                public void Dispose()
                {
                    if (_disposed) return;
                    if (!ReferenceEquals(_owner._invocationPolicy.Value, _policy))
                        throw new InvalidOperationException("Invocation scopes must be disposed in reverse order.");
                    _owner._invocationPolicy.Value = _policy.Previous;
                    _disposed = true;
                }
            }

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
                bool addsSource = persistToStore && DemandSourceCapacity(modId);
                AttributionSnapshot snapshot = Capture(modId);
                PrepareNewOwner(caller, modId);
                Session session = _owner.Current;
                LuaCsModRuntime runtime = session.Stack.Runtime;
                try
                {
                    using IDisposable invocation = EnterInvocation(runtime);
                    runtime.LoadMod(caller, id, luaCode, capabilities, persistToStore,
                        SourceKeptCheck(runtime, session.SourceStore, addsSource));
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
                LuaCsModRuntime runtime = Inner;
                using IDisposable invocation = EnterInvocation(runtime);
                runtime.ReloadMod(caller, id, luaCode);
            }

            public ModReloadReport ReloadMod(
                ActorContext caller,
                string id,
                string luaCode,
                ModReloadMode mode)
            {
                string modId = Normalize(id);
                PrepareExistingOwner(caller, modId);
                LuaCsModRuntime runtime = Inner;
                using IDisposable invocation = EnterInvocation(runtime);
                return runtime.ReloadMod(caller, id, luaCode, mode);
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
                bool addsSource = DemandSourceCapacity(modId);
                AttributionSnapshot snapshot = Capture(modId);
                PrepareNewOwner(caller, modId);
                Session session = _owner.Current;
                LuaCsModRuntime runtime = session.Stack.Runtime;
                bool imported;
                try
                {
                    using IDisposable invocation = EnterInvocation(runtime);
                    imported = runtime.ImportMod(caller, bundleJson, hostGrant, allowFull,
                        SourceKeptCheck(runtime, session.SourceStore, addsSource));
                    if (!imported)
                    {
                        Restore(modId, snapshot);
                    }
                }
                catch
                {
                    Restore(modId, snapshot);
                    throw;
                }

                return imported;
            }

            public bool ReleaseScriptInstance(ActorContext caller, string id)
            {
                return Inner is ILuaModScriptInstanceReleaser releaser && releaser.ReleaseScriptInstance(caller, id);
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
                LuaCsModRuntime runtime = Inner;
                using IDisposable invocation = EnterInvocation(runtime);
                return runtime.TryRevertMod(caller, id, revisionIndex, out restoredSource);
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

            /// <summary>
            /// Refuses a mod that would become the live world's source beyond the world-package limit,
            /// before anything runs. A mod whose source is already stored is never refused. True when
            /// the load adds a source the store does not hold yet.
            /// </summary>
            /// <remarks>
            /// WHY here as well as in the file source store: the store can only refuse the source after
            /// the mod ran, and a mod that loaded without its source would run now and be missing from
            /// every save; here the caller is told why and what to forget. Both sides ask the same rule
            /// (<see cref="ILuaModSourceAdmission"/>), so they cannot disagree about the count.
            /// WHY a store that cannot answer refuses the load: its save of the new source fails the
            /// same way, so the mod would run without it.
            /// </remarks>
            private bool DemandSourceCapacity(string modId)
            {
                ILuaModSourceStore sources = _owner.Current.SourceStore;
                // WHY the null store is exempt: a session composed without persistence keeps no source
                // by design, so there is neither a limit to pass nor a source to verify.
                if (modId.Length == 0 || sources is NullLuaModSourceStore)
                {
                    return false;
                }

                bool stored;
                string refusal;
                try
                {
                    stored = sources.TryLoad(modId, out _, out _);
                    if (stored || CanAdmitSource(sources, modId, out refusal))
                    {
                        return !stored;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Mod '" + modId + "' cannot be added right now: the mod source store could not "
                        + "confirm there is room for its source (" + ex.Message + "). Retry.",
                        ex);
                }

                throw new RbxWorldPackageFormatLimitException(refusal);
            }

            /// <summary>
            /// The check a load or import that adds a new source hands the runtime
            /// (<see cref="LuaCsModRuntime.LoadMod(ActorContext, string, string, LuaCapabilities, bool, Action{string})"/>);
            /// null when nothing needs checking: the load adds no source, or the runtime persists none.
            /// </summary>
            private static Action<string> SourceKeptCheck(
                LuaCsModRuntime runtime,
                ILuaModSourceStore sources,
                bool addsSource)
            {
                if (!addsSource || !runtime.PersistsModSources)
                {
                    return null;
                }

                return modId => DemandSourceKept(sources, modId);
            }

            /// <summary>
            /// Throws why, when <paramref name="sources"/> did not keep the source of a mod whose load
            /// just persisted it. Runs inside the load's commit, so the throw undoes the load through the
            /// runtime's failed-build rollback.
            /// </summary>
            /// <remarks>
            /// WHY: the runtime persists best-effort and only logs a refused save, so the mod ran and
            /// was missing from every save and from the next start, and the caller heard nothing.
            /// WHY inside the commit (C2-04): undone afterwards by an ordinary unload, a load that
            /// displaced another live mod's logic-slot formula left that formula removed, kept its own
            /// revision, and raised a load and an unload for a mod that never loaded.
            /// </remarks>
            private static void DemandSourceKept(ILuaModSourceStore sources, string modId)
            {
                string limitRefusal = "";
                string failure;
                try
                {
                    if (sources.TryLoad(modId, out _, out _))
                    {
                        return;
                    }

                    failure = CanAdmitSource(sources, modId, out limitRefusal)
                        ? "the mod source store did not keep its source (the store's log names the error)"
                        : limitRefusal;
                }
                catch (Exception ex)
                {
                    failure = "the mod source store could not confirm it kept the source (" + ex.Message + ")";
                }

                if (limitRefusal.Length > 0)
                {
                    throw new RbxWorldPackageFormatLimitException(limitRefusal);
                }

                throw new InvalidOperationException(
                    "Mod '" + modId + "' was not loaded: " + failure + ". A mod whose source is not stored "
                    + "would be missing from every save and from the next start, so the load was undone.");
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
