using UnityEngine;

namespace CoreAI.Look
{
    /// <summary>Tonemapping curve of the default look.</summary>
    public enum CoreAiLookTonemapping
    {
        /// <summary>No tonemapping: HDR values clip at 1.</summary>
        None = 0,

        /// <summary>Neutral curve: keeps hues, compresses highlights gently.</summary>
        Neutral = 1,

        /// <summary>ACES filmic curve: richer contrast, the default.</summary>
        Aces = 2
    }

    /// <summary>
    /// Plain look data: tonemapping, bloom and colour adjustments, sky, ambient, reflection and fog.
    /// It holds no scene references; the applier builds the Volume profile and the sky material from these
    /// values at runtime. Every default lives in code, so a fresh project gets the look without any
    /// asset; create one (Assets/Create/CoreAI/Graphics/Look Profile) only to override values.
    /// </summary>
    [CreateAssetMenu(fileName = "CoreAiLookProfile", menuName = "CoreAI/Graphics/Look Profile")]
    public sealed class CoreAiLookProfile : ScriptableObject
    {
        [Header("Post-processing")]
        [Tooltip("Tonemapping curve applied to the HDR image.")]
        [SerializeField]
        private CoreAiLookTonemapping tonemapping = CoreAiLookTonemapping.Aces;

        [Tooltip("Bloom strength. Only pixels brighter than the threshold bloom, so Neon glows and plain " +
                 "lit surfaces do not.")]
        [Min(0f)]
        [SerializeField]
        private float bloomIntensity = 0.35f;

        [Tooltip("Brightness above which a pixel blooms. Keep it at or above 1 so only HDR surfaces bloom.")]
        [Min(0f)]
        [SerializeField]
        private float bloomThreshold = 1.1f;

        [Tooltip("How far bloom spreads, 0..1.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float bloomScatter = 0.6f;

        [Tooltip("Exposure shift in EV applied after tonemapping.")]
        [SerializeField]
        private float postExposure;

        [Tooltip("Contrast adjustment, -100..100.")]
        [Range(-100f, 100f)]
        [SerializeField]
        private float contrast = 6f;

        [Tooltip("Saturation adjustment, -100..100.")]
        [Range(-100f, 100f)]
        [SerializeField]
        private float saturation = 8f;

        [Header("Sky")]
        [Tooltip("Optional skybox material. Empty builds a procedural sky from the values below.")]
        [SerializeField]
        private Material skyboxMaterial;

        [Tooltip("Procedural sky tint.")]
        [SerializeField]
        private Color skyTint = new(0.52f, 0.58f, 0.66f, 1f);

        [Tooltip("Procedural sky ground colour below the horizon.")]
        [SerializeField]
        private Color skyGroundColor = new(0.38f, 0.36f, 0.34f, 1f);

        [Tooltip("Procedural sky sun disc size.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float skySunSize = 0.035f;

        [Tooltip("Procedural sky atmosphere thickness.")]
        [Range(0f, 5f)]
        [SerializeField]
        private float skyAtmosphereThickness = 0.9f;

        [Tooltip("Procedural sky exposure.")]
        [Range(0f, 8f)]
        [SerializeField]
        private float skyExposure = 1.25f;

        [Header("Ambient and reflection")]
        [Tooltip("Ambient intensity while the sky lights the scene.")]
        [Range(0f, 8f)]
        [SerializeField]
        private float ambientIntensity = 1f;

        [Tooltip("Environment reflection intensity.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float reflectionIntensity = 1f;

        [Tooltip("Render a sky-only realtime reflection probe after the sky changes, so metals reflect the " +
                 "sky in a built player.")]
        [SerializeField]
        private bool realtimeReflectionProbe = true;

        [Tooltip("Cubemap face size of that reflection probe.")]
        [SerializeField]
        private int reflectionProbeResolution = 128;

        [Tooltip("Ambient sky colour used while the sky is switched off.")]
        [SerializeField]
        private Color flatAmbientSky = new(0.55f, 0.6f, 0.68f, 1f);

        [Tooltip("Ambient horizon colour used while the sky is switched off.")]
        [SerializeField]
        private Color flatAmbientEquator = new(0.45f, 0.46f, 0.48f, 1f);

        [Tooltip("Ambient ground colour used while the sky is switched off.")]
        [SerializeField]
        private Color flatAmbientGround = new(0.3f, 0.29f, 0.28f, 1f);

        [Header("Fog")]
        [Tooltip("Fog colour.")]
        [SerializeField]
        private Color fogColor = new(0.66f, 0.74f, 0.82f, 1f);

        [Tooltip("Fog falloff.")]
        [SerializeField]
        private FogMode fogMode = FogMode.ExponentialSquared;

        [Tooltip("Exponential fog density per metre.")]
        [Min(0f)]
        [SerializeField]
        private float fogDensity = 0.006f;

        [Tooltip("Linear fog start distance in metres.")]
        [Min(0f)]
        [SerializeField]
        private float fogStartDistance = 30f;

        [Tooltip("Linear fog end distance in metres.")]
        [Min(0f)]
        [SerializeField]
        private float fogEndDistance = 300f;

        /// <summary>Tonemapping curve.</summary>
        public CoreAiLookTonemapping Tonemapping
        {
            get => tonemapping;
            set => tonemapping = value;
        }

        /// <summary>Bloom strength.</summary>
        public float BloomIntensity
        {
            get => bloomIntensity;
            set => bloomIntensity = Mathf.Max(0f, value);
        }

        /// <summary>Brightness above which a pixel blooms.</summary>
        public float BloomThreshold
        {
            get => bloomThreshold;
            set => bloomThreshold = Mathf.Max(0f, value);
        }

        /// <summary>Bloom spread, 0..1.</summary>
        public float BloomScatter
        {
            get => bloomScatter;
            set => bloomScatter = Mathf.Clamp01(value);
        }

        /// <summary>Exposure shift in EV.</summary>
        public float PostExposure
        {
            get => postExposure;
            set => postExposure = value;
        }

        /// <summary>Contrast adjustment, -100..100.</summary>
        public float Contrast
        {
            get => contrast;
            set => contrast = Mathf.Clamp(value, -100f, 100f);
        }

        /// <summary>Saturation adjustment, -100..100.</summary>
        public float Saturation
        {
            get => saturation;
            set => saturation = Mathf.Clamp(value, -100f, 100f);
        }

        /// <summary>Optional skybox material; null builds the procedural sky.</summary>
        public Material SkyboxMaterial
        {
            get => skyboxMaterial;
            set => skyboxMaterial = value;
        }

        /// <summary>Procedural sky tint.</summary>
        public Color SkyTint
        {
            get => skyTint;
            set => skyTint = value;
        }

        /// <summary>Procedural sky ground colour.</summary>
        public Color SkyGroundColor
        {
            get => skyGroundColor;
            set => skyGroundColor = value;
        }

        /// <summary>Procedural sky sun disc size.</summary>
        public float SkySunSize
        {
            get => skySunSize;
            set => skySunSize = Mathf.Clamp01(value);
        }

        /// <summary>Procedural sky atmosphere thickness.</summary>
        public float SkyAtmosphereThickness
        {
            get => skyAtmosphereThickness;
            set => skyAtmosphereThickness = Mathf.Clamp(value, 0f, 5f);
        }

        /// <summary>Procedural sky exposure.</summary>
        public float SkyExposure
        {
            get => skyExposure;
            set => skyExposure = Mathf.Clamp(value, 0f, 8f);
        }

        /// <summary>Ambient intensity while the sky lights the scene.</summary>
        public float AmbientIntensity
        {
            get => ambientIntensity;
            set => ambientIntensity = Mathf.Clamp(value, 0f, 8f);
        }

        /// <summary>Environment reflection intensity.</summary>
        public float ReflectionIntensity
        {
            get => reflectionIntensity;
            set => reflectionIntensity = Mathf.Clamp01(value);
        }

        /// <summary>Whether a sky-only realtime reflection probe is rendered after sky changes.</summary>
        public bool RealtimeReflectionProbe
        {
            get => realtimeReflectionProbe;
            set => realtimeReflectionProbe = value;
        }

        /// <summary>Cubemap face size of the reflection probe, clamped to 16..2048.</summary>
        public int ReflectionProbeResolution
        {
            get => Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(16, reflectionProbeResolution)), 16, 2048);
            set => reflectionProbeResolution = value;
        }

        /// <summary>Ambient sky colour while the sky is off.</summary>
        public Color FlatAmbientSky
        {
            get => flatAmbientSky;
            set => flatAmbientSky = value;
        }

        /// <summary>Ambient horizon colour while the sky is off.</summary>
        public Color FlatAmbientEquator
        {
            get => flatAmbientEquator;
            set => flatAmbientEquator = value;
        }

        /// <summary>Ambient ground colour while the sky is off.</summary>
        public Color FlatAmbientGround
        {
            get => flatAmbientGround;
            set => flatAmbientGround = value;
        }

        /// <summary>Fog colour.</summary>
        public Color FogColor
        {
            get => fogColor;
            set => fogColor = value;
        }

        /// <summary>Fog falloff.</summary>
        public FogMode FogMode
        {
            get => fogMode;
            set => fogMode = value;
        }

        /// <summary>Exponential fog density per metre.</summary>
        public float FogDensity
        {
            get => fogDensity;
            set => fogDensity = Mathf.Max(0f, value);
        }

        /// <summary>Linear fog start distance in metres.</summary>
        public float FogStartDistance
        {
            get => fogStartDistance;
            set => fogStartDistance = Mathf.Max(0f, value);
        }

        /// <summary>Linear fog end distance in metres.</summary>
        public float FogEndDistance
        {
            get => fogEndDistance;
            set => fogEndDistance = Mathf.Max(0f, value);
        }

        /// <summary>
        /// A transient profile carrying the code defaults. It is never saved into a scene or the project.
        /// </summary>
        public static CoreAiLookProfile CreateDefault()
        {
            CoreAiLookProfile profile = CreateInstance<CoreAiLookProfile>();
            profile.name = "CoreAiLookProfile (code defaults)";
            // WHY: DontSave would also keep a Play Mode instance alive after the session ends.
            profile.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave;
            return profile;
        }
    }
}
