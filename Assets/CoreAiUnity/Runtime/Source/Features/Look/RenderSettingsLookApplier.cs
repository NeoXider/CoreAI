using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CoreAI.Look
{
    /// <summary>
    /// The render-pipeline-independent part of the default look: sky, ambient, a sky-only reflection
    /// probe, fog and directional-light shadows. A skybox the scene authored is kept (only the built-in
    /// default skybox, or none, is replaced) unless the profile names one. It is the default applier when
    /// no render-pipeline assembly registers one, and the base the URP applier extends with
    /// post-processing and anti-aliasing. Everything it changes is recorded on the first apply and
    /// restored by <see cref="Revert"/>. While another system claims the ambient
    /// (<see cref="LookAmbientOwnership"/>), the ambient mode, colours and intensity are left to it; the
    /// sky, reflections, fog and shadows still follow the look, and the look's ambient returns when the
    /// last claim is released.
    /// </summary>
    public class RenderSettingsLookApplier : ICoreAiLookApplier
    {
        /// <summary>The built-in shader the procedural sky is built from.</summary>
        public const string ProceduralSkyShaderName = "Skybox/Procedural";

        /// <summary>Name of the object that holds the sky-only reflection probe.</summary>
        public const string ReflectionProbeObjectName = "CoreAI Look Reflection";

        private const string BuiltInDefaultSkyboxName = "Default-Skybox";

        private static readonly int SunSizeId = Shader.PropertyToID("_SunSize");
        private static readonly int AtmosphereThicknessId = Shader.PropertyToID("_AtmosphereThickness");
        private static readonly int SkyTintId = Shader.PropertyToID("_SkyTint");
        private static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");

        private readonly IGameLogger _logger;
        private readonly LookAmbientOwnership _ambientOwnership;
        private readonly Dictionary<Light, LightShadows> _originalShadows = new();
        private RenderSettingsSnapshot _snapshot;
        private Scene _capturedScene;
        private Material _proceduralSky;
        private GameObject _probeObject;
        private ReflectionProbe _probe;
        private bool _skyShaderMissingReported;
        private CoreAiLookProfile _lastProfile;
        private ResolvedGraphicsOptions _lastOptions;
        private LookMode _lastMode = LookMode.Off;
        private bool _ownershipSubscribed;

        /// <summary>
        /// Creates the applier; <paramref name="logger"/> defaults to the unscoped CoreAI logger and
        /// <paramref name="ambientOwnership"/> to <see cref="LookAmbientOwnership.Shared"/>.
        /// </summary>
        public RenderSettingsLookApplier(IGameLogger logger = null, LookAmbientOwnership ambientOwnership = null)
        {
            _logger = logger;
            _ambientOwnership = ambientOwnership ?? LookAmbientOwnership.Shared;
        }

        /// <summary>The ownership record this applier yields the ambient to.</summary>
        public LookAmbientOwnership AmbientOwnership => _ambientOwnership;

        /// <summary>The procedural sky material this applier built, or null.</summary>
        public Material ProceduralSkyMaterial => _proceduralSky;

        /// <summary>The sky-only reflection probe this applier created, or null.</summary>
        public ReflectionProbe ReflectionProbe => _probe;

        /// <inheritdoc />
        public virtual void Apply(CoreAiLookProfile profile, ResolvedGraphicsOptions options, LookMode mode)
        {
            if (mode == LookMode.Off || profile == null || options == null)
            {
                return;
            }

            CaptureForActiveScene();
            _lastProfile = profile;
            _lastOptions = options;
            _lastMode = mode;
            if (!_ownershipSubscribed)
            {
                _ambientOwnership.Changed += HandleAmbientOwnershipChanged;
                _ambientOwnership.SkyReflectionScaleChanged += HandleSkyReflectionScaleChanged;
                _ownershipSubscribed = true;
            }

            if (mode == LookMode.Apply || _snapshot.SkyboxMissing)
            {
                ApplySky(profile, options);
            }

            if (mode == LookMode.Apply || _snapshot.FogMissing)
            {
                ApplyFog(profile, options);
            }

            if (mode == LookMode.Apply)
            {
                ApplyShadows(options);
            }
        }

        /// <inheritdoc />
        public virtual void Revert()
        {
            if (_ownershipSubscribed)
            {
                _ambientOwnership.Changed -= HandleAmbientOwnershipChanged;
                _ambientOwnership.SkyReflectionScaleChanged -= HandleSkyReflectionScaleChanged;
                _ownershipSubscribed = false;
            }

            _lastProfile = null;
            _lastOptions = null;
            _lastMode = LookMode.Off;
            if (_snapshot != null && SceneManager.GetActiveScene() == _capturedScene)
            {
                // WHY: an ambient someone else owns stays theirs; writing the scene's original back would
                // undo a day cycle that outlives this scope.
                _snapshot.Restore(!_ambientOwnership.IsClaimed);
                DynamicGI.UpdateEnvironment();
            }

            _snapshot = null;
            foreach (KeyValuePair<Light, LightShadows> entry in _originalShadows)
            {
                if (entry.Key != null)
                {
                    entry.Key.shadows = entry.Value;
                }
            }

            _originalShadows.Clear();
            DestroyOwned(_probeObject);
            _probeObject = null;
            _probe = null;
            DestroyOwned(_proceduralSky);
            _proceduralSky = null;
        }

        /// <summary>
        /// Hide flags for objects this applier creates: none in Play Mode, so the engine cleans them up with
        /// the session; DontSave in Edit Mode, so they never end up in a saved scene.
        /// </summary>
        protected static HideFlags OwnedHideFlags => Application.isPlaying ? HideFlags.None : HideFlags.DontSave;

        /// <summary>Destroys an object this applier created, in Play Mode and in Edit Mode alike.</summary>
        protected static void DestroyOwned(Object owned)
        {
            if (owned == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(owned);
            }
            else
            {
                Object.DestroyImmediate(owned);
            }
        }

        private IGameLogger Logger => _logger ?? GameLoggerUnscopedFallback.Instance;

        private void CaptureForActiveScene()
        {
            Scene active = SceneManager.GetActiveScene();
            if (_snapshot != null && active == _capturedScene)
            {
                return;
            }

            // WHY: render settings belong to the active scene. After a single-scene load the old
            // snapshot describes a scene that is gone, so it is dropped rather than written back.
            _snapshot = RenderSettingsSnapshot.Capture(BuiltInDefaultSkyboxName);
            _capturedScene = active;
        }

        private void HandleAmbientOwnershipChanged()
        {
            // WHY only on release: a claimant writes its own ambient right after claiming, while a released
            // ambient would keep the last owner's colours until the look's next apply.
            if (_ambientOwnership.IsClaimed || _snapshot == null || _lastProfile == null || _lastOptions == null ||
                _lastMode == LookMode.Off || SceneManager.GetActiveScene() != _capturedScene)
            {
                return;
            }

            if (_lastMode == LookMode.Apply || _snapshot.SkyboxMissing)
            {
                ApplySky(_lastProfile, _lastOptions);
            }
        }

        private void HandleSkyReflectionScaleChanged()
        {
            if (_snapshot == null || _lastProfile == null || _lastMode == LookMode.Off ||
                SceneManager.GetActiveScene() != _capturedScene ||
                !(_lastMode == LookMode.Apply || _snapshot.SkyboxMissing))
            {
                return;
            }

            ApplySkyReflectionScale(_lastProfile);
        }

        private void ApplySkyReflectionScale(CoreAiLookProfile profile)
        {
            float scale = _ambientOwnership.SkyReflectionScale;
            RenderSettings.reflectionIntensity = profile.ReflectionIntensity * scale;
            if (_probe != null)
            {
                _probe.intensity = scale;
            }
        }

        private void ApplySky(CoreAiLookProfile profile, ResolvedGraphicsOptions options)
        {
            bool ownsAmbient = !_ambientOwnership.IsClaimed;
            if (options.Sky && profile.SkyboxMaterial == null && !_snapshot.SkyboxMissing)
            {
                // WHY: a skybox the scene authored wins, the way a scene Volume wins over the default
                // one; only the built-in default skybox (or none) is replaced by the procedural sky.
                _snapshot.RestoreSky();
                if (ownsAmbient)
                {
                    _snapshot.RestoreAmbient();
                }
            }
            else if (options.Sky)
            {
                Material sky = profile.SkyboxMaterial != null ? profile.SkyboxMaterial : BuildProceduralSky(profile);
                if (sky != null)
                {
                    RenderSettings.skybox = sky;
                    if (ownsAmbient)
                    {
                        RenderSettings.ambientMode = AmbientMode.Skybox;
                    }
                }

                if (ownsAmbient)
                {
                    RenderSettings.ambientIntensity = profile.AmbientIntensity;
                }
            }
            else
            {
                RenderSettings.skybox = null;
                if (ownsAmbient)
                {
                    RenderSettings.ambientMode = AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor = profile.FlatAmbientSky;
                    RenderSettings.ambientEquatorColor = profile.FlatAmbientEquator;
                    RenderSettings.ambientGroundColor = profile.FlatAmbientGround;
                }
            }

            DynamicGI.UpdateEnvironment();
            UpdateReflectionProbe(profile, options.Sky && RenderSettings.skybox != null);
            ApplySkyReflectionScale(profile);
        }

        private Material BuildProceduralSky(CoreAiLookProfile profile)
        {
            if (_proceduralSky == null)
            {
                Shader shader = Shader.Find(ProceduralSkyShaderName);
                if (shader == null)
                {
                    if (!_skyShaderMissingReported)
                    {
                        _skyShaderMissingReported = true;
                        Logger.LogWarning(GameLogFeature.Core,
                            "[CoreAI.Look] Shader '" + ProceduralSkyShaderName + "' is not in this build, so " +
                            "the scene keeps its own skybox. Add it to Graphics Settings > Always Included " +
                            "Shaders, or assign a skybox material on the look profile.");
                    }

                    return null;
                }

                _proceduralSky = new Material(shader)
                {
                    name = "CoreAI Look Sky",
                    hideFlags = OwnedHideFlags
                };
            }

            _proceduralSky.SetFloat(SunSizeId, profile.SkySunSize);
            _proceduralSky.SetFloat(AtmosphereThicknessId, profile.SkyAtmosphereThickness);
            _proceduralSky.SetColor(SkyTintId, profile.SkyTint);
            _proceduralSky.SetColor(GroundColorId, profile.SkyGroundColor);
            _proceduralSky.SetFloat(ExposureId, profile.SkyExposure);
            return _proceduralSky;
        }

        private void UpdateReflectionProbe(CoreAiLookProfile profile, bool skyDrawn)
        {
            if (!profile.RealtimeReflectionProbe || !skyDrawn)
            {
                DestroyOwned(_probeObject);
                _probeObject = null;
                _probe = null;
                return;
            }

            if (_probe == null)
            {
                _probeObject = new GameObject(ReflectionProbeObjectName) { hideFlags = OwnedHideFlags };
                _probe = _probeObject.AddComponent<ReflectionProbe>();
                _probe.mode = ReflectionProbeMode.Realtime;
                _probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                _probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
                _probe.clearFlags = ReflectionProbeClearFlags.Skybox;
                // WHY: culling mask 0 renders the sky alone, so the probe costs six sky draws and never
                // the scene; importance 0 lets every probe the scene authored win where it overlaps.
                _probe.cullingMask = 0;
                _probe.importance = 0;
                _probe.boxProjection = false;
                _probe.size = new Vector3(100000f, 100000f, 100000f);
            }

            _probe.resolution = profile.ReflectionProbeResolution;
            _probe.RenderProbe();
        }

        private static void ApplyFog(CoreAiLookProfile profile, ResolvedGraphicsOptions options)
        {
            RenderSettings.fog = options.Fog;
            if (!options.Fog)
            {
                return;
            }

            RenderSettings.fogColor = profile.FogColor;
            RenderSettings.fogMode = profile.FogMode;
            RenderSettings.fogDensity = profile.FogDensity;
            RenderSettings.fogStartDistance = profile.FogStartDistance;
            RenderSettings.fogEndDistance = Mathf.Max(profile.FogEndDistance, profile.FogStartDistance + 0.01f);
        }

        private void ApplyShadows(ResolvedGraphicsOptions options)
        {
            LightShadows target = options.Shadows switch
            {
                GraphicsShadowQuality.Off => LightShadows.None,
                GraphicsShadowQuality.Hard => LightShadows.Hard,
                _ => LightShadows.Soft
            };

            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
            {
                if (light.type != LightType.Directional)
                {
                    continue;
                }

                if (!_originalShadows.TryGetValue(light, out LightShadows original))
                {
                    original = light.shadows;
                    _originalShadows.Add(light, original);
                }

                // WHY: a light the scene authored without shadows (a fill light) stays without them;
                // the option only chooses how the shadow-casting lights cast.
                if (original != LightShadows.None)
                {
                    light.shadows = target;
                }
            }
        }

        private sealed class RenderSettingsSnapshot
        {
            private Material _skybox;
            private AmbientMode _ambientMode;
            private float _ambientIntensity;
            private Color _ambientSky;
            private Color _ambientEquator;
            private Color _ambientGround;
            private float _reflectionIntensity;
            private bool _fog;
            private FogMode _fogMode;
            private Color _fogColor;
            private float _fogDensity;
            private float _fogStart;
            private float _fogEnd;

            public bool SkyboxMissing { get; private set; }

            public bool FogMissing { get; private set; }

            public static RenderSettingsSnapshot Capture(string builtInDefaultSkyboxName)
            {
                Material skybox = RenderSettings.skybox;
                return new RenderSettingsSnapshot
                {
                    _skybox = skybox,
                    _ambientMode = RenderSettings.ambientMode,
                    _ambientIntensity = RenderSettings.ambientIntensity,
                    _ambientSky = RenderSettings.ambientSkyColor,
                    _ambientEquator = RenderSettings.ambientEquatorColor,
                    _ambientGround = RenderSettings.ambientGroundColor,
                    _reflectionIntensity = RenderSettings.reflectionIntensity,
                    _fog = RenderSettings.fog,
                    _fogMode = RenderSettings.fogMode,
                    _fogColor = RenderSettings.fogColor,
                    _fogDensity = RenderSettings.fogDensity,
                    _fogStart = RenderSettings.fogStartDistance,
                    _fogEnd = RenderSettings.fogEndDistance,
                    SkyboxMissing = skybox == null ||
                                    string.Equals(skybox.name, builtInDefaultSkyboxName,
                                        System.StringComparison.Ordinal),
                    FogMissing = !RenderSettings.fog
                };
            }

            public void RestoreSky()
            {
                RenderSettings.skybox = _skybox;
            }

            public void RestoreAmbient()
            {
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.ambientSkyColor = _ambientSky;
                RenderSettings.ambientEquatorColor = _ambientEquator;
                RenderSettings.ambientGroundColor = _ambientGround;
            }

            public void Restore(bool includeAmbient)
            {
                RestoreSky();
                if (includeAmbient)
                {
                    RestoreAmbient();
                }

                RenderSettings.reflectionIntensity = _reflectionIntensity;
                RenderSettings.fog = _fog;
                RenderSettings.fogMode = _fogMode;
                RenderSettings.fogColor = _fogColor;
                RenderSettings.fogDensity = _fogDensity;
                RenderSettings.fogStartDistance = _fogStart;
                RenderSettings.fogEndDistance = _fogEnd;
            }
        }
    }
}
