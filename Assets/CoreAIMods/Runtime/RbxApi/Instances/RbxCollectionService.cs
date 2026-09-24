using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox CollectionService: tag-based instance collections over <see cref="InstanceTagStore"/>
    /// (R6.8). Tags live only in the registry tag store, so Instance:AddTag/RemoveTag and
    /// CollectionService:AddTag/RemoveTag share one substrate and one signal layer; the service
    /// keeps nothing but a per-tag count of holders inside the DataModel. Mirror-pinned semantics:
    /// GetTagged returns DataModel descendants only with no ordering promise; a duplicate AddTag
    /// does nothing and fires nothing ("doing nothing if the tag is already applied to that
    /// instance"); the per-tag added/removed signals fire only on later changes, never for
    /// instances that already carry the tag ("thus won't fire the event if they already are in
    /// the DataModel"). TagAdded, TagRemoved and GetAllTags count only instances inside the
    /// DataModel (CollectionService.yaml): TagAdded fires when a tag goes from no holder in the
    /// DataModel to one — AddTag on an in-tree instance, or a tagged instance entering the tree —
    /// and TagRemoved when the last holder in the DataModel loses the tag or leaves the tree. An
    /// instance parented to nil keeps its tags but counts toward none of the three.
    /// Deprecated GetCollection/ItemAdded/ItemRemoved are absent, not stubbed.
    /// </summary>
    public sealed class RbxCollectionService : RbxInstance
    {
        private readonly Dictionary<string, RbxScriptSignal> _addedSignals =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, RbxScriptSignal> _removedSignals =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _inTreeHolderCounts =
            new(StringComparer.Ordinal);
        private readonly RbxScriptSignal _tagAdded;
        private readonly RbxScriptSignal _tagRemoved;
        private ModScheduler _scheduler;
        private InstanceRegistry _subscribedRegistry;

        internal RbxCollectionService(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "CollectionService";
            _tagAdded = new RbxScriptSignal("CollectionService.TagAdded");
            _tagRemoved = new RbxScriptSignal("CollectionService.TagRemoved");
        }

        /// <summary>
        /// Mirror TagAdded: fires with the tag string when the tag goes from being carried by no
        /// instance inside the DataModel to being carried by one — AddTag on an in-tree instance,
        /// or a tagged instance entering the tree. Once per tag lifetime, not once per instance;
        /// tagging a nil-parented instance fires nothing until it enters the tree.
        /// </summary>
        public RbxScriptSignal TagAdded => _tagAdded;

        /// <summary>
        /// Mirror TagRemoved: fires with the tag string when the last instance inside the
        /// DataModel that carries the tag has it removed or leaves the tree (parented to nil or
        /// destroyed), so no instance in the DataModel carries it any more.
        /// </summary>
        public RbxScriptSignal TagRemoved => _tagRemoved;

        /// <summary>
        /// Applies a tag to an instance; a duplicate add is a store-level no-op that fires
        /// nothing. Authorization runs at the Lua boundary (metadata mutation on the target).
        /// </summary>
        public void AddTag(RbxInstance instance, string tag)
        {
            if (instance == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:AddTag expects an Instance at argument 1",
                    "pass an Instance, e.g. CollectionService:AddTag(part, \"KillBrick\")");
            }

            instance.AddTag(tag);
        }

        /// <summary>
        /// Removes a tag from an instance; removing a tag never held changes nothing and fires
        /// nothing. Authorization runs at the Lua boundary (metadata mutation on the target).
        /// </summary>
        public void RemoveTag(RbxInstance instance, string tag)
        {
            if (instance == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:RemoveTag expects an Instance at argument 1",
                    "pass an Instance, e.g. CollectionService:RemoveTag(part, \"KillBrick\")");
            }

            instance.RemoveTag(tag);
        }

        /// <summary>Whether the instance currently holds the tag.</summary>
        public bool HasTag(RbxInstance instance, string tag)
        {
            if (instance == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:HasTag expects an Instance at argument 1",
                    "pass an Instance, e.g. CollectionService:HasTag(part, \"KillBrick\")");
            }

            return instance.HasTag(tag);
        }

        /// <summary>All tags currently applied to the instance, sorted.</summary>
        public IReadOnlyList<string> GetTags(RbxInstance instance)
        {
            if (instance == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:GetTags expects an Instance at argument 1",
                    "pass an Instance, e.g. CollectionService:GetTags(part)");
            }

            return instance.GetTags();
        }

        /// <summary>
        /// Every live instance holding the tag that is a descendant of the DataModel. Tagged
        /// instances parented to nil (or under no scene) are excluded; destroyed instances are
        /// gone from the registry and never returned. No ordering is promised.
        /// </summary>
        public IReadOnlyList<RbxInstance> GetTagged(string tag)
        {
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:GetTagged cannot search: the service is not attached to a world",
                    "resolve it via game:GetService(\"CollectionService\")");
            }

            IReadOnlyList<InstanceId> ids = registry.Tags.GetTagged(tag);
            List<RbxInstance> result = new(ids.Count);
            for (int index = 0; index < ids.Count; index++)
            {
                if (registry.TryGet(ids[index], out RbxInstance instance)
                    && !instance.IsDestroyed
                    && registry.IsInScene(instance))
                {
                    result.Add(instance);
                }
            }

            return result;
        }

        /// <summary>
        /// Every tag carried by at least one instance inside the DataModel, sorted (the mirror
        /// promises no order). A tag held only by nil-parented instances is absent, and a tag
        /// leaves the result as soon as its last holder in the DataModel leaves the tree.
        /// </summary>
        public IReadOnlyList<string> GetAllTags()
        {
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:GetAllTags cannot search: the service is not attached to a world",
                    "resolve it via game:GetService(\"CollectionService\")");
            }

            Dictionary<string, int> counts = _inTreeHolderCounts;
            if (!ReferenceEquals(_subscribedRegistry, registry))
            {
                // WHY: the running counts are kept current only by the registry subscriptions, so
                // a service that is not subscribed to this registry counts the tree afresh.
                counts = new Dictionary<string, int>(StringComparer.Ordinal);
                CountInTreeHolders(registry, counts);
            }

            List<string> result = new(counts.Keys);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// The per-tag added signal: fires with the instance when the tag is assigned to an
        /// in-tree instance, or when a tagged instance enters the tree. Repeated calls with the
        /// same tag return the same signal object.
        /// </summary>
        public RbxScriptSignal GetInstanceAddedSignal(string tag)
        {
            ValidateSignalTag(tag, "GetInstanceAddedSignal");
            if (!_addedSignals.TryGetValue(tag, out RbxScriptSignal signal))
            {
                signal = new RbxScriptSignal("CollectionService.GetInstanceAddedSignal(" + tag + ")");
                BindSignal(signal);
                _addedSignals.Add(tag, signal);
            }

            return signal;
        }

        /// <summary>
        /// The per-tag removed signal: fires with the instance when the tag is removed from an
        /// in-tree instance, or when a tagged instance leaves the tree. Repeated calls with the
        /// same tag return the same signal object.
        /// </summary>
        public RbxScriptSignal GetInstanceRemovedSignal(string tag)
        {
            ValidateSignalTag(tag, "GetInstanceRemovedSignal");
            if (!_removedSignals.TryGetValue(tag, out RbxScriptSignal signal))
            {
                signal = new RbxScriptSignal(
                    "CollectionService.GetInstanceRemovedSignal(" + tag + ")");
                BindSignal(signal);
                _removedSignals.Add(tag, signal);
            }

            return signal;
        }

        /// <summary>
        /// Attaches the registry tag-transition subscriptions; safe to call again (a snapshot
        /// restore replaces the service instance, and the next call re-attaches through here).
        /// </summary>
        internal void AttachHost(ModScheduler scheduler)
        {
            if (scheduler == null)
            {
                throw new ArgumentNullException(nameof(scheduler));
            }

            InstanceRegistry registry = Registry;
            if (registry != null && !ReferenceEquals(_subscribedRegistry, registry))
            {
                DetachHost();
                registry.TagAdded += OnRegistryTagAdded;
                registry.TagRemoved += OnRegistryTagRemoved;
                registry.SceneMembershipChanged += OnSceneMembershipChanged;
                _subscribedRegistry = registry;
                // WHY: tags applied before this subscription (a world built or restored before
                // the bindings attached) raised no event here, so the counts start from the tree
                // as it stands and the subscriptions keep them current from now on.
                CountInTreeHolders(registry, _inTreeHolderCounts);
            }

            _scheduler = scheduler;
            _tagAdded.BindScheduler(scheduler);
            _tagRemoved.BindScheduler(scheduler);
        }

        /// <summary>Attaches when the scheduler host is missing or replaced; otherwise a no-op.</summary>
        internal void EnsureHost(ModScheduler scheduler)
        {
            if (_scheduler == null || !ReferenceEquals(_scheduler, scheduler)
                || _subscribedRegistry == null
                || !ReferenceEquals(_subscribedRegistry, Registry))
            {
                AttachHost(scheduler);
            }
        }

        /// <summary>Releases the registry subscriptions.</summary>
        internal void DetachHost()
        {
            if (_subscribedRegistry != null)
            {
                _subscribedRegistry.TagAdded -= OnRegistryTagAdded;
                _subscribedRegistry.TagRemoved -= OnRegistryTagRemoved;
                _subscribedRegistry.SceneMembershipChanged -= OnSceneMembershipChanged;
                _subscribedRegistry = null;
                _inTreeHolderCounts.Clear();
            }

            _scheduler = null;
        }

        private void OnRegistryTagAdded(RbxInstance instance, string tag, bool isFirstPlaceUse)
        {
            // WHY the store-wide first-use flag is ignored: the mirror counts only instances
            // inside the DataModel, so tagging a nil-parented instance (a pooled coin) changes
            // neither the placewide signal nor the per-tag one until that instance enters the tree.
            if (!IsInTree(instance))
            {
                return;
            }

            if (AddInTreeHolder(tag))
            {
                _tagAdded.Fire(tag);
            }

            if (_addedSignals.TryGetValue(tag, out RbxScriptSignal signal))
            {
                signal.Fire(instance);
            }
        }

        private void OnRegistryTagRemoved(RbxInstance instance, string tag, bool isLastPlaceUse)
        {
            // WHY: a holder outside the DataModel was already uncounted when it left the tree (the
            // destroy sweep's tag clearing lands here after the detach), so it moves nothing.
            if (!IsInTree(instance))
            {
                return;
            }

            if (RemoveInTreeHolder(tag))
            {
                _tagRemoved.Fire(tag);
            }

            if (_removedSignals.TryGetValue(tag, out RbxScriptSignal signal))
            {
                signal.Fire(instance);
            }
        }

        private void OnSceneMembershipChanged(RbxInstance root, bool entered)
        {
            // WHY: DescendantAdded/Removing fire for within-tree moves too and cannot tell a
            // boundary crossing at fire time; the registry membership flip is the exact enter/exit
            // signal, so only these transitions fire the per-tag signals and move the in-DataModel
            // counts behind TagAdded/TagRemoved (a move within the tree changes neither).
            List<RbxInstance> nodes = new() { root };
            if (!root.IsDestroyed)
            {
                nodes.AddRange(root.GetDescendants());
            }

            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                return;
            }

            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                IReadOnlyList<string> tags = registry.Tags.GetTags(nodes[nodeIndex].Id);
                for (int tagIndex = 0; tagIndex < tags.Count; tagIndex++)
                {
                    string tag = tags[tagIndex];
                    if (entered)
                    {
                        if (AddInTreeHolder(tag))
                        {
                            _tagAdded.Fire(tag);
                        }

                        if (_addedSignals.TryGetValue(tag, out RbxScriptSignal added))
                        {
                            added.Fire(nodes[nodeIndex]);
                        }
                    }
                    else
                    {
                        if (RemoveInTreeHolder(tag))
                        {
                            _tagRemoved.Fire(tag);
                        }

                        if (_removedSignals.TryGetValue(tag, out RbxScriptSignal removed))
                        {
                            removed.Fire(nodes[nodeIndex]);
                        }
                    }
                }
            }
        }

        /// <summary>Counts one more holder of the tag inside the DataModel; true when it is the
        /// first.</summary>
        private bool AddInTreeHolder(string tag)
        {
            _inTreeHolderCounts.TryGetValue(tag, out int count);
            _inTreeHolderCounts[tag] = count + 1;
            return count == 0;
        }

        /// <summary>Counts one fewer holder of the tag inside the DataModel; true when it was the
        /// last.</summary>
        private bool RemoveInTreeHolder(string tag)
        {
            if (!_inTreeHolderCounts.TryGetValue(tag, out int count))
            {
                return false;
            }

            if (count <= 1)
            {
                _inTreeHolderCounts.Remove(tag);
                return true;
            }

            _inTreeHolderCounts[tag] = count - 1;
            return false;
        }

        /// <summary>Fills <paramref name="counts"/> with the number of live holders of each tag
        /// inside the DataModel; tags no in-tree instance carries are left out.</summary>
        private static void CountInTreeHolders(InstanceRegistry registry, Dictionary<string, int> counts)
        {
            counts.Clear();
            IReadOnlyList<string> tags = registry.Tags.GetAllTags();
            for (int tagIndex = 0; tagIndex < tags.Count; tagIndex++)
            {
                IReadOnlyList<InstanceId> holders = registry.Tags.GetTagged(tags[tagIndex]);
                int inTree = 0;
                for (int holderIndex = 0; holderIndex < holders.Count; holderIndex++)
                {
                    if (registry.TryGet(holders[holderIndex], out RbxInstance holder)
                        && !holder.IsDestroyed
                        && registry.IsInScene(holder))
                    {
                        inTree++;
                    }
                }

                if (inTree > 0)
                {
                    counts.Add(tags[tagIndex], inTree);
                }
            }
        }

        private bool IsInTree(RbxInstance instance)
        {
            InstanceRegistry registry = Registry;
            return registry != null && instance != null && registry.IsInScene(instance);
        }

        private void BindSignal(RbxScriptSignal signal)
        {
            if (_scheduler != null)
            {
                signal.BindScheduler(_scheduler);
            }
        }

        private static void ValidateSignalTag(string tag, string member)
        {
            if (string.IsNullOrEmpty(tag))
            {
                throw RbxError.BadArgument(
                    "CollectionService:" + member + " expects a non-empty tag at argument 1",
                    "pass a tag name like \"KillBrick\" at argument 1");
            }
        }
    }
}
