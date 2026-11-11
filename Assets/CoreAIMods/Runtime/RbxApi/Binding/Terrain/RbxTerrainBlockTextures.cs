using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// The Unity textures of the painted block tiles (<see cref="TerrainBlockPainter"/>): colour (sRGB), normal and
    /// roughness, each enlarged <see cref="Upscale"/> times texel by texel and mip-mapped, built once per style and
    /// shared by every renderer in the process.
    /// </summary>
    /// <remarks>
    /// WHY enlarged with hard texel edges and then filtered: a 16-texel tile filtered as it is blurs every texel into
    /// its neighbours up close, and point filtering shimmers far away. Copying each texel into an 8 x 8 block keeps
    /// the edges crisp at a few studs while trilinear filtering and the mip chain (whose fourth level is exactly
    /// the painted tile) keep distant blocks calm. 21 styles cost about 4 MB of texture memory.
    /// </remarks>
    public static class RbxTerrainBlockTextures
    {
        /// <summary>Texels each painted texel becomes along each axis.</summary>
        public const int Upscale = 8;

        private static readonly Dictionary<TerrainBlockStyle, Set> Sets = new();

        /// <summary>Total texture builds since the process started (a test counter: each style builds once).</summary>
        public static int BuildCount { get; private set; }

        /// <summary>
        /// The textures of <paramref name="style"/>; false for <see cref="TerrainBlockStyle.Photo"/>. Textures a
        /// scene teardown destroyed are rebuilt.
        /// </summary>
        public static bool TryGet(TerrainBlockStyle style, out Texture2D albedo, out Texture2D normal,
            out Texture2D roughness)
        {
            albedo = null;
            normal = null;
            roughness = null;
            if (style == TerrainBlockStyle.Photo)
            {
                return false;
            }

            if (!Sets.TryGetValue(style, out Set set) || set.Albedo == null || set.Normal == null
                || set.Roughness == null)
            {
                TerrainBlockTile tile = TerrainBlockPainter.Get(style);
                set = new Set(
                    Build(tile, tile.Albedo, 4, TextureFormat.RGBA32, false, style + " colour"),
                    Build(tile, tile.Normal, 4, TextureFormat.RGBA32, true, style + " normal"),
                    Build(tile, tile.Roughness, 1, TextureFormat.R8, true, style + " roughness"));
                Sets[style] = set;
                BuildCount++;
            }

            albedo = set.Albedo;
            normal = set.Normal;
            roughness = set.Roughness;
            return true;
        }

        /// <summary>
        /// Builds the textures of every style <paramref name="look"/> paints, so a host can pay for them behind its
        /// loading screen instead of in the first frames that draw each material; returns the styles built now.
        /// Builds nothing without a graphics device (a headless or dedicated-server host draws no Terrain).
        /// </summary>
        /// <remarks>
        /// Cost on a desktop: painting and uploading 21 styles, a few milliseconds each on the main thread, about 4 MB
        /// of texture memory; a second call costs one dictionary lookup per material.
        /// </remarks>
        public static int Preload(TerrainBlockLook look)
        {
            if (look == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return 0;
            }

            int before = BuildCount;
            for (byte slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                TryGet(look.StyleOf(slot), out Texture2D _, out Texture2D _, out Texture2D _);
            }

            return BuildCount - before;
        }

        private static Texture2D Build(TerrainBlockTile tile, byte[] source, int channels, TextureFormat format,
            bool linear, string name)
        {
            int size = tile.Size * Upscale;
            byte[] pixels = new byte[size * size * channels];
            for (int y = 0; y < size; y++)
            {
                int sourceRow = y / Upscale * tile.Size;
                for (int x = 0; x < size; x++)
                {
                    int from = (sourceRow + x / Upscale) * channels;
                    int to = (y * size + x) * channels;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        pixels[to + channel] = source[from + channel];
                    }
                }
            }

            Texture2D texture = new(size, size, format, true, linear)
            {
                name = "CoreAI Terrain block " + name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };
            texture.SetPixelData(pixels, 0);
            texture.Apply(true, true);
            return texture;
        }

        private readonly struct Set
        {
            public Set(Texture2D albedo, Texture2D normal, Texture2D roughness)
            {
                Albedo = albedo;
                Normal = normal;
                Roughness = roughness;
            }

            public Texture2D Albedo { get; }

            public Texture2D Normal { get; }

            public Texture2D Roughness { get; }
        }
    }
}
