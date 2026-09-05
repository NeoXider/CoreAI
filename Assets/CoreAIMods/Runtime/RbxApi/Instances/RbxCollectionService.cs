using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox CollectionService: tag-based instance collections over <see cref="InstanceTagStore"/>
    /// (R6.8). No storage of its own — every query and transition resolves through the registry
    /// tag store, so Instance:AddTag/RemoveTag and CollectionService:AddTag/RemoveTag share one
    /// substrate and one signal layer. Mirror-pinned semantics: GetTagged returns DataModel
    /// descendants only with no ordering promise; a duplicate AddTag does nothing and fires
    /// nothing ("doing nothing if the tag is already applied to that instance"); the per-tag
    /// added/removed signals fire only on later changes, never for instances that already carry
    /// the tag ("thus won't fire the event if they already are in the DataModel"); TagAdded
    /// fires only when the added tag is the only occurrence in the place, TagRemoved only when
    /// the removed tag is used nowhere afterwards. First/last-use is tracked at store level
    /// (OURS — the mirror does not say whether out-of-tree holders count as "in the place").
    /// Deprecated GetCollection/ItemAdded/ItemRemoved are absent, not stubbed.
    /// </summary>
    public sealed class RbxCollectionService : RbxInstance
    {
        private readonly Dictionary<string, RbxScriptSignal> _addedSignals =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, RbxScriptSignal> _removedSignals =
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
        /// Mirror TagAdded: fires with the tag string when a tag is added to an instance and the
        /// added tag is the only occurrence of that tag in the place.
        /// </summary>
        public RbxScriptSignal TagAdded => _tagAdded;

        /// <summary>
        /// Mirror TagRemoved: fires with the tag string when a tag is removed from an instance
        /// and the removed tag is no longer used anywhere in the place.
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

        /// <summary>Every tag currently held by any instance, sorted.</summary>
        public IReadOnlyList<string> GetAllTags()
        {
            InstanceRegistry registry = Registry;
            if (registry == null)
            {
                throw RbxError.BadArgument(
                    "CollectionService:GetAllTags cannot search: the service is not attached to a world",
                    "resolve it via game:GetService(\"CollectionService\")");
            }

            return registry.Tags.GetAllTags();
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
            }

            _scheduler = null;
        }

        private void OnRegistryTagAdded(RbxInstance instance, string tag, bool isFirstPlaceUse)
        {
            if (isFirstPlaceUse)
            {
                _tagAdded.Fire(tag);
            }

            if (IsInTree(instance)
                && _addedSignals.TryGetValue(tag, out RbxScriptSignal signal))
            {
                signal.Fire(instance);
            }
        }

        private void OnRegistryTagRemoved(RbxInstance instance, string tag, bool isLastPlaceUse)
        {
            if (isLastPlaceUse)
            {
                _tagRemoved.Fire(tag);
            }

            if (IsInTree(instance)
                && _removedSignals.TryGetValue(tag, out RbxScriptSignal signal))
            {
                signal.Fire(instance);
            }
        }

        private void OnSceneMembershipChanged(RbxInstance root, bool entered)
        {
            // WHY: DescendantAdded/Removing fire for within-tree moves too and cannot tell a
            // boundary crossing at fire time; the registry membership flip is the exact enter/exit
            // signal, so only these transitions fire the per-tag signals (usage — and therefore
            // the globals — is unchanged by a move).
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
                        if (_addedSignals.TryGetValue(tag, out RbxScriptSignal added))
                        {
                            added.Fire(nodes[nodeIndex]);
                        }
                    }
                    else if (_removedSignals.TryGetValue(tag, out RbxScriptSignal removed))
                    {
                        removed.Fire(nodes[nodeIndex]);
                    }
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
