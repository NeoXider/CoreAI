using System.Collections.Generic;
using CoreAI.Look;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CoreAI.Look.Urp
{
    /// <summary>
    /// URP default look: everything <see cref="RenderSettingsLookApplier"/> does, plus a global Volume
    /// (tonemapping, bloom, colour adjustments) built at runtime from the profile, URP camera data with
    /// post-processing and anti-aliasing on every base game camera (including cameras created later), and
    /// the pipeline render scale. The Volume sits at <see cref="VolumePriority"/>, so every Volume a scene
    /// authors at priority 0 or above wins.
    /// </summary>
    public sealed class UrpLookApplier : RenderSettingsLookApplier
    {
        /// <summary>Priority of the default Volume, below every scene-authored default of 0.</summary>
        public const float VolumePriority = -100f;

        /// <summary>Name of the object that holds the default Volume.</summary>
        public const string VolumeObjectName = "CoreAI Look Volume";

        private readonly Dictionary<Camera, CameraState> _cameras = new();
        private GameObject _volumeObject;
        private Volume _volume;
        private VolumeProfile _volumeProfile;
        private ResolvedGraphicsOptions _options;
        private LookMode _mode = LookMode.Off;
        private bool _subscribed;
        private UniversalRenderPipelineAsset _scaledPipeline;
        private float _originalRenderScale;

        /// <summary>
        /// Creates the applier; <paramref name="logger"/> defaults to the unscoped CoreAI logger and
        /// <paramref name="ambientOwnership"/> to <see cref="LookAmbientOwnership.Shared"/>.
        /// </summary>
        public UrpLookApplier(IGameLogger logger = null, LookAmbientOwnership ambientOwnership = null)
            : base(logger, ambientOwnership)
        {
        }

        /// <summary>The default Volume, or null when none was created.</summary>
        public Volume Volume => _volume;

        /// <inheritdoc />
        public override void Apply(CoreAiLookProfile profile, ResolvedGraphicsOptions options, LookMode mode)
        {
            base.Apply(profile, options, mode);
            if (mode == LookMode.Off || profile == null || options == null)
            {
                return;
            }

            _options = options;
            _mode = mode;
            ApplyVolume(profile, mode);
            ApplyRenderScale(options, mode);
            PruneDestroyedCameras();
            foreach (Camera camera in Camera.allCameras)
            {
                ApplyCamera(camera);
            }

            foreach (KeyValuePair<Camera, CameraState> entry in _cameras)
            {
                WriteOptions(entry.Value);
            }

            if (!_subscribed)
            {
                RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
                _subscribed = true;
            }
        }

        /// <inheritdoc />
        public override void Revert()
        {
            if (_subscribed)
            {
                RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
                _subscribed = false;
            }

            foreach (KeyValuePair<Camera, CameraState> entry in _cameras)
            {
                if (entry.Key != null)
                {
                    entry.Value.Restore();
                }
            }

            _cameras.Clear();
            RestoreRenderScale();
            DestroyOwned(_volumeObject);
            _volumeObject = null;
            _volume = null;
            if (_volumeProfile != null)
            {
                foreach (VolumeComponent component in _volumeProfile.components)
                {
                    DestroyOwned(component);
                }

                DestroyOwned(_volumeProfile);
                _volumeProfile = null;
            }

            _options = null;
            _mode = LookMode.Off;
            base.Revert();
        }

        private void HandleBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && !_cameras.ContainsKey(camera))
            {
                ApplyCamera(camera);
            }
        }

        private void ApplyVolume(CoreAiLookProfile profile, LookMode mode)
        {
            if (_volume == null && mode == LookMode.FillMissing && SceneHasGlobalVolume())
            {
                return;
            }

            if (_volume == null)
            {
                _volumeObject = new GameObject(VolumeObjectName) { hideFlags = OwnedHideFlags };
                _volumeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                _volumeProfile.name = "CoreAI Look Volume Profile";
                _volumeProfile.hideFlags = OwnedHideFlags;
                _volume = _volumeObject.AddComponent<Volume>();
                _volume.isGlobal = true;
                _volume.priority = VolumePriority;
                _volume.weight = 1f;
                _volume.sharedProfile = _volumeProfile;
            }

            Tonemapping tonemapping = GetOrAdd<Tonemapping>(_volumeProfile);
            tonemapping.mode.Override(ToUrp(profile.Tonemapping));

            Bloom bloom = GetOrAdd<Bloom>(_volumeProfile);
            bloom.active = profile.BloomIntensity > 0f;
            bloom.intensity.Override(profile.BloomIntensity);
            bloom.threshold.Override(profile.BloomThreshold);
            bloom.scatter.Override(profile.BloomScatter);

            ColorAdjustments colorAdjustments = GetOrAdd<ColorAdjustments>(_volumeProfile);
            colorAdjustments.postExposure.Override(profile.PostExposure);
            colorAdjustments.contrast.Override(profile.Contrast);
            colorAdjustments.saturation.Override(profile.Saturation);
        }

        private bool SceneHasGlobalVolume()
        {
            // WHY: FillMissing must know whether the scene authored its own global Volume, and only the
            // loaded scene can answer that; this runs once per apply, never per frame.
            foreach (Volume volume in Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
            {
                if (volume != _volume && volume.isGlobal && volume.enabled && volume.sharedProfile != null)
                {
                    return true;
                }
            }

            return false;
        }

        private void ApplyRenderScale(ResolvedGraphicsOptions options, LookMode mode)
        {
            if (mode != LookMode.Apply)
            {
                return;
            }

            UniversalRenderPipelineAsset pipeline =
                GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                return;
            }

            if (!ReferenceEquals(pipeline, _scaledPipeline))
            {
                RestoreRenderScale();
                _scaledPipeline = pipeline;
                _originalRenderScale = pipeline.renderScale;
            }

            // WHY: render scale lives on the pipeline asset, which in the Editor is a project asset; the
            // original value is restored by Revert when the scope is disposed (leaving Play Mode).
            pipeline.renderScale = options.RenderScale;
        }

        private void RestoreRenderScale()
        {
            if (_scaledPipeline != null)
            {
                _scaledPipeline.renderScale = _originalRenderScale;
            }

            _scaledPipeline = null;
        }

        private void ApplyCamera(Camera camera)
        {
            if (camera == null || _options == null || _mode == LookMode.Off || camera.cameraType != CameraType.Game)
            {
                return;
            }

            if (!_cameras.TryGetValue(camera, out CameraState state))
            {
                UniversalAdditionalCameraData existing = camera.GetComponent<UniversalAdditionalCameraData>();
                if (_mode == LookMode.FillMissing && existing != null)
                {
                    state = CameraState.Untouched;
                }
                else
                {
                    UniversalAdditionalCameraData data = existing != null
                        ? existing
                        : camera.GetUniversalAdditionalCameraData();
                    state = new CameraState(data);
                }

                _cameras.Add(camera, state);
            }

            WriteOptions(state);
        }

        private void WriteOptions(CameraState state)
        {
            if (_options == null || !state.Managed || state.Data == null ||
                state.Data.renderType != CameraRenderType.Base)
            {
                return;
            }

            state.Data.renderPostProcessing = _options.PostProcessing;
            state.Data.antialiasing = ToUrp(_options.AntiAliasing);
            state.Data.antialiasingQuality = _options.AntiAliasing == GraphicsAntiAliasing.Smaa
                ? AntialiasingQuality.High
                : AntialiasingQuality.Medium;
        }

        private void PruneDestroyedCameras()
        {
            List<Camera> destroyed = null;
            foreach (KeyValuePair<Camera, CameraState> entry in _cameras)
            {
                if (entry.Key == null)
                {
                    destroyed ??= new List<Camera>();
                    destroyed.Add(entry.Key);
                }
            }

            if (destroyed == null)
            {
                return;
            }

            foreach (Camera camera in destroyed)
            {
                _cameras.Remove(camera);
            }
        }

        private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet(out T component))
            {
                return component;
            }

            T added = profile.Add<T>();
            added.hideFlags = OwnedHideFlags;
            return added;
        }

        private static TonemappingMode ToUrp(CoreAiLookTonemapping tonemapping)
        {
            switch (tonemapping)
            {
                case CoreAiLookTonemapping.None:
                    return TonemappingMode.None;
                case CoreAiLookTonemapping.Neutral:
                    return TonemappingMode.Neutral;
                default:
                    return TonemappingMode.ACES;
            }
        }

        private static AntialiasingMode ToUrp(GraphicsAntiAliasing antiAliasing)
        {
            switch (antiAliasing)
            {
                case GraphicsAntiAliasing.Fxaa:
                    return AntialiasingMode.FastApproximateAntialiasing;
                case GraphicsAntiAliasing.Smaa:
                    return AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                default:
                    return AntialiasingMode.None;
            }
        }

        /// <summary>One camera's URP data as found, so <see cref="Revert"/> can put it back.</summary>
        private sealed class CameraState
        {
            public static readonly CameraState Untouched = new(null);

            private readonly bool _postProcessing;
            private readonly AntialiasingMode _antialiasing;
            private readonly AntialiasingQuality _antialiasingQuality;

            public CameraState(UniversalAdditionalCameraData data)
            {
                Data = data;
                if (data == null)
                {
                    return;
                }

                _postProcessing = data.renderPostProcessing;
                _antialiasing = data.antialiasing;
                _antialiasingQuality = data.antialiasingQuality;
            }

            public UniversalAdditionalCameraData Data { get; }

            public bool Managed => Data != null;

            public void Restore()
            {
                if (Data == null)
                {
                    return;
                }

                Data.renderPostProcessing = _postProcessing;
                Data.antialiasing = _antialiasing;
                Data.antialiasingQuality = _antialiasingQuality;
            }
        }
    }
}
