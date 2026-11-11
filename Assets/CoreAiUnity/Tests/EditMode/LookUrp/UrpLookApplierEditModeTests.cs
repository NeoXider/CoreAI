using System.Collections.Generic;
using CoreAI.Look;
using CoreAI.Look.Urp;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The URP look: one global Volume below priority 0 built from the profile's plain data, camera data
    /// with post-processing and anti-aliasing on every base game camera, the pipeline render scale, and
    /// the negative twins (a scene Volume and authored camera data survive FillMissing, Off changes
    /// nothing, Revert restores everything).
    /// </summary>
    public sealed class UrpLookApplierEditModeTests
    {
        private readonly List<Object> _created = new();
        private CoreAiLookProfile _profile;
        private UrpLookApplier _applier;

        [SetUp]
        public void SetUp()
        {
            _profile = CoreAiLookProfile.CreateDefault();
            _profile.RealtimeReflectionProbe = false;
            _created.Add(_profile);
            _applier = new UrpLookApplier();
        }

        [TearDown]
        public void TearDown()
        {
            _applier.Revert();
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
        public void Apply_CreatesOneGlobalVolumeBelowSceneVolumes()
        {
            _applier.Apply(_profile, Options(), LookMode.Apply);
            _applier.Apply(_profile, Options(), LookMode.Apply);

            Volume volume = _applier.Volume;
            Assert.IsNotNull(volume);
            Assert.IsTrue(volume.isGlobal);
            Assert.Less(volume.priority, 0f);
            Assert.AreEqual(UrpLookApplier.VolumePriority, volume.priority);
            Assert.AreEqual(1, CountObjectsNamed(UrpLookApplier.VolumeObjectName), "applying twice reuses it");
        }

        [Test]
        public void Volume_IsBuiltFromTheProfilesPlainData()
        {
            _profile.Tonemapping = CoreAiLookTonemapping.Neutral;
            _profile.BloomThreshold = 1.3f;
            _profile.Saturation = 12f;

            _applier.Apply(_profile, Options(), LookMode.Apply);

            VolumeProfile volumeProfile = _applier.Volume.sharedProfile;
            Assert.IsTrue(volumeProfile.TryGet(out Tonemapping tonemapping));
            Assert.IsTrue(tonemapping.mode.overrideState);
            Assert.AreEqual(TonemappingMode.Neutral, tonemapping.mode.value);
            Assert.IsTrue(volumeProfile.TryGet(out Bloom bloom));
            Assert.AreEqual(1.3f, bloom.threshold.value);
            Assert.GreaterOrEqual(bloom.threshold.value, 1f, "only HDR surfaces such as Neon bloom");
            Assert.IsTrue(volumeProfile.TryGet(out ColorAdjustments colorAdjustments));
            Assert.AreEqual(12f, colorAdjustments.saturation.value);
        }

        [Test]
        public void Camera_GetsPostProcessingAndAntiAliasing()
        {
            Camera camera = NewCamera(withUrpData: false);

            _applier.Apply(_profile, Options(), LookMode.Apply);

            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsNotNull(data);
            Assert.IsTrue(data.renderPostProcessing);
            Assert.AreEqual(AntialiasingMode.SubpixelMorphologicalAntiAliasing, data.antialiasing);
        }

        [Test]
        public void Camera_FollowsLaterOptionChanges()
        {
            Camera camera = NewCamera(withUrpData: true, postProcessing: false);
            _applier.Apply(_profile, Options(), LookMode.Apply);

            _applier.Apply(_profile, Options(new GraphicsOptions(postProcessing: false,
                antiAliasing: GraphicsAntiAliasing.Fxaa)), LookMode.Apply);

            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsFalse(data.renderPostProcessing);
            Assert.AreEqual(AntialiasingMode.FastApproximateAntialiasing, data.antialiasing);
        }

        [Test]
        public void Apply_OverridesAuthoredCameraData_AndRevertRestoresIt()
        {
            Camera camera = NewCamera(withUrpData: true, postProcessing: false);

            _applier.Apply(_profile, Options(), LookMode.Apply);
            Assert.IsTrue(camera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing);

            _applier.Revert();

            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsFalse(data.renderPostProcessing);
            Assert.AreEqual(AntialiasingMode.None, data.antialiasing);
            Assert.IsNull(_applier.Volume);
            Assert.AreEqual(0, CountObjectsNamed(UrpLookApplier.VolumeObjectName));
        }

        [Test]
        public void FillMissing_KeepsASceneVolumeAndAuthoredCameraData()
        {
            GameObject sceneVolumeObject = new("Scene Volume");
            _created.Add(sceneVolumeObject);
            Volume sceneVolume = sceneVolumeObject.AddComponent<Volume>();
            sceneVolume.isGlobal = true;
            VolumeProfile sceneProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            _created.Add(sceneProfile);
            sceneVolume.sharedProfile = sceneProfile;
            Camera camera = NewCamera(withUrpData: true, postProcessing: false);

            _applier.Apply(_profile, Options(), LookMode.FillMissing);

            Assert.IsNull(_applier.Volume, "the scene authored a global Volume");
            Assert.IsFalse(camera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing);
            Assert.AreSame(sceneProfile, sceneVolume.sharedProfile);
        }

        [Test]
        public void FillMissing_FillsACameraWithoutUrpData()
        {
            Camera camera = NewCamera(withUrpData: false);

            _applier.Apply(_profile, Options(), LookMode.FillMissing);

            Assert.IsTrue(camera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing);
        }

        [Test]
        public void Off_ChangesNothing()
        {
            Camera camera = NewCamera(withUrpData: false);

            _applier.Apply(_profile, Options(), LookMode.Off);

            Assert.IsNull(_applier.Volume);
            Assert.IsNull(camera.GetComponent<UniversalAdditionalCameraData>());
        }

        [Test]
        public void RenderScale_IsAppliedToTheActivePipeline_AndRestored()
        {
            UniversalRenderPipelineAsset pipeline =
                GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                Assert.Ignore("URP is not the active render pipeline in this project.");
            }

            float original = pipeline.renderScale;

            _applier.Apply(_profile, Options(new GraphicsOptions(renderScale: 0.6f)), LookMode.Apply);
            Assert.AreEqual(0.6f, pipeline.renderScale, 0.0001f);

            _applier.Revert();
            Assert.AreEqual(original, pipeline.renderScale, 0.0001f);
        }

        [Test]
        public void RenderScale_IsNotTouchedUnderFillMissing()
        {
            UniversalRenderPipelineAsset pipeline =
                GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                Assert.Ignore("URP is not the active render pipeline in this project.");
            }

            float original = pipeline.renderScale;

            _applier.Apply(_profile, Options(new GraphicsOptions(renderScale: 0.6f)), LookMode.FillMissing);

            Assert.AreEqual(original, pipeline.renderScale, 0.0001f);
        }

        [Test]
        public void Registration_OffersTheUrpApplierWhileUrpIsActive()
        {
            ICoreAiLookApplier applier = UrpLookRegistration.CreateWhenUrpActive();

            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
            {
                Assert.IsInstanceOf<UrpLookApplier>(applier);
            }
            else
            {
                Assert.IsNull(applier, "another pipeline keeps the render-settings default");
            }
        }

        private static ResolvedGraphicsOptions Options(GraphicsOptions options = null)
        {
            return GraphicsOptionsResolver.Resolve(options ?? GraphicsOptions.Default, GraphicsQualityPreset.High);
        }

        private Camera NewCamera(bool withUrpData, bool postProcessing = false)
        {
            GameObject owner = new("Look Test Camera");
            _created.Add(owner);
            Camera camera = owner.AddComponent<Camera>();
            if (withUrpData)
            {
                UniversalAdditionalCameraData data = owner.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = postProcessing;
                data.antialiasing = AntialiasingMode.None;
            }

            return camera;
        }

        private static int CountObjectsNamed(string name)
        {
            int count = 0;
            // WHY: the default Volume is a DontSave object, which FindObjectsByType does not return.
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate.name == name)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
