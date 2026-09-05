using System;
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
        private readonly struct CaptureFrame
        {
            public CaptureFrame(RbxInstance instance, ulong parentId, int depth)
            {
                Instance = instance;
                ParentId = parentId;
                Depth = depth;
            }

            public RbxInstance Instance { get; }

            public ulong ParentId { get; }

            public int Depth { get; }
        }

        public const int MaximumSnapshotInstances = 100000;
        public const int MaximumSnapshotDepth = 2048;
        public const int MaximumAttributesPerInstance = 256;
        public const int MaximumTagsPerInstance = 256;

        /// <summary>Captures <paramref name="root"/> and its whole subtree in preorder.</summary>
        public static InstanceTreeSnapshot Capture(RbxInstance root)
        {
            if (root == null)
            {
                throw RbxError.BadArgument("cannot capture a nil root", "pass a live instance");
            }

            InstanceTreeSnapshot snapshot = new()
            {
                WorldAclVersion = root.Registry?.WorldAclVersion
            };
            Stack<CaptureFrame> pending = new();
            pending.Push(new CaptureFrame(root, 0UL, 1));
            while (pending.Count > 0)
            {
                CaptureFrame frame = pending.Pop();
                CaptureNode(frame.Instance, frame.ParentId, frame.Depth, snapshot.Instances);
                IReadOnlyList<RbxInstance> children = frame.Instance.GetChildren();
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    pending.Push(new CaptureFrame(
                        children[index], frame.Instance.Id.Value, frame.Depth + 1));
                }
            }

            return snapshot;
        }

        private static void CaptureNode(RbxInstance instance, ulong parentId, int depth,
            List<InstanceSnapshot> output)
        {
            if (depth > MaximumSnapshotDepth)
            {
                throw RbxError.BadArgument(
                    "live instance hierarchy exceeds depth limit " + MaximumSnapshotDepth,
                    "flatten the instance hierarchy before capture");
            }

            if (output.Count >= MaximumSnapshotInstances)
            {
                throw RbxError.BadArgument(
                    "live instance tree exceeds instance limit " + MaximumSnapshotInstances,
                    "split the world or reduce the live instance count before capture");
            }

            if (!instance.Id.IsServerAssigned)
            {
                throw RbxError.BadArgument(
                    "locally-assigned instance id " + instance.Id.Value
                    + " cannot be captured in a world file",
                    "capture only server-authority instances");
            }

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
                OriginTag = record?.OriginTag,
                OwnerActorId = record?.OwnerActorId,
                AccessScope = record?.AccessScope,
                Revision = record?.Revision ?? 0L
            };

            if (instance is RbxModel model)
            {
                node.Model = new ModelSnapshot
                {
                    PrimaryPartId = model.PrimaryPart?.Id.Value ?? 0UL,
                    HasStoredWorldPivot = model.HasStoredWorldPivot,
                    StoredWorldPivot = model.HasStoredWorldPivot
                        ? Join(model.StoredWorldPivot.GetComponents())
                        : null
                };
            }

            if (instance is RbxClickDetector clickDetector)
            {
                node.ClickDetector = new ClickDetectorSnapshot
                {
                    MaxActivationDistance = clickDetector.MaxActivationDistance.ToString(
                        "R", CultureInfo.InvariantCulture)
                };
            }

            if (instance is RbxMaterialVariant materialVariant)
            {
                node.MaterialVariant = new MaterialVariantSnapshot
                {
                    BaseMaterial = materialVariant.BaseMaterial.Name,
                    BaseMaterialValue = materialVariant.BaseMaterial.Value,
                    ColorMap = materialVariant.ColorMap ?? string.Empty,
                    NormalMap = materialVariant.NormalMap ?? string.Empty,
                    RoughnessMap = materialVariant.RoughnessMap ?? string.Empty,
                    MetalnessMap = materialVariant.MetalnessMap ?? string.Empty,
                    StudsPerTile = materialVariant.StudsPerTile.ToString(
                        "R", CultureInfo.InvariantCulture)
                };
            }

            if (instance is RbxValueBase valueBase)
            {
                node.Value = CaptureValue(valueBase);
            }

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
        }

        /// <summary>Encodes a live value payload; ObjectValue stores the target id (0 = nil).</summary>
        private static ValueSnapshot CaptureValue(RbxValueBase valueBase)
        {
            switch (valueBase)
            {
                case RbxIntValue intValue:
                    return new ValueSnapshot
                    {
                        StringValue = intValue.Value.ToString(CultureInfo.InvariantCulture)
                    };
                case RbxNumberValue numberValue:
                    return new ValueSnapshot
                    {
                        StringValue = numberValue.Value.ToString("R", CultureInfo.InvariantCulture)
                    };
                case RbxStringValue stringValue:
                    return new ValueSnapshot { StringValue = stringValue.Value };
                case RbxBoolValue boolValue:
                    return new ValueSnapshot
                    {
                        StringValue = boolValue.Value ? "true" : "false"
                    };
                case RbxObjectValue objectValue:
                    return new ValueSnapshot
                    {
                        ObjectTargetId = objectValue.Value?.Id.Value ?? 0UL
                    };
                case RbxVector3Value vector3Value:
                {
                    RbxVector3 vector = vector3Value.Value;
                    return new ValueSnapshot
                    {
                        StringValue = Join(vector.X, vector.Y, vector.Z)
                    };
                }
                case RbxCFrameValue cframeValue:
                    return new ValueSnapshot
                    {
                        StringValue = Join(cframeValue.Value.GetComponents())
                    };
                case RbxColor3Value color3Value:
                {
                    RbxColor3 color = color3Value.Value;
                    return new ValueSnapshot
                    {
                        StringValue = Join(color.R, color.G, color.B)
                    };
                }
                default:
                    throw RbxError.BadArgument(
                        "cannot capture unsupported value class '" + valueBase.ClassName + "'",
                        "capture only the eight MVP8 value classes");
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
            Validate(snapshot, registry);

            registry.ConfigureWorldAclVersion(snapshot.WorldAclVersion);
            Dictionary<ulong, RbxInstance> restored = new();
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                RbxInstance instance = registry.RestoreInstance(node.ClassName,
                    new InstanceId(node.Id), node.OwnerModId, node.OriginTag,
                    node.OwnerActorId, node.AccessScope);
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

            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                RbxInstance instance = restored[node.Id];
                if (node.Model != null)
                {
                    RbxModel model = (RbxModel)instance;
                    if (node.Model.HasStoredWorldPivot)
                    {
                        model.SetWorldPivot(ParseCFrame(node.Model.StoredWorldPivot));
                    }

                    if (node.Model.PrimaryPartId != 0UL)
                    {
                        model.SetPrimaryPart(restored[node.Model.PrimaryPartId]);
                    }
                }

                if (node.ClickDetector != null)
                {
                    RbxClickDetector clickDetector = (RbxClickDetector)instance;
                    clickDetector.MaxActivationDistance = double.Parse(
                        node.ClickDetector.MaxActivationDistance, CultureInfo.InvariantCulture);
                }

                if (node.MaterialVariant != null)
                {
                    RbxMaterialVariant materialVariant = (RbxMaterialVariant)instance;
                    materialVariant.BaseMaterial = new RbxMaterialId(
                        node.MaterialVariant.BaseMaterial,
                        node.MaterialVariant.BaseMaterialValue);
                    materialVariant.ColorMap = node.MaterialVariant.ColorMap ?? string.Empty;
                    materialVariant.NormalMap = node.MaterialVariant.NormalMap ?? string.Empty;
                    materialVariant.RoughnessMap =
                        node.MaterialVariant.RoughnessMap ?? string.Empty;
                    materialVariant.MetalnessMap =
                        node.MaterialVariant.MetalnessMap ?? string.Empty;
                    materialVariant.StudsPerTile = float.Parse(
                        node.MaterialVariant.StudsPerTile, CultureInfo.InvariantCulture);
                }

                if (node.Value != null)
                {
                    RestoreValue((RbxValueBase)instance, node, restored);
                }
            }

            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                registry.GetRecord(new InstanceId(node.Id)).Revision = node.Revision;
            }

            return root;
        }

        /// <summary>Applies an already-validated value payload without firing Changed.</summary>
        private static void RestoreValue(RbxValueBase valueBase, InstanceSnapshot node,
            IReadOnlyDictionary<ulong, RbxInstance> restored)
        {
            ValueSnapshot value = node.Value;
            switch (valueBase)
            {
                case RbxIntValue intValue:
                    intValue.SetValueSilent(long.Parse(value.StringValue, CultureInfo.InvariantCulture));
                    break;
                case RbxNumberValue numberValue:
                    numberValue.SetValueSilent(double.Parse(value.StringValue, CultureInfo.InvariantCulture));
                    break;
                case RbxStringValue stringValue:
                    stringValue.SetValueSilent(value.StringValue ?? string.Empty);
                    break;
                case RbxBoolValue boolValue:
                    boolValue.SetValueSilent(string.Equals(
                        value.StringValue, "true", StringComparison.Ordinal));
                    break;
                case RbxObjectValue objectValue:
                    objectValue.SetValueSilent(value.ObjectTargetId == 0UL
                        ? null
                        : restored[value.ObjectTargetId]);
                    break;
                case RbxVector3Value vector3Value:
                {
                    float[] parts = Parse(value.StringValue, 3);
                    vector3Value.SetValueSilent(new RbxVector3(parts[0], parts[1], parts[2]));
                    break;
                }
                case RbxCFrameValue cframeValue:
                    cframeValue.SetValueSilent(ParseCFrame(value.StringValue));
                    break;
                case RbxColor3Value color3Value:
                {
                    float[] parts = Parse(value.StringValue, 3);
                    color3Value.SetValueSilent(new RbxColor3(parts[0], parts[1], parts[2]));
                    break;
                }
                default:
                    throw RbxError.BadArgument(
                        "cannot restore unsupported value class '" + valueBase.ClassName + "'",
                        "restore only the eight MVP8 value classes");
            }
        }

        /// <summary>Validates the entire tree before the destination registry is mutated.</summary>
        public static void Validate(InstanceTreeSnapshot snapshot, InstanceRegistry registry)
        {
            if (snapshot == null || snapshot.Instances == null || snapshot.Instances.Count == 0)
            {
                throw RbxError.BadArgument("cannot restore an empty snapshot",
                    "capture a subtree with InstanceTreeSerializer.Capture first");
            }

            if (snapshot.Instances.Count > MaximumSnapshotInstances)
            {
                throw RbxError.BadArgument(
                    "snapshot contains " + snapshot.Instances.Count + " instances; limit is "
                    + MaximumSnapshotInstances,
                    "split the world or reduce the serialized instance count");
            }

            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (snapshot.WorldAclVersion.HasValue
                && snapshot.WorldAclVersion.Value != InstanceRegistry.CurrentWorldAclVersion)
            {
                throw RbxError.BadArgument(
                    "snapshot uses unsupported world ACL version " + snapshot.WorldAclVersion.Value,
                    "use world ACL version " + InstanceRegistry.CurrentWorldAclVersion);
            }

            Dictionary<ulong, InstanceSnapshot> byId = new();
            int rootCount = 0;
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node == null || node.Id == 0UL)
                {
                    throw RbxError.BadArgument("snapshot contains an invalid instance id",
                        "every serialized instance id must be non-zero");
                }

                InstanceId nodeId = new(node.Id);
                if (!nodeId.IsServerAssigned)
                {
                    throw RbxError.BadArgument(
                        "snapshot contains locally-assigned instance id " + node.Id,
                        "world files may contain only server-authority instance ids");
                }

                if (!byId.TryAdd(node.Id, node))
                {
                    throw RbxError.BadArgument("snapshot contains duplicate instance id " + node.Id,
                        "every serialized instance id must be unique");
                }

                if (registry.TryGet(new InstanceId(node.Id), out RbxInstance _))
                {
                    throw RbxError.BadArgument(
                        "snapshot instance id " + node.Id + " already exists in the destination registry",
                        "restore into a fresh registry or remove the colliding destination instance");
                }

                if (!OriginTag.IsValid(node.OriginTag))
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " has invalid origin tag '"
                        + node.OriginTag + "'",
                        "use a mod:, console:, or ai: origin tag with a non-empty payload");
                }

                if (!registry.Catalog.TryGet(node.ClassName, out ClassDescriptor descriptor)
                    || descriptor.IsAbstract)
                {
                    throw RbxError.BadArgument(
                        "snapshot contains unsupported class '" + node.ClassName + "'",
                        "load the package with a catalog that supports every serialized class");
                }

                if (node.Revision < 0L)
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " has negative revision " + node.Revision,
                        "instance revisions must be zero or greater");
                }

                if (node.ParentId == 0UL)
                {
                    rootCount++;
                }
                else if (node.ParentId == node.Id)
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " is its own parent",
                        "parent ids must form one acyclic tree");
                }

                ValidateAttributes(node);
                ValidateTags(node);
                ValidateSpecializedState(node, registry.Catalog);
            }

            if (rootCount != 1)
            {
                throw RbxError.BadArgument(
                    "snapshot must contain exactly one root but contains " + rootCount,
                    "capture one connected instance subtree");
            }

            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (node.ParentId != 0UL && !byId.ContainsKey(node.ParentId))
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " names missing parent " + node.ParentId,
                        "include every parent in the captured subtree");
                }
            }

            ValidateHierarchy(byId);
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                ValidateModelReferences(node, byId, registry.Catalog);
                ValidateValueReferences(node, byId);
            }
        }

        private static void ValidateAttributes(InstanceSnapshot node)
        {
            if (node.Attributes == null)
            {
                throw RbxError.BadArgument(
                    "snapshot instance " + node.Id + " has a nil attribute collection",
                    "serialize an empty attribute collection instead");
            }
            if (node.Attributes.Count > MaximumAttributesPerInstance)
            {
                throw RbxError.BadArgument(
                    "snapshot instance " + node.Id + " contains " + node.Attributes.Count
                    + " attributes; limit is " + MaximumAttributesPerInstance,
                    "reduce the serialized attribute count");
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (AttributeSnapshot attribute in node.Attributes)
            {
                if (attribute == null)
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " contains a nil attribute",
                        "remove the invalid attribute entry");
                }

                AttributeContract.ValidateName(attribute.Name);
                if (!names.Add(attribute.Name))
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " contains duplicate attribute '"
                        + attribute.Name + "'",
                        "serialize each attribute name once");
                }

                if (!Enum.IsDefined(typeof(AttributeValueKind), attribute.Kind))
                {
                    throw RbxError.BadArgument(
                        "snapshot attribute '" + attribute.Name + "' uses unsupported kind "
                        + (int)attribute.Kind,
                        "use a supported attribute value kind");
                }

                FromAttributeSnapshot(attribute);
            }
        }

        private static void ValidateTags(InstanceSnapshot node)
        {
            if (node.Tags == null)
            {
                throw RbxError.BadArgument(
                    "snapshot instance " + node.Id + " has a nil tag collection",
                    "serialize an empty tag collection instead");
            }
            if (node.Tags.Count > MaximumTagsPerInstance)
            {
                throw RbxError.BadArgument(
                    "snapshot instance " + node.Id + " contains " + node.Tags.Count
                    + " tags; limit is " + MaximumTagsPerInstance,
                    "reduce the serialized tag count");
            }

            HashSet<string> tags = new(StringComparer.Ordinal);
            foreach (string tag in node.Tags)
            {
                if (string.IsNullOrEmpty(tag) || !tags.Add(tag))
                {
                    throw RbxError.BadArgument(
                        "snapshot instance " + node.Id + " contains an empty or duplicate tag",
                        "serialize each non-empty tag once");
                }
            }
        }

        private static void ValidateSpecializedState(InstanceSnapshot node, ClassCatalog catalog)
        {
            bool isModel = catalog.IsA(node.ClassName, "Model");
            if ((node.Model != null) != isModel)
            {
                throw RbxError.BadArgument(
                    "snapshot class '" + node.ClassName + "' has mismatched Model state",
                    isModel ? "include Model state" : "remove Model state from this class");
            }

            if (node.Model != null && node.Model.HasStoredWorldPivot)
            {
                ParseCFrame(node.Model.StoredWorldPivot);
            }
            else if (node.Model != null && node.Model.StoredWorldPivot != null)
            {
                throw RbxError.BadArgument(
                    "snapshot Model " + node.Id
                    + " stores a WorldPivot while has_stored_world_pivot is false",
                    "clear StoredWorldPivot or mark the stored pivot as present");
            }

            bool isClickDetector = string.Equals(
                node.ClassName, "ClickDetector", StringComparison.Ordinal);
            if ((node.ClickDetector != null) != isClickDetector)
            {
                throw RbxError.BadArgument(
                    "snapshot class '" + node.ClassName + "' has mismatched ClickDetector state",
                    isClickDetector
                        ? "include ClickDetector state"
                        : "remove ClickDetector state from this class");
            }

            if (node.ClickDetector != null)
            {
                double value = double.Parse(
                    node.ClickDetector.MaxActivationDistance, CultureInfo.InvariantCulture);
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
                {
                    throw RbxError.BadArgument(
                        "snapshot ClickDetector has invalid MaxActivationDistance '"
                        + node.ClickDetector.MaxActivationDistance + "'",
                        "use a finite non-negative distance");
                }
            }

            bool isMaterialVariant = string.Equals(
                node.ClassName, "MaterialVariant", StringComparison.Ordinal);
            if ((node.MaterialVariant != null) != isMaterialVariant)
            {
                throw RbxError.BadArgument(
                    "snapshot class '" + node.ClassName + "' has mismatched MaterialVariant state",
                    isMaterialVariant
                        ? "include MaterialVariant state"
                        : "remove MaterialVariant state from this class");
            }

            if (node.MaterialVariant != null)
            {
                if (string.IsNullOrWhiteSpace(node.MaterialVariant.BaseMaterial))
                {
                    throw RbxError.BadArgument(
                        "snapshot MaterialVariant " + node.Id + " has an empty BaseMaterial name",
                        "use a canonical Enum.Material item name");
                }

                float studs = float.Parse(
                    node.MaterialVariant.StudsPerTile, CultureInfo.InvariantCulture);
                if (float.IsNaN(studs) || float.IsInfinity(studs) || studs <= 0f)
                {
                    throw RbxError.BadArgument(
                        "snapshot MaterialVariant " + node.Id + " has invalid StudsPerTile '"
                        + node.MaterialVariant.StudsPerTile + "'",
                        "use a positive finite tile density");
                }
            }

            bool isValue = catalog.IsA(node.ClassName, "ValueBase");
            if ((node.Value != null) != isValue)
            {
                throw RbxError.BadArgument(
                    "snapshot class '" + node.ClassName + "' has mismatched Value state",
                    isValue ? "include Value state" : "remove Value state from this class");
            }

            if (node.Value != null)
            {
                ValidateValuePayload(node);
            }
        }

        private static void ValidateHierarchy(
            IReadOnlyDictionary<ulong, InstanceSnapshot> byId)
        {
            Dictionary<ulong, byte> states = new();
            Dictionary<ulong, int> depths = new();
            foreach (InstanceSnapshot start in byId.Values)
            {
                if (states.TryGetValue(start.Id, out byte completedState)
                    && completedState == 2)
                {
                    continue;
                }

                List<ulong> path = new();
                InstanceSnapshot current = start;
                int ancestorDepth = 0;
                while (true)
                {
                    if (states.TryGetValue(current.Id, out byte state))
                    {
                        if (state == 1)
                        {
                            throw RbxError.BadArgument(
                                "snapshot hierarchy contains a cycle at instance " + current.Id,
                                "parent ids must form one acyclic tree");
                        }

                        ancestorDepth = depths[current.Id];
                        break;
                    }

                    states.Add(current.Id, 1);
                    path.Add(current.Id);
                    if (current.ParentId == 0UL)
                    {
                        break;
                    }

                    current = byId[current.ParentId];
                }

                for (int index = path.Count - 1; index >= 0; index--)
                {
                    ulong id = path[index];
                    ancestorDepth++;
                    if (ancestorDepth > MaximumSnapshotDepth)
                    {
                        throw RbxError.BadArgument(
                            "snapshot hierarchy exceeds depth limit " + MaximumSnapshotDepth,
                            "flatten the serialized instance hierarchy");
                    }

                    depths[id] = ancestorDepth;
                    states[id] = 2;
                }
            }
        }

        private static void ValidateModelReferences(InstanceSnapshot node,
            IReadOnlyDictionary<ulong, InstanceSnapshot> byId, ClassCatalog catalog)
        {
            if (node.Model == null || node.Model.PrimaryPartId == 0UL)
            {
                return;
            }

            if (!byId.TryGetValue(node.Model.PrimaryPartId, out InstanceSnapshot primary)
                || !catalog.IsA(primary.ClassName, "BasePart"))
            {
                throw RbxError.BadArgument(
                    "snapshot Model " + node.Id + " names invalid PrimaryPart "
                    + node.Model.PrimaryPartId,
                    "PrimaryPart must name a serialized BasePart descendant");
            }

            InstanceSnapshot current = primary;
            while (current.ParentId != 0UL && current.ParentId != node.Id)
            {
                current = byId[current.ParentId];
            }

            if (current.ParentId != node.Id)
            {
                throw RbxError.BadArgument(
                    "snapshot Model " + node.Id + " names non-descendant PrimaryPart "
                    + node.Model.PrimaryPartId,
                    "PrimaryPart must name a serialized BasePart descendant");
            }
        }

        /// <summary>Strict per-type check of an already shape-matched value payload.</summary>
        private static void ValidateValuePayload(InstanceSnapshot node)
        {
            string raw = node.Value.StringValue;
            switch (node.ClassName)
            {
                case "IntValue":
                    try
                    {
                        long.Parse(raw, CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex) when (ex is FormatException || ex is OverflowException
                        || ex is ArgumentNullException)
                    {
                        throw RbxError.BadArgument(
                            "snapshot IntValue " + node.Id + " has non-integer Value '"
                            + raw + "'",
                            "serialize an exact 64-bit integer");
                    }

                    break;
                case "NumberValue":
                    try
                    {
                        double parsed = double.Parse(raw, CultureInfo.InvariantCulture);
                        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
                        {
                            throw RbxError.BadArgument(
                                "snapshot NumberValue " + node.Id
                                + " has non-finite Value '" + raw + "'",
                                "serialize a finite JSON number");
                        }
                    }
                    catch (Exception ex) when (ex is FormatException || ex is OverflowException
                        || ex is ArgumentNullException)
                    {
                        throw RbxError.BadArgument(
                            "snapshot NumberValue " + node.Id + " has malformed Value '"
                            + raw + "'",
                            "serialize a finite JSON number");
                    }

                    break;
                case "StringValue":
                    if (raw == null)
                    {
                        throw RbxError.BadArgument(
                            "snapshot StringValue " + node.Id + " has a nil value",
                            "serialize an empty string when the value is intentionally empty");
                    }

                    if (raw.Length > RbxStringValue.MaxLength)
                    {
                        throw RbxError.BadArgument(
                            "snapshot StringValue " + node.Id + " exceeds "
                            + RbxStringValue.MaxLength + " characters",
                            "split the text across several values");
                    }

                    break;
                case "BoolValue":
                    if (!string.Equals(raw, "true", StringComparison.Ordinal)
                        && !string.Equals(raw, "false", StringComparison.Ordinal))
                    {
                        throw RbxError.BadArgument(
                            "snapshot BoolValue " + node.Id + " has malformed Value '"
                            + raw + "'",
                            "serialize 'true' or 'false'");
                    }

                    break;
                case "ObjectValue":
                    break;
                case "Vector3Value":
                    Parse(raw, 3);
                    break;
                case "CFrameValue":
                    ParseCFrame(raw);
                    break;
                case "Color3Value":
                    Parse(raw, 3);
                    break;
                default:
                    throw RbxError.BadArgument(
                        "snapshot class '" + node.ClassName + "' has mismatched Value state",
                        "remove Value state from this class");
            }
        }

        /// <summary>ObjectValue targets must name a serialized instance (nil is 0).</summary>
        private static void ValidateValueReferences(InstanceSnapshot node,
            IReadOnlyDictionary<ulong, InstanceSnapshot> byId)
        {
            if (node.Value == null
                || !string.Equals(node.ClassName, "ObjectValue", StringComparison.Ordinal)
                || node.Value.ObjectTargetId == 0UL)
            {
                return;
            }

            if (!byId.ContainsKey(node.Value.ObjectTargetId))
            {
                throw RbxError.BadArgument(
                    "snapshot ObjectValue " + node.Id + " names missing target "
                    + node.Value.ObjectTargetId,
                    "ObjectValue targets must name a serialized instance in the same package");
            }
        }

        private static object FromAttributeSnapshot(AttributeSnapshot attribute)
        {
            switch (attribute.Kind)
            {
                case AttributeValueKind.String:
                    if (attribute.StringValue == null)
                    {
                        throw RbxError.BadArgument(
                            "string attribute '" + attribute.Name + "' has a nil value",
                            "serialize an empty string when the attribute is intentionally empty");
                    }

                    return attribute.StringValue;
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
                    string[] components = attribute.StringValue?.Split(',');
                    if (components == null || components.Length != 2
                        || !float.TryParse(
                            components[0],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float scale)
                        || float.IsNaN(scale)
                        || float.IsInfinity(scale)
                        || !int.TryParse(
                            components[1],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int offset))
                    {
                        throw RbxError.BadArgument(
                            "UDim attribute '" + attribute.Name + "' has invalid value '"
                            + attribute.StringValue + "'",
                            "serialize a finite Scale and exact 32-bit integer Offset");
                    }

                    return new RbxUDim(scale, offset);
                }
                default: return attribute.NumberValue;
            }
        }

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
                if (float.IsNaN(result[i]) || float.IsInfinity(result[i]))
                {
                    throw RbxError.BadArgument(
                        "datatype attribute value '" + serialized + "' contains a non-finite component",
                        "serialize only finite datatype components");
                }
            }

            return result;
        }

        private static RbxCFrame ParseCFrame(string serialized)
        {
            float[] values = Parse(serialized, 12);
            return new RbxCFrame(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8],
                values[9], values[10], values[11]);
        }
    }
}
