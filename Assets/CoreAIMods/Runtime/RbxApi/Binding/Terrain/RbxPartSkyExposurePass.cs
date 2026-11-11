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
    /// Parts do not shade the Terrain or each other: only Terrain cells block the sky. A part written darker
    /// than the open sky is also checked on every call: once its centre is in another cell it is written again
    /// at once, outside its turn, so a character back from a cave or from under the ground is not drawn
    /// black in the open until the sweep reaches it.
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
        private readonly Dictionary<Renderer, long> _dimmedAt = new();
        private readonly List<Renderer> _moved = new();
        private MaterialPropertyBlock _block;
        private int _cursor;
        private long _cachedRevision = -1;
        private TerrainSkyExposure _cachedFor;

        /// <summary>Renderers written since construction (a test and diagnostics counter).</summary>
        public long WriteCount { get; private set; }

        /// <summary>
        /// Renderers applied outside their round-robin turn because they were written darker than the open sky
        /// and have since moved to another cell (a test and diagnostics counter).
        /// </summary>
        public long OutOfTurnCount { get; private set; }

        /// <summary>
        /// Re-applies every dimmed renderer that has moved to another cell, then visits up to
        /// <paramref name="budget"/> renderers in turn. When a sweep ends, <paramref name="collect"/> fills the
        /// next sweep's list. Returns the renderers visited, both kinds.
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

            // WHY outside the budget: the budget paces the round-robin; a moved dimmed part costs one cell lookup.
            int moved = ReapplyMoved(exposure);
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

            return visited + moved;
        }

        /// <summary>Samples and writes one renderer now (a part that just moved, a test).</summary>
        public void Apply(Renderer renderer, TerrainSkyExposure exposure)
        {
            if (renderer == null || exposure == null)
            {
                return;
            }

            long key = CellKey(renderer, out int x, out int y, out int z);
            if (!_cellExposure.TryGetValue(key, out byte value))
            {
                value = (byte)Math.Round(exposure.SampleExposure(x, y, z) * TerrainSkyExposure.FullExposure);
                _cellExposure.Add(key, value);
            }

            if (value < TerrainSkyExposure.FullExposure)
            {
                _dimmedAt[renderer] = key;
            }
            else
            {
                _dimmedAt.Remove(renderer);
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

        /// <summary>Applies, outside their turn, the dimmed renderers whose centre left the cell they were written in.</summary>
        private int ReapplyMoved(TerrainSkyExposure exposure)
        {
            if (_dimmedAt.Count == 0)
            {
                return 0;
            }

            _moved.Clear();
            foreach (KeyValuePair<Renderer, long> dimmed in _dimmedAt)
            {
                if (dimmed.Key == null || CellKey(dimmed.Key, out int _, out int _, out int _) != dimmed.Value)
                {
                    _moved.Add(dimmed.Key);
                }
            }

            int applied = 0;
            foreach (Renderer renderer in _moved)
            {
                if (renderer == null)
                {
                    _dimmedAt.Remove(renderer);
                    continue;
                }

                Apply(renderer, exposure);
                OutOfTurnCount++;
                applied++;
            }

            _moved.Clear();
            return applied;
        }

        private static long CellKey(Renderer renderer, out int x, out int y, out int z)
        {
            RbxVector3 centre = RbxSpace.FromUnity(renderer.bounds.center);
            double cell = TerrainBoxRasterizer.CellStuds;
            x = (int)Math.Floor(centre.X / cell);
            y = (int)Math.Floor(centre.Y / cell);
            z = (int)Math.Floor(centre.Z / cell);
            return (((long)(x + KeyBias) & KeyMask) << 42) | (((long)(y + KeyBias) & KeyMask) << 21)
                                                         | ((long)(z + KeyBias) & KeyMask);
        }
    }
}
