using System;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// The look while the camera's eye is in a Terrain water cell: a blue-green tint through dense fog. The fog is
    /// claimed from the look (<see cref="LookAmbientOwnership.ClaimFog"/>) and put back as it was on leaving the
    /// water, including any fog a world wrote while the eye was under (the day-night cycle writes it every 0.1 s).
    /// </summary>
    /// <remarks>
    /// WHY fog and not a screen overlay: fog tints every surface by its distance, as water does, needs no extra pass
    /// on WebGL, and the horizon dome already paints the fog colour where nothing is drawn.
    /// </remarks>
    public sealed class RbxUnderwaterView : IDisposable
    {
        /// <summary>The name the underwater view claims the fog under.</summary>
        public const string FogOwnerName = "CoreAI.Mods underwater";

        /// <summary>How far one sees under water before the fog is whole, in studs (seven blocks).</summary>
        public const float FogEndStuds = 28f;

        /// <summary>The colour of the water seen from inside it (linear fog colour as written to RenderSettings).</summary>
        public static readonly Color FogColor = new(0.05f, 0.22f, 0.3f, 1f);

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

        /// <summary>Looks at where the eye is and enters or leaves the underwater look; call once a frame.</summary>
        public void Update(Vector3? eye, IRbxTerrainWaterCells water)
        {
            if (eye.HasValue && water != null && InWater(eye.Value, water))
            {
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

        private static bool InWater(Vector3 eye, IRbxTerrainWaterCells water)
        {
            RbxVector3 studs = RbxSpace.FromUnity(eye);
            double cell = TerrainBoxRasterizer.CellStuds;
            return water.IsWaterCell((int)Math.Floor(studs.X / cell), (int)Math.Floor(studs.Y / cell),
                (int)Math.Floor(studs.Z / cell));
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
            RenderSettings.fogColor = FogColor;
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
