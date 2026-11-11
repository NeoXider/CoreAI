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
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// <c>Lighting.EnvironmentDiffuseScale</c> and <c>EnvironmentSpecularScale</c> in a saved world: the <c>lighting</c>
    /// block carries each one only when it differs from its default of 1, so a block whose scales were never changed is
    /// written exactly as before they existed, a block without them reads 1, a value outside [0, 1] is refused, and an
    /// AI edit that changes only a scale is an undoable change whose undo puts the saved scale back.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageEnvironmentScalesEditModeTests
    {
        private const string WorldId = "worldpkg-environment-scales";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);

        private static readonly string[] BlockKeysBeforeTheScales =
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
        public void EnvironmentScales_RoundTripByteIdentically_AndRestore()
        {
            ScaleWorld world = ScaleWorld.Create();
            world.Lighting.SetEnvironmentDiffuseScale(0.25d);
            world.Lighting.SetEnvironmentSpecularScale(0d);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);

            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
            JObject lighting = (JObject)WorldJson(package)["lighting"];
            Assert.AreEqual(0.25d, lighting.Value<double>("environment_diffuse_scale"), 0d);
            Assert.AreEqual(0d, lighting.Value<double>("environment_specular_scale"), 0d);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            RbxLighting back = (RbxLighting)restored.Game.FindFirstChildOfClass("Lighting");
            Assert.AreEqual(0.25f, back.EnvironmentDiffuseScale);
            Assert.AreEqual(0f, back.EnvironmentSpecularScale);
            restored.Game.Destroy();
        }

        /// <summary>
        /// Twin: a driven block whose scales are at their default writes only the keys it had before the scales
        /// existed, and a block without the two keys reads 1 and 1.
        /// </summary>
        [Test]
        public void EnvironmentScales_AtTheirDefault_WriteNoKey_AndABlockWithoutThemReadsOne()
        {
            ScaleWorld world = ScaleWorld.Create();
            world.Lighting.SetClockTime(8d);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            CollectionAssert.AreEqual(BlockKeysBeforeTheScales, PropertyNames((JObject)WorldJson(package)["lighting"]),
                "the block is what it was before the scales existed, key for key and in order");
            ScaleWorld changed = ScaleWorld.Create();
            changed.Lighting.SetEnvironmentDiffuseScale(0.25d);
            changed.Lighting.SetEnvironmentSpecularScale(0d);
            JObject withoutKeys = WorldJson(RbxWorldPackageSerializer.WritePackage(changed.Capture()));
            ((JObject)withoutKeys["lighting"]).Remove("environment_diffuse_scale");
            ((JObject)withoutKeys["lighting"]).Remove("environment_specular_scale");
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(
                ReplaceEntry(package, RbxWorldPackageSerializer.WorldEntryName, withoutKeys.ToString()));
            Assert.AreEqual(1f, read.Lighting.Value.EnvironmentDiffuseScale);
            Assert.AreEqual(1f, read.Lighting.Value.EnvironmentSpecularScale);
        }

        [Test]
        public void EnvironmentScales_OutsideTheUnitRange_AreRefused()
        {
            ScaleWorld world = ScaleWorld.Create();
            world.Lighting.SetEnvironmentDiffuseScale(0.5d);
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            AssertRefused(package, json => json["lighting"]["environment_diffuse_scale"] = 1.5d);
            AssertRefused(package, json => json["lighting"]["environment_specular_scale"] = -0.25d);
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "twin: the package as written reads");
        }

        /// <summary>
        /// "Dim the sky light" changes nothing but a scale and is one AI change each; the undo of the second puts back
        /// the scale the first one saved, read from the encoded pre-mutation backup.
        /// </summary>
        [Test]
        public async Task Undo_AnAiEditOfAnEnvironmentScaleOnly_IsRecorded_AndUndoneToTheSavedScale()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore());
            ActorContext builder = new LocalActorIdentityProvider("builder").GetActorContext(BuiltInAgentRoleIds.Programmer);

            LuaTool.LuaResult first = await session.ExecuteAsAsync(builder,
                "game:GetService('Lighting').EnvironmentDiffuseScale = 0.25 return true");
            Assert.IsTrue(first.Success, first.Error);
            await session.Gate.WaitForPendingBackupAsync();
            LuaTool.LuaResult second = await session.ExecuteAsAsync(builder,
                "game:GetService('Lighting').EnvironmentDiffuseScale = 0.5 return true");
            Assert.IsTrue(second.Success, second.Error);
            await session.Gate.WaitForPendingBackupAsync();

            Assert.AreEqual(0.5f, LightingOf(session).EnvironmentDiffuseScale);
            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.AreEqual(2, status.UndoCount, "each scale-only edit is an AI change");
            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreEqual(0.25f, LightingOf(session).EnvironmentDiffuseScale, "the backup kept the first scale");
            undone = await session.Undo.UndoAsync(builder);
            Assert.IsTrue(undone.Success, undone.Message);
            Assert.AreEqual(RbxLighting.DefaultEnvironmentDiffuseScale, LightingOf(session).EnvironmentDiffuseScale);
            await session.Gate.WaitForPendingBackupAsync();
        }

        private static RbxLighting LightingOf(P3HeadlessSession session)
        {
            return (RbxLighting)session.Controller.CurrentRbxApi.Registry.WorldRoot.Parent.FindFirstChildOfClass("Lighting");
        }

        private static void AssertRefused(byte[] package, Action<JObject> edit)
        {
            JObject world = WorldJson(package);
            edit(world);
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() =>
                RbxWorldPackageSerializer.ReadPackage(
                    ReplaceEntry(package, RbxWorldPackageSerializer.WorldEntryName, world.ToString())));
            StringAssert.Contains("environment scales within [0, 1]", refused.Message);
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

        private sealed class ScaleWorld
        {
            private ScaleWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink)
            {
                Registry = registry;
                Game = game;
                Sink = sink;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink Sink { get; }

            public RbxLighting Lighting => (RbxLighting)Game.FindFirstChildOfClass("Lighting");

            public static ScaleWorld Create()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                return new ScaleWorld(registry, game, new InMemoryPartPropertySink(registry));
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry, Game, Sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: CapturedAtUtc));
            }
        }
    }
}
