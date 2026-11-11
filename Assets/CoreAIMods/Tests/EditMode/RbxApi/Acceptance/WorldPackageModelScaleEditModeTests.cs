using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// A Model's <c>GetScale()</c> in the world package: a Model scaled to 1.44 saves and loads with its factor and its
    /// parts at their scaled sizes, and a later ScaleTo(1) gives back the scale-1 sizes from that factor; an unscaled
    /// Model writes no "scale" key, so the package of a world with no scaled model is the same bytes as before (twin).
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageModelScaleEditModeTests
    {
        private const string WorldId = "scale-world";
        private static readonly DateTime CapturedAtUtc = new(2026, 11, 12, 4, 0, 0, DateTimeKind.Utc);
        private readonly List<RbxDataModel> _games = new();

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

            _games.Clear();
        }

        [Test]
        public void AScaledModel_SavesAndLoadsItsFactor_AndScaleOneGivesBackItsScaleOneSizes()
        {
            byte[] package = Package(1.44d, out RbxInstance part);
            RbxWorldPackagePayload decoded = RbxWorldPackageSerializer.ReadPackage(package);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(decoded,
                new RbxWorldPackageRestoreOptions { CameraRig = new InMemoryCameraRig() });
            _games.Add(restored.Game);

            Assert.IsTrue(restored.Registry.TryGet(part.Parent.Id, out RbxInstance model));
            Assert.AreEqual(1.44d, ((RbxModel)model).Scale, 1e-9, "the factor comes back");
            Assert.IsTrue(restored.PartSink.TryGetPartProperties(part.Id, out PartProperties loaded));
            Assert.AreEqual(2.88f, loaded.Size.X, 1e-4f, "the part comes back at its scaled size");

            RbxModel scaled = (RbxModel)model;
            RbxCFrame pivot = loaded.CFrame;
            RbxModelScaling.OriginAtScaleOne(pivot, loaded.CFrame, loaded.Size, scaled.Scale, out RbxVector3 sizeAtOne,
                out RbxCFrame _);
            Assert.AreEqual(2f, sizeAtOne.X, 1e-4f, "ScaleTo(1) after the load finds the scale-1 size from the factor");
        }

        [Test]
        public void AnUnscaledModel_WritesNoScaleKey_AndTheSameBytes_NegativeTwin()
        {
            byte[] unscaled = Package(1d, out RbxInstance _);
            byte[] again = Package(1d, out RbxInstance _);

            CollectionAssert.AreEqual(unscaled, again, "the package of a world with no scaled model is unchanged");
            string world = WorldJson(unscaled);
            StringAssert.DoesNotContain("\"scale\"", world);
            string scaledWorld = WorldJson(Package(1.44d, out RbxInstance _));
            StringAssert.Contains("\"scale\"", scaledWorld, "a scaled model writes the key");
        }

        /// <summary>A world holding one Model with one 2x2x1 part, scaled to <paramref name="scale"/>, as a package.</summary>
        private byte[] Package(double scale, out RbxInstance part)
        {
            InstanceRegistry registry = new(worldId: WorldId);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _games.Add(game);
            InMemoryPartPropertySink partSink = new();
            RbxModel model = (RbxModel)registry.Create("Model");
            model.Name = "Body";
            model.Parent = registry.WorldRoot;
            part = registry.Create("Part");
            part.Name = "Root";
            part.Parent = model;
            PartProperties defaults = PartProperties.CreateDefault();
            partSink.SetPartProperties(part.Id, in defaults);
            partSink.SetSize(part.Id, new RbxVector3(2f, 2f, 1f) * (float)scale);
            partSink.SetPosition(part.Id, new RbxVector3(10f, 5f, 0f));
            model.SetPrimaryPart(part);
            if (scale != 1d)
            {
                model.SetScale(scale);
            }

            RbxWorldPackageCaptureContext context = new(registry, game, partSink, new RbxWorldSettings
            {
                WorldId = WorldId,
                MetersPerStud = 0.28f,
                GravityStudsPerSecondSquared = 196.2d,
                SignalBehavior = RbxWorldSettings.DeferredSignalBehavior
            }, new InMemoryCameraRig(), capturedAtUtc: CapturedAtUtc);
            return RbxWorldPackageSerializer.WritePackage(RbxWorldPackageSerializer.Capture(context));
        }

        private static string WorldJson(byte[] package)
        {
            using MemoryStream stream = new(package);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read);
            ZipArchiveEntry entry = archive.GetEntry(RbxWorldPackageSerializer.WorldEntryName);
            Assert.IsNotNull(entry, "the package holds world.json");
            using StreamReader reader = new(entry.Open());
            return reader.ReadToEnd();
        }
    }
}
