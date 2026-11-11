using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Look;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The pipeline-independent look: sky, ambient, fog and directional-light shadows follow the profile
    /// and the options under <see cref="LookMode.Apply"/>, a scene-authored sky and fog survive
    /// <see cref="LookMode.FillMissing"/>, <see cref="LookMode.Off"/> changes nothing,
    /// <see cref="RenderSettingsLookApplier.Revert"/> puts the scene back exactly, and an ambient another
    /// system claims (<see cref="LookAmbientOwnership"/>) is left to it until the claim is released.
    /// </summary>
    public sealed class RenderSettingsLookApplierEditModeTests
    {
        private readonly List<Object> _created = new();
        private CoreAiLookProfile _profile;
        private RenderSettingsLookApplier _applier;
        private Material _originalSkybox;
        private bool _originalFog;
        private Color _originalFogColor;
        private AmbientMode _originalAmbientMode;
        private Color _originalAmbientSky;
        private Color _originalAmbientEquator;
        private Color _originalAmbientGround;
        private LookAmbientOwnership _ownership;

        [SetUp]
        public void SetUp()
        {
            _originalSkybox = RenderSettings.skybox;
            _originalFog = RenderSettings.fog;
            _originalFogColor = RenderSettings.fogColor;
            _originalAmbientMode = RenderSettings.ambientMode;
            _originalAmbientSky = RenderSettings.ambientSkyColor;
            _originalAmbientEquator = RenderSettings.ambientEquatorColor;
            _originalAmbientGround = RenderSettings.ambientGroundColor;
            _profile = CoreAiLookProfile.CreateDefault();
            _profile.RealtimeReflectionProbe = false;
            _created.Add(_profile);
            // WHY a private ownership record: a claim another test left on the shared one must not change
            // what these tests see.
            _ownership = new LookAmbientOwnership();
            _applier = new RenderSettingsLookApplier(new SilentLogger(), _ownership);
        }

        [TearDown]
        public void TearDown()
        {
            _applier.Revert();
            RenderSettings.skybox = _originalSkybox;
            RenderSettings.fog = _originalFog;
            RenderSettings.fogColor = _originalFogColor;
            RenderSettings.ambientMode = _originalAmbientMode;
            RenderSettings.ambientSkyColor = _originalAmbientSky;
            RenderSettings.ambientEquatorColor = _originalAmbientEquator;
            RenderSettings.ambientGroundColor = _originalAmbientGround;
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        [Test]
        public void Apply_BuildsTheProceduralSkyAndFog_AndRevertRestoresTheScene()
        {
            RequireProceduralSkyShader();
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            _applier.Apply(_profile, Options(), LookMode.Apply);

            Assert.IsNotNull(_applier.ProceduralSkyMaterial);
            Assert.AreSame(_applier.ProceduralSkyMaterial, RenderSettings.skybox);
            Assert.AreEqual(RenderSettingsLookApplier.ProceduralSkyShaderName, RenderSettings.skybox.shader.name);
            Assert.AreEqual(AmbientMode.Skybox, RenderSettings.ambientMode);
            Assert.IsTrue(RenderSettings.fog);
            Assert.AreEqual(_profile.FogColor, RenderSettings.fogColor);

            _applier.Revert();

            Assert.IsNull(RenderSettings.skybox);
            Assert.IsFalse(RenderSettings.fog);
            Assert.IsNull(_applier.ProceduralSkyMaterial, "the owned sky material is destroyed");
        }

        [Test]
        public void SkyOff_ReplacesTheSkyWithFlatAmbient()
        {
            _applier.Apply(_profile, Options(GraphicsOptions.Default.WithSky(false)), LookMode.Apply);

            Assert.IsNull(RenderSettings.skybox);
            Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode);
            Assert.AreEqual(_profile.FlatAmbientSky, RenderSettings.ambientSkyColor);
        }

        [Test]
        public void FogOff_TurnsFogOff()
        {
            RenderSettings.fog = true;

            _applier.Apply(_profile, Options(GraphicsOptions.Default.WithFog(false)), LookMode.Apply);

            Assert.IsFalse(RenderSettings.fog);
        }

        [Test]
        public void Apply_KeepsASceneAuthoredSky_UntilTheSkyIsSwitchedOff()
        {
            Material sceneSky = NewMaterial("Scene Sky");
            RenderSettings.skybox = sceneSky;

            _applier.Apply(_profile, Options(), LookMode.Apply);
            Assert.AreSame(sceneSky, RenderSettings.skybox, "an authored sky wins, like a scene Volume");
            Assert.IsNull(_applier.ProceduralSkyMaterial);

            _applier.Apply(_profile, Options(GraphicsOptions.Default.WithSky(false)), LookMode.Apply);
            Assert.IsNull(RenderSettings.skybox);

            _applier.Apply(_profile, Options(), LookMode.Apply);
            Assert.AreSame(sceneSky, RenderSettings.skybox, "switching the sky back on restores the scene's");
        }

        [Test]
        public void ProfileSkybox_ReplacesASceneAuthoredSky()
        {
            RenderSettings.skybox = NewMaterial("Scene Sky");
            Material profileSky = NewMaterial("Profile Sky");
            _profile.SkyboxMaterial = profileSky;

            _applier.Apply(_profile, Options(), LookMode.Apply);

            Assert.AreSame(profileSky, RenderSettings.skybox);
        }

        [Test]
        public void FillMissing_KeepsASceneAuthoredSkyAndFog()
        {
            Material sceneSky = NewMaterial("Scene Sky");
            RenderSettings.skybox = sceneSky;
            RenderSettings.fog = true;
            RenderSettings.fogColor = Color.red;

            _applier.Apply(_profile, Options(), LookMode.FillMissing);

            Assert.AreSame(sceneSky, RenderSettings.skybox);
            Assert.IsTrue(RenderSettings.fog);
            Assert.AreEqual(Color.red, RenderSettings.fogColor);
        }

        [Test]
        public void FillMissing_ReplacesTheBuiltInDefaultSky_AndAddsFog()
        {
            RequireProceduralSkyShader();
            RenderSettings.skybox = NewMaterial("Default-Skybox");
            RenderSettings.fog = false;

            _applier.Apply(_profile, Options(), LookMode.FillMissing);

            Assert.AreSame(_applier.ProceduralSkyMaterial, RenderSettings.skybox);
            Assert.IsTrue(RenderSettings.fog);
        }

        [Test]
        public void Off_ChangesNothing()
        {
            Material sceneSky = NewMaterial("Scene Sky");
            RenderSettings.skybox = sceneSky;
            RenderSettings.fog = false;
            Light sun = NewDirectionalLight(LightShadows.Soft);

            _applier.Apply(_profile, Options(new GraphicsOptions(shadows: GraphicsShadowQuality.Off)), LookMode.Off);

            Assert.AreSame(sceneSky, RenderSettings.skybox);
            Assert.IsFalse(RenderSettings.fog);
            Assert.AreEqual(LightShadows.Soft, sun.shadows);
            Assert.IsNull(_applier.ProceduralSkyMaterial);
        }

        [Test]
        public void Shadows_FollowTheOption_AndRevertRestoresEachLight()
        {
            Light sun = NewDirectionalLight(LightShadows.Soft);
            Light fill = NewDirectionalLight(LightShadows.None);

            _applier.Apply(_profile, Options(new GraphicsOptions(shadows: GraphicsShadowQuality.Hard)), LookMode.Apply);
            Assert.AreEqual(LightShadows.Hard, sun.shadows);
            Assert.AreEqual(LightShadows.None, fill.shadows, "a light authored without shadows stays without");

            _applier.Apply(_profile, Options(new GraphicsOptions(shadows: GraphicsShadowQuality.Off)), LookMode.Apply);
            Assert.AreEqual(LightShadows.None, sun.shadows);

            _applier.Revert();
            Assert.AreEqual(LightShadows.Soft, sun.shadows);
            Assert.AreEqual(LightShadows.None, fill.shadows);
        }

        [Test]
        public void FillMissing_LeavesLightsAlone()
        {
            Light sun = NewDirectionalLight(LightShadows.Soft);

            _applier.Apply(_profile, Options(new GraphicsOptions(shadows: GraphicsShadowQuality.Off)),
                LookMode.FillMissing);

            Assert.AreEqual(LightShadows.Soft, sun.shadows);
        }

        [Test]
        public void SkyOnlyReflectionProbe_IsCreatedAndRemoved()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Rendering a reflection probe needs a graphics device.");
            }

            RequireProceduralSkyShader();
            _profile.RealtimeReflectionProbe = true;

            _applier.Apply(_profile, Options(), LookMode.Apply);

            Assert.IsNotNull(_applier.ReflectionProbe);
            Assert.AreEqual(0, _applier.ReflectionProbe.cullingMask, "the probe renders the sky only");
            Assert.AreEqual(ReflectionProbeMode.Realtime, _applier.ReflectionProbe.mode);

            _applier.Revert();

            Assert.IsTrue(_applier.ReflectionProbe == null);
        }

        [Test]
        public void ClaimedAmbient_IsLeftToItsOwner_WhileTheSkyAndFogStillFollowTheLook()
        {
            RequireProceduralSkyShader();
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            using (_ownership.Claim("day cycle"))
            {
                WriteOwnersAmbient();

                _applier.Apply(_profile, Options(), LookMode.Apply);

                AssertOwnersAmbient("with the sky on");
                Assert.AreSame(_applier.ProceduralSkyMaterial, RenderSettings.skybox, "the sky still follows the look");
                Assert.IsTrue(RenderSettings.fog, "fog still follows the look");

                _applier.Apply(_profile, Options(GraphicsOptions.Default.WithSky(false)), LookMode.Apply);

                AssertOwnersAmbient("with the sky off");
                Assert.IsNull(RenderSettings.skybox);
            }
        }

        [Test]
        public void ReleasingTheLastClaim_GivesTheAmbientBackToTheLook()
        {
            RequireProceduralSkyShader();
            RenderSettings.skybox = null;
            System.IDisposable first = _ownership.Claim("day cycle");
            System.IDisposable second = _ownership.Claim("weather");
            WriteOwnersAmbient();
            _applier.Apply(_profile, Options(), LookMode.Apply);

            first.Dispose();
            AssertOwnersAmbient("while one claim is still held");

            second.Dispose();
            Assert.IsFalse(_ownership.IsClaimed);
            Assert.AreEqual(AmbientMode.Skybox, RenderSettings.ambientMode, "the look's sky ambient is back");
            Assert.AreEqual(_profile.AmbientIntensity, RenderSettings.ambientIntensity);
        }

        [Test]
        public void SkyReflectionScale_ScalesTheLooksSkyReflections_AndFollowsLaterChanges()
        {
            RequireProceduralSkyShader();
            RenderSettings.skybox = null;
            _ownership.SetSkyReflectionScale(0.5f);

            _applier.Apply(_profile, Options(), LookMode.Apply);

            Assert.AreEqual(_profile.ReflectionIntensity * 0.5f, RenderSettings.reflectionIntensity, 1e-5f);
            if (_applier.ReflectionProbe != null)
            {
                Assert.AreEqual(0.5f, _applier.ReflectionProbe.intensity, 1e-5f, "the sky probe is scaled too");
            }

            _ownership.SetSkyReflectionScale(0f);
            Assert.AreEqual(0f, RenderSettings.reflectionIntensity, 1e-5f, "a change after the apply reaches the scene");

            _applier.Revert();
            float restored = RenderSettings.reflectionIntensity;
            _ownership.SetSkyReflectionScale(1f);
            Assert.AreEqual(restored, RenderSettings.reflectionIntensity, 1e-5f, "a reverted look no longer listens");
        }

        [Test]
        public void Revert_WhileTheAmbientIsClaimed_LeavesTheOwnersAmbient_AndRestoresTheRest()
        {
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            _applier.Apply(_profile, Options(), LookMode.Apply);
            System.IDisposable claim = _ownership.Claim("day cycle");
            WriteOwnersAmbient();

            _applier.Revert();

            AssertOwnersAmbient("after the look reverted");
            Assert.IsNull(RenderSettings.skybox, "the sky the scene had is restored");
            Assert.IsFalse(RenderSettings.fog, "the fog the scene had is restored");
            claim.Dispose();
            Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode,
                "a reverted look does not take the ambient back when the claim ends");
        }

        private static void WriteOwnersAmbient()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.02f, 0.03f, 0.05f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.02f, 0.03f, 0.05f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.01f, 0.01f, 0.01f, 1f);
        }

        private static void AssertOwnersAmbient(string when)
        {
            Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode, "the owner's ambient mode stays " + when);
            Assert.AreEqual(new Color(0.02f, 0.03f, 0.05f, 1f), RenderSettings.ambientSkyColor,
                "the owner's sky ambient stays " + when);
            Assert.AreEqual(new Color(0.01f, 0.01f, 0.01f, 1f), RenderSettings.ambientGroundColor,
                "the owner's ground ambient stays " + when);
        }

        private static ResolvedGraphicsOptions Options(GraphicsOptions options = null)
        {
            return GraphicsOptionsResolver.Resolve(options ?? GraphicsOptions.Default, GraphicsQualityPreset.High);
        }

        private static void RequireProceduralSkyShader()
        {
            if (Shader.Find(RenderSettingsLookApplier.ProceduralSkyShaderName) == null)
            {
                Assert.Ignore("The built-in procedural sky shader is not available in this Editor.");
            }
        }

        private Material NewMaterial(string name)
        {
            Shader shader = Shader.Find(RenderSettingsLookApplier.ProceduralSkyShaderName) ??
                            Shader.Find("Hidden/InternalErrorShader");
            Material material = new(shader) { name = name };
            _created.Add(material);
            return material;
        }

        private Light NewDirectionalLight(LightShadows shadows)
        {
            GameObject owner = new("Look Test Light");
            _created.Add(owner);
            Light light = owner.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = shadows;
            return light;
        }

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
            }
        }
    }
}
