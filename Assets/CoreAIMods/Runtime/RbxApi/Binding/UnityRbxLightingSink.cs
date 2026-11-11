using CoreAI.Look;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The default <see cref="IRbxLightingSink"/>: points the scene's sun light along
    /// <c>Lighting:GetSunDirection()</c> with <c>Brightness</c> times daylight as its intensity, makes it
    /// <c>RenderSettings.sun</c> so the sky follows it, and sets a trilight ambient (sky and equator from
    /// the daylight-scaled <c>OutdoorAmbient</c>, ground from <c>Ambient</c>; DEV P9-a). It touches only
    /// <c>RenderSettings</c> and that one light, records what it changes on the first apply and puts it
    /// back in <see cref="Revert"/>. From the first apply until that revert it holds a
    /// <see cref="LookAmbientOwnership"/> claim, so the default look leaves the ambient to the day cycle and
    /// takes it back once the revert has restored the scene.
    /// </summary>
    public sealed class UnityRbxLightingSink : IRbxLightingSink
    {
        private Light _sun;
        private bool _sunResolved;
        private bool _captured;
        private Light _originalSunSetting;
        private Quaternion _originalSunRotation;
        private float _originalSunIntensity;
        private bool _originalSunEnabled;
        private AmbientMode _originalAmbientMode;
        private Color _originalAmbientSky;
        private Color _originalAmbientEquator;
        private Color _originalAmbientGround;
        private bool _hasLastState;
        private RbxLightingState _lastState;
        private System.IDisposable _ambientClaim;
        private readonly LookAmbientOwnership _ambientOwnership;

        /// <summary>The owner name this sink claims the ambient under (<see cref="LookAmbientOwnership.Owners"/>).</summary>
        public const string AmbientOwnerName = "CoreAI.Mods Lighting";

        /// <param name="sun">The directional light to drive; null resolves <c>RenderSettings.sun</c>, then the
        /// scene's first active directional light, at the first apply.</param>
        public UnityRbxLightingSink(Light sun = null) : this(sun, null)
        {
        }

        /// <param name="sun">The directional light to drive; null resolves it at the first apply.</param>
        /// <param name="ambientOwnership">Where the ambient is claimed while this sink drives it; null uses
        /// <see cref="LookAmbientOwnership.Shared"/>, the instance the default look appliers read.</param>
        public UnityRbxLightingSink(Light sun, LookAmbientOwnership ambientOwnership)
        {
            _sun = sun;
            _sunResolved = sun != null;
            _ambientOwnership = ambientOwnership ?? LookAmbientOwnership.Shared;
        }

        /// <summary>Where this sink claims the ambient.</summary>
        public LookAmbientOwnership AmbientOwnership => _ambientOwnership;

        /// <summary>True while this sink holds its ambient claim: from the first apply until <see cref="Revert"/>.</summary>
        public bool ClaimsAmbient => _ambientClaim != null;

        /// <summary>Scales the sun's intensity after the Roblox mapping (1 keeps <c>Brightness</c> as the intensity at noon).</summary>
        public float SunIntensityScale { get; set; } = 1f;

        /// <summary>The light this sink drives, or null when the scene has none.</summary>
        public Light Sun => _sun;

        /// <summary>The last state applied, for a host that re-applies after its own look pass.</summary>
        public bool TryGetLastState(out RbxLightingState state)
        {
            state = _lastState;
            return _hasLastState;
        }

        /// <inheritdoc />
        public void Apply(in RbxLightingState state)
        {
            _lastState = state;
            _hasLastState = true;
            Light sun = ResolveSun();
            Capture(sun);
            if (sun != null)
            {
                Vector3 towardSun = RbxSpace.DirectionToUnity(state.SunDirection);
                if (towardSun.sqrMagnitude > 1e-8f)
                {
                    Vector3 travel = -towardSun.normalized;
                    Vector3 up = Mathf.Abs(travel.y) > 0.999f ? Vector3.forward : Vector3.up;
                    sun.transform.rotation = Quaternion.LookRotation(travel, up);
                }

                float intensity = RbxLightingMapping.SunIntensity(state) * SunIntensityScale;
                sun.intensity = intensity;
                // WHY off rather than at zero intensity: a shadow-casting light still renders its shadow
                // map at intensity 0, which on WebGL is the most expensive pass of a night frame.
                sun.enabled = intensity > 0f;
                RenderSettings.sun = sun;
            }

            RbxColor3 outdoor = RbxLightingMapping.EffectiveOutdoorAmbient(state);
            // WHY: while the world drives its lighting the ambient is the day cycle's; the default look leaves it
            // alone until Revert releases the claim, and then puts its own ambient back.
            _ambientClaim ??= _ambientOwnership.Claim(AmbientOwnerName);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ToColor(outdoor);
            RenderSettings.ambientEquatorColor = ToColor(outdoor);
            RenderSettings.ambientGroundColor = ToColor(state.Ambient);
        }

        /// <summary>Puts the last applied state on screen again, after a host pass rewrote <c>RenderSettings</c>.</summary>
        public void Reapply()
        {
            if (_hasLastState)
            {
                RbxLightingState state = _lastState;
                Apply(in state);
            }
        }

        /// <summary>Restores what the first apply recorded. Idempotent; a later apply records again.</summary>
        public void Revert()
        {
            if (!_captured)
            {
                return;
            }

            _captured = false;
            _hasLastState = false;
            RenderSettings.ambientMode = _originalAmbientMode;
            RenderSettings.ambientSkyColor = _originalAmbientSky;
            RenderSettings.ambientEquatorColor = _originalAmbientEquator;
            RenderSettings.ambientGroundColor = _originalAmbientGround;
            RenderSettings.sun = _originalSunSetting;
            if (_sun != null)
            {
                _sun.transform.rotation = _originalSunRotation;
                _sun.intensity = _originalSunIntensity;
                _sun.enabled = _originalSunEnabled;
            }

            _ambientClaim?.Dispose();
            _ambientClaim = null;
        }

        private void Capture(Light sun)
        {
            if (_captured)
            {
                return;
            }

            _captured = true;
            _originalSunSetting = RenderSettings.sun;
            _originalAmbientMode = RenderSettings.ambientMode;
            _originalAmbientSky = RenderSettings.ambientSkyColor;
            _originalAmbientEquator = RenderSettings.ambientEquatorColor;
            _originalAmbientGround = RenderSettings.ambientGroundColor;
            if (sun != null)
            {
                _originalSunRotation = sun.transform.rotation;
                _originalSunIntensity = sun.intensity;
                _originalSunEnabled = sun.enabled;
            }
        }

        private Light ResolveSun()
        {
            if (_sunResolved)
            {
                return _sun;
            }

            _sunResolved = true;
            _sun = RenderSettings.sun;
            if (_sun != null)
            {
                return _sun;
            }

            // WHY one search at the first apply and never again: a scene's directional light is usually
            // not assigned to RenderSettings.sun (Unity then picks it for the sky itself), and the sink is
            // built before scripts run, so there is no earlier point to be handed it explicitly.
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                {
                    _sun = light;
                    break;
                }
            }

            return _sun;
        }

        private static Color ToColor(RbxColor3 color)
        {
            return new Color(color.R, color.G, color.B, 1f);
        }
    }
}
