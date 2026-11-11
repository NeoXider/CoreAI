using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP4 step B2: the world package reads format version 2, in which every script instance carries a
    /// <c>script</c> block (<c>source_id</c>, and on a Script or LocalScript <c>enabled</c> and <c>run_context</c>), and
    /// still reads and writes version 1, which production capture keeps writing until step C1. Every refusal of the
    /// note's B2 rows has its accepted twin: a dangling binding, a duplicate binding, a class and RunContext that
    /// disagree with the mod's context, a Plugin RunContext, an unknown format version.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageScriptsEditModeTests
    {
        private const string WorldId = "worldpkg-scripts";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 18, 30, 0, DateTimeKind.Utc);

        private const string LiteralManifestJson = @"{
  ""format"": ""coreai-rbx-world"",
  ""format_version"": 2,
  ""minimum_reader_version"": 2,
  ""api_version"": ""MVP2"",
  ""created_utc"": ""2026-11-11T18:30:00.0000000Z"",
  ""world_entry"": ""world.json"",
  ""mods"": [
    {
      ""id"": ""door-script"",
      ""manifest_entry"": ""Mods/0000/manifest.json"",
      ""source_entry"": ""Mods/0000/main.lua""
    },
    {
      ""id"": ""shared-tools"",
      ""manifest_entry"": ""Mods/0001/manifest.json"",
      ""source_entry"": ""Mods/0001/main.lua""
    }
  ]
}";

        private const string LiteralDoorManifestJson = @"{
  ""Id"": ""door-script"",
  ""Name"": ""Door"",
  ""Active"": true,
  ""Context"": ""server""
}";

        private const string LiteralToolsManifestJson = @"{
  ""Id"": ""shared-tools"",
  ""Name"": ""Tools"",
  ""Active"": true,
  ""Context"": ""shared""
}";

        private const string LiteralWorldJson = @"{
  ""schema_version"": 1,
  ""settings"": {
    ""world_id"": ""worldpkg-scripts-literal"",
    ""world_acl_version"": null,
    ""meters_per_stud"": 0.28,
    ""gravity_studs_per_second_squared"": 196.2,
    ""signal_behavior"": ""Deferred""
  },
  ""camera_cframe"": null,
  ""instances"": [
    { ""id"": ""1"", ""parent_id"": ""0"", ""class_name"": ""DataModel"", ""name"": ""Game"", ""archivable"": true,
      ""revision"": ""1"", ""tags"": [], ""attributes"": [] },
    { ""id"": ""2"", ""parent_id"": ""1"", ""class_name"": ""Workspace"", ""name"": ""Workspace"", ""archivable"": true,
      ""revision"": ""1"", ""tags"": [], ""attributes"": [],
      ""model"": { ""primary_part_id"": ""0"", ""has_stored_world_pivot"": false, ""stored_world_pivot"": null } },
    { ""id"": ""3"", ""parent_id"": ""1"", ""class_name"": ""ServerScriptService"", ""name"": ""ServerScriptService"",
      ""archivable"": true, ""revision"": ""1"", ""tags"": [], ""attributes"": [] },
    { ""id"": ""4"", ""parent_id"": ""1"", ""class_name"": ""ReplicatedStorage"", ""name"": ""ReplicatedStorage"",
      ""archivable"": true, ""revision"": ""1"", ""tags"": [], ""attributes"": [] },
    { ""id"": ""5"", ""parent_id"": ""3"", ""class_name"": ""Script"", ""name"": ""Door"", ""archivable"": true,
      ""origin_tag"": ""console:literal"", ""revision"": ""4"", ""tags"": [], ""attributes"": [],
      ""script"": { ""source_id"": ""door-script"", ""enabled"": false, ""run_context"": 1 } },
    { ""id"": ""6"", ""parent_id"": ""4"", ""class_name"": ""ModuleScript"", ""name"": ""Tools"", ""archivable"": true,
      ""origin_tag"": ""console:literal"", ""revision"": ""2"", ""tags"": [], ""attributes"": [],
      ""script"": { ""source_id"": ""shared-tools"" } },
    { ""id"": ""7"", ""parent_id"": ""4"", ""class_name"": ""LocalScript"", ""name"": ""Hud"", ""archivable"": true,
      ""origin_tag"": ""console:literal"", ""revision"": ""2"", ""tags"": [], ""attributes"": [],
      ""script"": { ""source_id"": """", ""enabled"": true, ""run_context"": 0 } }
  ]
}";

        // ==================== Round trips ====================

        [Test]
        public void PackageV2_ScriptInstances_RoundTripSourceIdEnabledRunContext()
        {
            ScriptWorld world = ScriptWorld.CreateFull();

            RbxWorldPackagePayload captured = world.Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion);
            byte[] package = RbxWorldPackageSerializer.WritePackage(captured);

            Assert.AreEqual(2, captured.FormatVersion);
            Assert.IsEmpty(captured.Diagnostics, "a world whose bindings are all whole needs no repair");
            JObject manifest = EntryJson(package, RbxWorldPackageSerializer.ManifestEntryName);
            Assert.AreEqual(2, (int)manifest["format_version"]);
            Assert.AreEqual(2, (int)manifest["minimum_reader_version"]);
            JObject worldJson = EntryJson(package, RbxWorldPackageSerializer.WorldEntryName);
            AssertBlock(worldJson, world.ServerMain.Id, "{\"source_id\":\"server-mod\",\"enabled\":true,\"run_context\":1}");
            AssertBlock(worldJson, world.ClientMain.Id, "{\"source_id\":\"client-mod\",\"enabled\":false,\"run_context\":2}");
            AssertBlock(worldJson, world.SharedModule.Id, "{\"source_id\":\"shared-mod\"}");
            AssertBlock(worldJson, world.LegacyInPart.Id, "{\"source_id\":\"legacy-mod\",\"enabled\":true,\"run_context\":0}");
            AssertBlock(worldJson, world.Unbound.Id, "{\"source_id\":\"\",\"enabled\":true,\"run_context\":0}");
            Assert.IsNull(FindInstance(worldJson, world.Door.Id)["script"], "twin: a Part carries no script block");
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.SerializeWorldThroughDtosForTests(captured),
                EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName),
                "the direct writer and the DTO writer agree byte for byte");

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            Assert.AreEqual(2, read.FormatVersion);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read), "decode -> encode is canonical");
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            try
            {
                Assert.IsFalse(restored.LegacyScriptPlacement, "a version 2 world keeps its script instances");
                RbxBaseScript server = (RbxBaseScript)Restored(restored, world.ServerMain.Id);
                Assert.AreEqual("server-mod", server.SourceId);
                Assert.AreEqual(RunContextValues.Server, server.RunContext);
                RbxBaseScript client = (RbxBaseScript)Restored(restored, world.ClientMain.Id);
                Assert.IsFalse(client.Enabled);
                Assert.AreEqual(RunContextValues.Client, client.RunContext);
                Assert.AreEqual("shared-mod", ((RbxLuaSourceContainer)Restored(restored, world.SharedModule.Id)).SourceId);
                Assert.AreEqual(world.Door.Id, Restored(restored, world.LegacyInPart.Id).Parent.Id,
                    "a Script inside a Part stays inside it");
                Assert.IsFalse(((RbxBaseScript)Restored(restored, world.Unbound.Id)).IsBound);

                RbxWorldPackagePayload recaptured = RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                        restored.Registry, restored.Game, restored.PartSink,
                        WorldPackageTestWorlds.NewSettingsFor(WorldId), null, MemorySources.Of(restored.Mods), CapturedAtUtc),
                    RbxWorldPackageSerializer.ScriptBindingsFormatVersion);
                CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(recaptured),
                    "capture -> package -> restore -> capture is byte-identical");
            }
            finally
            {
                restored.Game.Destroy();
            }
        }

        [Test]
        public void PackageV2_HandWrittenLiteralPackage_IsRead_WithItsBindings()
        {
            byte[] package = LiteralPackage(LiteralManifestJson, LiteralWorldJson, LiteralDoorManifestJson);

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            try
            {
                Assert.AreEqual(2, read.FormatVersion);
                Assert.IsFalse(restored.LegacyScriptPlacement);
                RbxBaseScript door = (RbxBaseScript)Restored(restored, 5UL);
                Assert.AreEqual("door-script", door.SourceId);
                Assert.IsFalse(door.Enabled);
                Assert.AreEqual(RunContextValues.Server, door.RunContext);
                Assert.IsTrue(restored.Registry.TryGetRecord(door.Id, out InstanceRecord record));
                Assert.AreEqual(4L, record.Revision, "the restore counts no change on the script");
                Assert.AreEqual("shared-tools", ((RbxLuaSourceContainer)Restored(restored, 6UL)).SourceId);
                Assert.IsFalse(((RbxBaseScript)Restored(restored, 7UL)).IsBound, "an empty source_id is unbound");
                Assert.AreEqual("server", restored.Mods[0].Manifest.Context);
            }
            finally
            {
                restored.Game.Destroy();
            }
        }

        /// <summary>
        /// Twin: a version 1 capture of a world with script instances writes no script block and the manifest versions
        /// of a 7.56.0 package, and it restores with the legacy placement. Production capture writes this world as
        /// version 2 since MVP4 step C1-4 (rule R4, <c>ScriptWorldPackageEditModeTests</c>), so the capture names the
        /// version.
        /// </summary>
        [Test]
        public void PackageV1_CaptureOfAWorldWithScriptInstances_WritesNoScriptBlock_AndIsMarkedLegacyPlacement()
        {
            ScriptWorld world = ScriptWorld.CreateFull();

            RbxWorldPackagePayload captured = world.Capture(RbxWorldPackageSerializer.LegacyScriptPlacementFormatVersion);
            byte[] package = RbxWorldPackageSerializer.WritePackage(captured);

            Assert.AreEqual(1, captured.FormatVersion);
            Assert.AreEqual(RbxWorldPackageSerializer.CurrentFormatVersion, captured.FormatVersion);
            Assert.AreEqual(2, world.CaptureProduction().FormatVersion,
                "rule R4: production capture writes a world that holds script instances as version 2");
            JObject manifest = EntryJson(package, RbxWorldPackageSerializer.ManifestEntryName);
            Assert.AreEqual(1, (int)manifest["format_version"]);
            Assert.AreEqual(1, (int)manifest["minimum_reader_version"]);
            StringAssert.DoesNotContain("\"script\"",
                Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName)));
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            try
            {
                Assert.IsTrue(restored.LegacyScriptPlacement, "a version 1 package starts every active mod at the legacy placement");
                RbxBaseScript server = (RbxBaseScript)Restored(restored, world.ServerMain.Id);
                Assert.IsFalse(server.IsBound, "version 1 carries no binding");
                Assert.AreEqual(RunContextValues.Legacy, server.RunContext, "nor a RunContext");
            }
            finally
            {
                restored.Game.Destroy();
            }
        }

        // ==================== Reader refusals and their twins ====================

        [Test]
        public void PackageV2_DanglingSourceId_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            AssertWorldRefused(package, json => FindInstance(json, world.ServerMain.Id)["script"]["source_id"] = "nobody",
                "dangling script binding");
        }

        [Test]
        public void PackageV2_BoundSourceId_IsAccepted()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            byte[] rebound = EditWorld(package,
                json => FindInstance(json, world.Unbound.Id)["script"]["source_id"] = "detached-mod");

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(rebound);
            Assert.AreEqual("detached-mod", FindNode(read, world.Unbound.Id).Script.SourceId,
                "a binding to a source the package holds reads (a LocalScript is a client script, and the mod declares none)");
        }

        [Test]
        public void PackageV2_TwoInstancesOneSource_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            AssertWorldRefused(package, json =>
            {
                FindInstance(json, world.ClientMain.Id)["script"]["source_id"] = "server-mod";
                FindInstance(json, world.ClientMain.Id)["script"]["run_context"] = RunContextValues.Server;
            }, "bound to two script instances");
        }

        [Test]
        public void PackageV2_UnboundScripts_AreAccepted()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            byte[] unbound = EditWorld(package, json =>
            {
                FindInstance(json, world.ServerMain.Id)["script"]["source_id"] = "";
                FindInstance(json, world.ClientMain.Id)["script"]["source_id"] = "";
            });

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(unbound);
            Assert.AreEqual(string.Empty, FindNode(read, world.ServerMain.Id).Script.SourceId);
            Assert.AreEqual(string.Empty, FindNode(read, world.ClientMain.Id).Script.SourceId,
                "any number of scripts may be unbound; their sources are then detached");
        }

        [Test]
        public void PackageV2_ContextDisagreesWithInstance_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            AssertWorldRefused(package, json => FindInstance(json, world.ServerMain.Id)["script"]["run_context"] = 2,
                "disagrees with the context of mod 'server-mod'");
            AssertWorldRefused(package, json => FindInstance(json, world.SharedModule.Id)["class_name"] = "Script",
                "Script");
            byte[] serverToModule = ReplaceEntry(package, ModManifestEntry(package, "shared-mod"),
                EntryText(package, ModManifestEntry(package, "shared-mod")).Replace("\"shared\"", "\"server\""));
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(serverToModule));
            StringAssert.Contains("a ModuleScript runs on the side of the script that requires it", refused.Message);
        }

        /// <summary>Twin: agreeing instances read, and a declared server context on a Legacy Script agrees (SC-4).</summary>
        [Test]
        public void PackageV2_ContextAgrees_IsAccepted()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package));
            byte[] legacyServer = EditWorld(package,
                json => FindInstance(json, world.ServerMain.Id)["script"]["run_context"] = 0);
            Assert.AreEqual(RunContextValues.Legacy,
                FindNode(RbxWorldPackageSerializer.ReadPackage(legacyServer), world.ServerMain.Id).Script.RunContext,
                "an imported Legacy Script whose source declares server runs as server");
        }

        [Test]
        public void PackageV2_PluginRunContext_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            AssertWorldRefused(package, json => FindInstance(json, world.LegacyInPart.Id)["script"]["run_context"] = 3,
                "RunContext Plugin");
            AssertWorldRefused(package, json => FindInstance(json, world.LegacyInPart.Id)["script"]["run_context"] = 9,
                "is not an Enum.RunContext value");
        }

        [Test]
        public void PackageV2_LocalScriptWithARunContext_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            AssertWorldRefused(package, json => FindInstance(json, world.Unbound.Id)["script"]["run_context"] = 1,
                "RunContext cannot be used from a LocalScript");
        }

        [Test]
        public void PackageV2_UnknownContextValue_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();
            string entry = ModManifestEntry(package, "server-mod");

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(ReplaceEntry(package, entry,
                    EntryText(package, entry).Replace("\"server\"", "\"clinet\""))));

            StringAssert.Contains("@coreai context 'clinet' is not server, client or shared", refused.Message);
        }

        [Test]
        public void PackageV2_ScriptBlockShape_IsStrict()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();

            AssertWorldRefused(package, json => ((JObject)FindInstance(json, world.Unbound.Id)).Remove("script"),
                "requires a script block");
            AssertWorldRefused(package, json => FindInstance(json, world.SharedModule.Id)["script"]["enabled"] = true,
                "carries Enabled or RunContext");
            AssertWorldRefused(package, json => ((JObject)FindInstance(json, world.ServerMain.Id)["script"]).Remove("enabled"),
                "is missing Enabled or RunContext");
            AssertWorldRefused(package, json => FindInstance(json, world.ServerMain.Id)["script"]["source_id"] = null,
                "source_id");
            AssertWorldRefused(package, json => FindInstance(json, world.ServerMain.Id)["script"]["future"] = 1, "future");
            AssertWorldRefused(package, json => FindInstance(json, world.Door.Id)["script"] = new JObject { ["source_id"] = "" },
                "mismatched Script state");
        }

        [Test]
        public void PackageV1_WithAScriptBlock_IsRefused()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();
            string manifest = EntryText(package, RbxWorldPackageSerializer.ManifestEntryName)
                .Replace("\"format_version\": 2", "\"format_version\": 1")
                .Replace("\"minimum_reader_version\": 2", "\"minimum_reader_version\": 1");

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(
                    ReplaceEntry(package, RbxWorldPackageSerializer.ManifestEntryName, manifest)));

            StringAssert.Contains("format version 1 cannot carry the script block", refused.Message);
        }

        [Test]
        public void Package_UnknownFormatVersion3_IsRefused_AndAVersion2ManifestMustDeclareReaderVersion2()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            byte[] package = world.WriteV2();
            string manifest = EntryText(package, RbxWorldPackageSerializer.ManifestEntryName);

            AssertManifestRefused(package, manifest.Replace("\"format_version\": 2", "\"format_version\": 3"),
                "Unsupported world package format version 3; this reader supports versions 1 and 2.");
            AssertManifestRefused(package,
                manifest.Replace("\"minimum_reader_version\": 2", "\"minimum_reader_version\": 1"),
                "declares minimum_reader_version 1; a format version 2 package declares 2");
            AssertManifestRefused(package,
                manifest.Replace("\"minimum_reader_version\": 2", "\"minimum_reader_version\": 3"),
                "requires reader version 3");
            CollectionAssert.AreEqual(new[] { 1, 2 }, RbxWorldPackageSerializer.SupportedFormatVersions);
        }

        [Test]
        public void Payload_FormatVersion3_IsRefusedByTheWriter()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            RbxWorldPackagePayload captured = world.CaptureProduction();
            RbxWorldPackageOptionalState optional = captured.OptionalState;
            optional.FormatVersion = 3;
            RbxWorldPackagePayload version3 = new(captured.CapturedAtUtc, captured.Settings, captured.Tree,
                captured.Parts, captured.CameraCFrame, captured.Mods, captured.Diagnostics, optional);

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.WritePackage(version3));
            StringAssert.Contains("Unsupported world package format version 3", refused.Message);

            Assert.IsNotNull(RbxWorldPackageSerializer.WritePackage(captured), "twin: the same payload as version 1 writes");
        }

        // ==================== Capture repairs (SC-7) ====================

        [Test]
        public void Capture_ScriptInsideModOwnedSubtree_IsOmittedWithDiagnostic()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            RbxInstance modFolder = world.Registry.Create("Folder", "other-mod", OriginTag.FromMod("other-mod"));
            modFolder.Parent = world.Registry.WorldRoot;
            world.ServerMain.Parent = modFolder;

            RbxWorldPackagePayload captured = world.Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion);

            Assert.IsNull(FindNodeOrNull(captured, world.ServerMain.Id), "the bound script left with the mod's subtree");
            RbxWorldPackageDiagnostic diagnostic = SingleDiagnostic(captured, world.ServerMain.Id);
            Assert.AreEqual("mod-ephemeral", diagnostic.Reason);
            Assert.AreEqual("Script", diagnostic.Member);
            Assert.IsNotNull(FindMod(captured, "server-mod"), "its source stays in the package and loads detached");
            byte[] package = RbxWorldPackageSerializer.WritePackage(captured);
            JArray diagnostics = (JArray)EntryJson(package, RbxWorldPackageSerializer.ManifestEntryName)["diagnostics"];
            Assert.AreEqual("Script", (string)diagnostics[0]["member"]);
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "the repaired package reads");
        }

        [Test]
        public void Capture_ScriptInsideWorldOwnedPart_IsSaved()
        {
            ScriptWorld world = ScriptWorld.CreateFull();

            RbxWorldPackagePayload captured = world.Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion);

            InstanceSnapshot node = FindNodeOrNull(captured, world.LegacyInPart.Id);
            Assert.IsNotNull(node);
            Assert.AreEqual(world.Door.Id.Value, node.ParentId);
            Assert.AreEqual("legacy-mod", node.Script.SourceId);
        }

        /// <summary>
        /// A mod's own legacy script, mod-owned runtime infrastructure until step C1, is left out without a diagnostic:
        /// the mod rebuilds it when it starts.
        /// </summary>
        [Test]
        public void Capture_AModsOwnRuntimeLegacyScript_IsLeftOutSilently()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            RbxInstance folder = world.Registry.Create("Folder", "server-mod", OriginTag.FromMod("server-mod"),
                isRuntimeInfrastructure: true);
            folder.Name = "server-mod";
            RbxBaseScript synthetic = (RbxBaseScript)world.Registry.Create("Script", "server-mod",
                OriginTag.FromMod("server-mod"), isRuntimeInfrastructure: true);
            synthetic.SourceId = "rt-mod";
            synthetic.Parent = folder;
            folder.Parent = world.Game.GetService("ServerScriptService");
            world.ServerMain.SourceId = string.Empty;

            RbxWorldPackagePayload captured = world.Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion);

            Assert.IsNull(FindNodeOrNull(captured, synthetic.Id));
            Assert.IsEmpty(captured.Diagnostics);
        }

        [Test]
        public void Capture_BindingToASourceThePackageDoesNotHold_IsSavedUnboundWithDiagnostic()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            world.ClientMain.SourceId = "gone-mod";

            RbxWorldPackagePayload captured = world.Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion);

            Assert.AreEqual(string.Empty, FindNodeOrNull(captured, world.ClientMain.Id).Script.SourceId);
            RbxWorldPackageDiagnostic diagnostic = SingleDiagnostic(captured, world.ClientMain.Id);
            Assert.AreEqual("missing", diagnostic.Reason);
            Assert.AreEqual("SourceId", diagnostic.Member);
            Assert.AreEqual("gone-mod", world.ClientMain.SourceId, "the live world keeps what it had");
            Assert.AreEqual("server-mod", FindNodeOrNull(captured, world.ServerMain.Id).Script.SourceId,
                "twin: a binding to a held source is kept");
        }

        /// <summary>
        /// Two instances bound to one source exist only through a runtime defect, so a version 2 capture fails on it
        /// instead of saving a world that would start another script; version 1 carries no binding and saves.
        /// </summary>
        [Test]
        public void Capture_TwoInstancesBoundToOneSource_FailsInVersion2_AndSavesInVersion1()
        {
            ScriptWorld world = ScriptWorld.CreateFull();
            world.ClientMain.RunContext = RunContextValues.Server;
            world.ClientMain.SourceId = "server-mod";

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => world.Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion));
            StringAssert.Contains("bound to two script instances", refused.Message);
            RbxWorldPackagePayload version1 = world.Capture(RbxWorldPackageSerializer.LegacyScriptPlacementFormatVersion);
            Assert.IsEmpty(version1.Diagnostics, "version 1 neither repairs nor reports a binding");
            Assert.IsNotNull(RbxWorldPackageSerializer.WritePackage(version1));
        }

        // ==================== Helpers ====================

        private static void AssertBlock(JObject worldJson, InstanceId id, string expected)
        {
            JToken block = FindInstance(worldJson, id)["script"];
            Assert.IsNotNull(block, "instance " + id.Value + " carries a script block");
            Assert.AreEqual(expected, block.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static RbxInstance Restored(RbxWorldPackageRestoreResult restored, InstanceId id)
        {
            return Restored(restored, id.Value);
        }

        private static RbxInstance Restored(RbxWorldPackageRestoreResult restored, ulong id)
        {
            Assert.IsTrue(restored.Registry.TryGet(new InstanceId(id), out RbxInstance instance), "instance " + id);
            return instance;
        }

        private static InstanceSnapshot FindNode(RbxWorldPackagePayload payload, InstanceId id)
        {
            InstanceSnapshot node = FindNodeOrNull(payload, id);
            Assert.IsNotNull(node, "instance " + id.Value + " is in the payload");
            return node;
        }

        private static InstanceSnapshot FindNodeOrNull(RbxWorldPackagePayload payload, InstanceId id)
        {
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.Id == id.Value)
                {
                    return node;
                }
            }

            return null;
        }

        private static RbxWorldModSource FindMod(RbxWorldPackagePayload payload, string id)
        {
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                if (mod.Manifest.Id == id)
                {
                    return mod;
                }
            }

            return null;
        }

        private static RbxWorldPackageDiagnostic SingleDiagnostic(RbxWorldPackagePayload payload, InstanceId id)
        {
            Assert.AreEqual(1, payload.Diagnostics.Count, "one diagnostic");
            Assert.AreEqual(id.Value, payload.Diagnostics[0].ModelId);
            return payload.Diagnostics[0];
        }

        private static JToken FindInstance(JObject world, InstanceId id)
        {
            return FindInstance(world, id.Value);
        }

        private static JToken FindInstance(JObject world, ulong id)
        {
            foreach (JToken node in (JArray)world["instances"])
            {
                if ((string)node["id"] == id.ToString(CultureInfo.InvariantCulture))
                {
                    return node;
                }
            }

            Assert.Fail("instance " + id + " is not in world.json");
            return null;
        }

        private static void AssertWorldRefused(byte[] package, Action<JObject> edit, string expected)
        {
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(EditWorld(package, edit)));
            StringAssert.Contains(expected, refused.Message);
        }

        private static void AssertManifestRefused(byte[] package, string manifest, string expected)
        {
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(
                    ReplaceEntry(package, RbxWorldPackageSerializer.ManifestEntryName, manifest)));
            StringAssert.Contains(expected, refused.Message);
        }

        private static byte[] EditWorld(byte[] package, Action<JObject> edit)
        {
            JObject world = EntryJson(package, RbxWorldPackageSerializer.WorldEntryName);
            edit(world);
            return ReplaceEntry(package, RbxWorldPackageSerializer.WorldEntryName, world.ToString());
        }

        private static string ModManifestEntry(byte[] package, string modId)
        {
            foreach (JToken mod in (JArray)EntryJson(package, RbxWorldPackageSerializer.ManifestEntryName)["mods"])
            {
                if ((string)mod["id"] == modId)
                {
                    return (string)mod["manifest_entry"];
                }
            }

            Assert.Fail("mod " + modId + " is not in the package");
            return null;
        }

        private static byte[] LiteralPackage(string manifest, string world, string doorManifest)
        {
            UTF8Encoding utf8 = new(false, true);
            return WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, utf8.GetBytes(manifest)),
                (RbxWorldPackageSerializer.WorldEntryName, utf8.GetBytes(world)),
                ("Mods/0000/manifest.json", utf8.GetBytes(doorManifest)),
                ("Mods/0000/main.lua", utf8.GetBytes("print('door')")),
                ("Mods/0001/manifest.json", utf8.GetBytes(LiteralToolsManifestJson)),
                ("Mods/0001/main.lua", utf8.GetBytes("return {}")));
        }

        private static JObject EntryJson(byte[] package, string name)
        {
            using StringReader text = new(EntryText(package, name));
            using Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static string EntryText(byte[] package, string name)
        {
            return Encoding.UTF8.GetString(EntryBytes(package, name));
        }

        private static byte[] EntryBytes(byte[] package, string name)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(name).Open();
            using MemoryStream output = new();
            stream.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] ReplaceEntry(byte[] package, string name, string text)
        {
            List<(string Name, byte[] Bytes)> entries = new();
            using (MemoryStream input = new(package, false))
            using (ZipArchive archive = new(input, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    entries.Add((entry.FullName, entry.FullName == name
                        ? new UTF8Encoding(false, true).GetBytes(text)
                        : EntryBytes(package, entry.FullName)));
                }
            }

            return WorldPackageTestWorlds.BuildZip(entries.ToArray());
        }

        /// <summary>
        /// A world with one script of every shape: a server Script in ServerScriptService, a disabled client Script and
        /// a ModuleScript in ReplicatedStorage, a Legacy Script inside a Workspace Part, an unbound LocalScript, and a
        /// source no instance is bound to (<c>detached-mod</c>).
        /// </summary>
        private sealed class ScriptWorld
        {
            private ScriptWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink)
            {
                Registry = registry;
                Game = game;
                Sink = sink;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink Sink { get; }

            public MemorySources Sources { get; } = new();

            public RbxBaseScript ServerMain { get; private set; }

            public RbxBaseScript ClientMain { get; private set; }

            public RbxLuaSourceContainer SharedModule { get; private set; }

            public RbxInstance Door { get; private set; }

            public RbxBaseScript LegacyInPart { get; private set; }

            public RbxBaseScript Unbound { get; private set; }

            public static ScriptWorld CreateFull()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                ScriptWorld world = new(registry, game, new InMemoryPartPropertySink(registry));
                RbxInstance serverScripts = game.GetService("ServerScriptService");
                RbxInstance replicated = game.GetService("ReplicatedStorage");
                world.ServerMain = world.AddScript("Script", "ServerMain", serverScripts, "server-mod", RunContextValues.Server);
                world.ClientMain = world.AddScript("Script", "ClientMain", replicated, "client-mod", RunContextValues.Client);
                world.ClientMain.Enabled = false;
                world.SharedModule = (RbxLuaSourceContainer)registry.Create("ModuleScript", null, OriginTag.FromConsole("1"));
                world.SharedModule.Name = "SharedModule";
                world.SharedModule.SourceId = "shared-mod";
                world.SharedModule.Parent = replicated;
                world.Door = registry.Create("Part", null, OriginTag.FromConsole("1"));
                world.Door.Name = "Door";
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = true;
                world.Sink.SetPartProperties(world.Door.Id, in properties);
                world.Door.Parent = registry.WorldRoot;
                world.LegacyInPart = world.AddScript("Script", "DoorScript", world.Door, "legacy-mod", RunContextValues.Legacy);
                world.Unbound = world.AddScript("LocalScript", "Hud", replicated, string.Empty, RunContextValues.Legacy);
                world.Sources.Add("client-mod", "client");
                world.Sources.Add("detached-mod", "");
                world.Sources.Add("legacy-mod", "");
                world.Sources.Add("server-mod", "server");
                world.Sources.Add("shared-mod", "shared");
                return world;
            }

            private RbxBaseScript AddScript(string className, string name, RbxInstance parent, string sourceId, int runContext)
            {
                RbxBaseScript script = (RbxBaseScript)Registry.Create(className, null, OriginTag.FromConsole("1"));
                script.Name = name;
                script.SourceId = sourceId;
                script.RunContext = runContext;
                script.Parent = parent;
                return script;
            }

            public RbxWorldPackagePayload Capture(int formatVersion)
            {
                return RbxWorldPackageSerializer.Capture(Context(), formatVersion);
            }

            public RbxWorldPackagePayload CaptureProduction()
            {
                return RbxWorldPackageSerializer.Capture(Context());
            }

            public byte[] WriteV2()
            {
                return RbxWorldPackageSerializer.WritePackage(Capture(RbxWorldPackageSerializer.ScriptBindingsFormatVersion));
            }

            private RbxWorldPackageCaptureContext Context()
            {
                return new RbxWorldPackageCaptureContext(Registry, Game, Sink,
                    WorldPackageTestWorlds.NewSettingsFor(WorldId), null, Sources, CapturedAtUtc);
            }
        }

        /// <summary>An in-memory source store that keeps every manifest field, Context included.</summary>
        private sealed class MemorySources : ILuaModSourceStore
        {
            private readonly SortedDictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

            public static MemorySources Of(IReadOnlyList<RbxWorldModSource> mods)
            {
                MemorySources sources = new();
                foreach (RbxWorldModSource mod in mods)
                {
                    sources.Save(mod.Manifest.Id, mod.Source, mod.Manifest);
                }

                return sources;
            }

            public void Add(string id, string context)
            {
                Save(id, "return true", new LuaModManifest { Id = id, Name = id, Active = true, Context = context });
            }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = (source, Copy(manifest));
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (id != null && _entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    source = entry.Source;
                    manifest = Copy(entry.Manifest);
                    return true;
                }

                source = null;
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach ((string Source, LuaModManifest Manifest) entry in _entries.Values)
                {
                    manifests.Add(Copy(entry.Manifest));
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }

            private static LuaModManifest Copy(LuaModManifest manifest)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<LuaModManifest>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(manifest));
            }
        }
    }
}
