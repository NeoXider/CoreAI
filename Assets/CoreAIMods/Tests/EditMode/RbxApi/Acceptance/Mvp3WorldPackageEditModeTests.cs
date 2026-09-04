using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>MVP3 world-package coverage for projection, codec, restore, rejection, and storage.</summary>
    [TestFixture]
    public sealed class Mvp3WorldPackageEditModeTests
    {
        private const string WorldId = "mvp3-world";

        private static readonly DateTime CapturedAtUtc =
            new(2026, 9, 1, 6, 7, 8, DateTimeKind.Utc);

        private readonly List<RbxDataModel> _games = new();
        private readonly List<string> _temporaryDirectories = new();
        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (RbxDataModel game in _games)
            {
                if (game != null && !game.IsDestroyed)
                {
                    game.Destroy();
                }
            }

            foreach (string directory in _temporaryDirectories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void WorldOwnedPayload_WithPackagedLuaSources_CodecRestoreRecapture_RoundTrips()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload captured = Capture(source, CapturedAtUtc);
            byte[] firstBytes = RbxWorldPackageSerializer.WritePackage(captured);
            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(firstBytes);

            float appliedScale = 1f;
            InMemoryCameraRig restoredCamera = new();
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                decoded,
                new RbxWorldPackageRestoreOptions
                {
                    CameraRig = restoredCamera,
                    BeginMetersPerStudRestore = metersPerStud =>
                    {
                        float previousScale = appliedScale;
                        appliedScale = metersPerStud;
                        return () => appliedScale = previousScale;
                    }
                });
            _games.Add(restored.Game);

            Assert.AreEqual(0.35f, appliedScale);
            AssertRestoredState(source, decoded, restored, restoredCamera);

            MemorySourceStore restoredSources = new();
            restoredSources.ReplaceWith(restored.Mods);
            RbxWorldPackagePayload recaptured = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    restored.Registry,
                    restored.Game,
                    restored.PartSink,
                    CloneSettings(decoded.Settings),
                    restoredCamera,
                    restoredSources,
                    CapturedAtUtc));
            byte[] secondBytes = RbxWorldPackageSerializer.WritePackage(recaptured);

            CollectionAssert.AreEqual(firstBytes, secondBytes,
                "capture -> package -> restore -> capture must be byte-identical.");
            CollectionAssert.AreEqual(
                firstBytes,
                RbxWorldPackageSerializer.WritePackage(
                    RbxWorldPackageSerializer.ReadPackage(firstBytes)),
                "decode -> encode must preserve the canonical package bytes.");
        }

        [Test]
        public void ReadPackage_UnsupportedFormatVersion_IsRejectedBeforeRestore()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            byte[] package = RbxWorldPackageSerializer.WritePackage(
                Capture(source, CapturedAtUtc));
            byte[] unsupported = ReplaceEntryText(
                package,
                RbxWorldPackageSerializer.ManifestEntryName,
                "\"format_version\": 1",
                "\"format_version\": 999");

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(unsupported));

            StringAssert.Contains("Unsupported world package format version 999", exception.Message);
        }

        [Test]
        public void ReadPackage_ManifestModCountBeyondSemanticLimit_IsRejectedEarly()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            byte[] package = RbxWorldPackageSerializer.WritePackage(Capture(source, CapturedAtUtc));
            StringBuilder injectedMods = new("\"mods\": [");
            for (int index = 0; index <= RbxWorldPackageSerializer.MaximumMods; index++)
            {
                injectedMods.Append("{\"id\":\"overflow-")
                    .Append(index.ToString("D3"))
                    .Append("\",\"manifest_entry\":\"Mods/0000/manifest.json\",")
                    .Append("\"source_entry\":\"Mods/0000/main.lua\"},");
            }

            byte[] hostile = ReplaceEntryText(
                package,
                RbxWorldPackageSerializer.ManifestEntryName,
                "\"mods\": [",
                injectedMods.ToString());

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(hostile));

            StringAssert.Contains("mods; limit is", exception.Message);
        }

        [Test]
        public void Capture_BasePartWithoutReadableState_IsRejectedInsteadOfDefaulted()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            RbxInstance part = registry.Create("Part");
            part.Parent = registry.WorldRoot;
            InMemoryPartPropertySink emptySink = new();

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    emptySink,
                    NewSettings())));

            StringAssert.Contains("durable Part state is missing", exception.Message);
        }

        [Test]
        public void Capture_ModOwnedSubtree_IsExcludedWithUnownedDescendantsAndNoDanglingRefs()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxModel durableModel = (RbxModel)registry.Create("Model");
            durableModel.Name = "DurableModel";
            durableModel.Parent = registry.WorldRoot;
            RbxInstance durablePart = registry.Create("Part");
            durablePart.Name = "DurablePrimaryPart";
            durablePart.Parent = durableModel;
            durableModel.SetPrimaryPart(durablePart);
            PartProperties durableProperties = PartProperties.CreateDefault();
            partSink.SetPartProperties(durablePart.Id, in durableProperties);
            RbxInstance ephemeralRoot = registry.Create(
                "Folder",
                "active-builder",
                OriginTag.FromMod("active-builder"));
            ephemeralRoot.Name = "EphemeralRoot";
            ephemeralRoot.Parent = registry.WorldRoot;
            RbxInstance inheritedEphemeralChild = registry.Create("Folder");
            inheritedEphemeralChild.Name = "InheritedEphemeralChild";
            inheritedEphemeralChild.Parent = ephemeralRoot;
            RbxInstance inheritedEphemeralPart = registry.Create("Part");
            inheritedEphemeralPart.Name = "InheritedEphemeralPart";
            inheritedEphemeralPart.Parent = inheritedEphemeralChild;
            PartProperties ephemeralProperties = PartProperties.CreateDefault();
            partSink.SetPartProperties(inheritedEphemeralPart.Id, in ephemeralProperties);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.ExportSnapshot(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    partSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));

            HashSet<ulong> retainedIds = new();
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                retainedIds.Add(node.Id);
            }

            Assert.IsFalse(retainedIds.Contains(ephemeralRoot.Id.Value));
            Assert.IsFalse(retainedIds.Contains(inheritedEphemeralChild.Id.Value));
            Assert.IsFalse(retainedIds.Contains(inheritedEphemeralPart.Id.Value));
            Assert.IsFalse(payload.Parts.ContainsKey(inheritedEphemeralPart.Id));
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                Assert.IsTrue(node.ParentId == 0UL || retainedIds.Contains(node.ParentId));
                Assert.IsTrue(
                    node.Model == null
                    || node.Model.PrimaryPartId == 0UL
                    || retainedIds.Contains(node.Model.PrimaryPartId));
            }

            Assert.AreEqual(durablePart.Id.Value, FindNode(payload, "DurableModel").Model.PrimaryPartId);
        }

        [Test]
        public void Capture_RuntimePlayerIdentitySubtree_IsExcludedWhilePlayersServiceRemains()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            RbxPlayers players = (RbxPlayers)game.FindFirstChildOfClass("Players");
            RbxPlayer player = players.EnsureActor(registry, "capture-client");
            RbxInstance runtimeChild = registry.Create("Folder");
            runtimeChild.Name = "RuntimeIdentityChild";
            runtimeChild.Parent = player;

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));

            Assert.IsNotNull(FindNode(payload, "Players"));
            Assert.IsNull(FindNodeOrNull(payload, player.Name));
            Assert.IsNull(FindNodeOrNull(payload, runtimeChild.Name));
        }

        [Test]
        public void Capture_DurableModelReferencingModEphemeralPrimaryPart_DropsReferenceAndEmitsDiagnostic()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxModel durableModel = (RbxModel)registry.Create("Model");
            durableModel.Name = "DurableModel";
            durableModel.Parent = registry.WorldRoot;
            RbxInstance ephemeralPart = registry.Create(
                "Part",
                "active-builder",
                OriginTag.FromMod("active-builder"));
            ephemeralPart.Name = "EphemeralPrimaryPart";
            ephemeralPart.Parent = durableModel;
            durableModel.SetPrimaryPart(ephemeralPart);
            PartProperties ephemeralProperties = PartProperties.CreateDefault();
            partSink.SetPartProperties(ephemeralPart.Id, in ephemeralProperties);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    partSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));

            InstanceSnapshot capturedModel = null;
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.Id == durableModel.Id.Value)
                {
                    capturedModel = node;
                    break;
                }
            }

            Assert.IsNotNull(capturedModel);
            Assert.IsNotNull(capturedModel.Model);
            Assert.AreEqual(0UL, capturedModel.Model.PrimaryPartId);
            Assert.AreEqual(1, payload.Diagnostics.Count);
            Assert.AreEqual(durableModel.Id.Value, payload.Diagnostics[0].ModelId);
            Assert.AreEqual(ephemeralPart.Id.Value, payload.Diagnostics[0].DroppedPrimaryPartId);
            StringAssert.Contains("mod-ephemeral", payload.Diagnostics[0].Reason);
            Assert.IsNotNull(durableModel.PrimaryPart);
            Assert.AreEqual(ephemeralPart.Id, durableModel.PrimaryPart.Id);
        }

        [Test]
        public void ReadPackage_InjectedModOwnedNode_IsRejectedBeforeRestore()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);
            byte[] hostile = ReplaceEntryText(
                package,
                RbxWorldPackageSerializer.WorldEntryName,
                "\"owner_mod_id\": null",
                "\"owner_mod_id\": \"injected-mod\"");

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(hostile));

            StringAssert.Contains("mod-ephemeral instance", exception.Message);
        }

        [Test]
        public void RestoreFresh_ThrowingPartSink_RollsBackHostScaleTransaction()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            ThrowingPartPropertySink throwingSink = new();
            float hostScale = 0.28f;

            Assert.Throws<InvalidOperationException>(() =>
                RbxWorldPackageSerializer.RestoreFresh(
                    payload,
                    new RbxWorldPackageRestoreOptions
                    {
                        PartSink = throwingSink,
                        CameraRig = new InMemoryCameraRig(),
                        BeginMetersPerStudRestore = metersPerStud =>
                        {
                            float previousScale = hostScale;
                            hostScale = metersPerStud;
                            return () => hostScale = previousScale;
                        }
                    }));

            Assert.AreEqual(1, throwingSink.FullStateCalls);
            Assert.AreEqual(0.28f, hostScale,
                "A failed restore must restore the host's previous meters-per-stud value.");
        }

        [Test]
        public void RestoreFresh_CameraStateWithoutRig_IsRejectedBeforeScaleMutation()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            int scaleMutations = 0;

            Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.RestoreFresh(
                    payload,
                    new RbxWorldPackageRestoreOptions
                    {
                        BeginMetersPerStudRestore = metersPerStud =>
                        {
                            scaleMutations++;
                            return () => scaleMutations--;
                        }
                    }));

            Assert.AreEqual(0, scaleMutations);
        }

        [Test]
        public void RestoreFresh_InvalidOriginTag_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            FindNode(payload, "RuntimeModel").OriginTag = "invalid-origin";

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_NullStringAttribute_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            FindAttribute(FindNode(payload, "RuntimeModel"), "Label").StringValue = null;

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_FractionalUDimOffset_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            FindAttribute(FindNode(payload, "RuntimeModel"), "Padding").StringValue = "0.5,12.5";

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_OverflowingUDimOffset_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            FindAttribute(FindNode(payload, "RuntimeModel"), "Padding").StringValue =
                "0.5,2147483648";

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_MismatchedMaterialNameAndValue_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            InstanceSnapshot partNode = FindNode(payload, "PrimaryPart");
            Dictionary<InstanceId, PartProperties> parts =
                payload.Parts as Dictionary<InstanceId, PartProperties>;
            Assert.IsNotNull(parts);
            InstanceId partId = new(partNode.Id);
            PartProperties properties = parts[partId];
            properties.Material = new RbxMaterialId("Wood", RbxMaterialId.PlasticValue);
            parts[partId] = properties;

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void WorldPackage_MaterialVariant_RoundTripsVariantStateAndPartReference()
        {
            VariantWorld world = BuildVariantWorld("MossyRock");
            RbxWorldPackagePayload captured = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    world.Registry,
                    world.Game,
                    world.PartSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));
            byte[] firstBytes = RbxWorldPackageSerializer.WritePackage(captured);
            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(firstBytes);

            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            Assert.IsTrue(materialEnum.TryGetItem("Rock", out RbxEnumItem rock));
            InstanceSnapshot variantNode = FindNode(decoded, "MossyRock");
            Assert.IsNotNull(variantNode.MaterialVariant);
            Assert.AreEqual("Rock", variantNode.MaterialVariant.BaseMaterial);
            Assert.AreEqual(rock.Value, variantNode.MaterialVariant.BaseMaterialValue);
            Assert.AreEqual("rbxasset://mossy_albedo", variantNode.MaterialVariant.ColorMap);
            Assert.AreEqual("rbxasset://mossy_normal", variantNode.MaterialVariant.NormalMap);
            Assert.AreEqual("rbxasset://mossy_rough", variantNode.MaterialVariant.RoughnessMap);
            Assert.AreEqual("rbxasset://mossy_metal", variantNode.MaterialVariant.MetalnessMap);
            Assert.AreEqual("2.5", variantNode.MaterialVariant.StudsPerTile);
            Assert.AreEqual(
                "MossyRock",
                decoded.Parts[world.Part.Id].MaterialVariant);

            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                decoded,
                new RbxWorldPackageRestoreOptions { CameraRig = new InMemoryCameraRig() });
            _games.Add(restored.Game);

            RbxMaterialVariant restoredVariant =
                (RbxMaterialVariant)restored.Game.FindFirstChildOfClass("MaterialService")
                    .FindFirstChild("MossyRock");
            Assert.IsNotNull(restoredVariant);
            Assert.AreEqual("Rock", restoredVariant.BaseMaterial.Name);
            Assert.AreEqual(rock.Value, restoredVariant.BaseMaterial.Value);
            Assert.AreEqual("rbxasset://mossy_albedo", restoredVariant.ColorMap);
            Assert.AreEqual("rbxasset://mossy_normal", restoredVariant.NormalMap);
            Assert.AreEqual("rbxasset://mossy_rough", restoredVariant.RoughnessMap);
            Assert.AreEqual("rbxasset://mossy_metal", restoredVariant.MetalnessMap);
            Assert.AreEqual(2.5f, restoredVariant.StudsPerTile);
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(
                world.Part.Id, out PartProperties restoredProperties));
            Assert.AreEqual("MossyRock", restoredProperties.MaterialVariant);

            CollectionAssert.AreEqual(
                firstBytes,
                RbxWorldPackageSerializer.WritePackage(
                    RbxWorldPackageSerializer.ReadPackage(firstBytes)),
                "decode -> encode must preserve the canonical package bytes.");
        }

        [Test]
        public void ReadPackage_WorldJsonWithoutMaterialVariantKeys_DeserializesWithNullVariant()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            byte[] package = RbxWorldPackageSerializer.WritePackage(Capture(source, CapturedAtUtc));
            byte[] legacy = StripMaterialVariantKeys(package);
            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(legacy);

            foreach (KeyValuePair<InstanceId, PartProperties> entry in decoded.Parts)
            {
                Assert.IsNull(entry.Value.MaterialVariant);
            }

            foreach (InstanceSnapshot node in decoded.Tree.Instances)
            {
                Assert.IsNull(node.MaterialVariant);
            }
        }

        [Test]
        public void WritePackage_PartWithUndefinedMaterialVariant_IsRejectedWithNamedError()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            InstanceSnapshot partNode = FindNode(payload, "PrimaryPart");
            Dictionary<InstanceId, PartProperties> parts =
                payload.Parts as Dictionary<InstanceId, PartProperties>;
            Assert.IsNotNull(parts);
            InstanceId partId = new(partNode.Id);
            PartProperties properties = parts[partId];
            properties.MaterialVariant = "GhostVariant";
            parts[partId] = properties;

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.WritePackage(payload));

            StringAssert.Contains(partId.Value.ToString(), exception.Message);
            StringAssert.Contains("GhostVariant", exception.Message);
        }

        [Test]
        public void WritePackage_MaterialVariantWithBogusBaseMaterial_IsRejectedWithNamedError()
        {
            VariantWorld world = BuildVariantWorld("BogusBase");
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    world.Registry,
                    world.Game,
                    world.PartSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));
            InstanceSnapshot variantNode = FindNode(payload, "BogusBase");
            variantNode.MaterialVariant.BaseMaterial = "NotAMaterial";
            variantNode.MaterialVariant.BaseMaterialValue = 12345;

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.WritePackage(payload));

            StringAssert.Contains("BaseMaterial", exception.Message);
            StringAssert.Contains("NotAMaterial", exception.Message);
        }

        [Test]
        public void WritePackage_MaterialVariantWithNonPositiveStudsPerTile_IsRejected()
        {
            VariantWorld world = BuildVariantWorld("FlatVariant");
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    world.Registry,
                    world.Game,
                    world.PartSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));
            InstanceSnapshot variantNode = FindNode(payload, "FlatVariant");
            variantNode.MaterialVariant.StudsPerTile = "0";

            RbxError exception = Assert.Throws<RbxError>(() =>
                RbxWorldPackageSerializer.WritePackage(payload));

            StringAssert.Contains("StudsPerTile", exception.Message);
        }

        private VariantWorld BuildVariantWorld(string variantName)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxInstance materialService = game.FindFirstChildOfClass("MaterialService");
            Assert.IsNotNull(materialService);
            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            Assert.IsTrue(materialEnum.TryGetItem("Rock", out RbxEnumItem rock));
            RbxMaterialVariant variant = (RbxMaterialVariant)registry.Create("MaterialVariant");
            variant.Name = variantName;
            variant.BaseMaterial = new RbxMaterialId(rock.Name, rock.Value);
            variant.ColorMap = "rbxasset://mossy_albedo";
            variant.NormalMap = "rbxasset://mossy_normal";
            variant.RoughnessMap = "rbxasset://mossy_rough";
            variant.MetalnessMap = "rbxasset://mossy_metal";
            variant.StudsPerTile = 2.5f;
            variant.Parent = materialService;
            RbxInstance part = registry.Create("Part");
            part.Name = "VariantPart";
            part.Parent = registry.WorldRoot;
            PartProperties properties = PartProperties.CreateDefault();
            properties.MaterialVariant = variantName;
            partSink.SetPartProperties(part.Id, in properties);
            return new VariantWorld
            {
                Registry = registry,
                Game = game,
                PartSink = partSink,
                Part = part,
                Variant = variant
            };
        }

        private static byte[] StripMaterialVariantKeys(byte[] package)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive source = new(input, ZipArchiveMode.Read, false);
            using MemoryStream output = new();
            using (ZipArchive destination = new(output, ZipArchiveMode.Create, true))
            {
                foreach (ZipArchiveEntry sourceEntry in source.Entries)
                {
                    byte[] bytes;
                    using (Stream entryStream = sourceEntry.Open())
                    using (MemoryStream entryBytes = new())
                    {
                        entryStream.CopyTo(entryBytes);
                        bytes = entryBytes.ToArray();
                    }

                    if (string.Equals(
                            sourceEntry.FullName,
                            RbxWorldPackageSerializer.WorldEntryName,
                            StringComparison.Ordinal))
                    {
                        string text = new UTF8Encoding(false, true).GetString(bytes);
                        Assert.IsTrue(
                            text.Contains("\"material_variant\""),
                            "The fixture package must carry material_variant keys before stripping.");
                        JObject world = JObject.Parse(text);
                        List<JToken> doomed = new();
                        foreach (JToken token in world.SelectTokens("$..material_variant"))
                        {
                            doomed.Add(token);
                        }

                        foreach (JToken token in doomed)
                        {
                            ((JProperty)token.Parent).Remove();
                        }

                        string stripped = world.ToString(Newtonsoft.Json.Formatting.None);
                        Assert.IsFalse(stripped.Contains("material_variant"));
                        bytes = new UTF8Encoding(false, true).GetBytes(stripped);
                    }

                    ZipArchiveEntry destinationEntry = destination.CreateEntry(
                        sourceEntry.FullName, CompressionLevel.Optimal);
                    destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
                    using Stream destinationStream = destinationEntry.Open();
                    destinationStream.Write(bytes, 0, bytes.Length);
                }
            }

            return output.ToArray();
        }

        [Test]
        public void RestoreFresh_NonFiniteDatatypeComponent_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            FindAttribute(FindNode(payload, "RuntimeModel"), "Spawn").StringValue = "NaN,-2,3.25";

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_MissingWorkspace_IsRejectedInsteadOfSynthesized()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            InstanceSnapshot workspace = null;
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (string.Equals(node.ClassName, "Workspace", StringComparison.Ordinal))
                {
                    workspace = node;
                    break;
                }
            }

            Assert.IsNotNull(workspace);
            workspace.ClassName = "Folder";

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_DisabledStoredWorldPivotWithValue_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            InstanceSnapshot model = FindNode(payload, "RuntimeModel");
            model.Model.HasStoredWorldPivot = false;

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void InstanceTreeRestore_DestinationIdCollision_IsRejectedBeforeBinderMutation()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(source.Game);
            CountingBinder binder = new();
            InstanceRegistry destination = new(binder: binder);
            destination.Create("Folder");
            int callsBeforeRestore = binder.RegisterCalls;

            Assert.Throws<RbxError>(() =>
                InstanceTreeSerializer.Restore(snapshot, destination));

            Assert.AreEqual(callsBeforeRestore, binder.RegisterCalls);
        }

        [Test]
        public void InstanceTreeRestore_ExcessiveHierarchyDepth_IsRejectedBeforeBinderMutation()
        {
            InstanceTreeSnapshot snapshot = new();
            for (int index = 0; index <= InstanceTreeSerializer.MaximumSnapshotDepth; index++)
            {
                snapshot.Instances.Add(new InstanceSnapshot
                {
                    Id = (ulong)index + 1UL,
                    ParentId = index == 0 ? 0UL : (ulong)index,
                    ClassName = "Folder",
                    Name = "Node" + index,
                    Archivable = true
                });
            }

            CountingBinder binder = new();
            InstanceRegistry destination = new(binder: binder);

            Assert.Throws<RbxError>(() =>
                InstanceTreeSerializer.Restore(snapshot, destination));
            Assert.AreEqual(0, binder.RegisterCalls);
        }

        [Test]
        public void InstanceTreeCapture_ExcessiveHierarchyDepth_IsRejectedBeforeUnboundedRecursion()
        {
            InstanceRegistry registry = new();
            RbxInstance root = registry.Create("Folder");
            RbxInstance parent = root;
            for (int depth = 1; depth <= InstanceTreeSerializer.MaximumSnapshotDepth; depth++)
            {
                RbxInstance child = registry.Create("Folder");
                child.Parent = parent;
                parent = child;
            }

            Assert.Throws<RbxError>(() => InstanceTreeSerializer.Capture(root));
        }

        [Test]
        public void InstanceTreeCapture_LocallyAssignedId_IsRejectedFromWorldSnapshot()
        {
            InstanceRegistry registry = new();
            RbxInstance local = registry.Create(
                "Folder", authority: InstanceIdAuthority.Local);

            RbxError exception = Assert.Throws<RbxError>(() =>
                InstanceTreeSerializer.Capture(local));

            Assert.AreEqual(RbxErrorCode.BadArgument, exception.Code);
            StringAssert.Contains("locally-assigned instance id", exception.Message);
        }

        [Test]
        public void InstanceTreeCaptureRestore_UDimOffset_RetainsExactInt32Values()
        {
            int[] offsets = { 16777217, -16777217, int.MinValue, int.MaxValue };
            foreach (int offset in offsets)
            {
                InstanceRegistry source = new();
                RbxInstance root = source.Create("Folder");
                root.SetAttribute("ExactOffset", new RbxUDim(0.25f, offset));
                InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(root);

                InstanceRegistry destination = new();
                RbxInstance restored = InstanceTreeSerializer.Restore(snapshot, destination);

                Assert.AreEqual(
                    new RbxUDim(0.25f, offset),
                    restored.GetAttribute("ExactOffset"));
            }
        }

        [Test]
        public void RestoreFresh_ExcessiveAttributeCount_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            InstanceSnapshot model = FindNode(payload, "RuntimeModel");
            while (model.Attributes.Count <= InstanceTreeSerializer.MaximumAttributesPerInstance)
            {
                int index = model.Attributes.Count;
                model.Attributes.Add(new AttributeSnapshot
                {
                    Name = "Extra" + index,
                    Kind = AttributeValueKind.Number,
                    NumberValue = index
                });
            }

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void RestoreFresh_ExcessiveTagCount_IsRejectedBeforeAdaptersRun()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            InstanceSnapshot model = FindNode(payload, "RuntimeModel");
            while (model.Tags.Count <= InstanceTreeSerializer.MaximumTagsPerInstance)
            {
                model.Tags.Add("ExtraTag" + model.Tags.Count);
            }

            AssertPrevalidationRejects(payload);
        }

        [Test]
        public void WritePackage_ExcessiveModCount_IsRejected()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            List<RbxWorldModSource> mods = payload.Mods as List<RbxWorldModSource>;
            Assert.IsNotNull(mods);
            RbxWorldModSource template = mods[0];
            while (mods.Count <= RbxWorldPackageSerializer.MaximumMods)
            {
                mods.Add(template);
            }

            Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.WritePackage(payload));
        }

        [Test]
        public void WritePackage_HighlyCompressibleExpandedPayloadBeyondReaderLimit_IsRejected()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            List<RbxWorldModSource> mods = payload.Mods as List<RbxWorldModSource>;
            Assert.IsNotNull(mods);
            mods.Clear();
            int sourceLength = (RbxWorldPackageSerializer.MaximumExpandedPackageBytes / 9) + 1;
            string sourceText = new('x', sourceLength);
            for (int index = 0; index < 9; index++)
            {
                LuaModManifest manifest = new()
                {
                    Id = "compressible-" + index.ToString("D2"),
                    Name = "Compressible " + index
                };
                mods.Add(new RbxWorldModSource(manifest, sourceText));
            }

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.WritePackage(payload));

            StringAssert.Contains("expands to", exception.Message);
        }

        [Test]
        public async Task ConfirmedBackup_ExecuteLuaFalse_DoesNotChangeWorldStateOrManualSlots()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            DelegateWorldPackageStore store = new((trigger, captured, cancellationToken) =>
                UniTask.FromResult(new RbxWorldPackageWriteResult(
                    false,
                    "",
                    "Injected durability refusal.")));
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(payload),
                store);
            RecordingLuaCsBindings bindings = new();
            LuaCsGameToolExecutor executor = new(
                new LuaCsSecureEnvironment(),
                bindings,
                new NullLuaExecutionObserver(),
                null,
                gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync(
                "mutate_world()",
                CancellationToken.None);

            Assert.IsFalse(result.Success, result.Output);
            StringAssert.Contains("Confirmed pre-mutation backup", result.Error);
            Assert.AreEqual("old-tree", bindings.TreeState);
            Assert.AreEqual(17, bindings.Revision);
            CollectionAssert.AreEqual(new[] { "old-ledger-entry" }, bindings.Ledger);
            CollectionAssert.AreEqual(
                new[] { LuaCsGameToolExecutor.ExecuteLuaBackupTrigger },
                store.AutoTriggers);
            Assert.AreEqual(0, store.ManualCalls);
        }

        [Test]
        public async Task ConfirmedBackup_ManageModsLoadException_DoesNotLoadOrCreateManualSlot()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            DelegateWorldPackageStore store = new((trigger, captured, cancellationToken) =>
                throw new IOException("Injected backup I/O failure."));
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(payload),
                store);
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LocalActorIdentityProvider identity = new("backup-load-actor");
            TestCoreAiSettings settings = new();
            LuaModsLlmTool tool = CreateWorldGatedModsTool(runtime, identity, settings, gate);

            JObject result = JObject.Parse(await tool.ExecuteAsync(
                "load",
                "blocked-load",
                "local value = 1"));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Injected backup I/O failure", result.Value<string>("message"));
            Assert.IsFalse(runtime.IsLoaded(
                identity.GetActorContext(BuiltInAgentRoleIds.Programmer),
                "blocked-load"));
            CollectionAssert.AreEqual(
                new[] { LuaModsLlmTool.LoadBackupTrigger },
                store.AutoTriggers);
            Assert.AreEqual(0, store.ManualCalls);
        }

        [Test]
        public async Task ConfirmedBackup_ManageModsReloadCancellation_PreservesSourceAndRevisionLedger()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            DelegateWorldPackageStore store = new((trigger, captured, cancellationToken) =>
                throw new OperationCanceledException("Injected backup cancellation."));
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(payload),
                store);
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LocalActorIdentityProvider identity = new("backup-reload-actor");
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            runtime.LoadMod(actor, "stable-mod", "local value = 1", LuaCapabilities.All);
            int originalRevisionCount = runtime.ListModVersions(actor, "stable-mod").Count;
            TestCoreAiSettings settings = new();
            LuaModsLlmTool tool = CreateWorldGatedModsTool(runtime, identity, settings, gate);

            JObject result = JObject.Parse(await tool.ExecuteAsync(
                "reload",
                "stable-mod",
                "local value = 2"));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(runtime.TryGetModSource(actor, "stable-mod", out string source));
            Assert.AreEqual("local value = 1", source);
            Assert.AreEqual(
                originalRevisionCount,
                runtime.ListModVersions(actor, "stable-mod").Count);
            CollectionAssert.AreEqual(
                new[] { LuaModsLlmTool.ReloadBackupTrigger },
                store.AutoTriggers);
            Assert.AreEqual(0, store.ManualCalls);
        }

        [Test]
        public async Task ConfirmedBackup_AllManageModsMutations_FailClosedForEveryBackupFailure()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            string[] actions = { "load", "reload", "unload", "import", "forget", "revert" };
            BackupFailureMode[] failureModes =
            {
                BackupFailureMode.FalseResult,
                BackupFailureMode.Exception,
                BackupFailureMode.Cancellation
            };

            foreach (string action in actions)
            {
                foreach (BackupFailureMode failureMode in failureModes)
                {
                    await AssertManageModsMutationBlockedAsync(action, failureMode, payload);
                }
            }
        }

        [Test]
        public async Task ConfirmedBackup_ReadOnlyManageModsActions_BypassCaptureAndAutosave()
        {
            MemorySourceStore sourceStore = new();
            LuaCsModRuntime runtime = new(
                sourceStore: sourceStore,
                versionStore: new MemoryLuaScriptVersionStore());
            LocalActorIdentityProvider identity = new("backup-read-actor");
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            runtime.LoadMod(actor, "read-target", "local value = 1", LuaCapabilities.All);
            DelegateWorldPackageStore store = new((trigger, captured, cancellationToken) =>
                throw new InvalidOperationException("Read-only action requested an autosave."));
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => throw new InvalidOperationException(
                    "Read-only action requested a capture."),
                store);
            LuaModsLlmTool tool = CreateWorldGatedModsTool(
                runtime,
                identity,
                new TestCoreAiSettings(),
                gate);
            string before = CaptureManageModsState(
                runtime,
                actor,
                sourceStore,
                new[] { "read-target" });
            IReadOnlyList<string> manualBefore = store.ListManualSlots();
            string[] actions = { "list", "get_source", "export", "versions", "diagnostics" };

            foreach (string action in actions)
            {
                JObject result = JObject.Parse(await tool.ExecuteAsync(
                    action,
                    "read-target"));
                Assert.IsTrue(result.Value<bool>("success"), action + ": " + result);
            }

            Assert.AreEqual(
                before,
                CaptureManageModsState(
                    runtime,
                    actor,
                    sourceStore,
                    new[] { "read-target" }));
            Assert.AreEqual(0, store.AutoTriggers.Count);
            Assert.AreEqual(0, store.ManualCalls);
            CollectionAssert.AreEqual(manualBefore, store.ListManualSlots());
        }

        [Test]
        public async Task ConfirmedBackup_SharedGateSerializesCrossToolSnapshotAndMutationOrder()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            List<string> sequence = new();
            UniTaskCompletionSource<bool> firstBackupRelease = new();
            int backupCalls = 0;
            DelegateWorldPackageStore store = new(async (trigger, captured, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                backupCalls++;
                sequence.Add("backup:" + trigger);
                if (backupCalls == 1)
                {
                    await firstBackupRelease.Task;
                }

                return new RbxWorldPackageWriteResult(true, trigger + ".world", "");
            });
            RecordingLuaCsBindings bindings = new(() => sequence.Add("mutation:execute_lua"));
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            LocalActorIdentityProvider identity = new("backup-order-actor");
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            ConfirmedWorldMutationGate gate = new(
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sequence.Add(
                        "capture:" + bindings.Revision + ":" + runtime.IsLoaded(actor, "ordered-mod"));
                    return UniTask.FromResult(payload);
                },
                store);
            LuaCsGameToolExecutor executor = new(
                new LuaCsSecureEnvironment(),
                bindings,
                new NullLuaExecutionObserver(),
                null,
                gate);
            TestCoreAiSettings settings = new();
            LuaModsLlmTool tool = CreateWorldGatedModsTool(runtime, identity, settings, gate);
            Task<LuaTool.LuaResult> execute = executor.ExecuteAsync(
                "mutate_world()",
                CancellationToken.None);
            Task<string> load = tool.ExecuteAsync(
                "load",
                "ordered-mod",
                "local value = 1");

            CollectionAssert.AreEqual(
                new[]
                {
                    "capture:17:False",
                    "backup:" + LuaCsGameToolExecutor.ExecuteLuaBackupTrigger
                },
                sequence);

            firstBackupRelease.TrySetResult(true);
            LuaTool.LuaResult executeResult = await execute;
            JObject loadResult = JObject.Parse(await load);

            Assert.IsTrue(executeResult.Success, executeResult.Error);
            Assert.IsTrue(loadResult.Value<bool>("success"), loadResult.ToString());
            CollectionAssert.AreEqual(
                new[]
                {
                    "capture:17:False",
                    "backup:" + LuaCsGameToolExecutor.ExecuteLuaBackupTrigger,
                    "mutation:execute_lua",
                    "capture:18:False",
                    "backup:" + LuaModsLlmTool.LoadBackupTrigger
                },
                sequence);
            Assert.IsTrue(runtime.IsLoaded(actor, "ordered-mod"));
            Assert.AreEqual(0, store.ManualCalls);
        }

        [Test]
        public async Task FileStore_ManualSlotIsCreateOnce_AndFailedDurabilityIsNotSuccess()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore durableStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));

            RbxWorldPackageWriteResult first = await durableStore.CreateManualAsync("slot-a", payload);
            byte[] original = File.ReadAllBytes(first.Path);
            RbxWorldPackageWriteResult second = await durableStore.CreateManualAsync("slot-a", payload);

            Assert.IsTrue(first.Success);
            Assert.IsFalse(second.Success);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(first.Path));

            FileRbxWorldPackageStore unconfirmedStore = new(
                Path.Combine(root, "unconfirmed"),
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(false));
            RbxWorldPackageWriteResult unconfirmed =
                await unconfirmedStore.CreateManualAsync("slot-b", payload);

            Assert.IsFalse(unconfirmed.Success);
            StringAssert.Contains("durable persistence was not confirmed", unconfirmed.Error);
            Assert.IsFalse(File.Exists(unconfirmed.Path));
        }

        [Test]
        public async Task FileStore_FailedAutosaveDurability_PreservesConfirmedRingBytes()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            string root = NewTemporaryDirectory();
            int syncCalls = 0;
            FileRbxWorldPackageStore store = new(
                root,
                1,
                cancellationToken => UniTask.FromResult(++syncCalls == 1),
                () => CapturedAtUtc);
            RbxWorldPackageWriteResult confirmed =
                await store.CreateAutoAsync("confirmed", payload);
            byte[] confirmedBytes = File.ReadAllBytes(confirmed.Path);

            RbxWorldPackageWriteResult failed = await store.CreateAutoAsync("failed", payload);

            Assert.IsTrue(confirmed.Success);
            Assert.IsFalse(failed.Success);
            Assert.IsFalse(File.Exists(failed.Path));
            IReadOnlyList<string> remaining = store.ListAutoFiles();
            Assert.AreEqual(1, remaining.Count);
            Assert.AreEqual(Path.GetFileName(confirmed.Path), remaining[0]);
            CollectionAssert.AreEqual(confirmedBytes, File.ReadAllBytes(confirmed.Path));
        }

        [Test]
        public async Task FileStore_FailedFirstSync_ReloadKeepsOnlyPriorDurableAutosave()
        {
            MemoryDurabilityFileSystem fileSystem = new();
            Queue<bool> outcomes = new(new[] { true, false, true });
            FileRbxWorldPackageStore store = CreateDurabilityStore(fileSystem, outcomes);
            RbxWorldPackagePayload firstPayload = CreateMinimalPayload(CapturedAtUtc);
            RbxWorldPackagePayload secondPayload = CreateMinimalPayload(
                CapturedAtUtc.AddSeconds(1d));
            RbxWorldPackageWriteResult first = await store.CreateAutoAsync("first", firstPayload);
            byte[] firstBytes = await fileSystem.ReadAllBytesAsync(
                first.Path,
                CancellationToken.None);

            RbxWorldPackageWriteResult second = await store.CreateAutoAsync("second", secondPayload);
            fileSystem.ReloadFromDurable();

            Assert.IsTrue(first.Success);
            Assert.IsFalse(second.Success);
            CollectionAssert.AreEqual(
                new[] { Path.GetFileName(first.Path) },
                store.ListAutoFiles());
            CollectionAssert.AreEqual(
                firstBytes,
                await fileSystem.ReadAllBytesAsync(first.Path, CancellationToken.None));
        }

        [Test]
        public async Task FileStore_FailedRotationSync_ReloadRestoresExactPriorRing()
        {
            MemoryDurabilityFileSystem fileSystem = new();
            Queue<bool> outcomes = new(new[] { true, true, false, true });
            FileRbxWorldPackageStore store = CreateDurabilityStore(fileSystem, outcomes);
            RbxWorldPackageWriteResult first = await store.CreateAutoAsync(
                "first",
                CreateMinimalPayload(CapturedAtUtc));
            byte[] firstBytes = await fileSystem.ReadAllBytesAsync(
                first.Path,
                CancellationToken.None);

            RbxWorldPackageWriteResult second = await store.CreateAutoAsync(
                "second",
                CreateMinimalPayload(CapturedAtUtc.AddSeconds(1d)));
            fileSystem.ReloadFromDurable();

            Assert.IsTrue(first.Success);
            Assert.IsFalse(second.Success);
            StringAssert.Contains("rotation was not confirmed", second.Error);
            StringAssert.Contains("rollback durability was confirmed", second.Error);
            CollectionAssert.AreEqual(
                new[] { Path.GetFileName(first.Path) },
                store.ListAutoFiles());
            CollectionAssert.AreEqual(
                firstBytes,
                await fileSystem.ReadAllBytesAsync(first.Path, CancellationToken.None));
            Assert.IsFalse(fileSystem.FileExists(second.Path));
        }

        [Test]
        public async Task FileStore_ConfirmedSecondSync_ReloadRetainsDurableRingCapacity()
        {
            MemoryDurabilityFileSystem fileSystem = new();
            Queue<bool> outcomes = new(new[] { true, true, true });
            FileRbxWorldPackageStore store = CreateDurabilityStore(fileSystem, outcomes);
            RbxWorldPackageWriteResult first = await store.CreateAutoAsync(
                "first",
                CreateMinimalPayload(CapturedAtUtc));
            RbxWorldPackageWriteResult second = await store.CreateAutoAsync(
                "second",
                CreateMinimalPayload(CapturedAtUtc.AddSeconds(1d)));
            fileSystem.ReloadFromDurable();

            Assert.IsTrue(first.Success);
            Assert.IsTrue(second.Success);
            CollectionAssert.AreEqual(
                new[] { Path.GetFileName(second.Path) },
                store.ListAutoFiles());
            Assert.IsFalse(fileSystem.FileExists(first.Path));
        }

        [Test]
        public async Task FileStore_RotationReadFailureAfterDelete_ReloadRestoresExactPriorRing()
        {
            MemoryDurabilityFileSystem fileSystem = new();
            string root = Path.Combine(
                Path.GetTempPath(),
                "CoreAI-FakeDurability-" + Guid.NewGuid().ToString("N"));
            FileRbxWorldPackageStore setupStore = CreateDurabilityStore(
                fileSystem,
                new Queue<bool>(new[] { true, true, true }),
                root,
                3);
            RbxWorldPackageWriteResult first = await setupStore.CreateAutoAsync(
                "first",
                CreateMinimalPayload(CapturedAtUtc));
            RbxWorldPackageWriteResult second = await setupStore.CreateAutoAsync(
                "second",
                CreateMinimalPayload(CapturedAtUtc.AddSeconds(1d)));
            RbxWorldPackageWriteResult third = await setupStore.CreateAutoAsync(
                "third",
                CreateMinimalPayload(CapturedAtUtc.AddSeconds(2d)));
            Dictionary<string, byte[]> priorBytes = new(StringComparer.Ordinal)
            {
                [first.Path] = await fileSystem.ReadAllBytesAsync(first.Path, CancellationToken.None),
                [second.Path] = await fileSystem.ReadAllBytesAsync(second.Path, CancellationToken.None),
                [third.Path] = await fileSystem.ReadAllBytesAsync(third.Path, CancellationToken.None)
            };
            IReadOnlyList<string> priorNames = setupStore.ListAutoFiles();
            fileSystem.ArmReadFailure(2);
            FileRbxWorldPackageStore shrinkingStore = CreateDurabilityStore(
                fileSystem,
                new Queue<bool>(new[] { true, true }),
                root,
                1);

            RbxWorldPackageWriteResult failed = await shrinkingStore.CreateAutoAsync(
                "fourth",
                CreateMinimalPayload(CapturedAtUtc.AddSeconds(3d)));
            fileSystem.ReloadFromDurable();

            Assert.IsFalse(failed.Success);
            StringAssert.Contains("Exact pre-call state durability was restored", failed.Error);
            CollectionAssert.AreEqual(priorNames, shrinkingStore.ListAutoFiles());
            foreach (KeyValuePair<string, byte[]> entry in priorBytes)
            {
                CollectionAssert.AreEqual(
                    entry.Value,
                    await fileSystem.ReadAllBytesAsync(entry.Key, CancellationToken.None));
            }
        }

        [Test]
        public async Task FileStore_ConcurrentCreates_AreSerializedAcrossDurabilityPhases()
        {
            MemoryDurabilityFileSystem fileSystem = new();
            UniTaskCompletionSource<bool> firstSync = new();
            int syncCalls = 0;
            string root = Path.Combine(
                Path.GetTempPath(),
                "CoreAI-FakeDurability-" + Guid.NewGuid().ToString("N"));
            FileRbxWorldPackageStore store = new(
                root,
                1,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    syncCalls++;
                    if (syncCalls == 1)
                    {
                        return firstSync.Task;
                    }

                    fileSystem.Commit();
                    return UniTask.FromResult(true);
                },
                () => CapturedAtUtc,
                fileSystem);

            UniTask<RbxWorldPackageWriteResult> firstOperation = store.CreateAutoAsync(
                "first",
                CreateMinimalPayload(CapturedAtUtc));
            UniTask<RbxWorldPackageWriteResult> secondOperation = store.CreateAutoAsync(
                "second",
                CreateMinimalPayload(CapturedAtUtc.AddSeconds(1d)));

            Assert.AreEqual(1, syncCalls);
            fileSystem.Commit();
            firstSync.TrySetResult(true);
            RbxWorldPackageWriteResult first = await firstOperation;
            RbxWorldPackageWriteResult second = await secondOperation;
            fileSystem.ReloadFromDurable();

            Assert.IsTrue(first.Success);
            Assert.IsTrue(second.Success);
            Assert.AreEqual(3, syncCalls);
            CollectionAssert.AreEqual(
                new[] { Path.GetFileName(second.Path) },
                store.ListAutoFiles());
        }

        [Test]
        public async Task FileStore_AutosaveRingRotatesWithoutChangingManualBytes()
        {
            RuntimeWorld source = BuildAuthoredWorld();
            RbxWorldPackagePayload payload = Capture(source, CapturedAtUtc);
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                2,
                cancellationToken => UniTask.FromResult(true),
                () => CapturedAtUtc);
            RbxWorldPackageWriteResult manual = await store.CreateManualAsync("golden", payload);
            byte[] manualBytes = File.ReadAllBytes(manual.Path);

            Assert.IsTrue((await store.CreateAutoAsync("z-first", payload)).Success);
            Assert.IsTrue((await store.CreateAutoAsync("a-second", payload)).Success);
            Assert.IsTrue((await store.CreateAutoAsync("m-third", payload)).Success);

            IReadOnlyList<string> autoFiles = store.ListAutoFiles();
            Assert.AreEqual(2, autoFiles.Count);
            StringAssert.Contains("a-second", autoFiles[0]);
            StringAssert.Contains("m-third", autoFiles[1]);
            CollectionAssert.AreEqual(manualBytes, File.ReadAllBytes(manual.Path));
        }

        [Test]
        public async Task FileStore_OversizedHostileFileIsRejectedBeforeReadAllBytes()
        {
            string root = NewTemporaryDirectory();
            string manualDirectory = Path.Combine(root, "Manual");
            Directory.CreateDirectory(manualDirectory);
            string path = Path.Combine(manualDirectory, "hostile.world");
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength((long)RbxWorldPackageSerializer.MaximumPackageBytes + 1L);
            }

            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));

            RbxWorldPackageException exception = Assert.ThrowsAsync<RbxWorldPackageException>(
                async () => await store.LoadManualAsync("hostile"));
            StringAssert.Contains("format version 1 limit", exception.Message);
        }

        private RuntimeWorld BuildAuthoredWorld()
        {
            RuntimeWorld world = new(WorldId);
            _games.Add(world.Game);
            world.Stack.Runtime.LoadMod("world-builder", BuilderSource(), LuaCapabilities.All);
            world.Stack.Runtime.LoadMod(
                "dormant-helper", "local dormant = true", LuaCapabilities.Read);
            Assert.IsTrue(world.Stack.Runtime.UnloadMod("dormant-helper"));
            RbxModel ephemeralModel =
                (RbxModel)world.Registry.WorldRoot.FindFirstChild("EphemeralRuntimeModel");
            Assert.IsNotNull(ephemeralModel);
            RbxModel model = CreateDurableMirror(world, ephemeralModel);
            world.Registry.ConfigureWorldAclVersion(InstanceRegistry.CurrentWorldAclVersion);
            world.Registry.SetAccessControl(
                model, "actor-builder", InstanceAccessScope.Owned, true);
            return world;
        }

        private static RbxModel CreateDurableMirror(RuntimeWorld world, RbxModel ephemeralModel)
        {
            RbxModel durableModel = (RbxModel)world.Registry.Create(
                "Model",
                originTag: OriginTag.FromConsole("mvp3-fixture"));
            CopyMetadata(ephemeralModel, durableModel);
            durableModel.Name = "RuntimeModel";
            RbxInstance ephemeralPart = ephemeralModel.PrimaryPart;
            RbxInstance durablePart = world.Registry.Create(ephemeralPart.ClassName);
            CopyMetadata(ephemeralPart, durablePart);
            durablePart.Parent = durableModel;
            Assert.IsTrue(world.PartSink.TryGetPartProperties(
                ephemeralPart.Id, out PartProperties partProperties));
            world.PartSink.SetPartProperties(durablePart.Id, in partProperties);
            foreach (RbxInstance ephemeralChild in ephemeralPart.GetChildren())
            {
                RbxInstance durableChild = world.Registry.Create(ephemeralChild.ClassName);
                CopyMetadata(ephemeralChild, durableChild);
                if (ephemeralChild is RbxClickDetector ephemeralDetector
                    && durableChild is RbxClickDetector durableDetector)
                {
                    durableDetector.MaxActivationDistance =
                        ephemeralDetector.MaxActivationDistance;
                }

                durableChild.Parent = durablePart;
            }

            durableModel.SetPrimaryPart(durablePart);
            if (ephemeralModel.HasStoredWorldPivot)
            {
                RbxCFrame pivot = ephemeralModel.StoredWorldPivot;
                durableModel.SetWorldPivot(in pivot);
            }

            durableModel.Parent = world.Registry.WorldRoot;
            return durableModel;
        }

        private static void CopyMetadata(RbxInstance source, RbxInstance destination)
        {
            destination.Name = source.Name;
            destination.Archivable = source.Archivable;
            foreach (KeyValuePair<string, object> attribute in source.GetAttributes())
            {
                destination.SetAttribute(attribute.Key, attribute.Value);
            }

            foreach (string tag in source.GetTags())
            {
                destination.AddTag(tag);
            }
        }

        private static string BuilderSource()
        {
            return @"
                local model = Instance.new('Model')
                model.Name = 'EphemeralRuntimeModel'
                model.Parent = workspace
                model:SetAttribute('Label', 'boss')
                model:SetAttribute('Health', 125.5)
                model:SetAttribute('Enabled', true)
                model:SetAttribute('Spawn', Vector3.new(1.5, -2, 3.25))
                model:SetAttribute('Screen', Vector2.new(10, 20))
                model:SetAttribute('Tint', Color3.fromRGB(255, 128, 0))
                model:SetAttribute('Padding', UDim.new(0.5, 12))
                model:AddTag('Boss')
                model:AddTag('RuntimeAuthored')

                local part = Instance.new('Part')
                part.Name = 'PrimaryPart'
                part.Archivable = false
                part.CFrame = CFrame.new(2, 3, -4) * CFrame.Angles(0.2, -0.4, 0.6)
                part.Size = Vector3.new(7, 8, 9)
                part.Color = Color3.fromRGB(12, 34, 56)
                part.Anchored = true
                part.Transparency = 0.375
                part.CanCollide = false
                part.Shape = Enum.PartType.Ball
                part.Material = Enum.Material.Wood
                part.Parent = model

                local detector = Instance.new('ClickDetector')
                detector.Name = 'ClickTarget'
                detector.MaxActivationDistance = 17.25
                detector.Parent = part

                model.PrimaryPart = part
                model.WorldPivot = CFrame.new(-8, 6, 12) * CFrame.Angles(0.3, -0.5, 0.7)
                workspace.CurrentCamera.CFrame = CFrame.new(10, 5, -4) * CFrame.Angles(0.1, 0.2, 0.3)
                hooks_on('world_ping', function(_, payload)
                    store_set('last_ping', payload)
                end)";
        }

        private static RbxWorldPackagePayload Capture(RuntimeWorld world, DateTime capturedAtUtc)
        {
            return RbxWorldPackageSerializer.ExportSnapshot(
                new RbxWorldPackageCaptureContext(
                    world.Registry,
                    world.Game,
                    world.PartSink,
                    NewSettings(),
                    world.CameraRig,
                    world.SourceStore,
                    capturedAtUtc));
        }

        private RbxWorldPackagePayload CreateMinimalPayload(DateTime capturedAtUtc)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            return RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: capturedAtUtc));
        }

        private FileRbxWorldPackageStore CreateDurabilityStore(
            MemoryDurabilityFileSystem fileSystem,
            Queue<bool> outcomes,
            string root = null,
            int capacity = 1)
        {
            string resolvedRoot = root ?? Path.Combine(
                Path.GetTempPath(),
                "CoreAI-FakeDurability-" + Guid.NewGuid().ToString("N"));
            return new FileRbxWorldPackageStore(
                resolvedRoot,
                capacity,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool outcome = outcomes.Dequeue();
                    if (outcome)
                    {
                        fileSystem.Commit();
                    }

                    return UniTask.FromResult(outcome);
                },
                () => CapturedAtUtc,
                fileSystem);
        }

        private static RbxWorldSettings NewSettings()
        {
            return new RbxWorldSettings
            {
                WorldId = WorldId,
                MetersPerStud = 0.35f,
                GravityStudsPerSecondSquared = 144.5d,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            };
        }

        private static RbxWorldSettings CloneSettings(RbxWorldSettings source)
        {
            return new RbxWorldSettings
            {
                WorldId = source.WorldId,
                MetersPerStud = source.MetersPerStud,
                GravityStudsPerSecondSquared = source.GravityStudsPerSecondSquared,
                SignalBehavior = source.SignalBehavior
            };
        }

        private static LuaModsLlmTool CreateWorldGatedModsTool(
            LuaCsModRuntime runtime,
            IActorIdentityProvider identity,
            ICoreAISettings settings,
            IConfirmedWorldMutationGate gate)
        {
            return new LuaModsLlmTool(
                runtime,
                settings,
                NullLog.Instance,
                LuaCapabilities.All,
                true,
                identity,
                BuiltInAgentRoleIds.Programmer,
                gate);
        }

        private static async Task AssertManageModsMutationBlockedAsync(
            string action,
            BackupFailureMode failureMode,
            RbxWorldPackagePayload payload)
        {
            MemorySourceStore sourceStore = new();
            LuaCsModRuntime runtime = new(
                sourceStore: sourceStore,
                versionStore: new MemoryLuaScriptVersionStore());
            LocalActorIdentityProvider identity = new(
                "backup-" + action + "-" + failureMode.ToString().ToLowerInvariant());
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            const string sentinelId = "sentinel-mod";
            string targetId = "target-" + action;
            runtime.LoadMod(actor, sentinelId, "local sentinel = 1", LuaCapabilities.Read);

            string code = null;
            string bundle = null;
            int revision = -1;
            switch (action)
            {
                case "load":
                    code = "local loaded = 2";
                    break;
                case "reload":
                case "unload":
                case "forget":
                    runtime.LoadMod(actor, targetId, "local value = 1", LuaCapabilities.All);
                    code = action == "reload" ? "local value = 2" : null;
                    break;
                case "import":
                    bundle = Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        manifest = new LuaModManifest
                        {
                            Id = targetId,
                            Name = targetId,
                            Capabilities = LuaCapabilities.All.ToString(),
                            Active = true
                        },
                        source = "local imported = true"
                    });
                    break;
                case "revert":
                    runtime.LoadMod(actor, targetId, "local value = 1", LuaCapabilities.All);
                    runtime.ReloadMod(actor, targetId, "local value = 2");
                    revision = 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown mutation action.");
            }

            DelegateWorldPackageStore store = new((trigger, captured, cancellationToken) =>
                RejectBackup(failureMode));
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(payload),
                store);
            LuaModsLlmTool tool = CreateWorldGatedModsTool(
                runtime,
                identity,
                new TestCoreAiSettings(),
                gate);
            string[] trackedIds = { sentinelId, targetId };
            string before = CaptureManageModsState(runtime, actor, sourceStore, trackedIds);
            IReadOnlyList<string> manualBefore = store.ListManualSlots();

            JObject result = JObject.Parse(await tool.ExecuteAsync(
                action,
                targetId,
                code,
                bundle,
                revision));

            Assert.IsFalse(
                result.Value<bool>("success"),
                action + "/" + failureMode + ": " + result);
            Assert.AreEqual(
                before,
                CaptureManageModsState(runtime, actor, sourceStore, trackedIds),
                action + "/" + failureMode + " changed runtime, source, or revision state.");
            CollectionAssert.AreEqual(
                new[] { "manage_mods-" + action },
                store.AutoTriggers,
                action + "/" + failureMode + " used the wrong deterministic trigger.");
            Assert.AreEqual(
                0,
                store.ManualCalls,
                action + "/" + failureMode + " touched manual world slots.");
            CollectionAssert.AreEqual(
                manualBefore,
                store.ListManualSlots(),
                action + "/" + failureMode + " changed manual world slots.");
        }

        private static UniTask<RbxWorldPackageWriteResult> RejectBackup(
            BackupFailureMode failureMode)
        {
            if (failureMode == BackupFailureMode.FalseResult)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    false,
                    "",
                    "Injected durability refusal."));
            }

            if (failureMode == BackupFailureMode.Exception)
            {
                throw new IOException("Injected backup I/O failure.");
            }

            throw new OperationCanceledException("Injected backup cancellation.");
        }

        private static string CaptureManageModsState(
            LuaCsModRuntime runtime,
            ActorContext actor,
            MemorySourceStore sourceStore,
            IReadOnlyList<string> trackedIds)
        {
            StringBuilder builder = new();
            List<LuaModInfo> loaded = new(runtime.ListMods(actor));
            loaded.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            foreach (LuaModInfo mod in loaded)
            {
                builder.Append("loaded|")
                    .Append(mod.Id).Append('|')
                    .Append(mod.OwnerActorId).Append('|')
                    .Append(mod.Capabilities).Append('|')
                    .Append(mod.HandlerCount).Append('|')
                    .Append(mod.TimerCount).Append('|')
                    .Append(mod.ErrorCount).Append('|')
                    .Append(mod.Quarantined).Append('|')
                    .Append(mod.LogReports).AppendLine();
            }

            List<LuaModManifest> manifests = new(sourceStore.List());
            manifests.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            foreach (LuaModManifest manifest in manifests)
            {
                Assert.IsTrue(sourceStore.TryLoad(
                    manifest.Id,
                    out string storedSource,
                    out LuaModManifest storedManifest));
                builder.Append("stored|")
                    .Append(Newtonsoft.Json.JsonConvert.SerializeObject(storedManifest))
                    .Append('|')
                    .Append(storedSource)
                    .AppendLine();
            }

            List<string> ids = new(trackedIds);
            ids.Sort(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                bool hasSource = runtime.TryGetModSource(actor, id, out string source);
                builder.Append("tracked|")
                    .Append(id).Append('|')
                    .Append(runtime.IsLoaded(actor, id)).Append('|')
                    .Append(runtime.GetModOwnerActorId(actor, id)).Append('|')
                    .Append(hasSource).Append('|')
                    .Append(source)
                    .AppendLine();
                foreach (LuaScriptRevision item in runtime.ListModVersions(actor, id))
                {
                    builder.Append("revision|")
                        .Append(id).Append('|')
                        .Append(item.Index).Append('|')
                        .Append(item.UtcTicks).Append('|')
                        .Append(item.Source)
                        .AppendLine();
                }
            }

            return builder.ToString();
        }

        private static void AssertRestoredState(
            RuntimeWorld source,
            RbxWorldPackagePayload decoded,
            RbxWorldPackageRestoreResult restored,
            InMemoryCameraRig restoredCamera)
        {
            Assert.AreEqual(WorldId, decoded.Settings.WorldId);
            Assert.AreEqual(0.35f, decoded.Settings.MetersPerStud);
            Assert.AreEqual(144.5d, decoded.Settings.GravityStudsPerSecondSquared);
            Assert.AreEqual(InstanceRegistry.CurrentWorldAclVersion, restored.Registry.WorldAclVersion);
            Assert.AreEqual(source.Game.Id, restored.Game.Id);
            Assert.AreEqual(decoded.Tree.Instances.Count, restored.Registry.Count);

            RbxModel sourceModel = (RbxModel)source.Registry.WorldRoot.FindFirstChild("RuntimeModel");
            Assert.IsTrue(restored.Registry.TryGet(sourceModel.Id, out RbxInstance restoredNode));
            RbxModel restoredModel = (RbxModel)restoredNode;
            Assert.AreEqual("boss", restoredModel.GetAttribute("Label"));
            Assert.AreEqual(125.5d, restoredModel.GetAttribute("Health"));
            Assert.AreEqual(true, restoredModel.GetAttribute("Enabled"));
            Assert.AreEqual(new RbxVector3(1.5f, -2f, 3.25f), restoredModel.GetAttribute("Spawn"));
            Assert.AreEqual(new RbxVector2(10f, 20f), restoredModel.GetAttribute("Screen"));
            Assert.AreEqual(RbxColor3.FromRGB(255f, 128f, 0f), restoredModel.GetAttribute("Tint"));
            Assert.AreEqual(new RbxUDim(0.5f, 12), restoredModel.GetAttribute("Padding"));
            Assert.IsTrue(restoredModel.HasTag("Boss"));
            Assert.IsTrue(restoredModel.HasTag("RuntimeAuthored"));
            Assert.IsTrue(restored.Registry.TryGetRecord(restoredModel.Id, out InstanceRecord modelRecord));
            Assert.IsNull(modelRecord.OwnerModId);
            Assert.AreEqual(OriginTag.FromConsole("mvp3-fixture"), modelRecord.OriginTag);
            Assert.AreEqual("actor-builder", modelRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, modelRecord.AccessScope);
            InstanceSnapshot capturedModel = FindNode(decoded, "RuntimeModel");
            Assert.AreEqual(capturedModel.Revision, modelRecord.Revision);

            RbxInstance sourcePart = sourceModel.FindFirstChild("PrimaryPart");
            RbxInstance restoredPart = restoredModel.FindFirstChild("PrimaryPart");
            Assert.AreEqual(sourcePart.Id, restoredPart.Id);
            Assert.IsFalse(restoredPart.Archivable);
            Assert.AreEqual(restoredPart.Id, restoredModel.PrimaryPart.Id);
            Assert.IsTrue(restoredModel.HasStoredWorldPivot);
            CollectionAssert.AreEqual(
                sourceModel.StoredWorldPivot.GetComponents(),
                restoredModel.StoredWorldPivot.GetComponents());

            Assert.IsTrue(source.PartSink.TryGetPartProperties(
                sourcePart.Id, out PartProperties sourceProperties));
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(
                restoredPart.Id, out PartProperties restoredProperties));
            AssertPartPropertiesEqual(in sourceProperties, in restoredProperties);

            RbxClickDetector detector =
                (RbxClickDetector)restoredPart.FindFirstChild("ClickTarget");
            Assert.AreEqual(17.25d, detector.MaxActivationDistance);
            Assert.IsNull(restored.Registry.WorldRoot.FindFirstChild("EphemeralRuntimeModel"));
            CollectionAssert.AreEqual(
                source.CameraRig.GetCFrame().GetComponents(),
                restoredCamera.GetCFrame().GetComponents());

            Assert.AreEqual(2, restored.Mods.Count);
            Assert.AreEqual("dormant-helper", restored.Mods[0].Manifest.Id);
            Assert.IsFalse(restored.Mods[0].Manifest.Active);
            Assert.AreEqual("world-builder", restored.Mods[1].Manifest.Id);
            Assert.IsTrue(restored.Mods[1].Manifest.Active);
            Assert.AreEqual(BuilderSource(), restored.Mods[1].Source);
        }

        private static void AssertPartPropertiesEqual(
            in PartProperties expected,
            in PartProperties actual)
        {
            Assert.AreEqual(expected.Shape, actual.Shape);
            Assert.AreEqual(expected.Material.Name, actual.Material.Name);
            Assert.AreEqual(expected.Material.Value, actual.Material.Value);
            CollectionAssert.AreEqual(expected.CFrame.GetComponents(), actual.CFrame.GetComponents());
            Assert.AreEqual(expected.Size, actual.Size);
            Assert.AreEqual(expected.Color, actual.Color);
            Assert.AreEqual(expected.ColorWasExplicitlySet, actual.ColorWasExplicitlySet);
            Assert.AreEqual(expected.Anchored, actual.Anchored);
            Assert.AreEqual(expected.Transparency, actual.Transparency);
            Assert.AreEqual(expected.CanCollide, actual.CanCollide);
        }

        private static void AssertPrevalidationRejects(RbxWorldPackagePayload payload)
        {
            int scaleMutations = 0;
            ThrowingPartPropertySink sink = new();
            Assert.Catch<Exception>(() => RbxWorldPackageSerializer.RestoreFresh(
                payload,
                new RbxWorldPackageRestoreOptions
                {
                    PartSink = sink,
                    CameraRig = new InMemoryCameraRig(),
                    BeginMetersPerStudRestore = metersPerStud =>
                    {
                        scaleMutations++;
                        return () => scaleMutations--;
                    }
                }));
            Assert.AreEqual(0, scaleMutations);
            Assert.AreEqual(0, sink.FullStateCalls);
        }

        private static InstanceSnapshot FindNode(RbxWorldPackagePayload payload, string name)
        {
            InstanceSnapshot node = FindNodeOrNull(payload, name);
            if (node != null)
            {
                return node;
            }

            Assert.Fail("Missing package node '" + name + "'.");
            return null;
        }

        private static InstanceSnapshot FindNodeOrNull(RbxWorldPackagePayload payload, string name)
        {
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            return null;
        }

        private static AttributeSnapshot FindAttribute(InstanceSnapshot node, string name)
        {
            foreach (AttributeSnapshot attribute in node.Attributes)
            {
                if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
                {
                    return attribute;
                }
            }

            Assert.Fail("Missing package attribute '" + name + "'.");
            return null;
        }

        private string NewTemporaryDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "CoreAI-Mvp3World-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _temporaryDirectories.Add(directory);
            return directory;
        }

        private static byte[] ReplaceEntryText(
            byte[] package,
            string targetEntry,
            string oldText,
            string newText)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive source = new(input, ZipArchiveMode.Read, false);
            using MemoryStream output = new();
            using (ZipArchive destination = new(output, ZipArchiveMode.Create, true))
            {
                foreach (ZipArchiveEntry sourceEntry in source.Entries)
                {
                    byte[] bytes;
                    using (Stream entryStream = sourceEntry.Open())
                    using (MemoryStream entryBytes = new())
                    {
                        entryStream.CopyTo(entryBytes);
                        bytes = entryBytes.ToArray();
                    }

                    if (string.Equals(sourceEntry.FullName, targetEntry, StringComparison.Ordinal))
                    {
                        string text = new UTF8Encoding(false, true).GetString(bytes);
                        string replaced = text.Replace(oldText, newText);
                        Assert.AreNotEqual(text, replaced, "The red mutation must alter its target entry.");
                        bytes = new UTF8Encoding(false, true).GetBytes(replaced);
                    }

                    ZipArchiveEntry destinationEntry = destination.CreateEntry(
                        sourceEntry.FullName, CompressionLevel.Optimal);
                    destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
                    using Stream destinationStream = destinationEntry.Open();
                    destinationStream.Write(bytes, 0, bytes.Length);
                }
            }

            return output.ToArray();
        }

        private sealed class VariantWorld
        {
            public InstanceRegistry Registry;

            public RbxDataModel Game;

            public InMemoryPartPropertySink PartSink;

            public RbxInstance Part;

            public RbxMaterialVariant Variant;
        }

        private sealed class RuntimeWorld
        {
            public RuntimeWorld(string worldId)
            {
                Registry = new InstanceRegistry(worldId: worldId);
                Game = DataModelBootstrap.CreateGame(Registry);
                PartSink = new InMemoryPartPropertySink();
                CameraRig = new InMemoryCameraRig();
                SourceStore = new MemorySourceStore();
                LuaCsRbxApiBindings bindings = new(
                    Registry, Game, partSink: PartSink, cameraRig: CameraRig);
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new Mvp1AcceptanceNullLogger(),
                    ModStore = new Mvp1AcceptanceMemoryStore(),
                    ModSourceStore = SourceStore,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink PartSink { get; }

            public InMemoryCameraRig CameraRig { get; }

            public MemorySourceStore SourceStore { get; }

            public LuaCsModStack Stack { get; }
        }

        private sealed class RecordingLuaCsBindings : ILuaCsGameRuntimeBindings
        {
            private readonly Action _onMutation;

            public RecordingLuaCsBindings(Action onMutation = null)
            {
                _onMutation = onMutation;
                Ledger = new List<string> { "old-ledger-entry" };
            }

            public string TreeState { get; private set; } = "old-tree";

            public int Revision { get; private set; } = 17;

            public List<string> Ledger { get; }

            public void RegisterGameplayApis(LuaCsApiRegistry registry)
            {
                registry.Register("mutate_world", new Action(MutateWorld));
            }

            private void MutateWorld()
            {
                TreeState = "new-tree";
                Revision++;
                Ledger.Add("new-ledger-entry");
                _onMutation?.Invoke();
            }
        }

        private sealed class TestCoreAiSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 0;

            public bool EnableMeaiDebugLogging => false;

            public float LlmRequestTimeoutSeconds => 30f;

            public int MaxLlmRequestRetries => 0;

            public bool EnableHttpDebugLogging => false;

            public bool LogTokenUsage => false;

            public bool LogLlmLatency => false;

            public bool LogLlmConnectionErrors => false;

            public int ContextWindowTokens => 4096;

            public string UniversalSystemPromptPrefix => "";

            public float Temperature => 0f;

            public int MaxToolCallRetries => 0;

            public bool LogToolCalls => false;

            public bool LogToolCallArguments => false;

            public bool LogToolCallResults => false;

            public bool LogMeaiToolCallingSteps => false;

            public bool AllowDuplicateToolCalls => false;

            public bool EnableStreaming => false;
        }

        private enum BackupFailureMode
        {
            FalseResult,
            Exception,
            Cancellation
        }

        private sealed class DelegateWorldPackageStore : IRbxWorldPackageStore
        {
            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }

            private readonly Func<string, RbxWorldPackagePayload, CancellationToken,
                UniTask<RbxWorldPackageWriteResult>> _createAutoAsync;
            private readonly IReadOnlyList<string> _manualSlots = new[] { "golden-slot" };

            public DelegateWorldPackageStore(
                Func<string, RbxWorldPackagePayload, CancellationToken,
                    UniTask<RbxWorldPackageWriteResult>> createAutoAsync)
            {
                _createAutoAsync = createAutoAsync
                                   ?? throw new ArgumentNullException(nameof(createAutoAsync));
            }

            public List<string> AutoTriggers { get; } = new();

            public int ManualCalls { get; private set; }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
                string slot,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                ManualCalls++;
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    false,
                    "",
                    "Manual slots are outside this test seam."));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
                string trigger,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                AutoTriggers.Add(trigger);
                return _createAutoAsync(trigger, payload, cancellationToken);
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(
                string slot,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(
                string fileName,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return _manualSlots;
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                return Array.Empty<string>();
            }
        }

        private sealed class MemoryDurabilityFileSystem : IRbxWorldPackageFileSystem
        {
            private readonly Dictionary<string, byte[]> _volatileFiles =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, byte[]> _durableFiles =
                new(StringComparer.Ordinal);
            private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
            private int _readCalls;
            private int _failReadCall;

            public bool DirectoryExists(string path)
            {
                return _directories.Contains(Normalize(path));
            }

            public void CreateDirectory(string path)
            {
                _directories.Add(Normalize(path));
            }

            public bool FileExists(string path)
            {
                return _volatileFiles.ContainsKey(Normalize(path));
            }

            public long GetFileLength(string path)
            {
                return _volatileFiles[Normalize(path)].LongLength;
            }

            public UniTask WriteAllBytesCreateNewAsync(
                string path,
                byte[] bytes,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = Normalize(path);
                if (_volatileFiles.ContainsKey(normalized))
                {
                    throw new IOException("File already exists: " + normalized);
                }

                _volatileFiles.Add(normalized, Clone(bytes));
                string directory = Path.GetDirectoryName(normalized);
                if (!string.IsNullOrEmpty(directory))
                {
                    _directories.Add(directory);
                }

                return UniTask.CompletedTask;
            }

            public UniTask<byte[]> ReadAllBytesAsync(
                string path,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _readCalls++;
                if (_failReadCall != 0 && _readCalls == _failReadCall)
                {
                    throw new IOException("Injected volatile read failure.");
                }

                return UniTask.FromResult(Clone(_volatileFiles[Normalize(path)]));
            }

            public void MoveCreateNew(string sourcePath, string destinationPath)
            {
                string source = Normalize(sourcePath);
                string destination = Normalize(destinationPath);
                if (!_volatileFiles.TryGetValue(source, out byte[] bytes))
                {
                    throw new FileNotFoundException("Missing source file.", source);
                }

                if (_volatileFiles.ContainsKey(destination))
                {
                    throw new IOException("File already exists: " + destination);
                }

                _volatileFiles.Remove(source);
                _volatileFiles.Add(destination, bytes);
            }

            public void DeleteFile(string path)
            {
                _volatileFiles.Remove(Normalize(path));
            }

            public IReadOnlyList<string> GetFiles(string directory, string extension)
            {
                string normalizedDirectory = Normalize(directory);
                List<string> files = new();
                foreach (string path in _volatileFiles.Keys)
                {
                    if (string.Equals(
                            Path.GetDirectoryName(path),
                            normalizedDirectory,
                            StringComparison.Ordinal)
                        && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }

                return files;
            }

            public void Commit()
            {
                _durableFiles.Clear();
                foreach (KeyValuePair<string, byte[]> entry in _volatileFiles)
                {
                    _durableFiles.Add(entry.Key, Clone(entry.Value));
                }
            }

            public void ReloadFromDurable()
            {
                _volatileFiles.Clear();
                foreach (KeyValuePair<string, byte[]> entry in _durableFiles)
                {
                    _volatileFiles.Add(entry.Key, Clone(entry.Value));
                }
            }

            public void ArmReadFailure(int readCall)
            {
                _readCalls = 0;
                _failReadCall = readCall;
            }

            private static string Normalize(string path)
            {
                return Path.GetFullPath(path);
            }

            private static byte[] Clone(byte[] bytes)
            {
                byte[] clone = new byte[bytes.Length];
                Buffer.BlockCopy(bytes, 0, clone, 0, bytes.Length);
                return clone;
            }
        }

        private sealed class MemorySourceStore : ILuaModSourceStore
        {
            private sealed class Entry
            {
                public string Source;
                public LuaModManifest Manifest;
            }

            private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = new Entry
                {
                    Source = source,
                    Manifest = CloneManifest(manifest)
                };
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out Entry entry))
                {
                    source = entry.Source;
                    manifest = CloneManifest(entry.Manifest);
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new(_entries.Count);
                foreach (Entry entry in _entries.Values)
                {
                    manifests.Add(CloneManifest(entry.Manifest));
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out Entry entry))
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }

            public void ReplaceWith(IReadOnlyList<RbxWorldModSource> mods)
            {
                _entries.Clear();
                foreach (RbxWorldModSource mod in mods)
                {
                    Save(mod.Manifest.Id, mod.Source, mod.Manifest);
                }
            }

            private static LuaModManifest CloneManifest(LuaModManifest source)
            {
                return new LuaModManifest
                {
                    Id = source.Id,
                    Name = source.Name,
                    Description = source.Description,
                    Version = source.Version,
                    Category = source.Category,
                    Tags = source.Tags,
                    Origin = source.Origin,
                    SeededVersion = source.SeededVersion,
                    SeededHash = source.SeededHash,
                    Author = source.Author,
                    OwnerActorId = source.OwnerActorId,
                    Capabilities = source.Capabilities,
                    Active = source.Active,
                    UpdateAvailable = source.UpdateAvailable,
                    Entry = source.Entry
                };
            }
        }

        private sealed class ThrowingPartPropertySink : IPartPropertySink
        {
            public int FullStateCalls { get; private set; }

            public void SetCFrame(InstanceId id, in RbxCFrame cframe)
            {
                throw new NotSupportedException();
            }

            public void SetPosition(InstanceId id, RbxVector3 position)
            {
                throw new NotSupportedException();
            }

            public void SetSize(InstanceId id, RbxVector3 size)
            {
                throw new NotSupportedException();
            }

            public void SetColor(InstanceId id, RbxColor3 color)
            {
                throw new NotSupportedException();
            }

            public void SetAnchored(InstanceId id, bool anchored)
            {
                throw new NotSupportedException();
            }

            public void SetTransparency(InstanceId id, float transparency)
            {
                throw new NotSupportedException();
            }

            public void SetCanCollide(InstanceId id, bool canCollide)
            {
                throw new NotSupportedException();
            }

            public void SetShape(InstanceId id, RbxPartShape shape)
            {
                throw new NotSupportedException();
            }

            public void SetMaterial(InstanceId id, in RbxMaterialId material)
            {
                throw new NotSupportedException();
            }

            public void SetMaterialVariant(InstanceId id, string variantName)
            {
                throw new NotSupportedException();
            }

            public void RefreshMaterialVariant(string variantName)
            {
                throw new NotSupportedException();
            }

            public void SetPartProperties(InstanceId id, in PartProperties properties)
            {
                FullStateCalls++;
                throw new InvalidOperationException("Injected Part restore failure.");
            }

            public bool TryGetPartProperties(InstanceId id, out PartProperties properties)
            {
                properties = default;
                return false;
            }

            public PartProperties GetPartPropertiesOrDefault(InstanceId id)
            {
                return PartProperties.CreateDefault();
            }
        }

        private sealed class CountingBinder : IInstanceBackingBinder
        {
            public int RegisterCalls { get; private set; }

            public void OnEnteredWorld(InstanceRecord record)
            {
                RegisterCalls++;
            }

            public void OnLeftWorld(InstanceRecord record)
            {
            }

            public void OnDestroyed(InstanceRecord record)
            {
            }

            public void OnReparented(InstanceRecord record)
            {
            }

            public void OnNameChanged(InstanceRecord record)
            {
            }
        }
    }
}
