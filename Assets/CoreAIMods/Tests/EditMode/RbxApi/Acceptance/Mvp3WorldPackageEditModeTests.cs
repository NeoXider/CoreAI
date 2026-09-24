using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
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

        private const string GoldenWorldId = "mvp3-golden";

        /// <summary>2^53 + 1: the first id a JSON number (an IEEE double) cannot represent exactly.</summary>
        private const ulong GoldenFirstId = 9007199254740993UL;

        private const string HandWrittenManifestJson = @"{
  ""format"": ""coreai-rbx-world"",
  ""format_version"": 1,
  ""minimum_reader_version"": 1,
  ""api_version"": ""MVP2"",
  ""created_utc"": ""2026-09-01T06:07:08.0000000Z"",
  ""world_entry"": ""world.json"",
  ""mods"": [
    {
      ""id"": ""golden-mod"",
      ""manifest_entry"": ""Mods/0000/manifest.json"",
      ""source_entry"": ""Mods/0000/main.lua""
    }
  ]
}";

        private const string HandWrittenModManifestJson = @"{
  ""Id"": ""golden-mod"",
  ""Name"": ""Golden Mod"",
  ""Active"": false
}";

        private const string HandWrittenWorldJson = @"{
  ""schema_version"": 1,
  ""settings"": {
    ""world_id"": ""mvp3-handwritten"",
    ""world_acl_version"": 1,
    ""meters_per_stud"": 0.5,
    ""gravity_studs_per_second_squared"": 144.5,
    ""signal_behavior"": ""Deferred""
  },
  ""camera_cframe"": null,
  ""instances"": [
    {
      ""id"": ""9007199254740993"",
      ""parent_id"": ""0"",
      ""class_name"": ""DataModel"",
      ""name"": ""Game"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": ""HostProtected"",
      ""revision"": ""3"",
      ""tags"": [],
      ""attributes"": []
    },
    {
      ""id"": ""9007199254740995"",
      ""parent_id"": ""9007199254740993"",
      ""class_name"": ""Workspace"",
      ""name"": ""Workspace"",
      ""archivable"": true,
      ""access_scope"": ""HostProtected"",
      ""revision"": ""5"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": {
        ""primary_part_id"": ""0"",
        ""has_stored_world_pivot"": false,
        ""stored_world_pivot"": null
      }
    },
    {
      ""id"": ""9007199254740997"",
      ""parent_id"": ""9007199254740995"",
      ""class_name"": ""Model"",
      ""name"": ""HandModel"",
      ""archivable"": true,
      ""origin_tag"": ""console:hand-written"",
      ""owner_actor_id"": ""actor-hand"",
      ""access_scope"": ""Owned"",
      ""revision"": ""9007199254740999"",
      ""tags"": [ ""Golden"" ],
      ""attributes"": [
        {
          ""name"": ""Label"",
          ""kind"": ""String"",
          ""string_value"": ""hand"",
          ""number_value"": 0,
          ""bool_value"": false
        }
      ],
      ""model"": {
        ""primary_part_id"": ""9007199254741001"",
        ""has_stored_world_pivot"": true,
        ""stored_world_pivot"": [ 1, 2, 3, 1, 0, 0, 0, 1, 0, 0, 0, 1 ]
      }
    },
    {
      ""id"": ""9007199254741001"",
      ""parent_id"": ""9007199254740997"",
      ""class_name"": ""Part"",
      ""name"": ""HandPart"",
      ""archivable"": false,
      ""origin_tag"": ""console:hand-written"",
      ""owner_actor_id"": ""actor-hand"",
      ""access_scope"": ""Owned"",
      ""revision"": ""7"",
      ""tags"": [],
      ""attributes"": [],
      ""part"": {
        ""shape"": ""Cylinder"",
        ""shape_value"": 2,
        ""material"": ""Wood"",
        ""material_value"": 512,
        ""material_variant"": null,
        ""cframe"": [ 2, 3, -4, 0, 0, 1, 0, 1, 0, -1, 0, 0 ],
        ""size"": [ 4, 1.5, 2 ],
        ""color"": [ 0.25, 0.5, 0.75 ],
        ""color_was_explicitly_set"": true,
        ""anchored"": true,
        ""transparency"": 0.25,
        ""can_collide"": false
      }
    }
  ]
}";

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
            Assert.IsNull(payload.Diagnostics[0].Member);
            Assert.IsNotNull(durableModel.PrimaryPart);
            Assert.AreEqual(ephemeralPart.Id, durableModel.PrimaryPart.Id);
            JToken entry = ParseJsonLiteral(ReadEntryText(
                RbxWorldPackageSerializer.WritePackage(payload),
                RbxWorldPackageSerializer.ManifestEntryName))["diagnostics"][0];
            CollectionAssert.AreEqual(
                new[] { "model_id", "dropped_primary_part_id", "reason" },
                PropertyNames(entry),
                "A PrimaryPart diagnostic keeps its pre-member shape, so older readers still read it.");
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void Capture_NumberValueHoldingNonFiniteValue_PackagesZeroWithOneDiagnosticAndKeepsLiveValue(
            double nonFinite)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            RbxNumberValue score = (RbxNumberValue)registry.Create(
                "NumberValue",
                originTag: OriginTag.FromConsole("non-finite-fixture"));
            score.Name = "Score";
            score.Parent = registry.WorldRoot;
            score.Value = nonFinite;
            RbxWorldPackageCaptureContext context = new(
                registry,
                game,
                new InMemoryPartPropertySink(),
                NewSettings(),
                capturedAtUtc: CapturedAtUtc);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(context);
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            CollectionAssert.AreEqual(
                package,
                RbxWorldPackageSerializer.WritePackage(RbxWorldPackageSerializer.ExportSnapshot(context)),
                "The disk capture and the join snapshot must share one projection.");
            Assert.AreEqual("0", FindNode(payload, "Score").Value.StringValue);
            AssertOnlyNonFiniteDiagnostics(payload, score.Id.Value + ":Value");
            JToken entry = ParseJsonLiteral(
                ReadEntryText(package, RbxWorldPackageSerializer.ManifestEntryName))["diagnostics"][0];
            CollectionAssert.AreEqual(
                new[] { "model_id", "dropped_primary_part_id", "reason", "member" },
                PropertyNames(entry));
            Assert.AreEqual(
                score.Id.Value.ToString(CultureInfo.InvariantCulture), (string)entry["model_id"]);
            Assert.AreEqual("0", (string)entry["dropped_primary_part_id"]);
            Assert.AreEqual("non-finite-value", (string)entry["reason"]);
            Assert.AreEqual("Value", (string)entry["member"]);

            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(package);
            Assert.AreEqual("0", FindNode(decoded, "Score").Value.StringValue);
            AssertOnlyNonFiniteDiagnostics(decoded, score.Id.Value + ":Value");
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(decoded),
                "decode -> encode must keep the diagnostic member byte-identical.");
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(decoded);
            _games.Add(restored.Game);
            Assert.IsTrue(restored.Registry.TryGet(score.Id, out RbxInstance restoredScore));
            Assert.AreEqual(0d, ((RbxNumberValue)restoredScore).Value);
            Assert.IsTrue(nonFinite.Equals(score.Value),
                "Only the payload is adjusted; the live NumberValue keeps what the script wrote.");
        }

        [Test]
        public void Capture_EveryScriptReachableNonFiniteMember_PackagesItsDefaultWithOneDiagnosticEach()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            InMemoryCameraRig cameraRig = new();
            RbxModel model = (RbxModel)registry.Create("Model");
            model.Name = "Pivoted";
            model.Parent = registry.WorldRoot;
            RbxCFrame nonFinitePivot = RbxCFrame.FromPosition(float.NaN, 0f, 0f);
            model.SetWorldPivot(in nonFinitePivot);
            model.SetAttribute("Speed", double.NaN);
            model.SetAttribute("Spawn", new RbxVector3(float.PositiveInfinity, 0f, 0f));
            model.SetAttribute("Screen", new RbxVector2(0f, float.NaN));
            model.SetAttribute("Tint", new RbxColor3(float.NaN, 0f, 0f));
            model.SetAttribute("Pad", new RbxUDim(float.NegativeInfinity, 3));
            model.SetAttribute("Kept", 7d);
            RbxInstance part = registry.Create("Part");
            part.Name = "Brick";
            part.Parent = model;
            PartProperties defaults = PartProperties.CreateDefault();
            partSink.SetPartProperties(part.Id, in defaults);
            partSink.SetPosition(part.Id, new RbxVector3(float.NaN, 0f, 0f));
            partSink.SetSize(part.Id, new RbxVector3(1f, float.PositiveInfinity, 1f));
            partSink.SetColor(part.Id, new RbxColor3(float.NaN, 0f, 0f));
            partSink.SetTransparency(part.Id, float.NaN);
            RbxClickDetector detector = (RbxClickDetector)registry.Create("ClickDetector");
            detector.Parent = part;
            detector.MaxActivationDistance = double.NaN;
            RbxMaterialVariant variant = (RbxMaterialVariant)registry.Create("MaterialVariant");
            variant.Name = "Mossy";
            variant.Parent = game.FindFirstChildOfClass("MaterialService");
            variant.StudsPerTile = float.PositiveInfinity;
            RbxNumberValue number = (RbxNumberValue)registry.Create("NumberValue");
            number.Parent = model;
            number.Value = double.NaN;
            RbxVector3Value vector = (RbxVector3Value)registry.Create("Vector3Value");
            vector.Parent = model;
            vector.Value = new RbxVector3(0f, float.NaN, 0f);
            RbxCFrameValue cframe = (RbxCFrameValue)registry.Create("CFrameValue");
            cframe.Parent = model;
            cframe.Value = RbxCFrame.FromPosition(0f, 0f, float.NegativeInfinity);
            RbxColor3Value color = (RbxColor3Value)registry.Create("Color3Value");
            color.Parent = model;
            color.Value = new RbxColor3(0f, 0f, float.PositiveInfinity);
            RbxNumberValue modOwned = (RbxNumberValue)registry.Create(
                "NumberValue",
                "nan-mod",
                OriginTag.FromMod("nan-mod"));
            modOwned.Parent = registry.WorldRoot;
            modOwned.Value = double.NaN;
            RbxCFrame nonFiniteCamera = RbxCFrame.FromPosition(0f, float.NaN, 0f);
            cameraRig.SetCFrame(in nonFiniteCamera);
            RbxInstance camera = registry.WorldRoot.FindFirstChildOfClass("Camera");
            Assert.IsNotNull(camera);
            RbxWorldPackageCaptureContext context = new(
                registry,
                game,
                partSink,
                NewSettings(),
                cameraRig,
                capturedAtUtc: CapturedAtUtc);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(context);
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            string[] expected =
            {
                model.Id.Value + ":WorldPivot",
                model.Id.Value + ":Attributes.Speed",
                model.Id.Value + ":Attributes.Spawn",
                model.Id.Value + ":Attributes.Screen",
                model.Id.Value + ":Attributes.Tint",
                model.Id.Value + ":Attributes.Pad",
                part.Id.Value + ":CFrame",
                part.Id.Value + ":Size",
                part.Id.Value + ":Color",
                part.Id.Value + ":Transparency",
                detector.Id.Value + ":MaxActivationDistance",
                variant.Id.Value + ":StudsPerTile",
                number.Id.Value + ":Value",
                vector.Id.Value + ":Value",
                cframe.Id.Value + ":Value",
                color.Id.Value + ":Value",
                camera.Id.Value + ":CFrame"
            };
            AssertOnlyNonFiniteDiagnostics(payload, expected);
            CollectionAssert.AreEqual(
                package,
                RbxWorldPackageSerializer.WritePackage(RbxWorldPackageSerializer.Capture(context)),
                "Two captures of the same live world must write byte-identical packages.");
            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(package);
            AssertOnlyNonFiniteDiagnostics(decoded, expected);

            InMemoryCameraRig restoredCamera = new();
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                decoded,
                new RbxWorldPackageRestoreOptions { CameraRig = restoredCamera });
            _games.Add(restored.Game);
            Assert.IsTrue(restored.Registry.TryGet(model.Id, out RbxInstance restoredModel));
            Assert.IsFalse(((RbxModel)restoredModel).HasStoredWorldPivot);
            Assert.IsNull(restoredModel.GetAttribute("Speed"));
            Assert.IsNull(restoredModel.GetAttribute("Spawn"));
            Assert.IsNull(restoredModel.GetAttribute("Screen"));
            Assert.IsNull(restoredModel.GetAttribute("Tint"));
            Assert.IsNull(restoredModel.GetAttribute("Pad"));
            Assert.AreEqual(7d, restoredModel.GetAttribute("Kept"));
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(part.Id, out PartProperties restoredPart));
            AssertPartPropertiesEqual(in defaults, in restoredPart);
            Assert.IsTrue(restored.Registry.TryGet(detector.Id, out RbxInstance restoredDetector));
            Assert.AreEqual(
                ((RbxClickDetector)registry.Create("ClickDetector")).MaxActivationDistance,
                ((RbxClickDetector)restoredDetector).MaxActivationDistance);
            Assert.IsTrue(restored.Registry.TryGet(variant.Id, out RbxInstance restoredVariant));
            Assert.AreEqual(
                ((RbxMaterialVariant)registry.Create("MaterialVariant")).StudsPerTile,
                ((RbxMaterialVariant)restoredVariant).StudsPerTile);
            Assert.IsTrue(restored.Registry.TryGet(number.Id, out RbxInstance restoredNumber));
            Assert.AreEqual(0d, ((RbxNumberValue)restoredNumber).Value);
            Assert.IsTrue(restored.Registry.TryGet(vector.Id, out RbxInstance restoredVector));
            Assert.AreEqual(RbxVector3.Zero, ((RbxVector3Value)restoredVector).Value);
            Assert.IsTrue(restored.Registry.TryGet(cframe.Id, out RbxInstance restoredCFrame));
            Assert.AreEqual(RbxCFrame.Identity, ((RbxCFrameValue)restoredCFrame).Value);
            Assert.IsTrue(restored.Registry.TryGet(color.Id, out RbxInstance restoredColor));
            Assert.AreEqual(new RbxColor3(0f, 0f, 0f), ((RbxColor3Value)restoredColor).Value);
            Assert.AreEqual(RbxCFrame.Identity, restoredCamera.GetCFrame());
            Assert.IsFalse(restored.Registry.TryGet(modOwned.Id, out RbxInstance _),
                "A mod-owned value is excluded by the ownership projection, not reported.");

            Assert.IsTrue(model.HasStoredWorldPivot);
            Assert.IsTrue(float.IsNaN(model.StoredWorldPivot.GetComponents()[0]));
            Assert.IsTrue(double.IsNaN((double)model.GetAttribute("Speed")));
            Assert.IsTrue(partSink.TryGetPartProperties(part.Id, out PartProperties livePart));
            Assert.IsTrue(float.IsNaN(livePart.CFrame.GetComponents()[0]));
            Assert.IsTrue(float.IsPositiveInfinity(livePart.Size.Y));
            Assert.IsTrue(float.IsNaN(livePart.Color.R));
            Assert.IsTrue(livePart.ColorWasExplicitlySet);
            Assert.IsTrue(float.IsNaN(livePart.Transparency));
            Assert.IsTrue(double.IsNaN(detector.MaxActivationDistance));
            Assert.IsTrue(float.IsPositiveInfinity(variant.StudsPerTile));
            Assert.IsTrue(double.IsNaN(number.Value));
            Assert.IsTrue(float.IsNaN(vector.Value.Y));
            Assert.IsTrue(float.IsNegativeInfinity(cframe.Value.GetComponents()[2]));
            Assert.IsTrue(float.IsPositiveInfinity(color.Value.B));
            Assert.IsTrue(float.IsNaN(cameraRig.GetCFrame().GetComponents()[1]));
        }

        [TestCase("\"string_value\": \"5.5\"", "\"string_value\": \"NaN\"", "non-finite Value")]
        [TestCase("\"number_value\": 2.5", "\"number_value\": NaN", "is not a finite JSON number")]
        [TestCase("\"number_value\": 2.5", "\"number_value\": Infinity", "is not a finite JSON number")]
        public void ReadPackage_HandCraftedNonFiniteValue_IsRejectedBeforeRestore(
            string finiteText,
            string nonFiniteText,
            string expectedMessage)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            RbxNumberValue score = (RbxNumberValue)registry.Create("NumberValue");
            score.Name = "Score";
            score.Parent = registry.WorldRoot;
            score.Value = 5.5d;
            score.SetAttribute("Ratio", 2.5d);
            byte[] package = RbxWorldPackageSerializer.WritePackage(RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc)));
            Assert.DoesNotThrow(() => RbxWorldPackageSerializer.ReadPackage(package));
            byte[] hostile = ReplaceEntryText(
                package, RbxWorldPackageSerializer.WorldEntryName, finiteText, nonFiniteText);

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(hostile));

            StringAssert.Contains(expectedMessage, exception.Message);
        }

        [Test]
        public void Capture_DanglingReferencesAndOutOfRangeValues_ProjectOneDiagnosticEachAndKeepLiveState()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxInstance workspace = registry.WorldRoot;
            RbxInstance materialService = game.FindFirstChildOfClass("MaterialService");
            RbxInstance target = CreateNamed(registry, "Folder", "Target", workspace);
            RbxInstance destroyedTarget = CreateNamed(registry, "Folder", "DestroyedTarget", workspace);
            RbxInstance unparentedTarget = registry.Create("Folder");
            RbxInstance modTarget = registry.Create("Folder", "ref-mod", OriginTag.FromMod("ref-mod"));
            modTarget.Parent = workspace;
            RbxObjectValue refKept = (RbxObjectValue)CreateNamed(registry, "ObjectValue", "RefKept", workspace);
            refKept.Value = target;
            RbxObjectValue refDestroyed =
                (RbxObjectValue)CreateNamed(registry, "ObjectValue", "RefDestroyed", workspace);
            refDestroyed.Value = destroyedTarget;
            destroyedTarget.Destroy();
            RbxObjectValue refUnparented =
                (RbxObjectValue)CreateNamed(registry, "ObjectValue", "RefUnparented", workspace);
            refUnparented.Value = unparentedTarget;
            RbxObjectValue refModOwned =
                (RbxObjectValue)CreateNamed(registry, "ObjectValue", "RefModOwned", workspace);
            refModOwned.Value = modTarget;
            RbxInstance button = CreateNamedPart(registry, partSink, "Button", workspace, null);
            RbxClickDetector clicker = (RbxClickDetector)CreateNamed(registry, "ClickDetector", "Clicker", button);
            clicker.MaxActivationDistance = -5d;
            RbxClickDetector zeroClicker =
                (RbxClickDetector)CreateNamed(registry, "ClickDetector", "ZeroClicker", button);
            zeroClicker.MaxActivationDistance = 0d;
            RbxMaterialVariant mossy =
                (RbxMaterialVariant)CreateNamed(registry, "MaterialVariant", "Mossy", materialService);
            mossy.StudsPerTile = 0f;
            RbxInstance modMoss = registry.Create(
                "MaterialVariant", "moss-mod", OriginTag.FromMod("moss-mod"));
            modMoss.Name = "ModMoss";
            modMoss.Parent = materialService;
            RbxModel rig = (RbxModel)CreateNamed(registry, "Model", "Rig", workspace);
            CreateNamedPart(registry, partSink, "Head", rig, null);
            RbxInstance loose = CreateNamedPart(registry, partSink, "Loose", workspace, null);
            rig.SetPrimaryPart(loose);
            RbxModel keptModel = (RbxModel)CreateNamed(registry, "Model", "KeptModel", workspace);
            RbxInstance keptPrimary = CreateNamedPart(registry, partSink, "KeptPrimary", keptModel, null);
            keptModel.SetPrimaryPart(keptPrimary);
            RbxInstance brick = CreateNamedPart(registry, partSink, "Brick", workspace, "Nope");
            RbxInstance tile = CreateNamedPart(registry, partSink, "Tile", workspace, "Mossy");
            RbxInstance modTile = CreateNamedPart(registry, partSink, "ModTile", workspace, "ModMoss");
            RbxWorldPackageCaptureContext context = new(
                registry,
                game,
                partSink,
                NewSettings(),
                capturedAtUtc: CapturedAtUtc);

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(context);
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            string[] expected =
            {
                Diagnostic(refDestroyed, null, "missing", "Value"),
                Diagnostic(refUnparented, null, "missing", "Value"),
                Diagnostic(refModOwned, null, "mod-ephemeral", "Value"),
                Diagnostic(clicker, null, "out-of-range", "MaxActivationDistance"),
                Diagnostic(mossy, null, "out-of-range", "StudsPerTile"),
                Diagnostic(rig, loose, "not-descendant", null),
                Diagnostic(brick, null, "missing", "MaterialVariant"),
                Diagnostic(modTile, null, "mod-ephemeral", "MaterialVariant")
            };
            AssertDiagnostics(payload, expected);
            CollectionAssert.AreEqual(
                package,
                RbxWorldPackageSerializer.WritePackage(RbxWorldPackageSerializer.ExportSnapshot(context)),
                "The disk capture and the join snapshot must share one projection.");
            JArray manifestDiagnostics = (JArray)ParseJsonLiteral(
                ReadEntryText(package, RbxWorldPackageSerializer.ManifestEntryName))["diagnostics"];
            foreach (JToken entry in manifestDiagnostics)
            {
                bool primaryPartEntry = (string)entry["reason"] == "not-descendant";
                CollectionAssert.AreEqual(
                    primaryPartEntry
                        ? new[] { "model_id", "dropped_primary_part_id", "reason" }
                        : new[] { "model_id", "dropped_primary_part_id", "reason", "member" },
                    PropertyNames(entry));
            }

            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(package);
            AssertDiagnostics(decoded, expected);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(decoded);
            _games.Add(restored.Game);
            Assert.AreEqual(target.Id, RestoredObjectValue(restored, refKept).Value.Id);
            Assert.IsNull(RestoredObjectValue(restored, refDestroyed).Value);
            Assert.IsNull(RestoredObjectValue(restored, refUnparented).Value);
            Assert.IsNull(RestoredObjectValue(restored, refModOwned).Value);
            Assert.IsTrue(restored.Registry.TryGet(clicker.Id, out RbxInstance restoredClicker));
            Assert.AreEqual(32d, ((RbxClickDetector)restoredClicker).MaxActivationDistance);
            Assert.IsTrue(restored.Registry.TryGet(zeroClicker.Id, out RbxInstance restoredZero));
            Assert.AreEqual(0d, ((RbxClickDetector)restoredZero).MaxActivationDistance);
            Assert.IsTrue(restored.Registry.TryGet(mossy.Id, out RbxInstance restoredMossy));
            Assert.AreEqual(1f, ((RbxMaterialVariant)restoredMossy).StudsPerTile);
            Assert.IsTrue(restored.Registry.TryGet(rig.Id, out RbxInstance restoredRig));
            Assert.IsNull(((RbxModel)restoredRig).PrimaryPart);
            Assert.IsTrue(restored.Registry.TryGet(keptModel.Id, out RbxInstance restoredKept));
            Assert.AreEqual(keptPrimary.Id, ((RbxModel)restoredKept).PrimaryPart.Id);
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(brick.Id, out PartProperties restoredBrick));
            Assert.IsNull(restoredBrick.MaterialVariant);
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(modTile.Id, out PartProperties restoredModTile));
            Assert.IsNull(restoredModTile.MaterialVariant);
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(tile.Id, out PartProperties restoredTile));
            Assert.AreEqual("Mossy", restoredTile.MaterialVariant);

            Assert.AreSame(destroyedTarget, refDestroyed.Value, "Only the payload is adjusted.");
            Assert.AreSame(unparentedTarget, refUnparented.Value);
            Assert.AreSame(modTarget, refModOwned.Value);
            Assert.AreEqual(-5d, clicker.MaxActivationDistance);
            Assert.AreEqual(0f, mossy.StudsPerTile);
            Assert.AreSame(loose, rig.PrimaryPart);
            Assert.IsTrue(partSink.TryGetPartProperties(brick.Id, out PartProperties liveBrick));
            Assert.AreEqual("Nope", liveBrick.MaterialVariant);
        }

        [TestCase("object-target", "names missing target")]
        [TestCase("click-distance", "MaxActivationDistance")]
        [TestCase("studs-per-tile", "StudsPerTile")]
        [TestCase("primary-part", "non-descendant PrimaryPart")]
        [TestCase("material-variant", "undefined MaterialVariant")]
        public void ReadPackage_HandCraftedDanglingOrOutOfRangeState_IsRejectedBeforeRestore(
            string mutation,
            string expectedMessage)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxInstance workspace = registry.WorldRoot;
            RbxInstance target = CreateNamed(registry, "Folder", "Target", workspace);
            RbxObjectValue reference = (RbxObjectValue)CreateNamed(registry, "ObjectValue", "Ref", workspace);
            reference.Value = target;
            RbxInstance button = CreateNamedPart(registry, partSink, "Button", workspace, null);
            RbxClickDetector clicker = (RbxClickDetector)CreateNamed(registry, "ClickDetector", "Clicker", button);
            clicker.MaxActivationDistance = 12.5d;
            RbxMaterialVariant mossy = (RbxMaterialVariant)CreateNamed(
                registry, "MaterialVariant", "Mossy", game.FindFirstChildOfClass("MaterialService"));
            mossy.StudsPerTile = 2.5f;
            RbxModel rig = (RbxModel)CreateNamed(registry, "Model", "Rig", workspace);
            RbxInstance head = CreateNamedPart(registry, partSink, "Head", rig, null);
            rig.SetPrimaryPart(head);
            RbxInstance loose = CreateNamedPart(registry, partSink, "Loose", workspace, null);
            CreateNamedPart(registry, partSink, "Tile", workspace, "Mossy");
            byte[] package = RbxWorldPackageSerializer.WritePackage(RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    partSink,
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc)));
            Assert.DoesNotThrow(() => RbxWorldPackageSerializer.ReadPackage(package));
            Assert.AreEqual(0, RbxWorldPackageSerializer.ReadPackage(package).Diagnostics.Count);
            string finiteText;
            string hostileText;
            switch (mutation)
            {
                case "object-target":
                    finiteText = "\"object_target_id\": \"" + target.Id.Value.ToString(CultureInfo.InvariantCulture) + "\"";
                    hostileText = "\"object_target_id\": \"999999999\"";
                    break;
                case "click-distance":
                    finiteText = "\"max_activation_distance\": 12.5";
                    hostileText = "\"max_activation_distance\": -12.5";
                    break;
                case "studs-per-tile":
                    finiteText = "\"studs_per_tile\": 2.5";
                    hostileText = "\"studs_per_tile\": 0.0";
                    break;
                case "primary-part":
                    finiteText = "\"primary_part_id\": \"" + head.Id.Value.ToString(CultureInfo.InvariantCulture) + "\"";
                    hostileText = "\"primary_part_id\": \"" + loose.Id.Value.ToString(CultureInfo.InvariantCulture) + "\"";
                    break;
                default:
                    finiteText = "\"material_variant\": \"Mossy\"";
                    hostileText = "\"material_variant\": \"Nope\"";
                    break;
            }

            byte[] hostile = ReplaceEntryText(
                package, RbxWorldPackageSerializer.WorldEntryName, finiteText, hostileText);

            RbxWorldPackageException exception = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(hostile));

            StringAssert.Contains(expectedMessage, exception.Message);
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
            for (int depth = 1; depth < InstanceTreeSerializer.MaximumSnapshotDepth; depth++)
            {
                RbxInstance child = registry.Create("Folder");
                child.Parent = parent;
                parent = child;
            }

            Assert.DoesNotThrow(() => InstanceTreeSerializer.Capture(root),
                "a live tree at the depth cap must stay capturable");
            RbxInstance tooDeep = registry.Create("Folder");
            Assert.Throws<RbxError>(() => tooDeep.Parent = parent,
                "the live tree refuses the parenting that capture could not serialize");
            Assert.IsNull(tooDeep.Parent);
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
            RbxWorldPackagePayload laterPayload = Capture(source, CapturedAtUtc.AddSeconds(1d));
            CollectionAssert.AreNotEqual(
                original,
                RbxWorldPackageSerializer.WritePackage(laterPayload),
                "The second save must encode differently, or an overwrite would leave identical bytes.");
            RbxWorldPackageWriteResult second = await durableStore.CreateManualAsync("slot-a", laterPayload);

            Assert.IsTrue(first.Success);
            Assert.IsFalse(second.Success);
            Assert.AreEqual(first.Path, second.Path);
            StringAssert.Contains("already exists and cannot be overwritten", second.Error);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(first.Path));
            CollectionAssert.AreEqual(
                new[] { first.Path },
                Directory.GetFiles(Path.GetDirectoryName(first.Path)),
                "A refused create must leave no second file or temporary behind.");

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

            Assert.IsTrue((await store.CreateAutoAsync(
                "z-first", Capture(source, CapturedAtUtc.AddSeconds(1d)))).Success);
            Assert.IsTrue((await store.CreateAutoAsync(
                "a-second", Capture(source, CapturedAtUtc.AddSeconds(2d)))).Success);
            Assert.IsTrue((await store.CreateAutoAsync(
                "m-third", Capture(source, CapturedAtUtc.AddSeconds(3d)))).Success);

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

        /// <summary>
        /// Golden, not a round trip: the writer's manifest.json and world.json are parsed as plain JSON and
        /// compared against literal values derived from the format spec. A codec whose writer and reader
        /// agree on a wrong mapping (ids as JSON numbers, a renamed key, a dropped field) passes every
        /// round-trip test and fails here. Ids start above 2^53, where a double cannot hold them exactly.
        /// </summary>
        [Test]
        public void WritePackage_AuthoredWorld_MatchesLiteralGoldenJson()
        {
            GoldenWorld golden = BuildGoldenWorld();
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    golden.Registry,
                    golden.Game,
                    golden.PartSink,
                    new RbxWorldSettings
                    {
                        WorldId = GoldenWorldId,
                        MetersPerStud = 0.5f,
                        GravityStudsPerSecondSquared = 144.5d,
                        SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
                    },
                    golden.CameraRig,
                    golden.SourceStore,
                    CapturedAtUtc));

            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            CollectionAssert.AreEqual(
                new[]
                {
                    "manifest.json",
                    "world.json",
                    "Mods/0000/manifest.json",
                    "Mods/0000/main.lua",
                    "Mods/0001/manifest.json",
                    "Mods/0001/main.lua"
                },
                ReadEntryNames(package));

            JObject manifest = ParseJsonLiteral(ReadEntryText(package, "manifest.json"));
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "format", "format_version", "minimum_reader_version", "api_version", "created_utc",
                    "world_entry", "mods"
                },
                PropertyNames(manifest));
            Assert.AreEqual("coreai-rbx-world", (string)manifest["format"]);
            Assert.AreEqual(1, (int)manifest["format_version"]);
            Assert.AreEqual(1, (int)manifest["minimum_reader_version"]);
            Assert.AreEqual("MVP2", (string)manifest["api_version"]);
            Assert.AreEqual(JTokenType.String, manifest["created_utc"].Type);
            Assert.AreEqual("2026-09-01T06:07:08.0000000Z", (string)manifest["created_utc"]);
            Assert.AreEqual("world.json", (string)manifest["world_entry"]);
            JArray modIndex = (JArray)manifest["mods"];
            Assert.AreEqual(2, modIndex.Count);
            AssertGoldenModIndex(modIndex[0], "alpha-mod", "Mods/0000/");
            AssertGoldenModIndex(modIndex[1], "beta-mod", "Mods/0001/");

            JObject alphaManifest = ParseJsonLiteral(ReadEntryText(package, "Mods/0000/manifest.json"));
            Assert.AreEqual("alpha-mod", (string)alphaManifest["Id"]);
            Assert.AreEqual("Alpha", (string)alphaManifest["Name"]);
            Assert.IsTrue((bool)alphaManifest["Active"]);
            Assert.AreEqual("return 'alpha'", ReadEntryText(package, "Mods/0000/main.lua"));
            JObject betaManifest = ParseJsonLiteral(ReadEntryText(package, "Mods/0001/manifest.json"));
            Assert.AreEqual("beta-mod", (string)betaManifest["Id"]);
            Assert.AreEqual("Beta", (string)betaManifest["Name"]);
            Assert.IsFalse((bool)betaManifest["Active"]);
            Assert.AreEqual("return 'beta'", ReadEntryText(package, "Mods/0001/main.lua"));

            JObject world = ParseJsonLiteral(ReadEntryText(package, "world.json"));
            CollectionAssert.AreEquivalent(
                new[] { "schema_version", "settings", "camera_cframe", "instances" },
                PropertyNames(world));
            Assert.AreEqual(1, (int)world["schema_version"]);
            JObject settings = (JObject)world["settings"];
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "world_id", "world_acl_version", "meters_per_stud",
                    "gravity_studs_per_second_squared", "signal_behavior"
                },
                PropertyNames(settings));
            Assert.AreEqual(GoldenWorldId, (string)settings["world_id"]);
            Assert.AreEqual(1, (int)settings["world_acl_version"]);
            Assert.AreEqual(0.5d, settings["meters_per_stud"].Value<double>(), 0d);
            Assert.AreEqual(144.5d, settings["gravity_studs_per_second_squared"].Value<double>(), 0d);
            Assert.AreEqual("Deferred", (string)settings["signal_behavior"]);
            AssertJsonNumbers(world["camera_cframe"], 10d, 5d, -4d, 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d);

            JArray instances = (JArray)world["instances"];
            Assert.AreEqual(4, instances.Count);
            AssertGoldenNode(
                instances[0], "9007199254740993", "0", "DataModel", "Game",
                null, null, "HostProtected", "1");
            Assert.AreEqual(JTokenType.Null, instances[0]["model"].Type);
            AssertGoldenNode(
                instances[1], "9007199254740994", "9007199254740993", "Workspace", "Workspace",
                null, null, "HostProtected", "2");
            AssertGoldenModelState(instances[1]["model"], "0", null);
            AssertGoldenNode(
                instances[2], "9007199254740995", "9007199254740994", "Model", "GoldenModel",
                "console:golden-invocation", "actor-golden", "Owned", "8");
            AssertGoldenModelState(
                instances[2]["model"],
                "9007199254740996",
                new[] { 1d, 2d, 3d, 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d });
            CollectionAssert.AreEqual(new[] { "Golden" }, instances[2]["tags"].Values<string>());
            JArray attributes = (JArray)instances[2]["attributes"];
            Assert.AreEqual(2, attributes.Count);
            AssertGoldenAttribute(attributes[0], "Label", "String", "golden", 0d);
            AssertGoldenAttribute(attributes[1], "Weight", "Number", null, 4.5d);
            AssertGoldenNode(
                instances[3], "9007199254740996", "9007199254740995", "Part", "GoldenPart",
                "console:golden-invocation", "actor-golden", "Owned", "2");
            Assert.AreEqual(JTokenType.Null, instances[3]["model"].Type);

            for (int index = 0; index < 3; index++)
            {
                Assert.AreEqual(JTokenType.Null, instances[index]["part"].Type, "Only the Part carries Part state.");
            }

            JObject part = (JObject)instances[3]["part"];
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "shape", "shape_value", "material", "material_value", "material_variant", "cframe",
                    "size", "color", "color_was_explicitly_set", "anchored", "transparency", "can_collide"
                },
                PropertyNames(part));
            Assert.AreEqual("Cylinder", (string)part["shape"]);
            Assert.AreEqual(2, (int)part["shape_value"]);
            Assert.AreEqual("Wood", (string)part["material"]);
            Assert.AreEqual(512, (int)part["material_value"]);
            Assert.AreEqual(JTokenType.Null, part["material_variant"].Type);
            AssertJsonNumbers(part["cframe"], 2d, 3d, -4d, 0d, 0d, 1d, 0d, 1d, 0d, -1d, 0d, 0d);
            AssertJsonNumbers(part["size"], 4d, 1.5d, 2d);
            AssertJsonNumbers(part["color"], 0.25d, 0.5d, 0.75d);
            Assert.IsTrue((bool)part["color_was_explicitly_set"]);
            Assert.IsTrue((bool)part["anchored"]);
            Assert.AreEqual(0.25d, part["transparency"].Value<double>(), 0d);
            Assert.IsFalse((bool)part["can_collide"]);
        }

        /// <summary>
        /// Golden reader: a hand-written package (no writer involved) restores literal ids, parents,
        /// revisions and Part state. Every id is an odd number above 2^53, which a double would round.
        /// </summary>
        [Test]
        public void ReadPackage_HandWrittenLiteralPackage_RestoresLiteralIdsParentsRevisionsAndPartState()
        {
            byte[] package = BuildLiteralPackage(
                ("manifest.json", HandWrittenManifestJson),
                ("world.json", HandWrittenWorldJson),
                ("Mods/0000/manifest.json", HandWrittenModManifestJson),
                ("Mods/0000/main.lua", "return 42"));

            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(package);

            Assert.AreEqual(new DateTime(2026, 9, 1, 6, 7, 8, DateTimeKind.Utc), decoded.CapturedAtUtc);
            Assert.AreEqual(DateTimeKind.Utc, decoded.CapturedAtUtc.Kind);
            Assert.AreEqual("mvp3-handwritten", decoded.Settings.WorldId);
            Assert.AreEqual(0.5f, decoded.Settings.MetersPerStud);
            Assert.AreEqual(144.5d, decoded.Settings.GravityStudsPerSecondSquared);
            Assert.IsFalse(decoded.CameraCFrame.HasValue);

            float appliedScale = 1f;
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                decoded,
                new RbxWorldPackageRestoreOptions
                {
                    BeginMetersPerStudRestore = metersPerStud =>
                    {
                        float previousScale = appliedScale;
                        appliedScale = metersPerStud;
                        return () => appliedScale = previousScale;
                    }
                });
            _games.Add(restored.Game);
            InstanceRegistry registry = restored.Registry;

            Assert.AreEqual(0.5f, appliedScale);
            Assert.AreEqual("mvp3-handwritten", registry.WorldId);
            Assert.AreEqual(InstanceRegistry.CurrentWorldAclVersion, registry.WorldAclVersion);
            Assert.AreEqual(4, registry.Count);
            Assert.AreEqual(9007199254740993UL, restored.Game.Id.Value);
            Assert.AreEqual("Game", restored.Game.Name);
            Assert.IsNull(restored.Game.Parent);
            Assert.AreEqual(9007199254740995UL, registry.WorldRoot.Id.Value);
            AssertLiteralRecord(
                registry, 9007199254740993UL, "DataModel", 0UL, 3L,
                null, null, InstanceAccessScope.HostProtected);
            AssertLiteralRecord(
                registry, 9007199254740995UL, "Workspace", 9007199254740993UL, 5L,
                null, null, InstanceAccessScope.HostProtected);
            RbxModel model = (RbxModel)AssertLiteralRecord(
                registry, 9007199254740997UL, "Model", 9007199254740995UL, 9007199254740999L,
                "console:hand-written", "actor-hand", InstanceAccessScope.Owned);
            RbxInstance part = AssertLiteralRecord(
                registry, 9007199254741001UL, "Part", 9007199254740997UL, 7L,
                "console:hand-written", "actor-hand", InstanceAccessScope.Owned);

            Assert.AreEqual("HandModel", model.Name);
            Assert.IsTrue(model.Archivable);
            Assert.AreEqual("hand", model.GetAttribute("Label"));
            Assert.IsTrue(model.HasTag("Golden"));
            Assert.AreEqual(9007199254741001UL, model.PrimaryPart.Id.Value);
            Assert.IsTrue(model.HasStoredWorldPivot);
            CollectionAssert.AreEqual(
                new[] { 1f, 2f, 3f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f },
                model.StoredWorldPivot.GetComponents());
            Assert.AreEqual("HandPart", part.Name);
            Assert.IsFalse(part.Archivable);

            Assert.IsTrue(restored.PartSink.TryGetPartProperties(part.Id, out PartProperties properties));
            Assert.AreEqual(RbxPartShape.Cylinder, properties.Shape);
            Assert.AreEqual("Wood", properties.Material.Name);
            Assert.AreEqual(512, properties.Material.Value);
            Assert.IsNull(properties.MaterialVariant);
            CollectionAssert.AreEqual(
                new[] { 2f, 3f, -4f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f },
                properties.CFrame.GetComponents());
            Assert.AreEqual(new RbxVector3(4f, 1.5f, 2f), properties.Size);
            Assert.AreEqual(new RbxColor3(0.25f, 0.5f, 0.75f), properties.Color);
            Assert.IsTrue(properties.ColorWasExplicitlySet);
            Assert.IsTrue(properties.Anchored);
            Assert.AreEqual(0.25f, properties.Transparency);
            Assert.IsFalse(properties.CanCollide);

            Assert.AreEqual(1, restored.Mods.Count);
            Assert.AreEqual("golden-mod", restored.Mods[0].Manifest.Id);
            Assert.AreEqual("Golden Mod", restored.Mods[0].Manifest.Name);
            Assert.IsFalse(restored.Mods[0].Manifest.Active);
            Assert.AreEqual("return 42", restored.Mods[0].Source);

            Assert.AreEqual(
                9007199254741002UL,
                registry.Create("Folder").Id.Value,
                "The allocator must continue past the highest restored id, never reuse one.");
        }

        /// <summary>
        /// Pins the production durability hook. Off WebGL every hook answers true, so a default replaced by
        /// <c>_ =&gt; true</c> passes every behavioural test; only the hook's own identity can tell them apart.
        /// </summary>
        [Test]
        public void FileStores_WithoutInjectedHook_DefaultToCoreAiWebGlPersistenceSyncAsync()
        {
            MethodInfo syncAsync = typeof(CoreAI.Infrastructure.CoreAiWebGlPersistence).GetMethod(
                nameof(CoreAI.Infrastructure.CoreAiWebGlPersistence.SyncAsync),
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(syncAsync);
            string root = NewTemporaryDirectory();

            FileRbxWorldPackageStore defaultPackageStore = new(root);
            FileRbxWorldPackageStore injectedPackageStore = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));
            using CoreAI.Infrastructure.Lua.FileLuaModSourceStore defaultSourceStore =
                new(Path.Combine(root, "DefaultMods"));
            using CoreAI.Infrastructure.Lua.FileLuaModSourceStore injectedSourceStore = new(
                Path.Combine(root, "InjectedMods"),
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true));

            Assert.IsTrue(
                ForwardsTo(defaultPackageStore.PersistenceSyncForTests, syncAsync),
                "FileRbxWorldPackageStore must default to CoreAiWebGlPersistence.SyncAsync.");
            Assert.IsTrue(
                ForwardsTo(defaultSourceStore.PersistenceSyncForTests, syncAsync),
                "FileLuaModSourceStore must default to CoreAiWebGlPersistence.SyncAsync.");
            Assert.IsFalse(
                ForwardsTo(injectedPackageStore.PersistenceSyncForTests, syncAsync),
                "An injected hook is used as given; the check must be able to fail.");
            Assert.IsFalse(
                ForwardsTo(injectedSourceStore.PersistenceSyncForTests, syncAsync),
                "An injected hook is used as given; the check must be able to fail.");
        }

        [Test]
        public async Task FileStore_DefaultAutosaveCapacity_IsTenAndRotatesOnlyTheOldest()
        {
            Assert.AreEqual(10, FileRbxWorldPackageStore.DefaultAutoBackupCapacity);
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);

            for (int index = 0; index < 10; index++)
            {
                RbxWorldPackageWriteResult result = await store.CreateAutoAsync(
                    "ring-" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                    payload);
                Assert.IsTrue(result.Success, result.Error);
            }

            Assert.AreEqual(10, store.ListAutoFiles().Count, "Ten autosaves fit before any rotation.");

            RbxWorldPackageWriteResult eleventh = await store.CreateAutoAsync("ring-10", payload);

            Assert.IsTrue(eleventh.Success, eleventh.Error);
            IReadOnlyList<string> files = store.ListAutoFiles();
            Assert.AreEqual(10, files.Count);
            Assert.AreEqual("20260901T060708000Z-0001-ring-01.world", files[0]);
            Assert.AreEqual("20260901T060708000Z-0010-ring-10.world", files[9]);
        }

        [Test]
        public async Task ConfirmedBackup_GatedExecuteLua_WritesExactlyOneExecuteLuaAutosaveToFileStore()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
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
            Assert.AreEqual(0, store.ListAutoSaves().Count);

            LuaTool.LuaResult result = await executor.ExecuteAsync(
                "mutate_world()",
                CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("new-tree", bindings.TreeState);
            IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
            Assert.AreEqual(1, autosaves.Count);
            Assert.AreEqual("execute_lua", autosaves[0].Trigger);
            Assert.AreEqual(LuaCsGameToolExecutor.ExecuteLuaBackupTrigger, autosaves[0].Trigger);
            Assert.AreEqual("20260901T060708000Z-0000-execute_lua.world", autosaves[0].FileName);
            Assert.AreEqual(CapturedAtUtc, autosaves[0].TimestampUtc);
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(payload),
                File.ReadAllBytes(Path.Combine(root, "Auto", autosaves[0].FileName)),
                "The autosave must hold exactly the pre-mutation capture.");
            Assert.AreEqual(0, store.ListManualSlots().Count);
        }

        [Test]
        public async Task ConfirmedBackup_GatedExecuteLua_UnconfirmedFileStoreBackupLeavesNoAutosaveAndNoMutation()
        {
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(false),
                utcNow: () => CapturedAtUtc);
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
            StringAssert.Contains("Confirmed pre-mutation backup 'execute_lua' failed", result.Error);
            StringAssert.Contains("durable persistence was not confirmed", result.Error);
            Assert.AreEqual("old-tree", bindings.TreeState);
            Assert.AreEqual(0, store.ListAutoSaves().Count);
            Assert.AreEqual(0, store.ListManualSlots().Count);
        }

        [Test]
        public async Task ConfirmedBackup_GatedExecuteLuaAfterModWroteNaN_StillWritesAutosaveAndRuns()
        {
            RuntimeWorld world = new(WorldId);
            _games.Add(world.Game);
            RbxNumberValue score = (RbxNumberValue)world.Registry.Create(
                "NumberValue",
                originTag: OriginTag.FromConsole("non-finite-fixture"));
            score.Name = "Score";
            score.Parent = world.Registry.WorldRoot;
            world.Stack.Runtime.LoadMod(
                "nan-writer",
                @"local score = workspace:FindFirstChild('Score')
                score.Value = 0/0
                score:SetAttribute('Ratio', math.huge)",
                LuaCapabilities.All);
            Assert.IsTrue(double.IsNaN(score.Value), "The mod must have written NaN through Lua.");
            Assert.IsTrue(double.IsPositiveInfinity((double)score.GetAttribute("Ratio")));
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(Capture(world, CapturedAtUtc)),
                store);
            RecordingLuaCsBindings bindings = new();
            LuaCsGameToolExecutor executor = new(
                new LuaCsSecureEnvironment(),
                bindings,
                new NullLuaExecutionObserver(),
                null,
                gate);

            LuaTool.LuaResult first = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);
            LuaTool.LuaResult second = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(first.Success, "One NaN written by a mod must not refuse execute_lua: " + first.Error);
            Assert.IsTrue(second.Success, "The next gated execute_lua must not be refused either: " + second.Error);
            Assert.AreEqual("new-tree", bindings.TreeState);
            Assert.AreEqual(19, bindings.Revision, "Both gated mutations must have run.");
            IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
            Assert.AreEqual(2, autosaves.Count);
            foreach (RbxAutoSaveInfo autosave in autosaves)
            {
                Assert.AreEqual(LuaCsGameToolExecutor.ExecuteLuaBackupTrigger, autosave.Trigger);
                RbxWorldPackagePayload saved = RbxWorldPackageSerializer.ReadPackage(
                    File.ReadAllBytes(Path.Combine(root, "Auto", autosave.FileName)));
                InstanceSnapshot savedScore = FindNode(saved, "Score");
                Assert.AreEqual("0", savedScore.Value.StringValue);
                Assert.AreEqual(0, savedScore.Attributes.Count);
                AssertOnlyNonFiniteDiagnostics(
                    saved,
                    score.Id.Value + ":Value",
                    score.Id.Value + ":Attributes.Ratio");
            }

            Assert.IsTrue(double.IsNaN(score.Value), "The live world keeps the mod's NaN.");
            Assert.IsTrue(double.IsPositiveInfinity((double)score.GetAttribute("Ratio")));
            Assert.AreEqual(0, store.ListManualSlots().Count);
        }

        [TestCase(
            "local reference = workspace:FindFirstChild('Ref') local target = workspace:FindFirstChild('Target') "
            + "reference.Value = target target:Destroy()",
            "Ref", null, "missing", "Value")]
        [TestCase(
            "local folder = Instance.new('Folder') folder.Parent = workspace "
            + "workspace:FindFirstChild('Ref').Value = folder",
            "Ref", null, "mod-ephemeral", "Value")]
        [TestCase(
            "workspace:FindFirstChild('Button'):FindFirstChild('Clicker').MaxActivationDistance = -5",
            "Clicker", null, "out-of-range", "MaxActivationDistance")]
        [TestCase(
            "game:GetService('MaterialService'):FindFirstChild('Mossy').StudsPerTile = 0",
            "Mossy", null, "out-of-range", "StudsPerTile")]
        [TestCase(
            "workspace:FindFirstChild('Rig').PrimaryPart = workspace:FindFirstChild('Loose')",
            "Rig", "Loose", "not-descendant", null)]
        [TestCase(
            "workspace:FindFirstChild('Brick').MaterialVariant = 'Nope'",
            "Brick", null, "missing", "MaterialVariant")]
        public async Task ConfirmedBackup_GatedExecuteLuaAfterModLeftFormatInvalidState_StillAutosavesAndRuns(
            string modSource,
            string instanceName,
            string droppedName,
            string reason,
            string member)
        {
            RuntimeWorld world = new(WorldId);
            _games.Add(world.Game);
            InstanceRegistry registry = world.Registry;
            RbxInstance workspace = registry.WorldRoot;
            RbxInstance target = CreateNamed(registry, "Folder", "Target", workspace);
            RbxObjectValue reference = (RbxObjectValue)CreateNamed(registry, "ObjectValue", "Ref", workspace);
            reference.Value = target;
            RbxInstance button = CreateNamedPart(registry, world.PartSink, "Button", workspace, null);
            CreateNamed(registry, "ClickDetector", "Clicker", button);
            CreateNamed(registry, "MaterialVariant", "Mossy", world.Game.FindFirstChildOfClass("MaterialService"));
            RbxModel rig = (RbxModel)CreateNamed(registry, "Model", "Rig", workspace);
            RbxInstance head = CreateNamedPart(registry, world.PartSink, "Head", rig, null);
            rig.SetPrimaryPart(head);
            CreateNamedPart(registry, world.PartSink, "Loose", workspace, null);
            CreateNamedPart(registry, world.PartSink, "Brick", workspace, null);
            RbxInstance instance = world.Game.FindFirstChild(instanceName, true);
            Assert.IsNotNull(instance);
            string expected = Diagnostic(
                instance,
                droppedName == null ? null : world.Game.FindFirstChild(droppedName, true),
                reason,
                member);
            world.Stack.Runtime.LoadMod("format-breaker", modSource, LuaCapabilities.All);
            string root = NewTemporaryDirectory();
            FileRbxWorldPackageStore store = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc);
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(Capture(world, CapturedAtUtc)),
                store);
            RecordingLuaCsBindings bindings = new();
            LuaCsGameToolExecutor executor = new(
                new LuaCsSecureEnvironment(),
                bindings,
                new NullLuaExecutionObserver(),
                null,
                gate);

            LuaTool.LuaResult result = await executor.ExecuteAsync("mutate_world()", CancellationToken.None);

            Assert.IsTrue(result.Success,
                "One format-invalid write by a mod must not refuse execute_lua: " + result.Error);
            Assert.AreEqual("new-tree", bindings.TreeState);
            IReadOnlyList<RbxAutoSaveInfo> autosaves = store.ListAutoSaves();
            Assert.AreEqual(1, autosaves.Count);
            Assert.AreEqual(LuaCsGameToolExecutor.ExecuteLuaBackupTrigger, autosaves[0].Trigger);
            RbxWorldPackagePayload saved = RbxWorldPackageSerializer.ReadPackage(
                File.ReadAllBytes(Path.Combine(root, "Auto", autosaves[0].FileName)));
            AssertDiagnostics(saved, expected);
        }

        // ==================== R1 audit A1: format limits, load serialization, request-time refusals ====================

        private const LuaCapabilities SessionCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        private const string ValidAutoName = "20260902T120000000Z-0000-execute_lua.world";

        /// <summary>
        /// A1-01: an unloaded mod keeps its source, so 256 load/unload cycles fill the world-package mod
        /// limit. The 257th distinct mod used to load and persist; from then on every capture threw, so
        /// every gated mutation and every save was refused, across restarts. It is now refused before it
        /// runs, with the way out named, and the world stays capturable.
        /// </summary>
        [Test]
        public async Task ModSourceLimit_DistinctModBeyondTheFormatLimit_IsRefusedWithTheWayOut_AndTheWorldStaysCapturable()
        {
            MemorySourceStore sources = new();
            ScriptedWorldPackageStore packages = new();
            using GatedHeadlessSession session = new(packages, sources);
            LocalActorIdentityProvider identity = new("limit-host");
            ActorContext host = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            for (int index = 0; index < RbxWorldPackageSerializer.MaximumMods; index++)
            {
                string id = "mod" + index.ToString("D3", CultureInfo.InvariantCulture);
                session.Controller.Runtime.LoadMod(host, id, "local value = " + index, SessionCapabilities);
                Assert.IsTrue(session.Controller.Runtime.UnloadMod(host, id));
            }

            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, sources.List().Count,
                "precondition: every unloaded mod kept its source");

            RbxWorldPackageFormatLimitException refused = Assert.Throws<RbxWorldPackageFormatLimitException>(
                () => session.Controller.Runtime.LoadMod(host, "one-too-many", "local value = 1", SessionCapabilities));
            StringAssert.Contains("'forget'", refused.Message);
            Assert.IsFalse(session.Controller.Runtime.IsLoaded(host, "one-too-many"));

            LuaModsLlmTool tool = new(
                session.Controller.Runtime,
                new TestCoreAiSettings(),
                NullLog.Instance,
                SessionCapabilities,
                true,
                identity,
                BuiltInAgentRoleIds.Programmer,
                session.Gate);
            JObject refusedByTool = JObject.Parse(await tool.ExecuteAsync("load", "one-too-many", "local value = 1"));

            Assert.IsFalse(refusedByTool.Value<bool>("success"), refusedByTool.ToString());
            StringAssert.Contains("'forget'", refusedByTool.Value<string>("message"));
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, sources.List().Count);
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, session.Controller.CaptureCurrent().Mods.Count,
                "the world stays capturable, so saves and gated mutations keep working");

            session.Controller.Runtime.LoadMod(host, "mod000", "local value = 'again'", SessionCapabilities);
            Assert.IsTrue(session.Controller.Runtime.IsLoaded(host, "mod000"),
                "a mod whose source is already stored is never refused");

            JObject forgotten = JObject.Parse(await tool.ExecuteAsync("forget", "mod001"));
            JObject accepted = JObject.Parse(await tool.ExecuteAsync("load", "one-too-many", "local value = 1"));

            Assert.IsTrue(forgotten.Value<bool>("success"), forgotten.ToString());
            Assert.IsTrue(accepted.Value<bool>("success"), accepted.ToString());
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, sources.List().Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    LuaModsLlmTool.LoadBackupTrigger,
                    LuaModsLlmTool.ForgetBackupTrigger,
                    LuaModsLlmTool.LoadBackupTrigger
                },
                packages.AutoTriggers);
        }

        /// <summary>
        /// A1-01: a world already past the mod limit (saved before the limit was enforced) cannot be
        /// captured, and the gate used to refuse every mutation, including the forget that is the only
        /// way back under the limit. Forget now runs without its impossible backup; every other
        /// mutation stays refused until the world fits again.
        /// B2-13: unload also skipped its backup, although an unloaded mod keeps its source, so an
        /// unload never brings the world back under the limit; it is refused like the other mutations.
        /// </summary>
        [Test]
        public async Task ConfirmedBackup_WorldPastTheModLimit_RunsOnlyForgetWithoutBackup_AndRefusesOtherMutations()
        {
            MemorySourceStore sources = new();
            for (int index = 0; index <= RbxWorldPackageSerializer.MaximumMods; index++)
            {
                string id = "stored-" + index.ToString("D3", CultureInfo.InvariantCulture);
                sources.Save(id, "local value = " + index, new LuaModManifest
                {
                    Id = id,
                    Name = id,
                    OwnerActorId = "limit-actor",
                    Capabilities = LuaCapabilities.Read.ToString(),
                    Active = false
                });
            }

            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            List<RbxWorldPackagePayload> backups = new();
            DelegateWorldPackageStore store = new((trigger, captured, cancellationToken) =>
            {
                backups.Add(captured);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, trigger + ".world", ""));
            });
            ConfirmedWorldMutationGate gate = new(
                cancellationToken => UniTask.FromResult(RbxWorldPackageSerializer.Capture(
                    new RbxWorldPackageCaptureContext(
                        registry,
                        game,
                        new InMemoryPartPropertySink(),
                        NewSettings(),
                        null,
                        sources,
                        CapturedAtUtc))),
                store);
            LuaCsModRuntime runtime = new(sourceStore: sources, versionStore: new MemoryLuaScriptVersionStore());
            LocalActorIdentityProvider identity = new("limit-actor");
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            runtime.LoadMod(actor, "stored-000", "local value = 0", LuaCapabilities.All, persistToStore: false);
            LuaModsLlmTool tool = CreateWorldGatedModsTool(runtime, identity, new TestCoreAiSettings(), gate);

            JObject load = JObject.Parse(await tool.ExecuteAsync("load", "new-mod", "local value = 2"));
            JObject unload = JObject.Parse(await tool.ExecuteAsync("unload", "stored-000"));
            JObject forget = JObject.Parse(await tool.ExecuteAsync("forget", "stored-001"));

            Assert.IsFalse(load.Value<bool>("success"), load.ToString());
            StringAssert.Contains("'forget'", load.Value<string>("message"));
            Assert.IsFalse(runtime.IsLoaded(actor, "new-mod"));
            Assert.IsFalse(unload.Value<bool>("success"), "an unload keeps the source, so it stays refused: " + unload);
            Assert.IsTrue(runtime.IsLoaded(actor, "stored-000"), "the refused unload left the mod running");
            Assert.IsTrue(forget.Value<bool>("success"), forget.ToString());
            Assert.IsFalse(sources.TryLoad("stored-001", out _, out _));
            CollectionAssert.IsEmpty(store.AutoTriggers, "a world past the limit cannot be captured, so no backup exists");

            JObject unloadAfterForget = JObject.Parse(await tool.ExecuteAsync("unload", "stored-000"));
            JObject afterForget = JObject.Parse(await tool.ExecuteAsync("load", "new-mod", "local value = 2"));

            Assert.IsTrue(unloadAfterForget.Value<bool>("success"), unloadAfterForget.ToString());
            Assert.IsFalse(runtime.IsLoaded(actor, "stored-000"));
            Assert.IsTrue(afterForget.Value<bool>("success"), afterForget.ToString());
            CollectionAssert.AreEqual(
                new[] { LuaModsLlmTool.UnloadBackupTrigger, LuaModsLlmTool.LoadBackupTrigger },
                store.AutoTriggers,
                "back under the limit, every mutation is backed up again");
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods, backups[0].Mods.Count);
        }

        /// <summary>
        /// A1-04: the live-network rule was checked once, before the multi-frame safety autosave and
        /// staging, so a client that joined meanwhile had its world replaced under it. It is checked
        /// again right before publication, and the refused load rolls back like any staging failure.
        /// </summary>
        [Test]
        public async Task WorldLoad_ClientJoiningDuringTheSafetyAutosave_IsRefusedBeforePublish_AndRollsBack()
        {
            ScriptedWorldPackageStore packages = new() { HoldNextWrite = true };
            RecordingTransactionalSourceStore sources = new();
            JoinableNetworkBridge bridge = new();
            using GatedHeadlessSession session = new(packages, sources, bridge);
            InstanceRegistry live = session.Controller.CurrentRbxApi.Registry;

            UniTask<RbxWorldLoadResult> loading = session.Controller.LoadConfirmedAsync(
                CreateMinimalPayload(CapturedAtUtc));
            CollectionAssert.AreEqual(new[] { "load_world-pre" }, packages.AutoTriggers,
                "precondition: the safety autosave is still in flight");
            bridge.RegisterActor("remote-client");
            packages.ReleaseHeldWrite(true);
            RbxWorldLoadResult refused = await loading;

            Assert.IsFalse(refused.Success, "a client joined before publication");
            Assert.AreEqual(RbxWorldLoadRefusedException.NetworkSessionsActiveStatus, refused.Status);
            StringAssert.Contains("MVP11 session handoff", refused.Error);
            Assert.AreSame(live, session.Controller.CurrentRbxApi.Registry, "the live world was not replaced");
            Assert.IsFalse(live.IsDetached);
            Assert.AreEqual(1, sources.Prepared);
            Assert.AreEqual(1, sources.RolledBack, "the staged source version was rolled back");
            Assert.AreEqual(0, sources.Completed);

            bridge.UnregisterActor("remote-client");
            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(CreateMinimalPayload(CapturedAtUtc));

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual("", loaded.Status);
            Assert.AreNotSame(live, session.Controller.CurrentRbxApi.Registry);
            Assert.AreEqual(1, sources.Completed);
        }

        /// <summary>
        /// A1-07: the load captured its safety autosave outside the shared gate, so an execute_lua that
        /// was waiting for its own autosave changed the outgoing world after that capture: the change
        /// was in neither the safety autosave nor the loaded world. The load now waits for the gate.
        /// </summary>
        [Test]
        public async Task WorldLoad_SafetyAutosaveWaitsForTheSharedGate_SoAnExecuteLuaInFlightIsInTheBackup()
        {
            ScriptedWorldPackageStore packages = new() { HoldNextWrite = true };
            using GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());

            Task<LuaTool.LuaResult> executing = session.Controller.Executor.ExecuteAsync(
                "local work = Instance.new('Folder') work.Name = 'AiWork' work.Parent = workspace return true",
                CancellationToken.None);
            CollectionAssert.AreEqual(new[] { LuaCsGameToolExecutor.ExecuteLuaBackupTrigger }, packages.AutoTriggers,
                "precondition: execute_lua holds the gate while its autosave is in flight");
            UniTask<RbxWorldLoadResult> loading = session.Controller.LoadConfirmedAsync(
                CreateMinimalPayload(CapturedAtUtc));
            CollectionAssert.AreEqual(new[] { LuaCsGameToolExecutor.ExecuteLuaBackupTrigger }, packages.AutoTriggers,
                "the load must not capture the world while a gated mutation is in flight");

            packages.ReleaseHeldWrite(true);
            LuaTool.LuaResult executed = await executing;
            RbxWorldLoadResult loaded = await loading;

            Assert.IsTrue(executed.Success, executed.Error);
            Assert.IsTrue(loaded.Success, loaded.Error);
            CollectionAssert.AreEqual(
                new[] { LuaCsGameToolExecutor.ExecuteLuaBackupTrigger, "load_world-pre" },
                packages.AutoTriggers);
            Assert.IsNull(FindNodeOrNull(packages.AutoPayloads[0], "AiWork"));
            Assert.IsNotNull(FindNodeOrNull(packages.AutoPayloads[1], "AiWork"),
                "the safety autosave holds the change execute_lua made before the load");
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("AiWork"));
        }

        /// <summary>
        /// A1-14: an execute_lua that waited behind a confirmed load ran against the outgoing world and
        /// failed with WORLD_DETACHED, whose text blames a destroyed scene host and says to reload the
        /// mods. It now says what happened: a world load replaced the world, nothing reached it.
        /// </summary>
        [Test]
        public async Task ExecuteLua_WhoseWorldAConfirmedLoadReplacedWhileItWaited_ReportsTheLoad()
        {
            ScriptedWorldPackageStore packages = new() { HoldNextWrite = true };
            using GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());

            UniTask<RbxWorldLoadResult> loading = session.Controller.LoadConfirmedAsync(
                CreateMinimalPayload(CapturedAtUtc));
            Task<LuaTool.LuaResult> executing = session.Controller.Executor.ExecuteAsync(
                "local work = Instance.new('Folder') work.Name = 'LateWork' work.Parent = workspace return true",
                CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "load_world-pre" }, packages.AutoTriggers,
                "precondition: execute_lua waits behind the load");

            packages.ReleaseHeldWrite(true);
            RbxWorldLoadResult loaded = await loading;
            LuaTool.LuaResult executed = await executing;

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.IsFalse(executed.Success, executed.Output);
            StringAssert.Contains("a confirmed world load replaced the live world", executed.Error);
            StringAssert.DoesNotContain("RbxWorldHost", executed.Error);
            StringAssert.DoesNotContain("reload the mods so", executed.Error);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("LateWork"));

            LuaTool.LuaResult ordinaryFailure = await session.Controller.Executor.ExecuteAsync(
                "error('plain failure')",
                CancellationToken.None);

            Assert.IsFalse(ordinaryFailure.Success);
            StringAssert.Contains("plain failure", ordinaryFailure.Error,
                "a chunk whose world was not replaced keeps its own error");
        }

        /// <summary>
        /// B2-06: every failure of an execute_lua that waited behind a confirmed load was replaced by
        /// the replaced-world explanation, the chunk's own syntax error included, so the model retried
        /// the same broken chunk before it saw the real error. Only a failure the replaced world caused
        /// is replaced; any other keeps its own error, followed by the explanation.
        /// </summary>
        [Test]
        public async Task ExecuteLua_QueuedBehindAConfirmedLoad_KeepsItsOwnErrorAndExplainsTheReplacedWorld()
        {
            ScriptedWorldPackageStore packages = new() { HoldNextWrite = true };
            using GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());

            UniTask<RbxWorldLoadResult> loading = session.Controller.LoadConfirmedAsync(
                CreateMinimalPayload(CapturedAtUtc));
            Task<LuaTool.LuaResult> executing = session.Controller.Executor.ExecuteAsync(
                "local broken = = 1",
                CancellationToken.None);
            packages.ReleaseHeldWrite(true);
            RbxWorldLoadResult loaded = await loading;
            LuaTool.LuaResult queued = await executing;
            LuaTool.LuaResult direct = await session.Controller.Executor.ExecuteAsync(
                "local broken = = 1",
                CancellationToken.None);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.IsFalse(direct.Success, "precondition: the chunk does not compile");
            Assert.IsFalse(queued.Success, queued.Output);
            StringAssert.Contains(direct.Error, queued.Error, "the chunk's own error reaches the caller");
            StringAssert.Contains("a confirmed world load replaced the live world", queued.Error,
                "and so does why the chunk ran against the previous world");
        }

        /// <summary>
        /// B2-10: the boot-time startup restore published outside the shared gate, so a manage_mods
        /// call that arrived during boot backed up the default world, then changed the restored one
        /// once the facade resolved it, and the change missed the startup selection. The restore now
        /// holds the gate: the call waits, is backed up from the restored world, changes it, and its
        /// change is recorded for the next start.
        /// </summary>
        [Test]
        public async Task StartupRestore_HoldsTheSharedGate_SoAModChangeArrivingDuringBootLandsOnTheRestoredWorld()
        {
            RbxWorldPackagePayload selected = CreatePayloadWithFolder("RestoredWorldMarker");
            ScriptedStartupWorldPackageStore packages = new(selected) { HoldNextStartupRead = true };
            using GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());
            InstanceRegistry bootWorld = session.Controller.CurrentRbxApi.Registry;
            LocalActorIdentityProvider identity = new("boot-actor");
            LuaModsLlmTool tool = new(
                session.Controller.Runtime,
                new TestCoreAiSettings(),
                NullLog.Instance,
                SessionCapabilities,
                true,
                identity,
                BuiltInAgentRoleIds.Programmer,
                session.Gate);

            UniTask<RbxWorldStartupRestoreResult> restoring = session.Controller.RestoreStartupSelectionAsync();
            Task<string> loading = tool.ExecuteAsync("load", "bootmod", "local value = 1");
            CollectionAssert.IsEmpty(packages.AutoTriggers,
                "the mod change must not back up or change a world while the boot restore holds the gate");

            packages.ReleaseHeldStartupRead();
            RbxWorldStartupRestoreResult restored = await restoring;
            JObject loaded = JObject.Parse(await loading);

            Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
            Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            Assert.AreNotSame(bootWorld, session.Controller.CurrentRbxApi.Registry);
            Assert.IsTrue(session.Controller.Runtime.IsLoaded(
                identity.GetActorContext(BuiltInAgentRoleIds.Programmer), "bootmod"));
            CollectionAssert.AreEqual(new[] { LuaModsLlmTool.LoadBackupTrigger }, packages.AutoTriggers);
            Assert.IsNotNull(FindNodeOrNull(packages.AutoPayloads[0], "RestoredWorldMarker"),
                "the backup is of the restored world the change went to");
            Assert.AreEqual(1, packages.SelectedPayloads.Count,
                "the change to the restored startup world is recorded for the next start");
            Assert.IsNotNull(FindNodeOrNull(packages.SelectedPayloads[0], "RestoredWorldMarker"));
            CollectionAssert.AreEqual(
                new[] { "bootmod" },
                ModIdsOf(packages.SelectedPayloads[0]));
            CollectionAssert.IsEmpty(session.Diagnostics);
        }

        /// <summary>
        /// B2-10 twin: an execute_lua that arrived during boot waits for the restore like one that
        /// waited behind a confirmed load, and says it ran against the previous world instead of
        /// changing the default world that the restore then discarded without a word.
        /// </summary>
        [Test]
        public async Task StartupRestore_ExecuteLuaArrivingDuringBoot_WaitsAndReportsTheReplacedWorld()
        {
            RbxWorldPackagePayload selected = CreatePayloadWithFolder("RestoredWorldMarker");
            ScriptedStartupWorldPackageStore packages = new(selected) { HoldNextStartupRead = true };
            using GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());

            UniTask<RbxWorldStartupRestoreResult> restoring = session.Controller.RestoreStartupSelectionAsync();
            Task<LuaTool.LuaResult> executing = session.Controller.Executor.ExecuteAsync(
                "local work = Instance.new('Folder') work.Name = 'BootWork' work.Parent = workspace return true",
                CancellationToken.None);
            CollectionAssert.IsEmpty(packages.AutoTriggers, "the chunk waits for the boot restore");

            packages.ReleaseHeldStartupRead();
            RbxWorldStartupRestoreResult restored = await restoring;
            LuaTool.LuaResult executed = await executing;

            Assert.AreEqual(RbxWorldStartupRestoreOutcome.Restored, restored.Outcome, restored.Error);
            Assert.IsFalse(executed.Success, executed.Output);
            StringAssert.Contains("a confirmed world load replaced the live world", executed.Error);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("BootWork"));
        }

        /// <summary>
        /// B2-07: the session counted the manifests the store could list while the file store counted
        /// the folders holding a manifest file. With one unreadable manifest the session admitted a new
        /// mod that the store then refused to keep, silently: the mod ran and was missing from every
        /// save. Both now ask the same rule, and the session refuses with the store's own refusal.
        /// </summary>
        [Test]
        public void ModSourceLimit_UnreadableManifest_TheSessionRefusesWithTheStoresOwnRefusal()
        {
            string root = NewTemporaryDirectory();
            CoreAI.Infrastructure.Lua.FileLuaModSourceStore sources = new(
                root, persistenceSyncAsync: _ => UniTask.FromResult(true));
            for (int index = 0; index < RbxWorldPackageSerializer.MaximumMods - 1; index++)
            {
                string id = "mod" + index.ToString("D3", CultureInfo.InvariantCulture);
                sources.Save(id, "local value = " + index, new LuaModManifest
                {
                    Id = id,
                    Name = id,
                    Capabilities = SessionCapabilities.ToString(),
                    Active = false
                });
            }

            Directory.CreateDirectory(Path.Combine(root, "broken"));
            File.WriteAllText(Path.Combine(root, "broken", "manifest.json"), "{ not json");
            File.WriteAllText(Path.Combine(root, "broken", "main.lua"), "local value = 0");
            Assert.AreEqual(RbxWorldPackageSerializer.MaximumMods - 1, sources.List().Count,
                "precondition: the unreadable manifest is not listed");
            RbxWorldPackageFormatLimitException storeRefusal = Assert.Throws<RbxWorldPackageFormatLimitException>(
                () => sources.Save("probe", "local value = 1", new LuaModManifest { Id = "probe", Name = "probe" }),
                "precondition: the store refuses a new source");

            using GatedHeadlessSession session = new(new ScriptedWorldPackageStore(), sources);
            ActorContext host = new LocalActorIdentityProvider("limit-host")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            RbxWorldPackageFormatLimitException refused = Assert.Throws<RbxWorldPackageFormatLimitException>(
                () => session.Controller.Runtime.LoadMod(host, "probe", "local value = 1", SessionCapabilities));

            Assert.AreEqual(storeRefusal.Message, refused.Message, "one rule, one refusal");
            StringAssert.Contains("'forget'", refused.Message);
            Assert.IsFalse(session.Controller.Runtime.IsLoaded(host, "probe"), "the refused mod never ran");
            Assert.IsFalse(sources.TryLoad("probe", out _, out _));
        }

        /// <summary>
        /// B2-07: the runtime persists best-effort and only logs a refused save, so a mod whose store
        /// did not keep its source ran on, missing from every save and from the next start, and the tool
        /// that loaded it reported success. The load is now undone and the tool reports why.
        /// </summary>
        [Test]
        public async Task ModSourceLoad_StoreThatDoesNotKeepTheSource_UndoesTheLoadAndTheToolSaysWhy()
        {
            MemorySourceStore sources = new() { DroppedSaveId = "unkept" };
            ScriptedWorldPackageStore packages = new();
            using GatedHeadlessSession session = new(packages, sources);
            LocalActorIdentityProvider identity = new("drop-host");
            LuaModsLlmTool tool = new(
                session.Controller.Runtime,
                new TestCoreAiSettings(),
                NullLog.Instance,
                SessionCapabilities,
                true,
                identity,
                BuiltInAgentRoleIds.Programmer,
                session.Gate);

            JObject refused = JObject.Parse(await tool.ExecuteAsync("load", "unkept", "local value = 1"));
            JObject kept = JObject.Parse(await tool.ExecuteAsync("load", "kept", "local value = 1"));

            Assert.IsFalse(refused.Value<bool>("success"), refused.ToString());
            StringAssert.Contains("did not keep its source", refused.Value<string>("message"));
            Assert.IsFalse(
                session.Controller.Runtime.IsLoaded(identity.GetActorContext(BuiltInAgentRoleIds.Programmer), "unkept"),
                "the mod whose source was not kept no longer runs");
            Assert.IsTrue(kept.Value<bool>("success"), kept.ToString());
            Assert.IsTrue(sources.TryLoad("kept", out _, out _));
        }

        /// <summary>
        /// A1-08: a package the confirmed load would refuse (an active Full-capability mod, or a session
        /// whose source store cannot replace a source set) used to be queued, so the player was asked to
        /// confirm a load that could only fail. It is refused at request time as invalid_package.
        /// </summary>
        [Test]
        public async Task WorldLoadRequest_PackageTheConfirmationWouldRefuse_IsRefusedBeforeThePlayerIsAsked()
        {
            RbxWorldPackagePayload fullModPackage = WithMods(
                CreateMinimalPayload(CapturedAtUtc),
                new RbxWorldModSource(
                    new LuaModManifest
                    {
                        Id = "full-mod",
                        Name = "full-mod",
                        Capabilities = LuaCapabilities.Full.ToString(),
                        Active = true
                    },
                    "return 1"));
            ScriptedWorldPackageStore packages = new() { ManualPayload = fullModPackage, AutoPayload = fullModPackage };
            using GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());
            LocalActorIdentityProvider identity = new("request-actor");
            int asked = 0;
            session.Controller.ManualLoadConfirmationRequested += _ => asked++;

            RbxWorldLoadRefusedException manual = await CatchLoadRefusal(() =>
                session.Controller.RequestManualLoadAsync(
                    identity.GetActorContext(BuiltInAgentRoleIds.Programmer), "full-slot"));
            JObject manualTool = JObject.Parse(await new LoadWorldLlmTool(
                session.Controller, identity, BuiltInAgentRoleIds.Programmer).ExecuteAsync("full-slot"));
            JObject autoTool = JObject.Parse(await new LoadAutoSaveLlmTool(
                session.Controller, identity, BuiltInAgentRoleIds.Programmer).ExecuteAsync(ValidAutoName));

            Assert.AreEqual(RbxWorldLoadRefusedException.IncompatiblePackageStatus, manual.Status);
            StringAssert.Contains("Active Full-capability mod 'full-mod'", manual.Message);
            Assert.AreEqual("invalid_package", (string)manualTool["status"], manualTool.ToString());
            Assert.AreEqual("invalid_package", (string)autoTool["status"], autoTool.ToString());
            StringAssert.Contains("full-mod", (string)autoTool["error"]);

            ScriptedWorldPackageStore plainPackages = new()
            {
                ManualPayload = CreateMinimalPayload(CapturedAtUtc)
            };
            using GatedHeadlessSession nonTransactional = new(plainPackages, new MemorySourceStore());
            nonTransactional.Controller.ManualLoadConfirmationRequested += _ => asked++;
            RbxWorldLoadRefusedException storeRefusal = await CatchLoadRefusal(() =>
                nonTransactional.Controller.RequestManualLoadAsync(
                    identity.GetActorContext(BuiltInAgentRoleIds.Programmer), "plain-slot"));
            JObject storeTool = JObject.Parse(await new LoadWorldLlmTool(
                nonTransactional.Controller, identity, BuiltInAgentRoleIds.Programmer).ExecuteAsync("plain-slot"));

            Assert.AreEqual(RbxWorldLoadRefusedException.IncompatiblePackageStatus, storeRefusal.Status);
            StringAssert.Contains("cannot atomically replace a world source set", storeRefusal.Message);
            Assert.AreEqual("invalid_package", (string)storeTool["status"], storeTool.ToString());
            Assert.AreEqual(0, plainPackages.LoadCalls, "the session rule is checked before the slot is read");
            Assert.AreEqual(0, session.Controller.GetPendingManualLoads().Count);
            Assert.AreEqual(0, nonTransactional.Controller.GetPendingManualLoads().Count);
            Assert.AreEqual(0, asked, "the player is never asked to confirm a load that would be refused");

            ScriptedWorldPackageStore loadablePackages = new()
            {
                ManualPayload = CreateMinimalPayload(CapturedAtUtc)
            };
            using GatedHeadlessSession loadable = new(loadablePackages, new RecordingTransactionalSourceStore());
            loadable.Controller.ManualLoadConfirmationRequested += _ => asked++;
            RbxWorldLoadRequest request = await loadable.Controller.RequestManualLoadAsync(
                identity.GetActorContext(BuiltInAgentRoleIds.Programmer), "loadable-slot");

            Assert.IsTrue(request.PlayerConfirmationRequired);
            Assert.AreEqual(1, asked, "a loadable package is still offered to the player");
        }

        /// <summary>
        /// A1-11: a load tool called on a session that was already shut down let
        /// ObjectDisposedException cross the tool boundary; it is now an ordinary session_unavailable
        /// result and nothing is read.
        /// </summary>
        [Test]
        public async Task WorldLoadTools_SessionAlreadyShutDown_ReturnSessionUnavailable()
        {
            ScriptedWorldPackageStore packages = new()
            {
                ManualPayload = CreateMinimalPayload(CapturedAtUtc),
                AutoPayload = CreateMinimalPayload(CapturedAtUtc)
            };
            GatedHeadlessSession session = new(packages, new RecordingTransactionalSourceStore());
            LocalActorIdentityProvider identity = new("disposed-actor");
            session.Dispose();

            JObject manual = JObject.Parse(await new LoadWorldLlmTool(
                session.Controller, identity, BuiltInAgentRoleIds.Programmer).ExecuteAsync("kept-slot"));
            JObject auto = JObject.Parse(await new LoadAutoSaveLlmTool(
                session.Controller, identity, BuiltInAgentRoleIds.Programmer).ExecuteAsync(ValidAutoName));

            foreach (JObject json in new[] { manual, auto })
            {
                Assert.IsFalse((bool)json["success"], json.ToString());
                Assert.AreEqual("session_unavailable", (string)json["status"]);
                Assert.IsFalse((bool)json["player_confirmation_required"]);
                Assert.AreEqual("", (string)json["request_id"]);
                StringAssert.Contains("The tool was NOT executed.", (string)json["error"]);
            }

            Assert.AreEqual("kept-slot", (string)manual["slot"]);
            Assert.AreEqual(ValidAutoName, (string)auto["slot"]);
            Assert.AreEqual(0, packages.LoadCalls, "nothing is read for a session that is gone");
        }

        /// <summary>
        /// A1-05: the name check called Path.GetFileName, which throws on Mono for '"', '&lt;', '&gt;',
        /// '|' and control characters (Windows) and '\0' (every player), and accepted them where it did
        /// not throw. Every such name is now refused by explicit character checks, never by a throw.
        /// </summary>
        [TestCase("\"a.world\"")]
        [TestCase("\"a\".world")]
        [TestCase("a|b.world")]
        [TestCase("a<b>.world")]
        [TestCase("a\tb.world")]
        [TestCase("a\0b.world")]
        [TestCase("a:b.world")]
        [TestCase("a*b.world")]
        [TestCase("a?b.world")]
        [TestCase("a\\b.world")]
        [TestCase("nested/b.world")]
        public void PackageNames_AutoFileNameWithACharacterNoPlayerAccepts_IsRefusedWithoutThrowing(string name)
        {
            bool accepted = true;
            string error = null;

            Assert.DoesNotThrow(() => accepted = RbxWorldPackageNames.TryValidateAutoFileName(name, out error));

            Assert.IsFalse(accepted);
            Assert.AreEqual("Auto package name must be one .world file name without a path.", error);
            Assert.IsTrue(RbxWorldPackageNames.TryValidateAutoFileName(ValidAutoName, out error), error);
        }

        /// <summary>
        /// A1-06: the WebGL work budget left out value strings (a StringValue holds 200,000 characters,
        /// and every Vector3 and CFrame value is encoded into the same string) and Humanoid state, so a
        /// world far past two million characters passed it. Both now count, exactly at the boundary.
        /// </summary>
        [Test]
        public void WebGlWorkBudget_CountsValueStringsAndHumanoidState_ExactlyAtTheBoundary()
        {
            FileRbxWorldPackageStore.MeasureWebGlWork(BudgetPayload("", ""), out _, out long baseline);
            int room = checked((int)(FileRbxWorldPackageStore.MaximumWebGlSafeTextCharacters - baseline));

            FileRbxWorldPackageStore.MeasureWebGlWork(
                BudgetPayload(new string('v', 7), new string('d', 5)), out _, out long counted);
            Assert.AreEqual(baseline + 12L, counted, "every value and DisplayName character counts");

            Assert.DoesNotThrow(() => FileRbxWorldPackageStore.ValidateWebGlWorkBudget(
                BudgetPayload(new string('v', room), "")));
            RbxWorldPackageException valueOver = Assert.Throws<RbxWorldPackageException>(() =>
                FileRbxWorldPackageStore.ValidateWebGlWorkBudget(BudgetPayload(new string('v', room + 1), "")));
            StringAssert.Contains("work budget", valueOver.Message);
            Assert.DoesNotThrow(() => FileRbxWorldPackageStore.ValidateWebGlWorkBudget(
                BudgetPayload("", new string('d', room))));
            Assert.Throws<RbxWorldPackageException>(() =>
                FileRbxWorldPackageStore.ValidateWebGlWorkBudget(BudgetPayload("", new string('d', room + 1))));
        }

        /// <summary>A1-06, the world the audit built: eleven full StringValues are refused, ten still fit.</summary>
        [Test]
        public void WebGlWorkBudget_ElevenFullStringValues_AreRefused_TenFit()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            for (int index = 0; index < 10; index++)
            {
                RbxStringValue value = (RbxStringValue)registry.Create("StringValue");
                value.Value = new string('x', RbxStringValue.MaxLength);
                value.Parent = registry.WorldRoot;
            }

            RbxWorldPackageCaptureContext context = new(
                registry, game, new InMemoryPartPropertySink(), NewSettings(), capturedAtUtc: CapturedAtUtc);
            Assert.DoesNotThrow(() => FileRbxWorldPackageStore.ValidateWebGlWorkBudget(
                RbxWorldPackageSerializer.Capture(context)));

            RbxStringValue eleventh = (RbxStringValue)registry.Create("StringValue");
            eleventh.Value = new string('x', RbxStringValue.MaxLength);
            eleventh.Parent = registry.WorldRoot;
            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(context);

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() =>
                FileRbxWorldPackageStore.ValidateWebGlWorkBudget(payload));
            StringAssert.Contains("text characters", refused.Message);
        }

        /// <summary>
        /// A1-10: manual slots were unbounded and create-once, so a model could fill the disk with
        /// saves until every autosave failed. The count and the total bytes are capped, and a save past
        /// either is an ordinary failed result that writes nothing; one that exactly reaches the byte
        /// cap is still written.
        /// </summary>
        [Test]
        public async Task FileStore_ManualSlotCountAndByteCaps_RefuseAsResultsAndWriteNothing()
        {
            string root = NewTemporaryDirectory();
            RbxWorldPackagePayload payload = CreateMinimalPayload(CapturedAtUtc);
            FileRbxWorldPackageStore countCapped = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                maximumManualSlots: 2);

            RbxWorldPackageWriteResult first = await countCapped.CreateManualAsync("first", payload);
            RbxWorldPackageWriteResult second = await countCapped.CreateManualAsync("second", payload);
            RbxWorldPackageWriteResult third = await countCapped.CreateManualAsync("third", payload);

            Assert.IsTrue(first.Success, first.Error);
            Assert.IsTrue(second.Success, second.Error);
            Assert.IsFalse(third.Success);
            StringAssert.Contains("already keeps 2 manual slots, the limit is 2", third.Error);
            StringAssert.Contains("Nothing was written.", third.Error);
            CollectionAssert.AreEqual(new[] { "first", "second" }, countCapped.ListManualSlots());
            Assert.IsFalse(File.Exists(Path.Combine(root, "Manual", "third.world")));

            long packageBytes = new FileInfo(second.Path).Length;
            Assert.AreEqual(new FileInfo(first.Path).Length, packageBytes, "precondition: equal payloads encode equally");
            FileRbxWorldPackageStore byteCapped = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                maximumManualSlotBytes: 3L * packageBytes - 1L);
            RbxWorldPackageWriteResult overBytes = await byteCapped.CreateManualAsync("third", payload);

            Assert.IsFalse(overBytes.Success);
            StringAssert.Contains("bytes this store allows", overBytes.Error);
            Assert.IsFalse(File.Exists(Path.Combine(root, "Manual", "third.world")));

            FileRbxWorldPackageStore exactBytes = new(
                root,
                persistenceSyncAsync: cancellationToken => UniTask.FromResult(true),
                utcNow: () => CapturedAtUtc,
                maximumManualSlotBytes: 3L * packageBytes);
            RbxWorldPackageWriteResult atLimit = await exactBytes.CreateManualAsync("third", payload);

            Assert.IsTrue(atLimit.Success, atLimit.Error);
            Assert.AreEqual(64, FileRbxWorldPackageStore.DefaultMaximumManualSlots);
        }

        /// <summary>
        /// A1-13: a crash between a temporary write and its install left <c>&lt;entry&gt;.&lt;guid&gt;.tmp</c>
        /// files that nothing ever removed. Opening the store sweeps exactly that shape in its manual,
        /// autosave and startup directories and leaves every other file alone.
        /// </summary>
        [Test]
        public void FileStore_Open_SweepsOnlyCrashLeftTemporaryFiles()
        {
            const string Guid32 = "0123456789abcdef0123456789abcdef";
            string root = NewTemporaryDirectory();
            string startup = new FileRbxWorldPackageStore(root, startupNamespace: "sweep-ns").StartupDirectoryForTests;
            string manual = Path.Combine(root, "Manual");
            string auto = Path.Combine(root, "Auto");
            Directory.CreateDirectory(manual);
            Directory.CreateDirectory(auto);
            Directory.CreateDirectory(startup);
            string[] swept =
            {
                Path.Combine(manual, "slot.world." + Guid32 + ".tmp"),
                Path.Combine(auto, ValidAutoName + "." + Guid32 + ".tmp"),
                Path.Combine(startup, "0000000002.world." + Guid32 + ".tmp"),
                Path.Combine(startup, "0000000002.json." + Guid32 + ".tmp"),
                Path.Combine(startup, "0000000003.default." + Guid32 + ".tmp")
            };
            string[] kept =
            {
                Path.Combine(manual, "slot.world"),
                Path.Combine(manual, "notes.tmp"),
                Path.Combine(manual, "slot.world.tmp"),
                Path.Combine(manual, "slot.world." + Guid32.Substring(1) + ".tmp"),
                Path.Combine(manual, "slot.world." + Guid32.ToUpperInvariant() + ".tmp"),
                Path.Combine(manual, "slot.json." + Guid32 + ".tmp"),
                Path.Combine(auto, ".world." + Guid32 + ".tmp"),
                Path.Combine(root, "stray.world." + Guid32 + ".tmp")
            };
            foreach (string path in swept)
            {
                File.WriteAllText(path, "partial");
            }

            foreach (string path in kept)
            {
                File.WriteAllText(path, "kept");
            }

            FileRbxWorldPackageStore reopened = new(root, startupNamespace: "sweep-ns");

            foreach (string path in swept)
            {
                Assert.IsFalse(File.Exists(path), "a crash-left temporary file must be swept: " + path);
            }

            foreach (string path in kept)
            {
                Assert.IsTrue(File.Exists(path), "only the exact temporary shape is swept: " + path);
            }

            CollectionAssert.AreEqual(new[] { "slot" }, reopened.ListManualSlots());
        }

        private static RbxWorldPackagePayload WithMods(RbxWorldPackagePayload source, params RbxWorldModSource[] mods)
        {
            return new RbxWorldPackagePayload(
                source.CapturedAtUtc,
                source.Settings,
                source.Tree,
                source.Parts,
                source.CameraCFrame,
                mods);
        }

        /// <summary>A structurally minimal payload holding one value string and one Humanoid DisplayName.</summary>
        private static RbxWorldPackagePayload BudgetPayload(string valueString, string displayName)
        {
            InstanceTreeSnapshot tree = new();
            tree.Instances.Add(new InstanceSnapshot { Id = 1UL, ClassName = "DataModel", Name = "Game" });
            tree.Instances.Add(new InstanceSnapshot
            {
                Id = 2UL,
                ParentId = 1UL,
                ClassName = "StringValue",
                Name = "Value",
                Value = new ValueSnapshot { StringValue = valueString }
            });
            tree.Instances.Add(new InstanceSnapshot
            {
                Id = 3UL,
                ParentId = 1UL,
                ClassName = "Humanoid",
                Name = "Humanoid",
                Humanoid = new HumanoidSnapshot
                {
                    Health = "100",
                    MaxHealth = "100",
                    WalkSpeed = "16",
                    JumpPower = "50",
                    JumpHeight = "7.2",
                    DisplayName = displayName
                }
            });
            return new RbxWorldPackagePayload(
                CapturedAtUtc,
                NewSettings(),
                tree,
                new Dictionary<InstanceId, PartProperties>(),
                null,
                Array.Empty<RbxWorldModSource>());
        }

        private static async Task<RbxWorldLoadRefusedException> CatchLoadRefusal(
            Func<UniTask<RbxWorldLoadRequest>> request)
        {
            try
            {
                await request();
            }
            catch (RbxWorldLoadRefusedException refused)
            {
                return refused;
            }

            Assert.Fail("The world-load request was expected to be refused.");
            return null;
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

        private static List<string> ModIdsOf(RbxWorldPackagePayload payload)
        {
            List<string> ids = new(payload.Mods.Count);
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                ids.Add(mod.Manifest.Id);
            }

            return ids;
        }

        /// <summary>A minimal captured world whose workspace holds one Folder named <paramref name="folderName"/>.</summary>
        private RbxWorldPackagePayload CreatePayloadWithFolder(string folderName)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            RbxInstance folder = registry.Create("Folder");
            folder.Name = folderName;
            folder.Parent = registry.WorldRoot;
            return RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry,
                    game,
                    new InMemoryPartPropertySink(),
                    NewSettings(),
                    capturedAtUtc: CapturedAtUtc));
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

        /// <summary>
        /// Asserts the payload carries exactly these non-finite diagnostics ("instanceId:member"), each
        /// with no dropped PrimaryPart and the <c>non-finite-value</c> reason, and nothing else.
        /// </summary>
        /// <summary>Formats one expected diagnostic as "modelId:droppedId:reason:member".</summary>
        private static string Diagnostic(RbxInstance instance, RbxInstance dropped, string reason, string member)
        {
            return instance.Id.Value.ToString(CultureInfo.InvariantCulture)
                   + ":" + (dropped == null ? 0UL : dropped.Id.Value).ToString(CultureInfo.InvariantCulture)
                   + ":" + reason
                   + ":" + (member ?? "");
        }

        /// <summary>Asserts the payload carries exactly these diagnostics, in any order.</summary>
        private static void AssertDiagnostics(RbxWorldPackagePayload payload, params string[] expected)
        {
            List<string> actual = new();
            foreach (RbxWorldPackageDiagnostic diagnostic in payload.Diagnostics)
            {
                actual.Add(diagnostic.ModelId.ToString(CultureInfo.InvariantCulture)
                           + ":" + diagnostic.DroppedPrimaryPartId.ToString(CultureInfo.InvariantCulture)
                           + ":" + diagnostic.Reason
                           + ":" + (diagnostic.Member ?? ""));
            }

            CollectionAssert.AreEquivalent(expected, actual);
        }

        private static RbxInstance CreateNamed(
            InstanceRegistry registry,
            string className,
            string name,
            RbxInstance parent)
        {
            RbxInstance instance = registry.Create(className);
            instance.Name = name;
            instance.Parent = parent;
            return instance;
        }

        /// <summary>Creates a world-owned Part with the default bundle, optionally naming a MaterialVariant.</summary>
        private static RbxInstance CreateNamedPart(
            InstanceRegistry registry,
            IPartPropertySink partSink,
            string name,
            RbxInstance parent,
            string materialVariant)
        {
            RbxInstance part = CreateNamed(registry, "Part", name, parent);
            PartProperties properties = PartProperties.CreateDefault();
            properties.MaterialVariant = materialVariant;
            partSink.SetPartProperties(part.Id, in properties);
            return part;
        }

        private static RbxObjectValue RestoredObjectValue(
            RbxWorldPackageRestoreResult restored,
            RbxInstance original)
        {
            Assert.IsTrue(restored.Registry.TryGet(original.Id, out RbxInstance instance));
            return (RbxObjectValue)instance;
        }

        private static void AssertOnlyNonFiniteDiagnostics(
            RbxWorldPackagePayload payload,
            params string[] expectedMembers)
        {
            List<string> actual = new();
            foreach (RbxWorldPackageDiagnostic diagnostic in payload.Diagnostics)
            {
                Assert.AreEqual(0UL, diagnostic.DroppedPrimaryPartId);
                Assert.AreEqual("non-finite-value", diagnostic.Reason);
                actual.Add(diagnostic.ModelId.ToString(CultureInfo.InvariantCulture)
                           + ":" + diagnostic.Member);
            }

            CollectionAssert.AreEquivalent(expectedMembers, actual);
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

        /// <summary>
        /// A four-node world whose every id, revision and Part value is known from its build steps:
        /// DataModel 2^53+1 (rev 1: Workspace parented), Workspace 2^53+2 (rev 2: parented, Model added),
        /// Model 2^53+3 (rev 8: Name, Part added, PrimaryPart, WorldPivot, two attributes, tag, parented),
        /// Part 2^53+4 (rev 2: Name, parented). Access control and ACL versioning do not advance revisions.
        /// </summary>
        private GoldenWorld BuildGoldenWorld()
        {
            InstanceIdAllocator allocator = new();
            allocator.EnsureNotBelow(new InstanceId(GoldenFirstId - 1UL));
            InstanceRegistry registry = new(allocator: allocator, worldId: GoldenWorldId);
            RbxDataModel game = (RbxDataModel)registry.Create("DataModel");
            _games.Add(game);
            registry.SetSceneRoot(game);
            RbxInstance workspace = registry.Create("Workspace");
            workspace.Parent = game;
            registry.SetWorldRoot(workspace);
            string originTag = OriginTag.FromConsole("golden-invocation");
            RbxModel model = (RbxModel)registry.Create("Model", originTag: originTag);
            model.Name = "GoldenModel";
            RbxInstance part = registry.Create("Part", originTag: originTag);
            part.Name = "GoldenPart";
            part.Parent = model;
            model.SetPrimaryPart(part);
            model.SetWorldPivot(RbxCFrame.FromPosition(1f, 2f, 3f));
            model.SetAttribute("Label", "golden");
            model.SetAttribute("Weight", 4.5d);
            model.AddTag("Golden");
            model.Parent = workspace;
            registry.ConfigureWorldAclVersion(InstanceRegistry.CurrentWorldAclVersion);
            registry.SetAccessControl(model, "actor-golden", InstanceAccessScope.Owned, true);
            Assert.AreEqual(GoldenFirstId, game.Id.Value, "Fixture precondition: ids start at 2^53 + 1.");
            Assert.AreEqual(GoldenFirstId + 3UL, part.Id.Value, "Fixture precondition: ids are sequential.");

            InMemoryPartPropertySink partSink = new();
            PartProperties properties = new()
            {
                Shape = RbxPartShape.Cylinder,
                Material = new RbxMaterialId("Wood", 512),
                MaterialVariant = null,
                CFrame = new RbxCFrame(2f, 3f, -4f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f),
                Size = new RbxVector3(4f, 1.5f, 2f),
                Color = new RbxColor3(0.25f, 0.5f, 0.75f),
                ColorWasExplicitlySet = true,
                Anchored = true,
                Transparency = 0.25f,
                CanCollide = false
            };
            partSink.SetPartProperties(part.Id, in properties);
            InMemoryCameraRig cameraRig = new();
            cameraRig.SetCFrame(RbxCFrame.FromPosition(10f, 5f, -4f));
            MemorySourceStore sourceStore = new();
            sourceStore.Save(
                "beta-mod",
                "return 'beta'",
                new LuaModManifest { Id = "beta-mod", Name = "Beta", Active = false });
            sourceStore.Save(
                "alpha-mod",
                "return 'alpha'",
                new LuaModManifest { Id = "alpha-mod", Name = "Alpha", Active = true });
            return new GoldenWorld(registry, game, partSink, cameraRig, sourceStore);
        }

        private static List<string> ReadEntryNames(byte[] package)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            List<string> names = new(archive.Entries.Count);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                names.Add(entry.FullName);
            }

            return names;
        }

        private static string ReadEntryText(byte[] package, string entryName)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            ZipArchiveEntry entry = archive.GetEntry(entryName);
            Assert.IsNotNull(entry, "Missing package entry '" + entryName + "'.");
            using Stream stream = entry.Open();
            using StreamReader reader = new(stream, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }

        /// <summary>Parses JSON without turning ISO-8601 strings into dates, so literal text compares exactly.</summary>
        private static JObject ParseJsonLiteral(string json)
        {
            using StringReader text = new(json);
            using Newtonsoft.Json.JsonTextReader reader = new(text)
            {
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
                FloatParseHandling = Newtonsoft.Json.FloatParseHandling.Double
            };
            return JObject.Load(reader);
        }

        private static List<string> PropertyNames(JToken token)
        {
            Assert.AreEqual(JTokenType.Object, token.Type);
            List<string> names = new();
            foreach (JProperty property in ((JObject)token).Properties())
            {
                names.Add(property.Name);
            }

            return names;
        }

        private static void AssertJsonNumbers(JToken token, params double[] expected)
        {
            Assert.AreEqual(JTokenType.Array, token.Type);
            JArray array = (JArray)token;
            Assert.AreEqual(expected.Length, array.Count);
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.IsTrue(
                    array[index].Type == JTokenType.Float || array[index].Type == JTokenType.Integer,
                    "Component " + index + " must be a JSON number.");
                Assert.AreEqual(expected[index], array[index].Value<double>(), 0d, "Component " + index);
            }
        }

        private static void AssertGoldenModIndex(JToken entry, string id, string prefix)
        {
            CollectionAssert.AreEquivalent(
                new[] { "id", "manifest_entry", "source_entry" },
                PropertyNames(entry));
            Assert.AreEqual(id, (string)entry["id"]);
            Assert.AreEqual(prefix + "manifest.json", (string)entry["manifest_entry"]);
            Assert.AreEqual(prefix + "main.lua", (string)entry["source_entry"]);
        }

        private static void AssertGoldenNode(
            JToken node,
            string id,
            string parentId,
            string className,
            string name,
            string originTag,
            string ownerActorId,
            string accessScope,
            string revision)
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "id", "parent_id", "class_name", "name", "archivable", "owner_mod_id", "origin_tag",
                    "owner_actor_id", "access_scope", "revision", "tags", "attributes", "model", "part",
                    "click_detector", "material_variant", "value", "humanoid"
                },
                PropertyNames(node));
            Assert.AreEqual(JTokenType.String, node["id"].Type, className + " id must be a decimal string.");
            Assert.AreEqual(id, (string)node["id"]);
            Assert.AreEqual(JTokenType.String, node["parent_id"].Type, className + " parent id must be a decimal string.");
            Assert.AreEqual(parentId, (string)node["parent_id"]);
            Assert.AreEqual(className, (string)node["class_name"]);
            Assert.AreEqual(name, (string)node["name"]);
            Assert.IsTrue((bool)node["archivable"]);
            Assert.AreEqual(JTokenType.Null, node["owner_mod_id"].Type);
            Assert.AreEqual(originTag, (string)node["origin_tag"]);
            Assert.AreEqual(ownerActorId, (string)node["owner_actor_id"]);
            Assert.AreEqual(accessScope, (string)node["access_scope"]);
            Assert.AreEqual(JTokenType.String, node["revision"].Type, className + " revision must be a decimal string.");
            Assert.AreEqual(revision, (string)node["revision"]);
            Assert.AreEqual(JTokenType.Null, node["click_detector"].Type);
            Assert.AreEqual(JTokenType.Null, node["material_variant"].Type);
            Assert.AreEqual(JTokenType.Null, node["value"].Type);
            Assert.AreEqual(JTokenType.Null, node["humanoid"].Type);
            if (!string.Equals(className, "Model", StringComparison.Ordinal))
            {
                CollectionAssert.IsEmpty((JArray)node["tags"]);
                CollectionAssert.IsEmpty((JArray)node["attributes"]);
            }
        }

        private static void AssertGoldenModelState(JToken model, string primaryPartId, double[] storedWorldPivot)
        {
            CollectionAssert.AreEquivalent(
                new[] { "primary_part_id", "has_stored_world_pivot", "stored_world_pivot" },
                PropertyNames(model));
            Assert.AreEqual(JTokenType.String, model["primary_part_id"].Type);
            Assert.AreEqual(primaryPartId, (string)model["primary_part_id"]);
            Assert.AreEqual(storedWorldPivot != null, (bool)model["has_stored_world_pivot"]);
            if (storedWorldPivot == null)
            {
                Assert.AreEqual(JTokenType.Null, model["stored_world_pivot"].Type);
            }
            else
            {
                AssertJsonNumbers(model["stored_world_pivot"], storedWorldPivot);
            }
        }

        private static void AssertGoldenAttribute(
            JToken attribute,
            string name,
            string kind,
            string stringValue,
            double numberValue)
        {
            CollectionAssert.AreEquivalent(
                new[] { "name", "kind", "string_value", "number_value", "bool_value" },
                PropertyNames(attribute));
            Assert.AreEqual(name, (string)attribute["name"]);
            Assert.AreEqual(kind, (string)attribute["kind"]);
            Assert.AreEqual(stringValue, (string)attribute["string_value"]);
            Assert.AreEqual(numberValue, attribute["number_value"].Value<double>(), 0d);
            Assert.IsFalse((bool)attribute["bool_value"]);
        }

        private static byte[] BuildLiteralPackage(params (string Name, string Text)[] entries)
        {
            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
            {
                foreach ((string name, string text) in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
                    using Stream stream = entry.Open();
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            return output.ToArray();
        }

        private static RbxInstance AssertLiteralRecord(
            InstanceRegistry registry,
            ulong id,
            string className,
            ulong parentId,
            long revision,
            string originTag,
            string ownerActorId,
            InstanceAccessScope accessScope)
        {
            Assert.IsTrue(registry.TryGet(new InstanceId(id), out RbxInstance instance), "Missing instance " + id);
            Assert.IsTrue(registry.TryGetRecord(new InstanceId(id), out InstanceRecord record));
            Assert.AreEqual(className, instance.ClassName);
            Assert.AreEqual(parentId, instance.Parent?.Id.Value ?? 0UL, className + " parent");
            Assert.AreEqual(revision, record.Revision, className + " revision");
            Assert.IsNull(record.OwnerModId);
            Assert.AreEqual(originTag, record.OriginTag);
            Assert.AreEqual(ownerActorId, record.OwnerActorId);
            Assert.AreEqual(accessScope, record.AccessScope);
            return instance;
        }

        /// <summary>
        /// True when <paramref name="hook"/> is <paramref name="target"/> or a method whose body calls it
        /// directly (a method-group wrapper or a forwarding lambda). Read from IL, because invoking the hook
        /// cannot tell: off WebGL both the real default and a stub answer true.
        /// </summary>
        private static bool ForwardsTo(Delegate hook, MethodInfo target)
        {
            MethodInfo method = hook.Method;
            if (IsSameMethod(method, target))
            {
                return true;
            }

            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                return false;
            }

            byte callOpcode = (byte)System.Reflection.Emit.OpCodes.Call.Value;
            for (int index = 0; index + 4 < il.Length; index++)
            {
                if (il[index] != callOpcode)
                {
                    continue;
                }

                MethodBase callee;
                try
                {
                    callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1));
                }
                catch (Exception)
                {
                    // WHY: a byte equal to the call opcode inside another instruction's operand is not a
                    // WHY: call; the four bytes after it are no method token, so there is nothing to compare.
                    continue;
                }

                if (callee is MethodInfo calleeMethod && IsSameMethod(calleeMethod, target))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSameMethod(MethodInfo left, MethodInfo right)
        {
            return left.MetadataToken == right.MetadataToken
                   && left.Module.ModuleVersionId == right.Module.ModuleVersionId;
        }

        private sealed class GoldenWorld
        {
            public GoldenWorld(
                InstanceRegistry registry,
                RbxDataModel game,
                InMemoryPartPropertySink partSink,
                InMemoryCameraRig cameraRig,
                MemorySourceStore sourceStore)
            {
                Registry = registry;
                Game = game;
                PartSink = partSink;
                CameraRig = cameraRig;
                SourceStore = sourceStore;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink PartSink { get; }

            public InMemoryCameraRig CameraRig { get; }

            public MemorySourceStore SourceStore { get; }
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

            /// <summary>A mod id whose saves this store drops without a word, like a store that fails quietly.</summary>
            public string DroppedSaveId { get; set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                if (string.Equals(id, DroppedSaveId, StringComparison.Ordinal))
                {
                    return;
                }

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

        /// <summary>
        /// The production session controller over the headless host, every session stack sharing one
        /// <see cref="ConfirmedWorldMutationGate"/> over the controller's own capture, as the installer
        /// composes them.
        /// </summary>
        private sealed class GatedHeadlessSession : IDisposable
        {
            public GatedHeadlessSession(
                IRbxWorldPackageStore packageStore,
                ILuaModSourceStore sourceStore,
                INetworkBridge networkBridge = null)
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                HeadlessRbxWorldSessionHost host = new(
                    registry,
                    game,
                    NewSettings(),
                    new InMemoryPartPropertySink(),
                    new InMemoryCameraRig());
                LuaCsRbxApiBindings rbxApi = new(
                    host.Registry,
                    host.Game,
                    partSink: host.PartSink,
                    cameraRig: host.CameraRig,
                    networkBridge: networkBridge);
                Gate = new ConfirmedWorldMutationGate(
                    cancellationToken => UniTask.FromResult(Controller.CaptureCurrent()),
                    packageStore);
                Controller = new RbxWorldRuntimeSessionController(
                    host,
                    packageStore,
                    sourceStore,
                    CreateStack(rbxApi, sourceStore, new Mvp1AcceptanceMemoryStore(), null),
                    rbxApi,
                    (candidate, stagedNetwork) => new LuaCsRbxApiBindings(
                        candidate.Registry,
                        candidate.Game,
                        partSink: candidate.PartSink,
                        cameraRig: candidate.CameraRig,
                        networkBridge: stagedNetwork),
                    CreateStack,
                    (stack, stagedApi) => { },
                    networkBridge,
                    SessionCapabilities,
                    false,
                    new Mvp1AcceptanceMemoryStore(),
                    null,
                    message => Diagnostics.Add(message));
            }

            public ConfirmedWorldMutationGate Gate { get; }

            public RbxWorldRuntimeSessionController Controller { get; }

            public List<string> Diagnostics { get; } = new();

            public void Dispose()
            {
                Controller.Dispose();
            }

            private LuaCsModStack CreateStack(
                LuaCsRbxApiBindings rbxApi,
                ILuaModSourceStore sourceStore,
                ILuaModStore modStore,
                ILuaScriptVersionStore versionStore)
            {
                return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new Mvp1AcceptanceNullLogger(),
                    LuaScriptVersions = versionStore,
                    ModStore = modStore,
                    ModSourceStore = sourceStore,
                    Capabilities = SessionCapabilities,
                    OneOffCapabilities = SessionCapabilities,
                    RbxApi = rbxApi,
                    WorldMutationGate = Gate,
                    RegisterWorldEditBuildBindings = false
                });
            }
        }

        /// <summary>A package store whose next autosave can be held open; it records every autosave request.</summary>
        private sealed class ScriptedWorldPackageStore : IRbxWorldPackageStore
        {
            private UniTaskCompletionSource<RbxWorldPackageWriteResult> _heldWrite;

            /// <summary>When set, the next autosave stays pending until <see cref="ReleaseHeldWrite"/>.</summary>
            public bool HoldNextWrite { get; set; }

            public List<string> AutoTriggers { get; } = new();

            public List<RbxWorldPackagePayload> AutoPayloads { get; } = new();

            public RbxWorldPackagePayload ManualPayload { get; set; }

            public RbxWorldPackagePayload AutoPayload { get; set; }

            public int LoadCalls { get; private set; }

            public void ReleaseHeldWrite(bool success)
            {
                UniTaskCompletionSource<RbxWorldPackageWriteResult> held = _heldWrite
                    ?? throw new InvalidOperationException("No autosave is held.");
                _heldWrite = null;
                held.TrySetResult(new RbxWorldPackageWriteResult(
                    success,
                    "held.world",
                    success ? "" : "Injected durability refusal."));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
                string trigger,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                AutoTriggers.Add(trigger);
                AutoPayloads.Add(payload);
                if (!HoldNextWrite)
                {
                    return UniTask.FromResult(new RbxWorldPackageWriteResult(true, trigger + ".world", ""));
                }

                HoldNextWrite = false;
                _heldWrite = new UniTaskCompletionSource<RbxWorldPackageWriteResult>();
                return _heldWrite.Task;
            }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
                string slot,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    false,
                    "",
                    "Manual slots are outside this test seam."));
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(
                string slot,
                CancellationToken cancellationToken = default)
            {
                LoadCalls++;
                if (ManualPayload == null)
                {
                    throw new FileNotFoundException("No manual payload is scripted.", slot);
                }

                return UniTask.FromResult(ManualPayload);
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(
                string fileName,
                CancellationToken cancellationToken = default)
            {
                LoadCalls++;
                if (AutoPayload == null)
                {
                    throw new FileNotFoundException("No autosave payload is scripted.", fileName);
                }

                return UniTask.FromResult(AutoPayload);
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }
        }

        /// <summary>
        /// A package store that also keeps the startup selection: it answers one selected package, can
        /// hold its read open, and records every autosave and every recorded startup entry.
        /// </summary>
        private sealed class ScriptedStartupWorldPackageStore : IRbxWorldPackageStore, IRbxWorldStartupStore
        {
            private readonly RbxWorldPackagePayload _selected;
            private UniTaskCompletionSource<bool> _heldRead;

            public ScriptedStartupWorldPackageStore(RbxWorldPackagePayload selected)
            {
                _selected = selected;
            }

            /// <summary>When set, the next full startup read stays pending until <see cref="ReleaseHeldStartupRead"/>.</summary>
            public bool HoldNextStartupRead { get; set; }

            public List<string> AutoTriggers { get; } = new();

            public List<RbxWorldPackagePayload> AutoPayloads { get; } = new();

            public List<RbxWorldPackagePayload> SelectedPayloads { get; } = new();

            public void ReleaseHeldStartupRead()
            {
                UniTaskCompletionSource<bool> held = _heldRead
                    ?? throw new InvalidOperationException("No startup read is held.");
                _heldRead = null;
                held.TrySetResult(true);
            }

            public UniTask<RbxWorldPackageWriteResult> SelectStartupAsync(
                RbxWorldPackagePayload payload,
                string sourceKind,
                string sourceName,
                CancellationToken cancellationToken = default)
            {
                SelectedPayloads.Add(payload);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, "startup.world", ""));
            }

            public UniTask<RbxWorldPackageWriteResult> ClearStartupAsync(
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, "startup.default", ""));
            }

            public async UniTask<RbxWorldStartupSelection> ReadStartupAsync(
                CancellationToken cancellationToken = default)
            {
                if (HoldNextStartupRead)
                {
                    HoldNextStartupRead = false;
                    _heldRead = new UniTaskCompletionSource<bool>();
                    await _heldRead.Task;
                }

                return new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package,
                    1,
                    _selected,
                    _selected.Settings.WorldId,
                    CapturedAtUtc,
                    "manual",
                    "boot-slot",
                    "");
            }

            public UniTask<RbxWorldStartupSelection> ReadStartupInfoAsync(
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldStartupSelection(
                    RbxWorldStartupSelectionKind.Package,
                    1,
                    null,
                    _selected.Settings.WorldId,
                    CapturedAtUtc,
                    "manual",
                    "boot-slot",
                    ""));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateAutoAsync(
                string trigger,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                AutoTriggers.Add(trigger);
                AutoPayloads.Add(payload);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, trigger + ".world", ""));
            }

            public UniTask<RbxWorldPackageWriteResult> CreateManualAsync(
                string slot,
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(new RbxWorldPackageWriteResult(
                    false,
                    "",
                    "Manual slots are outside this test seam."));
            }

            public UniTask<RbxWorldPackagePayload> LoadManualAsync(
                string slot,
                CancellationToken cancellationToken = default)
            {
                throw new FileNotFoundException("No manual payload is scripted.", slot);
            }

            public UniTask<RbxWorldPackagePayload> LoadAutoAsync(
                string fileName,
                CancellationToken cancellationToken = default)
            {
                throw new FileNotFoundException("No autosave payload is scripted.", fileName);
            }

            public IReadOnlyList<string> ListManualSlots()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<string> ListAutoFiles()
            {
                return Array.Empty<string>();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }
        }

        /// <summary>A transactional source store that counts each stage of its exact replacements.</summary>
        private sealed class RecordingTransactionalSourceStore : ILuaModSourceStore, IRbxWorldModSourceStore
        {
            private readonly MemorySourceStore _sources = new();

            public int Prepared { get; private set; }

            public int Completed { get; private set; }

            public int RolledBack { get; private set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _sources.Save(id, source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                return _sources.TryLoad(id, out source, out manifest);
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return _sources.List();
            }

            public void SetActive(string id, bool active)
            {
                _sources.SetActive(id, active);
            }

            public void Delete(string id)
            {
                _sources.Delete(id);
            }

            public UniTask<IRbxWorldModSourceReplacement> PrepareExactReplacementAsync(
                IReadOnlyList<RbxWorldModSource> mods,
                CancellationToken cancellationToken = default)
            {
                Prepared++;
                MemorySourceStore staged = new();
                staged.ReplaceWith(mods);
                return UniTask.FromResult<IRbxWorldModSourceReplacement>(new Replacement(this, staged));
            }

            private sealed class Replacement : IRbxWorldModSourceReplacement
            {
                private readonly RecordingTransactionalSourceStore _owner;

                public Replacement(RecordingTransactionalSourceStore owner, ILuaModSourceStore staged)
                {
                    _owner = owner;
                    SourceStore = staged;
                }

                public ILuaModSourceStore SourceStore { get; }

                public void Activate()
                {
                }

                public UniTask CompleteAsync(CancellationToken cancellationToken = default)
                {
                    _owner.Completed++;
                    return UniTask.CompletedTask;
                }

                public UniTask RollbackAsync(CancellationToken cancellationToken = default)
                {
                    _owner.RolledBack++;
                    return UniTask.CompletedTask;
                }

                public void Dispose()
                {
                }
            }
        }

        /// <summary>A host transport whose registered actors stand for live remote sessions.</summary>
        private sealed class JoinableNetworkBridge : INetworkBridge
        {
            private readonly List<string> _actorIds = new();

            public RbxNetworkTopology Topology => RbxNetworkTopology.Host;

            public IReadOnlyList<string> ActorIds => _actorIds;

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public void RegisterActor(string actorId)
            {
                if (!_actorIds.Contains(actorId))
                {
                    _actorIds.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actorIds.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
            }

            public void SendRequest(RbxNetworkRequestMessage message, Action<RbxNetworkResponse> response)
            {
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

            public void CopyBackingState(InstanceId sourceId, InstanceId destinationId)
            {
            }
        }
    }
}
