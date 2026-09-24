using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Registry-level tag store — the CollectionService substrate (R6.8). Instance:AddTag/
    /// RemoveTag/HasTag/GetTags delegate here from MVP1; CollectionService:GetTagged and the
    /// add/remove signals layer on top in MVP8 without a storage change.
    /// </summary>
    public sealed class InstanceTagStore
    {
        private static readonly string[] EmptyTags = new string[0];

        private readonly Dictionary<string, HashSet<InstanceId>> _byTag = new(StringComparer.Ordinal);
        private readonly Dictionary<InstanceId, HashSet<string>> _byInstance = new();

        public void AddTag(InstanceId id, string tag)
        {
            ValidateTag(tag);
            if (!_byTag.TryGetValue(tag, out HashSet<InstanceId> ids))
            {
                ids = new HashSet<InstanceId>();
                _byTag.Add(tag, ids);
            }

            ids.Add(id);

            if (!_byInstance.TryGetValue(id, out HashSet<string> tags))
            {
                tags = new HashSet<string>(StringComparer.Ordinal);
                _byInstance.Add(id, tags);
            }

            tags.Add(tag);
        }

        public void RemoveTag(InstanceId id, string tag)
        {
            ValidateTag(tag);
            if (_byTag.TryGetValue(tag, out HashSet<InstanceId> ids))
            {
                ids.Remove(id);
                if (ids.Count == 0)
                {
                    _byTag.Remove(tag);
                }
            }

            if (_byInstance.TryGetValue(id, out HashSet<string> tags))
            {
                tags.Remove(tag);
                if (tags.Count == 0)
                {
                    _byInstance.Remove(id);
                }
            }
        }

        public bool HasTag(InstanceId id, string tag)
        {
            ValidateTag(tag);
            return _byInstance.TryGetValue(id, out HashSet<string> tags) && tags.Contains(tag);
        }

        /// <summary>Sorted for deterministic enumeration and snapshot stability.</summary>
        public IReadOnlyList<string> GetTags(InstanceId id)
        {
            if (!_byInstance.TryGetValue(id, out HashSet<string> tags))
            {
                return EmptyTags;
            }

            List<string> result = new(tags);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>CollectionService:GetTagged substrate (ids; MVP8 resolves them to instances).</summary>
        public IReadOnlyList<InstanceId> GetTagged(string tag)
        {
            ValidateTag(tag);
            if (!_byTag.TryGetValue(tag, out HashSet<InstanceId> ids))
            {
                return new InstanceId[0];
            }

            List<InstanceId> result = new(ids);
            result.Sort();
            return result;
        }

        /// <summary>
        /// Every tag currently held by any registered instance, inside the DataModel or not, sorted
        /// for deterministic enumeration. CollectionService:GetAllTags is narrower: it lists only
        /// tags with a holder inside the DataModel, and walks these to seed its counts.
        /// </summary>
        public IReadOnlyList<string> GetAllTags()
        {
            List<string> result = new(_byTag.Keys);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Whether any registered instance holds the tag, inside the DataModel or not: the
        /// store-wide first-use/last-use flag the registry passes with
        /// <see cref="InstanceRegistry.TagAdded"/> and <see cref="InstanceRegistry.TagRemoved"/>.
        /// </summary>
        /// <remarks>
        /// The flag is informational. CollectionService does not fire its TagAdded/TagRemoved
        /// from it: it keeps its own count of holders inside the DataModel and fires on that
        /// count's first and last holder (M8-10), because the mirror counts only instances in the
        /// DataModel, and a tagged nil-parented instance (a pooled coin) holds the tag here while
        /// counting for nothing there.
        /// </remarks>
        public bool IsTagInUse(string tag)
        {
            ValidateTag(tag);
            return _byTag.ContainsKey(tag);
        }

        /// <summary>Destroy sweep: drops every tag held by the instance (R6.2 cleanup).</summary>
        public void ClearInstance(InstanceId id)
        {
            if (!_byInstance.TryGetValue(id, out HashSet<string> tags))
            {
                return;
            }

            foreach (string tag in tags)
            {
                if (_byTag.TryGetValue(tag, out HashSet<InstanceId> ids))
                {
                    ids.Remove(id);
                    if (ids.Count == 0)
                    {
                        _byTag.Remove(tag);
                    }
                }
            }

            _byInstance.Remove(id);
        }

        private static void ValidateTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                throw RbxError.BadArgument("tag must be a non-empty string",
                    "pass a tag name like \"KillBrick\" at argument 1");
            }
        }
    }
}
