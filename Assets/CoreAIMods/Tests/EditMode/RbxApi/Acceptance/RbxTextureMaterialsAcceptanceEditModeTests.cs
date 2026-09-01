using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Production-path acceptance coverage for hybrid texture/procedural Rbx materials.</summary>
    [TestFixture]
    public sealed class RbxTextureMaterialsAcceptanceEditModeTests
    {
        private const float ColorEpsilon = 1e-5f;
        private const int BrickValue = 848;
        private const int WoodValue = 512;

        private static readonly (string Name, int Value)[] ExpectedRenderSupportedMaterials =
        {
            ("Plastic", 256),
            ("SmoothPlastic", 272),
            ("Neon", 288),
            ("Wood", 512),
            ("WoodPlanks", 528),
            ("Marble", 784),
            ("Basalt", 788),
            ("Slate", 800),
            ("CrackedLava", 804),
            ("Concrete", 816),
            ("Limestone", 820),
            ("Granite", 832),
            ("Pavement", 836),
            ("Brick", 848),
            ("Pebble", 864),
            ("Cobblestone", 880),
            ("Rock", 896),
            ("Sandstone", 912),
            ("CorrodedMetal", 1040),
            ("DiamondPlate", 1056),
            ("Foil", 1072),
            ("Metal", 1088),
            ("Grass", 1280),
            ("LeafyGrass", 1284),
            ("Sand", 1296),
            ("Fabric", 1312),
            ("Snow", 1328),
            ("Mud", 1344),
            ("Ground", 1360),
            ("Asphalt", 1376),
            ("Salt", 1392),
            ("Ice", 1536),
            ("Glacier", 1552),
            ("Glass", 1568),
            ("ForceField", 1584),
            ("Air", 1792),
            ("Water", 2048),
            ("Cardboard", 2304),
            ("Carpet", 2305),
            ("CeramicTiles", 2306),
            ("ClayRoofTiles", 2307),
            ("RoofShingles", 2308),
            ("Leather", 2309),
            ("Plaster", 2310),
            ("Rubber", 2311)
        };

        [SetUp]
        public void SetUp()
        {
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [TearDown]
        public void TearDown()
        {
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [Test]
        public void EveryRenderSupportedMaterial_AppliesThroughPublicLuaPartApiWithoutPinkFallback()
        {
            IReadOnlyList<RbxMaterialId> supported =
                RbxProceduralMaterialProvider.SupportedMaterials;
            Assert.AreEqual(ExpectedRenderSupportedMaterials.Length, supported.Count);

            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            Assert.AreEqual(45, materialEnum.GetEnumItems().Count,
                "The complete public material enum must remain render-supported.");
            Assert.AreEqual(materialEnum.GetEnumItems().Count, supported.Count,
                "Every public Enum.Material item must have a runtime render mapping.");

            StringBuilder source = new();
            for (int index = 0; index < ExpectedRenderSupportedMaterials.Length; index++)
            {
                (string Name, int Value) expected = ExpectedRenderSupportedMaterials[index];
                RbxMaterialId supportedId = supported[index];
                Assert.AreEqual(expected.Name, supportedId.Name, "catalog name at " + index);
                Assert.AreEqual(expected.Value, supportedId.Value, "catalog value at " + index);
                Assert.AreEqual(expected.Value, materialEnum[expected.Name].Value,
                    "public Enum.Material value for " + expected.Name);

                source.Append("local part").Append(index)
                    .Append(" = Instance.new('Part', workspace)\n")
                    .Append("part").Append(index).Append(".Name = 'ApiMaterial_")
                    .Append(expected.Name).Append("'\n")
                    .Append("part").Append(index).Append(".Material = Enum.Material.")
                    .Append(expected.Name).Append("\n");
                int red = 32 + index * 29 % 192;
                int green = 48 + index * 47 % 176;
                int blue = 64 + index * 61 % 160;
                source.Append("part").Append(index).Append(".Color = Color3.fromRGB(")
                    .Append(red).Append(", ").Append(green).Append(", ")
                    .Append(blue).Append(")\n");
            }

            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("all-render-materials", source.ToString());
                for (int index = 0; index < ExpectedRenderSupportedMaterials.Length; index++)
                {
                    (string Name, int Value) expected = ExpectedRenderSupportedMaterials[index];
                    RbxInstance part = world.Workspace.FindFirstChild(
                        "ApiMaterial_" + expected.Name);
                    Assert.IsNotNull(part, expected.Name);
                    PartProperties properties = world.Binder.GetPartPropertiesOrDefault(part.Id);
                    Assert.AreEqual(expected.Name, properties.Material.Name, expected.Name);
                    Assert.AreEqual(expected.Value, properties.Material.Value, expected.Name);

                    Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                    Material material = renderer.sharedMaterial;
                    Assert.IsNotNull(material, expected.Name);
                    Assert.IsNotNull(material.shader, expected.Name);
                    Assert.IsTrue(material.shader.isSupported,
                        expected.Name + " shader must be supported by the active player renderer.");
                    Assert.AreNotEqual("Hidden/InternalErrorShader", material.shader.name,
                        expected.Name);
                    Assert.AreNotEqual("CoreAiRbxMaterial_FALLBACK_UNMAPPED", material.name,
                        expected.Name);
                    int red = 32 + index * 29 % 192;
                    int green = 48 + index * 47 % 176;
                    int blue = 64 + index * 61 % 160;
                    RbxColor3 expectedColor = RbxColor3.FromRGB(red, green, blue);
                    Assert.AreEqual(expectedColor, properties.Color, expected.Name);
                    Assert.IsTrue(properties.ColorWasExplicitlySet, expected.Name);
                    MaterialPropertyBlock block = new();
                    renderer.GetPropertyBlock(block);
                    Color tint = block.GetColor(Shader.PropertyToID("_Color"));
                    Assert.AreEqual(expectedColor.R, tint.r, ColorEpsilon, expected.Name);
                    Assert.AreEqual(expectedColor.G, tint.g, ColorEpsilon, expected.Name);
                    Assert.AreEqual(expectedColor.B, tint.b, ColorEpsilon, expected.Name);
                    Assert.AreEqual(
                        tint,
                        block.GetColor(Shader.PropertyToID("_BaseColor")),
                        expected.Name);
                }
            }
        }

        [TestCase("Brick", 848, "Bricks104", 10f)]
        [TestCase("Wood", 512, "Wood095", 10f)]
        [TestCase("WoodPlanks", 528, "Wood095", 8f)]
        [TestCase("Grass", 1280, "Grass005", 7f)]
        [TestCase("Cobblestone", 880, "PavingStones151", 14f)]
        [TestCase("Metal", 1088, "Metal063", 3.5f)]
        public void LuaPartMaterial_ResolvesExpectedTexturedSharedHandle(
            string materialName, int materialValue, string textureStem, float tileWidthStuds)
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("texture-map", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'TexturedPart'
                    part.Material = Enum.Material." + materialName);

                RbxInstance part = world.Workspace.FindFirstChild("TexturedPart");
                Material texturedMaterial = world.BoundObject(part).GetComponent<Renderer>().sharedMaterial;
                RbxMaterialId id = new(materialName, materialValue);
                RbxProceduralMaterialProvider proceduralProvider = new();
                bool proceduralMapped = proceduralProvider.TryGetMaterial(in id,
                    out Material proceduralMaterial);

                Assert.IsTrue(proceduralMapped);
                Assert.AreEqual("CoreAiRbxTextureMaterial_" + materialName, texturedMaterial.name);
                Assert.AreEqual("CoreAI/Rbx/Textured Surface", texturedMaterial.shader.name);
                Assert.AreNotSame(proceduralMaterial, texturedMaterial);
                StringAssert.Contains(textureStem + "_1K-JPG_Color",
                    texturedMaterial.GetTexture("_BaseMap").name);
                StringAssert.Contains(textureStem + "_1K-JPG_NormalGL",
                    texturedMaterial.GetTexture("_BumpMap").name);
                StringAssert.Contains(textureStem + "_1K-JPG_Roughness",
                    texturedMaterial.GetTexture("_RoughnessMap").name);
                Texture colorTexture = texturedMaterial.GetTexture("_BaseMap");
                Assert.That(texturedMaterial.GetFloat("_TextureAspect"),
                    Is.EqualTo((float)colorTexture.width / colorTexture.height).Within(0.0001f));
                float expectedScale = texturedMaterial.GetFloat("_TextureAspect") /
                                      (tileWidthStuds * RbxSpace.MetersPerStud);
                Assert.That(texturedMaterial.GetFloat("_TextureScale"),
                    Is.EqualTo(expectedScale).Within(0.0001f));
                Assert.AreEqual(1f, texturedMaterial.GetFloat("_NeutralDefaultPartColor"));
                if (string.Equals(materialName, "Metal", StringComparison.Ordinal))
                {
                    StringAssert.Contains("Metal063_1K-JPG_Metalness",
                        texturedMaterial.GetTexture("_MetallicMap").name);
                    Assert.IsTrue(texturedMaterial.IsKeywordEnabled("_RBX_METALLIC_MAP"));
                }
            }
        }

        [TestCase("Brick", 10f, 0.28f)]
        [TestCase("Brick", 10f, 0.5f)]
        [TestCase("Wood", 10f, 0.28f)]
        [TestCase("WoodPlanks", 8f, 0.28f)]
        [TestCase("Grass", 7f, 0.28f)]
        [TestCase("Cobblestone", 14f, 0.28f)]
        [TestCase("Metal", 3.5f, 0.28f)]
        public void LuaTexturedPart_ThreeStudFaceProjectsConfiguredFullTextureCount(
            string materialName, float tileWidthStuds, float metersPerStud)
        {
            const float partSizeStuds = 3f;
            using (Mvp1AcceptanceWorld world = new(metersPerStud))
            {
                world.Stack.Runtime.LoadMod("texture-session-scale", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'ScaledTexturePart'
                    part.Size = Vector3.new(3, 3, 3)
                    part.Material = Enum.Material." + materialName);

                RbxInstance part = world.Workspace.FindFirstChild("ScaledTexturePart");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                Material material = renderer.sharedMaterial;
                float textureAspect = material.GetFloat("_TextureAspect");
                float textureScale = material.GetFloat("_TextureScale");
                Matrix4x4 objectToWorld = renderer.transform.localToWorldMatrix;
                float faceWidthMeters = objectToWorld.MultiplyVector(Vector3.right).magnitude;
                float faceHeightMeters = objectToWorld.MultiplyVector(Vector3.up).magnitude;
                float horizontalTileCount = faceWidthMeters * textureScale / textureAspect;
                float verticalTileCount = faceHeightMeters * textureScale;

                Assert.That(faceWidthMeters,
                    Is.EqualTo(partSizeStuds * metersPerStud).Within(0.0001f));
                Assert.That(faceHeightMeters,
                    Is.EqualTo(partSizeStuds * metersPerStud).Within(0.0001f));
                Assert.That(horizontalTileCount,
                    Is.EqualTo(partSizeStuds / tileWidthStuds).Within(0.0001f));
                Assert.That(verticalTileCount,
                    Is.EqualTo(partSizeStuds * textureAspect / tileWidthStuds).Within(0.0001f));
                if (string.Equals(materialName, "Brick", StringComparison.Ordinal))
                {
                    const float brickCoursesPerTextureHeight = 10f;
                    Assert.That(verticalTileCount * brickCoursesPerTextureHeight,
                        Is.EqualTo(6f).Within(0.0001f));
                }
            }
        }

        [Test]
        public void LuaDiamondPlate_ThreeStudFaceKeepsReadableTreadCount()
        {
            const float diamondCellsPerPatternUnit = 1.35f;
            const float diamondHalfWidthInCell = 0.28f;
            string commonPath = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralCommon.hlsl");
            string commonSource = File.ReadAllText(commonPath);
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("diamond-scale", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'ScaledDiamondPlate'
                    part.Size = Vector3.new(3, 3, 3)
                    part.Material = Enum.Material.DiamondPlate");

                RbxInstance part = world.Workspace.FindFirstChild("ScaledDiamondPlate");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                Material material = renderer.sharedMaterial;
                float patternScale = material.GetFloat("_PatternScale");
                float cellSpacingMeters = 1f / (patternScale * diamondCellsPerPatternUnit);
                float treadWidthMeters = 2f * diamondHalfWidthInCell /
                                         (patternScale * diamondCellsPerPatternUnit);
                float faceWidthMeters = renderer.transform.localToWorldMatrix
                    .MultiplyVector(Vector3.right).magnitude;
                float cellsPerEdge = faceWidthMeters / cellSpacingMeters;
                float estimatedTreadCountOnFace = cellsPerEdge * cellsPerEdge;

                Assert.AreEqual("CoreAI/Rbx/Procedural Surface", material.shader.name);
                StringAssert.Contains("float2 plateCell = frac(uv * 1.35)", commonSource);
                StringAssert.Contains(
                    "RbxFilteredInsideMask(diamondDistance, 0.28, 0.06, distanceFootprint)",
                    commonSource);
                Assert.That(faceWidthMeters, Is.EqualTo(0.84f).Within(0.0001f));
                Assert.That(patternScale, Is.EqualTo(5f).Within(0.0001f));
                Assert.That(cellSpacingMeters, Is.EqualTo(0.1481f).Within(0.0001f));
                Assert.That(treadWidthMeters, Is.EqualTo(0.083f).Within(0.0001f));
                Assert.That(cellsPerEdge, Is.EqualTo(5.67f).Within(0.0001f));
                Assert.That(estimatedTreadCountOnFace, Is.InRange(20f, 40f),
                    "A three-stud face must show readable diamonds instead of filtered tread haze.");
            }
        }

        [Test]
        public void LuaTexturedPart_DefaultColorUsesNeutralWhiteTint()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("texture-neutral", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'NeutralBrick'
                    part.Material = Enum.Material.Brick");

                RbxInstance part = world.Workspace.FindFirstChild("NeutralBrick");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                PartProperties properties = world.Binder.GetPartPropertiesOrDefault(part.Id);

                Assert.AreEqual(Color.white, block.GetColor("_Color"));
                Assert.AreEqual(Color.white, block.GetColor("_BaseColor"));
                Assert.AreEqual(RbxColor3.FromRGB(163f, 162f, 165f), properties.Color);
                Assert.IsFalse(properties.ColorWasExplicitlySet);
            }
        }

        [Test]
        public void LuaPartColor_ExplicitlyModulatesTexturedMaterialThroughPropertyBlock()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("texture-tint", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'TintedBrick'
                    part.Material = Enum.Material.Brick
                    part.Color = Color3.fromRGB(32, 160, 224)");

                RbxInstance part = world.Workspace.FindFirstChild("TintedBrick");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                Color tint = block.GetColor("_Color");

                Assert.AreEqual(32f / 255f, tint.r, ColorEpsilon);
                Assert.AreEqual(160f / 255f, tint.g, ColorEpsilon);
                Assert.AreEqual(224f / 255f, tint.b, ColorEpsilon);
                Assert.AreEqual(tint, block.GetColor("_BaseColor"));
                Assert.AreEqual(Color.white, renderer.sharedMaterial.GetColor("_Color"));
                Assert.Greater(renderer.sharedMaterial.GetFloat("_PartColorInfluence"), 0f);
                Assert.IsTrue(world.Binder.GetPartPropertiesOrDefault(part.Id)
                    .ColorWasExplicitlySet);
            }
        }

        [Test]
        public void LuaProceduralPart_DefaultColorBehaviorIsUnchanged()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("procedural-default-color", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'DefaultConcrete'
                    part.Material = Enum.Material.Concrete");

                RbxInstance part = world.Workspace.FindFirstChild("DefaultConcrete");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                Color expected = new(163f / 255f, 162f / 255f, 165f / 255f, 1f);

                Assert.AreEqual(expected, block.GetColor("_Color"));
                Assert.AreEqual(expected, block.GetColor("_BaseColor"));
                Assert.IsFalse(world.Binder.GetPartPropertiesOrDefault(part.Id)
                    .ColorWasExplicitlySet);
            }
        }

        [Test]
        public void ManyLuaParts_ReuseOneTexturedHandleWithoutNativeMaterialAllocation()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("texture-warm", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'Wood0'
                    part.Material = Enum.Material.Wood");
                RbxInstance firstPart = world.Workspace.FindFirstChild("Wood0");
                Material expected = world.BoundObject(firstPart).GetComponent<Renderer>().sharedMaterial;
                int allocationsBefore = RbxTextureMaterialProvider.SharedMaterialAllocationCount;
                Assert.AreEqual(6, allocationsBefore);

                world.Stack.Runtime.LoadMod("texture-many", @"
                    for index = 1, 64 do
                        local part = Instance.new('Part', workspace)
                        part.Name = 'Wood' .. tostring(index)
                        part.Material = Enum.Material.Wood
                    end");

                for (int index = 1; index <= 64; index++)
                {
                    RbxInstance part = world.Workspace.FindFirstChild("Wood" + index);
                    Material actual = world.BoundObject(part).GetComponent<Renderer>().sharedMaterial;
                    Assert.AreSame(expected, actual, "Wood" + index);
                }

                Assert.AreEqual(allocationsBefore,
                    RbxTextureMaterialProvider.SharedMaterialAllocationCount,
                    "part material changes must not construct native Material instances");
            }
        }

        [Test]
        public void PartialTextureSet_UsesVisibleFallbackThroughLuaBinderPath()
        {
            LogAssert.Expect(LogType.Error,
                new Regex("Incomplete PBR texture set for Enum\\.Material\\.Brick.*" +
                          "visible diagnostic fallback"));
            RbxTextureMaterialProvider provider = new(LoadWithoutBrickNormal);
            using (Mvp1AcceptanceWorld world = new(materialProvider: provider))
            {
                world.Stack.Runtime.LoadMod("texture-missing", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'BrokenBrick'
                    part.Material = Enum.Material.Brick");

                RbxInstance part = world.Workspace.FindFirstChild("BrokenBrick");
                Material material = world.BoundObject(part).GetComponent<Renderer>().sharedMaterial;

                Assert.AreSame(provider.FallbackMaterial, material);
                Assert.AreEqual("CoreAiRbxMaterial_FALLBACK_UNMAPPED", material.name);
                Assert.AreEqual("CoreAI/Rbx/Material Fallback", material.shader.name);
            }
        }

        [Test]
        public void TextureFreeCatalog_UsesProceduralDefaultThroughLuaBinderPath()
        {
            LogAssert.Expect(LogType.Warning,
                "[CoreAI.RbxApi] No texture-backed material resources were found; the complete " +
                "procedural catalog remains active.");
            RbxTextureMaterialProvider provider = new(
                delegate(string resourcePath) { return null; });
            using (Mvp1AcceptanceWorld world = new(materialProvider: provider))
            {
                world.Stack.Runtime.LoadMod("texture-free", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'ProceduralBrick'
                    part.Material = Enum.Material.Brick");

                RbxInstance part = world.Workspace.FindFirstChild("ProceduralBrick");
                Material material = world.BoundObject(part).GetComponent<Renderer>().sharedMaterial;

                Assert.AreEqual("CoreAiRbxMaterial_Brick", material.name);
                Assert.AreEqual("CoreAI/Rbx/Procedural Surface", material.shader.name);
            }
        }

        [TestCase("Bricks104_1K-JPG_Color.jpg")]
        [TestCase("Wood095_1K-JPG_Color.jpg")]
        [TestCase("Grass005_1K-JPG_Color.jpg")]
        [TestCase("PavingStones151_1K-JPG_Color.jpg")]
        [TestCase("Metal063_1K-JPG_Color.jpg")]
        public void ColorTextureImport_IsSrgb(string fileName)
        {
            TextureImporter importer = GetTextureImporter(fileName);

            Assert.IsTrue(importer.sRGBTexture, fileName);
            Assert.AreEqual(TextureImporterType.Default, importer.textureType, fileName);
        }

        [TestCase("Bricks104_1K-JPG_NormalGL.jpg")]
        [TestCase("Wood095_1K-JPG_NormalGL.jpg")]
        [TestCase("Grass005_1K-JPG_NormalGL.jpg")]
        [TestCase("PavingStones151_1K-JPG_NormalGL.jpg")]
        [TestCase("Metal063_1K-JPG_NormalGL.jpg")]
        public void NormalGlTextureImport_IsNormalMapWithoutGreenFlip(string fileName)
        {
            TextureImporter importer = GetTextureImporter(fileName);

            Assert.IsFalse(importer.sRGBTexture, fileName);
            Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType, fileName);
            Assert.IsFalse(importer.flipGreenChannel, fileName);
        }

        [TestCase("Bricks104_1K-JPG_Roughness.jpg")]
        [TestCase("Wood095_1K-JPG_Roughness.jpg")]
        [TestCase("Grass005_1K-JPG_Roughness.jpg")]
        [TestCase("PavingStones151_1K-JPG_Roughness.jpg")]
        [TestCase("Metal063_1K-JPG_Roughness.jpg")]
        [TestCase("Metal063_1K-JPG_Metalness.jpg")]
        public void DataTextureImport_IsLinear(string fileName)
        {
            TextureImporter importer = GetTextureImporter(fileName);

            Assert.IsFalse(importer.sRGBTexture, fileName);
            Assert.AreEqual(TextureImporterType.Default, importer.textureType, fileName);
        }

        [Test]
        public void TexturedShader_UsesNarrowGeometricNormalAxisBlendWithoutParallax()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxTexturedSurface.shader");
            string source = File.ReadAllText(path);

            StringAssert.Contains("static const float RBX_AXIS_BLEND_WIDTH = 0.10;", source);
            StringAssert.Contains(
                "float3 projectionWeights = RbxNarrowAxisWeights(geometricNormalAligned);", source);
            StringAssert.Contains("UNITY_BRANCH if (projectionWeights.x > 0.0)", source);
            StringAssert.Contains("UNITY_BRANCH if (projectionWeights.y > 0.0)", source);
            StringAssert.Contains("UNITY_BRANCH if (projectionWeights.z > 0.0)", source);
            StringAssert.IsMatch(
                "output\\.positionAligned\\s*=\\s*input\\.positionOS\\.xyz\\s*\\*\\s*" +
                "RbxTextureObjectAxisScale\\(\\)", source);
            StringAssert.IsMatch(
                "float2\\s+uvScale\\s*=\\s*float2\\(\\s*_TextureScale\\s*/\\s*" +
                "max\\(_TextureAspect,\\s*0\\.0001\\),\\s*_TextureScale\\s*\\)", source);
            StringAssert.Contains("#if defined(_RBX_METALLIC_MAP)", source);
            StringAssert.DoesNotContain("RbxDominantAxisUv", source);
            StringAssert.DoesNotContain("Parallax", source);
            Assert.AreEqual(4, Regex.Matches(source, "SAMPLE_TEXTURE2D_GRAD\\(").Count,
                "one projection reads three maps; Metal063 enables the fourth read");
            Assert.AreEqual(0, Regex.Matches(source, "SAMPLE_TEXTURE2D\\(").Count);
        }

        private static Texture2D LoadWithoutBrickNormal(string resourcePath)
        {
            if (string.Equals(resourcePath,
                "CoreAIRbxTextures/Bricks104_1K-JPG_NormalGL", StringComparison.Ordinal))
            {
                return null;
            }

            return Resources.Load<Texture2D>(resourcePath);
        }

        private static TextureImporter GetTextureImporter(string fileName)
        {
            string path = "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/" +
                          "CoreAIRbxTextures/" + fileName;
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Assert.IsNotNull(importer, path);
            Assert.IsTrue(importer.mipmapEnabled, path);
            Assert.AreEqual(TextureWrapMode.Repeat, importer.wrapMode, path);
            Assert.IsFalse(importer.isReadable, path);
            return importer;
        }
    }
}
