using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// Gives each Part renderer the sky exposure of the Terrain cell at its centre, so a part or a
    /// character in a cave is lit by the cave and not by the noon sky. One value per renderer, written to
    /// the <c>_RbxSkyExposure</c> material property through the renderer's property block; the Rbx
    /// materials scale their sky ambient and sky reflections by it, as the Terrain does per vertex.
    /// </summary>
    /// <remarks>
    /// WHY one value per renderer and not per pixel: a part is small next to the four-stud cell the light
    /// is known at, and every bound part already carries a property block, so the value costs no
    /// batching. The pass walks the renderers round-robin under a per-call budget and caches the exposure
    /// of every cell it samples until the Terrain changes, so parts that do not move cost one dictionary
    /// lookup per sweep. A part keeps exposure 1 (nothing written) while it stands under the open sky.
    /// Parts do not shade the Terrain or each other: only Terrain cells block the sky.
    /// </remarks>
    public sealed class RbxPartSkyExposurePass
    {
        /// <summary>The per-renderer exposure the Rbx shaders read (0..1; 1 when absent).</summary>
        public const string SkyExposureProperty = "_RbxSkyExposure";

        private const int KeyBias = 1 << 20;
        private const long KeyMask = (1L << 21) - 1;

        private static readonly int SkyExposureId = Shader.PropertyToID(SkyExposureProperty);

        private readonly List<Renderer> _renderers = new();
        private readonly Dictionary<long, byte> _cellExposure = new();
        private MaterialPropertyBlock _block;
        private int _cursor;
        private long _cachedRevision = -1;
        private TerrainSkyExposure _cachedFor;

        /// <summary>Renderers written since construction (a test and diagnostics counter).</summary>
        public long WriteCount { get; private set; }

        /// <summary>
        /// Visits up to <paramref name="budget"/> renderers. When a sweep ends, <paramref name="collect"/>
        /// fills the next sweep's list. Returns the renderers visited.
        /// </summary>
        public int Tick(TerrainSkyExposure exposure, long terrainRevision, Action<List<Renderer>> collect, int budget)
        {
            if (exposure == null || collect == null || budget <= 0)
            {
                return 0;
            }

            if (!ReferenceEquals(exposure, _cachedFor) || terrainRevision != _cachedRevision)
            {
                _cellExposure.Clear();
                _cachedFor = exposure;
                _cachedRevision = terrainRevision;
            }

            if (_cursor >= _renderers.Count)
            {
                _renderers.Clear();
                _cursor = 0;
                collect(_renderers);
            }

            int visited = 0;
            while (_cursor < _renderers.Count && visited < budget)
            {
                Renderer renderer = _renderers[_cursor++];
                if (renderer == null)
                {
                    continue;
                }

                Apply(renderer, exposure);
                visited++;
            }

            return visited;
        }

        /// <summary>Samples and writes one renderer now (a part that just moved, a test).</summary>
        public void Apply(Renderer renderer, TerrainSkyExposure exposure)
        {
            if (renderer == null || exposure == null)
            {
                return;
            }

            RbxVector3 centre = RbxSpace.FromUnity(renderer.bounds.center);
            double cell = TerrainBoxRasterizer.CellStuds;
            int x = (int)Math.Floor(centre.X / cell);
            int y = (int)Math.Floor(centre.Y / cell);
            int z = (int)Math.Floor(centre.Z / cell);
            long key = (((long)(x + KeyBias) & KeyMask) << 42) | (((long)(y + KeyBias) & KeyMask) << 21)
                                                                | ((long)(z + KeyBias) & KeyMask);
            if (!_cellExposure.TryGetValue(key, out byte value))
            {
                value = (byte)Math.Round(exposure.SampleExposure(x, y, z) * TerrainSkyExposure.FullExposure);
                _cellExposure.Add(key, value);
            }

            float target = value / (float)TerrainSkyExposure.FullExposure;
            // WHY created here: a property block is a Unity object, and this pass is built in a component's
            // field initializer, where Unity refuses to create one.
            _block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            bool written = _block.HasFloat(SkyExposureId);
            if (!written && value == TerrainSkyExposure.FullExposure)
            {
                return;
            }

            if (written && Math.Abs(_block.GetFloat(SkyExposureId) - target) < 0.5f / TerrainSkyExposure.FullExposure)
            {
                return;
            }

            _block.SetFloat(SkyExposureId, target);
            renderer.SetPropertyBlock(_block);
            WriteCount++;
        }
    }
}
