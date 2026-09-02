using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Adversarial QA gates for the 45-item Enum.Material catalog: Roblox parity of the
    /// Part.Color contract, stud-authored texture tiling across a session-scale replacement, and
    /// the binder path from public Lua writes to the renderer. Tests that never touch a native
    /// engine object are also runnable off-device through reflection.</summary>
    [TestFixture]
    public sealed class RbxMaterialCatalogQaEditModeTests
    {
        private const float Epsilon = 1e-5f;
        private const int NeonValue = 288;
        private const int WoodValue = 512;
        private const int BrickValue = 848;
        private const float BrickTileWidthStuds = 10f;
        private const float WoodTileWidthStuds = 10f;

        private static readonly string[] ExpectedTexturedNames =
        {
            "Wood", "WoodPlanks", "Brick", "Cobblestone", "Metal", "Grass"
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

        // ---- Engine-free catalog contracts ------------------------------------------------

        [Test]
        public void ProceduralCatalog_CoversEveryPublicMaterialEnumItemExactlyOnce()
        {
            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            IReadOnlyList<RbxEnumItem> items = materialEnum.GetEnumItems();
            IReadOnlyList<RbxMaterialId> supported = RbxProceduralMaterialProvider.SupportedMaterials;
            HashSet<string> names = new(StringComparer.Ordinal);
            HashSet<int> values = new();

            Assert.AreEqual(45, items.Count, "public Enum.Material item count");
            Assert.AreEqual(items.Count, supported.Count, "every public item needs a render mapping");
            for (int index = 0; index < supported.Count; index++)
            {
                RbxMaterialId id = supported[index];
                Assert.IsTrue(names.Add(id.Name), "duplicate catalog name " + id.Name);
                Assert.IsTrue(values.Add(id.Value), "duplicate catalog value " + id.Value);
                Assert.IsTrue(materialEnum.TryGetItem(id.Name, out RbxEnumItem item),
                    id.Name + " is not a public Enum.Material item");
                Assert.AreEqual(item.Value, id.Value, id.Name + " value");
                Assert.IsTrue(
                    RbxProceduralMaterialProvider.TryGetPartColorContract(in id,
                        out Color intrinsicColor, out float partColorInfluence),
                    id.Name + " contract");
                Assert.That(partColorInfluence, Is.InRange(0f, 1f), id.Name + " influence");
                Assert.Greater(intrinsicColor.maxColorComponent, 0.1f, id.Name + " intrinsic");
            }

            for (int index = 0; index < items.Count; index++)
            {
                Assert.IsTrue(names.Contains(items[index].Name),
                    "Enum.Material." + items[index].Name + " has no catalog entry");
            }
        }

        [Test]
        public void TexturedCatalog_SixEntriesAreCanonicalEnumItemsWithProceduralTwins()
        {
            RbxEnum materialEnum = RbxEnumRegistry.CreateWithBuiltins().Get("Material");
            IReadOnlyList<RbxMaterialId> textured = RbxTextureMaterialProvider.TexturedMaterials;

            Assert.AreEqual(ExpectedTexturedNames.Length, textured.Count);
            for (int index = 0; index < textured.Count; index++)
            {
                RbxMaterialId id = textured[index];
                Assert.AreEqual(ExpectedTexturedNames[index], id.Name, "textured entry " + index);
                Assert.IsTrue(materialEnum.TryGetItem(id.Name, out RbxEnumItem item), id.Name);
                Assert.AreEqual(item.Value, id.Value, id.Name + " value");
                Assert.IsTrue(
                    RbxProceduralMaterialProvider.TryGetPartColorContract(in id, out _, out _),
                    id.Name + " must keep a procedural twin for the texture-free fallback path");
            }
        }

        [Test]
        public void NeonCatalogEntry_LetsPartColorOwnTheEmissionHue()
        {
            RbxMaterialId neon = new("Neon", NeonValue);
            Assert.IsTrue(RbxProceduralMaterialProvider.TryGetPartColorContract(in neon,
                out Color intrinsicColor, out float partColorInfluence));

            Assert.AreEqual(1f, partColorInfluence, Epsilon,
                "Neon has no intrinsic palette in Roblox; Part.Color must own the emission");
            Assert.AreEqual(1f, intrinsicColor.r, Epsilon, "Neon intrinsic red");
            Assert.AreEqual(1f, intrinsicColor.g, Epsilon, "Neon intrinsic green");
            Assert.AreEqual(1f, intrinsicColor.b, Epsilon, "Neon intrinsic blue");

            Color redEmission = NeonEmission(intrinsicColor, partColorInfluence,
                new Color(1f, 0f, 0f));
            Assert.Greater(redEmission.r, 0.95f, "red Neon must glow red");
            Assert.Less(redEmission.g, 0.05f, "red Neon must not glow green");
            Assert.Less(redEmission.b, 0.05f, "red Neon must not glow blue");

            Color defaultPartColor = new(163f / 255f, 162f / 255f, 165f / 255f);
            Color greyEmission = NeonEmission(intrinsicColor, partColorInfluence, defaultPartColor);
            Assert.AreEqual(defaultPartColor.r, greyEmission.r, Epsilon, "default grey Neon red");
            Assert.AreEqual(defaultPartColor.g, greyEmission.g, Epsilon, "default grey Neon green");
            Assert.AreEqual(defaultPartColor.b, greyEmission.b, Epsilon, "default grey Neon blue");

            string neonSource = File.ReadAllText(Path.Combine(ShaderRoot(),
                "RbxProceduralNeon.shader"));
            StringAssert.Contains(
                "half3 emissionColor = _MaterialColor.rgb * lerp(half3(1.0h, 1.0h, 1.0h), _Color.rgb,",
                neonSource);
            StringAssert.Contains("return half4(emissionColor * intensity, 1.0h);", neonSource);
            StringAssert.DoesNotContain("RbxComposeMaterialColor(", neonSource);
        }

        [TestCase(1f, 10f, 0.28f)]
        [TestCase(1f, 10f, 0.5f)]
        [TestCase(1.5f, 7f, 0.28f)]
        [TestCase(1f, 3.5f, 1f)]
        public void TexturedTextureScale_IsCyclesPerMetreOfAStudAuthoredTileWidth(
            float textureAspect, float tileWidthStuds, float metersPerStud)
        {
            const float faceStuds = 3f;
            float textureScale = RbxTextureMaterialProvider.ComputeTextureScale(textureAspect,
                tileWidthStuds, metersPerStud);
            float faceMeters = faceStuds * metersPerStud;
            float horizontalTiles = faceMeters * textureScale / textureAspect;

            Assert.That(textureScale,
                Is.EqualTo(textureAspect / (tileWidthStuds * metersPerStud)).Within(1e-6f));
            Assert.That(horizontalTiles, Is.EqualTo(faceStuds / tileWidthStuds).Within(1e-5f),
                "a three-stud face must show the same tile count at every session scale");
        }

        [Test]
        public void CatalogShaders_NeverRaiseAnUnclampedBaseToAPower()
        {
            string[] files = Directory.GetFiles(ShaderRoot(), "*.*", SearchOption.TopDirectoryOnly);
            Regex powCall = new("\\bpow\\(\\s*([^,]+),", RegexOptions.Compiled);
            int occurrences = 0;

            for (int index = 0; index < files.Length; index++)
            {
                string file = files[index];
                if (!file.EndsWith(".shader", StringComparison.Ordinal)
                    && !file.EndsWith(".hlsl", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = File.ReadAllText(file);
                foreach (Match match in powCall.Matches(source))
                {
                    occurrences++;
                    string baseExpression = match.Groups[1].Value.Trim();
                    bool clamped = baseExpression.StartsWith("saturate(", StringComparison.Ordinal)
                                   || baseExpression.StartsWith("max(", StringComparison.Ordinal)
                                   || baseExpression.StartsWith("1.0h - saturate(",
                                       StringComparison.Ordinal)
                                   || baseExpression.StartsWith("1.0 - saturate(",
                                       StringComparison.Ordinal);
                    Assert.IsTrue(clamped, Path.GetFileName(file) + ": pow base '" + baseExpression
                                           + "' can go negative and produce NaN");
                }
            }

            Assert.GreaterOrEqual(occurrences, 7, "the lint must see the catalog's pow calls");
        }

        // ---- Unity-backed production-path gates -------------------------------------------

        [Test]
        public void LuaNeonPart_PartColorReachesTheEmissionContractThroughTheBinder()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("neon-red", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'RedNeon'
                    part.Material = Enum.Material.Neon
                    part.Color = Color3.fromRGB(255, 0, 0)");

                RbxInstance part = world.Workspace.FindFirstChild("RedNeon");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                Material material = renderer.sharedMaterial;
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                Color tint = block.GetColor("_Color");

                Assert.AreEqual("CoreAI/Rbx/Procedural Neon", material.shader.name);
                Assert.AreEqual(Color.white, material.GetColor("_MaterialColor"),
                    "Neon must not carry an intrinsic hue that overrides Part.Color");
                Assert.AreEqual(1f, material.GetFloat("_PartColorInfluence"), Epsilon);
                Assert.AreEqual(1f, tint.r, Epsilon);
                Assert.AreEqual(0f, tint.g, Epsilon);
                Assert.AreEqual(0f, tint.b, Epsilon);
            }
        }

        [Test]
        public void TexturedTileWidth_TracksSessionScaleReplacementWithoutReallocating()
        {
            RbxSpace.ResetForTests(0.28f);
            try
            {
                RbxTextureMaterialProvider provider = new();
                RbxMaterialId brick = new("Brick", BrickValue);
                RbxMaterialId wood = new("Wood", WoodValue);
                Assert.IsTrue(provider.TryGetMaterial(in brick, out Material before));
                float brickAspect = before.GetFloat("_TextureAspect");
                Assert.That(before.GetFloat("_TextureScale"),
                    Is.EqualTo(brickAspect / (BrickTileWidthStuds * 0.28f)).Within(1e-4f));
                int allocations = RbxTextureMaterialProvider.SharedMaterialAllocationCount;

                Action rollback = RbxSpace.BeginSessionReplacement(0.5f);
                try
                {
                    Assert.IsTrue(provider.TryGetMaterial(in brick, out Material after));
                    Assert.AreSame(before, after, "the shared handle must survive a scale change");
                    Assert.That(after.GetFloat("_TextureScale"),
                        Is.EqualTo(brickAspect / (BrickTileWidthStuds * 0.5f)).Within(1e-4f),
                        "tile widths are authored in studs; a replaced session scale must " +
                        "re-derive cycles per metre");
                    Assert.IsTrue(provider.TryGetMaterial(in wood, out Material woodMaterial));
                    float woodAspect = woodMaterial.GetFloat("_TextureAspect");
                    Assert.That(woodMaterial.GetFloat("_TextureScale"),
                        Is.EqualTo(woodAspect / (WoodTileWidthStuds * 0.5f)).Within(1e-4f));
                    Assert.AreEqual(allocations,
                        RbxTextureMaterialProvider.SharedMaterialAllocationCount,
                        "rescaling must not construct native materials");
                }
                finally
                {
                    rollback();
                }

                Assert.IsTrue(provider.TryGetMaterial(in brick, out Material restored));
                Assert.That(restored.GetFloat("_TextureScale"),
                    Is.EqualTo(brickAspect / (BrickTileWidthStuds * 0.28f)).Within(1e-4f),
                    "a rolled-back replacement must restore the previous tiling");
            }
            finally
            {
                RbxSpace.ResetForTests();
            }
        }

        [Test]
        public void LuaPart_MaterialReassignmentKeepsColorAndColorReassignmentKeepsMaterial()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("swap-material", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'Swap'
                    part.Material = Enum.Material.Brick
                    part.Color = Color3.fromRGB(32, 160, 224)
                    part.Material = Enum.Material.Concrete");

                RbxInstance part = world.Workspace.FindFirstChild("Swap");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                Assert.AreEqual("CoreAiRbxMaterial_Concrete", renderer.sharedMaterial.name);
                AssertTint(renderer, 32, 160, 224, "Material reassignment must keep Part.Color");

                world.Stack.Runtime.LoadMod("swap-color", @"
                    local part = workspace:FindFirstChild('Swap')
                    part.Color = Color3.fromRGB(200, 40, 40)");

                Assert.AreEqual("CoreAiRbxMaterial_Concrete", renderer.sharedMaterial.name,
                    "Color reassignment must keep Part.Material");
                AssertTint(renderer, 200, 40, 40, "Color reassignment must reach the renderer");

                world.Stack.Runtime.LoadMod("swap-back", @"
                    local part = workspace:FindFirstChild('Swap')
                    part.Material = Enum.Material.Brick");

                Assert.AreEqual("CoreAiRbxTextureMaterial_Brick", renderer.sharedMaterial.name);
                AssertTint(renderer, 200, 40, 40,
                    "an explicitly set Part.Color must tint a textured material too");
            }
        }

        [Test]
        public void MismatchedMaterialId_ThroughBinderResolvesVisibleFallbackNotErrorShader()
        {
            using (Mvp1AcceptanceWorld world = new())
            {
                world.Stack.Runtime.LoadMod("mismatch", @"
                    local part = Instance.new('Part', workspace)
                    part.Name = 'Broken'
                    part.Material = Enum.Material.Brick");

                RbxInstance part = world.Workspace.FindFirstChild("Broken");
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                world.Binder.SetMaterial(part.Id, new RbxMaterialId("Brick", WoodValue));
                Material broken = renderer.sharedMaterial;

                Assert.AreEqual("CoreAiRbxMaterial_FALLBACK_UNMAPPED", broken.name);
                Assert.AreEqual("CoreAI/Rbx/Material Fallback", broken.shader.name);
                Assert.AreNotEqual("Hidden/InternalErrorShader", broken.shader.name);
                Assert.IsTrue(renderer.enabled, "the diagnostic must stay visible");

                world.Binder.SetMaterial(part.Id, new RbxMaterialId("Brick", BrickValue));
                Assert.AreEqual("CoreAiRbxTextureMaterial_Brick", renderer.sharedMaterial.name,
                    "a later valid id must recover from the diagnostic without a rebuild");
            }
        }

        /// <summary>Mirror of the Neon shader emission line
        /// (<c>_MaterialColor.rgb * lerp(1, _Color.rgb, saturate(_PartColorInfluence))</c>).</summary>
        private static Color NeonEmission(Color intrinsicColor, float partColorInfluence,
            Color partColor)
        {
            float influence = Mathf.Clamp01(partColorInfluence);
            return new Color(
                intrinsicColor.r * Mathf.Lerp(1f, partColor.r, influence),
                intrinsicColor.g * Mathf.Lerp(1f, partColor.g, influence),
                intrinsicColor.b * Mathf.Lerp(1f, partColor.b, influence));
        }

        private static void AssertTint(Renderer renderer, int red, int green, int blue, string why)
        {
            MaterialPropertyBlock block = new();
            renderer.GetPropertyBlock(block);
            Color tint = block.GetColor("_Color");
            Assert.AreEqual(red / 255f, tint.r, Epsilon, why);
            Assert.AreEqual(green / 255f, tint.g, Epsilon, why);
            Assert.AreEqual(blue / 255f, tint.b, Epsilon, why);
            Assert.AreEqual(tint, block.GetColor("_BaseColor"), why);
        }

        // WHY: Application.dataPath is a native call; walking up from the test assembly finds the
        // project root both under Library/ScriptAssemblies and under the generated csproj output,
        // so the shader-source gates stay runnable off-device.
        private static string ShaderRoot()
        {
            string directory = Path.GetDirectoryName(
                typeof(RbxMaterialCatalogQaEditModeTests).Assembly.Location);
            while (directory != null
                   && !Directory.Exists(Path.Combine(directory, "Assets", "CoreAIMods")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            Assert.IsNotNull(directory, "project root not found above the test assembly");
            return Path.Combine(directory, "Assets", "CoreAIMods", "Runtime", "RbxApi", "Unity",
                "Resources", "CoreAIRbxMaterials");
        }
    }
}
