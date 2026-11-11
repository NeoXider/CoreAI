using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// The engine-free <see cref="IScriptInstanceHost"/> over one world's registry: a binding index by source id, the
    /// SC-6 placement (a <c>parent:</c> path or the default container and folder), the undo of a failed build, the
    /// release of a forgotten source, and the hand-over from a mod's synthetic legacy script to its durable one.
    /// </summary>
    /// <remarks>
    /// WHY an index rebuilt lazily: a bound instance names its source in <see cref="RbxLuaSourceContainer.SourceId"/>,
    /// which a restore sets only after it registers the instance, so registration alone cannot index it. The index is
    /// rebuilt from the live tree at the first lookup after a world-owned script container registered, which is rare;
    /// the synthetic legacy scripts and every mod-owned script are not bindings and never mark it stale.
    /// </remarks>
    public sealed class ScriptInstanceHost : IScriptInstanceHost, IDisposable
    {
        private const string FolderClassName = "Folder";

        private readonly InstanceRegistry _registry;
        private readonly RbxDataModel _game;
        private readonly Action<string> _log;
        private readonly Dictionary<string, RbxLuaSourceContainer> _bound = new(StringComparer.Ordinal);
        private readonly HashSet<string> _loggedDuplicates = new(StringComparer.Ordinal);
        private bool _indexStale = true;
        private bool _disposed;

        public ScriptInstanceHost(InstanceRegistry registry, RbxDataModel game, Action<string> log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _log = log;
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
        }

        /// <inheritdoc />
        public event Action<string, ScriptInstanceChange> ScriptInstanceChanged;

        /// <inheritdoc />
        public bool TryGetBoundScript(string sourceId, out RbxLuaSourceContainer script)
        {
            script = null;
            if (string.IsNullOrEmpty(sourceId))
            {
                return false;
            }

            EnsureIndex();
            if (!_bound.TryGetValue(sourceId, out RbxLuaSourceContainer candidate))
            {
                return false;
            }

            if (candidate.IsDestroyed || !string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal))
            {
                _bound.Remove(sourceId);
                return false;
            }

            script = candidate;
            return true;
        }

        /// <inheritdoc />
        public ScriptInstancePlacement Place(string sourceId, ScriptPlacementPlan plan, string ownerActorId,
            bool ownerHasHostAuthority)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                throw new ArgumentException("A source id is required.", nameof(sourceId));
            }

            if (plan == null || plan.Kind != ScriptPlacementKind.Durable)
            {
                throw new ArgumentException("Only a durable placement plan has an instance to place.", nameof(plan));
            }

            bool hasBound = TryGetBoundScript(sourceId, out RbxLuaSourceContainer bound);
            if (plan.ReusesBoundInstance != hasBound)
            {
                throw new InvalidOperationException(hasBound
                    ? "Mod '" + sourceId + "' is already bound to " + bound.ClassName + " instance "
                      + bound.Id.Value + "; a source is bound to at most one instance."
                    : "Mod '" + sourceId + "' was planned to keep its bound instance, but none is bound.");
            }

            List<RbxInstance> createdFolders = new List<RbxInstance>();
            try
            {
                return hasBound
                    ? Update(sourceId, bound, plan, ownerActorId, ownerHasHostAuthority, createdFolders)
                    : Create(sourceId, plan, ownerActorId, ownerHasHostAuthority, createdFolders);
            }
            catch
            {
                DestroyEmptyFolders(createdFolders);
                throw;
            }
        }

        /// <inheritdoc />
        public void UndoPlacement(ScriptInstancePlacement placement)
        {
            if (placement == null || placement.Undone)
            {
                return;
            }

            placement.Undone = true;
            RbxLuaSourceContainer script = placement.Script;
            if (placement.Created)
            {
                Unbind(placement.SourceId, script);
                if (!script.IsDestroyed)
                {
                    script.Destroy();
                }

                Raise(placement.SourceId, ScriptInstanceChange.Destroyed);
            }
            else if (!script.IsDestroyed)
            {
                if (placement.Adopted)
                {
                    Unbind(placement.SourceId, script);
                }

                RestoreRunContext(script, placement.PreviousRunContext);
                if (!ReferenceEquals(script.Parent, placement.PreviousParent)
                    && (placement.PreviousParent == null || !placement.PreviousParent.IsDestroyed))
                {
                    script.SetParentAtSiblingIndex(placement.PreviousParent, placement.PreviousSiblingIndex);
                }

                Raise(placement.SourceId, placement.Adopted ? ScriptInstanceChange.Released : ScriptInstanceChange.Updated);
            }

            DestroyEmptyFolders(placement.CreatedFolders);
        }

        /// <inheritdoc />
        public ScriptReleaseOutcome Release(string sourceId, IReadOnlyList<string> folderSegments)
        {
            if (!TryGetBoundScript(sourceId, out RbxLuaSourceContainer script))
            {
                return ScriptReleaseOutcome.NothingBound;
            }

            Unbind(sourceId, script);
            bool atDefault = ScriptContexts.TryGetEffective(script.ClassName, RunContextOf(script), ScriptContext.Legacy,
                                 out ScriptContext context, out string _)
                             && IsAt(script.Parent, ScriptPlacementPlanner.DefaultPath(context, folderSegments));
            if (atDefault && script.GetChildren().Count == 0)
            {
                script.Destroy();
                Raise(sourceId, ScriptInstanceChange.Destroyed);
                return ScriptReleaseOutcome.Destroyed;
            }

            _log?.Invoke("[CoreAI.RbxApi] mod '" + sourceId + "' was forgotten; its " + script.ClassName + " '"
                         + script.Name + "' stays in the world unbound because "
                         + (atDefault ? "it holds children" : "it was moved from its default placement")
                         + ", and runs nothing until a source is bound to it.");
            Raise(sourceId, ScriptInstanceChange.Released);
            return ScriptReleaseOutcome.Unbound;
        }

        /// <inheritdoc />
        public bool MoveFromDefaultPlacement(string sourceId, IReadOnlyList<string> fromDefaultPath,
            IReadOnlyList<string> toDefaultPath)
        {
            if (fromDefaultPath == null || toDefaultPath == null
                || !TryGetBoundScript(sourceId, out RbxLuaSourceContainer script)
                || !IsAt(script.Parent, fromDefaultPath) || SamePath(fromDefaultPath, toDefaultPath))
            {
                return false;
            }

            List<RbxInstance> createdFolders = new List<RbxInstance>();
            try
            {
                script.Parent = EnsureDefaultPath(toDefaultPath, createdFolders);
            }
            catch
            {
                DestroyEmptyFolders(createdFolders);
                throw;
            }

            Raise(sourceId, ScriptInstanceChange.Updated);
            return true;
        }

        /// <summary>
        /// Moves what a mod's synthetic legacy placement holds into its new durable script, then destroys the synthetic
        /// folder and script (MVP4 step C1, a reload from a legacy header to a declared one). In
        /// <paramref name="keepOwnObjects"/> mode every child moves; otherwise the children the mod itself owns go with
        /// the folder, as the startup objects of a clean reload do, and only what someone else owns is moved.
        /// Returns how many instances moved.
        /// </summary>
        public int HandOverLegacyPlacement(string sourceId, RbxInstance legacyFolder, RbxInstance legacyScript,
            RbxInstance successor, bool keepOwnObjects)
        {
            if (successor == null || successor.IsDestroyed)
            {
                throw new ArgumentException("The durable script to hand the legacy placement to is gone.",
                    nameof(successor));
            }

            int moved = 0;
            moved += MoveChildren(legacyScript, null, successor, sourceId, keepOwnObjects);
            moved += MoveChildren(legacyFolder, legacyScript, successor, sourceId, keepOwnObjects);
            if (legacyScript != null && !legacyScript.IsDestroyed)
            {
                legacyScript.Destroy();
            }

            if (legacyFolder != null && !legacyFolder.IsDestroyed)
            {
                legacyFolder.Destroy();
            }

            return moved;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Registered -= OnRegistered;
            _registry.Unregistered -= OnUnregistered;
            _bound.Clear();
            ScriptInstanceChanged = null;
        }

        private ScriptInstancePlacement Create(string sourceId, ScriptPlacementPlan plan, string ownerActorId,
            bool ownerHasHostAuthority, List<RbxInstance> createdFolders)
        {
            RbxInstance parent = plan.ParentPath != null
                ? ResolveParentPath(sourceId, plan.ParentPath, ownerActorId, ownerHasHostAuthority)
                : EnsureDefaultPath(plan.DefaultPath, createdFolders);
            RbxLuaSourceContainer adopted = FindAdoptable(parent, sourceId, plan.ClassName);
            if (adopted != null)
            {
                int previousRunContext = RunContextOf(adopted);
                SetRunContext(adopted, plan.RunContext);
                adopted.SourceId = sourceId;
                _bound[sourceId] = adopted;
                Raise(sourceId, ScriptInstanceChange.Bound);
                return new ScriptInstancePlacement(sourceId, adopted, false, true, adopted.Parent,
                    SiblingIndexOf(adopted), previousRunContext, createdFolders);
            }

            bool indexWasStale = _indexStale;
            RbxLuaSourceContainer script = null;
            try
            {
                script = (RbxLuaSourceContainer)_registry.Create(plan.ClassName, null, OriginTag.FromMod(sourceId),
                    ownerActorId: string.IsNullOrWhiteSpace(ownerActorId) ? null : ownerActorId.Trim(),
                    operation: "placing the script of mod '" + sourceId + "'");
                script.Name = sourceId;
                SetRunContext(script, plan.RunContext);
                script.SourceId = sourceId;
                script.Parent = parent;
            }
            catch
            {
                if (script != null && !script.IsDestroyed)
                {
                    script.Destroy();
                }

                throw;
            }
            finally
            {
                // WHY: the registration of this very instance marked the index stale, but it is indexed below, so a
                // declared load never costs a rescan of the world.
                _indexStale = indexWasStale;
            }

            _bound[sourceId] = script;
            Raise(sourceId, ScriptInstanceChange.Bound);
            return new ScriptInstancePlacement(sourceId, script, true, false, null, -1, RunContextOf(script),
                createdFolders);
        }

        private ScriptInstancePlacement Update(string sourceId, RbxLuaSourceContainer script, ScriptPlacementPlan plan,
            string ownerActorId, bool ownerHasHostAuthority, List<RbxInstance> createdFolders)
        {
            RbxInstance previousParent = script.Parent;
            int previousSiblingIndex = SiblingIndexOf(script);
            int previousRunContext = RunContextOf(script);
            RbxInstance target = null;
            if (plan.ParentPath != null)
            {
                target = ResolveParentPath(sourceId, plan.ParentPath, ownerActorId, ownerHasHostAuthority);
            }
            else if (plan.PreviousDefaultPath != null && IsAt(previousParent, plan.PreviousDefaultPath)
                                                      && !SamePath(plan.PreviousDefaultPath, plan.DefaultPath))
            {
                target = EnsureDefaultPath(plan.DefaultPath, createdFolders);
            }

            bool changed = false;
            try
            {
                if (previousRunContext != plan.RunContext)
                {
                    SetRunContext(script, plan.RunContext);
                    changed = true;
                }

                if (target != null && !ReferenceEquals(target, previousParent))
                {
                    script.Parent = target;
                    changed = true;
                }
            }
            catch
            {
                RestoreRunContext(script, previousRunContext);
                throw;
            }

            if (changed)
            {
                Raise(sourceId, ScriptInstanceChange.Updated);
            }

            return new ScriptInstancePlacement(sourceId, script, false, false, previousParent, previousSiblingIndex,
                previousRunContext, createdFolders);
        }

        /// <summary>
        /// Resolves a <c>parent:</c> path from <c>game</c> without waiting. Refuses, as BAD_ARGUMENT naming the cause, a
        /// segment that is not there, and a parent the world would not save the script with: runtime infrastructure,
        /// anything inside a mod's own subtree, and for an actor without host authority an instance another actor owns.
        /// </summary>
        private RbxInstance ResolveParentPath(string sourceId, IReadOnlyList<string> path, string ownerActorId,
            bool ownerHasHostAuthority)
        {
            string written = string.Join("/", path);
            RbxInstance node = _game;
            string reached = "game";
            for (int index = 0; index < path.Count; index++)
            {
                RbxInstance next = node.FindFirstChild(path[index]);
                if (next == null)
                {
                    throw RbxError.BadArgument(
                        "@coreai parent '" + written + "' of mod '" + sourceId + "': " + reached + " has no child named '"
                        + path[index] + "'",
                        "create the instance first, or correct the path (it is read from game down, names are exact)");
                }

                node = next;
                reached = reached + "." + next.Name;
            }

            for (RbxInstance ancestor = node; ancestor != null && !ReferenceEquals(ancestor, _game);
                 ancestor = ancestor.Parent)
            {
                if (!_registry.TryGetRecord(ancestor.Id, out InstanceRecord record))
                {
                    continue;
                }

                string refusal = null;
                if (record.IsRuntimeInfrastructure)
                {
                    refusal = ancestor.Name + " is runtime infrastructure, which the world does not save";
                }
                else if (!string.IsNullOrEmpty(record.OwnerModId))
                {
                    refusal = ancestor.Name + " belongs to mod '" + record.OwnerModId
                                            + "', and what a mod makes is not saved with the world";
                }
                else if (!ownerHasHostAuthority && record.AccessScope == InstanceAccessScope.Owned
                                                && !string.Equals(record.OwnerActorId, ownerActorId?.Trim(),
                                                    StringComparison.Ordinal))
                {
                    refusal = ancestor.Name + " belongs to actor '" + record.OwnerActorId + "'";
                }

                if (refusal != null)
                {
                    throw RbxError.BadArgument(
                        "@coreai parent '" + written + "' of mod '" + sourceId + "' cannot hold the script: " + refusal,
                        "place the script under a world-owned instance, such as a part or folder the world was built with");
                }
            }

            return node;
        }

        /// <summary>
        /// The default container service named by the first entry, then one world-owned Folder per later entry, found by
        /// name or created (owned by the host, like every container the world is built with).
        /// </summary>
        private RbxInstance EnsureDefaultPath(IReadOnlyList<string> path, List<RbxInstance> createdFolders)
        {
            if (path == null || path.Count == 0)
            {
                throw new ArgumentException("A default placement names its container service first.", nameof(path));
            }

            RbxInstance node = _game.FindFirstChildOfClass(path[0])
                               ?? throw new InvalidOperationException(
                                   "the game tree has no " + path[0] + " to place a script in");
            for (int index = 1; index < path.Count; index++)
            {
                RbxInstance folder = FindWorldOwnedFolder(node, path[index]);
                if (folder == null)
                {
                    folder = _registry.Create(FolderClassName);
                    folder.Name = path[index];
                    folder.Parent = node;
                    createdFolders.Add(folder);
                }

                node = folder;
            }

            return node;
        }

        private RbxInstance FindWorldOwnedFolder(RbxInstance parent, string name)
        {
            IReadOnlyList<RbxInstance> children = parent.GetChildren();
            for (int index = 0; index < children.Count; index++)
            {
                RbxInstance child = children[index];
                if (string.Equals(child.ClassName, FolderClassName, StringComparison.Ordinal)
                    && string.Equals(child.Name, name, StringComparison.Ordinal)
                    && IsWorldOwned(child))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>
        /// An unbound, world-owned instance of the planned class named after the source that already sits at the
        /// placement: what a version 1 save of a declared script restores, since version 1 keeps the node but not its
        /// binding. Binding it keeps one script per source instead of a second one next to it.
        /// </summary>
        private RbxLuaSourceContainer FindAdoptable(RbxInstance parent, string sourceId, string className)
        {
            IReadOnlyList<RbxInstance> children = parent.GetChildren();
            for (int index = 0; index < children.Count; index++)
            {
                if (children[index] is RbxLuaSourceContainer container
                    && !container.IsBound
                    && string.Equals(container.ClassName, className, StringComparison.Ordinal)
                    && string.Equals(container.Name, sourceId, StringComparison.Ordinal)
                    && IsWorldOwned(container))
                {
                    return container;
                }
            }

            return null;
        }

        private bool IsWorldOwned(RbxInstance instance)
        {
            return _registry.TryGetRecord(instance.Id, out InstanceRecord record)
                   && !record.IsRuntimeInfrastructure
                   && string.IsNullOrEmpty(record.OwnerModId);
        }

        /// <summary>
        /// True when <paramref name="parent"/> is exactly the default placement <paramref name="path"/>: the container
        /// service of the first entry directly under <c>game</c>, then Folders named by the later entries.
        /// </summary>
        private bool IsAt(RbxInstance parent, IReadOnlyList<string> path)
        {
            if (parent == null || path == null || path.Count == 0)
            {
                return false;
            }

            RbxInstance node = parent;
            for (int index = path.Count - 1; index >= 1; index--)
            {
                if (node == null
                    || !string.Equals(node.ClassName, FolderClassName, StringComparison.Ordinal)
                    || !string.Equals(node.Name, path[index], StringComparison.Ordinal))
                {
                    return false;
                }

                node = node.Parent;
            }

            return node != null
                   && string.Equals(node.ClassName, path[0], StringComparison.Ordinal)
                   && ReferenceEquals(node.Parent, _game);
        }

        private static bool SamePath(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private int MoveChildren(RbxInstance from, RbxInstance skip, RbxInstance to, string sourceId,
            bool keepOwnObjects)
        {
            if (from == null || from.IsDestroyed)
            {
                return 0;
            }

            int moved = 0;
            List<RbxInstance> children = new List<RbxInstance>(from.GetChildren());
            for (int index = 0; index < children.Count; index++)
            {
                RbxInstance child = children[index];
                if (ReferenceEquals(child, skip) || child.IsDestroyed)
                {
                    continue;
                }

                bool ownedByTheMod = _registry.TryGetRecord(child.Id, out InstanceRecord record)
                                     && string.Equals(record.OwnerModId, sourceId, StringComparison.Ordinal);
                if (!keepOwnObjects && ownedByTheMod)
                {
                    continue;
                }

                child.Parent = to;
                moved++;
            }

            return moved;
        }

        private void DestroyEmptyFolders(List<RbxInstance> createdFolders)
        {
            if (createdFolders == null)
            {
                return;
            }

            for (int index = createdFolders.Count - 1; index >= 0; index--)
            {
                RbxInstance folder = createdFolders[index];
                if (!folder.IsDestroyed && folder.GetChildren().Count == 0)
                {
                    folder.Destroy();
                }
            }
        }

        private void Unbind(string sourceId, RbxLuaSourceContainer script)
        {
            if (_bound.TryGetValue(sourceId, out RbxLuaSourceContainer indexed) && ReferenceEquals(indexed, script))
            {
                _bound.Remove(sourceId);
            }

            if (!script.IsDestroyed)
            {
                script.SourceId = string.Empty;
            }
        }

        private static int RunContextOf(RbxLuaSourceContainer script)
        {
            return script is RbxBaseScript baseScript ? baseScript.RunContext : RunContextValues.Legacy;
        }

        private static void SetRunContext(RbxLuaSourceContainer script, int runContext)
        {
            if (script is RbxBaseScript baseScript && baseScript.RunContext != runContext)
            {
                baseScript.RunContext = runContext;
            }
        }

        private static void RestoreRunContext(RbxLuaSourceContainer script, int runContext)
        {
            if (!script.IsDestroyed)
            {
                SetRunContext(script, runContext);
            }
        }

        private static int SiblingIndexOf(RbxInstance instance)
        {
            RbxInstance parent = instance.Parent;
            if (parent == null)
            {
                return -1;
            }

            IReadOnlyList<RbxInstance> siblings = parent.GetChildren();
            for (int index = 0; index < siblings.Count; index++)
            {
                if (ReferenceEquals(siblings[index], instance))
                {
                    return index;
                }
            }

            return -1;
        }

        private void EnsureIndex()
        {
            if (!_indexStale)
            {
                return;
            }

            _indexStale = false;
            _bound.Clear();
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (!(live[index] is RbxLuaSourceContainer container) || !container.IsBound || container.IsDestroyed
                    || !IsWorldOwned(container))
                {
                    continue;
                }

                if (_bound.TryGetValue(container.SourceId, out RbxLuaSourceContainer first))
                {
                    // WHY not refused here: the package reader refuses a duplicate binding, so one can only come from
                    // a runtime defect; the first instance keeps the binding and the defect is reported once.
                    if (_loggedDuplicates.Add(container.SourceId))
                    {
                        _log?.Invoke("[CoreAI.RbxApi] mod '" + container.SourceId + "' is bound to two script "
                                     + "instances, ids " + first.Id.Value + " and " + container.Id.Value
                                     + "; the first keeps the binding.");
                    }

                    continue;
                }

                _bound.Add(container.SourceId, container);
            }
        }

        private void OnRegistered(InstanceRecord record)
        {
            if (record.Instance is RbxLuaSourceContainer && !record.IsRuntimeInfrastructure
                                                        && string.IsNullOrEmpty(record.OwnerModId))
            {
                _indexStale = true;
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (!(record.Instance is RbxLuaSourceContainer container) || !container.IsBound
                || !_bound.TryGetValue(container.SourceId, out RbxLuaSourceContainer indexed)
                || !ReferenceEquals(indexed, container))
            {
                return;
            }

            _bound.Remove(container.SourceId);
            Raise(container.SourceId, ScriptInstanceChange.Destroyed);
        }

        private void Raise(string sourceId, ScriptInstanceChange change)
        {
            Action<string, ScriptInstanceChange> handlers = ScriptInstanceChanged;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string, ScriptInstanceChange>)handler)(sourceId, change);
                }
                catch (Exception exception)
                {
                    _log?.Invoke("[CoreAI.RbxApi] a ScriptInstanceChanged subscriber failed for mod '" + sourceId
                                 + "' (" + change + "): " + exception.Message);
                }
            }
        }
    }
}
