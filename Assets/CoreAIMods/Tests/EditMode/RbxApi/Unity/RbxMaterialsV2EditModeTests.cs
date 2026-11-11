using System.IO;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>
    /// Materials v2, stage 1 (roadmap P8): the animated water material, Glass refraction on the scene-texture
    /// tier, Plastic surface detail, the CrackedLava glow, the blended twin for partial Transparency and the
    /// DepthNormals pass. The pixels themselves are proven by the PlayMode capture fixtures; these pin the
    /// contracts and the fallbacks that keep today's look when a feature is off.
    /// </summary>
    [TestFixture]
    public sealed class RbxMaterialsV2EditModeTests
    {
        private const int WaterValue = 2048;
        private const int GlassValue = 1568;
        private const int AirValue = 1792;
        private const int PlasticValue = 256;
        private const int NeonValue = 288;
        private const int BrickValue = 848;
        private const int BasaltValue = 788;
        private const int CrackedLavaValue = 804;

        private static readonly string ShaderRoot = Path.Combine(Application.dataPath, "CoreAIMods",
            "Runtime", "RbxApi", "Unity", "Resources", "CoreAIRbxMaterials");

        private RbxProceduralMaterialProvider _procedural;

        [SetUp]
        public void SetUp()
        {
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = true;
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            RbxTransparentTwins.ResetForTests();
            _procedural = new RbxProceduralMaterialProvider();
        }

        [TearDown]
        public void TearDown()
        {
            RbxWaterTime.ClearOverride();
            RbxSceneTextures.ClearOverride();
            RbxTransparentTwins.ResetForTests();
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = false;
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
        }

        [Test]
        public void Water_UsesTheAnimatedWaterShader_InTheTransparentQueue()
        {
            Material water = Resolve(_procedural, "Water", WaterValue);

            Assert.AreEqual("CoreAI/Rbx/Water", water.shader.name);
            Assert.AreEqual((int)RenderQueue.Transparent, water.renderQueue);
            Assert.IsTrue(water.HasProperty("_WaveSpeed"));
            Assert.IsTrue(water.HasProperty("_Absorption"));
            Assert.Greater(water.GetFloat("_BumpStrength"), 0f, "the waves need a strength");
        }

        [Test]
        public void Water_ThroughTheDefaultBinderProvider_IsTheSameWaterMaterial()
        {
            using RbxTextureMaterialProvider textured = new();

            Material water = Resolve(textured, "Water", WaterValue);

            Assert.AreEqual("CoreAI/Rbx/Water", water.shader.name);
        }

        [Test]
        public void WaterMaterial_CarriesTheTransparentBlendState_ForItsFallbackShader()
        {
            Material water = Resolve(_procedural, "Water", WaterValue);

            Assert.AreEqual((float)BlendMode.SrcAlpha, water.GetFloat("_SrcBlend"));
            Assert.AreEqual((float)BlendMode.OneMinusSrcAlpha, water.GetFloat("_DstBlend"));
            Assert.AreEqual(2, Mathf.RoundToInt(water.GetFloat("_MaterialMode")),
                "the fallback shader renders mode 2, the pre-v2 water look");
        }

        [Test]
        public void WaterShader_AnimatesByDefault_PinsToTheOverride_AndFallsBackToTodaysLook()
        {
            string source = File.ReadAllText(Path.Combine(ShaderRoot, "RbxWater.shader"));

            StringAssert.Contains(
                "_RbxWaterTimeOverrideEnabled > 0.5 ? _RbxWaterTimeOverride : _Time.y", source);
            StringAssert.Contains("Fallback \"CoreAI/Rbx/Procedural Transparent\"", source);
            StringAssert.Contains("#pragma multi_compile_fragment _ _RBX_SCENE_TEXTURES", source);
            int sceneBranch = source.IndexOf("#if defined(_RBX_SCENE_TEXTURES)\n                    float2",
                System.StringComparison.Ordinal);
            Assert.Greater(sceneBranch, 0, "the scene-texture branch must exist");
            Assert.Greater(source.IndexOf("SampleSceneColor(", System.StringComparison.Ordinal), sceneBranch,
                "the opaque texture may only be read inside the scene-texture branch");
            Assert.Greater(source.IndexOf("SampleSceneDepth(", System.StringComparison.Ordinal), sceneBranch,
                "the depth texture may only be read inside the scene-texture branch");
            StringAssert.Contains("#else", source.Substring(sceneBranch));
            StringAssert.Contains("RbxSimplexNoise01Grad(", source);
        }

        [Test]
        public void WaterTime_Override_PinsTheGlobalClock_AndClearReleasesIt()
        {
            RbxWaterTime.SetOverride(12.5f);

            Assert.IsTrue(RbxWaterTime.IsOverridden);
            Assert.AreEqual(12.5f, Shader.GetGlobalFloat(RbxWaterTime.OverrideProperty));
            Assert.AreEqual(1f, Shader.GetGlobalFloat(RbxWaterTime.OverrideEnabledProperty));

            RbxWaterTime.ClearOverride();

            Assert.IsFalse(RbxWaterTime.IsOverridden);
            Assert.AreEqual(0f, Shader.GetGlobalFloat(RbxWaterTime.OverrideEnabledProperty),
                "cleared, the waves follow the engine time again");
        }

        [Test]
        public void SceneTextures_KeywordFollowsTheAvailability()
        {
            RbxSceneTextures.SetAvailable(true);
            Assert.IsTrue(Shader.IsKeywordEnabled(RbxSceneTextures.Keyword));

            RbxSceneTextures.SetAvailable(false);
            Assert.IsFalse(Shader.IsKeywordEnabled(RbxSceneTextures.Keyword),
                "off, water and Glass keep the alpha-blended look");

            RbxSceneTextures.ClearOverride();
            Assert.AreEqual(RbxSceneTextures.Available, Shader.IsKeywordEnabled(RbxSceneTextures.Keyword));
        }

        [Test]
        public void SceneTextures_AskThePipelineForBothTextures()
        {
            Assert.AreEqual(true, RbxSceneTextures.PipelineSupport(new FakePipeline(true, true)));
            Assert.AreEqual(false, RbxSceneTextures.PipelineSupport(new FakePipeline(true, false)),
                "refraction needs the opaque texture and the depth tint needs the depth texture");
            Assert.AreEqual(false, RbxSceneTextures.PipelineSupport(new FakePipeline(false, true)));
            Assert.IsNull(RbxSceneTextures.PipelineSupport(new object()),
                "a pipeline without the switches falls back to the platform default");
            Assert.IsNull(RbxSceneTextures.PipelineSupport(null));
        }

        [TestCase(RuntimePlatform.WebGLPlayer, false)]
        [TestCase(RuntimePlatform.Android, false)]
        [TestCase(RuntimePlatform.IPhonePlayer, false)]
        [TestCase(RuntimePlatform.WindowsPlayer, true)]
        [TestCase(RuntimePlatform.OSXPlayer, true)]
        public void SceneTextures_DefaultFollowsThePipelineTier(RuntimePlatform platform, bool expected)
        {
            Assert.AreEqual(expected, RbxSceneTextures.DefaultFor(platform));
        }

        [Test]
        public void Glass_RefractsOnTheSceneTier_AndAirStaysInvisible()
        {
            Material glass = Resolve(_procedural, "Glass", GlassValue);
            Material air = Resolve(_procedural, "Air", AirValue);
            string source = File.ReadAllText(Path.Combine(ShaderRoot, "RbxProceduralTransparent.shader"));

            Assert.AreEqual(1f, glass.GetFloat("_SceneRefraction"));
            Assert.AreEqual(0f, air.GetFloat("_SceneRefraction"), "Air shares Glass's mode and must not refract");
            int sceneBranch = source.IndexOf("#if defined(_RBX_SCENE_TEXTURES)\n                    //",
                System.StringComparison.Ordinal);
            Assert.Greater(sceneBranch, 0);
            Assert.Greater(source.IndexOf("SampleSceneColor(", System.StringComparison.Ordinal), sceneBranch);
            StringAssert.Contains("UNITY_BRANCH if (materialMode == 1 && _SceneRefraction > 0.5)", source);
        }

        [TestCase("Plastic", 256, 1f)]
        [TestCase("SmoothPlastic", 272, 0.6f)]
        [TestCase("Salt", 1392, 0.8f)]
        [TestCase("Rubber", 2311, 0f)]
        [TestCase("Snow", 1328, 0f)]
        public void SurfaceDetail_IsSetPerMaterialNotPerShaderMode(string name, int value, float expected)
        {
            Material material = Resolve(_procedural, name, value);

            Assert.IsTrue(material.HasProperty("_SurfaceDetail"), name);
            Assert.AreEqual(expected, material.GetFloat("_SurfaceDetail"), name);
        }

        [Test]
        public void SurfaceDetail_ShapesRoughnessAndReliefOnly_AndIsSkippedAtZero()
        {
            string common = File.ReadAllText(Path.Combine(ShaderRoot, "RbxProceduralCommon.hlsl"));
            int start = common.IndexOf("void RbxApplyPlasticSurfaceDetail", System.StringComparison.Ordinal);
            Assert.Greater(start, 0);
            string body = common.Substring(start, common.IndexOf("half3 RbxComposeMaterialColor",
                start, System.StringComparison.Ordinal) - start);

            StringAssert.DoesNotContain("sample.albedo", body, "Part.Color must stay exactly as authored");
            StringAssert.Contains("sample.smoothness", body);
            StringAssert.Contains("sample.height", body);
            Assert.AreEqual(3, System.Text.RegularExpressions.Regex.Matches(common,
                "UNITY_BRANCH if \\(surfaceDetail > 0\\.0\\)").Count, "Plastic, SmoothPlastic and Salt");
        }

        [Test]
        public void CrackedLava_Glows_AndPlainRockDoesNot()
        {
            using RbxTextureMaterialProvider textured = new();

            Material lava = Resolve(textured, "CrackedLava", CrackedLavaValue);
            Material basalt = Resolve(textured, "Basalt", BasaltValue);
            Material brick = Resolve(textured, "Brick", BrickValue);

            Assert.AreEqual("CoreAI/Rbx/Textured Surface", lava.shader.name);
            Assert.IsTrue(lava.IsKeywordEnabled("_RBX_EMISSION"));
            Assert.Greater(lava.GetColor("_EmissionColor").maxColorComponent, 1f,
                "an HDR glow reaches the bloom threshold");
            Assert.IsFalse(basalt.IsKeywordEnabled("_RBX_EMISSION"));
            Assert.IsFalse(brick.IsKeywordEnabled("_RBX_EMISSION"));
        }

        [Test]
        public void EntryEmission_DefaultsByMaterial_AndAnExplicitChoiceWins()
        {
            RbxMaterialTextureCatalog.Entry lava = new() { MaterialName = "CrackedLava", MaterialValue = 804 };
            RbxMaterialTextureCatalog.Entry brick = new() { MaterialName = "Brick", MaterialValue = 848 };

            Assert.AreEqual(RbxEmissionMode.MaterialDefault, lava.EmissionMode,
                "entries saved before emission existed load as MaterialDefault");
            Assert.IsTrue(lava.ResolveEmission().Emits);
            Assert.IsFalse(brick.ResolveEmission().Emits);

            lava.EmissionMode = RbxEmissionMode.None;
            Assert.IsFalse(lava.ResolveEmission().Emits, "an explicit None switches the default glow off");

            brick.EmissionMode = RbxEmissionMode.FromAlbedo;
            brick.EmissionColor = new Color(2f, 0.5f, 0.1f);
            Assert.IsTrue(brick.ResolveEmission().Emits);
            Assert.AreEqual(new Color(2f, 0.5f, 0.1f), brick.ResolveEmission().Color);
        }

        [Test]
        public void TexturedShader_EmitsOnlyUnderItsKeyword()
        {
            string source = File.ReadAllText(Path.Combine(ShaderRoot, "RbxTexturedSurface.shader"));

            StringAssert.Contains("#pragma multi_compile_local_fragment _ _RBX_EMISSION", source);
            StringAssert.Contains("#if defined(_RBX_EMISSION)", source);
            StringAssert.Contains("surfaceData.emission = emission;", source);
            StringAssert.Contains("half3 emission = half3(0.0h, 0.0h, 0.0h);", source);
        }

        [Test]
        public void OpaqueMaterial_GetsABlendedTwin_AndTheSourceStaysOpaque()
        {
            Material plastic = Resolve(_procedural, "Plastic", PlasticValue);

            Assert.IsTrue(_procedural.TryGetTransparentMaterial(plastic, out Material twin));
            Assert.AreNotSame(plastic, twin);
            Assert.AreSame(plastic.shader, twin.shader);
            Assert.AreEqual((int)RenderQueue.Transparent, twin.renderQueue);
            Assert.AreEqual(0f, twin.GetFloat("_ZWrite"));
            Assert.AreEqual(1f, twin.GetFloat("_AlphaFromPartColor"));
            Assert.AreEqual((float)BlendMode.SrcAlpha, twin.GetFloat("_SrcBlend"));
            Assert.AreEqual((float)BlendMode.OneMinusSrcAlpha, twin.GetFloat("_DstBlend"));
            Assert.AreEqual("Transparent", twin.GetTag("RenderType", false));

            Assert.AreEqual(1f, plastic.GetFloat("_ZWrite"), "negative twin: the shared source is untouched");
            Assert.AreEqual(0f, plastic.GetFloat("_AlphaFromPartColor"));
            Assert.Less(plastic.renderQueue, (int)RenderQueue.Transparent);
        }

        [Test]
        public void Twin_IsCachedPerSource()
        {
            Material plastic = Resolve(_procedural, "Plastic", PlasticValue);

            Assert.IsTrue(_procedural.TryGetTransparentMaterial(plastic, out Material first));
            Assert.IsTrue(_procedural.TryGetTransparentMaterial(plastic, out Material second));

            Assert.AreSame(first, second);
            Assert.AreEqual(1, RbxTransparentTwins.Count);
        }

        [Test]
        public void TexturedMaterial_GetsABlendedTwin()
        {
            using RbxTextureMaterialProvider textured = new();
            Material brick = Resolve(textured, "Brick", BrickValue);

            Assert.IsTrue(textured.TryGetTransparentMaterial(brick, out Material twin));

            Assert.AreEqual((int)RenderQueue.Transparent, twin.renderQueue);
            Assert.AreSame(brick.GetTexture("_BaseMap"), twin.GetTexture("_BaseMap"));
        }

        [Test]
        public void AlreadyBlendedMaterial_IsItsOwnTwin_AndUnblendableShadersHaveNone()
        {
            Material glass = Resolve(_procedural, "Glass", GlassValue);
            Material water = Resolve(_procedural, "Water", WaterValue);
            Material neon = Resolve(_procedural, "Neon", NeonValue);

            Assert.IsTrue(_procedural.TryGetTransparentMaterial(glass, out Material glassTwin));
            Assert.AreSame(glass, glassTwin);
            Assert.IsTrue(_procedural.TryGetTransparentMaterial(water, out Material waterTwin));
            Assert.AreSame(water, waterTwin);
            Assert.IsFalse(_procedural.TryGetTransparentMaterial(neon, out _), "Neon stays opaque");
            Assert.IsFalse(_procedural.TryGetTransparentMaterial(_procedural.FallbackMaterial, out _));
            Assert.AreEqual(0, RbxTransparentTwins.Count);
        }

        [Test]
        public void Twin_IsDestroyedWithItsSource()
        {
            Material plastic = Resolve(_procedural, "Plastic", PlasticValue);
            Assert.IsTrue(_procedural.TryGetTransparentMaterial(plastic, out Material twin));

            RbxProceduralMaterialProvider.ResetSharedCacheForTests();

            Assert.IsTrue(twin == null, "a twin must not outlive its source material");
            Assert.AreEqual(0, RbxTransparentTwins.Count);
        }

        [Test]
        public void Binder_DrawsPartialTransparencyWithTheTwin_AndOpaqueOtherwise()
        {
            RbxSpace.ResetForTests(0.28f);
            GameObject root = new("MaterialsV2BinderRoot");
            InstanceGameObjectBinder binder = new(root.transform);
            InstanceRegistry registry = new(null, binder);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            try
            {
                RbxInstance part = registry.Create("Part");
                part.Parent = registry.WorldRoot;
                Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject partObject));
                Renderer renderer = partObject.GetComponent<Renderer>();
                Material opaque = renderer.sharedMaterial;
                Assert.Less(opaque.renderQueue, (int)RenderQueue.Transparent);

                binder.SetTransparency(part.Id, 0.5f);
                Assert.AreEqual((int)RenderQueue.Transparent, renderer.sharedMaterial.renderQueue);
                Assert.AreEqual(1f, renderer.sharedMaterial.GetFloat("_AlphaFromPartColor"));
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                Assert.AreEqual(0.5f, block.GetColor("_Color").a, 1e-4f);
                Assert.IsTrue(renderer.enabled);

                binder.SetTransparency(part.Id, 0f);
                Assert.AreSame(opaque, renderer.sharedMaterial, "negative twin: Transparency 0 is opaque");

                binder.SetTransparency(part.Id, 1f);
                Assert.IsFalse(renderer.enabled, "Transparency 1 hides the part (Roblox parity)");
            }
            finally
            {
                game.Destroy();
                Object.DestroyImmediate(root);
                RbxSpace.ResetForTests();
            }
        }

        [TestCase("RbxTexturedSurface.shader", true)]
        [TestCase("RbxProceduralSurface.shader", true)]
        [TestCase("RbxProceduralNeon.shader", true)]
        [TestCase("RbxProceduralFallback.shader", true)]
        [TestCase("RbxProceduralTransparent.shader", false)]
        [TestCase("RbxWater.shader", false)]
        public void OpaqueRbxShaders_DrawDepthNormals_SoPartsReceiveSsao(string shaderFile, bool expected)
        {
            string source = File.ReadAllText(Path.Combine(ShaderRoot, shaderFile));

            Assert.AreEqual(expected,
                source.Contains("UsePass \"Universal Render Pipeline/Lit/DepthNormals\""),
                shaderFile + (expected
                    ? " must draw DepthNormals so SSAO sees it"
                    : " is transparent and must stay out of the depth prepass"));
        }

        [TestCase("RbxTexturedSurface.shader")]
        [TestCase("RbxProceduralSurface.shader")]
        public void OpaqueLitShaders_ExposeTheTwinBlendState_WithOpaqueDefaults(string shaderFile)
        {
            string source = File.ReadAllText(Path.Combine(ShaderRoot, shaderFile));

            StringAssert.Contains("_SrcBlend(\"Source Blend\", Float) = 1", source);
            StringAssert.Contains("_DstBlend(\"Destination Blend\", Float) = 0", source);
            StringAssert.Contains("_ZWrite(\"Depth Write\", Float) = 1", source);
            StringAssert.Contains("Blend [_SrcBlend] [_DstBlend]", source);
            StringAssert.Contains("ZWrite [_ZWrite]", source);
            StringAssert.Contains(
                "color.a = lerp(1.0h, saturate(_Color.a), (half)saturate(_AlphaFromPartColor));", source);
        }

        private sealed class FakePipeline
        {
            public FakePipeline(bool opaque, bool depth)
            {
                supportsCameraOpaqueTexture = opaque;
                supportsCameraDepthTexture = depth;
            }

            // WHY: the names mirror the URP asset's public switches that RbxSceneTextures reads.
            public bool supportsCameraOpaqueTexture { get; }

            public bool supportsCameraDepthTexture { get; }
        }

        private static Material Resolve(IRbxMaterialProvider<Material> provider, string name, int value)
        {
            RbxMaterialId id = new(name, value);
            Assert.IsTrue(provider.TryGetMaterial(in id, out Material material), id.ToString());
            return material;
        }
    }
}
