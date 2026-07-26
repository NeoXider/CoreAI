using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Captures and restores instance subtrees with stable ids (roadmap §2, world file; Q3
    /// resolved: no remap table, ever). Capture is deterministic — preorder tree walk, sorted
    /// attributes and tags — so capture→restore→capture is byte-identical. The MVP3 world-file
    /// serializer encodes these DTOs via RbxJson; this class owns only the tree↔DTO mapping.
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

            InstanceTreeSnapshot snapshot = new();
            CaptureNode(root, 0UL, snapshot.Instances);
            return snapshot;
        }

        private static void CaptureNode(RbxInstance instance, ulong parentId,
            List<InstanceSnapshot> output)
        {
            InstanceRecord record = null;
            instance.Registry?.TryGetRecord(instance.Id, out record);

            InstanceSnapshot node = new()
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
            AttributeSnapshot snapshot = new() { Name = name };
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
                case RbxVector3 v3:
                    snapshot.Kind = AttributeValueKind.Vector3;
                    snapshot.StringValue = Join(v3.X, v3.Y, v3.Z);
                    break;
                case RbxVector2 v2:
                    snapshot.Kind = AttributeValueKind.Vector2;
                    snapshot.StringValue = Join(v2.X, v2.Y);
                    break;
                case RbxColor3 c:
                    snapshot.Kind = AttributeValueKind.Color3;
                    snapshot.StringValue = Join(c.R, c.G, c.B);
                    break;
                case RbxUDim u:
                    snapshot.Kind = AttributeValueKind.UDim;
                    snapshot.StringValue = F(u.Scale) + "," + u.Offset.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    throw RbxError.BadArgument(
                        "attribute '" + name + "' holds a non-serializable value",
                        "store only string/boolean/number/Vector3/Vector2/Color3/UDim attributes");
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

            Dictionary<ulong, RbxInstance> restored = new();
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
                case AttributeValueKind.Number: return attribute.NumberValue;
                case AttributeValueKind.Vector3:
                {
                    float[] p = Parse(attribute.StringValue, 3);
                    return new RbxVector3(p[0], p[1], p[2]);
                }
                case AttributeValueKind.Vector2:
                {
                    float[] p = Parse(attribute.StringValue, 2);
                    return new RbxVector2(p[0], p[1]);
                }
                case AttributeValueKind.Color3:
                {
                    float[] p = Parse(attribute.StringValue, 3);
                    return new RbxColor3(p[0], p[1], p[2]);
                }
                case AttributeValueKind.UDim:
                {
                    float[] p = Parse(attribute.StringValue, 2);
                    return new RbxUDim(p[0], (int)p[1]);
                }
                default: return attribute.NumberValue;
            }
        }

        // ---- Datatype attribute string codec (stable, invariant-culture) --------------------

        private static string Join(params float[] components)
        {
            string[] parts = new string[components.Length];
            for (int i = 0; i < components.Length; i++)
            {
                parts[i] = F(components[i]);
            }

            return string.Join(",", parts);
        }

        /// <summary>Round-trippable float format so restore reproduces the captured value exactly.</summary>
        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float[] Parse(string serialized, int expected)
        {
            string[] parts = (serialized ?? string.Empty).Split(',');
            if (parts.Length != expected)
            {
                throw RbxError.BadArgument(
                    "datatype attribute value '" + serialized + "' is malformed",
                    "expected " + expected + " comma-separated components");
            }

            float[] result = new float[expected];
            for (int i = 0; i < expected; i++)
            {
                result[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            }

            return result;
        }
    }
}
