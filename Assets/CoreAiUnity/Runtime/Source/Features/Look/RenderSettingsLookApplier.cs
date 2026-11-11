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
    /// last claim is released. With its procedural sky the look follows the sun: the sky below the horizon continues
    /// the haze above it (<see cref="CoreAiLookProfile.SkyGroundFollowsHaze"/>) and the fog takes the haze colour
    /// (<see cref="CoreAiLookProfile.FogFollowsHaze"/>), recomputed whenever the sun has moved before a frame renders.
    /// A fog another system claims (<see cref="LookAmbientOwnership.ClaimFog"/>, a world's <c>Lighting</c> fog) is
    /// left to it, and the look's fog returns when the last fog claim is released.
    /// </summary>
    public class RenderSettingsLookApplier : ICoreAiLookApplier
    {
        /// <summary>The built-in shader the procedural sky is built from.</summary>
        public const string ProceduralSkyShaderName = "Skybox/Procedural";

        /// <summary>Name of the object that holds the sky-only reflection probe.</summary>
        public const string ReflectionProbeObjectName = "CoreAI Look Reflection";

        /// <summary>Name of the object that draws the fog colour over the horizon (<see cref="CoreAiLookProfile.HorizonFollowsFog"/>).</summary>
        public const string HorizonObjectName = "CoreAI Look Horizon";

        /// <summary>Resources path of the horizon dome's shader.</summary>
        public const string HorizonShaderResource = "CoreAILook/HorizonFog";

        private const string HorizonShaderName = "CoreAI/Look/Horizon Fog";

        private const string BuiltInDefaultSkyboxName = "Default-Skybox";

        private static readonly int SunSizeId = Shader.PropertyToID("_SunSize");
        private static readonly int AtmosphereThicknessId = Shader.PropertyToID("_AtmosphereThickness");
        private static readonly int SkyTintId = Shader.PropertyToID("_SkyTint");
        private static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");

        /// <summary>Cosine of the sun turn below which the haze colours are not recomputed (about 0.05 degrees).</summary>
        private const float HazeSunTolerance = 0.9999996f;

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
        private bool _hazeSubscribed;
        private bool _hazeValid;
        private Vector3 _hazeSun;
        private bool _hazeGroundFollows;
        private bool _hazeFogFollows;
        private bool _hazeTwilight;
        private bool _ownsFog;
        private GameObject _horizonObject;
        private Material _horizonMaterial;
        private Mesh _horizonMesh;
        private bool _horizonShaderMissingReported;

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
                _ambientOwnership.FogChanged += HandleFogOwnershipChanged;
                _ownershipSubscribed = true;
            }

            if (mode == LookMode.Apply || _snapshot.SkyboxMissing)
            {
                ApplySky(profile, options);
            }

            _ownsFog = mode == LookMode.Apply || _snapshot.FogMissing;
            if (_ownsFog && !_ambientOwnership.IsFogClaimed)
            {
                ApplyFog(profile, options);
            }

            if (mode == LookMode.Apply)
            {
                ApplyShadows(options);
            }

            _hazeValid = false;
            UpdateHaze();
            UpdateHorizon();
            if (!_hazeSubscribed)
            {
                RenderPipelineManager.beginContextRendering += HandleBeginContextRendering;
                _hazeSubscribed = true;
            }
        }

        /// <summary>
        /// The ground colour the procedural sky was last given (linear) and the fog colour the haze set; false while the
        /// look does not draw its procedural sky or the sky does not follow the haze.
        /// </summary>
        public bool TryGetHazeColors(out Color groundLinear, out Color fogLinear)
        {
            groundLinear = default;
            fogLinear = default;
            if (_proceduralSky == null || !_hazeValid)
            {
                return false;
            }

            groundLinear = _proceduralSky.GetColor(GroundColorId).linear;
            fogLinear = RenderSettings.fogColor.linear;
            return true;
        }

        /// <inheritdoc />
        public virtual void Revert()
        {
            if (_ownershipSubscribed)
            {
                _ambientOwnership.Changed -= HandleAmbientOwnershipChanged;
                _ambientOwnership.SkyReflectionScaleChanged -= HandleSkyReflectionScaleChanged;
                _ambientOwnership.FogChanged -= HandleFogOwnershipChanged;
                _ownershipSubscribed = false;
            }

            if (_hazeSubscribed)
            {
                RenderPipelineManager.beginContextRendering -= HandleBeginContextRendering;
                _hazeSubscribed = false;
            }

            _hazeValid = false;
            _ownsFog = false;
            DestroyOwned(_horizonObject);
            _horizonObject = null;
            DestroyOwned(_horizonMaterial);
            _horizonMaterial = null;
            DestroyOwned(_horizonMesh);
            _horizonMesh = null;

            _lastProfile = null;
            _lastOptions = null;
            _lastMode = LookMode.Off;
            if (_snapshot != null && SceneManager.GetActiveScene() == _capturedScene)
            {
                // WHY: an ambient someone else owns stays theirs; writing the scene's original back would
                // undo a day cycle that outlives this scope.
                _snapshot.Restore(!_ambientOwnership.IsClaimed, !_ambientOwnership.IsFogClaimed);
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

        private void HandleFogOwnershipChanged()
        {
            // WHY only on release: the claimant writes its own fog right after claiming; a released fog would keep the
            // last owner's colour and distances until the look's next apply.
            if (_ambientOwnership.IsFogClaimed || !_ownsFog || _snapshot == null || _lastProfile == null
                || _lastOptions == null || _lastMode == LookMode.Off || SceneManager.GetActiveScene() != _capturedScene)
            {
                return;
            }

            ApplyFog(_lastProfile, _lastOptions);
            _hazeValid = false;
            UpdateHaze();
        }

        private void HandleBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            UpdateHaze();
            UpdateHorizon();
        }

        /// <summary>The horizon dome, or null while the look has none (the profile switched it off, no fog, no sky).</summary>
        public GameObject HorizonObject => _horizonObject != null && _horizonObject.activeSelf ? _horizonObject : null;

        /// <summary>
        /// Keeps the horizon dome on exactly while fog is on and a sky is drawn; it reads the fog colour itself, so a
        /// world that claims the fog (<see cref="LookAmbientOwnership.ClaimFog"/>) is covered as well as the look's haze.
        /// </summary>
        private void UpdateHorizon()
        {
            CoreAiLookProfile profile = _lastProfile;
            bool wanted = profile != null && profile.HorizonFollowsFog && _lastMode != LookMode.Off && RenderSettings.fog
                          && RenderSettings.skybox != null;
            if (!wanted)
            {
                if (_horizonObject != null && _horizonObject.activeSelf)
                {
                    _horizonObject.SetActive(false);
                }

                return;
            }

            if (_horizonObject == null && !BuildHorizon())
            {
                return;
            }

            if (!_horizonObject.activeSelf)
            {
                _horizonObject.SetActive(true);
            }
        }

        private bool BuildHorizon()
        {
            Shader shader = Resources.Load<Shader>(HorizonShaderResource);
            shader = shader != null ? shader : Shader.Find(HorizonShaderName);
            if (shader == null)
            {
                if (!_horizonShaderMissingReported)
                {
                    _horizonShaderMissingReported = true;
                    Logger.LogWarning(GameLogFeature.Core,
                        "[CoreAI.Look] Shader '" + HorizonShaderName + "' is not in this build, so the fog does not " +
                        "cover the horizon.");
                }

                return false;
            }

            _horizonMaterial = new Material(shader) { name = "CoreAI Look Horizon", hideFlags = OwnedHideFlags };
            _horizonMesh = BuildDome();
            _horizonObject = new GameObject(HorizonObjectName) { hideFlags = OwnedHideFlags };
            _horizonObject.AddComponent<MeshFilter>().sharedMesh = _horizonMesh;
            MeshRenderer renderer = _horizonObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _horizonMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return true;
        }

        /// <summary>A unit sphere the shader moves to the far plane round each camera; its bounds keep it from being culled.</summary>
        private static Mesh BuildDome()
        {
            const int rings = 12;
            const int segments = 24;
            List<Vector3> vertices = new();
            List<int> triangles = new();
            for (int ring = 0; ring <= rings; ring++)
            {
                float polar = Mathf.PI * ring / rings;
                for (int segment = 0; segment <= segments; segment++)
                {
                    float azimuth = 2f * Mathf.PI * segment / segments;
                    vertices.Add(new Vector3(Mathf.Sin(polar) * Mathf.Cos(azimuth), Mathf.Cos(polar),
                        Mathf.Sin(polar) * Mathf.Sin(azimuth)));
                }
            }

            for (int ring = 0; ring < rings; ring++)
            {
                for (int segment = 0; segment < segments; segment++)
                {
                    int a = ring * (segments + 1) + segment;
                    int b = a + segments + 1;
                    triangles.Add(a);
                    triangles.Add(a + 1);
                    triangles.Add(b);
                    triangles.Add(b);
                    triangles.Add(a + 1);
                    triangles.Add(b + 1);
                }
            }

            Mesh mesh = new() { name = "CoreAI Look Horizon Dome", hideFlags = OwnedHideFlags };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e7f, 1e7f));
            return mesh;
        }

        /// <summary>
        /// Recolours the procedural sky's ground and, while the look owns the fog, the fog from the sun's direction,
        /// when the sun has turned since the last time.
        /// </summary>
        private void UpdateHaze()
        {
            CoreAiLookProfile profile = _lastProfile;
            if (profile == null || _proceduralSky == null || RenderSettings.skybox != _proceduralSky)
            {
                return;
            }

            Light sun = RenderSettings.sun;
            if (sun == null)
            {
                return;
            }

            Vector3 towardSun = -sun.transform.forward;
            // WHY the switches too: a profile switched on or off while the sun stands still must still take effect.
            if (_hazeValid && Vector3.Dot(towardSun, _hazeSun) > HazeSunTolerance
                           && _hazeGroundFollows == profile.SkyGroundFollowsHaze
                           && _hazeFogFollows == profile.FogFollowsHaze
                           && _hazeTwilight == profile.TwilightSky)
            {
                return;
            }

            _hazeSun = towardSun;
            _hazeGroundFollows = profile.SkyGroundFollowsHaze;
            _hazeFogFollows = profile.FogFollowsHaze;
            _hazeTwilight = profile.TwilightSky;
            _hazeValid = true;
            Color tint = profile.SkyTint;
            float thickness = profile.SkyAtmosphereThickness;
            Color skyTint = tint;
            float skyThickness = thickness;
            if (profile.TwilightSky)
            {
                ProceduralSkyHaze.TwilightSky(towardSun.y, tint.r, tint.g, tint.b, thickness, out float twilightR,
                    out float twilightG, out float twilightB, out skyThickness);
                skyTint = new Color(twilightR, twilightG, twilightB, tint.a);
            }

            _proceduralSky.SetColor(SkyTintId, skyTint);
            _proceduralSky.SetFloat(AtmosphereThicknessId, skyThickness);
            if (profile.SkyGroundFollowsHaze)
            {
                ProceduralSkyHaze.GroundColorMatchingHaze(towardSun.x, towardSun.y, towardSun.z, skyTint.r, skyTint.g,
                    skyTint.b, skyThickness, out float red, out float green, out float blue);
                // WHY gamma: in a linear project Unity converts a material colour to linear before the shader sees
                // it, and the sky uses its ground colour as it arrives.
                _proceduralSky.SetColor(GroundColorId, new Color(red, green, blue, 1f).gamma);
            }
            else
            {
                _proceduralSky.SetColor(GroundColorId, profile.SkyGroundColor);
            }

            if (!_ownsFog || _ambientOwnership.IsFogClaimed || !RenderSettings.fog)
            {
                return;
            }

            if (!profile.FogFollowsHaze)
            {
                RenderSettings.fogColor = profile.FogColor;
            }
            else
            {
                // WHY the authored sky and not the twilight one: its haze keeps the warm dusk horizon, which the
                // horizon dome then fades into the blue twilight above it.
                ProceduralSkyHaze.HorizonHaze(towardSun.x, towardSun.y, towardSun.z, tint.r, tint.g, tint.b, thickness,
                    out float red, out float green, out float blue);
                float exposure = profile.SkyExposure;
                Color haze = new(Mathf.Clamp01(red * exposure), Mathf.Clamp01(green * exposure),
                    Mathf.Clamp01(blue * exposure), 1f);
                RenderSettings.fogColor = haze.gamma;
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
            // WHY: the ground colour just written is the profile's; the haze colours are recomputed before the next
            // frame even when the sun has not moved.
            _hazeValid = false;
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

            public void Restore(bool includeAmbient, bool includeFog)
            {
                RestoreSky();
                if (includeAmbient)
                {
                    RestoreAmbient();
                }

                RenderSettings.reflectionIntensity = _reflectionIntensity;
                if (!includeFog)
                {
                    return;
                }

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
