using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Catalog, shader, and local texture-ingestion regression coverage.</summary>
    [TestFixture]
    public sealed class RbxMaterialTextureCatalogEditModeTests
    {
        private const string CatalogTypeName =
            "CoreAI.Mods.Rbx.Rendering.RbxMaterialTextureCatalog";
        private const string ImporterTypeName =
            "CoreAI.Editor.RbxMaterials.RbxMegascansCatalogImporter";
        private const string DownloaderTypeName =
            "CoreAI.Editor.RbxMaterials.RbxAmbientCgCatalogDownloader";

        // WHY: this pins the downloader output, which is derived from the shared RbxCc0TextureSets
        // table minus the two Poly Haven sets (Slate, Basalt) that are not on ambientCG.
        private static readonly KeyValuePair<string, string>[] ConfirmedAmbientCgMappings =
        {
            new("Cobblestone", "PavingStones151"),
            new("Brick", "Bricks104"),
            new("Limestone", "Tiles139"),
            new("Sandstone", "Rock029"),
            new("Granite", "Granite002A"),
            new("Rock", "Rock064"),
            new("Concrete", "Concrete034"),
            new("Marble", "Marble016"),
            new("Plaster", "Plaster005"),
            new("Pavement", "PavingStones150"),
            new("Pebble", "Gravel041"),
            new("CeramicTiles", "Tiles141"),
            new("ClayRoofTiles", "RoofingTiles014A"),
            new("RoofShingles", "RoofingTiles003"),
            new("Wood", "Wood095"),
            new("WoodPlanks", "WoodFloor034"),
            new("Metal", "Metal063"),
            new("CorrodedMetal", "Metal021"),
            new("DiamondPlate", "DiamondPlate008C"),
            new("Foil", "Foil002"),
            new("Grass", "Grass004"),
            new("LeafyGrass", "Grass001"),
            new("Ground", "Ground110"),
            new("Mud", "Ground109"),
            new("Sand", "Ground025"),
            new("Snow", "Snow010A"),
            new("Ice", "Ice003"),
            new("CrackedLava", "Lava004"),
            new("Asphalt", "Asphalt016"),
            new("Fabric", "Fabric048"),
            new("Carpet", "Carpet014"),
            new("Leather", "Leather008"),
            new("Cardboard", "Cardboard001"),
            new("Rubber", "Rubber003")
        };

        [TearDown]
        public void TearDown()
        {
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [Test]
        public void CatalogOverrideWinsPerMaterial()
        {
            Type catalogType = RequiredRuntimeType(CatalogTypeName);
            Type entryType = RequiredNestedType(catalogType, "Entry");
            object defaultBrick = CreateEntry(entryType, "Brick", 848, 10f);
            object defaultWood = CreateEntry(entryType, "Wood", 512, 10f);
            object overrideBrick = CreateEntry(entryType, "Brick", 848, 22f);
            Array defaults = CreateEntryArray(entryType, defaultBrick, defaultWood);
            Array overrides = CreateEntryArray(entryType, overrideBrick);
            MethodInfo merge = catalogType.GetMethod("MergeEntries",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(merge, "RbxMaterialTextureCatalog.MergeEntries must exist.");

            IDictionary merged = (IDictionary)merge.Invoke(null, new object[] { defaults, overrides });

            Assert.AreSame(overrideBrick, merged[848], "override Brick entry must win by enum value");
            Assert.AreSame(defaultWood, merged[512], "unoverridden packaged entry must remain");
        }

        [Test]
        public void CatalogEntryForProceduralMaterialUsesTexturedShader()
        {
            Type catalogType = RequiredRuntimeType(CatalogTypeName);
            Type entryType = RequiredNestedType(catalogType, "Entry");
            Shader shader = Shader.Find("CoreAI/Rbx/Textured Surface");
            Assert.NotNull(shader);
            Texture2D albedo = new(2, 2);
            Texture2D normal = new(2, 2);
            Texture2D roughness = new(2, 2);
            ScriptableObject catalog = null;
            try
            {
                object entry = CreateCompleteEntry(entryType, "Concrete", 816, albedo, normal,
                    roughness, true);
                catalog = CreateCatalog(catalogType, entryType, entry);
                RbxTextureMaterialProvider provider = CreateProvider(catalogType, catalog, shader);
                RbxMaterialId concrete = new("Concrete", 816);

                Assert.IsTrue(provider.TryGetMaterial(in concrete, out Material material));
                Assert.AreEqual("CoreAI/Rbx/Textured Surface", material.shader.name);
            }
            finally
            {
                Destroy(albedo);
                Destroy(normal);
                Destroy(roughness);
                Destroy(catalog);
            }
        }

        [Test]
        public void DirectXNormalEntrySetsKeyword()
        {
            Type catalogType = RequiredRuntimeType(CatalogTypeName);
            Type entryType = RequiredNestedType(catalogType, "Entry");
            Shader shader = Shader.Find("CoreAI/Rbx/Textured Surface");
            Assert.NotNull(shader);
            Texture2D albedo = new(2, 2);
            Texture2D normal = new(2, 2);
            Texture2D roughness = new(2, 2);
            ScriptableObject catalog = null;
            try
            {
                object entry = CreateCompleteEntry(entryType, "Concrete", 816, albedo, normal,
                    roughness, false);
                catalog = CreateCatalog(catalogType, entryType, entry);
                RbxTextureMaterialProvider provider = CreateProvider(catalogType, catalog, shader);
                RbxMaterialId concrete = new("Concrete", 816);

                Assert.IsTrue(provider.TryGetMaterial(in concrete, out Material material));
                Assert.IsTrue(material.IsKeywordEnabled("_RBX_NORMAL_DIRECTX"));
            }
            finally
            {
                Destroy(albedo);
                Destroy(normal);
                Destroy(roughness);
                Destroy(catalog);
            }
        }

        [Test]
        public void MissingRequiredTextureFallsBackToProceduralAndLogsOnce()
        {
            Type catalogType = RequiredRuntimeType(CatalogTypeName);
            Type entryType = RequiredNestedType(catalogType, "Entry");
            Shader shader = Shader.Find("CoreAI/Rbx/Textured Surface");
            Assert.NotNull(shader);
            Texture2D albedo = new(2, 2);
            Texture2D roughness = new(2, 2);
            ScriptableObject catalog = null;
            try
            {
                object entry = CreateEntry(entryType, "Concrete", 816, 8f);
                SetProperty(entry, "Albedo", albedo);
                SetProperty(entry, "RoughnessOrSmoothness", roughness);
                catalog = CreateCatalog(catalogType, entryType, entry);
                RbxTextureMaterialProvider provider = CreateProvider(catalogType, catalog, shader);
                RbxMaterialId concrete = new("Concrete", 816);
                LogAssert.Expect(LogType.Error, new Regex(
                    "Catalog entry for Enum\\.Material\\.Concrete.*missing required.*using procedural",
                    RegexOptions.IgnoreCase));

                Assert.IsTrue(provider.TryGetMaterial(in concrete, out Material first));
                Assert.AreEqual("CoreAiRbxMaterial_Concrete", first.name);
                Assert.AreNotEqual("Hidden/InternalErrorShader", first.shader.name);
                Assert.IsTrue(provider.TryGetMaterial(in concrete, out Material second));
                Assert.AreSame(first, second);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Destroy(albedo);
                Destroy(roughness);
                Destroy(catalog);
            }
        }

        [Test]
        public void CatalogResourcesAndShaderExposeRequestedPbrContract()
        {
            Type providerType = typeof(RbxTextureMaterialProvider);
            Assert.AreEqual("CoreAIRbxTextures/RbxMaterialTextureCatalog",
                Constant(providerType, "DefaultCatalogResource"));
            Assert.AreEqual("CoreAIRbxTextureCatalogOverride",
                Constant(providerType, "OverrideCatalogResource"));
            string shader = File.ReadAllText(Path.Combine(ProjectRoot(),
                "Assets", "CoreAIMods", "Runtime", "RbxApi", "Unity", "Resources",
                "CoreAIRbxMaterials", "RbxTexturedSurface.shader"));

            StringAssert.Contains("_BumpScale(\"Normal Strength\", Range(0,2)) = 1.0", shader);
            StringAssert.Contains("_OcclusionMap", shader);
            StringAssert.Contains("_RBX_OCCLUSION_MAP", shader);
            StringAssert.Contains("_RBX_NORMAL_DIRECTX", shader);
            StringAssert.Contains("RBX_AXIS_BLEND_WIDTH = 0.10", shader);
        }

        [Test]
        public void MegascansScannerMapsFixtureAndDetectsDirectX()
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(),
                "CoreAiRbxMegascansFixture_" + Guid.NewGuid().ToString("N"));
            string surface = Path.Combine(fixtureRoot, "Old_Red_Brick_Wall");
            Directory.CreateDirectory(surface);
            try
            {
                File.WriteAllBytes(Path.Combine(surface, "RedBrick_Albedo.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RedBrick_Normal_DX.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RedBrick_Roughness.png"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(surface, "RedBrick_AO.png"), Array.Empty<byte>());
                File.WriteAllText(Path.Combine(surface, "RedBrick.json"),
                    "{\"maps\": [{\"normal\": \"DirectX\"}]}");
                Type importerType = RequiredEditorType(ImporterTypeName);
                MethodInfo scan = importerType.GetMethod("ScanFolder",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(scan, "RbxMegascansCatalogImporter.ScanFolder must exist.");

                IEnumerable results = (IEnumerable)scan.Invoke(null, new object[] { fixtureRoot });
                IEnumerator enumerator = results.GetEnumerator();
                Assert.IsTrue(enumerator.MoveNext(), "fixture surface was not scanned");
                object result = enumerator.Current;
                Assert.AreEqual("Brick", GetProperty(result, "SuggestedMaterialName"));
                Assert.AreEqual(false, GetProperty(result, "IsOpenGlNormal"));
                StringAssert.EndsWith("RedBrick_Albedo.png", (string)GetProperty(result, "AlbedoPath"));
                StringAssert.EndsWith("RedBrick_Normal_DX.png", (string)GetProperty(result, "NormalPath"));
                StringAssert.EndsWith("RedBrick_Roughness.png",
                    (string)GetProperty(result, "RoughnessPath"));
                StringAssert.EndsWith("RedBrick_AO.png",
                    (string)GetProperty(result, "AmbientOcclusionPath"));
                Assert.IsFalse(enumerator.MoveNext(), "fixture should produce one surface");
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
        public void AmbientCgMappingUsesConfirmedAssetIds()
        {
            Type downloaderType = RequiredEditorType(DownloaderTypeName);
            PropertyInfo mappingsProperty = downloaderType.GetProperty("Mappings",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(mappingsProperty, "RbxAmbientCgCatalogDownloader.Mappings must exist.");
            IEnumerable mappings = (IEnumerable)mappingsProperty.GetValue(null);
            List<KeyValuePair<string, string>> actual = new();
            foreach (object mapping in mappings)
            {
                actual.Add(new KeyValuePair<string, string>(
                    (string)GetProperty(mapping, "MaterialName"),
                    (string)GetProperty(mapping, "AssetId")));
            }

            CollectionAssert.AreEqual(ConfirmedAmbientCgMappings, actual);
        }

        private static Type RequiredRuntimeType(string typeName)
        {
            Type type = typeof(RbxTextureMaterialProvider).Assembly.GetType(typeName, false);
            Assert.NotNull(type, typeName + " must exist.");
            return type;
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

        private static Type RequiredNestedType(Type declaringType, string name)
        {
            Type nested = declaringType.GetNestedType(name,
                BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(nested, declaringType.FullName + "." + name + " must exist.");
            return nested;
        }

        private static object CreateEntry(Type entryType, string name, int value, float tileWidth)
        {
            object entry = Activator.CreateInstance(entryType);
            SetProperty(entry, "MaterialName", name);
            SetProperty(entry, "MaterialValue", value);
            SetProperty(entry, "TileWidthStuds", tileWidth);
            return entry;
        }

        private static object CreateCompleteEntry(Type entryType, string name, int value,
            Texture2D albedo, Texture2D normal, Texture2D roughness, bool isOpenGlNormal)
        {
            object entry = CreateEntry(entryType, name, value, 8f);
            SetProperty(entry, "Albedo", albedo);
            SetProperty(entry, "Normal", normal);
            SetProperty(entry, "RoughnessOrSmoothness", roughness);
            SetProperty(entry, "IsOpenGlNormal", isOpenGlNormal);
            SetProperty(entry, "IntrinsicColor", Color.white);
            SetProperty(entry, "PartColorInfluence", 0.5f);
            SetProperty(entry, "RoughnessScale", 1f);
            SetProperty(entry, "NormalStrength", 1f);
            return entry;
        }

        private static Array CreateEntryArray(Type entryType, params object[] entries)
        {
            Array array = Array.CreateInstance(entryType, entries.Length);
            for (int index = 0; index < entries.Length; index++)
            {
                array.SetValue(entries[index], index);
            }

            return array;
        }

        private static ScriptableObject CreateCatalog(Type catalogType, Type entryType,
            params object[] entries)
        {
            ScriptableObject catalog = ScriptableObject.CreateInstance(catalogType);
            MethodInfo replace = catalogType.GetMethod("ReplaceEntries",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(replace, "RbxMaterialTextureCatalog.ReplaceEntries must exist.");
            replace.Invoke(catalog, new object[] { CreateEntryArray(entryType, entries) });
            return catalog;
        }

        private static RbxTextureMaterialProvider CreateProvider(Type catalogType,
            ScriptableObject catalog, Shader shader)
        {
            ConstructorInfo constructor = typeof(RbxTextureMaterialProvider).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { catalogType, catalogType, typeof(Shader) }, null);
            Assert.NotNull(constructor, "testable catalog constructor must exist");
            return (RbxTextureMaterialProvider)constructor.Invoke(new object[]
            {
                catalog, null, shader
            });
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(property, target.GetType().FullName + "." + name + " must exist.");
            property.SetValue(target, value);
        }

        private static object GetProperty(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(property, target.GetType().FullName + "." + name + " must exist.");
            return property.GetValue(target);
        }

        private static object Constant(Type type, string name)
        {
            FieldInfo field = type.GetField(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(field, type.FullName + "." + name + " must exist.");
            return field.GetRawConstantValue();
        }

        private static string ProjectRoot()
        {
            DirectoryInfo directory = new(Directory.GetCurrentDirectory());
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Assets")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory, "Unity project root not found from " + Directory.GetCurrentDirectory());
            return directory.FullName;
        }

        private static void Destroy(Object value)
        {
            if (value != null)
            {
                Object.DestroyImmediate(value);
            }
        }
    }
}
