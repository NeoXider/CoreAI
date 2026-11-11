using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// PointLight properties (the instance's optional <c>light</c> block) and the Lighting service's day cycle (the
    /// optional top-level <c>lighting</c> block) survive a save: written only when there is something to keep,
    /// restored through the classes' own setters, refused when out of range, and absent in every package that
    /// has neither, which therefore stays byte-identical.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageLightingEditModeTests
    {
        private const string WorldId = "worldpkg-lighting";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 14, 0, 0, DateTimeKind.Utc);

        /// <summary>A world with a torch saves, loads and saves to the same bytes, and the torch comes back lit as it was.</summary>
        [Test]
        public void Light_TorchRoundTripsByteIdentically_AndRestoresItsProperties()
        {
            LitWorld world = LitWorld.Create();
            RbxPointLight torch = world.AddTorch();
            torch.SetBrightness(2.5d);
            torch.SetColor(new RbxColor3(1f, 0.6f, 0.2f));
            torch.SetShadows(true);
            torch.SetRange(18d);
            torch.SetEnabled(false);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
            JObject light = (JObject)FindInstance(WorldJson(package), torch.Id.Value)["light"];
            Assert.AreEqual(2.5d, light.Value<double>("brightness"), 0d);
            Assert.AreEqual(18d, light.Value<double>("range"), 0d);
            Assert.IsFalse(light.Value<bool>("enabled"));
            Assert.IsTrue(light.Value<bool>("shadows"));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            Assert.IsTrue(restored.Registry.TryGet(torch.Id, out RbxInstance instance));
            RbxPointLight back = (RbxPointLight)instance;
            Assert.AreEqual(2.5f, back.Brightness);
            Assert.AreEqual(new RbxColor3(1f, 0.6f, 0.2f), back.Color);
            Assert.IsTrue(back.Shadows);
            Assert.IsFalse(back.Enabled);
            Assert.AreEqual(18f, back.Range);
            restored.Game.Destroy();
        }

        /// <summary>
        /// Twin: a world with neither a PointLight nor driven lighting writes neither block, so its world.json is
        /// what the 7.56.0 serializer wrote; and a PointLight node that comes without a block restores with the
        /// class defaults.
        /// </summary>
        [Test]
        public void Light_WorldWithoutLights_WritesNoBlock_AndABareLightNodeRestoresDefaults()
        {
            LitWorld plain = LitWorld.Create();
            RbxWorldPackagePayload payload = plain.Capture();
            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);
            string world = Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName));

            StringAssert.DoesNotContain("\"light\"", world);
            StringAssert.DoesNotContain("\"lighting\"", world);
            Assert.IsNull(payload.Lighting);

            LitWorld lit = LitWorld.Create();
            RbxPointLight torch = lit.AddTorch();
            torch.SetBrightness(7d);
            byte[] withLight = RbxWorldPackageSerializer.WritePackage(lit.Capture());
            JObject worldJson = WorldJson(withLight);
            ((JObject)FindInstance(worldJson, torch.Id.Value)).Remove("light");
            byte[] bare = ReplaceEntry(withLight, RbxWorldPackageSerializer.WorldEntryName, worldJson.ToString());

            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(RbxWorldPackageSerializer.ReadPackage(bare));
            Assert.IsTrue(restored.Registry.TryGet(torch.Id, out RbxInstance instance));
            Assert.AreEqual(1f, ((RbxPointLight)instance).Brightness);
            Assert.AreEqual(RbxPointLight.DefaultRange, ((RbxPointLight)instance).Range);
            restored.Game.Destroy();
        }

        /// <summary>
        /// A world that drives its lighting saves the day cycle and restores it, and the restored world drives its
        /// lighting; TimeOfDay is derived, not stored.
        /// </summary>
        [Test]
        public void Lighting_DrivenWorld_SavesAndRestoresTheDayCycle()
        {
            LitWorld world = LitWorld.Create();
            world.Lighting.SetClockTime(6.5d);
            world.Lighting.SetGeographicLatitude(-12.25d);
            world.Lighting.SetBrightness(2d);
            world.Lighting.SetAmbient(new RbxColor3(0.1f, 0.2f, 0.3f));
            world.Lighting.SetOutdoorAmbient(new RbxColor3(0.4f, 0.5f, 0.6f));

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            JObject lighting = (JObject)WorldJson(package)["lighting"];

            CollectionAssert.AreEquivalent(
                new[] { "clock_time", "geographic_latitude", "brightness", "ambient", "outdoor_ambient" },
                PropertyNames(lighting));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(RbxWorldPackageSerializer.ReadPackage(package));
            RbxLighting back = (RbxLighting)restored.Game.FindFirstChildOfClass("Lighting");
            Assert.IsTrue(back.IsDriven);
            Assert.AreEqual(6.5f, back.ClockTime);
            Assert.AreEqual("06:30:00", back.TimeOfDay);
            Assert.AreEqual(-12.25f, back.GeographicLatitude);
            Assert.AreEqual(2f, back.Brightness);
            Assert.AreEqual(new RbxColor3(0.1f, 0.2f, 0.3f), back.Ambient);
            Assert.AreEqual(new RbxColor3(0.4f, 0.5f, 0.6f), back.OutdoorAmbient);
            restored.Game.Destroy();
        }

        /// <summary>Twin: a world that never touched Lighting writes no block and restores an undriven Lighting at the class defaults.</summary>
        [Test]
        public void Lighting_UndrivenWorld_WritesNoBlock_AndRestoresTheDefaultsUndriven()
        {
            LitWorld world = LitWorld.Create();

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            Assert.IsNull(WorldJson(package)["lighting"]);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(RbxWorldPackageSerializer.ReadPackage(package));
            RbxLighting back = (RbxLighting)restored.Game.FindFirstChildOfClass("Lighting");
            Assert.IsFalse(back.IsDriven);
            Assert.AreEqual(RbxLighting.DefaultClockTime, back.ClockTime);
            restored.Game.Destroy();
        }

        /// <summary>Blocks the reader cannot take as they are are refused, never repaired.</summary>
        [Test]
        public void Blocks_OutOfRangeOrMisplaced_AreRefused()
        {
            LitWorld world = LitWorld.Create();
            RbxPointLight torch = world.AddTorch();
            world.Lighting.SetClockTime(8d);
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            AssertRefused(package, json => FindInstance(json, torch.Id.Value)["light"]["range"] = 61d, "range within");
            AssertRefused(package, json => json["lighting"]["clock_time"] = 24d, "clock_time");
            AssertRefused(package, json =>
            {
                JObject marker = FindInstance(json, world.Marker.Id.Value);
                marker["light"] = FindInstance(json, torch.Id.Value)["light"].DeepClone();
            }, "not a PointLight");
            AssertRefused(package, json =>
            {
                JArray instances = (JArray)json["instances"];
                for (int index = instances.Count - 1; index >= 0; index--)
                {
                    if ((string)instances[index]["class_name"] == "Lighting")
                    {
                        instances.RemoveAt(index);
                    }
                }
            }, "no Lighting service");
        }

        /// <summary>
        /// How a reader treats a key it does not know, which is what every reader up to 7.56.0 does with
        /// <c>lighting</c>, <c>light</c> and a new manifest key: it refuses the package and names the key
        /// (<c>MissingMemberHandling.Error</c> on every package DTO since MVP3), never loading it without the block.
        /// </summary>
        [Test]
        public void UnknownKeys_InWorldJsonAnInstanceOrTheManifest_AreRefusedByName()
        {
            LitWorld world = LitWorld.Create();
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            string manifest = Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.ManifestEntryName));

            AssertRefused(package, json => json["future_block"] = new JObject(), "future_block");
            AssertRefused(package, json => FindInstance(json, world.Marker.Id.Value)["future_member"] = 1, "future_member");
            JObject manifestJson = JObject.Parse(manifest);
            manifestJson["future_key"] = "x";
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() => RbxWorldPackageSerializer.ReadPackage(
                ReplaceEntry(package, RbxWorldPackageSerializer.ManifestEntryName, manifestJson.ToString())));
            StringAssert.Contains("future_key", refused.Message);
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "twin: the package as written reads");
        }

        private static void AssertRefused(byte[] package, Action<JObject> edit, string expected)
        {
            JObject world = WorldJson(package);
            edit(world);
            byte[] edited = ReplaceEntry(package, RbxWorldPackageSerializer.WorldEntryName, world.ToString());
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(edited));
            StringAssert.Contains(expected, refused.Message);
        }

        private static JObject FindInstance(JObject world, ulong id)
        {
            foreach (JToken node in (JArray)world["instances"])
            {
                if ((string)node["id"] == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    return (JObject)node;
                }
            }

            Assert.Fail("instance " + id + " is not in world.json");
            return null;
        }

        private static string[] PropertyNames(JObject value)
        {
            System.Collections.Generic.List<string> names = new();
            foreach (JProperty property in value.Properties())
            {
                names.Add(property.Name);
            }

            return names.ToArray();
        }

        private static JObject WorldJson(byte[] package)
        {
            using StringReader text = new(Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName)));
            using Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            return JObject.Load(reader);
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
            System.Collections.Generic.List<(string Name, byte[] Bytes)> entries = new();
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

        private sealed class LitWorld
        {
            private LitWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink, RbxInstance marker)
            {
                Registry = registry;
                Game = game;
                Sink = sink;
                Marker = marker;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink Sink { get; }

            /// <summary>An anchored Part the torches hang on.</summary>
            public RbxInstance Marker { get; }

            public RbxLighting Lighting => (RbxLighting)Game.FindFirstChildOfClass("Lighting");

            public static LitWorld Create()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                InMemoryPartPropertySink sink = new(registry);
                RbxInstance marker = registry.Create("Part");
                marker.Name = "Wall";
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = true;
                sink.SetPartProperties(marker.Id, in properties);
                marker.Parent = registry.WorldRoot;
                return new LitWorld(registry, game, sink, marker);
            }

            public RbxPointLight AddTorch()
            {
                RbxPointLight torch = (RbxPointLight)Registry.Create("PointLight");
                torch.Name = "Torch";
                torch.Parent = Marker;
                return torch;
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry, Game, Sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: CapturedAtUtc));
            }
        }
    }
}
