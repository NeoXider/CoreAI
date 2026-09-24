using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// Canonical MVP3 world codec. Capture/export produces one in-memory payload; disk packages
    /// encode and decode that same payload rather than maintaining a second tree mapper.
    /// </summary>
    public static class RbxWorldPackageSerializer
    {
        public const int CurrentFormatVersion = 1;
        public const int CurrentWorldSchemaVersion = 1;
        public const string CurrentApiVersion = "MVP2";
        public const string PackageFormat = "coreai-rbx-world";
        public const string ManifestEntryName = "manifest.json";
        public const string WorldEntryName = "world.json";
        public const int MaximumPackageBytes = 64 * 1024 * 1024;
        public const int MaximumEntryBytes = 16 * 1024 * 1024;
        public const int MaximumExpandedPackageBytes = 128 * 1024 * 1024;
        public const int MaximumEntries = 2048;
        public const int MaximumMods = 256;

        private static readonly DateTimeOffset StableZipTimestamp =
            new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>
        /// Diagnostic reason for a member whose non-finite live value the payload replaced with the
        /// member's default; the entry names the instance (<c>model_id</c>) and the member.
        /// </summary>
        private const string NonFiniteValueReason = "non-finite-value";

        /// <summary>Diagnostic reason for a finite live value outside the range the format accepts.</summary>
        private const string OutOfRangeReason = "out-of-range";

        /// <summary>Diagnostic reason for a reference to a mod-owned or otherwise excluded target.</summary>
        private const string ModEphemeralReason = "mod-ephemeral";

        /// <summary>Diagnostic reason for a reference to a destroyed, unparented or unknown target.</summary>
        private const string MissingReason = "missing";

        /// <summary>Diagnostic reason for a PrimaryPart that is retained but not inside its Model.</summary>
        private const string NotDescendantReason = "not-descendant";

        /// <summary>
        /// Diagnostic reason for a text member whose lone UTF-16 surrogate the payload replaced with
        /// U+FFFD; the entry names the instance (<c>model_id</c>, 0 for a mod source) and the member.
        /// </summary>
        private const string IllFormedTextReason = "ill-formed-text";

        /// <summary>The replacement for every lone UTF-16 surrogate the writer meets.</summary>
        private const char ReplacementCharacter = '\uFFFD';

        /// <summary>Captures the supported world-owned DataModel projection plus settings and mods.</summary>
        public static RbxWorldPackagePayload Capture(RbxWorldPackageCaptureContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.Registry.TryGet(context.Game.Id, out RbxInstance registeredGame)
                || !ReferenceEquals(registeredGame, context.Game))
            {
                throw new RbxWorldPackageException(
                    "Cannot capture a DataModel from a different or detached InstanceRegistry.");
            }

            InstanceTreeSnapshot capturedTree = InstanceTreeSerializer.Capture(context.Game);
            List<RbxWorldPackageDiagnostic> diagnostics = new();
            HashSet<string> excludedVariantNames = new(StringComparer.Ordinal);
            InstanceTreeSnapshot tree = ProjectWorldOwnedTree(
                capturedTree, context.Registry, diagnostics, excludedVariantNames);
            HashSet<string> retainedVariantNames = CollectMaterialVariantNames(tree);
            Dictionary<InstanceId, PartProperties> parts = new();
            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (string.Equals(node.ClassName, "Player", StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "World package format version 1 cannot serialize Player instance id "
                        + node.Id + "; Player identity/lifecycle state requires the MVP8 schema.");
                }

                if (!context.Registry.Catalog.IsA(node.ClassName, "BasePart"))
                {
                    continue;
                }

                if (context.PartSink == null)
                {
                    throw new RbxWorldPackageException(
                        "World package capture found BasePart instance id " + node.Id
                        + " but no readable IPartPropertySink was supplied.");
                }

                InstanceId id = new(node.Id);
                if (!context.PartSink.TryGetPartProperties(id, out PartProperties properties))
                {
                    throw new RbxWorldPackageException(
                        "World package capture found BasePart instance id " + node.Id
                        + " but its durable Part state is missing from IPartPropertySink.");
                }

                PartProperties finite = ProjectFinitePartState(id, properties, diagnostics);
                if (TryReplaceLoneSurrogates(finite.MaterialVariant, out string wellFormedVariant))
                {
                    finite.MaterialVariant = wellFormedVariant;
                    diagnostics.Add(IllFormedTextDiagnostic(id.Value, "MaterialVariant"));
                }

                parts.Add(id, ProjectMaterialVariantReference(
                    id, finite, retainedVariantNames, excludedVariantNames, diagnostics));
            }

            RbxWorldSettings settings = context.Settings;
            if (!string.Equals(
                    settings.WorldId ?? "", context.Registry.WorldId, StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "Capture settings world id '" + settings.WorldId
                    + "' does not match registry world id '" + context.Registry.WorldId + "'.");
            }

            DateTime capturedAtUtc = NormalizeUtc(context.CapturedAtUtc ?? DateTime.UtcNow);
            RbxCFrame? cameraCFrame = context.CameraRig != null
                ? ProjectFiniteCameraCFrame(context.CameraRig.GetCFrame(), tree, diagnostics)
                : (RbxCFrame?)null;
            IReadOnlyList<RbxWorldModSource> mods = CaptureMods(context.ModSourceStore, diagnostics);
            RbxWorldPackagePayload payload = new(
                capturedAtUtc,
                CloneSettings(settings),
                tree,
                parts,
                cameraCFrame,
                mods,
                diagnostics);
            ValidatePayload(payload, context.Registry.Catalog);
            return payload;
        }

        /// <summary>
        /// Projects the live tree to its world-owned, format-valid snapshot: excludes mod-ephemeral
        /// and runtime-infrastructure subtrees, then drops or replaces, in the snapshot only, every
        /// dangling reference and out-of-format value a script can leave behind, recording one
        /// diagnostic each. The names of excluded MaterialVariants are added to
        /// <paramref name="excludedVariantNames"/> for the Part reference projection.
        /// </summary>
        private static InstanceTreeSnapshot ProjectWorldOwnedTree(
            InstanceTreeSnapshot capturedTree,
            InstanceRegistry registry,
            List<RbxWorldPackageDiagnostic> diagnostics,
            HashSet<string> excludedVariantNames)
        {
            InstanceTreeSnapshot projectedTree = new()
            {
                WorldAclVersion = capturedTree.WorldAclVersion
            };
            HashSet<ulong> excludedIds = new();
            Dictionary<ulong, ulong> retainedParents = new();
            foreach (InstanceSnapshot node in capturedTree.Instances)
            {
                bool excludedByParent = node.ParentId != 0UL && excludedIds.Contains(node.ParentId);
                bool runtimeInfrastructure = registry.TryGetRecord(
                    new InstanceId(node.Id), out InstanceRecord record)
                    && record.IsRuntimeInfrastructure;
                if (node.OwnerModId != null || runtimeInfrastructure || excludedByParent)
                {
                    excludedIds.Add(node.Id);
                    if (string.Equals(node.ClassName, "MaterialVariant", StringComparison.Ordinal))
                    {
                        excludedVariantNames?.Add(ReplaceLoneSurrogates(node.Name));
                    }

                    continue;
                }

                if (node.ParentId != 0UL && !retainedParents.ContainsKey(node.ParentId))
                {
                    throw new RbxWorldPackageException(
                        "World-owned projection found instance id " + node.Id
                        + " with missing retained parent id " + node.ParentId + ".");
                }

                projectedTree.Instances.Add(node);
                retainedParents.Add(node.Id, node.ParentId);
            }

            foreach (InstanceSnapshot node in projectedTree.Instances)
            {
                if (node.Model == null || node.Model.PrimaryPartId == 0UL)
                {
                    continue;
                }

                ulong primaryPartId = node.Model.PrimaryPartId;
                string classification;
                if (!retainedParents.ContainsKey(primaryPartId))
                {
                    classification = excludedIds.Contains(primaryPartId)
                        ? ModEphemeralReason
                        : MissingReason;
                }
                else if (!IsRetainedDescendant(primaryPartId, node.Id, retainedParents))
                {
                    // WHY: SetPrimaryPart accepts any BasePart, and only the next pre-simulation
                    // step resets one outside the Model, so a capture in between sees it.
                    classification = NotDescendantReason;
                }
                else
                {
                    continue;
                }

                diagnostics?.Add(new RbxWorldPackageDiagnostic(
                    node.Id,
                    primaryPartId,
                    classification));
                node.Model.PrimaryPartId = 0UL;
            }

            foreach (InstanceSnapshot node in projectedTree.Instances)
            {
                if (node.Value == null
                    || node.Value.ObjectTargetId == 0UL
                    || !string.Equals(node.ClassName, "ObjectValue", StringComparison.Ordinal)
                    || retainedParents.ContainsKey(node.Value.ObjectTargetId))
                {
                    continue;
                }

                // WHY: nothing clears an ObjectValue when its target is destroyed or never parented,
                // and a world-owned ObjectValue may point at a mod-owned instance the projection drops.
                string classification = IsModEphemeralTarget(
                    node.Value.ObjectTargetId, excludedIds, registry)
                    ? ModEphemeralReason
                    : MissingReason;
                diagnostics?.Add(new RbxWorldPackageDiagnostic(node.Id, 0UL, classification, "Value"));
                node.Value.ObjectTargetId = 0UL;
            }

            InstanceTreeSerializer.ReplaceNonFiniteValues(projectedTree, (instanceId, member) =>
                diagnostics?.Add(NonFiniteValueDiagnostic(instanceId, member)));
            InstanceTreeSerializer.ReplaceOutOfRangeValues(projectedTree, (instanceId, member) =>
                diagnostics?.Add(new RbxWorldPackageDiagnostic(
                    instanceId, 0UL, OutOfRangeReason, member)));
            ReplaceIllFormedText(projectedTree, diagnostics);
            return projectedTree;
        }

        /// <summary>
        /// Replaces, in <paramref name="tree"/> only, every lone UTF-16 surrogate in the text the
        /// package writes with U+FFFD and records one <c>ill-formed-text</c> diagnostic per member.
        /// A tag or attribute that becomes a duplicate of an earlier one is dropped.
        /// </summary>
        /// <remarks>
        /// WHY: Lua strings are byte strings, and cutting a character in half (<c>string.sub</c> on
        /// the first byte of an emoji) hands the CLR a lone surrogate. UTF-8 cannot encode it, so the
        /// strict writer threw after a successful capture, and every save, every confirmed
        /// pre-mutation autosave and therefore every gated tool was refused. The live instance keeps
        /// what the script wrote, as for the non-finite projection.
        /// </remarks>
        private static void ReplaceIllFormedText(
            InstanceTreeSnapshot tree,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (node == null)
                {
                    continue;
                }

                node.Name = ReplaceMember(node.Id, "Name", node.Name, diagnostics);
                node.OriginTag = ReplaceMember(node.Id, "OriginTag", node.OriginTag, diagnostics);
                node.OwnerActorId = ReplaceMember(node.Id, "OwnerActorId", node.OwnerActorId, diagnostics);
                if (node.Value != null)
                {
                    node.Value.StringValue = ReplaceMember(
                        node.Id, "Value", node.Value.StringValue, diagnostics);
                }

                if (node.MaterialVariant != null)
                {
                    MaterialVariantSnapshot variant = node.MaterialVariant;
                    variant.ColorMap = ReplaceMember(node.Id, "ColorMap", variant.ColorMap, diagnostics);
                    variant.NormalMap = ReplaceMember(node.Id, "NormalMap", variant.NormalMap, diagnostics);
                    variant.RoughnessMap = ReplaceMember(
                        node.Id, "RoughnessMap", variant.RoughnessMap, diagnostics);
                    variant.MetalnessMap = ReplaceMember(
                        node.Id, "MetalnessMap", variant.MetalnessMap, diagnostics);
                }

                if (node.Humanoid != null)
                {
                    node.Humanoid.DisplayName = ReplaceMember(
                        node.Id, "DisplayName", node.Humanoid.DisplayName, diagnostics);
                }

                if (node.Tags != null)
                {
                    ReplaceIllFormedTags(node, diagnostics);
                }

                if (node.Attributes != null)
                {
                    ReplaceIllFormedAttributes(node, diagnostics);
                }
            }
        }

        private static void ReplaceIllFormedTags(
            InstanceSnapshot node,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            HashSet<string> seen = null;
            int index = 0;
            while (index < node.Tags.Count)
            {
                if (TryReplaceLoneSurrogates(node.Tags[index], out string wellFormed))
                {
                    diagnostics?.Add(IllFormedTextDiagnostic(node.Id, "Tags"));
                    seen ??= new HashSet<string>(node.Tags, StringComparer.Ordinal);
                    if (seen.Contains(wellFormed))
                    {
                        node.Tags.RemoveAt(index);
                        continue;
                    }

                    seen.Add(wellFormed);
                    node.Tags[index] = wellFormed;
                }

                index++;
            }
        }

        private static void ReplaceIllFormedAttributes(
            InstanceSnapshot node,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            HashSet<string> seen = null;
            int index = 0;
            while (index < node.Attributes.Count)
            {
                AttributeSnapshot attribute = node.Attributes[index];
                if (attribute == null)
                {
                    index++;
                    continue;
                }

                bool nameReplaced = TryReplaceLoneSurrogates(attribute.Name, out string wellFormedName);
                string member = InstanceTreeSerializer.AttributeMemberPrefix + wellFormedName;
                if (nameReplaced)
                {
                    diagnostics?.Add(IllFormedTextDiagnostic(node.Id, member));
                    if (seen == null)
                    {
                        seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (AttributeSnapshot other in node.Attributes)
                        {
                            if (other?.Name != null)
                            {
                                seen.Add(other.Name);
                            }
                        }
                    }

                    if (seen.Contains(wellFormedName))
                    {
                        node.Attributes.RemoveAt(index);
                        continue;
                    }

                    seen.Add(wellFormedName);
                    attribute.Name = wellFormedName;
                }

                if (TryReplaceLoneSurrogates(attribute.StringValue, out string wellFormedValue))
                {
                    attribute.StringValue = wellFormedValue;
                    if (!nameReplaced)
                    {
                        diagnostics?.Add(IllFormedTextDiagnostic(node.Id, member));
                    }
                }

                index++;
            }
        }

        private static string ReplaceMember(
            ulong instanceId,
            string member,
            string value,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            if (!TryReplaceLoneSurrogates(value, out string wellFormed))
            {
                return value;
            }

            diagnostics?.Add(IllFormedTextDiagnostic(instanceId, member));
            return wellFormed;
        }

        private static RbxWorldPackageDiagnostic IllFormedTextDiagnostic(ulong instanceId, string member)
        {
            return new RbxWorldPackageDiagnostic(instanceId, 0UL, IllFormedTextReason, member);
        }

        /// <summary>
        /// Returns <paramref name="value"/> with every lone UTF-16 surrogate replaced by U+FFFD, or the
        /// same instance when it is null or already well-formed.
        /// </summary>
        internal static string ReplaceLoneSurrogates(string value)
        {
            return TryReplaceLoneSurrogates(value, out string wellFormed) ? wellFormed : value;
        }

        /// <summary>True, with the replaced text, when <paramref name="value"/> holds a lone surrogate.</summary>
        private static bool TryReplaceLoneSurrogates(string value, out string wellFormed)
        {
            wellFormed = value;
            if (value == null)
            {
                return false;
            }

            char[] buffer = null;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (!char.IsSurrogate(current))
                {
                    continue;
                }

                if (char.IsHighSurrogate(current)
                    && index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    continue;
                }

                buffer ??= value.ToCharArray();
                buffer[index] = ReplacementCharacter;
            }

            if (buffer == null)
            {
                return false;
            }

            wellFormed = new string(buffer);
            return true;
        }

        /// <summary>True when <paramref name="descendantId"/> sits below <paramref name="ancestorId"/> in the retained tree.</summary>
        private static bool IsRetainedDescendant(
            ulong descendantId,
            ulong ancestorId,
            IReadOnlyDictionary<ulong, ulong> retainedParents)
        {
            ulong current = retainedParents[descendantId];
            while (current != 0UL)
            {
                if (current == ancestorId)
                {
                    return true;
                }

                if (!retainedParents.TryGetValue(current, out current))
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// True when a reference target that the projection did not retain still exists but is
        /// mod-owned, runtime infrastructure, or inside an excluded subtree.
        /// </summary>
        private static bool IsModEphemeralTarget(
            ulong targetId,
            HashSet<ulong> excludedIds,
            InstanceRegistry registry)
        {
            return excludedIds.Contains(targetId)
                   || (registry.TryGetRecord(new InstanceId(targetId), out InstanceRecord record)
                       && (record.OwnerModId != null || record.IsRuntimeInfrastructure));
        }

        private static HashSet<string> CollectMaterialVariantNames(InstanceTreeSnapshot tree)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (string.Equals(node.ClassName, "MaterialVariant", StringComparison.Ordinal))
                {
                    names.Add(node.Name);
                }
            }

            return names;
        }

        /// <summary>
        /// Clears, in the captured Part state only, a MaterialVariant name that no retained
        /// MaterialVariant carries, and records one diagnostic.
        /// </summary>
        /// <remarks>
        /// WHY: <c>part.MaterialVariant</c> accepts any string, and renaming or destroying a
        /// variant, or creating it from a mod, leaves Parts naming it. An unknown name renders the
        /// plain Material, which is what the cleared snapshot restores.
        /// </remarks>
        private static PartProperties ProjectMaterialVariantReference(
            InstanceId id,
            PartProperties properties,
            HashSet<string> retainedVariantNames,
            HashSet<string> excludedVariantNames,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            string variantName = properties.MaterialVariant;
            if (string.IsNullOrEmpty(variantName) || retainedVariantNames.Contains(variantName))
            {
                return properties;
            }

            string classification = excludedVariantNames.Contains(variantName)
                ? ModEphemeralReason
                : MissingReason;
            diagnostics.Add(new RbxWorldPackageDiagnostic(
                id.Value, 0UL, classification, "MaterialVariant"));
            properties.MaterialVariant = null;
            return properties;
        }

        /// <summary>
        /// Replaces, in the captured Part state only, a non-finite CFrame, Size, Color or
        /// Transparency with the Roblox default Part bundle's value and records one diagnostic each.
        /// </summary>
        /// <remarks>
        /// WHY: scripts reach all four (<c>part.Position = Vector3.new(0/0, 0, 0)</c>, and the
        /// sinks' Transparency clamp lets NaN through), and the sink returns a copy, so the live
        /// Part keeps what the script wrote. A replaced Color also clears ColorWasExplicitlySet:
        /// the member returns to its untouched default, which renders like a Part nobody colored.
        /// </remarks>
        private static PartProperties ProjectFinitePartState(
            InstanceId id,
            PartProperties properties,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            PartProperties defaults = PartProperties.CreateDefault();
            if (!AreFinite(properties.CFrame.GetComponents()))
            {
                properties.CFrame = defaults.CFrame;
                diagnostics.Add(NonFiniteValueDiagnostic(id.Value, "CFrame"));
            }

            if (!IsFinite(properties.Size.X) || !IsFinite(properties.Size.Y)
                || !IsFinite(properties.Size.Z))
            {
                properties.Size = defaults.Size;
                diagnostics.Add(NonFiniteValueDiagnostic(id.Value, "Size"));
            }

            if (!IsFinite(properties.Color.R) || !IsFinite(properties.Color.G)
                || !IsFinite(properties.Color.B))
            {
                properties.Color = defaults.Color;
                properties.ColorWasExplicitlySet = defaults.ColorWasExplicitlySet;
                diagnostics.Add(NonFiniteValueDiagnostic(id.Value, "Color"));
            }

            if (!IsFinite(properties.Transparency))
            {
                properties.Transparency = defaults.Transparency;
                diagnostics.Add(NonFiniteValueDiagnostic(id.Value, "Transparency"));
            }

            return properties;
        }

        /// <summary>
        /// Returns the camera pose to capture: the live pose, or the in-memory rig's identity
        /// default when a script left a non-finite component in it (one diagnostic, reported on
        /// the Workspace camera).
        /// </summary>
        private static RbxCFrame ProjectFiniteCameraCFrame(
            RbxCFrame cameraCFrame,
            InstanceTreeSnapshot tree,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            if (AreFinite(cameraCFrame.GetComponents()))
            {
                return cameraCFrame;
            }

            diagnostics.Add(NonFiniteValueDiagnostic(FindCurrentCameraId(tree), "CFrame"));
            return RbxCFrame.Identity;
        }

        /// <summary>The captured id of Workspace's first Camera child, or 0 when none was captured.</summary>
        private static ulong FindCurrentCameraId(InstanceTreeSnapshot tree)
        {
            ulong rootId = tree.Instances.Count > 0 ? tree.Instances[0].Id : 0UL;
            ulong workspaceId = 0UL;
            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (workspaceId == 0UL)
                {
                    if (node.ParentId == rootId && node.ParentId != 0UL
                        && string.Equals(node.ClassName, "Workspace", StringComparison.Ordinal))
                    {
                        workspaceId = node.Id;
                    }

                    continue;
                }

                if (node.ParentId == workspaceId
                    && string.Equals(node.ClassName, "Camera", StringComparison.Ordinal))
                {
                    return node.Id;
                }
            }

            return 0UL;
        }

        private static RbxWorldPackageDiagnostic NonFiniteValueDiagnostic(ulong instanceId, string member)
        {
            return new RbxWorldPackageDiagnostic(instanceId, 0UL, NonFiniteValueReason, member);
        }

        private static bool AreFinite(IReadOnlyList<float> values)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (!IsFinite(values[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>Join-snapshot entry point; returns the disk codec's world-owned payload.</summary>
        public static RbxWorldPackagePayload ExportSnapshot(RbxWorldPackageCaptureContext context)
        {
            return Capture(context);
        }

        /// <summary>Encodes a validated payload as one deterministic-entry-order ZIP artifact.</summary>
        public static byte[] WritePackage(RbxWorldPackagePayload payload)
        {
            ValidatePayload(payload, ClassCatalog.CreateMvp1());
            PackageManifestDto manifest = BuildManifest(payload);
            byte[] manifestBytes = SerializeJson(manifest);
            byte[] worldBytes = SerializeJson(BuildWorldDto(payload));
            long expandedBytes = manifestBytes.LongLength + worldBytes.LongLength;
            ValidateWritableEntry(ManifestEntryName, manifestBytes.LongLength);
            ValidateWritableEntry(WorldEntryName, worldBytes.LongLength);
            for (int index = 0; index < payload.Mods.Count; index++)
            {
                RbxWorldModSource mod = payload.Mods[index];
                PackageModIndexDto modIndex = manifest.Mods[index];
                byte[] modManifestBytes = SerializeJson(mod.Manifest);
                int sourceByteCount = StrictUtf8.GetByteCount(ReplaceLoneSurrogates(mod.Source));
                ValidateWritableEntry(modIndex.ManifestEntry, modManifestBytes.LongLength);
                ValidateWritableEntry(modIndex.SourceEntry, sourceByteCount);
                expandedBytes += modManifestBytes.LongLength + sourceByteCount;
                if (expandedBytes > MaximumExpandedPackageBytes)
                {
                    throw new RbxWorldPackageException(
                        "World package expands to " + expandedBytes
                        + " bytes; format version 1 limit is "
                        + MaximumExpandedPackageBytes + " bytes.");
                }
            }

            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
            {
                WriteEntry(archive, ManifestEntryName, manifestBytes);
                WriteEntry(archive, WorldEntryName, worldBytes);
                for (int index = 0; index < payload.Mods.Count; index++)
                {
                    RbxWorldModSource mod = payload.Mods[index];
                    PackageModIndexDto modIndex = manifest.Mods[index];
                    WriteEntry(archive, modIndex.ManifestEntry, SerializeJson(mod.Manifest));
                    WriteEntry(
                        archive,
                        modIndex.SourceEntry,
                        StrictUtf8.GetBytes(ReplaceLoneSurrogates(mod.Source)));
                }
            }

            byte[] bytes = output.ToArray();
            if (bytes.Length > MaximumPackageBytes)
            {
                throw new RbxWorldPackageException(
                    "World package is " + bytes.Length + " bytes; format version 1 limit is "
                    + MaximumPackageBytes + " bytes.");
            }

            return bytes;
        }

        /// <summary>Decodes and validates every ZIP entry before returning a canonical payload.</summary>
        public static RbxWorldPackagePayload ReadPackage(byte[] packageBytes)
        {
            if (packageBytes == null || packageBytes.Length == 0)
            {
                throw new RbxWorldPackageException("World package is empty.");
            }

            if (packageBytes.Length > MaximumPackageBytes)
            {
                throw new RbxWorldPackageException(
                    "World package is " + packageBytes.Length + " bytes; format version 1 limit is "
                    + MaximumPackageBytes + " bytes.");
            }

            try
            {
                using MemoryStream input = new(packageBytes, false);
                using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
                Dictionary<string, ZipArchiveEntry> entries = IndexEntries(archive);
                PackageManifestDto manifest = DeserializeJson<PackageManifestDto>(
                    ReadRequiredEntry(entries, ManifestEntryName), ManifestEntryName);
                ValidateManifest(manifest);
                ValidateEntryIndex(entries, manifest);

                WorldFileDto world = DeserializeJson<WorldFileDto>(
                    ReadRequiredEntry(entries, manifest.WorldEntry), manifest.WorldEntry);
                List<RbxWorldModSource> mods = ReadMods(entries, manifest);
                RbxWorldPackagePayload payload = BuildPayload(manifest, world, mods);
                ValidatePayload(payload, ClassCatalog.CreateMvp1());
                return payload;
            }
            catch (RbxWorldPackageException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RbxWorldPackageException(
                    "World package format version could not be read: " + ex.Message, ex);
            }
        }

        /// <summary>Restores a validated payload into a newly allocated registry and DataModel.</summary>
        public static RbxWorldPackageRestoreResult RestoreFresh(
            RbxWorldPackagePayload payload,
            RbxWorldPackageRestoreOptions options = null)
        {
            ClassCatalog catalog = options?.ClassCatalog ?? ClassCatalog.CreateMvp1();
            ValidatePayload(payload, catalog);
            if (payload.CameraCFrame.HasValue && options?.CameraRig == null)
            {
                throw new RbxWorldPackageException(
                    "World package contains Camera CFrame state but no IRbxCameraRig was supplied "
                    + "for restore.");
            }

            Action rollbackScale = options?.BeginMetersPerStudRestore?.Invoke(
                payload.Settings.MetersPerStud);
            if (options?.BeginMetersPerStudRestore != null && rollbackScale == null)
            {
                throw new RbxWorldPackageException(
                    "The world-scale restore adapter did not provide a rollback action.");
            }

            RbxDataModel game = null;
            try
            {
                IInstanceBackingBinder backingBinder =
                    options?.BackingBinder ?? NullInstanceBackingBinder.Instance;
                IPartPropertySink partSink = options?.PartSink
                    ?? (backingBinder as IPartPropertySink)
                    ?? new InMemoryPartPropertySink();
                InstanceRegistry registry = new(
                    catalog,
                    backingBinder,
                    worldAclVersion: null,
                    worldId: payload.Settings.WorldId);
                string hostActorId = string.IsNullOrWhiteSpace(options?.HostActorId)
                    ? LocalActorIdentityProvider.DefaultActorId
                    : options.HostActorId.Trim();
                game = (RbxDataModel)InstanceTreeSerializer.Restore(
                    payload.Tree, registry, hostActorId);

                foreach (KeyValuePair<InstanceId, PartProperties> entry in payload.Parts)
                {
                    PartProperties properties = entry.Value;
                    partSink.SetPartProperties(entry.Key, in properties);
                }

                registry.SetSceneRoot(game);
                RbxInstance workspace = game.FindFirstChildOfClass("Workspace")
                    ?? throw new RbxWorldPackageException(
                        "World package DataModel has no Workspace service.");
                registry.SetWorldRoot(workspace);
                if (payload.CameraCFrame.HasValue)
                {
                    RbxCFrame cameraCFrame = payload.CameraCFrame.Value;
                    options.CameraRig.SetCFrame(in cameraCFrame);
                }

                return new RbxWorldPackageRestoreResult(
                    registry, game, partSink, CloneMods(payload.Mods));
            }
            catch
            {
                game?.Destroy();
                rollbackScale?.Invoke();
                throw;
            }
        }

        private static IReadOnlyList<RbxWorldModSource> CaptureMods(
            ILuaModSourceStore sourceStore,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            if (sourceStore == null)
            {
                return Array.Empty<RbxWorldModSource>();
            }

            IReadOnlyList<LuaModManifest> listed = sourceStore.List()
                ?? throw new RbxWorldPackageException("The mod source store returned a nil manifest list.");
            if (listed is UnreadableLuaModSourceListing unreadable)
            {
                // WHY: an empty answer here would capture the world without any of its mods, and a
                // startup refresh would record that as the world that opens on the next start.
                throw new RbxWorldPackageException(unreadable.Reason + " Capture aborted.");
            }

            if (listed.Count > MaximumMods)
            {
                throw new RbxWorldPackageFormatLimitException(
                    "The live world stores " + listed.Count + " mod sources; world package format version "
                    + CurrentFormatVersion + " holds at most " + MaximumMods + ". Forget mods that are no "
                    + "longer needed (manage_mods action 'forget'; 'unload' keeps a mod's source) so the "
                    + "world can be captured again.");
            }

            // WHY id order, not load order: the bytes stay deterministic and the reader requires ascending
            // ids; the order the mods start in travels in each manifest's LoadOrder.
            List<LuaModManifest> manifests = new(listed);
            manifests.Sort((left, right) => string.CompareOrdinal(left?.Id, right?.Id));
            List<RbxWorldModSource> mods = new(manifests.Count);
            foreach (LuaModManifest listedManifest in manifests)
            {
                if (listedManifest == null || string.IsNullOrWhiteSpace(listedManifest.Id))
                {
                    throw new RbxWorldPackageException(
                        "The mod source store returned a nil manifest or empty mod id.");
                }

                if (!sourceStore.TryLoad(
                        listedManifest.Id, out string source, out LuaModManifest loadedManifest)
                    || loadedManifest == null || source == null)
                {
                    throw new RbxWorldPackageException(
                        "Mod '" + listedManifest.Id
                        + "' is listed but its source/manifest cannot be loaded; capture aborted.");
                }

                if (!string.Equals(
                        listedManifest.Id, loadedManifest.Id, StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "Mod store key '" + listedManifest.Id + "' loaded manifest id '"
                        + loadedManifest.Id + "'; capture aborted.");
                }

                LuaModManifest captured = CloneManifest(loadedManifest);
                if (captured.LoadOrder > LuaModManifest.MaximumLoadOrder)
                {
                    // WHY: only a hand-edited store holds such an order, every reader takes it for no
                    // order, and a package that carried it would be refused on read.
                    captured.LoadOrder = 0;
                }

                if (TryReplaceLoneSurrogates(source, out string wellFormedSource))
                {
                    diagnostics.Add(IllFormedTextDiagnostic(0UL, "Mods/" + captured.Id + "/source"));
                    source = wellFormedSource;
                }

                mods.Add(new RbxWorldModSource(captured, source));
            }

            return mods;
        }

        private static PackageManifestDto BuildManifest(RbxWorldPackagePayload payload)
        {
            PackageManifestDto manifest = new()
            {
                Format = PackageFormat,
                FormatVersion = CurrentFormatVersion,
                MinimumReaderVersion = CurrentFormatVersion,
                ApiVersion = CurrentApiVersion,
                CreatedUtc = payload.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                WorldEntry = WorldEntryName,
                Mods = new List<PackageModIndexDto>(payload.Mods.Count),
                Diagnostics = BuildDiagnosticDtos(payload.Diagnostics)
            };
            for (int index = 0; index < payload.Mods.Count; index++)
            {
                string prefix = "Mods/" + index.ToString("D4", CultureInfo.InvariantCulture) + "/";
                manifest.Mods.Add(new PackageModIndexDto
                {
                    Id = payload.Mods[index].Manifest.Id,
                    ManifestEntry = prefix + "manifest.json",
                    SourceEntry = prefix + "main.lua"
                });
            }

            return manifest;
        }

        private static List<PackageDiagnosticDto> BuildDiagnosticDtos(
            IReadOnlyList<RbxWorldPackageDiagnostic> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return null;
            }

            List<PackageDiagnosticDto> result = new(diagnostics.Count);
            foreach (RbxWorldPackageDiagnostic diagnostic in diagnostics)
            {
                result.Add(new PackageDiagnosticDto
                {
                    ModelId = U(diagnostic.ModelId),
                    DroppedPrimaryPartId = U(diagnostic.DroppedPrimaryPartId),
                    Reason = diagnostic.Reason,
                    Member = diagnostic.Member
                });
            }

            return result;
        }

        private static IReadOnlyList<RbxWorldPackageDiagnostic> BuildDiagnostics(
            List<PackageDiagnosticDto> dtos)
        {
            if (dtos == null || dtos.Count == 0)
            {
                return Array.Empty<RbxWorldPackageDiagnostic>();
            }

            List<RbxWorldPackageDiagnostic> result = new(dtos.Count);
            foreach (PackageDiagnosticDto dto in dtos)
            {
                result.Add(new RbxWorldPackageDiagnostic(
                    ParseUlong(dto.ModelId, "diagnostic model_id"),
                    ParseUlong(dto.DroppedPrimaryPartId, "diagnostic dropped_primary_part_id"),
                    dto.Reason,
                    dto.Member));
            }

            return result;
        }

        private static WorldFileDto BuildWorldDto(RbxWorldPackagePayload payload)
        {
            WorldFileDto world = new()
            {
                SchemaVersion = CurrentWorldSchemaVersion,
                Settings = new WorldSettingsDto
                {
                    WorldId = payload.Settings.WorldId,
                    MetersPerStud = payload.Settings.MetersPerStud,
                    GravityStudsPerSecondSquared =
                        payload.Settings.GravityStudsPerSecondSquared,
                    SignalBehavior = payload.Settings.SignalBehavior,
                    WorldAclVersion = payload.Tree.WorldAclVersion
                },
                CameraCFrame = payload.CameraCFrame.HasValue
                    ? payload.CameraCFrame.Value.GetComponents()
                    : null,
                Instances = new List<WorldInstanceDto>(payload.Tree.Instances.Count)
            };

            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                WorldInstanceDto dto = new()
                {
                    Id = U(node.Id),
                    ParentId = U(node.ParentId),
                    ClassName = node.ClassName,
                    Name = node.Name,
                    Archivable = node.Archivable,
                    OwnerModId = node.OwnerModId,
                    OriginTag = node.OriginTag,
                    OwnerActorId = node.OwnerActorId,
                    AccessScope = node.AccessScope?.ToString(),
                    Revision = L(node.Revision),
                    Tags = new List<string>(node.Tags),
                    Attributes = BuildAttributes(node.Attributes)
                };

                if (node.Model != null)
                {
                    dto.Model = new WorldModelDto
                    {
                        PrimaryPartId = U(node.Model.PrimaryPartId),
                        HasStoredWorldPivot = node.Model.HasStoredWorldPivot,
                        StoredWorldPivot = node.Model.HasStoredWorldPivot
                            ? ParseFloatComponents(node.Model.StoredWorldPivot, 12)
                            : null
                    };
                }

                if (payload.Parts.TryGetValue(new InstanceId(node.Id), out PartProperties part))
                {
                    dto.Part = BuildPartDto(in part);
                }

                if (node.ClickDetector != null)
                {
                    dto.ClickDetector = new WorldClickDetectorDto
                    {
                        MaxActivationDistance = double.Parse(
                            node.ClickDetector.MaxActivationDistance,
                            CultureInfo.InvariantCulture)
                    };
                }

                if (node.MaterialVariant != null)
                {
                    dto.MaterialVariant = new WorldMaterialVariantDto
                    {
                        BaseMaterial = node.MaterialVariant.BaseMaterial,
                        BaseMaterialValue = node.MaterialVariant.BaseMaterialValue,
                        ColorMap = node.MaterialVariant.ColorMap ?? string.Empty,
                        NormalMap = node.MaterialVariant.NormalMap ?? string.Empty,
                        RoughnessMap = node.MaterialVariant.RoughnessMap ?? string.Empty,
                        MetalnessMap = node.MaterialVariant.MetalnessMap ?? string.Empty,
                        StudsPerTile = float.Parse(
                            node.MaterialVariant.StudsPerTile,
                            CultureInfo.InvariantCulture)
                    };
                }

                if (node.Value != null)
                {
                    dto.Value = new WorldValueDto
                    {
                        StringValue = node.Value.StringValue,
                        ObjectTargetId = U(node.Value.ObjectTargetId)
                    };
                }

                if (node.Humanoid != null)
                {
                    dto.Humanoid = new WorldHumanoidDto
                    {
                        Health = double.Parse(node.Humanoid.Health, CultureInfo.InvariantCulture),
                        MaxHealth = double.Parse(
                            node.Humanoid.MaxHealth, CultureInfo.InvariantCulture),
                        WalkSpeed = double.Parse(
                            node.Humanoid.WalkSpeed, CultureInfo.InvariantCulture),
                        JumpPower = double.Parse(
                            node.Humanoid.JumpPower, CultureInfo.InvariantCulture),
                        JumpHeight = double.Parse(
                            node.Humanoid.JumpHeight, CultureInfo.InvariantCulture),
                        UseJumpPower = node.Humanoid.UseJumpPower,
                        DisplayName = node.Humanoid.DisplayName ?? string.Empty
                    };
                }

                world.Instances.Add(dto);
            }

            return world;
        }

        private static List<WorldAttributeDto> BuildAttributes(
            IReadOnlyList<AttributeSnapshot> attributes)
        {
            List<WorldAttributeDto> result = new(attributes.Count);
            foreach (AttributeSnapshot attribute in attributes)
            {
                result.Add(new WorldAttributeDto
                {
                    Name = attribute.Name,
                    Kind = attribute.Kind.ToString(),
                    StringValue = attribute.StringValue,
                    NumberValue = attribute.NumberValue,
                    BoolValue = attribute.BoolValue
                });
            }

            return result;
        }

        private static WorldPartDto BuildPartDto(in PartProperties part)
        {
            return new WorldPartDto
            {
                Shape = part.Shape.ToString(),
                ShapeValue = (int)part.Shape,
                Material = part.Material.Name,
                MaterialValue = part.Material.Value,
                MaterialVariant = string.IsNullOrEmpty(part.MaterialVariant)
                    ? null
                    : part.MaterialVariant,
                CFrame = part.CFrame.GetComponents(),
                Size = new[] { part.Size.X, part.Size.Y, part.Size.Z },
                Color = new[] { part.Color.R, part.Color.G, part.Color.B },
                ColorWasExplicitlySet = part.ColorWasExplicitlySet,
                Anchored = part.Anchored,
                Transparency = part.Transparency,
                CanCollide = part.CanCollide
            };
        }

        private static RbxWorldPackagePayload BuildPayload(
            PackageManifestDto manifest,
            WorldFileDto world,
            IReadOnlyList<RbxWorldModSource> mods)
        {
            if (world == null)
            {
                throw new RbxWorldPackageException("world.json is nil.");
            }

            if (world.SchemaVersion != CurrentWorldSchemaVersion)
            {
                throw new RbxWorldPackageException(
                    "Unsupported world.json schema version " + world.SchemaVersion
                    + "; this reader supports version " + CurrentWorldSchemaVersion + ".");
            }

            if (world.Settings == null || world.Instances == null)
            {
                throw new RbxWorldPackageException(
                    "world.json schema version 1 requires settings and instances.");
            }

            InstanceTreeSnapshot tree = new()
            {
                WorldAclVersion = world.Settings.WorldAclVersion
            };
            Dictionary<InstanceId, PartProperties> parts = new();
            foreach (WorldInstanceDto dto in world.Instances)
            {
                InstanceSnapshot node = BuildInstanceSnapshot(dto);
                tree.Instances.Add(node);
                if (dto.Part != null)
                {
                    parts.Add(new InstanceId(node.Id), BuildPartProperties(dto.Part));
                }
            }

            DateTime capturedAtUtc;
            if (!DateTime.TryParse(
                    manifest.CreatedUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out capturedAtUtc))
            {
                throw new RbxWorldPackageException(
                    "manifest.json has invalid created_utc '" + manifest.CreatedUtc + "'.");
            }

            RbxWorldSettings settings = new()
            {
                WorldId = world.Settings.WorldId ?? "",
                MetersPerStud = world.Settings.MetersPerStud,
                GravityStudsPerSecondSquared =
                    world.Settings.GravityStudsPerSecondSquared,
                SignalBehavior = world.Settings.SignalBehavior
            };
            RbxCFrame? cameraCFrame = world.CameraCFrame != null
                ? BuildCFrame(world.CameraCFrame, "camera_cframe")
                : (RbxCFrame?)null;
            IReadOnlyList<RbxWorldPackageDiagnostic> diagnostics = BuildDiagnostics(manifest.Diagnostics);
            return new RbxWorldPackagePayload(
                NormalizeUtc(capturedAtUtc), settings, tree, parts, cameraCFrame, mods, diagnostics);
        }

        private static InstanceSnapshot BuildInstanceSnapshot(WorldInstanceDto dto)
        {
            if (dto == null)
            {
                throw new RbxWorldPackageException("world.json contains a nil instance entry.");
            }

            InstanceAccessScope? accessScope = null;
            if (dto.AccessScope != null)
            {
                if (!Enum.TryParse(
                        dto.AccessScope, false, out InstanceAccessScope parsedScope)
                    || !Enum.IsDefined(typeof(InstanceAccessScope), parsedScope))
                {
                    throw new RbxWorldPackageException(
                        "Instance " + dto.Id + " has unsupported access_scope '"
                        + dto.AccessScope + "'.");
                }

                accessScope = parsedScope;
            }

            InstanceSnapshot node = new()
            {
                Id = ParseUlong(dto.Id, "instance id"),
                ParentId = ParseUlong(dto.ParentId, "parent id"),
                ClassName = dto.ClassName,
                Name = dto.Name,
                Archivable = dto.Archivable,
                OwnerModId = dto.OwnerModId,
                OriginTag = dto.OriginTag,
                OwnerActorId = dto.OwnerActorId,
                AccessScope = accessScope,
                Revision = ParseLong(dto.Revision, "instance revision"),
                Tags = dto.Tags ?? new List<string>(),
                Attributes = BuildAttributeSnapshots(dto.Attributes)
            };

            if (dto.Model != null)
            {
                node.Model = new ModelSnapshot
                {
                    PrimaryPartId = ParseUlong(dto.Model.PrimaryPartId, "PrimaryPart id"),
                    HasStoredWorldPivot = dto.Model.HasStoredWorldPivot,
                    StoredWorldPivot = dto.Model.HasStoredWorldPivot
                        ? Join(dto.Model.StoredWorldPivot, 12, "stored WorldPivot")
                        : null
                };
            }

            if (dto.ClickDetector != null)
            {
                node.ClickDetector = new ClickDetectorSnapshot
                {
                    MaxActivationDistance = dto.ClickDetector.MaxActivationDistance.ToString(
                        "R", CultureInfo.InvariantCulture)
                };
            }

            if (dto.MaterialVariant != null)
            {
                node.MaterialVariant = new MaterialVariantSnapshot
                {
                    BaseMaterial = dto.MaterialVariant.BaseMaterial,
                    BaseMaterialValue = dto.MaterialVariant.BaseMaterialValue,
                    ColorMap = dto.MaterialVariant.ColorMap ?? string.Empty,
                    NormalMap = dto.MaterialVariant.NormalMap ?? string.Empty,
                    RoughnessMap = dto.MaterialVariant.RoughnessMap ?? string.Empty,
                    MetalnessMap = dto.MaterialVariant.MetalnessMap ?? string.Empty,
                    StudsPerTile = dto.MaterialVariant.StudsPerTile.ToString(
                        "R", CultureInfo.InvariantCulture)
                };
            }

            if (dto.Value != null)
            {
                node.Value = new ValueSnapshot
                {
                    StringValue = dto.Value.StringValue,
                    ObjectTargetId = ParseUlong(dto.Value.ObjectTargetId, "ObjectValue target id")
                };
            }

            if (dto.Humanoid != null)
            {
                node.Humanoid = new HumanoidSnapshot
                {
                    Health = dto.Humanoid.Health.ToString("R", CultureInfo.InvariantCulture),
                    MaxHealth = dto.Humanoid.MaxHealth.ToString("R", CultureInfo.InvariantCulture),
                    WalkSpeed = dto.Humanoid.WalkSpeed.ToString("R", CultureInfo.InvariantCulture),
                    JumpPower = dto.Humanoid.JumpPower.ToString("R", CultureInfo.InvariantCulture),
                    JumpHeight = dto.Humanoid.JumpHeight.ToString(
                        "R", CultureInfo.InvariantCulture),
                    UseJumpPower = dto.Humanoid.UseJumpPower,
                    DisplayName = dto.Humanoid.DisplayName ?? string.Empty
                };
            }

            return node;
        }

        private static List<AttributeSnapshot> BuildAttributeSnapshots(
            IReadOnlyList<WorldAttributeDto> attributes)
        {
            if (attributes == null)
            {
                throw new RbxWorldPackageException("Instance attributes collection is nil.");
            }

            List<AttributeSnapshot> result = new(attributes.Count);
            foreach (WorldAttributeDto dto in attributes)
            {
                if (dto == null || !Enum.TryParse(
                        dto.Kind, false, out AttributeValueKind kind)
                    || !Enum.IsDefined(typeof(AttributeValueKind), kind))
                {
                    throw new RbxWorldPackageException(
                        "world.json contains an unsupported attribute kind '" + dto?.Kind + "'.");
                }

                result.Add(new AttributeSnapshot
                {
                    Name = dto.Name,
                    Kind = kind,
                    StringValue = dto.StringValue,
                    NumberValue = dto.NumberValue,
                    BoolValue = dto.BoolValue
                });
            }

            return result;
        }

        private static PartProperties BuildPartProperties(WorldPartDto dto)
        {
            if (!Enum.TryParse(dto.Shape, false, out RbxPartShape shape)
                || !Enum.IsDefined(typeof(RbxPartShape), shape)
                || (int)shape != dto.ShapeValue)
            {
                throw new RbxWorldPackageException(
                    "Part state has unsupported Shape '" + dto.Shape + "' ("
                    + dto.ShapeValue + ").");
            }

            RequireLength(dto.Size, 3, "Part.Size");
            RequireLength(dto.Color, 3, "Part.Color");
            PartProperties result = new()
            {
                Shape = shape,
                Material = new RbxMaterialId(dto.Material, dto.MaterialValue),
                MaterialVariant = string.IsNullOrEmpty(dto.MaterialVariant)
                    ? null
                    : dto.MaterialVariant,
                CFrame = BuildCFrame(dto.CFrame, "Part.CFrame"),
                Size = new RbxVector3(dto.Size[0], dto.Size[1], dto.Size[2]),
                Color = new RbxColor3(dto.Color[0], dto.Color[1], dto.Color[2]),
                ColorWasExplicitlySet = dto.ColorWasExplicitlySet,
                Anchored = dto.Anchored,
                Transparency = dto.Transparency,
                CanCollide = dto.CanCollide
            };
            return result;
        }

        private static List<RbxWorldModSource> ReadMods(
            IReadOnlyDictionary<string, ZipArchiveEntry> entries,
            PackageManifestDto manifest)
        {
            List<RbxWorldModSource> mods = new(manifest.Mods.Count);
            foreach (PackageModIndexDto modIndex in manifest.Mods)
            {
                LuaModManifest modManifest = DeserializeJson<LuaModManifest>(
                    ReadRequiredEntry(entries, modIndex.ManifestEntry), modIndex.ManifestEntry);
                string source = StrictUtf8.GetString(
                    ReadRequiredEntry(entries, modIndex.SourceEntry));
                if (modManifest == null
                    || !string.Equals(modManifest.Id, modIndex.Id, StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "Mod index id '" + modIndex.Id + "' does not match its manifest id '"
                        + modManifest?.Id + "'.");
                }

                mods.Add(new RbxWorldModSource(CloneManifest(modManifest), source));
            }

            return mods;
        }

        private static void ValidatePayload(RbxWorldPackagePayload payload, ClassCatalog catalog)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Settings == null || payload.Tree == null
                || payload.Parts == null || payload.Mods == null)
            {
                throw new RbxWorldPackageException(
                    "World package payload requires settings, tree, parts, and mods.");
            }
            if (payload.Mods.Count > MaximumMods)
            {
                throw new RbxWorldPackageException(
                    "World package contains " + payload.Mods.Count + " mods; limit is "
                    + MaximumMods + ".");
            }

            ValidateSettings(payload.Settings);
            InstanceRegistry validationRegistry = new(catalog, worldId: payload.Settings.WorldId);
            InstanceTreeSerializer.Validate(payload.Tree, validationRegistry);
            InstanceSnapshot root = payload.Tree.Instances[0];
            if (root.ParentId != 0UL
                || !string.Equals(root.ClassName, "DataModel", StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "World package format version 1 requires the first tree node to be the DataModel root.");
            }

            int workspaceCount = 0;
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.ParentId == root.Id
                    && string.Equals(node.ClassName, "Workspace", StringComparison.Ordinal))
                {
                    workspaceCount++;
                }
            }

            if (workspaceCount != 1)
            {
                throw new RbxWorldPackageException(
                    "World package format version 1 requires exactly one Workspace child of DataModel.");
            }

            HashSet<InstanceId> expectedParts = new();
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.OwnerModId != null)
                {
                    throw new RbxWorldPackageException(
                        "World package format version 1 cannot contain mod-ephemeral instance id "
                        + node.Id + "; capture only the world-owned projection and restart mods "
                        + "from their packaged sources.");
                }

                if (string.Equals(node.ClassName, "Player", StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "World package format version 1 cannot serialize Player instance id "
                        + node.Id + "; Player identity/lifecycle state requires the MVP8 schema.");
                }

                if (catalog.IsA(node.ClassName, "BasePart"))
                {
                    expectedParts.Add(new InstanceId(node.Id));
                }

                foreach (AttributeSnapshot attribute in node.Attributes)
                {
                    if (attribute.Kind == AttributeValueKind.Number
                        && (double.IsNaN(attribute.NumberValue)
                            || double.IsInfinity(attribute.NumberValue)))
                    {
                        throw new RbxWorldPackageException(
                            "Instance " + node.Id + " attribute '" + attribute.Name
                            + "' is not a finite JSON number.");
                    }
                }
            }

            if (expectedParts.Count != payload.Parts.Count)
            {
                throw new RbxWorldPackageException(
                    "World package Part state count " + payload.Parts.Count
                    + " does not match BasePart count " + expectedParts.Count + ".");
            }

            foreach (KeyValuePair<InstanceId, PartProperties> entry in payload.Parts)
            {
                if (!expectedParts.Contains(entry.Key))
                {
                    throw new RbxWorldPackageException(
                        "World package contains Part state for non-BasePart id "
                        + entry.Key.Value + ".");
                }

                PartProperties properties = entry.Value;
                ValidatePart(entry.Key, in properties);
            }

            HashSet<string> variantNames = new(StringComparer.Ordinal);
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (string.Equals(
                        node.ClassName, "MaterialVariant", StringComparison.Ordinal))
                {
                    variantNames.Add(node.Name);
                    ValidateMaterialVariant(node);
                }
            }

            foreach (KeyValuePair<InstanceId, PartProperties> entry in payload.Parts)
            {
                string variantName = entry.Value.MaterialVariant;
                if (!string.IsNullOrEmpty(variantName) && !variantNames.Contains(variantName))
                {
                    throw new RbxWorldPackageException(
                        "Part " + entry.Key.Value + " names undefined MaterialVariant '"
                        + variantName + "'.");
                }
            }

            if (payload.CameraCFrame.HasValue)
            {
                ValidateFinite(payload.CameraCFrame.Value.GetComponents(), "Camera.CFrame");
            }

            string previousModId = null;
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                if (mod?.Manifest == null || mod.Source == null
                    || string.IsNullOrWhiteSpace(mod.Manifest.Id))
                {
                    throw new RbxWorldPackageException(
                        "World package contains a mod without id, manifest, or source.");
                }

                if (previousModId != null
                    && string.CompareOrdinal(previousModId, mod.Manifest.Id) >= 0)
                {
                    throw new RbxWorldPackageException(
                        "World package mods must be uniquely sorted by id; encountered '"
                        + mod.Manifest.Id + "' after '" + previousModId + "'.");
                }

                // WHY refused rather than followed (C2-08): no store records an order past 2^53 (it
                // counts first loads), so only a hand-made package carries one, and once restored it
                // made every later first load in that world tie at the saturated maximum.
                if (mod.Manifest.LoadOrder > LuaModManifest.MaximumLoadOrder)
                {
                    throw new RbxWorldPackageException(
                        "World package mod '" + mod.Manifest.Id + "' has load order "
                        + mod.Manifest.LoadOrder.ToString(CultureInfo.InvariantCulture)
                        + ", above the largest a mod store records ("
                        + LuaModManifest.MaximumLoadOrder.ToString(CultureInfo.InvariantCulture) + ").");
                }

                previousModId = mod.Manifest.Id;
            }
        }

        private static void ValidateSettings(RbxWorldSettings settings)
        {
            if (settings.MetersPerStud <= 0f || float.IsNaN(settings.MetersPerStud)
                || float.IsInfinity(settings.MetersPerStud))
            {
                throw new RbxWorldPackageException(
                    "World package meters_per_stud must be a positive finite number.");
            }

            if (settings.GravityStudsPerSecondSquared < 0d
                || double.IsNaN(settings.GravityStudsPerSecondSquared)
                || double.IsInfinity(settings.GravityStudsPerSecondSquared))
            {
                throw new RbxWorldPackageException(
                    "World package gravity must be a finite non-negative number.");
            }

            if (!string.Equals(
                    settings.SignalBehavior,
                    RbxWorldSettings.DeferredSignalBehavior,
                    StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "World package signal_behavior '" + settings.SignalBehavior
                    + "' is unsupported; MVP2 requires Deferred.");
            }
        }

        private static void ValidatePart(InstanceId id, in PartProperties part)
        {
            if (!Enum.IsDefined(typeof(RbxPartShape), part.Shape))
            {
                throw new RbxWorldPackageException(
                    "Part " + id.Value + " has unsupported Shape value " + (int)part.Shape + ".");
            }

            if (string.IsNullOrWhiteSpace(part.Material.Name))
            {
                throw new RbxWorldPackageException(
                    "Part " + id.Value + " has an empty Material name.");
            }

            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            if (!materialEnum.TryGetItem(part.Material.Name, out RbxEnumItem materialItem)
                || materialItem.Value != part.Material.Value)
            {
                throw new RbxWorldPackageException(
                    "Part " + id.Value + " has unsupported or mismatched Material '"
                    + part.Material.Name + "' (" + part.Material.Value + ").");
            }

            ValidateFinite(part.CFrame.GetComponents(), "Part " + id.Value + " CFrame");
            ValidateFinite(
                new[] { part.Size.X, part.Size.Y, part.Size.Z },
                "Part " + id.Value + " Size");
            ValidateFinite(
                new[] { part.Color.R, part.Color.G, part.Color.B },
                "Part " + id.Value + " Color");
            if (float.IsNaN(part.Transparency) || float.IsInfinity(part.Transparency)
                || part.Transparency < 0f || part.Transparency > 1f)
            {
                throw new RbxWorldPackageException(
                    "Part " + id.Value + " Transparency must be within [0, 1].");
            }
        }

        private static void ValidateMaterialVariant(InstanceSnapshot node)
        {
            MaterialVariantSnapshot snapshot = node.MaterialVariant;
            if (snapshot == null)
            {
                throw new RbxWorldPackageException(
                    "MaterialVariant " + node.Id + " is missing its variant state.");
            }

            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            if (string.IsNullOrWhiteSpace(snapshot.BaseMaterial)
                || !materialEnum.TryGetItem(snapshot.BaseMaterial, out RbxEnumItem materialItem)
                || materialItem.Value != snapshot.BaseMaterialValue)
            {
                throw new RbxWorldPackageException(
                    "MaterialVariant " + node.Id + " has unsupported or mismatched BaseMaterial '"
                    + snapshot.BaseMaterial + "' (" + snapshot.BaseMaterialValue + ").");
            }

            float studs = float.Parse(snapshot.StudsPerTile, CultureInfo.InvariantCulture);
            if (float.IsNaN(studs) || float.IsInfinity(studs) || studs <= 0f)
            {
                throw new RbxWorldPackageException(
                    "MaterialVariant " + node.Id + " StudsPerTile must be a positive finite number.");
            }
        }

        private static void ValidateFinite(IReadOnlyList<float> values, string field)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                {
                    throw new RbxWorldPackageException(field + " contains a non-finite component.");
                }
            }
        }

        private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive)
        {
            if (archive.Entries.Count > MaximumEntries)
            {
                throw new RbxWorldPackageException(
                    "World package has " + archive.Entries.Count + " ZIP entries; limit is "
                    + MaximumEntries + ".");
            }

            Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
            long expandedBytes = 0L;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    throw new RbxWorldPackageException(
                        "World package contains a directory entry; only indexed files are allowed.");
                }

                if (!entries.TryAdd(entry.FullName, entry))
                {
                    throw new RbxWorldPackageException(
                        "World package contains duplicate ZIP entry '" + entry.FullName + "'.");
                }

                expandedBytes += entry.Length;
                if (expandedBytes > MaximumExpandedPackageBytes)
                {
                    throw new RbxWorldPackageException(
                        "World package expanded size exceeds format version 1 limit "
                        + MaximumExpandedPackageBytes + " bytes.");
                }
            }

            return entries;
        }

        private static void ValidateManifest(PackageManifestDto manifest)
        {
            if (manifest == null)
            {
                throw new RbxWorldPackageException("manifest.json is nil.");
            }

            if (!string.Equals(manifest.Format, PackageFormat, StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "Unsupported world package format '" + manifest.Format + "'.");
            }

            if (manifest.FormatVersion != CurrentFormatVersion)
            {
                throw new RbxWorldPackageException(
                    "Unsupported world package format version " + manifest.FormatVersion
                    + "; this reader supports version " + CurrentFormatVersion + ".");
            }

            if (manifest.MinimumReaderVersion > CurrentFormatVersion)
            {
                throw new RbxWorldPackageException(
                    "World package requires reader version " + manifest.MinimumReaderVersion
                    + "; this reader is version " + CurrentFormatVersion + ".");
            }

            if (!string.Equals(manifest.ApiVersion, CurrentApiVersion, StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "Unsupported world package API version '" + manifest.ApiVersion
                    + "'; this reader supports '" + CurrentApiVersion + "'.");
            }

            if (!string.Equals(manifest.WorldEntry, WorldEntryName, StringComparison.Ordinal)
                || manifest.Mods == null)
            {
                throw new RbxWorldPackageException(
                    "manifest.json format version 1 has an invalid world entry or mod index.");
            }

            if (manifest.Mods.Count > MaximumMods)
            {
                throw new RbxWorldPackageException(
                    "manifest.json contains " + manifest.Mods.Count + " mods; limit is "
                    + MaximumMods + ".");
            }

            string previousId = null;
            for (int index = 0; index < manifest.Mods.Count; index++)
            {
                PackageModIndexDto mod = manifest.Mods[index];
                string expectedPrefix =
                    "Mods/" + index.ToString("D4", CultureInfo.InvariantCulture) + "/";
                if (mod == null || string.IsNullOrWhiteSpace(mod.Id)
                    || !string.Equals(
                        mod.ManifestEntry, expectedPrefix + "manifest.json", StringComparison.Ordinal)
                    || !string.Equals(
                        mod.SourceEntry, expectedPrefix + "main.lua", StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "manifest.json contains an invalid mod index at position " + index + ".");
                }

                if (previousId != null && string.CompareOrdinal(previousId, mod.Id) >= 0)
                {
                    throw new RbxWorldPackageException(
                        "manifest.json mod ids must be uniquely sorted; encountered '"
                        + mod.Id + "' after '" + previousId + "'.");
                }

                previousId = mod.Id;
            }
        }

        private static void ValidateEntryIndex(
            IReadOnlyDictionary<string, ZipArchiveEntry> entries,
            PackageManifestDto manifest)
        {
            HashSet<string> expected = new(StringComparer.Ordinal)
            {
                ManifestEntryName,
                manifest.WorldEntry
            };
            foreach (PackageModIndexDto mod in manifest.Mods)
            {
                expected.Add(mod.ManifestEntry);
                expected.Add(mod.SourceEntry);
            }

            if (entries.Count != expected.Count)
            {
                throw new RbxWorldPackageException(
                    "World package ZIP entry count " + entries.Count
                    + " does not match manifest index count " + expected.Count + ".");
            }

            foreach (string entryName in expected)
            {
                if (!entries.ContainsKey(entryName))
                {
                    throw new RbxWorldPackageException(
                        "World package is missing indexed entry '" + entryName + "'.");
                }
            }
        }

        private static byte[] ReadRequiredEntry(
            IReadOnlyDictionary<string, ZipArchiveEntry> entries,
            string entryName)
        {
            if (!entries.TryGetValue(entryName, out ZipArchiveEntry entry))
            {
                throw new RbxWorldPackageException(
                    "World package is missing required entry '" + entryName + "'.");
            }

            if (entry.Length > MaximumEntryBytes)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + entryName + "' is " + entry.Length
                    + " bytes; limit is " + MaximumEntryBytes + ".");
            }

            using Stream stream = entry.Open();
            using MemoryStream output = new((int)entry.Length);
            byte[] buffer = new byte[81920];
            long total = 0L;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaximumEntryBytes)
                {
                    throw new RbxWorldPackageException(
                        "World package entry '" + entryName
                        + "' exceeds the decompressed size limit " + MaximumEntryBytes + ".");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            ValidateWritableEntry(name, bytes.LongLength);

            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = StableZipTimestamp;
            using Stream stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void ValidateWritableEntry(string name, long byteCount)
        {
            if (byteCount > MaximumEntryBytes)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + name + "' is " + byteCount
                    + " bytes; limit is " + MaximumEntryBytes + ".");
            }
        }

        private static byte[] SerializeJson(object value)
        {
            string json = JsonConvert.SerializeObject(value, CreateJsonSettings());
            // WHY a second replacement over the whole text: a payload that did not come through
            // Capture (settings, mod manifests, a hand-built payload) may still carry a lone
            // surrogate, and the writer must never throw on text it can encode with U+FFFD. The
            // JSON writer emits such a surrogate raw inside a string literal, so this touches only
            // string content. Reading stays strict: invalid UTF-8 bytes are still rejected.
            return StrictUtf8.GetBytes(ReplaceLoneSurrogates(json));
        }

        private static T DeserializeJson<T>(byte[] bytes, string entryName)
        {
            try
            {
                string json = StrictUtf8.GetString(bytes);
                return JsonConvert.DeserializeObject<T>(json, CreateJsonSettings());
            }
            catch (Exception ex)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + entryName + "' is invalid JSON: " + ex.Message,
                    ex);
            }
        }

        private static JsonSerializerSettings CreateJsonSettings()
        {
            return new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                Formatting = Formatting.Indented,
                MaxDepth = 128,
                MissingMemberHandling = MissingMemberHandling.Error,
                NullValueHandling = NullValueHandling.Include,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            };
        }

        private static RbxCFrame BuildCFrame(float[] values, string field)
        {
            RequireLength(values, 12, field);
            ValidateFinite(values, field);
            return new RbxCFrame(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8],
                values[9], values[10], values[11]);
        }

        private static string Join(float[] values, int expected, string field)
        {
            RequireLength(values, expected, field);
            ValidateFinite(values, field);
            string[] text = new string[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                text[index] = values[index].ToString("R", CultureInfo.InvariantCulture);
            }

            return string.Join(",", text);
        }

        private static float[] ParseFloatComponents(string serialized, int expected)
        {
            string[] parts = (serialized ?? "").Split(',');
            if (parts.Length != expected)
            {
                throw new RbxWorldPackageException(
                    "Serialized CFrame has " + parts.Length + " components; expected " + expected + ".");
            }

            float[] result = new float[expected];
            for (int index = 0; index < expected; index++)
            {
                result[index] = float.Parse(parts[index], CultureInfo.InvariantCulture);
            }

            ValidateFinite(result, "serialized CFrame");
            return result;
        }

        private static void RequireLength(float[] values, int expected, string field)
        {
            if (values == null || values.Length != expected)
            {
                throw new RbxWorldPackageException(
                    field + " requires " + expected + " finite components.");
            }
        }

        private static ulong ParseUlong(string text, string field)
        {
            if (!ulong.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out ulong value))
            {
                throw new RbxWorldPackageException(
                    "World package " + field + " '" + text + "' is not an unsigned decimal string.");
            }

            return value;
        }

        private static long ParseLong(string text, string field)
        {
            if (!long.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long value))
            {
                throw new RbxWorldPackageException(
                    "World package " + field + " '" + text + "' is not a decimal string.");
            }

            return value;
        }

        private static string U(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string L(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static RbxWorldSettings CloneSettings(RbxWorldSettings source)
        {
            return new RbxWorldSettings
            {
                WorldId = source.WorldId ?? "",
                MetersPerStud = source.MetersPerStud,
                GravityStudsPerSecondSquared = source.GravityStudsPerSecondSquared,
                SignalBehavior = source.SignalBehavior
            };
        }

        private static IReadOnlyList<RbxWorldModSource> CloneMods(
            IReadOnlyList<RbxWorldModSource> source)
        {
            List<RbxWorldModSource> result = new(source.Count);
            foreach (RbxWorldModSource mod in source)
            {
                result.Add(new RbxWorldModSource(CloneManifest(mod.Manifest), mod.Source));
            }

            return result;
        }

        private static LuaModManifest CloneManifest(LuaModManifest source)
        {
            return new LuaModManifest
            {
                Id = source.Id ?? "",
                Name = source.Name ?? "",
                Description = source.Description ?? "",
                Version = source.Version ?? "",
                Category = source.Category ?? "",
                Tags = source.Tags ?? "",
                Origin = source.Origin ?? "",
                SeededVersion = source.SeededVersion ?? "",
                SeededHash = source.SeededHash ?? "",
                Author = source.Author ?? "",
                OwnerActorId = source.OwnerActorId ?? "",
                Capabilities = source.Capabilities ?? "",
                Active = source.Active,
                UpdateAvailable = source.UpdateAvailable,
                Entry = source.Entry ?? "main.lua",
                LoadOrder = source.LoadOrder,
                SuspendedAfterBudgetTrips = source.SuspendedAfterBudgetTrips
            };
        }

        [Serializable]
        private sealed class PackageManifestDto
        {
            [JsonProperty("format", Required = Required.Always)]
            public string Format;

            [JsonProperty("format_version", Required = Required.Always)]
            public int FormatVersion;

            [JsonProperty("minimum_reader_version", Required = Required.Always)]
            public int MinimumReaderVersion;

            [JsonProperty("api_version", Required = Required.Always)]
            public string ApiVersion;

            [JsonProperty("created_utc", Required = Required.Always)]
            public string CreatedUtc;

            [JsonProperty("world_entry", Required = Required.Always)]
            public string WorldEntry;

            [JsonProperty("mods", Required = Required.Always)]
            public List<PackageModIndexDto> Mods;

            [JsonProperty("diagnostics", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public List<PackageDiagnosticDto> Diagnostics;
        }

        [Serializable]
        private sealed class PackageDiagnosticDto
        {
            [JsonProperty("model_id", Required = Required.Always)]
            public string ModelId;

            [JsonProperty("dropped_primary_part_id", Required = Required.Always)]
            public string DroppedPrimaryPartId;

            [JsonProperty("reason", Required = Required.Always)]
            public string Reason;

            // WHY no format_version bump: the diagnostics array is optional and additive, and this key
            // is omitted when null, so every package the previous writer could produce is still
            // byte-identical and old packages still read. A "non-finite-value" entry, the only one
            // that carries a member, appears only in a world the previous writer refused to save at
            // all, because its capture threw. A reader that predates this key rejects such a manifest
            // (MissingMemberHandling.Error), so it loses nothing it could read before. Every reader
            // still rejects a non-finite value inside world.json.
            [JsonProperty("member", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public string Member;
        }

        [Serializable]
        private sealed class PackageModIndexDto
        {
            [JsonProperty("id", Required = Required.Always)]
            public string Id;

            [JsonProperty("manifest_entry", Required = Required.Always)]
            public string ManifestEntry;

            [JsonProperty("source_entry", Required = Required.Always)]
            public string SourceEntry;
        }

        [Serializable]
        private sealed class WorldFileDto
        {
            [JsonProperty("schema_version", Required = Required.Always)]
            public int SchemaVersion;

            [JsonProperty("settings", Required = Required.Always)]
            public WorldSettingsDto Settings;

            [JsonProperty("camera_cframe")]
            public float[] CameraCFrame;

            [JsonProperty("instances", Required = Required.Always)]
            public List<WorldInstanceDto> Instances;
        }

        [Serializable]
        private sealed class WorldSettingsDto
        {
            [JsonProperty("world_id", Required = Required.Always)]
            public string WorldId;

            [JsonProperty("world_acl_version")]
            public int? WorldAclVersion;

            [JsonProperty("meters_per_stud", Required = Required.Always)]
            public float MetersPerStud;

            [JsonProperty("gravity_studs_per_second_squared", Required = Required.Always)]
            public double GravityStudsPerSecondSquared;

            [JsonProperty("signal_behavior", Required = Required.Always)]
            public string SignalBehavior;
        }

        [Serializable]
        private sealed class WorldInstanceDto
        {
            [JsonProperty("id", Required = Required.Always)]
            public string Id;

            [JsonProperty("parent_id", Required = Required.Always)]
            public string ParentId;

            [JsonProperty("class_name", Required = Required.Always)]
            public string ClassName;

            [JsonProperty("name", Required = Required.Always)]
            public string Name;

            [JsonProperty("archivable", Required = Required.Always)]
            public bool Archivable;

            [JsonProperty("owner_mod_id")]
            public string OwnerModId;

            [JsonProperty("origin_tag")]
            public string OriginTag;

            [JsonProperty("owner_actor_id")]
            public string OwnerActorId;

            [JsonProperty("access_scope")]
            public string AccessScope;

            [JsonProperty("revision", Required = Required.Always)]
            public string Revision;

            [JsonProperty("tags", Required = Required.Always)]
            public List<string> Tags;

            [JsonProperty("attributes", Required = Required.Always)]
            public List<WorldAttributeDto> Attributes;

            [JsonProperty("model")]
            public WorldModelDto Model;

            [JsonProperty("part")]
            public WorldPartDto Part;

            [JsonProperty("click_detector")]
            public WorldClickDetectorDto ClickDetector;

            [JsonProperty("material_variant")]
            public WorldMaterialVariantDto MaterialVariant;

            [JsonProperty("value")]
            public WorldValueDto Value;

            [JsonProperty("humanoid")]
            public WorldHumanoidDto Humanoid;
        }

        [Serializable]
        private sealed class WorldAttributeDto
        {
            [JsonProperty("name", Required = Required.Always)]
            public string Name;

            [JsonProperty("kind", Required = Required.Always)]
            public string Kind;

            [JsonProperty("string_value")]
            public string StringValue;

            [JsonProperty("number_value", Required = Required.Always)]
            public double NumberValue;

            [JsonProperty("bool_value", Required = Required.Always)]
            public bool BoolValue;
        }

        [Serializable]
        private sealed class WorldModelDto
        {
            [JsonProperty("primary_part_id", Required = Required.Always)]
            public string PrimaryPartId;

            [JsonProperty("has_stored_world_pivot", Required = Required.Always)]
            public bool HasStoredWorldPivot;

            [JsonProperty("stored_world_pivot")]
            public float[] StoredWorldPivot;
        }

        [Serializable]
        private sealed class WorldPartDto
        {
            [JsonProperty("shape", Required = Required.Always)]
            public string Shape;

            [JsonProperty("shape_value", Required = Required.Always)]
            public int ShapeValue;

            [JsonProperty("material", Required = Required.Always)]
            public string Material;

            [JsonProperty("material_value", Required = Required.Always)]
            public int MaterialValue;

            [JsonProperty("material_variant", Required = Required.Default)]
            public string MaterialVariant;

            [JsonProperty("cframe", Required = Required.Always)]
            public float[] CFrame;

            [JsonProperty("size", Required = Required.Always)]
            public float[] Size;

            [JsonProperty("color", Required = Required.Always)]
            public float[] Color;

            [JsonProperty("color_was_explicitly_set", Required = Required.Always)]
            public bool ColorWasExplicitlySet;

            [JsonProperty("anchored", Required = Required.Always)]
            public bool Anchored;

            [JsonProperty("transparency", Required = Required.Always)]
            public float Transparency;

            [JsonProperty("can_collide", Required = Required.Always)]
            public bool CanCollide;
        }

        [Serializable]
        private sealed class WorldValueDto
        {
            [JsonProperty("string_value")]
            public string StringValue;

            [JsonProperty("object_target_id", Required = Required.Always)]
            public string ObjectTargetId;
        }

        [Serializable]
        private sealed class WorldHumanoidDto
        {
            [JsonProperty("health", Required = Required.Always)]
            public double Health;

            [JsonProperty("max_health", Required = Required.Always)]
            public double MaxHealth;

            [JsonProperty("walk_speed", Required = Required.Always)]
            public double WalkSpeed;

            [JsonProperty("jump_power", Required = Required.Always)]
            public double JumpPower;

            [JsonProperty("jump_height", Required = Required.Always)]
            public double JumpHeight;

            [JsonProperty("use_jump_power", Required = Required.Always)]
            public bool UseJumpPower;

            [JsonProperty("display_name", Required = Required.Always)]
            public string DisplayName;
        }

        [Serializable]
        private sealed class WorldClickDetectorDto
        {
            [JsonProperty("max_activation_distance", Required = Required.Always)]
            public double MaxActivationDistance;
        }

        [Serializable]
        private sealed class WorldMaterialVariantDto
        {
            [JsonProperty("base_material", Required = Required.Always)]
            public string BaseMaterial;

            [JsonProperty("base_material_value", Required = Required.Always)]
            public int BaseMaterialValue;

            [JsonProperty("color_map", Required = Required.Always)]
            public string ColorMap;

            [JsonProperty("normal_map", Required = Required.Always)]
            public string NormalMap;

            [JsonProperty("roughness_map", Required = Required.Always)]
            public string RoughnessMap;

            [JsonProperty("metalness_map", Required = Required.Always)]
            public string MetalnessMap;

            [JsonProperty("studs_per_tile", Required = Required.Always)]
            public float StudsPerTile;
        }
    }
}
