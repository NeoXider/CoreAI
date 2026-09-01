using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Production-contract coverage for the runtime procedural material catalog.</summary>
    [TestFixture]
    public sealed class RbxProceduralMaterialProviderEditModeTests
    {
        private const int WoodValue = 512;
        private const int WoodPlanksValue = 528;
        private const int MarbleValue = 784;
        private const int SlateValue = 800;
        private const int BrickValue = 848;
        private const int CobblestoneValue = 880;
        private const int DiamondPlateValue = 1056;
        private const int MetalValue = 1088;
        private const int GrassValue = 1280;
        private const int SandValue = 1296;
        private const int FabricValue = 1312;
        private const int GroundValue = 1360;
        private const int LookupIterations = 4096;
        private const float ColorEpsilon = 1e-5f;

        private RbxProceduralMaterialProvider _provider;

        [SetUp]
        public void SetUp()
        {
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            _provider = new RbxProceduralMaterialProvider();
        }

        [TearDown]
        public void TearDown()
        {
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [Test]
        public void PartMaterialState_NonDefault_ResolvesDifferentSharedMaterialThanDefault()
        {
            PartProperties properties = PartProperties.CreateDefault();
            bool defaultMapped = _provider.TryGetMaterial(in properties.Material, out Material defaultMaterial);

            properties.Material = new RbxMaterialId("Wood", WoodValue);
            bool woodMapped = _provider.TryGetMaterial(in properties.Material, out Material woodMaterial);

            Assert.IsTrue(defaultMapped);
            Assert.IsTrue(woodMapped);
            Assert.AreNotSame(defaultMaterial, woodMaterial,
                "changing Part.Material must change the shared render handle");
            Assert.AreEqual("CoreAiRbxMaterial_Plastic", defaultMaterial.name);
            Assert.AreEqual("CoreAiRbxMaterial_Wood", woodMaterial.name);
        }

        [Test]
        public void UnmappedValue_ResolvesDocumentedVisibleFallback()
        {
            RbxMaterialId unmapped = new RbxMaterialId("FutureMaterial", int.MaxValue);

            bool mapped = _provider.TryGetMaterial(in unmapped, out Material material);

            Assert.IsFalse(mapped);
            Assert.AreSame(_provider.FallbackMaterial, material);
            Assert.AreEqual("CoreAiRbxMaterial_FALLBACK_UNMAPPED", material.name);
            Assert.AreEqual("CoreAI/Rbx/Material Fallback", material.shader.name);
        }

        [Test]
        public void CanonicalValueWithWrongName_ResolvesFallback()
        {
            RbxMaterialId stale = new RbxMaterialId("NotWood", WoodValue);

            bool mapped = _provider.TryGetMaterial(in stale, out Material material);

            Assert.IsFalse(mapped);
            Assert.AreSame(_provider.FallbackMaterial, material);
        }

        [Test]
        public void RepeatedPartLookups_ReuseSharedHandleWithoutManagedOrNativeAllocations()
        {
            RbxMaterialId wood = new RbxMaterialId("Wood", WoodValue);
            bool initiallyMapped = _provider.TryGetMaterial(in wood, out Material expected);
            Assert.IsTrue(initiallyMapped);

            for (int warmupIndex = 0; warmupIndex < 32; warmupIndex++)
            {
                _provider.TryGetMaterial(in wood, out Material warmupMaterial);
            }

            int nativeAllocationsBefore = RbxProceduralMaterialProvider.SharedMaterialAllocationCount;
            bool everyLookupMapped = true;
            bool everyHandleShared = true;
            long managedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int lookupIndex = 0; lookupIndex < LookupIterations; lookupIndex++)
            {
                bool mapped = _provider.TryGetMaterial(in wood, out Material resolved);
                everyLookupMapped &= mapped;
                everyHandleShared &= ReferenceEquals(expected, resolved);
            }

            long managedBytesAfter = GC.GetAllocatedBytesForCurrentThread();
            int nativeAllocationsAfter = RbxProceduralMaterialProvider.SharedMaterialAllocationCount;

            Assert.IsTrue(everyLookupMapped);
            Assert.IsTrue(everyHandleShared);
            Assert.AreEqual(nativeAllocationsBefore, nativeAllocationsAfter,
                "lookups must not construct a native Material per part");
            Assert.AreEqual(0L, managedBytesAfter - managedBytesBefore,
                "the warmed lookup path must not allocate managed memory");
        }

        [Test]
        public void SharedCache_ContainsOneMaterialPerCatalogEntryPlusFallback()
        {
            Material fallback = _provider.FallbackMaterial;
            HashSet<Material> materials = new HashSet<Material> { fallback };
            IReadOnlyList<RbxMaterialId> supported = RbxProceduralMaterialProvider.SupportedMaterials;

            for (int index = 0; index < supported.Count; index++)
            {
                RbxMaterialId id = supported[index];
                bool mapped = _provider.TryGetMaterial(in id, out Material material);
                Assert.IsTrue(mapped, id.ToString());
                Assert.IsTrue(materials.Add(material), id + " must own a distinct shared handle");
                Assert.AreEqual(ExpectedShaderName(id.Name), material.shader.name, id.ToString());
            }

            Assert.AreEqual(22, supported.Count);
            Assert.AreEqual(supported.Count + 1, materials.Count);
            Assert.AreEqual(supported.Count + 1,
                RbxProceduralMaterialProvider.SharedMaterialAllocationCount);
        }

        [Test]
        public void CatalogMaterials_OwnUniqueVisibleProceduralSignatures()
        {
            IReadOnlyList<RbxMaterialId> supported = RbxProceduralMaterialProvider.SupportedMaterials;
            HashSet<Color32> intrinsicColors = new HashSet<Color32>();
            HashSet<Color32> baseColors = new HashSet<Color32>();
            HashSet<int> surfaceModes = new HashSet<int>();

            for (int index = 0; index < supported.Count; index++)
            {
                RbxMaterialId id = supported[index];
                bool mapped = _provider.TryGetMaterial(in id, out Material material);

                Assert.IsTrue(mapped, id.ToString());
                Assert.IsTrue(material.HasProperty("_BaseColor"), id.ToString());
                Assert.IsTrue(material.HasProperty("_Color"), id.ToString());
                Assert.IsTrue(material.HasProperty("_MaterialColor"), id.ToString());
                Assert.IsTrue(material.HasProperty("_PartColorInfluence"), id.ToString());
                Assert.IsTrue(material.HasProperty("_MaterialMode"), id.ToString());
                Assert.IsTrue(material.HasProperty("_PatternScale"), id.ToString());
                Assert.IsTrue(material.HasProperty("_BumpStrength"), id.ToString());

                Color baseColor = material.GetColor("_BaseColor");
                Color intrinsicColor = material.GetColor("_MaterialColor");
                Assert.AreEqual(intrinsicColor.r, baseColor.r, ColorEpsilon,
                    id + " must expose its provider-owned red identity through _BaseColor");
                Assert.AreEqual(intrinsicColor.g, baseColor.g, ColorEpsilon,
                    id + " must expose its provider-owned green identity through _BaseColor");
                Assert.AreEqual(intrinsicColor.b, baseColor.b, ColorEpsilon,
                    id + " must expose its provider-owned blue identity through _BaseColor");
                Assert.AreEqual(intrinsicColor.a, baseColor.a, ColorEpsilon,
                    id + " must expose its provider-owned alpha identity through _BaseColor");
                Assert.AreEqual(Color.white, material.GetColor("_Color"),
                    id + " must reserve _Color for an optional per-renderer part tint");
                Assert.Greater(intrinsicColor.maxColorComponent, 0.1f, id.ToString());
                Assert.IsTrue(intrinsicColors.Add((Color32)intrinsicColor),
                    id + " must own a distinct intrinsic palette color");
                Assert.IsTrue(baseColors.Add((Color32)baseColor),
                    id + " must expose a distinct base palette color");
                Assert.AreEqual(ExpectedMode(id.Name), Mathf.RoundToInt(material.GetFloat("_MaterialMode")),
                    id.ToString());
                Assert.Greater(material.GetFloat("_PatternScale"), 0f, id.ToString());
                Assert.GreaterOrEqual(material.GetFloat("_BumpStrength"), 0f, id.ToString());
                Assert.That(material.GetFloat("_PartColorInfluence"), Is.InRange(0f, 1f), id.ToString());

                if (string.Equals(material.shader.name, "CoreAI/Rbx/Procedural Surface",
                    StringComparison.Ordinal))
                {
                    Assert.IsTrue(surfaceModes.Add(Mathf.RoundToInt(material.GetFloat("_MaterialMode"))),
                        id + " must own a distinct opaque procedural mode");
                }
            }

            Assert.AreEqual(22, intrinsicColors.Count);
            Assert.AreEqual(22, baseColors.Count);
            Assert.AreEqual(18, surfaceModes.Count);
        }

        [Test]
        public void ProceduralCommon_FractionalPowerClampsNegativeNoiseToZero()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralCommon.hlsl");
            string source = File.ReadAllText(path);

            StringAssert.Contains(
                "pow(max(crystalNoise, 0.0), 2.4)", source);
            StringAssert.DoesNotContain("pow(RbxValueNoise(", source);
        }

        [Test]
        public void NoiseShaderVendor_CoreSourcesAndLicenseRemainByteIdentical()
        {
            string root = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "NoiseShader");

            AssertSha256(Path.Combine(root, "ClassicNoise2D.hlsl"),
                "b8dd33086fe80b8780225cc2a6fa8206423630ea280c6069191cf43ecad9e644");
            AssertSha256(Path.Combine(root, "ClassicNoise3D.hlsl"),
                "32c3910c76599d4ce9bc18e015841e67f189aa4b11ccddbf7be6859c39f11978");
            AssertSha256(Path.Combine(root, "Common.hlsl"),
                "f4d34c6fa5eaf4d1b9ec369de840232ba9798f6e099601176461221f2efa6e6d");
            AssertSha256(Path.Combine(root, "Noise1D.hlsl"),
                "de8c079a6f7d36a6c317715860d50dc9f266aa2215cd4e026d49c4d3924f40dd");
            AssertSha256(Path.Combine(root, "SimplexNoise2D.hlsl"),
                "8f72b4caabb0154df4d44279c5256c8371042aea9137c36bd3101b3aae2ee243");
            AssertSha256(Path.Combine(root, "SimplexNoise3D.hlsl"),
                "003bd6f2366f432cfb40d8d212da2f012c424b2b737e3418bc9da397bd540c6a");
            AssertSha256(Path.Combine(root, "LICENSE"),
                "bdafce1bb01517c9ae6c4f3620c01340790b5e9d039ae9e356347d1174250916");
        }

        [Test]
        public void ProceduralNormals_UseVendoredAnalyticalDerivativesWithoutHeightResampling()
        {
            string root = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials");
            string wrapperSource = File.ReadAllText(Path.Combine(root, "RbxNoiseShader.hlsl"));
            string commonSource = File.ReadAllText(Path.Combine(root, "RbxProceduralCommon.hlsl"));
            string surfaceSource = File.ReadAllText(Path.Combine(root, "RbxProceduralSurface.shader"));

            StringAssert.Contains("#include \"NoiseShader/Common.hlsl\"", wrapperSource);
            StringAssert.Contains("float4 RbxSimplexNoiseGrad", wrapperSource);
            StringAssert.Contains("float4 RbxSimplexFbmGrad", wrapperSource);
            StringAssert.Contains("RbxSimplexFbmGrad", commonSource);
            StringAssert.Contains("ddx(centerHeight)", commonSource);
            StringAssert.Contains("float3 analyticalGradient", commonSource);
            StringAssert.Contains("procedural.heightGradient", surfaceSource);
            StringAssert.DoesNotContain("float epsilon", commonSource);
            StringAssert.DoesNotContain("RbxEvaluateSurface(patternPosition +", commonSource);
        }

        [Test]
        public void ProjectionSensitivePatterns_UseNarrowGeometricNormalAxisBlending()
        {
            string commonPath = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralCommon.hlsl");
            string surfacePath = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralSurface.shader");
            string commonSource = File.ReadAllText(commonPath);
            string surfaceSource = File.ReadAllText(surfacePath);

            StringAssert.Contains("uvX = position.zy;", commonSource);
            StringAssert.Contains("uvY = position.xz;", commonSource);
            StringAssert.Contains("uvZ = position.xy;", commonSource);
            StringAssert.Contains("static const float RBX_AXIS_BLEND_WIDTH = 0.10;", commonSource);
            StringAssert.Contains("float3 componentDelta = dominantComponent.xxx - absoluteNormal;",
                commonSource);
            StringAssert.Contains(
                "float3 weights = 1.0 - smoothstep(0.0, RBX_AXIS_BLEND_WIDTH, componentDelta);",
                commonSource);
            StringAssert.Contains("return weights / weightSum;", commonSource);
            StringAssert.DoesNotContain("RbxProjectedUv", commonSource);
            StringAssert.Contains("RbxBlendDiamondPlatePattern(position, weights, diamond, treadEdge)",
                commonSource);
            StringAssert.Contains("RbxBlendSlatePattern(position, weights", commonSource);
            StringAssert.Contains("RbxBlendCobblestonePattern(position, weights", commonSource);
            StringAssert.Contains("RbxBlendGrassPattern(position, weights", commonSource);
            StringAssert.Contains("RbxBlendSandPattern(position, weights", commonSource);
            StringAssert.Contains("RbxBlendGroundPattern(position, weights", commonSource);
            StringAssert.Contains("RbxBlendFabricPattern(position, weights, weave, weaveVisibility)",
                commonSource);
            StringAssert.Contains("input.positionOS.xyz * RbxObjectAxisScale()", surfaceSource);
            StringAssert.Contains("RbxUsesObjectAlignedProjection(materialMode)", surfaceSource);
            StringAssert.Contains(
                "return materialMode == 3 || materialMode == 4 || materialMode == 5 || materialMode == 8 ||",
                commonSource);
            StringAssert.Contains(
                "materialMode == 13 || materialMode == 14 || materialMode == 17;",
                commonSource);
        }

        [Test]
        public void ProjectionAxes_UseGeometricNormalsBeforeReliefPerturbation()
        {
            string root = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials");
            string texturedSource = File.ReadAllText(Path.Combine(root,
                "RbxTexturedSurface.shader"));
            string surfaceSource = File.ReadAllText(Path.Combine(root,
                "RbxProceduralSurface.shader"));
            string transparentSource = File.ReadAllText(Path.Combine(root,
                "RbxProceduralTransparent.shader"));

            int texturedProjection = texturedSource.IndexOf(
                "float3 projectionWeights = RbxNarrowAxisWeights(geometricNormalAligned);",
                StringComparison.Ordinal);
            int texturedPerturbation = texturedSource.IndexOf(
                "float3 normalWS = normalize(mappedNormalWS);", StringComparison.Ordinal);
            Assert.That(texturedProjection, Is.GreaterThanOrEqualTo(0));
            Assert.That(texturedPerturbation, Is.GreaterThan(texturedProjection));
            StringAssert.DoesNotContain("RbxNarrowAxisWeights(normalWS)", texturedSource);

            int proceduralProjection = surfaceSource.IndexOf(
                "geometricPatternNormal, materialMode", StringComparison.Ordinal);
            int proceduralPerturbation = surfaceSource.IndexOf(
                "float3 normalWS = RbxPerturbNormal", StringComparison.Ordinal);
            Assert.That(proceduralProjection, Is.GreaterThanOrEqualTo(0));
            Assert.That(proceduralPerturbation, Is.GreaterThan(proceduralProjection));
            StringAssert.DoesNotContain(
                "RbxEvaluateSurface(patternPosition, normalWS", surfaceSource);

            int forceFieldProjection = transparentSource.IndexOf(
                "RbxNarrowAxisWeights(geometricNormalWS)", StringComparison.Ordinal);
            int transparentPerturbation = transparentSource.IndexOf(
                "normalWS = RbxTransparentNormal", StringComparison.Ordinal);
            Assert.That(forceFieldProjection, Is.GreaterThanOrEqualTo(0));
            Assert.That(transparentPerturbation, Is.GreaterThan(forceFieldProjection));
        }

        [Test]
        public void MetalPattern_UsesFilteredDirectionalMachiningVariation()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralCommon.hlsl");
            string source = File.ReadAllText(path);

            StringAssert.Contains("float metalVariation = RbxFbm(physicalPosition * 1.45", source);
            StringAssert.Contains("RbxBlendDirectionalMachiningPattern(position, weights, 260.0",
                source);
            StringAssert.Contains("physicalPosition * 420.0", source);
            StringAssert.DoesNotContain("sin(uv.x * 34.0", source);
        }

        [Test]
        public void CorrectedMaterials_KeepIdentityAndTuningInRuntimeProvider()
        {
            AssertProviderTuning("WoodPlanks", WoodPlanksValue, 3, 5f, 0.34f);
            AssertProviderTuning("Metal", MetalValue, 4, 1f, 0.15f);
            AssertProviderTuning("Marble", MarbleValue, 7, 4f, 0.11f);
            AssertProviderTuning("Brick", BrickValue, 10, 5.6f, 0.42f);
            AssertProviderTuning("Cobblestone", CobblestoneValue, 11, 4f, 0.48f);
            AssertProviderTuning("Grass", GrassValue, 12, 2f, 0.38f);
            AssertProviderTuning("Ground", GroundValue, 14, 3f, 0.44f);
            AssertProviderTuning("DiamondPlate", DiamondPlateValue, 5, 5f, 1f);
            AssertProviderTuning("Fabric", FabricValue, 17, 6f, 0.28f);
        }

        [TestCase("Wood", WoodValue, 18f / (2f * Mathf.PI), 0.0291f)]
        [TestCase("WoodPlanks", WoodPlanksValue, 1.35f, 0.1481f)]
        [TestCase("Marble", MarbleValue, 2.15f / (2f * Mathf.PI), 0.7306f)]
        [TestCase("Slate", SlateValue, 13f / (2f * Mathf.PI), 0.0806f)]
        [TestCase("Brick", BrickValue, 1.28f, 0.1395f)]
        [TestCase("Cobblestone", CobblestoneValue, 1.35f, 0.1852f)]
        [TestCase("DiamondPlate", DiamondPlateValue, 1.35f, 0.1481f)]
        [TestCase("Metal", MetalValue, 260f, 0.00385f)]
        [TestCase("Grass", GrassValue, 3.2f, 0.1563f)]
        [TestCase("Sand", SandValue, 7.5f / (2f * Mathf.PI), 0.1047f)]
        [TestCase("Fabric", FabricValue, 42f / (2f * Mathf.PI), 0.0249f)]
        [TestCase("Ground", GroundValue, 1.18f, 0.2825f)]
        public void StructuredPatterns_ExposeCalibratedPhysicalFeatureSize(string name, int value,
            float repeatsPerPatternUnit, float expectedFeatureMeters)
        {
            RbxMaterialId id = new RbxMaterialId(name, value);
            bool mapped = _provider.TryGetMaterial(in id, out Material material);

            Assert.IsTrue(mapped, id.ToString());
            float actualFeatureMeters = 1f /
                                        (material.GetFloat("_PatternScale") * repeatsPerPatternUnit);
            Assert.That(actualFeatureMeters,
                Is.EqualTo(expectedFeatureMeters).Within(0.0006f), id.ToString());
        }

        [TestCase("WoodPlanks", WoodPlanksValue, 1.35f, 4f, 8f)]
        [TestCase("Brick", BrickValue, 1.28f, 4f, 8f)]
        [TestCase("Cobblestone", CobblestoneValue, 1.55f, 4f, 8f)]
        [TestCase("Grass", GrassValue, 4.7f, 6f, 10f)]
        [TestCase("Fabric", FabricValue, 42f / (2f * Mathf.PI), 20f, 40f)]
        public void CharacteristicPatterns_StayReadableAcrossThreeStudFace(string name, int value,
            float repeatsPerPatternUnit, float minimumCountAcross, float maximumCountAcross)
        {
            const float faceMeters = 3f * 0.28f;
            RbxMaterialId id = new RbxMaterialId(name, value);
            bool mapped = _provider.TryGetMaterial(in id, out Material material);

            Assert.IsTrue(mapped, id.ToString());
            float featureCountAcross = faceMeters * material.GetFloat("_PatternScale")
                                       * repeatsPerPatternUnit;
            Assert.That(featureCountAcross, Is.InRange(minimumCountAcross, maximumCountAcross),
                id.ToString());
        }

        [Test]
        public void PriorityRoughnessAndFiltering_AreIndependentAndFootprintAware()
        {
            string root = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials");
            string commonSource = File.ReadAllText(Path.Combine(root,
                "RbxProceduralCommon.hlsl"));
            string noiseSource = File.ReadAllText(Path.Combine(root, "RbxNoiseShader.hlsl"));

            StringAssert.Contains("float RbxPixelFootprint(float3 position)", commonSource);
            StringAssert.Contains("float2 RbxUvFootprint(float2 uv)", commonSource);
            StringAssert.Contains("fwidth(position)", commonSource);
            StringAssert.Contains("fwidth(uv)", commonSource);
            StringAssert.Contains("RbxFilteredInsideMask", commonSource);
            StringAssert.Contains("RbxFilteredWave", commonSource);
            StringAssert.Contains("RbxCompensateUnresolvedRoughness", commonSource);
            StringAssert.Contains("physicalPosition * 220.0", commonSource);
            StringAssert.Contains("physicalPosition * 240.0", commonSource);
            StringAssert.Contains("physicalPosition * 280.0", commonSource);
            StringAssert.Contains("physicalPosition * 320.0", commonSource);
            StringAssert.Contains("physicalPosition * 420.0", commonSource);
            StringAssert.Contains("physicalPosition * 650.0", commonSource);
            StringAssert.Contains(
                "RbxFilteredInsideMask(diamondDistance, 0.28, 0.06, distanceFootprint)",
                commonSource);
            StringAssert.Contains("float RbxSimplexOctaveVisibility(float footprint)", noiseSource);
            StringAssert.Contains("UNITY_BRANCH if (visibility > 0.0)", noiseSource);
            StringAssert.Contains("float normalVisibility = 1.0 - smoothstep", commonSource);
        }

        [Test]
        public void PatternScaleContract_IsConsumedByEveryProceduralShaderFamily()
        {
            string root = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials");
            string surfaceSource = File.ReadAllText(Path.Combine(root,
                "RbxProceduralSurface.shader"));
            string neonSource = File.ReadAllText(Path.Combine(root,
                "RbxProceduralNeon.shader"));
            string transparentSource = File.ReadAllText(Path.Combine(root,
                "RbxProceduralTransparent.shader"));

            StringAssert.Contains("materialMode, _PatternScale, baseColor", surfaceSource);
            StringAssert.Contains("input.positionWS * (_PatternScale * 3.2)", neonSource);
            StringAssert.Contains("float3 patternPosition = input.positionWS * _PatternScale;",
                transparentSource);
            StringAssert.Contains("RbxTransparentHeight(patternPosition, materialMode)",
                transparentSource);
        }

        [Test]
        public void MetalModes_UseNearOneMetallicColoredResponse()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralCommon.hlsl");
            string source = File.ReadAllText(path);

            Assert.That(Regex.Matches(source, "sample\\.metallic = 1\\.0h;").Count,
                Is.GreaterThanOrEqualTo(2));
            StringAssert.Contains("sample.albedo = baseColor", source);
            StringAssert.Contains("sample.metallic = saturate(sample.metallic);", source);
            StringAssert.Contains("if (sample.metallic < 0.5h)", source);
        }

        [TestCase("Plastic", 256, 0.08f)]
        [TestCase("SmoothPlastic", 272, 0.08f)]
        [TestCase("Metal", MetalValue, 0.08f)]
        [TestCase("Fabric", FabricValue, 0.08f)]
        [TestCase("Brick", BrickValue, 0.75f)]
        [TestCase("Grass", GrassValue, 0.7f)]
        [TestCase("Sand", SandValue, 0.45f)]
        [TestCase("Ground", GroundValue, 0.65f)]
        public void CalibratedPalette_DefaultSaturationStaysWithinPhysicalLimit(string name,
            int value, float maximumSaturation)
        {
            RbxMaterialId id = new RbxMaterialId(name, value);
            bool mapped = _provider.TryGetMaterial(in id, out Material material);

            Assert.IsTrue(mapped, id.ToString());
            Color.RGBToHSV(material.GetColor("_MaterialColor"), out float hue,
                out float saturation, out float brightness);
            Assert.That(saturation, Is.LessThanOrEqualTo(maximumSaturation), id.ToString());
            Assert.That(brightness, Is.InRange(0.2f, 0.9f), id.ToString());
            Assert.That(hue, Is.InRange(0f, 1f), id.ToString());
        }

        [Test]
        public void GrassGroundAndCobblestone_UseStructuredDiscretePatternKernels()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi",
                "Unity", "Resources", "CoreAIRbxMaterials", "RbxProceduralCommon.hlsl");
            string source = File.ReadAllText(path);

            StringAssert.Contains("float3 RbxGrassBladeLayer", source);
            StringAssert.Contains("localPosition -= jitter * 0.38;", source);
            StringAssert.Contains("float bladeProgress", source);
            StringAssert.Contains("void RbxGroundPattern", source);
            StringAssert.Contains("float pebbleRadius", source);
            StringAssert.Contains("void RbxCobblestonePattern", source);
            StringAssert.Contains(
                "stoneMask = RbxFilteredInsideMask(stoneDistance, 0.91, 0.09, distanceFootprint);",
                source);
            StringAssert.DoesNotContain("float bladeA = 0.5 + 0.5 * sin", source);
        }

        private static void AssertSha256(string path, string expected)
        {
            byte[] bytes = File.ReadAllBytes(path);
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }

            string actual = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            Assert.AreEqual(expected, actual, path);
        }

        private void AssertProviderTuning(string name, int value, int expectedMode,
            float expectedScale, float expectedBumpStrength)
        {
            RbxMaterialId id = new RbxMaterialId(name, value);
            bool mapped = _provider.TryGetMaterial(in id, out Material material);

            Assert.IsTrue(mapped, id.ToString());
            Assert.AreEqual("CoreAiRbxMaterial_" + name, material.name);
            Assert.AreEqual(expectedMode, Mathf.RoundToInt(material.GetFloat("_MaterialMode")));
            Assert.That(material.GetFloat("_PatternScale"),
                Is.EqualTo(expectedScale).Within(0.0001f), id.ToString());
            Assert.That(material.GetFloat("_BumpStrength"),
                Is.EqualTo(expectedBumpStrength).Within(0.0001f), id.ToString());
        }

        private static string ExpectedShaderName(string materialName)
        {
            if (string.Equals(materialName, "Neon", StringComparison.Ordinal))
            {
                return "CoreAI/Rbx/Procedural Neon";
            }

            if (string.Equals(materialName, "ForceField", StringComparison.Ordinal)
                || string.Equals(materialName, "Glass", StringComparison.Ordinal)
                || string.Equals(materialName, "Ice", StringComparison.Ordinal))
            {
                return "CoreAI/Rbx/Procedural Transparent";
            }

            return "CoreAI/Rbx/Procedural Surface";
        }

        private static int ExpectedMode(string materialName)
        {
            switch (materialName)
            {
                case "Plastic":
                case "Neon":
                case "ForceField":
                    return 0;
                case "SmoothPlastic":
                case "Glass":
                    return 1;
                case "Wood":
                case "Ice":
                    return 2;
                case "WoodPlanks":
                    return 3;
                case "Metal":
                    return 4;
                case "DiamondPlate":
                    return 5;
                case "CorrodedMetal":
                    return 6;
                case "Marble":
                    return 7;
                case "Slate":
                    return 8;
                case "Concrete":
                    return 9;
                case "Brick":
                    return 10;
                case "Cobblestone":
                    return 11;
                case "Grass":
                    return 12;
                case "Sand":
                    return 13;
                case "Ground":
                    return 14;
                case "Rock":
                    return 15;
                case "Snow":
                    return 16;
                case "Fabric":
                    return 17;
                default:
                    throw new ArgumentOutOfRangeException(nameof(materialName), materialName, null);
            }
        }
    }
}
