using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Editor.RbxMaterials;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Adversarial QA regression for tex2k catalog, scanner, URL and merge contract.</summary>
    [TestFixture]
    public sealed class RbxMaterialTextureCatalogQaEditModeTests
    {
        private const string ImporterTypeName =
            "CoreAI.Editor.RbxMaterials.RbxMegascansCatalogImporter";

        [Test]
        public void AmbientCgMapping_MatchesSharedCc0Table()
        {
            // WHY: this test used to freeze its own copy of the Enum.Material -> asset-id mapping
            // and went red when defective sets were replaced in RbxCc0TextureSets only. The
            // expectation is reconstructed from the shared table, so a replacement can never break
            // it again; Poly Haven sets are skipped because they are not on ambientCG.
            Dictionary<string, string> expected = new(StringComparer.Ordinal);
            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                if (!RbxCc0TextureSets.IsAmbientCg(set))
                {
                    continue;
                }

                expected[set.MaterialName] = RbxCc0TextureSets.AssetId(set);
            }

            IReadOnlyList<RbxAmbientCgMapping> mappings = RbxAmbientCgCatalogDownloader.Mappings;
            Assert.AreEqual(expected.Count, mappings.Count,
                "downloader mappings must cover exactly the shared ambientCG sets");
            foreach (RbxAmbientCgMapping mapping in mappings)
            {
                Assert.IsTrue(expected.TryGetValue(mapping.MaterialName, out string assetId),
                    mapping.MaterialName + " is not an ambientCG set in the shared table");
                Assert.AreEqual(assetId, mapping.AssetId,
                    mapping.MaterialName + " asset id must match the shared table");
                Assert.IsFalse(string.IsNullOrEmpty(mapping.AssetId),
                    mapping.MaterialName + " asset id must not be empty");
                StringAssert.StartsWith("https://ambientcg.com/get?file=",
                    RbxAmbientCgCatalogDownloader.BuildDownloadUrl(mapping.AssetId, "2K"),
                    mapping.MaterialName + " asset id must build a fetchable ambientCG URL");
            }

            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                if (RbxCc0TextureSets.IsAmbientCg(set))
                {
                    continue;
                }

                Assert.IsFalse(expected.ContainsKey(set.MaterialName),
                    set.MaterialName + " is a Poly Haven set and must not be fetched from ambientCG");
            }
        }

        [Test]
        public void BuildDownloadUrl_UsesAmbientCgJpgZipFormat()
        {
            // WHY: these literals are URL-format fixtures, not catalog claims — the id-to-material
            // mapping itself is guarded against the shared table by AmbientCgMapping_MatchesSharedCc0Table.
            string url = RbxAmbientCgCatalogDownloader.BuildDownloadUrl("DiamondPlate008C", "2K");
            Assert.AreEqual("https://ambientcg.com/get?file=DiamondPlate008C_2K-JPG.zip", url);
            string url4k = RbxAmbientCgCatalogDownloader.BuildDownloadUrl("Wood095", "4K");
            Assert.AreEqual("https://ambientcg.com/get?file=Wood095_4K-JPG.zip", url4k);
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
