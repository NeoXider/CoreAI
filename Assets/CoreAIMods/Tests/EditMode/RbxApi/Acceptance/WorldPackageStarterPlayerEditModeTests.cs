using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The StarterPlayer service's control and camera settings (the optional top-level <c>starter_player</c>
    /// block): saved when any differs from the class defaults, restored exactly, refused when invalid, and absent
    /// from every other package, which keeps loading exactly as before.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageStarterPlayerEditModeTests
    {
        private const string WorldId = "worldpkg-starter";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// A package written by 7.56.0 (literal, no block) loads with the class defaults and re-encodes to the same
        /// <c>world.json</c> text.
        /// </summary>
        private const string OldWorldJson = @"{
  ""schema_version"": 1,
  ""settings"": {
    ""world_id"": ""worldpkg-starter"",
    ""world_acl_version"": null,
    ""meters_per_stud"": 0.28,
    ""gravity_studs_per_second_squared"": 196.2,
    ""signal_behavior"": ""Deferred""
  },
  ""camera_cframe"": null,
  ""instances"": [
    {
      ""id"": ""1"",
      ""parent_id"": ""0"",
      ""class_name"": ""DataModel"",
      ""name"": ""Game"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""1"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": null,
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null
    },
    {
      ""id"": ""2"",
      ""parent_id"": ""1"",
      ""class_name"": ""Workspace"",
      ""name"": ""Workspace"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""1"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": {
        ""primary_part_id"": ""0"",
        ""has_stored_world_pivot"": false,
        ""stored_world_pivot"": null
      },
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null
    },
    {
      ""id"": ""3"",
      ""parent_id"": ""1"",
      ""class_name"": ""StarterPlayer"",
      ""name"": ""StarterPlayer"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""1"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": null,
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null
    }
  ]
}";

        private const string OldManifestJson = @"{
  ""format"": ""coreai-rbx-world"",
  ""format_version"": 1,
  ""minimum_reader_version"": 1,
  ""api_version"": ""MVP2"",
  ""created_utc"": ""2026-11-11T15:00:00.0000000Z"",
  ""world_entry"": ""world.json"",
  ""mods"": []
}";

        /// <summary>Settings that differ from the defaults save, load and save to the same bytes, and restore exactly.</summary>
        [Test]
        public void StarterPlayer_ChangedSettings_RoundTripByteIdentically_AndRestore()
        {
            StarterWorld world = StarterWorld.Create();
            world.StarterPlayer.DevComputerMovementMode = RbxDevComputerMovementMode.Scriptable;
            world.StarterPlayer.CameraMode = RbxCameraMode.LockFirstPerson;
            world.StarterPlayer.CameraMaxZoomDistance = 32.5d;
            world.StarterPlayer.CameraMinZoomDistance = 4d;

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
            JObject block = (JObject)WorldJson(package)["starter_player"];
            Assert.AreEqual("Scriptable", (string)block["dev_computer_movement_mode"]);
            Assert.AreEqual("LockFirstPerson", (string)block["camera_mode"]);
            RbxStarterPlayer back = Restore(read);
            Assert.AreEqual(RbxDevComputerMovementMode.Scriptable, back.DevComputerMovementMode);
            Assert.AreEqual(RbxCameraMode.LockFirstPerson, back.CameraMode);
            Assert.AreEqual(32.5d, back.CameraMaxZoomDistance);
            Assert.AreEqual(4d, back.CameraMinZoomDistance);
        }

        /// <summary>Zoom distances below the default minimum restore exactly, whatever order the clamps would impose.</summary>
        [Test]
        public void StarterPlayer_ZoomBelowTheDefaultMinimum_RestoresExactly()
        {
            StarterWorld world = StarterWorld.Create();
            world.StarterPlayer.CameraMinZoomDistance = 0.1d;
            world.StarterPlayer.CameraMaxZoomDistance = 0.2d;

            RbxStarterPlayer back = Restore(RbxWorldPackageSerializer.ReadPackage(
                RbxWorldPackageSerializer.WritePackage(world.Capture())));

            Assert.AreEqual(0.2d, back.CameraMaxZoomDistance);
            Assert.AreEqual(0.1d, back.CameraMinZoomDistance);
        }

        /// <summary>Twin: a world that kept the defaults writes no block.</summary>
        [Test]
        public void StarterPlayer_DefaultSettings_WriteNoBlock()
        {
            StarterWorld world = StarterWorld.Create();
            RbxWorldPackagePayload payload = world.Capture();

            Assert.IsNull(payload.StarterPlayer);
            Assert.IsNull(WorldJson(RbxWorldPackageSerializer.WritePackage(payload))["starter_player"]);
        }

        /// <summary>Golden: the literal 7.56.0 package loads with the defaults and re-encodes to the same world.json.</summary>
        [Test]
        public void StarterPlayer_OldPackageWithoutTheBlock_LoadsIdentically()
        {
            byte[] old = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, Encoding.UTF8.GetBytes(OldManifestJson)),
                (RbxWorldPackageSerializer.WorldEntryName, Encoding.UTF8.GetBytes(OldWorldJson)));

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(old);

            Assert.IsNull(read.StarterPlayer);
            RbxStarterPlayer back = Restore(read);
            Assert.AreEqual(RbxDevComputerMovementMode.UserChoice, back.DevComputerMovementMode);
            Assert.AreEqual(RbxCameraMode.Classic, back.CameraMode);
            Assert.AreEqual(RbxStarterPlayer.DefaultCameraMaxZoomDistance, back.CameraMaxZoomDistance);
            Assert.AreEqual(RbxStarterPlayer.DefaultCameraMinZoomDistance, back.CameraMinZoomDistance);
            byte[] rewritten = RbxWorldPackageSerializer.WritePackage(read);
            Assert.AreEqual(Normalize(OldWorldJson), Normalize(EntryText(rewritten, RbxWorldPackageSerializer.WorldEntryName)));
            Assert.AreEqual(Normalize(OldManifestJson), Normalize(EntryText(rewritten, RbxWorldPackageSerializer.ManifestEntryName)));
        }

        /// <summary>Invalid blocks are refused, never repaired.</summary>
        [Test]
        public void StarterPlayer_InvalidBlock_IsRefused()
        {
            StarterWorld world = StarterWorld.Create();
            world.StarterPlayer.CameraMode = RbxCameraMode.LockFirstPerson;
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            AssertRefused(package, json => json["starter_player"]["camera_min_zoom_distance"] = 500d, "camera_min_zoom_distance");
            AssertRefused(package, json => json["starter_player"]["camera_min_zoom_distance"] = -1d, "camera_min_zoom_distance");
            AssertRefused(package, json => json["starter_player"]["camera_mode"] = "Orbital", "camera_mode");
            AssertRefused(package, json => json["starter_player"]["dev_computer_movement_mode"] = "3", "dev_computer_movement_mode");
            AssertRefused(package, json =>
            {
                JArray instances = (JArray)json["instances"];
                for (int index = instances.Count - 1; index >= 0; index--)
                {
                    if ((string)instances[index]["class_name"] == "StarterPlayer")
                    {
                        instances.RemoveAt(index);
                    }
                }
            }, "no StarterPlayer service");
        }

        private static RbxStarterPlayer Restore(RbxWorldPackagePayload payload)
        {
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(payload);
            RbxStarterPlayer starterPlayer = (RbxStarterPlayer)restored.Game.FindFirstChildOfClass("StarterPlayer");
            Assert.IsNotNull(starterPlayer);
            return starterPlayer;
        }

        private static void AssertRefused(byte[] package, Action<JObject> edit, string expected)
        {
            JObject world = WorldJson(package);
            edit(world);
            byte[] edited = ReplaceWorldJson(package, world.ToString());
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(edited));
            StringAssert.Contains(expected, refused.Message);
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n");
        }

        private static string EntryText(byte[] package, string name)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(name).Open();
            using StreamReader reader = new(stream, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }

        private static JObject WorldJson(byte[] package)
        {
            using StringReader text = new(EntryText(package, RbxWorldPackageSerializer.WorldEntryName));
            using Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static byte[] ReplaceWorldJson(byte[] package, string world)
        {
            return WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName,
                    Encoding.UTF8.GetBytes(EntryText(package, RbxWorldPackageSerializer.ManifestEntryName))),
                (RbxWorldPackageSerializer.WorldEntryName, Encoding.UTF8.GetBytes(world)));
        }

        private sealed class StarterWorld
        {
            private StarterWorld(InstanceRegistry registry, RbxDataModel game)
            {
                Registry = registry;
                Game = game;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public RbxStarterPlayer StarterPlayer => (RbxStarterPlayer)Game.FindFirstChildOfClass("StarterPlayer");

            public static StarterWorld Create()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                return new StarterWorld(registry, DataModelBootstrap.CreateGame(registry));
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry, Game, new InMemoryPartPropertySink(Registry), WorldPackageTestWorlds.NewSettingsFor(WorldId),
                    capturedAtUtc: CapturedAtUtc));
            }
        }
    }
}
