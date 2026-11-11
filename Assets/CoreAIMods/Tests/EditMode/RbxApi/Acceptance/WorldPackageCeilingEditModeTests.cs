using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// A world at the desktop instance ceiling can be backed up: the format's entry limit fits it with headroom,
    /// the limits stay decompression-bomb bounds, and every package within the limits readers up to 7.56.0
    /// enforce is still the package they read.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageCeilingEditModeTests
    {
        /// <summary>The desktop registered-instance ceiling (<c>LuaCsModRuntime.EmergencyMaxRegisteredInstances</c>).</summary>
        private const int DesktopInstanceCeiling = 16384;

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The heaviest realistic world at the ceiling (every charged instance a rotated, coloured Part with eight
        /// attributes and four tags, built by AI console calls) encodes within the new limits with a quarter of
        /// the entry limit to spare, reads back identically, is proven encodable before an edit, and is refused
        /// by a reader held to the 7.56.0 limits by its size alone.
        /// </summary>
        [Test]
        public void Ceiling_HeaviestRealisticWorldAtTheDesktopCeiling_IsBackedUpWithHeadroom_AndReadsBack()
        {
            RbxWorldPackagePayload payload = WorldPackageTestWorlds.CaptureHeavyWorld(DesktopInstanceCeiling, 8, 4, CapturedAtUtc);

            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            long worldJsonBytes = EntryLength(package, RbxWorldPackageSerializer.WorldEntryName);
            Assert.Greater(worldJsonBytes, (long)RbxWorldPackageSerializer.Version1BaselineMaximumEntryBytes,
                "precondition: this world needed the raised limit");
            Assert.LessOrEqual(worldJsonBytes, RbxWorldPackageSerializer.MaximumEntryBytes * 3L / 4L,
                "the heaviest realistic world at the ceiling keeps at least a quarter of the entry limit free");
            Assert.IsTrue(
                RbxWorldPackageSerializer.TryProveEncodable(payload, RbxWorldPackageLimits.Format, out long packageBound),
                "the ceiling world is proven encodable, so its autosave can be encoded behind an edit");
            Assert.GreaterOrEqual(packageBound, package.LongLength);

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            Assert.AreEqual(payload.Tree.Instances.Count, read.Tree.Instances.Count);
            Assert.AreEqual(payload.Parts.Count, read.Parts.Count);
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.ComputeEntryDigest(payload),
                RbxWorldPackageSerializer.ComputeEntryDigest(read),
                "the world read back encodes to the same entries");

            RbxWorldPackageException baseline = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(package, RbxWorldPackageLimits.Version1Baseline));
            StringAssert.Contains("'world.json' is " + worldJsonBytes.ToString(CultureInfo.InvariantCulture), baseline.Message);
            StringAssert.Contains("limit is 16777216", baseline.Message);
        }

        /// <summary>
        /// Negative twin: a world whose <c>world.json</c> passes the new entry limit is refused by the writer, the
        /// proof does not claim it, and the store's write-behind preparation reports it as unencodable, which is
        /// what refuses the AI edit before it runs.
        /// </summary>
        [Test]
        public async Task Ceiling_WorldJsonAboveTheNewEntryLimit_IsRefusedAndKnownBeforeTheEdit()
        {
            RbxWorldPackagePayload payload = WorldPackageTestWorlds.CaptureLongStringValues(340, CapturedAtUtc);

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.WritePackage(payload));
            StringAssert.Contains("'world.json' is ", refused.Message);
            StringAssert.Contains("limit is " + RbxWorldPackageSerializer.MaximumEntryBytes.ToString(CultureInfo.InvariantCulture),
                refused.Message);
            Assert.IsFalse(RbxWorldPackageSerializer.TryProveEncodable(payload, RbxWorldPackageLimits.Format, out long _));

            string root = WorldPackageTestWorlds.NewTemporaryDirectory();
            try
            {
                FileRbxWorldPackageStore store = new(root, persistenceSyncAsync: _ => UniTask.FromResult(true));
                RbxPreparedAutoSave prepared = await store.PrepareAutoAsync("execute_lua", payload);
                Assert.IsFalse(prepared.Success);
                StringAssert.Contains("'world.json'", prepared.Error);
                Assert.IsTrue(prepared.ToFailedWrite().PackageCannotBeEncoded);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// A package whose <c>world.json</c> declares one byte more than the entry limit is refused from its header,
        /// before a byte of it is inflated.
        /// </summary>
        [Test]
        public void Ceiling_ReaderRefusesAWorldJsonDeclaredAboveTheEntryLimit()
        {
            string manifest = WorldPackageTestWorlds.SmallPackageEntryText(RbxWorldPackageSerializer.ManifestEntryName, CapturedAtUtc);
            byte[] package = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, Encoding.UTF8.GetBytes(manifest)),
                (RbxWorldPackageSerializer.WorldEntryName, new byte[RbxWorldPackageSerializer.MaximumEntryBytes + 1]));

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(package));

            StringAssert.Contains(
                "'world.json' is " + (RbxWorldPackageSerializer.MaximumEntryBytes + 1).ToString(CultureInfo.InvariantCulture)
                + " bytes; limit is " + RbxWorldPackageSerializer.MaximumEntryBytes.ToString(CultureInfo.InvariantCulture),
                refused.Message);
        }

        /// <summary>
        /// An entry whose ZIP header understates its size is refused once it inflates past the declared size, so the
        /// expanded-total limit, which is summed from those headers, still bounds what a reader inflates.
        /// </summary>
        [Test]
        public void Ceiling_EntryInflatingPastItsDeclaredSize_IsRefused()
        {
            byte[] package = RbxWorldPackageSerializer.WritePackage(WorldPackageTestWorlds.CaptureHeavyWorld(64, 2, 2, CapturedAtUtc));
            long worldJsonBytes = EntryLength(package, RbxWorldPackageSerializer.WorldEntryName);
            byte[] lying = WorldPackageTestWorlds.WithDeclaredUncompressedSize(
                package, RbxWorldPackageSerializer.WorldEntryName, (uint)(worldJsonBytes / 2L));

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(lying));

            Assert.That(
                refused.Message,
                Does.Contain("declares").Or.Contain("could not be read").Or.Contain("invalid JSON"),
                "refused by the inflated size, or by the runtime's own ZIP checks first");
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "twin: the honest package reads");
        }

        /// <summary>
        /// A package within the limits of every reader up to 7.56.0 is read the same by a reader held to those limits
        /// and by this one, and its <c>world.json</c> is byte for byte what the 7.56.0 writer (the DTO serializer)
        /// wrote, so those readers see nothing new.
        /// </summary>
        [Test]
        public void Ceiling_PackageWithinTheBaselineLimits_IsTheOneOldReadersRead()
        {
            RbxWorldPackagePayload payload = WorldPackageTestWorlds.CaptureHeavyWorld(512, 4, 2, CapturedAtUtc);

            byte[] package = RbxWorldPackageSerializer.WritePackage(payload);

            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.SerializeWorldThroughDtosForTests(payload),
                EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName));
            RbxWorldPackagePayload baseline = RbxWorldPackageSerializer.ReadPackage(package, RbxWorldPackageLimits.Version1Baseline);
            RbxWorldPackagePayload current = RbxWorldPackageSerializer.ReadPackage(package);
            CollectionAssert.AreEqual(
                RbxWorldPackageSerializer.WritePackage(current),
                RbxWorldPackageSerializer.WritePackage(baseline));
        }

        /// <summary>The presets carry the measured limits; the WebGL ones keep today's browser decode memory.</summary>
        [Test]
        public void Limits_PresetsCarryTheFormatTheBaselineAndTheWebGlBudgets()
        {
            Assert.AreEqual(64 * 1024 * 1024, RbxWorldPackageLimits.Format.MaximumEntryBytes);
            Assert.AreEqual(128 * 1024 * 1024, RbxWorldPackageLimits.Format.MaximumExpandedPackageBytes);
            Assert.AreEqual(64 * 1024 * 1024, RbxWorldPackageLimits.Format.MaximumPackageBytes);
            Assert.AreEqual(2048, RbxWorldPackageLimits.Format.MaximumEntries);
            Assert.AreEqual(16 * 1024 * 1024, RbxWorldPackageLimits.Version1Baseline.MaximumEntryBytes);
            Assert.AreEqual(RbxWorldPackageLimits.Format.MaximumExpandedPackageBytes,
                RbxWorldPackageLimits.Version1Baseline.MaximumExpandedPackageBytes);
            Assert.AreEqual(4 * 1024 * 1024, RbxWorldPackageLimits.WebGl.MaximumPackageBytes);
            Assert.AreEqual(16 * 1024 * 1024, RbxWorldPackageLimits.WebGl.MaximumEntryBytes);
        }

        /// <summary>A host may hold a reader or writer to lower limits, never to higher ones than the format's.</summary>
        [Test]
        public void Limits_CannotBeRaisedAboveTheFormat_ButMayBeLowered()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RbxWorldPackageLimits(
                RbxWorldPackageSerializer.MaximumPackageBytes,
                RbxWorldPackageSerializer.MaximumEntryBytes + 1,
                RbxWorldPackageSerializer.MaximumExpandedPackageBytes,
                RbxWorldPackageSerializer.MaximumEntries));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RbxWorldPackageLimits(1024, 2048, 1024, 8),
                "an entry cannot be allowed more than the whole package");

            RbxWorldPackageLimits lowered = new(1024 * 1024, 64 * 1024, 256 * 1024, 16);
            byte[] small = RbxWorldPackageSerializer.WritePackage(WorldPackageTestWorlds.CaptureHeavyWorld(4, 1, 1, CapturedAtUtc), lowered);
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(small, lowered));
        }

        private static long EntryLength(byte[] package, string entryName)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            return archive.GetEntry(entryName).Length;
        }

        private static byte[] EntryBytes(byte[] package, string entryName)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(entryName).Open();
            using MemoryStream output = new();
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    /// <summary>Worlds and packages the world-package limit and encode tests build.</summary>
    internal static class WorldPackageTestWorlds
    {
        public const string WorldId = "worldpkg-ceiling";

        /// <summary>An actor id as long as the ids a multiplayer session issues.</summary>
        public const string ActorId = "8f14e45f-ceea-467a-9a1b-6f1c2b3d4e5f";

        public static RbxWorldSettings NewSettings()
        {
            return NewSettingsFor(WorldId);
        }

        public static RbxWorldSettings NewSettingsFor(string worldId)
        {
            return new RbxWorldSettings
            {
                WorldId = worldId,
                MetersPerStud = 0.28f,
                GravityStudsPerSecondSquared = 196.2d,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            };
        }

        /// <summary>
        /// Captures a world of <paramref name="instances"/> charged instances: four MaterialVariants and Parts
        /// built by AI console calls, each with a rotated pose, a fractional size, an explicit colour, one of
        /// twelve materials (one in eight with a variant), varied transparency, anchoring and collision,
        /// <paramref name="attributes"/> attributes (up to eight) and <paramref name="tags"/> tags (up to four).
        /// </summary>
        public static RbxWorldPackagePayload CaptureHeavyWorld(int instances, int attributes, int tags, DateTime capturedAtUtc)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            InMemoryPartPropertySink sink = new(registry);
            Random random = new(12345);
            RbxEnum materials = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            List<RbxMaterialId> palette = new();
            foreach (string material in new[]
                     {
                         "Plastic", "SmoothPlastic", "Wood", "WoodPlanks", "Brick", "Cobblestone", "Concrete",
                         "Slate", "Granite", "Metal", "Grass", "Sand"
                     })
            {
                if (materials.TryGetItem(material, out RbxEnumItem item))
                {
                    palette.Add(new RbxMaterialId(item.Name, item.Value));
                }
            }

            List<string> variants = new();
            RbxInstance materialService = game.FindFirstChildOfClass("MaterialService");
            int variantCount = materialService == null ? 0 : Math.Min(4, instances);
            for (int index = 0; index < variantCount; index++)
            {
                RbxMaterialVariant variant = (RbxMaterialVariant)registry.Create(
                    "MaterialVariant", null, "console:session-1", ownerActorId: ActorId);
                variant.Name = "CastleStone_" + index.ToString(CultureInfo.InvariantCulture);
                variant.BaseMaterial = palette[index];
                variant.Parent = materialService;
                variants.Add(variant.Name);
            }

            string[] attributeNames = { "Health", "MaxHealth", "Team", "Destructible", "Zone", "SpawnWeight", "LootTable", "Damage" };
            for (int index = 0; index < instances - variantCount; index++)
            {
                RbxInstance part = registry.Create(
                    "Part", null, "console:session-" + (1 + index / 64).ToString(CultureInfo.InvariantCulture),
                    ownerActorId: ActorId);
                part.Name = "Castle_Wall_Segment_" + index.ToString("D5", CultureInfo.InvariantCulture);
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = random.Next(4) != 0;
                properties.CanCollide = random.Next(8) != 0;
                properties.Size = new RbxVector3(
                    (float)(0.2d + random.NextDouble() * 30d),
                    (float)(0.2d + random.NextDouble() * 12d),
                    (float)(0.2d + random.NextDouble() * 30d));
                RbxCFrame rotation = RbxCFrame.Angles(
                    (float)(random.NextDouble() * Math.PI * 2d),
                    (float)(random.NextDouble() * Math.PI * 2d),
                    (float)(random.NextDouble() * Math.PI * 2d));
                RbxCFrame position = RbxCFrame.FromPosition(
                    (float)(random.NextDouble() * 4000d - 2000d),
                    (float)(random.NextDouble() * 400d),
                    (float)(random.NextDouble() * 4000d - 2000d));
                properties.CFrame = position * rotation;
                properties.Color = new RbxColor3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                properties.ColorWasExplicitlySet = true;
                properties.Material = palette[random.Next(palette.Count)];
                properties.Transparency = random.Next(6) == 0 ? (float)random.NextDouble() : 0f;
                if (variants.Count > 0 && random.Next(8) == 0)
                {
                    properties.MaterialVariant = variants[random.Next(variants.Count)];
                }

                sink.SetPartProperties(part.Id, in properties);
                for (int attribute = 0; attribute < Math.Min(attributes, attributeNames.Length); attribute++)
                {
                    part.SetAttribute(attributeNames[attribute], AttributeValue(attribute, random));
                }

                string[] tagNames =
                {
                    "Destructible", "CastleWall", "Zone_" + random.Next(64).ToString(CultureInfo.InvariantCulture), "Climbable"
                };
                for (int tag = 0; tag < Math.Min(tags, tagNames.Length); tag++)
                {
                    part.AddTag(tagNames[tag]);
                }

                part.Parent = registry.WorldRoot;
            }

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(registry, game, sink, NewSettings(), capturedAtUtc: capturedAtUtc));
            game.Destroy();
            return payload;
        }

        /// <summary>A world of <paramref name="count"/> StringValues, each holding the longest text a StringValue takes.</summary>
        public static RbxWorldPackagePayload CaptureLongStringValues(int count, DateTime capturedAtUtc)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            string text = new('w', RbxStringValue.MaxLength);
            for (int index = 0; index < count; index++)
            {
                RbxStringValue value = (RbxStringValue)registry.Create("StringValue");
                value.Name = "Lore_" + index.ToString(CultureInfo.InvariantCulture);
                value.Value = text;
                value.Parent = registry.WorldRoot;
            }

            RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(
                    registry, game, new InMemoryPartPropertySink(registry), NewSettings(), capturedAtUtc: capturedAtUtc));
            game.Destroy();
            return payload;
        }

        /// <summary>The text of one entry of a package of a four-instance world.</summary>
        public static string SmallPackageEntryText(string entryName, DateTime capturedAtUtc)
        {
            byte[] package = RbxWorldPackageSerializer.WritePackage(CaptureHeavyWorld(4, 1, 1, capturedAtUtc));
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(entryName).Open();
            using StreamReader reader = new(stream, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }

        public static byte[] BuildZip(params (string Name, byte[] Bytes)[] entries)
        {
            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
            {
                foreach ((string name, byte[] bytes) in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                    using Stream stream = entry.Open();
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// A copy of <paramref name="package"/> whose local and central headers declare
        /// <paramref name="declaredBytes"/> as the uncompressed size of <paramref name="entryName"/>.
        /// </summary>
        public static byte[] WithDeclaredUncompressedSize(byte[] package, string entryName, uint declaredBytes)
        {
            byte[] copy = (byte[])package.Clone();
            byte[] name = Encoding.UTF8.GetBytes(entryName);
            int patched = 0;
            for (int offset = 0; offset + 46 <= copy.Length; offset++)
            {
                uint signature = BitConverter.ToUInt32(copy, offset);
                if (signature == 0x04034b50U && NameAt(copy, offset + 30, BitConverter.ToUInt16(copy, offset + 26), name))
                {
                    WriteUInt32(copy, offset + 22, declaredBytes);
                    patched++;
                }
                else if (signature == 0x02014b50U && NameAt(copy, offset + 46, BitConverter.ToUInt16(copy, offset + 28), name))
                {
                    WriteUInt32(copy, offset + 24, declaredBytes);
                    patched++;
                }
            }

            Assert.AreEqual(2, patched, "precondition: both headers of '" + entryName + "' were patched");
            return copy;
        }

        public static string NewTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "CoreAI-WorldPkg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static object AttributeValue(int slot, Random random)
        {
            switch (slot)
            {
                case 0:
                    return Math.Round(random.NextDouble() * 1000d, 1);
                case 1:
                    return 1000d;
                case 2:
                    return random.Next(2) == 0 ? "Red" : "Blue";
                case 3:
                    return random.Next(2) == 0;
                case 4:
                    return "Zone_" + random.Next(64).ToString(CultureInfo.InvariantCulture);
                case 5:
                    return random.NextDouble();
                case 6:
                    return "castle_common_loot";
                default:
                    return (double)random.Next(5, 80);
            }
        }

        private static bool NameAt(byte[] bytes, int offset, int length, byte[] name)
        {
            if (length != name.Length || offset + length > bytes.Length)
            {
                return false;
            }

            for (int index = 0; index < length; index++)
            {
                if (bytes[offset + index] != name[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }
    }
}
