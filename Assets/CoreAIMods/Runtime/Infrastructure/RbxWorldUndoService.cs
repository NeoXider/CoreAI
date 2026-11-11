using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using Cysharp.Threading.Tasks;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// Undo and redo of AI changes to the live world, one history per actor. A chat <c>/undo</c>, a HUD
    /// button or a host calls it; no AI tool is given it.
    /// </summary>
    public interface IRbxWorldUndoService
    {
        /// <summary>How many AI changes one actor's history keeps; older ones are dropped.</summary>
        int Capacity { get; }

        /// <summary>The actor's history and whether its latest change can be undone or redone now.</summary>
        RbxWorldUndoStatus GetStatus(string actorId);

        /// <summary>
        /// Restores the world as it was before the caller's latest AI change, in place and without a player
        /// confirmation, when nothing else changed the world since; otherwise refuses with the reason.
        /// Never throws for a refusal; cancellation throws. No thread and no blocking wait (WebGL).
        /// </summary>
        UniTask<RbxWorldUndoResult> UndoAsync(ActorContext caller, CancellationToken cancellationToken = default);

        /// <summary>Puts back the change the caller's latest undo removed, under the same rule.</summary>
        UniTask<RbxWorldUndoResult> RedoAsync(ActorContext caller, CancellationToken cancellationToken = default);

        /// <summary>Raised with the actor id whenever that actor's history changed.</summary>
        event Action<string> HistoryChanged;
    }

    /// <summary>What the undo service needs from the live world session.</summary>
    public interface IRbxWorldUndoHost
    {
        /// <summary>A cheap mark of the published world's world-owned instances and mod sources.</summary>
        RbxWorldStateMark MarkPublishedWorld();

        /// <summary>
        /// Replaces the published world with <paramref name="payload"/> without a player confirmation and
        /// keeps the world that opens on the next start in step with it. The caller holds the shared world
        /// mutation gate (the undo service calls it as its gated mutation).
        /// </summary>
        UniTask<RbxWorldLoadResult> RestoreSnapshotInPlaceAsync(
            RbxWorldPackagePayload payload,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// A fingerprint of a published world: which registry it is, every world-owned instance's id and
    /// revision, the Terrain cells' revision, how many times its mod sources were written, and
    /// (<see cref="Values"/>) the package state that carries no revision of its own. Two marks are equal only
    /// when nothing a world package holds can have changed in between.
    /// </summary>
    /// <remarks>
    /// WHY revisions and not a capture digest: a capture also holds every Part's pose and the camera,
    /// which physics and the player move every frame, so a digest would refuse every undo in a live world.
    /// Every scripted change to an instance advances its revision; physics and the camera do not.
    /// WHY the DataModel and its services count by id only: their revision also advances when a mod
    /// parents one of its own instances to them (the Children member), which no world package holds, so a
    /// mod spawning a coin into Workspace would otherwise refuse every undo. What a package holds of a service
    /// (the Lighting day cycle, StarterPlayer and StarterGui settings) is in <see cref="Values"/> instead,
    /// with the PointLight and GUI properties, whose setters advance no revision.
    /// Instances outside the DataModel, and instances below a mod's own or a runtime instance (a Player's
    /// PlayerGui copies of StarterGui, a character), are left out, as a package leaves them out.
    /// </remarks>
    public readonly struct RbxWorldStateMark : IEquatable<RbxWorldStateMark>
    {
        private readonly object _world;

        private RbxWorldStateMark(object world, long sourceChanges, int instances, ulong sum, ulong mix, ulong values)
        {
            _world = world;
            SourceChanges = sourceChanges;
            Instances = instances;
            Sum = sum;
            Mix = mix;
            Values = values;
        }

        /// <summary>True for the default mark, which equals no published world.</summary>
        public bool IsEmpty => _world == null;

        public long SourceChanges { get; }

        public int Instances { get; }

        public ulong Sum { get; }

        public ulong Mix { get; }

        /// <summary>
        /// A fingerprint of the package state that carries no revision: the Lighting day cycle (and whether the
        /// world drives it), the StarterPlayer and StarterGui settings, and the properties of every retained
        /// PointLight and GUI node. Their setters advance no revision (or, for Lighting, only the service's, which
        /// mod children churn), so they are compared by value.
        /// </summary>
        public ulong Values { get; }

        /// <summary>True when both marks were taken of the same published world object.</summary>
        public bool IsSameWorldAs(RbxWorldStateMark other)
        {
            return _world != null && ReferenceEquals(_world, other._world);
        }

        /// <summary>
        /// True when both marks hold the same instances, revisions, cells and source writes; <see cref="Values"/> is
        /// not compared.
        /// </summary>
        public bool HasSameRevisionsAs(RbxWorldStateMark other)
        {
            return _world != null
                   && ReferenceEquals(_world, other._world)
                   && SourceChanges == other.SourceChanges
                   && Instances == other.Instances
                   && Sum == other.Sum
                   && Mix == other.Mix;
        }

        /// <summary>Marks <paramref name="registry"/>: a walk over its live instances, no allocation per instance.</summary>
        public static RbxWorldStateMark Of(InstanceRegistry registry, long sourceChanges)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            int instances = 0;
            ulong sum = 0UL;
            ulong mix = 0UL;
            ulong values = 1469598103934665603UL;
            RbxInstance dataModel = registry.WorldRoot?.Parent;
            IReadOnlyList<RbxInstance> live = registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                RbxInstance instance = live[index];
                if (instance == null || !registry.TryGetRecord(instance.Id, out InstanceRecord record))
                {
                    continue;
                }

                // WHY the same filter as the startup watch: a mod's own instances and runtime
                // infrastructure are rebuilt by their owners and never stored in a world package. The
                // Terrain node is rebuilt too, but its cells are stored, so their store revision counts.
                if (record.IsRuntimeInfrastructure && instance is RbxTerrain terrain
                    && string.IsNullOrEmpty(record.OwnerModId))
                {
                    ulong cells = Spread(unchecked(record.Id.Value * 0x9E3779B97F4A7C15UL
                                                   ^ (ulong)terrain.Store.Revision ^ 0xA0761D6478BD642FUL));
                    sum = unchecked(sum + cells);
                    mix ^= Spread(cells ^ 0xD6E8FEB86659FD93UL);
                    continue;
                }

                if (!string.IsNullOrEmpty(record.OwnerModId) || record.IsRuntimeInfrastructure)
                {
                    continue;
                }

                RbxInstance parent = instance.Parent;
                if (dataModel != null && !ReferenceEquals(instance, dataModel)
                                      && !IsRetainedBelow(parent, dataModel, registry))
                {
                    continue;
                }

                bool rootOrService = parent == null || parent.Parent == null;
                ulong identity = unchecked(record.Id.Value * 0x9E3779B97F4A7C15UL);
                ulong hash = Spread(rootOrService ? identity : identity ^ unchecked((ulong)record.Revision));
                instances++;
                sum = unchecked(sum + hash);
                mix ^= Spread(hash ^ 0xD6E8FEB86659FD93UL);
                values = FoldValues(values, instance, identity);
            }

            return new RbxWorldStateMark(registry, sourceChanges, instances, sum, mix, values);
        }

        public bool Equals(RbxWorldStateMark other)
        {
            return HasSameRevisionsAs(other) && Values == other.Values;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldStateMark other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)(Sum ^ (Mix >> 7) ^ Values) ^ Instances);
        }

        /// <summary>
        /// Folds what a package stores of <paramref name="instance"/> without a revision into the running value hash.
        /// The walk visits instances in no fixed order, so each contribution is mixed with the instance's identity and
        /// added, which does not depend on the order.
        /// </summary>
        private static ulong FoldValues(ulong values, RbxInstance instance, ulong identity)
        {
            ulong state;
            switch (instance)
            {
                case RbxLighting lighting:
                {
                    RbxLightingDurableState durable = lighting.CaptureDurableState();
                    state = Combine(Combine(Combine(Combine(Combine(Combine(identity, lighting.IsDriven ? 1UL : 2UL), Bits(durable.ClockTime)),
                        Bits(durable.GeographicLatitude)), Bits(durable.Brightness)), Bits(durable.Ambient)),
                        Bits(durable.OutdoorAmbient));
                    break;
                }
                case RbxStarterPlayer starterPlayer:
                    state = Combine(Combine(Combine(Combine(identity, (ulong)starterPlayer.DevComputerMovementMode),
                        (ulong)starterPlayer.CameraMode), (ulong)BitConverter.DoubleToInt64Bits(starterPlayer.CameraMaxZoomDistance)),
                        (ulong)BitConverter.DoubleToInt64Bits(starterPlayer.CameraMinZoomDistance));
                    break;
                case RbxStarterGui starterGui:
                    state = Combine(Combine(identity, starterGui.ShowDevelopmentGui ? 1UL : 2UL), unchecked((ulong)starterGui.ScreenOrientation));
                    for (int type = 0; type < RbxWorldPackageStarterGui.CoreGuiTypeCount; type++)
                    {
                        if (type != RbxStarterGui.CoreGuiTypeAll)
                        {
                            state = Combine(state, starterGui.GetCoreGuiEnabled(type) ? 3UL : 5UL);
                        }
                    }

                    break;
                case RbxPointLight light:
                    state = Combine(Combine(Combine(Combine(Combine(identity, Bits(light.Brightness)), Bits(light.Color)),
                        light.Enabled ? 1UL : 2UL), light.Shadows ? 3UL : 5UL), Bits(light.Range));
                    break;
                case RbxGuiBase2d _:
                case RbxUiBase _:
                    state = RbxWorldPackageGuiSchema.Fold(identity, instance);
                    break;
                default:
                    return values;
            }

            return unchecked(values + Spread(state));
        }

        private static ulong Combine(ulong hash, ulong data)
        {
            return unchecked((hash ^ data) * 1099511628211UL);
        }

        private static ulong Bits(float value)
        {
            return unchecked((uint)BitConverter.SingleToInt32Bits(value));
        }

        private static ulong Bits(RbxColor3 color)
        {
            return Combine(Combine(Bits(color.R), Bits(color.G)), Bits(color.B));
        }

        /// <summary>
        /// True when <paramref name="ancestorOrSelf"/> is <paramref name="dataModel"/> or sits below it with no mod's
        /// own or runtime instance on the way, so a package would keep a child of it.
        /// </summary>
        private static bool IsRetainedBelow(RbxInstance ancestorOrSelf, RbxInstance dataModel, InstanceRegistry registry)
        {
            RbxInstance current = ancestorOrSelf;
            while (current != null)
            {
                if (ReferenceEquals(current, dataModel))
                {
                    return true;
                }

                if (registry.TryGetRecord(current.Id, out InstanceRecord record)
                    && (!string.IsNullOrEmpty(record.OwnerModId) || record.IsRuntimeInfrastructure))
                {
                    return false;
                }

                current = current.Parent;
            }

            return false;
        }

        private static ulong Spread(ulong value)
        {
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            return value ^ (value >> 31);
        }
    }

    /// <summary>Outcome of one undo or redo.</summary>
    public sealed class RbxWorldUndoResult
    {
        public const string UndoneStatus = "undone";
        public const string RedoneStatus = "redone";
        public const string NothingToUndoStatus = "nothing_to_undo";
        public const string NothingToRedoStatus = "nothing_to_redo";
        public const string WorldChangedStatus = "world_changed";
        public const string RestoreFailedStatus = "restore_failed";
        public const string BackupFailedStatus = "backup_failed";
        public const string UntrustedStatus = "untrusted";

        public RbxWorldUndoResult(
            bool success,
            string status,
            string message,
            string actorId,
            int undoCount,
            int redoCount)
        {
            Success = success;
            Status = status ?? "";
            Message = message ?? "";
            ActorId = actorId ?? "";
            UndoCount = undoCount;
            RedoCount = redoCount;
        }

        public bool Success { get; }

        /// <summary>One of the <c>*Status</c> constants.</summary>
        public string Status { get; }

        /// <summary>One line for the player: what happened, or why nothing did and what to do instead.</summary>
        public string Message { get; }

        public string ActorId { get; }

        /// <summary>AI changes of this actor still in its undo history after the call.</summary>
        public int UndoCount { get; }

        /// <summary>Undone changes of this actor that can be redone after the call.</summary>
        public int RedoCount { get; }

        internal RbxWorldUndoResult WithCounts(int undoCount, int redoCount)
        {
            return new RbxWorldUndoResult(Success, Status, Message, ActorId, undoCount, redoCount);
        }
    }

    /// <summary>One actor's undo history at one moment, for a HUD or a chat command.</summary>
    public sealed class RbxWorldUndoStatus
    {
        public RbxWorldUndoStatus(
            string actorId,
            int undoCount,
            int redoCount,
            bool canUndo,
            bool canRedo,
            string reason)
        {
            ActorId = actorId ?? "";
            UndoCount = undoCount;
            RedoCount = redoCount;
            CanUndo = canUndo;
            CanRedo = canRedo;
            Reason = reason ?? "";
        }

        public string ActorId { get; }

        public int UndoCount { get; }

        public int RedoCount { get; }

        public bool CanUndo { get; }

        public bool CanRedo { get; }

        /// <summary>Why the latest change cannot be undone now; empty when it can or when there is none.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// The default <see cref="IRbxWorldUndoService"/>: it observes the shared gate, keeps the pre-mutation
    /// snapshot of each actor's last <see cref="Capacity"/> AI changes in memory, and restores one through
    /// the gate (so the world before the undo is itself autosaved and can be redone).
    /// </summary>
    /// <remarks>
    /// An AI change is a gated mutation that changed the world-owned instances, the mod sources or the
    /// revision-free package state (<see cref="RbxWorldStateMark.Values"/>: the Lighting day cycle, StarterPlayer
    /// and StarterGui settings, PointLight and GUI properties) and whose snapshot exists; a completed
    /// <c>manage_mods</c> load, reload, unload, import, forget or revert always counts. The revision-free state
    /// counts only across a gated mutation: a script that drives it every frame changes it outside the gate, which
    /// neither records an entry nor refuses an undo, and an undo puts back the value its snapshot holds. A gated call that changed nothing (a read-only <c>execute_lua</c>) leaves the history
    /// alone, so <c>/undo</c> never "undoes" a lookup. In the world a composition starts with, a mod source
    /// edited outside the gate (in the Hub) is not seen by the "nothing changed since" check; in a world
    /// loaded or restored later it is. Undo restores the whole snapshot, which is
    /// what a world package holds: the world-owned tree, Part state, camera and mod sources; mods restart
    /// from their sources, as after a world load. A finer, operation-level undo is roadmap P5.
    /// </remarks>
    public sealed class RbxWorldUndoService : IRbxWorldUndoService, IRbxWorldMutationObserver, IDisposable
    {
        /// <summary>The default history length per actor.</summary>
        public const int DefaultCapacity = 10;

        /// <summary>The backup trigger of an undo; its autosave holds the world as the undone change left it.</summary>
        public const string UndoTrigger = "coreai-undo";

        /// <summary>The backup trigger of a redo.</summary>
        public const string RedoTrigger = "coreai-redo";

        private readonly object _gate = new();
        private readonly IConfirmedWorldMutationGate _mutationGate;
        private readonly Func<IRbxWorldUndoHost> _hostResolver;
        private readonly Dictionary<string, ActorHistory> _histories = new(StringComparer.Ordinal);
        private IRbxWorldUndoHost _host;
        private RbxWorldStateMark _markAtStart;
        private bool _markAtStartTaken;
        private PendingMove _pendingMove;
        private string _lastChangeActor = "";
        private long _lastChangeSequence;

        /// <summary>
        /// How many gated mutations (other than undo and redo) changed <see cref="RbxWorldStateMark.Values"/>: what
        /// "nothing changed since" compares for that state instead of its values.
        /// </summary>
        private long _valueEdits;
        private bool _disposed;

        /// <summary>Creates the service over <paramref name="host"/>.</summary>
        public RbxWorldUndoService(
            IConfirmedWorldMutationGate mutationGate,
            IRbxWorldUndoHost host,
            int capacity = DefaultCapacity)
            : this(mutationGate, CreateFixedResolver(host), capacity)
        {
        }

        /// <param name="hostResolver">
        /// Resolves the session on first use, so a composition can build the service before the world
        /// session exists; it is called at most until it returns a host.
        /// </param>
        public RbxWorldUndoService(
            IConfirmedWorldMutationGate mutationGate,
            Func<IRbxWorldUndoHost> hostResolver,
            int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The undo history must keep at least one change.");
            }

            _mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
            _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
            Capacity = capacity;
            if (mutationGate is IObservableWorldMutationGate observable)
            {
                observable.AddObserver(this);
            }
            else
            {
                IsObservingGate = false;
            }
        }

        /// <inheritdoc />
        public event Action<string> HistoryChanged;

        /// <inheritdoc />
        public int Capacity { get; }

        /// <summary>
        /// False when the gate cannot be observed (a host gate without <see cref="IObservableWorldMutationGate"/>):
        /// no AI change is then recorded and every undo answers that there is nothing to undo.
        /// </summary>
        public bool IsObservingGate { get; } = true;

        /// <inheritdoc />
        public RbxWorldUndoStatus GetStatus(string actorId)
        {
            string id = actorId ?? "";
            Entry undoTop;
            Entry redoTop;
            int undoCount;
            int redoCount;
            long valueEdits;
            lock (_gate)
            {
                if (!_histories.TryGetValue(id, out ActorHistory history))
                {
                    return new RbxWorldUndoStatus(id, 0, 0, false, false, "");
                }

                undoCount = history.Undo.Count;
                redoCount = history.Redo.Count;
                undoTop = undoCount > 0 ? history.Undo[undoCount - 1] : null;
                redoTop = redoCount > 0 ? history.Redo[redoCount - 1] : null;
                valueEdits = _valueEdits;
            }

            RbxWorldStateMark now = TryMarkNow();
            bool canUndo = undoTop != null && IsUnchangedSince(undoTop, now, valueEdits);
            bool canRedo = redoTop != null && IsUnchangedSince(redoTop, now, valueEdits);
            string reason = undoTop != null && !canUndo ? DescribeWorldChanged(id, undoTop, now) : "";
            return new RbxWorldUndoStatus(id, undoCount, redoCount, canUndo, canRedo, reason);
        }

        /// <inheritdoc />
        public UniTask<RbxWorldUndoResult> UndoAsync(
            ActorContext caller,
            CancellationToken cancellationToken = default)
        {
            return MoveThroughGateAsync(caller, true, cancellationToken);
        }

        /// <inheritdoc />
        public UniTask<RbxWorldUndoResult> RedoAsync(
            ActorContext caller,
            CancellationToken cancellationToken = default)
        {
            return MoveThroughGateAsync(caller, false, cancellationToken);
        }

        /// <inheritdoc />
        public void OnMutationStarting(RbxWorldMutationInfo mutation)
        {
            lock (_gate)
            {
                _markAtStartTaken = false;
            }

            if (mutation == null || IsOwnTrigger(mutation.Trigger) || IsWorldLoad(mutation.Trigger))
            {
                return;
            }

            RbxWorldStateMark mark = TryMarkNow();
            lock (_gate)
            {
                _markAtStart = mark;
                _markAtStartTaken = !mark.IsEmpty;
            }
        }

        /// <inheritdoc />
        public void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
        {
            if (mutation == null)
            {
                return;
            }

            if (IsOwnTrigger(mutation.Trigger))
            {
                FinishMove(before);
                return;
            }

            if (IsWorldLoad(mutation.Trigger))
            {
                // WHY cleared: a confirmed load replaced the whole world, so no snapshot taken before it
                // can be restored in place, and keeping them only holds memory.
                if (completed)
                {
                    ClearAll();
                }

                return;
            }

            RbxWorldStateMark start;
            lock (_gate)
            {
                if (!_markAtStartTaken)
                {
                    return;
                }

                start = _markAtStart;
                _markAtStartTaken = false;
            }

            RbxWorldStateMark after = TryMarkNow();
            // WHY a completed mod-management mutation always counts: the world the composition starts with
            // writes mod sources straight to its store, which no mark can see without reading every source,
            // so the trigger is the evidence there (a world loaded later also counts each write in the mark).
            bool changedSources = completed && IsModSourceTrigger(mutation.Trigger);
            // WHY the values count only between the start and the end of a gated mutation: a day-cycle or flicker
            // script writes ClockTime or Brightness every frame on the frame pump, outside the gate, so a value
            // that differs across one gated call is that call's edit (one that yields frames may also carry a
            // script's write of those frames, once, never once per frame).
            bool changedValues = !after.IsEmpty && after.IsSameWorldAs(start) && after.Values != start.Values;
            if (after.IsEmpty || (after.HasSameRevisionsAs(start) && !changedValues && !changedSources))
            {
                return;
            }

            string actorId = mutation.ActorId;
            lock (_gate)
            {
                long valueEditsAtStart = _valueEdits;
                if (changedValues)
                {
                    _valueEdits++;
                }

                _lastChangeActor = actorId;
                _lastChangeSequence = mutation.Sequence;
                if (before == null || actorId.Length == 0 || _disposed)
                {
                    return;
                }

                ActorHistory history = GetOrCreateHistoryLocked(actorId);
                bool chained = history.Undo.Count > 0
                               && IsUnchangedSince(history.Undo[history.Undo.Count - 1], start, valueEditsAtStart);
                history.Undo.Add(new Entry(before, after, chained, mutation.Trigger, mutation.Sequence, _valueEdits));
                TrimLocked(history.Undo, true);
                history.Redo.Clear();
            }

            RaiseHistoryChanged(actorId);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _histories.Clear();
            }

            if (_mutationGate is IObservableWorldMutationGate observable)
            {
                observable.RemoveObserver(this);
            }
        }

        private async UniTask<RbxWorldUndoResult> MoveThroughGateAsync(
            ActorContext caller,
            bool undo,
            CancellationToken cancellationToken)
        {
            string actorId = caller.ActorId ?? "";
            if (!caller.IsTrusted)
            {
                return new RbxWorldUndoResult(
                    false,
                    RbxWorldUndoResult.UntrustedStatus,
                    "The " + Verb(undo) + " was refused: the actor context was not issued by an identity provider.",
                    actorId,
                    0,
                    0);
            }

            if (!HasEntries(actorId, undo))
            {
                return Counted(NothingToMove(actorId, undo));
            }

            // WHY checked before the gate as well as inside it: a refusal must not cost an autosave, and
            // the check inside the gate still decides, so nothing can slip in between.
            RbxWorldUndoResult refusal = RefuseIfTheWorldMoved(actorId, undo);
            if (refusal != null)
            {
                return Counted(refusal);
            }

            RbxWorldUndoResult result;
            try
            {
                result = await _mutationGate.ExecuteAttributedAsync(
                    undo ? UndoTrigger : RedoTrigger,
                    actorId,
                    token => MoveInsideGateAsync(actorId, undo, token).AsTask(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ClearPendingMove();
                throw;
            }
            catch (Exception ex)
            {
                ClearPendingMove();
                result = new RbxWorldUndoResult(
                    false,
                    RbxWorldUndoResult.BackupFailedStatus,
                    "The " + Verb(undo) + " did not run, and the world was not changed: " + ex.Message,
                    actorId,
                    0,
                    0);
            }

            return Counted(result);
        }

        private async UniTask<RbxWorldUndoResult> MoveInsideGateAsync(
            string actorId,
            bool undo,
            CancellationToken cancellationToken)
        {
            Entry top;
            long valueEdits;
            lock (_gate)
            {
                _pendingMove = null;
                if (!_histories.TryGetValue(actorId, out ActorHistory history))
                {
                    return NothingToMove(actorId, undo);
                }

                List<Entry> from = undo ? history.Undo : history.Redo;
                if (from.Count == 0)
                {
                    return NothingToMove(actorId, undo);
                }

                top = from[from.Count - 1];
                valueEdits = _valueEdits;
            }

            IRbxWorldUndoHost host = ResolveHost();
            if (host == null)
            {
                return new RbxWorldUndoResult(false, RbxWorldUndoResult.RestoreFailedStatus,
                    "The " + Verb(undo) + " did not run: no world session is composed.", actorId, 0, 0);
            }

            RbxWorldStateMark now = host.MarkPublishedWorld();
            if (!IsUnchangedSince(top, now, valueEdits))
            {
                return new RbxWorldUndoResult(
                    false,
                    RbxWorldUndoResult.WorldChangedStatus,
                    DescribeWorldChanged(actorId, top, now),
                    actorId,
                    0,
                    0);
            }

            RbxWorldPackagePayload payload;
            try
            {
                payload = top.Before.ToPayload();
            }
            catch (Exception ex)
            {
                return new RbxWorldUndoResult(false, RbxWorldUndoResult.RestoreFailedStatus,
                    "The " + Verb(undo) + " did not run: the kept snapshot could not be read (" + ex.Message + ").",
                    actorId, 0, 0);
            }

            RbxWorldLoadResult restored = await host.RestoreSnapshotInPlaceAsync(payload, cancellationToken);
            if (restored == null || !restored.Success)
            {
                return new RbxWorldUndoResult(false, RbxWorldUndoResult.RestoreFailedStatus,
                    "The " + Verb(undo) + " did not run, and the world was not changed: "
                    + (restored?.Error ?? "the world session returned no result") + " Open the World Loads page "
                    + "to restore an autosave instead.",
                    actorId, 0, 0);
            }

            RbxWorldStateMark restoredMark = host.MarkPublishedWorld();
            lock (_gate)
            {
                if (_histories.TryGetValue(actorId, out ActorHistory history))
                {
                    List<Entry> from = undo ? history.Undo : history.Redo;
                    if (from.Count > 0 && ReferenceEquals(from[from.Count - 1], top))
                    {
                        from.RemoveAt(from.Count - 1);
                    }

                    // WHY: the entry below was taken of a world this restore just rebuilt; only when nothing
                    // else ran between the two changes is the rebuilt world the one that entry left.
                    if (undo && top.ChainedToPrevious && history.Undo.Count > 0)
                    {
                        history.Undo[history.Undo.Count - 1].MarkAfter(restoredMark, _valueEdits);
                    }

                    if (!undo && history.Redo.Count > 0)
                    {
                        history.Redo[history.Redo.Count - 1].MarkAfter(restoredMark, _valueEdits);
                    }
                }

                _pendingMove = new PendingMove(actorId, undo, top, restoredMark, _valueEdits);
            }

            return new RbxWorldUndoResult(
                true,
                undo ? RbxWorldUndoResult.UndoneStatus : RbxWorldUndoResult.RedoneStatus,
                undo
                    ? "The last AI change ('" + top.Trigger + "') was undone."
                    : "The undone AI change ('" + top.Trigger + "') was put back.",
                actorId,
                0,
                0);
        }

        /// <summary>
        /// Files the moved entry on the opposite stack, with the gate's snapshot of the world just before
        /// the undo or redo as what restores it.
        /// </summary>
        private void FinishMove(RbxWorldSnapshot before)
        {
            string actorId;
            lock (_gate)
            {
                PendingMove move = _pendingMove;
                _pendingMove = null;
                if (move == null || _disposed)
                {
                    return;
                }

                actorId = move.ActorId;
                if (before != null)
                {
                    ActorHistory history = GetOrCreateHistoryLocked(move.ActorId);
                    List<Entry> to = move.Undo ? history.Redo : history.Undo;
                    to.Add(new Entry(
                        before,
                        move.RestoredMark,
                        move.Entry.ChainedToPrevious,
                        move.Entry.Trigger,
                        move.Entry.Sequence,
                        move.ValueEdits));
                    TrimLocked(to, !move.Undo);
                }
            }

            RaiseHistoryChanged(actorId);
        }

        private RbxWorldUndoResult RefuseIfTheWorldMoved(string actorId, bool undo)
        {
            Entry top;
            long valueEdits;
            lock (_gate)
            {
                if (!_histories.TryGetValue(actorId, out ActorHistory history))
                {
                    return null;
                }

                List<Entry> from = undo ? history.Undo : history.Redo;
                top = from.Count > 0 ? from[from.Count - 1] : null;
                valueEdits = _valueEdits;
            }

            if (top == null)
            {
                return null;
            }

            RbxWorldStateMark now = TryMarkNow();
            if (now.IsEmpty || IsUnchangedSince(top, now, valueEdits))
            {
                return null;
            }

            return new RbxWorldUndoResult(
                false,
                RbxWorldUndoResult.WorldChangedStatus,
                DescribeWorldChanged(actorId, top, now),
                actorId,
                0,
                0);
        }

        /// <summary>
        /// True when nothing that refuses a move happened since <paramref name="entry"/>'s change: the revisions,
        /// cells and source writes are as it left them, and no other gated mutation edited the revision-free state.
        /// </summary>
        /// <remarks>
        /// WHY that state counts by gated edits and not by value here: a day-cycle script moves ClockTime every
        /// frame, so comparing the value would refuse every undo in a world that has one. A change of it outside the
        /// gate is treated like physics moving a Part: it never refuses an undo, and the undo puts the snapshot's
        /// value back. A gated change of it by anyone (another agent's "make it day") still does.
        /// </remarks>
        private static bool IsUnchangedSince(Entry entry, RbxWorldStateMark now, long valueEdits)
        {
            return now.HasSameRevisionsAs(entry.After) && entry.AfterValueEdits == valueEdits;
        }

        private bool HasEntries(string actorId, bool undo)
        {
            lock (_gate)
            {
                return _histories.TryGetValue(actorId, out ActorHistory history)
                       && (undo ? history.Undo.Count : history.Redo.Count) > 0;
            }
        }

        private RbxWorldUndoResult Counted(RbxWorldUndoResult result)
        {
            lock (_gate)
            {
                if (!_histories.TryGetValue(result.ActorId, out ActorHistory history))
                {
                    return result.WithCounts(0, 0);
                }

                return result.WithCounts(history.Undo.Count, history.Redo.Count);
            }
        }

        private static RbxWorldUndoResult NothingToMove(string actorId, bool undo)
        {
            return undo
                ? new RbxWorldUndoResult(false, RbxWorldUndoResult.NothingToUndoStatus,
                    "There is no AI change of yours to undo.", actorId, 0, 0)
                : new RbxWorldUndoResult(false, RbxWorldUndoResult.NothingToRedoStatus,
                    "There is no undone AI change of yours to redo.", actorId, 0, 0);
        }

        private string DescribeWorldChanged(string actorId, Entry top, RbxWorldStateMark now)
        {
            const string WayOut = " Open the World Loads page to restore an autosave instead.";
            if (!now.IsSameWorldAs(top.After))
            {
                return "The world was replaced (a world load, a restore or another undo) after this AI change, "
                       + "so it can no longer be undone in place." + WayOut;
            }

            string lastActor;
            long lastSequence;
            lock (_gate)
            {
                lastActor = _lastChangeActor;
                lastSequence = _lastChangeSequence;
            }

            if (lastSequence > top.Sequence && !string.Equals(lastActor, actorId, StringComparison.Ordinal))
            {
                return (lastActor.Length == 0 ? "Another AI change" : "Agent '" + lastActor + "'")
                       + " changed the world after your last AI change; undoing yours now would also undo that "
                       + "change." + WayOut;
            }

            return "The world changed after your last AI change (a player, a mod script or the Hub edited it), "
                   + "so undoing it now would also undo that edit." + WayOut;
        }

        private RbxWorldStateMark TryMarkNow()
        {
            try
            {
                IRbxWorldUndoHost host = ResolveHost();
                return host == null ? default : host.MarkPublishedWorld();
            }
            catch (Exception)
            {
                return default;
            }
        }

        private IRbxWorldUndoHost ResolveHost()
        {
            IRbxWorldUndoHost host = Volatile.Read(ref _host);
            if (host != null)
            {
                return host;
            }

            host = _hostResolver();
            if (host != null)
            {
                Volatile.Write(ref _host, host);
            }

            return host;
        }

        private ActorHistory GetOrCreateHistoryLocked(string actorId)
        {
            if (!_histories.TryGetValue(actorId, out ActorHistory history))
            {
                history = new ActorHistory();
                _histories.Add(actorId, history);
            }

            return history;
        }

        /// <param name="undoStack">True for an undo stack, whose oldest kept entry loses its chain.</param>
        private void TrimLocked(List<Entry> entries, bool undoStack)
        {
            int excess = entries.Count - Capacity;
            if (excess <= 0)
            {
                return;
            }

            entries.RemoveRange(0, excess);
            if (undoStack)
            {
                // WHY: the oldest kept entry has nothing below it now, so no chain can refresh one below.
                entries[0].ChainedToPrevious = false;
            }
        }

        private void ClearAll()
        {
            List<string> actors;
            lock (_gate)
            {
                actors = new List<string>(_histories.Keys);
                _histories.Clear();
            }

            foreach (string actor in actors)
            {
                RaiseHistoryChanged(actor);
            }
        }

        private void ClearPendingMove()
        {
            lock (_gate)
            {
                _pendingMove = null;
            }
        }

        private void RaiseHistoryChanged(string actorId)
        {
            Action<string> handlers = HistoryChanged;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(actorId);
                }
                catch (Exception)
                {
                }
            }
        }

        private static bool IsOwnTrigger(string trigger)
        {
            return string.Equals(trigger, UndoTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, RedoTrigger, StringComparison.Ordinal);
        }

        private static bool IsModSourceTrigger(string trigger)
        {
            return string.Equals(trigger, LuaModsLlmTool.LoadBackupTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, LuaModsLlmTool.ReloadBackupTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, LuaModsLlmTool.UnloadBackupTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, LuaModsLlmTool.ImportBackupTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, LuaModsLlmTool.ForgetBackupTrigger, StringComparison.Ordinal)
                   || string.Equals(trigger, LuaModsLlmTool.RevertBackupTrigger, StringComparison.Ordinal);
        }

        private static bool IsWorldLoad(string trigger)
        {
            return string.Equals(trigger, RbxWorldRuntimeSessionController.PreLoadAutosaveTrigger, StringComparison.Ordinal);
        }

        private static string Verb(bool undo)
        {
            return undo ? "undo" : "redo";
        }

        private static Func<IRbxWorldUndoHost> CreateFixedResolver(IRbxWorldUndoHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            return () => host;
        }

        private sealed class ActorHistory
        {
            public readonly List<Entry> Undo = new();
            public readonly List<Entry> Redo = new();
        }

        private sealed class Entry
        {
            public Entry(
                RbxWorldSnapshot before,
                RbxWorldStateMark after,
                bool chainedToPrevious,
                string trigger,
                long sequence,
                long afterValueEdits)
            {
                Before = before;
                After = after;
                ChainedToPrevious = chainedToPrevious;
                Trigger = trigger ?? "";
                Sequence = sequence;
                AfterValueEdits = afterValueEdits;
            }

            /// <summary>The world before the change; restoring it takes the change back.</summary>
            public RbxWorldSnapshot Before { get; }

            /// <summary>The world as the change left it; the entry can move only while the world still is.</summary>
            public RbxWorldStateMark After { get; private set; }

            /// <summary>The service's count of gated value edits right after the change.</summary>
            public long AfterValueEdits { get; private set; }

            public void MarkAfter(RbxWorldStateMark after, long valueEdits)
            {
                After = after;
                AfterValueEdits = valueEdits;
            }

            /// <summary>True when nothing changed the world between the entry below and this one.</summary>
            public bool ChainedToPrevious { get; set; }

            public string Trigger { get; }

            public long Sequence { get; }
        }

        private sealed class PendingMove
        {
            public PendingMove(string actorId, bool undo, Entry entry, RbxWorldStateMark restoredMark, long valueEdits)
            {
                ActorId = actorId;
                Undo = undo;
                Entry = entry;
                RestoredMark = restoredMark;
                ValueEdits = valueEdits;
            }

            public long ValueEdits { get; }

            public string ActorId { get; }

            public bool Undo { get; }

            public Entry Entry { get; }

            public RbxWorldStateMark RestoredMark { get; }
        }
    }
}
