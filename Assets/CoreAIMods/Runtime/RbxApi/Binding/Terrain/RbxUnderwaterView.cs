using System;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// The look while the camera's eye is in a Terrain water cell: a blue-green tint through dense fog, as bright as the
    /// light that reaches the water there. The fog is claimed from the look (<see cref="LookAmbientOwnership.ClaimFog"/>)
    /// and put back as it was on leaving the water, including any fog a world wrote while the eye was under (the
    /// day-night cycle writes it every 0.1 s).
    /// </summary>
    /// <remarks>
    /// WHY fog and not a screen overlay: fog tints every surface by its distance, as water does, needs no extra pass
    /// on WebGL, and the horizon dome already paints the fog colour where nothing is drawn.
    /// WHY the fog follows the light: it is drawn unlit, so a fixed colour made a flooded cave glow teal at midnight as
    /// brightly as open water at noon.
    /// </remarks>
    public sealed class RbxUnderwaterView : IDisposable
    {
        /// <summary>The name the underwater view claims the fog under.</summary>
        public const string FogOwnerName = "CoreAI.Mods underwater";

        /// <summary>How far one sees under water before the fog is whole, in studs (seven blocks).</summary>
        public const float FogEndStuds = 28f;

        /// <summary>
        /// The light on an open water surface, as linear luminance of the sky's ambient plus the sun on level ground,
        /// that counts as full daylight: <see cref="FogColor"/> is the water seen in it.
        /// </summary>
        public const float DaylightLuminance = 1f;

        /// <summary>
        /// The colour of the water seen from inside it in full daylight, as written to RenderSettings; the fog takes it
        /// with its light scaled by <see cref="LightLevel"/> (<see cref="FogColorAt"/>).
        /// </summary>
        public static readonly Color FogColor = new(0.05f, 0.22f, 0.3f, 1f);

        private static readonly int OccludedAmbientId = Shader.PropertyToID(UnityRbxLightingSink.OccludedAmbientProperty);
        private static readonly Vector3[] Upward = { Vector3.up };
        private readonly Color[] _probeResult = new Color[1];

        private IDisposable _claim;
        private bool _savedFog;
        private FogMode _savedMode;
        private Color _savedColor;
        private float _savedStart;
        private float _savedEnd;
        private float _savedDensity;
        private Color _writtenColor;
        private float _writtenEnd;

        /// <summary>True while the eye is under water and the view holds the fog.</summary>
        public bool IsUnderwater => _claim != null;

        /// <summary>The share of full daylight on the water around the eye at the last update under water (1 before).</summary>
        public float LightLevel { get; private set; } = 1f;

        /// <summary>
        /// The fog colour for <paramref name="lightLevel"/>, the share of full daylight on the water: <see cref="FogColor"/>
        /// with its light scaled in linear space (RenderSettings colours are gamma).
        /// </summary>
        public static Color FogColorAt(float lightLevel)
        {
            if (lightLevel >= 1f)
            {
                return FogColor;
            }

            Color light = FogColor.linear * Mathf.Max(0f, lightLevel);
            light.a = 1f;
            return light.gamma;
        }

        /// <summary>
        /// The share of full daylight (<see cref="DaylightLuminance"/>) on water whose cell sees
        /// <paramref name="skyExposure"/> of the sky (0 deep in rock, 1 under the open sky): the sky's ambient and the
        /// sun where the sky is seen, the occluded ambient (Roblox's <c>Lighting.Ambient</c>) where it is not. All
        /// colours are linear; <paramref name="sunOnLevelGround"/> is the sun's intensity times its colour's luminance
        /// times the sine of its height.
        /// </summary>
        public static float LightLevelAt(float skyExposure, Color skyAmbient, Color occludedAmbient, float sunOnLevelGround)
        {
            float open = Luminance(skyAmbient) + Mathf.Max(0f, sunOnLevelGround);
            float closed = Luminance(occludedAmbient);
            return Mathf.Clamp01(Mathf.Lerp(closed, open, Mathf.Clamp01(skyExposure)) / DaylightLuminance);
        }

        /// <summary>Looks at where the eye is and enters or leaves the underwater look; call once a frame.</summary>
        /// <param name="eye">The camera's position, or null with no camera.</param>
        /// <param name="water">Which cells hold water.</param>
        /// <param name="skyExposure">How much of the sky the space at a cell sees (0..1); null takes the open sky.</param>
        public void Update(Vector3? eye, IRbxTerrainWaterCells water, Func<int, int, int, float> skyExposure = null)
        {
            if (eye.HasValue && water != null && TryWaterCell(eye.Value, water, out int x, out int y, out int z))
            {
                LightLevel = LightLevelAt(skyExposure?.Invoke(x, y, z) ?? 1f, SkyAmbient(),
                    Shader.GetGlobalColor(OccludedAmbientId), SunOnLevelGround());
                Enter();
            }
            else
            {
                Leave();
            }
        }

        /// <summary>Gives the fog back as it was.</summary>
        public void Dispose()
        {
            Leave();
        }

        private static bool TryWaterCell(Vector3 eye, IRbxTerrainWaterCells water, out int x, out int y, out int z)
        {
            RbxVector3 studs = RbxSpace.FromUnity(eye);
            double cell = TerrainBoxRasterizer.CellStuds;
            x = (int)Math.Floor(studs.X / cell);
            y = (int)Math.Floor(studs.Y / cell);
            z = (int)Math.Floor(studs.Z / cell);
            return water.IsWaterCell(x, y, z);
        }

        private static float Luminance(Color linear)
        {
            return 0.2126f * linear.r + 0.7152f * linear.g + 0.0722f * linear.b;
        }

        /// <summary>The ambient light from the sky above, linear.</summary>
        /// <remarks>
        /// WHY two sources: a world's lighting sink sets a trilight (its sky colour is gamma), while a scene left on the
        /// skybox lights through the ambient probe.
        /// </remarks>
        private Color SkyAmbient()
        {
            if (RenderSettings.ambientMode != AmbientMode.Skybox)
            {
                return RenderSettings.ambientSkyColor.linear;
            }

            RenderSettings.ambientProbe.Evaluate(Upward, _probeResult);
            return _probeResult[0];
        }

        private static float SunOnLevelGround()
        {
            Light sun = RenderSettings.sun;
            if (sun == null || !sun.isActiveAndEnabled)
            {
                return 0f;
            }

            float height = Mathf.Max(0f, -sun.transform.forward.y);
            return sun.intensity * Luminance(sun.color.linear) * height;
        }

        private void Enter()
        {
            if (_claim == null)
            {
                Save();
                _claim = LookAmbientOwnership.Shared.ClaimFog(FogOwnerName);
            }
            else if (RenderSettings.fogColor != _writtenColor || !Mathf.Approximately(RenderSettings.fogEndDistance, _writtenEnd))
            {
                // WHY: someone wrote the fog while the eye was under (a world's Lighting fog); that is what returns.
                Save();
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = FogColorAt(LightLevel);
            RenderSettings.fogStartDistance = 0f;
            RenderSettings.fogEndDistance = RbxSpace.LengthToUnity(FogEndStuds);
            _writtenColor = RenderSettings.fogColor;
            _writtenEnd = RenderSettings.fogEndDistance;
        }

        private void Leave()
        {
            if (_claim == null)
            {
                return;
            }

            RenderSettings.fog = _savedFog;
            RenderSettings.fogMode = _savedMode;
            RenderSettings.fogColor = _savedColor;
            RenderSettings.fogStartDistance = _savedStart;
            RenderSettings.fogEndDistance = _savedEnd;
            RenderSettings.fogDensity = _savedDensity;
            IDisposable claim = _claim;
            _claim = null;
            claim.Dispose();
        }

        private void Save()
        {
            _savedFog = RenderSettings.fog;
            _savedMode = RenderSettings.fogMode;
            _savedColor = RenderSettings.fogColor;
            _savedStart = RenderSettings.fogStartDistance;
            _savedEnd = RenderSettings.fogEndDistance;
            _savedDensity = RenderSettings.fogDensity;
        }
    }
}
