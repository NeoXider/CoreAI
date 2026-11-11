using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// <c>Lighting.FogStart</c>, <c>FogEnd</c> and <c>FogColor</c> in a saved world: the <c>lighting</c> block carries
    /// <c>fog_start</c>, <c>fog_end</c> and <c>fog_color</c> each only when it differs from the class default (0, 100000,
    /// grey 0.75), so a block whose fog was never set is written exactly as before the fog members existed, a block
    /// without them reads the defaults, a non-finite value or a malformed colour is refused, and an AI edit that changes
    /// only the fog is an undoable change. Each behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageFogEditModeTests
    {
        private const string WorldId = "worldpkg-fog";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);

        private static readonly string[] BlockKeysBeforeTheFog =
        {
            "clock_time", "geographic_latitude", "brightness", "ambient", "outdoor_ambient"
        };

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
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void Fog_RoundTripsByteIdentically_AndRestores()
        {
            FogWorld world = FogWorld.Create();
            world.Lighting.SetFogStart(150d);
            world.Lighting.SetFogEnd(600d);
            world.Lighting.SetFogColor(RbxColor3.FromRGB(196f, 214f, 214f));

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
            JObject lighting = (JObject)WorldJson(package)["lighting"];
            Assert.AreEqual(150d, lighting.Value<double>("fog_start"), 0d);
            Assert.AreEqual(600d, lighting.Value<double>("fog_end"), 0d);
            JArray color = (JArray)lighting["fog_color"];
            Assert.AreEqual(3, color.Count);
            Assert.AreEqual(196f / 255f, color[0].Value<float>(), 1e-6f);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            RbxLighting back = (RbxLighting)restored.Game.FindFirstChildOfClass("Lighting");
            Assert.AreEqual(150f, back.FogStart);
            Assert.AreEqual(600f, back.FogEnd);
            Assert.AreEqual(RbxColor3.FromRGB(196f, 214f, 214f), back.FogColor);
            Assert.IsTrue(back.IsDriven, "a restored fog drives the world's lighting");
            restored.Game.Destroy();
        }

        /// <summary>
        /// The direct <c>world.json</c> writer and the reader's DTO graph agree on the fog keys byte for byte, with every
        /// fog member set and with only the colour set, and the size bound still covers the written bytes.
        /// </summary>
        [Test]
        public void Fog_DirectWriter_MatchesTheDtoSerializer_AndTheBoundCoversIt()
        {
            FogWorld all = FogWorld.Create();
            all.Lighting.SetFogStart(-0.5d);
            all.Lighting.SetFogEnd(3.4028234663852886E+38d);
            all.Lighting.SetFogColor(new RbxColor3(0.2f, -0f, 1f));
            FogWorld colourOnly = FogWorld.Create();
            colourOnly.Lighting.SetFogColor(new RbxColor3(0.333333343f, 0.5f, 0.25f));

            foreach (RbxWorldPackagePayload payload in new[] { all.Capture(), colourOnly.Capture() })
            {
                byte[] direct;
                using (MemoryStream stream = RbxWorldPackageWorldJson.Write(payload, 0L))
                {
                    direct = stream.ToArray();
                }

                CollectionAssert.AreEqual(RbxWorldPackageSerializer.SerializeWorldThroughDtosForTests(payload), direct);
                Assert.AreEqual(direct.LongLength, RbxWorldPackageWorldJson.MeasureExact(payload));
                Assert.GreaterOrEqual(RbxWorldPackageWorldJson.MeasureUpperBound(payload), direct.LongLength);
            }
        }

        /// <summary>
        /// Twin: a driven block whose fog is at its defaults writes only the keys it had before the fog members existed,
        /// one changed member writes only its own key, and a block without the keys reads the defaults.
        /// </summary>
        [Test]
        public void Fog_AtItsDefaults_WritesNoKey_AndABlockWithoutTheKeysReadsTheDefaults()
        {
            FogWorld world = FogWorld.Create();
            world.Lighting.SetClockTime(8d);
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            CollectionAssert.AreEqual(BlockKeysBeforeTheFog, PropertyNames((JObject)WorldJson(package)["lighting"]),
                "the block is what it was before the fog existed, key for key and in order");

            FogWorld colourOnly = FogWorld.Create();
            colourOnly.Lighting.SetFogColor(new RbxColor3(0.2f, 0.3f, 0.4f));
            string[] colourKeys = PropertyNames((JObject)WorldJson(
                RbxWorldPackageSerializer.WritePackage(colourOnly.Capture()))["lighting"]);
            CollectionAssert.Contains(colourKeys, "fog_color");
            CollectionAssert.DoesNotContain(colourKeys, "fog_start", "an unchanged FogStart writes no key");
            CollectionAssert.DoesNotContain(colourKeys, "fog_end", "an unchanged FogEnd writes no key");

            FogWorld changed = FogWorld.Create();
            changed.Lighting.SetFogStart(150d);
            changed.Lighting.SetFogEnd(600d);
            changed.Lighting.SetFogColor(new RbxColor3(0.2f, 0.3f, 0.4f));
            JObject withoutKeys = WorldJson(RbxWorldPackageSerializer.WritePackage(changed.Capture()));
            ((JObject)withoutKeys["lighting"]).Remove("fog_start");
            ((JObject)withoutKeys["lighting"]).Remove("fog_end");
            ((JObject)withoutKeys["lighting"]).Remove("fog_color");
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(
                ReplaceEntry(package, RbxWorldPackageSerializer.WorldEntryName, withoutKeys.ToString()));
            Assert.AreEqual(RbxLighting.DefaultFogStart, read.Lighting.Value.FogStart);
            Assert.AreEqual(RbxLighting.DefaultFogEnd, read.Lighting.Value.FogEnd);
            Assert.AreEqual(RbxLighting.DefaultFogColor, read.Lighting.Value.FogColor);
        }

        [Test]
        public void Fog_NonFiniteValuesAndAMalformedColour_AreRefused()
        {
            FogWorld world = FogWorld.Create();
            world.Lighting.SetFogEnd(600d);
            world.Lighting.SetFogColor(new RbxColor3(0.2f, 0.3f, 0.4f));
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            AssertRefused(package, json => json["lighting"]["fog_end"] = 1e39d, "needs finite values");
            AssertRefused(package, json => json["lighting"]["fog_start"] = -1e39d, "needs finite values");
            AssertRefused(package, json => json["lighting"]["fog_color"] = new JArray(0.2d, 1e39d, 0.4d),
                "needs finite values");
            AssertRefused(package, json => json["lighting"]["fog_color"] = new JArray(0.2d, 0.3d),
                "lighting.fog_color requires 3 finite components");
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "twin: the package as written reads");
        }

        /// <summary>
        /// Twin of the refusals: the mirror states no range for the fog distances, so a negative FogStart and a FogEnd
        /// below it are written, read and restored as set.
        /// </summary>
        [Test]
        public void Fog_UnorderedOrNegativeDistances_RoundTripAsSet()
        {
            FogWorld world = FogWorld.Create();
            world.Lighting.SetFogStart(-20d);
            world.Lighting.SetFogEnd(-40d);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            Assert.AreEqual(-20f, read.Lighting.Value.FogStart);
            Assert.AreEqual(-40f, read.Lighting.Value.FogEnd);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
        }

        /// <summary>
        /// "Make it foggy" changes nothing but the fog and is one AI change each; the undo of the second puts back the
        /// fog the first one saved, read from the encoded pre-mutation backup.
        /// </summary>
        [Test]
        public async Task Undo_AnAiEditOfTheFogOnly_IsRecorded_AndUndoneToTheSavedFog()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = new LocalActorIdentityProvider("builder").GetActorContext(BuiltInAgentRoleIds.Programmer);

            LuaTool.LuaResult first = await session.ExecuteAsAsync(builder,
                "game:GetService('Lighting').FogEnd = 300 return true");
            Assert.IsTrue(first.Success, first.Error);
            await session.Gate.WaitForPendingBackupAsync();
            LuaTool.LuaResult second = await session.ExecuteAsAsync(builder,
                "local L = game:GetService('Lighting') L.FogEnd = 600 L.FogColor = Color3.new(1, 0, 0) return true");
            Assert.IsTrue(second.Success, second.Error);
            await session.Gate.WaitForPendingBackupAsync();

            Assert.AreEqual(600f, LightingOf(session).FogEnd);
            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.AreEqual(2, status.UndoCount, "each fog-only edit is an AI change");
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreEqual(300f, LightingOf(session).FogEnd, "the backup kept the first fog");
            Assert.AreEqual(RbxLighting.DefaultFogColor, LightingOf(session).FogColor);
            undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreEqual(RbxLighting.DefaultFogEnd, LightingOf(session).FogEnd);
            await session.Gate.WaitForPendingBackupAsync();
        }

        /// <summary>
        /// The undo mark: a fog write moves <see cref="RbxWorldStateMark.Values"/> (a Lighting write leaves the revisions
        /// alone, so without the fog in the values a fog-only edit would be invisible); twin: an equal write moves nothing.
        /// </summary>
        [Test]
        public void StateMark_AFogWriteMovesTheValues_AndAnEqualWriteDoesNot()
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxLighting lighting = (RbxLighting)game.FindFirstChildOfClass("Lighting");
            lighting.SetFogEnd(600d);
            RbxWorldStateMark start = RbxWorldStateMark.Of(registry, 0);

            lighting.SetFogEnd(600d);
            RbxWorldStateMark equal = RbxWorldStateMark.Of(registry, 0);
            lighting.SetFogStart(10d);
            RbxWorldStateMark started = RbxWorldStateMark.Of(registry, 0);
            lighting.SetFogColor(new RbxColor3(1f, 0f, 0f));
            RbxWorldStateMark coloured = RbxWorldStateMark.Of(registry, 0);

            Assert.AreEqual(start, equal, "an equal write is not a change");
            Assert.AreNotEqual(start.Values, started.Values, "FogStart is folded into the mark");
            Assert.AreNotEqual(started.Values, coloured.Values, "FogColor is folded into the mark");
            game.Destroy();
        }

        private static RbxLighting LightingOf(P3HeadlessSession session)
        {
            return (RbxLighting)session.Controller.CurrentRbxApi.Registry.WorldRoot.Parent.FindFirstChildOfClass("Lighting");
        }

        private static void AssertRefused(byte[] package, Action<JObject> edit, string expected)
        {
            JObject world = WorldJson(package);
            edit(world);
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(
                    ReplaceEntry(package, RbxWorldPackageSerializer.WorldEntryName, world.ToString())));
            StringAssert.Contains(expected, refused.Message);
        }

        private static string[] PropertyNames(JObject value)
        {
            List<string> names = new();
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

        private sealed class FogWorld
        {
            private FogWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink)
            {
                Registry = registry;
                Game = game;
                Sink = sink;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink Sink { get; }

            public RbxLighting Lighting => (RbxLighting)Game.FindFirstChildOfClass("Lighting");

            public static FogWorld Create()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                return new FogWorld(registry, game, new InMemoryPartPropertySink(registry));
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry, Game, Sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: CapturedAtUtc));
            }
        }
    }
}
