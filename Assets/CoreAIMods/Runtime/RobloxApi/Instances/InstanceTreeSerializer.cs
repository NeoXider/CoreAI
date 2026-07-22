using System.Collections.Generic;

namespace CoreAI.RobloxApi.Instances
{
    /// <summary>
    /// Captures and restores instance subtrees with stable ids (roadmap §2, world file; Q3
    /// resolved: no remap table, ever). Capture is deterministic — preorder tree walk, sorted
    /// attributes and tags — so capture→restore→capture is byte-identical. The MVP3 world-file
    /// serializer encodes these DTOs via RobloxJson; this class owns only the tree↔DTO mapping.
    /// </summary>
    public static class InstanceTreeSerializer
    {
        /// <summary>Captures <paramref name="root"/> and its whole subtree in preorder.</summary>
        public static InstanceTreeSnapshot Capture(RbxInstance root)
        {
            if (root == null)
            {
                throw RbxError.BadArgument("cannot capture a nil root", "pass a live instance");
            }

            var snapshot = new InstanceTreeSnapshot();
            CaptureNode(root, 0UL, snapshot.Instances);
            return snapshot;
        }

        private static void CaptureNode(RbxInstance instance, ulong parentId,
            List<InstanceSnapshot> output)
        {
            InstanceRecord record = null;
            instance.Registry?.TryGetRecord(instance.Id, out record);

            var node = new InstanceSnapshot
            {
                Id = instance.Id.Value,
                ParentId = parentId,
                ClassName = instance.ClassName,
                Name = instance.Name,
                Archivable = instance.Archivable,
                OwnerModId = record?.OwnerModId,
                OriginTag = record?.OriginTag
            };

            foreach (string tag in instance.GetTags())
            {
                node.Tags.Add(tag);
            }

            foreach (KeyValuePair<string, object> attribute in
                     AttributeContract.Sorted(instance.GetAttributes()))
            {
                node.Attributes.Add(ToAttributeSnapshot(attribute.Key, attribute.Value));
            }

            output.Add(node);
            foreach (RbxInstance child in instance.GetChildren())
            {
                CaptureNode(child, instance.Id.Value, output);
            }
        }

        private static AttributeSnapshot ToAttributeSnapshot(string name, object value)
        {
            var snapshot = new AttributeSnapshot { Name = name };
            switch (value)
            {
                case string s:
                    snapshot.Kind = AttributeValueKind.String;
                    snapshot.StringValue = s;
                    break;
                case bool b:
                    snapshot.Kind = AttributeValueKind.Bool;
                    snapshot.BoolValue = b;
                    break;
                case double d:
                    snapshot.Kind = AttributeValueKind.Number;
                    snapshot.NumberValue = d;
                    break;
                default:
                    throw RbxError.BadArgument(
                        "attribute '" + name + "' holds a non-serializable value",
                        "store only string/boolean/number attributes in MVP1");
            }

            return snapshot;
        }

        /// <summary>
        /// Restores a captured subtree into <paramref name="registry"/> under the original ids
        /// and returns the restored root. Two passes: create all nodes, then link parents, so
        /// snapshot ordering is not load-bearing.
        /// </summary>
        public static RbxInstance Restore(InstanceTreeSnapshot snapshot, InstanceRegistry registry)
        {
            if (snapshot == null || snapshot.Instances == null || snapshot.Instances.Count == 0)
            {
                throw RbxError.BadArgument("cannot restore an empty snapshot",
                    "capture a subtree with InstanceTreeSerializer.Capture first");
            }

            var restored = new Dictionary<ulong, RbxInstance>();
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                RbxInstance instance = registry.RestoreInstance(node.ClassName,
                    new InstanceId(node.Id), node.OwnerModId, node.OriginTag);
                instance.Name = node.Name;
                instance.Archivable = node.Archivable;
                foreach (AttributeSnapshot attribute in node.Attributes)
                {
                    instance.SetAttribute(attribute.Name, FromAttributeSnapshot(attribute));
                }

                foreach (string tag in node.Tags)
                {
                    instance.AddTag(tag);
                }

                restored.Add(node.Id, instance);
            }

            RbxInstance root = null;
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                RbxInstance instance = restored[node.Id];
                if (node.ParentId == 0UL || !restored.TryGetValue(node.ParentId, out RbxInstance parent))
                {
                    root ??= instance;
                    continue;
                }

                instance.Parent = parent;
            }

            return root;
        }

        private static object FromAttributeSnapshot(AttributeSnapshot attribute)
        {
            switch (attribute.Kind)
            {
                case AttributeValueKind.String: return attribute.StringValue;
                case AttributeValueKind.Bool: return attribute.BoolValue;
                default: return attribute.NumberValue;
            }
        }
    }
}
