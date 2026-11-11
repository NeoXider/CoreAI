using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Folders;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using CoreAI.Mods.Rbx.Instances.Terrain;
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

        /// <summary>
        /// The newest <c>minimum_reader_version</c> this reader accepts. Readers up to 7.56.0 accept 1 only;
        /// 2 is format 1 with a <see cref="TerrainEntryName"/> entry.
        /// </summary>
        public const int CurrentReaderVersion = 2;

        /// <summary>
        /// The <c>minimum_reader_version</c> of a package that carries Terrain cells, so a reader that does not
        /// know the entry refuses the package instead of loading the world without its cells. A package without
        /// cells keeps version 1 and is byte-identical to one written before the entry existed.
        /// </summary>
        public const int TerrainMinimumReaderVersion = 2;

        /// <summary>The entry that carries the Terrain cells (<see cref="TerrainVoxelCodec"/>); present only when there are cells.</summary>
        public const string TerrainEntryName = TerrainVoxelCodec.PackageEntryName;

        /// <summary>
        /// Most bytes one chunk takes in <see cref="TerrainEntryName"/>: three coordinate varints of at most
        /// three bytes, a run-count varint of two, and 4,096 runs of a one-byte length, slot and occupancy.
        /// </summary>
        private const long TerrainChunkUpperBoundBytes = 3L * 3L + 2L + 4096L * 3L;

        /// <summary>Most bytes the Terrain entry header takes: signature, version, edge, palette and chunk count.</summary>
        private const long TerrainHeaderUpperBoundBytes = 4L + 2L + 2L + 1L + 2L * 255L + 4L;
        public const int CurrentWorldSchemaVersion = 1;
        public const string CurrentApiVersion = "MVP2";
        public const string PackageFormat = "coreai-rbx-world";
        public const string ManifestEntryName = "manifest.json";
        public const string WorldEntryName = "world.json";
        public const int MaximumPackageBytes = 64 * 1024 * 1024;

        /// <summary>
        /// Largest expanded entry the format allows (see <see cref="RbxWorldPackageLimits"/> for the
        /// measurement behind it); readers up to 7.56.0 enforce <see cref="Version1BaselineMaximumEntryBytes"/>.
        /// </summary>
        public const int MaximumEntryBytes = 64 * 1024 * 1024;

        /// <summary>The entry limit of every reader up to 7.56.0, and of WebGL players.</summary>
        public const int Version1BaselineMaximumEntryBytes = 16 * 1024 * 1024;

        public const int MaximumExpandedPackageBytes = 128 * 1024 * 1024;
        public const int MaximumEntries = 2048;
        public const int MaximumMods = 256;

        /// <summary>
        /// Most persisted mod folders one package holds (<c>mod_folders</c>): 1,024 folders of at most 255 characters
        /// are 261,120 characters, 13% of the WebGL text budget.
        /// </summary>
        public const int MaximumModFolders = 1024;

        /// <summary>
        /// Bytes a ZIP adds around one deflated entry at most (local header, central directory record and
        /// their extra fields), not counting the entry name, which appears twice.
        /// </summary>
        private const long ZipEntryOverheadBytes = 30 + 46 + 64;

        /// <summary>Bytes the end records of a ZIP take at most (end of central directory, ZIP64 record and locator).</summary>
        private const long ZipEndOverheadBytes = 128;

        private static readonly DateTimeOffset StableZipTimestamp =
            new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>The built-in Enum.Material every Part and MaterialVariant is validated against.</summary>
        /// <remarks>
        /// WHY one shared instance: validation built a whole enum registry per Part (136 KB and about
        /// 140 us each), which made capture plus encode of a 2,048-Part world cost 577 ms and 572 MB of
        /// garbage before every AI edit (dev-docs/PERF_BASELINE_2026-11.md, N1). The built-in registry is
        /// fixed data and only read here.
        /// </remarks>
        private static readonly RbxEnum BuiltInMaterialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");

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

        private const string PointLightClassName = "PointLight";

        private const string LightingClassName = "Lighting";

        private const string StarterPlayerClassName = "StarterPlayer";

        private const string StarterGuiClassName = "StarterGui";

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
                diagnostics,
                new RbxWorldPackageOptionalState
                {
                    Terrain = CaptureTerrain(context.Game),
                    PointLights = CapturePointLights(tree, context.Registry),
                    Lighting = CaptureLighting(tree, context.Game),
                    StarterPlayer = CaptureStarterPlayer(tree, context.Game),
                    ModFolders = CaptureModFolders(context.ModFolderListStore),
                    Gui = CaptureGui(tree, context.Registry, diagnostics),
                    StarterGui = CaptureStarterGui(tree, context.Game)
                });
            ValidatePayload(payload, context.Registry.Catalog);
            return payload;
        }

        /// <summary>
        /// A copy-on-write snapshot of the Workspace Terrain's cells (one reference per chunk), or null when
        /// the world has no Terrain or no cell: an empty Terrain writes no entry.
        /// </summary>
        private static TerrainSnapshot CaptureTerrain(RbxDataModel game)
        {
            RbxTerrain terrain = RbxTerrainBootstrap.Find(game.FindFirstChildOfClass("Workspace"));
            return terrain != null && TerrainVoxelCodec.HasContent(terrain.Store)
                ? terrain.Store.CaptureSnapshot()
                : null;
        }

        /// <summary>The properties of every retained PointLight, or null when the tree holds none.</summary>
        private static Dictionary<InstanceId, RbxWorldPackagePointLight> CapturePointLights(
            InstanceTreeSnapshot tree,
            InstanceRegistry registry)
        {
            Dictionary<InstanceId, RbxWorldPackagePointLight> lights = null;
            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (!string.Equals(node.ClassName, PointLightClassName, StringComparison.Ordinal))
                {
                    continue;
                }

                InstanceId id = new(node.Id);
                if (registry.TryGet(id, out RbxInstance instance) && instance is RbxPointLight light)
                {
                    lights ??= new Dictionary<InstanceId, RbxWorldPackagePointLight>();
                    lights.Add(id, RbxWorldPackagePointLight.Of(light));
                }
            }

            return lights;
        }

        /// <summary>
        /// The day-cycle state of the Lighting service when the world drives its lighting and the service is in
        /// the captured tree; null otherwise, so a world that never touched Lighting keeps the scene's own.
        /// </summary>
        private static RbxLightingDurableState? CaptureLighting(InstanceTreeSnapshot tree, RbxDataModel game)
        {
            if (!(game.FindFirstChildOfClass(LightingClassName) is RbxLighting lighting) || !lighting.IsDriven)
            {
                return null;
            }

            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (node.Id == lighting.Id.Value)
                {
                    return lighting.CaptureDurableState();
                }
            }

            return null;
        }

        /// <summary>
        /// The StarterPlayer service's settings when it is in the captured tree and any of them differs from the
        /// class defaults; null otherwise, so a world that kept the defaults writes no block.
        /// </summary>
        private static RbxWorldPackageStarterPlayer? CaptureStarterPlayer(InstanceTreeSnapshot tree, RbxDataModel game)
        {
            if (!(game.FindFirstChildOfClass(StarterPlayerClassName) is RbxStarterPlayer starterPlayer))
            {
                return null;
            }

            RbxWorldPackageStarterPlayer settings = RbxWorldPackageStarterPlayer.Of(starterPlayer);
            if (settings.Equals(RbxWorldPackageStarterPlayer.Defaults))
            {
                return null;
            }

            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (node.Id == starterPlayer.Id.Value)
                {
                    return settings;
                }
            }

            return null;
        }

        /// <summary>
        /// The <c>gui</c> block of every GUI node the projected tree retains (world-owned, wherever it sits; a mod's
        /// own GUI and every PlayerGui copy are not in the tree), or null when it holds none. A lone UTF-16 surrogate
        /// in a text property is replaced in the block only, with an <c>ill-formed-text</c> diagnostic, like every
        /// other text the package writes.
        /// </summary>
        private static Dictionary<InstanceId, RbxWorldPackageGui> CaptureGui(
            InstanceTreeSnapshot tree,
            InstanceRegistry registry,
            List<RbxWorldPackageDiagnostic> diagnostics)
        {
            Dictionary<InstanceId, RbxWorldPackageGui> blocks = null;
            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (!RbxWorldPackageGui.IsGuiClass(node.ClassName))
                {
                    continue;
                }

                InstanceId id = new(node.Id);
                if (!registry.TryGet(id, out RbxInstance instance))
                {
                    continue;
                }

                RbxWorldPackageGui block = RbxWorldPackageGui.Of(instance);
                if (block == null)
                {
                    continue;
                }

                ulong nodeId = node.Id;
                block = block.WithWellFormedText(member => diagnostics.Add(IllFormedTextDiagnostic(nodeId, member)));
                blocks ??= new Dictionary<InstanceId, RbxWorldPackageGui>();
                blocks.Add(id, block);
            }

            return blocks;
        }

        /// <summary>
        /// The StarterGui service's switches when it is in the captured tree and any of them differs from the class
        /// defaults; null otherwise, so a world that never touched them writes no block.
        /// </summary>
        private static RbxWorldPackageStarterGui? CaptureStarterGui(InstanceTreeSnapshot tree, RbxDataModel game)
        {
            if (!(game.FindFirstChildOfClass(StarterGuiClassName) is RbxStarterGui starterGui))
            {
                return null;
            }

            RbxWorldPackageStarterGui switches = RbxWorldPackageStarterGui.Of(starterGui);
            if (switches.IsDefault)
            {
                return null;
            }

            foreach (InstanceSnapshot node in tree.Instances)
            {
                if (node.Id == starterGui.Id.Value)
                {
                    return switches;
                }
            }

            return null;
        }

        /// <summary>
        /// The persisted mod folders as the package writes them (canonical, deduplicated, sorted); a folder the
        /// format refuses fails the capture, so a world never saves a list its reader would refuse.
        /// </summary>
        private static IReadOnlyList<string> CaptureModFolders(IModFolderListStore store)
        {
            if (store == null)
            {
                return null;
            }

            IReadOnlyList<string> folders = store.Load()
                ?? throw new RbxWorldPackageException("The mod folder list store returned a nil list.");
            return BuildModFolders(folders);
        }

        /// <summary>
        /// <paramref name="folders"/> in the form <c>mod_folders</c> holds: each one a valid non-top-level path in its
        /// normalized spelling, case-insensitive duplicates kept once in the first casing, sorted by
        /// <see cref="ModFolderTree.ComparePaths"/>; null for none.
        /// </summary>
        private static List<string> BuildModFolders(IReadOnlyList<string> folders)
        {
            if (folders == null || folders.Count == 0)
            {
                return null;
            }

            List<string> result = new(folders.Count);
            HashSet<string> seen = new(ModFolderPath.Comparer);
            foreach (string folder in folders)
            {
                ModFolderPathParse parse = ModFolderPath.Parse(folder);
                if (!parse.Succeeded || parse.Path.IsRoot)
                {
                    throw new RbxWorldPackageException(
                        "A persisted mod folder '" + folder + "' is not a valid folder path ("
                        + (parse.Succeeded ? "top level" : ModFolderErrors.Code(parse.Error)) + "): " + parse.Message);
                }

                if (seen.Add(parse.Path.Value))
                {
                    result.Add(parse.Path.Value);
                }
            }

            if (result.Count > MaximumModFolders)
            {
                throw new RbxWorldPackageFormatLimitException(
                    "The live world persists " + result.Count.ToString(CultureInfo.InvariantCulture)
                    + " mod folders; world package format version " + CurrentFormatVersion + " holds at most "
                    + MaximumModFolders.ToString(CultureInfo.InvariantCulture) + ". Delete empty folders that are "
                    + "no longer needed so the world can be captured again.");
            }

            result.Sort(ModFolderTree.ComparePaths);
            return result.Count == 0 ? null : result;
        }

        private static bool HasTerrainCells(RbxWorldPackagePayload payload)
        {
            return payload.Terrain != null && payload.Terrain.ChunkCount > 0;
        }

        /// <summary>The canonical <see cref="TerrainEntryName"/> bytes of <paramref name="snapshot"/>.</summary>
        private static byte[] EncodeTerrain(TerrainSnapshot snapshot)
        {
            TerrainChunkStore scratch = new();
            scratch.RestoreSnapshot(snapshot);
            return TerrainVoxelCodec.Encode(scratch);
        }

        /// <summary>
        /// Decodes <see cref="TerrainEntryName"/> into a scratch store held to this platform's Terrain bounds
        /// and budgets, so a refused entry has no side effect; the reader never repairs it.
        /// </summary>
        private static TerrainSnapshot DecodeTerrain(byte[] bytes)
        {
            TerrainChunkStore scratch = new(TerrainLimits.Default);
            try
            {
                TerrainVoxelCodec.DecodeInto(bytes, scratch);
            }
            catch (Exception ex) when (ex is FormatException || ex is RbxError || ex is ArgumentException)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + TerrainEntryName + "' is invalid: " + ex.Message, ex);
            }

            if (scratch.ChunkCount == 0)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + TerrainEntryName + "' holds no cell; a writer leaves the entry out instead.");
            }

            return scratch.CaptureSnapshot();
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
            if (!IsFinite(properties.CFrame))
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

        /// <summary>True when the position and all nine rotation components of <paramref name="cframe"/> are finite.</summary>
        /// <remarks>
        /// WHY not <c>GetComponents()</c>: it allocates a 12-float array, and capture and validation ask
        /// once per Part, so a 16,384-Part world paid 16,384 arrays twice per backup.
        /// </remarks>
        private static bool IsFinite(in RbxCFrame cframe)
        {
            return IsFinite(cframe.X) && IsFinite(cframe.Y) && IsFinite(cframe.Z)
                   && IsFinite(cframe.XVector) && IsFinite(cframe.YVector) && IsFinite(cframe.ZVector);
        }

        private static bool IsFinite(in RbxVector3 vector)
        {
            return IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);
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
            return WritePackage(payload, RbxWorldPackageLimits.Format);
        }

        /// <summary>
        /// Encodes a validated payload held to <paramref name="limits"/> (a platform's budget, at most the
        /// format's); a payload past one of them is refused with <see cref="RbxWorldPackageException"/>.
        /// </summary>
        public static byte[] WritePackage(RbxWorldPackagePayload payload, RbxWorldPackageLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            ValidatePayload(payload, ClassCatalog.CreateMvp1());
            PackageManifestDto manifest = BuildManifest(payload);
            byte[] manifestBytes = SerializeJson(manifest);
            ValidateWritableEntry(ManifestEntryName, manifestBytes.LongLength, limits);
            long worldBound = RbxWorldPackageWorldJson.MeasureUpperBound(payload);
            using MemoryStream world = RbxWorldPackageWorldJson.Write(
                payload, Math.Min(worldBound, (long)limits.MaximumEntryBytes + 1L));
            long expandedBytes = manifestBytes.LongLength + world.Length;
            ValidateWritableEntry(WorldEntryName, world.Length, limits);
            ValidateExpandedSize(expandedBytes, limits);
            byte[][] modManifests = new byte[payload.Mods.Count][];
            for (int index = 0; index < payload.Mods.Count; index++)
            {
                RbxWorldModSource mod = payload.Mods[index];
                PackageModIndexDto modIndex = manifest.Mods[index];
                byte[] modManifestBytes = SerializeJson(mod.Manifest);
                int sourceByteCount = StrictUtf8.GetByteCount(ReplaceLoneSurrogates(mod.Source));
                ValidateWritableEntry(modIndex.ManifestEntry, modManifestBytes.LongLength, limits);
                ValidateWritableEntry(modIndex.SourceEntry, sourceByteCount, limits);
                expandedBytes += modManifestBytes.LongLength + sourceByteCount;
                ValidateExpandedSize(expandedBytes, limits);
                modManifests[index] = modManifestBytes;
            }

            byte[] terrainBytes = HasTerrainCells(payload) ? EncodeTerrain(payload.Terrain) : null;
            if (terrainBytes != null)
            {
                ValidateWritableEntry(TerrainEntryName, terrainBytes.LongLength, limits);
                expandedBytes += terrainBytes.LongLength;
                ValidateExpandedSize(expandedBytes, limits);
            }

            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
            {
                WriteEntry(archive, ManifestEntryName, manifestBytes, manifestBytes.Length, limits);
                WriteEntry(archive, WorldEntryName, world.GetBuffer(), (int)world.Length, limits);
                for (int index = 0; index < payload.Mods.Count; index++)
                {
                    RbxWorldModSource mod = payload.Mods[index];
                    PackageModIndexDto modIndex = manifest.Mods[index];
                    WriteEntry(archive, modIndex.ManifestEntry, modManifests[index], modManifests[index].Length, limits);
                    byte[] source = StrictUtf8.GetBytes(ReplaceLoneSurrogates(mod.Source));
                    WriteEntry(archive, modIndex.SourceEntry, source, source.Length, limits);
                }

                if (terrainBytes != null)
                {
                    WriteEntry(archive, TerrainEntryName, terrainBytes, terrainBytes.Length, limits);
                }
            }

            if (output.Length > limits.MaximumPackageBytes)
            {
                throw new RbxWorldPackageException(
                    "World package is " + output.Length + " bytes; format version 1 limit is "
                    + limits.MaximumPackageBytes + " bytes.");
            }

            return output.ToArray();
        }

        /// <summary>
        /// Decides before encoding whether <see cref="WritePackage(RbxWorldPackagePayload, RbxWorldPackageLimits)"/>
        /// can fail: validates <paramref name="payload"/> exactly as the writer does (and throws what it
        /// would throw), then bounds every entry, the expanded total and the compressed package from above
        /// without formatting a number or deflating a byte. True proves the encode cannot fail; false means
        /// the bound is not tight enough to prove it, and only the encode itself can tell.
        /// </summary>
        /// <param name="packageBytesUpperBound">The compressed package can never be larger than this.</param>
        internal static bool TryProveEncodable(
            RbxWorldPackagePayload payload,
            RbxWorldPackageLimits limits,
            out long packageBytesUpperBound)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            packageBytesUpperBound = 0L;
            ValidatePayload(payload, ClassCatalog.CreateMvp1());
            PackageManifestDto manifest = BuildManifest(payload);
            long manifestBytes = SerializeJson(manifest).LongLength;
            long worldBytes = RbxWorldPackageWorldJson.MeasureUpperBound(payload);
            if (manifestBytes > limits.MaximumEntryBytes || worldBytes > limits.MaximumEntryBytes)
            {
                return false;
            }

            long expandedBytes = manifestBytes + worldBytes;
            long compressedBytes = ZipEndOverheadBytes
                                   + ZipEntryUpperBound(ManifestEntryName, manifestBytes)
                                   + ZipEntryUpperBound(WorldEntryName, worldBytes);
            for (int index = 0; index < payload.Mods.Count; index++)
            {
                RbxWorldModSource mod = payload.Mods[index];
                PackageModIndexDto modIndex = manifest.Mods[index];
                long modManifestBytes = SerializeJson(mod.Manifest).LongLength;
                long sourceBytes = StrictUtf8.GetByteCount(ReplaceLoneSurrogates(mod.Source));
                if (modManifestBytes > limits.MaximumEntryBytes || sourceBytes > limits.MaximumEntryBytes)
                {
                    return false;
                }

                expandedBytes += modManifestBytes + sourceBytes;
                compressedBytes += ZipEntryUpperBound(modIndex.ManifestEntry, modManifestBytes)
                                   + ZipEntryUpperBound(modIndex.SourceEntry, sourceBytes);
            }

            if (HasTerrainCells(payload))
            {
                long terrainBytes = TerrainHeaderUpperBoundBytes + payload.Terrain.ChunkCount * TerrainChunkUpperBoundBytes;
                if (terrainBytes > limits.MaximumEntryBytes)
                {
                    return false;
                }

                expandedBytes += terrainBytes;
                compressedBytes += ZipEntryUpperBound(TerrainEntryName, terrainBytes);
            }

            if (expandedBytes > limits.MaximumExpandedPackageBytes || compressedBytes > limits.MaximumPackageBytes)
            {
                return false;
            }

            packageBytesUpperBound = compressedBytes;
            return true;
        }

        /// <summary>
        /// The most bytes one entry of <paramref name="expandedBytes"/> can take inside the ZIP: zlib's
        /// conservative deflate bound for any compression parameters (stored blocks included), plus the
        /// entry's headers.
        /// </summary>
        private static long ZipEntryUpperBound(string name, long expandedBytes)
        {
            long deflated = expandedBytes + ((expandedBytes + 7L) >> 3) + ((expandedBytes + 63L) >> 6) + 5L;
            return deflated + ZipEntryOverheadBytes + 2L * StrictUtf8.GetByteCount(name);
        }

        /// <summary>
        /// A SHA-256 digest of every entry <see cref="WritePackage(RbxWorldPackagePayload)"/> would write for
        /// <paramref name="payload"/>, in entry order with names and lengths, without deflating: two payloads
        /// with the same digest encode to the same package.
        /// </summary>
        internal static byte[] ComputeEntryDigest(RbxWorldPackagePayload payload)
        {
            ValidatePayload(payload, ClassCatalog.CreateMvp1());
            PackageManifestDto manifest = BuildManifest(payload);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] manifestBytes = SerializeJson(manifest);
            AppendEntry(hash, ManifestEntryName, manifestBytes, manifestBytes.Length);
            using (MemoryStream world = RbxWorldPackageWorldJson.Write(payload, 0L))
            {
                AppendEntry(hash, WorldEntryName, world.GetBuffer(), (int)world.Length);
            }

            for (int index = 0; index < payload.Mods.Count; index++)
            {
                RbxWorldModSource mod = payload.Mods[index];
                PackageModIndexDto modIndex = manifest.Mods[index];
                byte[] modManifestBytes = SerializeJson(mod.Manifest);
                AppendEntry(hash, modIndex.ManifestEntry, modManifestBytes, modManifestBytes.Length);
                byte[] source = StrictUtf8.GetBytes(ReplaceLoneSurrogates(mod.Source));
                AppendEntry(hash, modIndex.SourceEntry, source, source.Length);
            }

            if (HasTerrainCells(payload))
            {
                byte[] terrain = EncodeTerrain(payload.Terrain);
                AppendEntry(hash, TerrainEntryName, terrain, terrain.Length);
            }

            return hash.GetHashAndReset();
        }

        private static void AppendEntry(IncrementalHash hash, string name, byte[] bytes, int count)
        {
            byte[] header = StrictUtf8.GetBytes(name + "\n" + count.ToString(CultureInfo.InvariantCulture) + "\n");
            hash.AppendData(header);
            hash.AppendData(bytes, 0, count);
        }

        private static void ValidateExpandedSize(long expandedBytes, RbxWorldPackageLimits limits)
        {
            if (expandedBytes > limits.MaximumExpandedPackageBytes)
            {
                throw new RbxWorldPackageException(
                    "World package expands to " + expandedBytes
                    + " bytes; format version 1 limit is "
                    + limits.MaximumExpandedPackageBytes + " bytes.");
            }
        }

        /// <summary>Decodes and validates every ZIP entry before returning a canonical payload.</summary>
        public static RbxWorldPackagePayload ReadPackage(byte[] packageBytes)
        {
            return ReadPackage(packageBytes, RbxWorldPackageLimits.Format);
        }

        /// <summary>
        /// Decodes a package held to <paramref name="limits"/>: a package, entry or expanded total past one
        /// of them is refused before it is inflated (by the sizes the ZIP declares) and again while
        /// inflating (by the bytes it actually yields).
        /// </summary>
        public static RbxWorldPackagePayload ReadPackage(byte[] packageBytes, RbxWorldPackageLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (packageBytes == null || packageBytes.Length == 0)
            {
                throw new RbxWorldPackageException("World package is empty.");
            }

            if (packageBytes.Length > limits.MaximumPackageBytes)
            {
                throw new RbxWorldPackageException(
                    "World package is " + packageBytes.Length + " bytes; format version 1 limit is "
                    + limits.MaximumPackageBytes + " bytes.");
            }

            try
            {
                using MemoryStream input = new(packageBytes, false);
                using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
                Dictionary<string, ZipArchiveEntry> entries = IndexEntries(archive, limits);
                PackageManifestDto manifest = DeserializeJson<PackageManifestDto>(
                    ReadRequiredEntry(entries, ManifestEntryName, limits), ManifestEntryName);
                ValidateManifest(manifest);
                ValidateEntryIndex(entries, manifest);

                WorldFileDto world = DeserializeJson<WorldFileDto>(
                    ReadRequiredEntry(entries, manifest.WorldEntry, limits), manifest.WorldEntry);
                List<RbxWorldModSource> mods = ReadMods(entries, manifest, limits);
                TerrainSnapshot terrain = entries.ContainsKey(TerrainEntryName)
                    ? DecodeTerrain(ReadRequiredEntry(entries, TerrainEntryName, limits))
                    : null;
                RbxWorldPackagePayload payload = BuildPayload(manifest, world, mods, terrain);
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
            if (HasTerrainCells(payload) && !catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                throw new RbxWorldPackageException(
                    "World package carries Terrain cells, but this composition's class catalog has no Terrain "
                    + "class to restore them into.");
            }

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
                RegistryAuthority authority = options?.RegistryAuthority ?? RegistryAuthority.Authoritative;
                InstanceRegistry registry = new(
                    catalog,
                    backingBinder,
                    worldAclVersion: null,
                    worldId: payload.Settings.WorldId,
                    authority: authority);
                string hostActorId = string.IsNullOrWhiteSpace(options?.HostActorId)
                    ? LocalActorIdentityProvider.DefaultActorId
                    : options.HostActorId.Trim();
                // WHY one scope around the whole restore: a replica's revisions are the server's, so every
                // write the restore makes must count as applied replication; outside the scope each would
                // mark its instance diverged.
                using ReplicationApplyScope replication = authority == RegistryAuthority.Replica
                    ? registry.BeginReplicationApply()
                    : null;
                game = (RbxDataModel)InstanceTreeSerializer.Restore(
                    payload.Tree, registry, hostActorId);

                foreach (KeyValuePair<InstanceId, PartProperties> entry in payload.Parts)
                {
                    PartProperties properties = entry.Value;
                    partSink.SetPartProperties(entry.Key, in properties);
                }

                foreach (KeyValuePair<InstanceId, RbxWorldPackagePointLight> entry in payload.PointLights)
                {
                    if (registry.TryGet(entry.Key, out RbxInstance instance) && instance is RbxPointLight light)
                    {
                        entry.Value.ApplyTo(light);
                    }
                }

                foreach (KeyValuePair<InstanceId, RbxWorldPackageGui> entry in payload.Gui)
                {
                    if (registry.TryGet(entry.Key, out RbxInstance instance))
                    {
                        entry.Value.ApplyTo(instance);
                    }
                }

                registry.SetSceneRoot(game);
                RbxInstance workspace = game.FindFirstChildOfClass("Workspace")
                    ?? throw new RbxWorldPackageException(
                        "World package DataModel has no Workspace service.");
                registry.SetWorldRoot(workspace);
                // WHY only with cells: a world without them gets its Terrain from the host's
                // DataModelBootstrap.AttachWorldRoot, and creating one here would hand it the next id.
                if (HasTerrainCells(payload))
                {
                    RbxTerrainBootstrap.EnsureTerrain(registry, workspace).Store.RestoreSnapshot(payload.Terrain);
                }

                if (payload.StarterPlayer.HasValue
                    && game.FindFirstChildOfClass(StarterPlayerClassName) is RbxStarterPlayer starterPlayer)
                {
                    payload.StarterPlayer.Value.ApplyTo(starterPlayer);
                }

                if (payload.StarterGui.HasValue
                    && game.FindFirstChildOfClass(StarterGuiClassName) is RbxStarterGui starterGui)
                {
                    payload.StarterGui.Value.ApplyTo(starterGui);
                }

                if (payload.Lighting.HasValue && game.FindFirstChildOfClass(LightingClassName) is RbxLighting lighting)
                {
                    RbxLightingDurableState state = payload.Lighting.Value;
                    lighting.RestoreDurableState(in state);
                }

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
                MinimumReaderVersion = HasTerrainCells(payload) ? TerrainMinimumReaderVersion : CurrentFormatVersion,
                ApiVersion = CurrentApiVersion,
                CreatedUtc = payload.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                WorldEntry = WorldEntryName,
                Mods = new List<PackageModIndexDto>(payload.Mods.Count),
                Diagnostics = BuildDiagnosticDtos(payload.Diagnostics),
                ModFolders = BuildModFolders(payload.ModFolders)
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

        /// <summary>
        /// <c>world.json</c> as the serializer writes the reader's DTO graph: the reference the direct writer
        /// (<see cref="RbxWorldPackageWorldJson"/>) must match byte for byte. Tests only.
        /// </summary>
        internal static byte[] SerializeWorldThroughDtosForTests(RbxWorldPackagePayload payload)
        {
            return SerializeJson(BuildWorldDto(payload));
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

                if (payload.PointLights.TryGetValue(new InstanceId(node.Id), out RbxWorldPackagePointLight light))
                {
                    dto.Light = new WorldLightDto
                    {
                        Brightness = light.Brightness,
                        Color = new[] { light.Color.R, light.Color.G, light.Color.B },
                        Enabled = light.Enabled,
                        Shadows = light.Shadows,
                        Range = light.Range
                    };
                }

                if (payload.Gui.TryGetValue(new InstanceId(node.Id), out RbxWorldPackageGui gui))
                {
                    dto.Gui = WorldGuiDto.Of(gui);
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

            if (payload.StarterPlayer.HasValue)
            {
                RbxWorldPackageStarterPlayer starterPlayer = payload.StarterPlayer.Value;
                world.StarterPlayer = new WorldStarterPlayerDto
                {
                    DevComputerMovementMode = starterPlayer.DevComputerMovementMode.ToString(),
                    CameraMode = starterPlayer.CameraMode.ToString(),
                    CameraMaxZoomDistance = starterPlayer.CameraMaxZoomDistance,
                    CameraMinZoomDistance = starterPlayer.CameraMinZoomDistance
                };
            }

            if (payload.StarterGui.HasValue)
            {
                RbxWorldPackageStarterGui starterGui = payload.StarterGui.Value;
                world.StarterGui = new WorldStarterGuiDto
                {
                    ShowDevelopmentGui = starterGui.ShowDevelopmentGui,
                    ScreenOrientation = starterGui.ScreenOrientation,
                    CoreGuiDisabled = new List<int>(starterGui.CoreGuiDisabled)
                };
            }

            if (payload.Lighting.HasValue)
            {
                RbxLightingDurableState lighting = payload.Lighting.Value;
                world.Lighting = new WorldLightingDto
                {
                    ClockTime = lighting.ClockTime,
                    GeographicLatitude = lighting.GeographicLatitude,
                    Brightness = lighting.Brightness,
                    Ambient = new[] { lighting.Ambient.R, lighting.Ambient.G, lighting.Ambient.B },
                    OutdoorAmbient = new[] { lighting.OutdoorAmbient.R, lighting.OutdoorAmbient.G, lighting.OutdoorAmbient.B }
                };
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
            IReadOnlyList<RbxWorldModSource> mods,
            TerrainSnapshot terrain)
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
            Dictionary<InstanceId, RbxWorldPackagePointLight> lights = null;
            Dictionary<InstanceId, RbxWorldPackageGui> gui = null;
            foreach (WorldInstanceDto dto in world.Instances)
            {
                InstanceSnapshot node = BuildInstanceSnapshot(dto);
                tree.Instances.Add(node);
                if (dto.Part != null)
                {
                    parts.Add(new InstanceId(node.Id), BuildPartProperties(dto.Part));
                }

                if (dto.Light != null)
                {
                    lights ??= new Dictionary<InstanceId, RbxWorldPackagePointLight>();
                    if (!lights.TryAdd(new InstanceId(node.Id), BuildPointLight(dto.Light)))
                    {
                        throw new RbxWorldPackageException("world.json repeats instance id " + node.Id + ".");
                    }
                }

                if (dto.Gui != null)
                {
                    gui ??= new Dictionary<InstanceId, RbxWorldPackageGui>();
                    if (!gui.TryAdd(
                            new InstanceId(node.Id),
                            RbxWorldPackageGuiSchema.FromEntries(node.ClassName, node.Id, dto.Gui.Entries)))
                    {
                        throw new RbxWorldPackageException("world.json repeats instance id " + node.Id + ".");
                    }
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
                NormalizeUtc(capturedAtUtc), settings, tree, parts, cameraCFrame, mods, diagnostics,
                new RbxWorldPackageOptionalState
                {
                    Terrain = terrain,
                    PointLights = lights,
                    Lighting = world.Lighting == null ? (RbxLightingDurableState?)null : BuildLighting(world.Lighting),
                    StarterPlayer = world.StarterPlayer == null
                        ? (RbxWorldPackageStarterPlayer?)null
                        : BuildStarterPlayer(world.StarterPlayer),
                    ModFolders = manifest.ModFolders,
                    Gui = gui,
                    StarterGui = world.StarterGui == null
                        ? (RbxWorldPackageStarterGui?)null
                        : new RbxWorldPackageStarterGui(
                            world.StarterGui.ShowDevelopmentGui,
                            world.StarterGui.ScreenOrientation,
                            world.StarterGui.CoreGuiDisabled
                            ?? throw new RbxWorldPackageException("starter_gui.core_gui_disabled is nil."))
                });
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

        private static RbxWorldPackagePointLight BuildPointLight(WorldLightDto dto)
        {
            RequireLength(dto.Color, 3, "light.color");
            return new RbxWorldPackagePointLight(
                dto.Brightness, new RbxColor3(dto.Color[0], dto.Color[1], dto.Color[2]), dto.Enabled, dto.Shadows, dto.Range);
        }

        private static RbxWorldPackageStarterPlayer BuildStarterPlayer(WorldStarterPlayerDto dto)
        {
            if (!Enum.TryParse(dto.DevComputerMovementMode, false, out RbxDevComputerMovementMode movement)
                || !Enum.IsDefined(typeof(RbxDevComputerMovementMode), movement)
                || !string.Equals(movement.ToString(), dto.DevComputerMovementMode, StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "starter_player has unsupported dev_computer_movement_mode '" + dto.DevComputerMovementMode + "'.");
            }

            if (!Enum.TryParse(dto.CameraMode, false, out RbxCameraMode cameraMode)
                || !Enum.IsDefined(typeof(RbxCameraMode), cameraMode)
                || !string.Equals(cameraMode.ToString(), dto.CameraMode, StringComparison.Ordinal))
            {
                throw new RbxWorldPackageException(
                    "starter_player has unsupported camera_mode '" + dto.CameraMode + "'.");
            }

            return new RbxWorldPackageStarterPlayer(
                movement, cameraMode, dto.CameraMaxZoomDistance, dto.CameraMinZoomDistance);
        }

        private static RbxLightingDurableState BuildLighting(WorldLightingDto dto)
        {
            RequireLength(dto.Ambient, 3, "lighting.ambient");
            RequireLength(dto.OutdoorAmbient, 3, "lighting.outdoor_ambient");
            return new RbxLightingDurableState(
                dto.ClockTime,
                dto.GeographicLatitude,
                dto.Brightness,
                new RbxColor3(dto.Ambient[0], dto.Ambient[1], dto.Ambient[2]),
                new RbxColor3(dto.OutdoorAmbient[0], dto.OutdoorAmbient[1], dto.OutdoorAmbient[2]));
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
            PackageManifestDto manifest,
            RbxWorldPackageLimits limits)
        {
            List<RbxWorldModSource> mods = new(manifest.Mods.Count);
            foreach (PackageModIndexDto modIndex in manifest.Mods)
            {
                LuaModManifest modManifest = DeserializeJson<LuaModManifest>(
                    ReadRequiredEntry(entries, modIndex.ManifestEntry, limits), modIndex.ManifestEntry);
                string source = StrictUtf8.GetString(
                    ReadRequiredEntry(entries, modIndex.SourceEntry, limits));
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

            ValidateLights(payload);
            ValidateLighting(payload, root);
            ValidateStarterPlayer(payload, root);
            ValidateGui(payload);
            ValidateStarterGui(payload, root);

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

        private static void ValidateLights(RbxWorldPackagePayload payload)
        {
            if (payload.PointLights.Count == 0)
            {
                return;
            }

            Dictionary<ulong, string> classes = new(payload.Tree.Instances.Count);
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                classes[node.Id] = node.ClassName;
            }

            foreach (KeyValuePair<InstanceId, RbxWorldPackagePointLight> entry in payload.PointLights)
            {
                if (!classes.TryGetValue(entry.Key.Value, out string className)
                    || !string.Equals(className, PointLightClassName, StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "World package carries PointLight state for instance " + entry.Key.Value
                        + ", which is not a PointLight of the tree.");
                }

                RbxWorldPackagePointLight light = entry.Value;
                if (!IsFinite(light.Brightness) || !IsFinite(light.Color.R) || !IsFinite(light.Color.G)
                    || !IsFinite(light.Color.B) || !IsFinite(light.Range)
                    || light.Range < 0f || light.Range > RbxPointLight.MaxRange)
                {
                    throw new RbxWorldPackageException(
                        "PointLight " + entry.Key.Value + " needs a finite brightness and colour and a range within [0, "
                        + RbxPointLight.MaxRange.ToString(CultureInfo.InvariantCulture) + "].");
                }
            }
        }

        private static void ValidateLighting(RbxWorldPackagePayload payload, InstanceSnapshot root)
        {
            if (!payload.Lighting.HasValue)
            {
                return;
            }

            RbxLightingDurableState lighting = payload.Lighting.Value;
            if (!IsFinite(lighting.ClockTime) || lighting.ClockTime < 0f || lighting.ClockTime >= 24f
                || !IsFinite(lighting.GeographicLatitude) || !IsFinite(lighting.Brightness)
                || !IsFinite(lighting.Ambient.R) || !IsFinite(lighting.Ambient.G) || !IsFinite(lighting.Ambient.B)
                || !IsFinite(lighting.OutdoorAmbient.R) || !IsFinite(lighting.OutdoorAmbient.G)
                || !IsFinite(lighting.OutdoorAmbient.B))
            {
                throw new RbxWorldPackageException(
                    "World package lighting needs finite values and a clock_time within [0, 24).");
            }

            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.ParentId == root.Id && string.Equals(node.ClassName, LightingClassName, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new RbxWorldPackageException(
                "World package carries a lighting block but its tree has no Lighting service.");
        }

        private static void ValidateStarterPlayer(RbxWorldPackagePayload payload, InstanceSnapshot root)
        {
            if (!payload.StarterPlayer.HasValue)
            {
                return;
            }

            RbxWorldPackageStarterPlayer settings = payload.StarterPlayer.Value;
            if (!Enum.IsDefined(typeof(RbxDevComputerMovementMode), settings.DevComputerMovementMode)
                || !Enum.IsDefined(typeof(RbxCameraMode), settings.CameraMode)
                || double.IsNaN(settings.CameraMinZoomDistance) || double.IsInfinity(settings.CameraMinZoomDistance)
                || double.IsNaN(settings.CameraMaxZoomDistance) || double.IsInfinity(settings.CameraMaxZoomDistance)
                || settings.CameraMinZoomDistance < 0d
                || settings.CameraMaxZoomDistance < settings.CameraMinZoomDistance)
            {
                throw new RbxWorldPackageException(
                    "World package starter_player needs defined modes and finite zoom distances with "
                    + "0 <= camera_min_zoom_distance <= camera_max_zoom_distance.");
            }

            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.ParentId == root.Id
                    && string.Equals(node.ClassName, StarterPlayerClassName, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new RbxWorldPackageException(
                "World package carries a starter_player block but its tree has no StarterPlayer service.");
        }

        /// <summary>
        /// Refuses a <c>gui</c> block on a node that is not of its class, and a value the class's setter would refuse,
        /// so restore, which puts every block back through the setters, never fails half-way.
        /// </summary>
        private static void ValidateGui(RbxWorldPackagePayload payload)
        {
            if (payload.Gui.Count == 0)
            {
                return;
            }

            Dictionary<ulong, string> classes = new(payload.Tree.Instances.Count);
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                classes[node.Id] = node.ClassName;
            }

            foreach (KeyValuePair<InstanceId, RbxWorldPackageGui> entry in payload.Gui)
            {
                string id = entry.Key.Value.ToString(CultureInfo.InvariantCulture);
                if (entry.Value == null
                    || !classes.TryGetValue(entry.Key.Value, out string className)
                    || !string.Equals(className, entry.Value.ClassName, StringComparison.Ordinal))
                {
                    throw new RbxWorldPackageException(
                        "World package carries GUI state for instance " + id + ", which is not a "
                        + (entry.Value?.ClassName ?? "GUI node") + " of the tree.");
                }

                string problem = RbxWorldPackageGuiSchema.DescribeInvalid(entry.Value);
                if (problem != null)
                {
                    throw new RbxWorldPackageException(
                        "World package gui block of " + className + " " + id + " is invalid: " + problem + ".");
                }
            }
        }

        private static void ValidateStarterGui(RbxWorldPackagePayload payload, InstanceSnapshot root)
        {
            if (!payload.StarterGui.HasValue)
            {
                return;
            }

            string problem = payload.StarterGui.Value.DescribeInvalidList();
            if (problem != null)
            {
                throw new RbxWorldPackageException("World package starter_gui is invalid: " + problem + ".");
            }

            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.ParentId == root.Id
                    && string.Equals(node.ClassName, StarterGuiClassName, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new RbxWorldPackageException(
                "World package carries a starter_gui block but its tree has no StarterGui service.");
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

            if (!BuiltInMaterialEnum.TryGetItem(part.Material.Name, out RbxEnumItem materialItem)
                || materialItem.Value != part.Material.Value)
            {
                throw new RbxWorldPackageException(
                    "Part " + id.Value + " has unsupported or mismatched Material '"
                    + part.Material.Name + "' (" + part.Material.Value + ").");
            }

            if (!IsFinite(part.CFrame))
            {
                throw new RbxWorldPackageException("Part " + id.Value + " CFrame contains a non-finite component.");
            }

            if (!IsFinite(part.Size))
            {
                throw new RbxWorldPackageException("Part " + id.Value + " Size contains a non-finite component.");
            }

            if (!IsFinite(part.Color.R) || !IsFinite(part.Color.G) || !IsFinite(part.Color.B))
            {
                throw new RbxWorldPackageException("Part " + id.Value + " Color contains a non-finite component.");
            }
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

            if (string.IsNullOrWhiteSpace(snapshot.BaseMaterial)
                || !BuiltInMaterialEnum.TryGetItem(snapshot.BaseMaterial, out RbxEnumItem materialItem)
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

        private static Dictionary<string, ZipArchiveEntry> IndexEntries(
            ZipArchive archive,
            RbxWorldPackageLimits limits)
        {
            if (archive.Entries.Count > limits.MaximumEntries)
            {
                throw new RbxWorldPackageException(
                    "World package has " + archive.Entries.Count + " ZIP entries; limit is "
                    + limits.MaximumEntries + ".");
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
                if (expandedBytes > limits.MaximumExpandedPackageBytes)
                {
                    throw new RbxWorldPackageException(
                        "World package expanded size exceeds format version 1 limit "
                        + limits.MaximumExpandedPackageBytes + " bytes.");
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

            if (manifest.MinimumReaderVersion > CurrentReaderVersion)
            {
                throw new RbxWorldPackageException(
                    "World package requires reader version " + manifest.MinimumReaderVersion
                    + "; this reader is version " + CurrentReaderVersion + ".");
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

            ValidateModFolders(manifest.ModFolders);
        }

        /// <summary>
        /// Refuses a <c>mod_folders</c> list that no writer produces: empty (a writer leaves the key out), longer than
        /// <see cref="MaximumModFolders"/>, or holding an entry that is not a canonical non-top-level path, a
        /// case-insensitive duplicate, or out of <see cref="ModFolderTree.ComparePaths"/> order.
        /// </summary>
        private static void ValidateModFolders(List<string> folders)
        {
            if (folders == null)
            {
                return;
            }

            if (folders.Count == 0)
            {
                throw new RbxWorldPackageException(
                    "manifest.json mod_folders is empty; a writer leaves the key out instead.");
            }

            if (folders.Count > MaximumModFolders)
            {
                throw new RbxWorldPackageException(
                    "manifest.json contains " + folders.Count.ToString(CultureInfo.InvariantCulture)
                    + " mod folders; limit is " + MaximumModFolders.ToString(CultureInfo.InvariantCulture) + ".");
            }

            HashSet<string> seen = new(ModFolderPath.Comparer);
            for (int index = 0; index < folders.Count; index++)
            {
                string entry = folders[index];
                ModFolderPathParse parse = ModFolderPath.Parse(entry);
                string code = !parse.Succeeded
                    ? ModFolderErrors.Code(parse.Error)
                    : parse.Path.IsRoot
                        ? "top level"
                        : !string.Equals(parse.Path.Value, entry, StringComparison.Ordinal)
                            ? "not canonical"
                            : !seen.Add(entry)
                                ? "duplicate"
                                : index > 0 && ModFolderTree.ComparePaths(folders[index - 1], entry) >= 0
                                    ? "out of order"
                                    : null;
                if (code != null)
                {
                    throw new RbxWorldPackageException(
                        "manifest.json mod_folders[" + index.ToString(CultureInfo.InvariantCulture) + "] '" + entry
                        + "' is not a canonical folder path (" + code + ").");
                }
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

            if (entries.ContainsKey(TerrainEntryName))
            {
                // WHY refused rather than read: a reader that predates the entry accepts such a manifest and
                // would open the world without its cells, so the writer always raises the version with it.
                if (manifest.MinimumReaderVersion < TerrainMinimumReaderVersion)
                {
                    throw new RbxWorldPackageException(
                        "World package carries '" + TerrainEntryName + "' but declares minimum_reader_version "
                        + manifest.MinimumReaderVersion + "; a package with Terrain cells declares "
                        + TerrainMinimumReaderVersion + ".");
                }

                expected.Add(TerrainEntryName);
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
            string entryName,
            RbxWorldPackageLimits limits)
        {
            if (!entries.TryGetValue(entryName, out ZipArchiveEntry entry))
            {
                throw new RbxWorldPackageException(
                    "World package is missing required entry '" + entryName + "'.");
            }

            if (entry.Length > limits.MaximumEntryBytes)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + entryName + "' is " + entry.Length
                    + " bytes; limit is " + limits.MaximumEntryBytes + ".");
            }

            using Stream stream = entry.Open();
            using MemoryStream output = new((int)entry.Length);
            byte[] buffer = new byte[81920];
            long total = 0L;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limits.MaximumEntryBytes)
                {
                    throw new RbxWorldPackageException(
                        "World package entry '" + entryName
                        + "' exceeds the decompressed size limit " + limits.MaximumEntryBytes + ".");
                }

                output.Write(buffer, 0, read);
            }

            // WHY the actual bytes, not the declared length: IndexEntries sums the sizes the ZIP declares,
            // and an entry whose header understates its size would otherwise let every entry inflate to the
            // per-entry limit, so the expanded total would bound nothing.
            if (total > entry.Length)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + entryName + "' inflates to more than the "
                    + entry.Length + " bytes its header declares.");
            }

            return output.ToArray();
        }

        private static void WriteEntry(
            ZipArchive archive,
            string name,
            byte[] bytes,
            int count,
            RbxWorldPackageLimits limits)
        {
            ValidateWritableEntry(name, count, limits);

            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = StableZipTimestamp;
            using Stream stream = entry.Open();
            stream.Write(bytes, 0, count);
        }

        private static void ValidateWritableEntry(string name, long byteCount, RbxWorldPackageLimits limits)
        {
            if (byteCount > limits.MaximumEntryBytes)
            {
                throw new RbxWorldPackageException(
                    "World package entry '" + name + "' is " + byteCount
                    + " bytes; limit is " + limits.MaximumEntryBytes + ".");
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
                // WHY a reader over the bytes instead of one string: a 64 MiB world.json decoded to a string
                // first held three copies of the entry (bytes, UTF-16 text, the DTO graph) at once. The strict
                // decoder still refuses invalid UTF-8, no byte-order mark is honoured, and trailing content
                // is still an error, as in JsonConvert.DeserializeObject.
                using MemoryStream input = new(bytes, false);
                using StreamReader text = new(input, StrictUtf8, false, 64 * 1024);
                using JsonTextReader json = new(text);
                JsonSerializer serializer = JsonSerializer.CreateDefault(CreateJsonSettings());
                serializer.CheckAdditionalContent = true;
                return serializer.Deserialize<T>(json);
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
                FloatParseHandling = FloatParseHandling.Double,
                Converters = { GuiBlocks }
            };
        }

        /// <summary>Constructed here rather than named in an attribute, so no reflection has to create it in a stripped player.</summary>
        private static readonly GuiBlockConverter GuiBlocks = new();

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

        internal static float[] ParseFloatComponents(string serialized, int expected)
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
                SuspendedAfterBudgetTrips = source.SuspendedAfterBudgetTrips,
                Context = source.Context ?? ""
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

            // WHY no format_version bump: the key is omitted when the world has no persisted folder, so every
            // package the previous writer produced is byte-identical; an older reader refuses a package that has
            // it (MissingMemberHandling.Error) instead of dropping its empty folders.
            [JsonProperty("mod_folders", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public List<string> ModFolders;
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

            // WHY omitted when null and no format_version bump: a world that does not drive its lighting
            // writes no key and stays byte-identical; a reader up to 7.56.0 refuses the key by name
            // ("Could not find member 'lighting'"), as it refuses every member it does not know.
            [JsonProperty("lighting", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public WorldLightingDto Lighting;

            // WHY omitted when null: a world that kept the StarterPlayer defaults writes no key and stays
            // byte-identical; a reader up to 7.56.0 refuses the key by name.
            [JsonProperty("starter_player", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public WorldStarterPlayerDto StarterPlayer;

            // WHY omitted when null: a world that kept the StarterGui switches at their defaults writes no key and
            // stays byte-identical; a reader up to 7.56.0 refuses the key by name.
            [JsonProperty("starter_gui", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public WorldStarterGuiDto StarterGui;
        }

        [Serializable]
        private sealed class WorldStarterGuiDto
        {
            [JsonProperty("show_development_gui", Required = Required.Always)]
            public bool ShowDevelopmentGui;

            [JsonProperty("screen_orientation", Required = Required.Always)]
            public int ScreenOrientation;

            [JsonProperty("core_gui_disabled", Required = Required.Always)]
            public List<int> CoreGuiDisabled;
        }

        [Serializable]
        private sealed class WorldStarterPlayerDto
        {
            [JsonProperty("dev_computer_movement_mode", Required = Required.Always)]
            public string DevComputerMovementMode;

            [JsonProperty("camera_mode", Required = Required.Always)]
            public string CameraMode;

            [JsonProperty("camera_max_zoom_distance", Required = Required.Always)]
            public double CameraMaxZoomDistance;

            [JsonProperty("camera_min_zoom_distance", Required = Required.Always)]
            public double CameraMinZoomDistance;
        }

        [Serializable]
        private sealed class WorldLightingDto
        {
            [JsonProperty("clock_time", Required = Required.Always)]
            public float ClockTime;

            [JsonProperty("geographic_latitude", Required = Required.Always)]
            public float GeographicLatitude;

            [JsonProperty("brightness", Required = Required.Always)]
            public float Brightness;

            [JsonProperty("ambient", Required = Required.Always)]
            public float[] Ambient;

            [JsonProperty("outdoor_ambient", Required = Required.Always)]
            public float[] OutdoorAmbient;
        }

        [Serializable]
        private sealed class WorldLightDto
        {
            [JsonProperty("brightness", Required = Required.Always)]
            public float Brightness;

            [JsonProperty("color", Required = Required.Always)]
            public float[] Color;

            [JsonProperty("enabled", Required = Required.Always)]
            public bool Enabled;

            [JsonProperty("shadows", Required = Required.Always)]
            public bool Shadows;

            [JsonProperty("range", Required = Required.Always)]
            public float Range;
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

            // WHY omitted when null: an instance that is no PointLight writes no key, so every package
            // without one stays byte-identical; readers up to 7.56.0 refuse the key by name.
            [JsonProperty("light", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public WorldLightDto Light;

            // WHY omitted when null: an instance that is no GUI node writes no key, so every package without GUI
            // stays byte-identical; readers up to 7.56.0 refuse the key by name.
            [JsonProperty("gui", Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)]
            public WorldGuiDto Gui;
        }

        /// <summary>
        /// A <c>gui</c> block as its key/value pairs in document order; <see cref="GuiBlockConverter"/> reads and
        /// writes it, typed by <see cref="RbxWorldPackageGuiSchema"/>.
        /// </summary>
        private sealed class WorldGuiDto
        {
            public List<KeyValuePair<string, RbxGuiValue>> Entries = new();

            public static WorldGuiDto Of(RbxWorldPackageGui gui)
            {
                WorldGuiDto dto = new();
                for (int index = 0; index < gui.Count; index++)
                {
                    dto.Entries.Add(new KeyValuePair<string, RbxGuiValue>(
                        RbxWorldPackageGuiSchema.KeyAt(gui.ClassName, index), gui.ValueAt(index)));
                }

                return dto;
            }
        }

        /// <summary>
        /// Reads and writes a <c>gui</c> block. A key no GUI class has is refused by name, as
        /// <c>MissingMemberHandling.Error</c> refuses one in every other block; a value of the wrong JSON type, an
        /// offset that is not an integer, NaN, and an infinity outside <c>min_size</c>/<c>max_size</c> are refused.
        /// </summary>
        private sealed class GuiBlockConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(WorldGuiDto);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                WorldGuiDto dto = (WorldGuiDto)value;
                writer.WriteStartObject();
                foreach (KeyValuePair<string, RbxGuiValue> entry in dto.Entries)
                {
                    writer.WritePropertyName(entry.Key);
                    RbxGuiValue item = entry.Value;
                    switch (item.Kind)
                    {
                        case RbxGuiValueKind.Bool:
                            writer.WriteValue(item.Flag);
                            break;
                        case RbxGuiValueKind.Int:
                            writer.WriteValue(item.First);
                            break;
                        case RbxGuiValueKind.Float:
                            writer.WriteValue(item.X);
                            break;
                        case RbxGuiValueKind.Color3:
                            writer.WriteStartArray();
                            writer.WriteValue(item.X);
                            writer.WriteValue(item.Y);
                            writer.WriteValue(item.Z);
                            writer.WriteEndArray();
                            break;
                        case RbxGuiValueKind.UDim:
                            writer.WriteStartArray();
                            writer.WriteValue(item.X);
                            writer.WriteValue(item.First);
                            writer.WriteEndArray();
                            break;
                        case RbxGuiValueKind.UDim2:
                            writer.WriteStartArray();
                            writer.WriteValue(item.X);
                            writer.WriteValue(item.First);
                            writer.WriteValue(item.Y);
                            writer.WriteValue(item.Second);
                            writer.WriteEndArray();
                            break;
                        case RbxGuiValueKind.Vector2:
                            writer.WriteStartArray();
                            writer.WriteValue(item.X);
                            writer.WriteValue(item.Y);
                            writer.WriteEndArray();
                            break;
                        default:
                            writer.WriteValue(item.Text);
                            break;
                    }
                }

                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                if (reader.TokenType != JsonToken.StartObject)
                {
                    throw new JsonSerializationException("A gui block must be a JSON object.");
                }

                WorldGuiDto dto = new();
                while (Next(reader) != JsonToken.EndObject)
                {
                    if (reader.TokenType != JsonToken.PropertyName)
                    {
                        throw new JsonSerializationException("A gui block holds an unexpected " + reader.TokenType + ".");
                    }

                    string key = (string)reader.Value;
                    if (!RbxWorldPackageGuiSchema.TryGetKind(key, out RbxGuiValueKind kind))
                    {
                        throw new JsonSerializationException("Could not find member '" + key + "' on a gui block.");
                    }

                    Next(reader);
                    dto.Entries.Add(new KeyValuePair<string, RbxGuiValue>(key, ReadValue(reader, key, kind)));
                }

                return dto;
            }

            private static RbxGuiValue ReadValue(JsonReader reader, string key, RbxGuiValueKind kind)
            {
                switch (kind)
                {
                    case RbxGuiValueKind.Bool:
                        if (reader.TokenType != JsonToken.Boolean)
                        {
                            throw Wrong(key, "a boolean");
                        }

                        return RbxGuiValue.OfBool((bool)reader.Value);
                    case RbxGuiValueKind.Int:
                        return RbxGuiValue.OfInt(ReadInt(reader, key));
                    case RbxGuiValueKind.Float:
                        return RbxGuiValue.OfFloat(ReadFloat(reader, key, false));
                    case RbxGuiValueKind.Text:
                        if (reader.TokenType != JsonToken.String)
                        {
                            throw Wrong(key, "a string");
                        }

                        return RbxGuiValue.OfText((string)reader.Value);
                    default:
                        return ReadArray(reader, key, kind);
                }
            }

            private static RbxGuiValue ReadArray(JsonReader reader, string key, RbxGuiValueKind kind)
            {
                if (reader.TokenType != JsonToken.StartArray)
                {
                    throw Wrong(key, "an array");
                }

                bool infinity = RbxWorldPackageGuiSchema.AllowsInfinity(key);
                RbxGuiValue value;
                Next(reader);
                switch (kind)
                {
                    case RbxGuiValueKind.Color3:
                    {
                        float r = ReadFloat(reader, key, false);
                        Next(reader);
                        float g = ReadFloat(reader, key, false);
                        Next(reader);
                        value = RbxGuiValue.OfColor(new RbxColor3(r, g, ReadFloat(reader, key, false)));
                        break;
                    }
                    case RbxGuiValueKind.UDim:
                    {
                        float scale = ReadFloat(reader, key, false);
                        Next(reader);
                        value = RbxGuiValue.OfUDim(new RbxUDim(scale, ReadInt(reader, key)));
                        break;
                    }
                    case RbxGuiValueKind.UDim2:
                    {
                        float xScale = ReadFloat(reader, key, false);
                        Next(reader);
                        int xOffset = ReadInt(reader, key);
                        Next(reader);
                        float yScale = ReadFloat(reader, key, false);
                        Next(reader);
                        value = RbxGuiValue.OfUDim2(new RbxUDim2(xScale, xOffset, yScale, ReadInt(reader, key)));
                        break;
                    }
                    default:
                    {
                        float x = ReadFloat(reader, key, infinity);
                        Next(reader);
                        value = RbxGuiValue.OfVector2(new RbxVector2(x, ReadFloat(reader, key, infinity)));
                        break;
                    }
                }

                if (Next(reader) != JsonToken.EndArray)
                {
                    throw Wrong(key, "an array of the property's length");
                }

                return value;
            }

            private static int ReadInt(JsonReader reader, string key)
            {
                if (reader.TokenType != JsonToken.Integer)
                {
                    throw Wrong(key, "an integer");
                }

                long value = Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw Wrong(key, "a 32-bit integer");
                }

                return (int)value;
            }

            private static float ReadFloat(JsonReader reader, string key, bool allowInfinity)
            {
                double value;
                switch (reader.TokenType)
                {
                    case JsonToken.Integer:
                    case JsonToken.Float:
                        value = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
                        break;
                    case JsonToken.String when allowInfinity && (string)reader.Value == "Infinity":
                        value = double.PositiveInfinity;
                        break;
                    case JsonToken.String when allowInfinity && (string)reader.Value == "-Infinity":
                        value = double.NegativeInfinity;
                        break;
                    default:
                        throw Wrong(key, "a number");
                }

                float narrowed = (float)value;
                if (double.IsNaN(value) || (!allowInfinity && float.IsInfinity(narrowed)))
                {
                    throw Wrong(key, "a finite number");
                }

                return narrowed;
            }

            private static JsonToken Next(JsonReader reader)
            {
                if (!reader.Read())
                {
                    throw new JsonSerializationException("A gui block ends before it is complete.");
                }

                return reader.TokenType;
            }

            private static JsonSerializationException Wrong(string key, string expected)
            {
                return new JsonSerializationException("gui key '" + key + "' needs " + expected + ".");
            }
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
