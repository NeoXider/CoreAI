using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Adversarial QA regression for tex2k catalog, scanner, URL and merge contract.</summary>
    [TestFixture]
    public sealed class RbxMaterialTextureCatalogQaEditModeTests
    {
        private const string DownloaderTypeName =
            "CoreAI.Editor.RbxMaterials.RbxAmbientCgCatalogDownloader";
        private const string ImporterTypeName =
            "CoreAI.Editor.RbxMaterials.RbxMegascansCatalogImporter";

        private static readonly KeyValuePair<string, string>[] VerifiedMappings =
        {
            new("Brick", "Bricks104"),
            new("Wood", "Wood049"),
            new("WoodPlanks", "WoodFloor051"),
            new("Cobblestone", "PavingStones150"),
            new("Metal", "Metal049A"),
            new("Grass", "Grass005"),
            new("Pavement", "PavingStones128"),
            new("Pebble", "Gravel023"),
            new("CeramicTiles", "Tiles133A"),
            new("Mud", "Ground106"),
            new("Ground", "Ground103"),
            new("ClayRoofTiles", "RoofingTiles006"),
            new("RoofShingles", "RoofingTiles003"),
            new("Fabric", "Fabric036"),
            new("Carpet", "Carpet016"),
            new("Slate", "Rock022"),
            new("Sandstone", "Bricks084"),
            new("Limestone", "Travertine009"),
            new("Granite", "Granite001A"),
            new("Basalt", "Rock035"),
            new("Concrete", "Concrete048"),
            new("Asphalt", "Asphalt033"),
            new("Sand", "Ground093C"),
            new("Plaster", "Plaster001"),
            new("DiamondPlate", "DiamondPlate008C"),
            new("CrackedLava", "Lava004"),
            new("CorrodedMetal", "Rust004"),
            new("Foil", "Foil003")
        };

        [Test]
        public void AmbientCgMapping_ContainsVerifiedCorrectedIds()
        {
            Type downloaderType = RequiredEditorType(DownloaderTypeName);
            PropertyInfo mappingsProperty = downloaderType.GetProperty("Mappings",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(mappingsProperty);
            IEnumerable mappings = (IEnumerable)mappingsProperty.GetValue(null);
            Dictionary<string, string> actual = new(StringComparer.Ordinal);
            foreach (object mapping in mappings)
            {
                actual[(string)GetProperty(mapping, "MaterialName")] =
                    (string)GetProperty(mapping, "AssetId");
            }

            foreach (KeyValuePair<string, string> expected in VerifiedMappings)
            {
                Assert.IsTrue(actual.ContainsKey(expected.Key), expected.Key + " must be mapped");
                Assert.AreEqual(expected.Value, actual[expected.Key],
                    expected.Key + " asset id must match verified SHADER_SOURCES_RESEARCH §2.4");
            }

            Assert.IsTrue(actual.ContainsKey("Foil"), "Foil must be mapped (was missing)");
            Assert.AreEqual("Foil003", actual["Foil"]);
        }

        [Test]
        public void BuildDownloadUrl_UsesAmbientCgJpgZipFormat()
        {
            Type downloaderType = RequiredEditorType(DownloaderTypeName);
            MethodInfo build = downloaderType.GetMethod("BuildDownloadUrl",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(build);
            string url = (string)build.Invoke(null, new object[] { "DiamondPlate008C", "2K" });
            Assert.AreEqual("https://ambientcg.com/get?file=DiamondPlate008C_2K-JPG.zip", url);
            string url4k = (string)build.Invoke(null, new object[] { "Wood049", "4K" });
            Assert.AreEqual("https://ambientcg.com/get?file=Wood049_4K-JPG.zip", url4k);
        }

        [Test]
        public void MegascansScanner_HandlesBridgeUnityExportLayout_AndDefaultsToDirectX()
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(),
                "CoreAiQaBridge_" + Guid.NewGuid().ToString("N"));
            string surface = Path.Combine(fixtureRoot, "RockySurface");
            Directory.CreateDirectory(surface);
            try
            {
                File.WriteAllBytes(Path.Combine(surface, "RockySurface_2K_Albedo.jpg"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RockySurface_2K_Normal.jpg"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RockySurface_2K_Roughness.jpg"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RockySurface_2K_AO.jpg"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RockySurface_2K_Metalness.jpg"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RockySurface_2K_Displacement.jpg"), Array.Empty<byte>());
                File.WriteAllText(Path.Combine(surface, "RockySurface.json"), "{}");
                Type importerType = RequiredEditorType(ImporterTypeName);
                MethodInfo scan = importerType.GetMethod("ScanSurfaceFolder",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(scan);
                object result = scan.Invoke(null, new object[] { surface });
                Assert.NotNull(result, "Bridge layout surface must be scanned");
                Assert.AreEqual(false, GetProperty(result, "IsOpenGlNormal"),
                    "plain Normal without GL suffix must default to DirectX");
                StringAssert.EndsWith("RockySurface_2K_Albedo.jpg", (string)GetProperty(result, "AlbedoPath"));
                StringAssert.EndsWith("RockySurface_2K_Normal.jpg", (string)GetProperty(result, "NormalPath"));
                StringAssert.EndsWith("RockySurface_2K_Roughness.jpg", (string)GetProperty(result, "RoughnessPath"));
                StringAssert.EndsWith("RockySurface_2K_AO.jpg", (string)GetProperty(result, "AmbientOcclusionPath"));
                StringAssert.EndsWith("RockySurface_2K_Metalness.jpg", (string)GetProperty(result, "MetalnessPath"));
                Assert.IsNotNull(GetProperty(result, "DisplacementPath"));
            }
            finally
            {
                if (Directory.Exists(fixtureRoot))
                {
                    Directory.Delete(fixtureRoot, true);
                }
            }
        }

        [Test]
        public void MegascansScanner_DetectsNormalGl_AndJsonDirectX()
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(),
                "CoreAiQaGl_" + Guid.NewGuid().ToString("N"));
            string surfaceGl = Path.Combine(fixtureRoot, "SurfaceGl");
            string surfaceDx = Path.Combine(fixtureRoot, "SurfaceDx");
            Directory.CreateDirectory(surfaceGl);
            Directory.CreateDirectory(surfaceDx);
            try
            {
                File.WriteAllBytes(Path.Combine(surfaceGl, "a_Albedo.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surfaceGl, "a_NormalGL.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surfaceGl, "a_Roughness.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surfaceDx, "b_Albedo.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surfaceDx, "b_Normal.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surfaceDx, "b_Roughness.png"), Array.Empty<byte>());
                File.WriteAllText(Path.Combine(surfaceDx, "b.json"), "{\"normal\": \"DirectX\"}");
                Type importerType = RequiredEditorType(ImporterTypeName);
                MethodInfo scan = importerType.GetMethod("ScanSurfaceFolder",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                object gl = scan.Invoke(null, new object[] { surfaceGl });
                object dx = scan.Invoke(null, new object[] { surfaceDx });
                Assert.AreEqual(true, GetProperty(gl, "IsOpenGlNormal"));
                Assert.AreEqual(false, GetProperty(dx, "IsOpenGlNormal"));
            }
            finally
            {
                if (Directory.Exists(fixtureRoot))
                {
                    Directory.Delete(fixtureRoot, true);
                }
            }
        }

        private static Type RequiredEditorType(string typeName)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load("CoreAI.Mods.Editor");
            }
            catch (Exception exception)
            {
                Assert.Fail("CoreAI.Mods.Editor must load: " + exception.Message);
                return null;
            }

            Type type = assembly.GetType(typeName, false);
            Assert.NotNull(type, typeName + " must exist.");
            return type;
        }

        private static object GetProperty(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(property, target.GetType().FullName + "." + name + " must exist.");
            return property.GetValue(target);
        }
    }
}
