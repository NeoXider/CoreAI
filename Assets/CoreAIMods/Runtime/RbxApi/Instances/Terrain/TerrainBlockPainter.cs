using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// How a Terrain material looks as a block when <see cref="TerrainBlockLook"/> draws it with a painted tile;
    /// <see cref="Photo"/> keeps the material catalog's photographed set.
    /// </summary>
    public enum TerrainBlockStyle : byte
    {
        /// <summary>The material catalog's own surface, as a Part shows it.</summary>
        Photo = 0,
        Grass,
        Leaves,
        Earth,
        Mud,
        Stone,
        Cobblestone,
        Slate,
        Basalt,
        CrackedLava,
        Sand,
        Sandstone,
        Snow,
        Ice,
        Glacier,
        Salt,
        Limestone,
        Brick,
        Concrete,
        Pavement,
        Asphalt,
        Planks
    }

    /// <summary>
    /// One painted block face: <see cref="TerrainBlockPainter.TileSize"/> square, rows bottom first (row 0 is the
    /// bottom edge of the face, the way a texture is uploaded), tileable on both axes.
    /// </summary>
    public sealed class TerrainBlockTile
    {
        internal TerrainBlockTile(TerrainBlockStyle style, int size, byte[] albedo, byte[] normal, byte[] roughness)
        {
            Style = style;
            Size = size;
            Albedo = albedo;
            Normal = normal;
            Roughness = roughness;
        }

        /// <summary>The style this tile was painted for.</summary>
        public TerrainBlockStyle Style { get; }

        /// <summary>Texels along each edge.</summary>
        public int Size { get; }

        /// <summary>Colour as sRGB RGBA, four bytes per texel, alpha 255.</summary>
        public byte[] Albedo { get; }

        /// <summary>
        /// Tangent-space normal as RGBA (x and y in R and G as n * 0.5 + 0.5, z in B, alpha 255); +X runs along
        /// the row, +Y up the face (the OpenGL convention).
        /// </summary>
        public byte[] Normal { get; }

        /// <summary>Roughness 0..255 per texel (linear).</summary>
        public byte[] Roughness { get; }
    }

    /// <summary>
    /// Paints the block tiles of <see cref="TerrainBlockStyle"/> in code: periodic value noise and wrapped Worley
    /// cells give the structure, rank equalisation and short hand-picked palettes give crisp pixel art, and a height
    /// field per tile bakes a top-left key light into the colours and becomes the normal map. Engine-free and
    /// deterministic: a style always paints the same texels, so every client and every test sees the same block.
    /// </summary>
    /// <remarks>
    /// WHY painted tiles and not photographs for blocks: a photographed set laid across a 4-stud face shows either a
    /// fragment of the picture (world mapping) or a visible repeat grid (one copy per face); a 16-texel tile drawn
    /// for exactly one face reads as one block, crisp up close and clean far away, and needs no asset or licence.
    /// The technique follows the code-painted blocks of Isleplay (MIT, Copyright (c) 2026 Patrick Blais); the code,
    /// palettes and seeds here are CoreAI's own.
    /// </remarks>
    public static class TerrainBlockPainter
    {
        /// <summary>Texels along each edge of a tile.</summary>
        public const int TileSize = 16;

        private const int S = TileSize;
        private const int P = S * S;

        private static readonly object Gate = new();
        private static readonly Dictionary<TerrainBlockStyle, TerrainBlockTile> Painted = new();

        /// <summary>
        /// The tile of <paramref name="style"/>, painted once per process and shared; null for
        /// <see cref="TerrainBlockStyle.Photo"/>.
        /// </summary>
        public static TerrainBlockTile Get(TerrainBlockStyle style)
        {
            if (style == TerrainBlockStyle.Photo)
            {
                return null;
            }

            lock (Gate)
            {
                if (!Painted.TryGetValue(style, out TerrainBlockTile tile))
                {
                    tile = Paint(style);
                    Painted.Add(style, tile);
                }

                return tile;
            }
        }

        /// <summary>Paints a fresh tile of <paramref name="style"/> (no cache); null for <see cref="TerrainBlockStyle.Photo"/>.</summary>
        public static TerrainBlockTile Paint(TerrainBlockStyle style)
        {
            Canvas canvas = style switch
            {
                TerrainBlockStyle.Photo => null,
                TerrainBlockStyle.Grass => GrassTop(),
                TerrainBlockStyle.Leaves => Leaves(),
                TerrainBlockStyle.Earth => Earth(EarthPalette, 0.92f, 31),
                TerrainBlockStyle.Mud => Mud(),
                TerrainBlockStyle.Stone => Stone(),
                TerrainBlockStyle.Cobblestone => Cobblestone(),
                TerrainBlockStyle.Slate => Slate(),
                TerrainBlockStyle.Basalt => Basalt(),
                TerrainBlockStyle.CrackedLava => CrackedLava(),
                TerrainBlockStyle.Sand => Grain(SandPalette, 0.25f, 0.9f, 0.35f, 61),
                TerrainBlockStyle.Sandstone => Sandstone(),
                TerrainBlockStyle.Snow => Grain(SnowPalette, 0.45f, 0.78f, 0.25f, 71),
                TerrainBlockStyle.Ice => Ice(IcePalette, 0.22f, 81, 3),
                TerrainBlockStyle.Glacier => Ice(GlacierPalette, 0.3f, 91, 2),
                TerrainBlockStyle.Salt => Salt(),
                TerrainBlockStyle.Limestone => Limestone(),
                TerrainBlockStyle.Brick => Brick(),
                TerrainBlockStyle.Concrete => Grain(ConcretePalette, 0.55f, 0.88f, 0.2f, 131),
                TerrainBlockStyle.Pavement => Pavement(),
                TerrainBlockStyle.Asphalt => Asphalt(),
                TerrainBlockStyle.Planks => Planks(),
                _ => throw new ArgumentOutOfRangeException(nameof(style), style, "unknown block style")
            };
            return canvas?.Finish(style);
        }

        // ---- palettes (sRGB, dark to light) ---------------------------------------------------------------

        private static readonly uint[] GrassPalette =
            { 0x3A6B1D, 0x447C22, 0x4F8D28, 0x5A9D2E, 0x66AC35, 0x74BA3E, 0x86C84B };

        private static readonly uint[] LeafPalette =
            { 0x1E4414, 0x285619, 0x33681F, 0x3F7B26, 0x4C8E2E, 0x5BA138, 0x6DB345 };

        private const uint LeafGap = 0x112A0B;

        private static readonly uint[] EarthPalette = { 0x553923, 0x62422A, 0x704C32, 0x7E573A, 0x8B6243, 0x986E4D };

        private static readonly uint[] MudPalette = { 0x37281D, 0x423023, 0x4D392A, 0x594331, 0x654D39 };

        private static readonly uint[] SandPalette = { 0xCCB079, 0xD5BB85, 0xDDC591, 0xE4CE9C, 0xEAD6A8, 0xF0DFB5 };

        private static readonly uint[] StonePalette =
            { 0x5A5B5F, 0x646569, 0x6E6F73, 0x78797D, 0x838488, 0x8E8F93, 0x9A9B9F };

        private static readonly uint[] CobblePalette =
            { 0x515256, 0x5D5E62, 0x6A6B6F, 0x77787C, 0x85868A, 0x939498, 0xA3A4A8 };

        private static readonly uint[] MortarPalette = { 0x323336, 0x3D3E41, 0x48494C };

        private static readonly uint[] SlatePalette = { 0x30363F, 0x39404A, 0x434A55, 0x4D5560, 0x58606B, 0x646C77 };

        private static readonly uint[] BasaltPalette = { 0x232428, 0x2C2D32, 0x36373C, 0x404247, 0x4B4D53, 0x575A60 };

        private static readonly uint[] CrustPalette = { 0x1E130F, 0x281913, 0x332018, 0x3F281E, 0x4B3125 };

        private static readonly uint[] GlowPalette = { 0x7E1E07, 0xB43A0B, 0xDE5E16, 0xF48E26, 0xFFC44E, 0xFFE58C };

        private static readonly uint[] SandstonePalette = { 0xB2834F, 0xBD8E59, 0xC89963, 0xD2A46E, 0xDBAF7A, 0xE3BA87 };

        private static readonly uint[] SnowPalette = { 0xD3DCE6, 0xDCE4EC, 0xE4EAF1, 0xECF0F6, 0xF3F6FA, 0xF9FBFD };

        private static readonly uint[] IcePalette = { 0x7BADDE, 0x89B8E6, 0x98C3EC, 0xA8CEF1, 0xB9D9F5, 0xCDE5F9 };

        private static readonly uint[] GlacierPalette = { 0x4A80BA, 0x568DC6, 0x649AD0, 0x73A8DA, 0x84B5E3, 0x98C4EC };

        private static readonly uint[] SaltPalette = { 0xC6BEBD, 0xD0C9C8, 0xDAD4D3, 0xE3DEDD, 0xECE8E7, 0xF5F2F1 };

        private static readonly uint[] LimestonePalette = { 0xA39B8B, 0xAEA696, 0xB9B1A1, 0xC4BDAD, 0xCFC8B9, 0xD9D3C5 };

        private static readonly uint[] BrickPalette = { 0x76301F, 0x853826, 0x93412D, 0xA04B36, 0xAC5640, 0xB8624B };

        private static readonly uint[] BrickMortarPalette = { 0x958E86, 0xA8A199, 0xB9B3AB };

        private static readonly uint[] ConcretePalette = { 0x878785, 0x8F8F8D, 0x969695, 0x9E9E9C, 0xA5A5A4 };

        private static readonly uint[] PavementPalette = { 0x7B7872, 0x86837D, 0x918E88, 0x9C9993, 0xA7A49E };

        private static readonly uint[] AsphaltPalette = { 0x292A2C, 0x303133, 0x37383B, 0x3F4043, 0x48494C };

        private static readonly uint[] PlankPalette = { 0x7A542F, 0x886136, 0x966D3E, 0xA37946, 0xAF854F, 0xBB9159 };

        private const uint PlankSeam = 0x4A321E;

        // ---- styles ---------------------------------------------------------------------------------------

        private static Canvas GrassTop()
        {
            Canvas canvas = new(0.88f, 0.9f);
            float[] field = Field((x, y) => 0.32f * Fbm(x, y, 4, 2, 11) + 0.2f * Noise(x, y, 8, 8, 12)
                                            + 0.48f * Rnd(x, y, 13));
            float[] ranks = Equalize(field);
            int[] levels = new int[P];
            for (int i = 0; i < P; i++)
            {
                levels[i] = Quantize(ranks[i], 0.07f, 0.22f, 0.42f, 0.63f, 0.81f, 0.94f);
            }

            Rng rng = new(14);
            for (int blade = 0; blade < 11; blade++)
            {
                int x = rng.Range(0, S - 1);
                int y = rng.Range(0, S - 1);
                levels[Index(x, y)] = 6;
                levels[Index(x, y + 1)] = rng.Range(0, 1);
            }

            for (int i = 0; i < P; i++)
            {
                canvas.Set(i, GrassPalette, levels[i], levels[i] / 6f);
            }

            return canvas;
        }

        private static Canvas Leaves()
        {
            Canvas canvas = new(1.3f, 0.72f);
            Rng rng = new(21);
            Site[] sites = Scatter(ref rng, 20, 2.7f, 1.5f, 2.2f);
            float[] cover = new float[P];
            float[] shade = new float[P];
            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                Nearest near = Worley(sites, x + 0.5f, y + 0.5f);
                float weight = sites[near.Id].Weight;
                cover[i] = 1f - near.F1 / weight + 0.35f * (Rnd(x, y, 22) - 0.5f);
                // WHY up-left brighter: a leaf catches the light on its upper side and its tip, the rim towards the
                // cluster below sits in the shadow of the leaf above it.
                shade[i] = -0.6f * (near.Dx + near.Dy) / weight - 0.3f * near.F1 / weight
                           + 0.3f * (Rnd(x, y, 23) - 0.5f) - (near.F2 - near.F1 < 0.15f ? 0.35f : 0f);
            }

            float[] coverRanks = Equalize(cover);
            float[] shadeRanks = Equalize(shade);
            for (int i = 0; i < P; i++)
            {
                if (coverRanks[i] < 0.2f)
                {
                    // WHY dark gaps instead of holes: the leaves are an opaque block, so the depth between the
                    // clusters is drawn as a deep shadow rather than cut out.
                    canvas.SetColor(i, LeafGap, 0f, 0.9f);
                    continue;
                }

                int level = Quantize(shadeRanks[i], 0.1f, 0.27f, 0.48f, 0.68f, 0.85f, 0.95f);
                canvas.Set(i, LeafPalette, level, 0.45f + 0.55f * level / 6f);
            }

            return canvas;
        }

        private static Canvas Earth(uint[] palette, float roughness, int seed)
        {
            Canvas canvas = new(0.9f, roughness);
            float[] field = Field((x, y) => 0.36f * Fbm(x, y, 4, 2, seed) + 0.24f * Noise(x, y, 8, 8, seed + 1)
                                            + 0.4f * Rnd(x, y, seed + 2));
            float[] ranks = Equalize(field);
            for (int i = 0; i < P; i++)
            {
                int level = Quantize(ranks[i], 0.06f, 0.21f, 0.46f, 0.73f, 0.92f);
                canvas.Set(i, palette, level, 0.4f * level / 5f);
            }

            Rng rng = new(seed + 3);
            for (int pebble = 0; pebble < 4; pebble++)
            {
                int x = rng.Range(0, S - 1);
                int y = rng.Range(0, S - 1);
                canvas.SetColor(Index(x, y), 0x9A8C7C, 0.95f, 0.75f);
                if (rng.Chance(0.5f))
                {
                    canvas.SetColor(Index(x + 1, y), 0x85776A, 0.85f, 0.75f);
                    canvas.Set(Index(x + 1, y + 1), palette, 0, 0.1f);
                }
                else
                {
                    canvas.Set(Index(x, y + 1), palette, 0, 0.1f);
                }
            }

            for (int speck = 0; speck < 5; speck++)
            {
                canvas.Set(Index(rng.Range(0, S - 1), rng.Range(0, S - 1)), palette, 0, 0f);
            }

            return canvas;
        }

        private static Canvas Mud()
        {
            Canvas canvas = Earth(MudPalette, 0.55f, 41);
            // WHY: wet mud shines in its puddles; the lowest texels get the glossiest roughness.
            for (int i = 0; i < P; i++)
            {
                if (canvas.Height[i] < 0.1f)
                {
                    canvas.Rough[i] = 0.32f;
                }
            }

            return canvas;
        }

        private static Canvas Stone()
        {
            Canvas canvas = new(1.1f, 0.82f);
            float[] field = Field((x, y) => 0.38f * Fbm(x, y, 4, 2, 51) + 0.3f * Noise(x, y, 2, 8, 52)
                                            + 0.32f * Rnd(x, y, 53));
            float[] ranks = Equalize(field);
            int[] levels = new int[P];
            for (int i = 0; i < P; i++)
            {
                levels[i] = 1 + Quantize(ranks[i], 0.1f, 0.35f, 0.68f, 0.9f, 0.98f);
            }

            Rng rng = new(54);
            for (int crack = 0; crack < 3; crack++)
            {
                int x = rng.Range(0, S - 1);
                int y = rng.Range(0, S - 1);
                int step = rng.Chance(0.5f) ? 1 : -1;
                int length = rng.Range(3, 5);
                for (int k = 0; k < length; k++)
                {
                    levels[Index(x, y)] = 0;
                    x += step;
                    if (rng.Chance(0.4f))
                    {
                        y += 1;
                    }
                }
            }

            for (int i = 0; i < P; i++)
            {
                canvas.Set(i, StonePalette, levels[i], levels[i] / 6f);
            }

            return canvas;
        }

        private static Canvas Cobblestone()
        {
            Canvas canvas = new(2.2f, 0.86f);
            Rng rng = new(61);
            Site[] sites = Scatter(ref rng, 11, 3.9f, 0.85f, 1.2f);
            float[] tone = new float[sites.Length];
            for (int index = 0; index < sites.Length; index++)
            {
                tone[index] = rng.Next() * 2f - 1f;
            }

            float[] height = new float[P];
            float[] edges = new float[P];
            int[] owner = new int[P];
            for (int i = 0; i < P; i++)
            {
                Nearest near = Worley(sites, i % S + 0.5f, i / S + 0.5f);
                float edge = near.F2 - near.F1;
                edges[i] = edge;
                owner[i] = near.Id;
                height[i] = edge < 0.85f ? 0f : 0.35f + 0.65f * (float)Math.Sqrt(Clamp01((edge - 0.85f) / 3f));
            }

            float[] light = KeyLight(height);
            for (int i = 0; i < P; i++)
            {
                if (edges[i] < 0.85f)
                {
                    canvas.SetColor(i, MortarPalette[edges[i] > 0.45f ? 1 : 0], 0f, 0.95f);
                    continue;
                }

                float value = 3f + 0.9f * tone[owner[i]] + 4.5f * light[i] + 0.9f * (Rnd(i % S, i / S, 62) - 0.5f);
                canvas.Set(i, CobblePalette, (int)Math.Round(value), height[i]);
            }

            return canvas;
        }

        private static Canvas Slate()
        {
            Canvas canvas = new(1.3f, 0.74f);
            Rng rng = new(71);
            // WHY layers: slate splits along its cleavage, so a block shows thin plates of slightly different tone.
            int[] layerOf = new int[S];
            float[] layerTone = new float[S];
            int row = 0;
            int layer = 0;
            while (row < S)
            {
                int thickness = rng.Range(2, 4);
                float tone = rng.Next() * 2f - 1f;
                for (int k = 0; k < thickness && row < S; k++, row++)
                {
                    layerOf[row] = layer;
                    layerTone[row] = tone;
                }

                layer++;
            }

            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                bool seam = y == S - 1 ? layerOf[y] != layerOf[0] : layerOf[y] != layerOf[y + 1];
                if (seam && Rnd(x, y, 72) < 0.85f)
                {
                    canvas.Set(i, SlatePalette, 0, 0f);
                    continue;
                }

                float value = 2.6f + 1.1f * layerTone[y] + 1.6f * (Noise(x, y, 4, 8, 73) - 0.5f)
                              + 0.8f * (Rnd(x, y, 74) - 0.5f);
                canvas.Set(i, SlatePalette, (int)Math.Round(value), 0.6f + 0.1f * layerTone[y]);
            }

            return canvas;
        }

        private static Canvas Basalt()
        {
            Canvas canvas = new(1.6f, 0.8f);
            Rng rng = new(81);
            Site[] sites = Scatter(ref rng, 9, 4.4f, 0.9f, 1.15f);
            int[] tone = new int[sites.Length];
            for (int index = 0; index < sites.Length; index++)
            {
                tone[index] = rng.Range(1, 4);
            }

            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                Nearest near = Worley(sites, x + 0.5f, (y + 0.5f) * 0.7f);
                if (near.F2 - near.F1 < 0.3f)
                {
                    canvas.Set(i, BasaltPalette, 0, 0f);
                    continue;
                }

                int level = tone[near.Id] + (Rnd(x, y, 82) < 0.18f ? 1 : 0) - (Rnd(x, y, 83) < 0.12f ? 1 : 0);
                canvas.Set(i, BasaltPalette, level, 0.5f + 0.1f * level);
            }

            return canvas;
        }

        private static Canvas CrackedLava()
        {
            Canvas canvas = new(1.8f, 0.85f);
            Rng rng = new(91);
            Site[] sites = Scatter(ref rng, 8, 4.6f, 0.9f, 1.2f);
            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                Nearest near = Worley(sites, x + 0.5f, y + 0.5f);
                float edge = near.F2 - near.F1;
                if (edge < 1.1f)
                {
                    int heat = 5 - (int)Math.Round(edge / 1.1f * 4f + (Rnd(x, y, 92) - 0.5f));
                    canvas.Set(i, GlowPalette, heat, 0f);
                    canvas.Rough[i] = 0.6f;
                    continue;
                }

                float value = 2f + 2f * (Noise(x, y, 4, 4, 93) - 0.5f) + 1.2f * (Rnd(x, y, 94) - 0.5f)
                              - (edge < 1.6f ? 1f : 0f);
                canvas.Set(i, CrustPalette, (int)Math.Round(value), 0.6f + 0.4f * Clamp01((edge - 1.1f) / 3f));
            }

            return canvas;
        }

        private static Canvas Grain(uint[] palette, float smoothWeight, float roughness, float relief, int seed)
        {
            Canvas canvas = new(relief, roughness);
            float[] field = Field((x, y) => smoothWeight * Fbm(x, y, 4, 2, seed)
                                            + (1f - smoothWeight) * Rnd(x, y, seed + 1));
            float[] ranks = Equalize(field);
            for (int i = 0; i < P; i++)
            {
                int level = palette.Length == 6
                    ? Quantize(ranks[i], 0.05f, 0.17f, 0.43f, 0.73f, 0.93f)
                    : Quantize(ranks[i], 0.1f, 0.35f, 0.68f, 0.9f);
                canvas.Set(i, palette, level, level / (float)(palette.Length - 1));
            }

            return canvas;
        }

        private static Canvas Sandstone()
        {
            Canvas canvas = new(1.0f, 0.88f);
            Rng rng = new(101);
            float[] bandTone = new float[S];
            int row = 0;
            while (row < S)
            {
                int thickness = rng.Range(3, 5);
                float tone = rng.Next() * 1.6f - 0.8f;
                for (int k = 0; k < thickness && row < S; k++, row++)
                {
                    bandTone[row] = tone - (k == thickness - 1 ? 0.9f : 0f) + (k == 0 ? 0.5f : 0f);
                }
            }

            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                float value = 2.6f + bandTone[y] + 1.2f * (Rnd(x, y, 102) - 0.5f) + 0.8f * (Noise(x, y, 4, 2, 103) - 0.5f);
                int level = (int)Math.Round(value);
                canvas.Set(i, SandstonePalette, level, 0.45f + 0.1f * bandTone[y]);
            }

            return canvas;
        }

        private static Canvas Ice(uint[] palette, float roughness, int seed, int cracks)
        {
            Canvas canvas = new(0.5f, roughness);
            float[] field = Field((x, y) => 0.65f * Fbm(x, y, 2, 2, seed) + 0.35f * Noise(x, y, 8, 8, seed + 1));
            float[] ranks = Equalize(field);
            for (int i = 0; i < P; i++)
            {
                int level = Quantize(ranks[i], 0.12f, 0.33f, 0.6f, 0.82f, 0.95f);
                canvas.Set(i, palette, level, 0.5f);
            }

            Rng rng = new(seed + 2);
            for (int crack = 0; crack < cracks; crack++)
            {
                int x = rng.Range(0, S - 1);
                int y = rng.Range(0, S - 1);
                int dx = rng.Chance(0.5f) ? 1 : -1;
                int length = rng.Range(4, 7);
                for (int k = 0; k < length; k++)
                {
                    canvas.SetColor(Index(x, y), 0xEEF6FF, 0.45f, roughness + 0.2f);
                    x += dx;
                    if (rng.Chance(0.5f))
                    {
                        y += rng.Chance(0.5f) ? 1 : -1;
                    }
                }
            }

            return canvas;
        }

        private static Canvas Salt()
        {
            Canvas canvas = new(1.2f, 0.62f);
            Rng rng = new(111);
            Site[] sites = Scatter(ref rng, 14, 3.2f, 0.85f, 1.2f);
            int[] tone = new int[sites.Length];
            for (int index = 0; index < sites.Length; index++)
            {
                tone[index] = rng.Range(2, 4);
            }

            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                Nearest near = Worley(sites, x + 0.5f, y + 0.5f);
                float edge = near.F2 - near.F1;
                int level = edge < 0.35f ? 1 : tone[near.Id] + (near.Dx + near.Dy < -0.6f ? 1 : 0);
                canvas.Set(i, SaltPalette, level, edge < 0.35f ? 0.2f : 0.7f);
            }

            return canvas;
        }

        private static Canvas Limestone()
        {
            Canvas canvas = new(1.0f, 0.84f);
            float[] field = Field((x, y) => 0.5f * Fbm(x, y, 4, 2, 121) + 0.2f * Noise(x, y, 8, 8, 122)
                                            + 0.3f * Rnd(x, y, 123));
            float[] ranks = Equalize(field);
            for (int i = 0; i < P; i++)
            {
                int level = Quantize(ranks[i], 0.08f, 0.26f, 0.55f, 0.82f, 0.96f);
                canvas.Set(i, LimestonePalette, level, 0.3f + 0.12f * level);
            }

            Rng rng = new(124);
            for (int pit = 0; pit < 6; pit++)
            {
                canvas.Set(Index(rng.Range(0, S - 1), rng.Range(0, S - 1)), LimestonePalette, 0, 0f);
            }

            return canvas;
        }

        private static Canvas Brick()
        {
            Canvas canvas = new(2.2f, 0.84f);
            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                int course = y / 4;
                int inCourse = y % 4;
                int shifted = (x + (course % 2 == 0 ? 0 : 4)) % S;
                int inBrick = shifted % 8;
                if (inCourse == 3 || inBrick == 7)
                {
                    int mortar = inCourse == 3 && inBrick == 7 ? 0 : Rnd(x, y, 141) < 0.4f ? 2 : 1;
                    canvas.SetColor(i, BrickMortarPalette[mortar], 0f, 0.95f);
                    continue;
                }

                float tone = Rnd(shifted / 8, course, 142) * 1.6f - 0.8f;
                float value = 2.5f + tone + 1.0f * (Rnd(x, y, 143) - 0.5f);
                value += inCourse == 0 ? 0.9f : inCourse == 2 ? -0.8f : 0f;
                value += inBrick == 0 ? 0.4f : inBrick == 6 ? -0.4f : 0f;
                bool rim = inCourse == 0 || inCourse == 2 || inBrick == 0 || inBrick == 6;
                canvas.Set(i, BrickPalette, (int)Math.Round(value), rim ? 0.75f : 1f);
            }

            return canvas;
        }

        private static Canvas Pavement()
        {
            Canvas canvas = new(1.4f, 0.86f);
            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                int slabX = x / 8;
                int slabY = y / 8;
                if (x % 8 == 7 || y % 8 == 7)
                {
                    canvas.SetColor(i, 0x56544F, 0f, 0.95f);
                    continue;
                }

                float tone = Rnd(slabX, slabY, 151) * 1.4f - 0.7f;
                float value = 2f + tone + 1.1f * (Rnd(x, y, 152) - 0.5f) + 0.6f * (Noise(x, y, 4, 4, 153) - 0.5f);
                value += x % 8 == 0 || y % 8 == 0 ? 0.6f : 0f;
                canvas.Set(i, PavementPalette, (int)Math.Round(value), 0.8f);
            }

            return canvas;
        }

        private static Canvas Asphalt()
        {
            Canvas canvas = Grain(AsphaltPalette, 0.3f, 0.92f, 0.3f, 161);
            Rng rng = new(162);
            for (int speck = 0; speck < 9; speck++)
            {
                canvas.SetColor(Index(rng.Range(0, S - 1), rng.Range(0, S - 1)), 0x6C6D70, 0.8f, 0.9f);
            }

            return canvas;
        }

        private static Canvas Planks()
        {
            Canvas canvas = new(1.5f, 0.78f);
            Rng rng = new(171);
            int[] joints = new int[4];
            int start = rng.Range(0, S - 1);
            int[] spread = { 0, 9, 4, 13 };
            for (int board = 0; board < 4; board++)
            {
                joints[board] = (start + spread[board] + rng.Range(-1, 1) + S) % S;
            }

            float[] boardTone = new float[4];
            for (int board = 0; board < 4; board++)
            {
                boardTone[board] = rng.Next() - 0.5f;
            }

            for (int i = 0; i < P; i++)
            {
                int x = i % S;
                int y = i / S;
                int board = y / 4;
                int inBoard = y % 4;
                if (inBoard == 3)
                {
                    canvas.SetColor(i, PlankSeam, 0f, 0.95f);
                    continue;
                }

                if (x == joints[board])
                {
                    canvas.SetColor(i, Mix(PlankSeam, PlankPalette[1], 0.45f), 0.2f, 0.9f);
                    continue;
                }

                // WHY stretched noise: the grain runs along the board, long streaks across x and short across y.
                float grain = Noise(x + board * 5, y, 2, 16, 172);
                float value = 2.6f + boardTone[board] + 2.3f * (grain - 0.5f) + 0.8f * (Rnd(x, y, 173) - 0.5f);
                value += inBoard == 0 ? 0.7f : inBoard == 2 ? -0.6f : 0f;
                canvas.Set(i, PlankPalette, (int)Math.Round(value), 0.85f + 0.15f * grain);
            }

            return canvas;
        }

        // ---- canvas ---------------------------------------------------------------------------------------

        /// <summary>A tile being painted: rows top first, as the art is drawn; <see cref="Finish"/> flips them.</summary>
        private sealed class Canvas
        {
            public readonly float[] Red = new float[P];
            public readonly float[] Green = new float[P];
            public readonly float[] Blue = new float[P];
            public readonly float[] Height = new float[P];
            public readonly float[] Rough = new float[P];
            private readonly float _relief;

            public Canvas(float relief, float roughness)
            {
                _relief = relief;
                for (int i = 0; i < P; i++)
                {
                    Height[i] = 0.5f;
                    Rough[i] = roughness;
                }
            }

            public void Set(int i, uint[] palette, int level, float height)
            {
                SetColor(i, palette[level < 0 ? 0 : level >= palette.Length ? palette.Length - 1 : level], height,
                    Rough[i]);
            }

            public void SetColor(int i, uint color, float height, float roughness)
            {
                Red[i] = (color >> 16) & 0xFF;
                Green[i] = (color >> 8) & 0xFF;
                Blue[i] = color & 0xFF;
                Height[i] = height;
                Rough[i] = roughness;
            }

            public TerrainBlockTile Finish(TerrainBlockStyle style)
            {
                byte[] albedo = new byte[P * 4];
                byte[] normal = new byte[P * 4];
                byte[] roughness = new byte[P];
                for (int i = 0; i < P; i++)
                {
                    int x = i % S;
                    int y = i / S;
                    int target = (S - 1 - y) * S + x;
                    albedo[target * 4] = ToByte(Red[i]);
                    albedo[target * 4 + 1] = ToByte(Green[i]);
                    albedo[target * 4 + 2] = ToByte(Blue[i]);
                    albedo[target * 4 + 3] = 255;
                    Sobel(Height, x, y, out float gradientX, out float gradientY);
                    // WHY gradientY not negated: the art's rows run down, the face's V runs up, so the slope along V
                    // is minus the slope along the rows and the normal's Y is minus that again.
                    float nx = -gradientX * _relief;
                    float ny = gradientY * _relief;
                    float length = (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                    normal[target * 4] = Encode(nx / length);
                    normal[target * 4 + 1] = Encode(ny / length);
                    normal[target * 4 + 2] = Encode(1f / length);
                    normal[target * 4 + 3] = 255;
                    roughness[target] = ToByte(Clamp01(Rough[i]) * 255f);
                }

                return new TerrainBlockTile(style, S, albedo, normal, roughness);
            }
        }

        // ---- noise ----------------------------------------------------------------------------------------

        private readonly struct Site
        {
            public Site(float x, float y, float weight)
            {
                X = x;
                Y = y;
                Weight = weight;
            }

            public float X { get; }

            public float Y { get; }

            public float Weight { get; }
        }

        private readonly struct Nearest
        {
            public Nearest(int id, float f1, float f2, float dx, float dy)
            {
                Id = id;
                F1 = f1;
                F2 = f2;
                Dx = dx;
                Dy = dy;
            }

            public int Id { get; }

            /// <summary>Weighted distance to the nearest site.</summary>
            public float F1 { get; }

            /// <summary>Weighted distance to the second nearest site.</summary>
            public float F2 { get; }

            /// <summary>Offset from the nearest site, wrapped onto the tile.</summary>
            public float Dx { get; }

            public float Dy { get; }
        }

        /// <summary>A small sequential generator for placing features.</summary>
        private struct Rng
        {
            private uint _state;

            public Rng(int seed)
            {
                _state = Mix((uint)seed * 0x9E3779B9u + 0x7F4A7C15u);
            }

            public uint NextBits()
            {
                _state = Mix(_state + 0x6D2B79F5u);
                return _state;
            }

            public float Next()
            {
                return (NextBits() >> 8) * (1f / 16777216f);
            }

            /// <summary>An integer in [<paramref name="low"/>, <paramref name="high"/>], both inclusive.</summary>
            public int Range(int low, int high)
            {
                return low + (int)(NextBits() % (uint)(high - low + 1));
            }

            public bool Chance(float probability)
            {
                return Next() < probability;
            }
        }

        private static uint Mix(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        private static uint Hash(int x, int y, int seed)
        {
            uint hash = Mix((uint)seed * 0x9E3779B9u ^ 0x5BD1E995u);
            hash = Mix(hash ^ (uint)x * 0xCC9E2D51u);
            return Mix(hash ^ (uint)y * 0x1B873593u);
        }

        private static int Wrap(int value, int period)
        {
            int result = value % period;
            return result < 0 ? result + period : result;
        }

        /// <summary>White noise per texel in [0, 1), wrapped onto the tile.</summary>
        private static float Rnd(int x, int y, int seed)
        {
            return (Hash(Wrap(x, S), Wrap(y, S), seed) >> 8) * (1f / 16777216f);
        }

        private static float Lattice(int x, int y, int cellsX, int cellsY, int seed)
        {
            return (Hash(Wrap(x, cellsX), Wrap(y, cellsY), seed) >> 8) * (1f / 16777216f);
        }

        /// <summary>Value noise in [0, 1] with <paramref name="cellsX"/> by <paramref name="cellsY"/> lattice cells per tile.</summary>
        private static float Noise(float x, float y, int cellsX, int cellsY, int seed)
        {
            float u = (x + 0.5f) / S * cellsX;
            float v = (y + 0.5f) / S * cellsY;
            int x0 = (int)Math.Floor(u);
            int y0 = (int)Math.Floor(v);
            float tx = Smooth(u - x0);
            float ty = Smooth(v - y0);
            float top = Lerp(Lattice(x0, y0, cellsX, cellsY, seed), Lattice(x0 + 1, y0, cellsX, cellsY, seed), tx);
            float bottom = Lerp(Lattice(x0, y0 + 1, cellsX, cellsY, seed),
                Lattice(x0 + 1, y0 + 1, cellsX, cellsY, seed), tx);
            return Lerp(top, bottom, ty);
        }

        private static float Fbm(float x, float y, int cells, int octaves, int seed)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += amplitude * Noise(x, y, cells << octave, cells << octave, seed + octave * 101);
                total += amplitude;
                amplitude *= 0.5f;
            }

            return sum / total;
        }

        private static Site[] Scatter(ref Rng rng, int count, float minDistance, float lowWeight, float highWeight)
        {
            List<Site> sites = new();
            for (int attempt = 0; attempt < 4000 && sites.Count < count; attempt++)
            {
                float x = rng.Next() * S;
                float y = rng.Next() * S;
                bool free = true;
                for (int index = 0; index < sites.Count && free; index++)
                {
                    float dx = WrapOffset(x - sites[index].X);
                    float dy = WrapOffset(y - sites[index].Y);
                    free = dx * dx + dy * dy >= minDistance * minDistance;
                }

                if (free)
                {
                    sites.Add(new Site(x, y, lowWeight + (highWeight - lowWeight) * rng.Next()));
                }
            }

            return sites.ToArray();
        }

        private static float WrapOffset(float delta)
        {
            if (delta > S * 0.5f)
            {
                return delta - S;
            }

            return delta < -S * 0.5f ? delta + S : delta;
        }

        private static Nearest Worley(Site[] sites, float x, float y)
        {
            float first = float.MaxValue;
            float second = float.MaxValue;
            int id = 0;
            float bestDx = 0f;
            float bestDy = 0f;
            for (int index = 0; index < sites.Length; index++)
            {
                float dx = WrapOffset(x - sites[index].X);
                float dy = WrapOffset(y - sites[index].Y);
                float distance = (float)Math.Sqrt(dx * dx + dy * dy) / sites[index].Weight;
                if (distance < first)
                {
                    second = first;
                    first = distance;
                    id = index;
                    bestDx = dx;
                    bestDy = dy;
                }
                else if (distance < second)
                {
                    second = distance;
                }
            }

            return new Nearest(id, first, second, bestDx, bestDy);
        }

        private static float[] Field(Func<int, int, float> sample)
        {
            float[] field = new float[P];
            for (int i = 0; i < P; i++)
            {
                field[i] = sample(i % S, i / S);
            }

            return field;
        }

        /// <summary>Ranks of a field in (0, 1): palette cut points then fix the share of every colour exactly.</summary>
        private static float[] Equalize(float[] field)
        {
            int[] order = new int[P];
            for (int i = 0; i < P; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int compare = field[a].CompareTo(field[b]);
                return compare != 0 ? compare : a.CompareTo(b);
            });
            float[] ranks = new float[P];
            for (int rank = 0; rank < P; rank++)
            {
                ranks[order[rank]] = (rank + 0.5f) / P;
            }

            return ranks;
        }

        private static int Quantize(float value, params float[] cuts)
        {
            int level = 0;
            for (int index = 0; index < cuts.Length; index++)
            {
                if (value > cuts[index])
                {
                    level++;
                }
            }

            return level;
        }

        /// <summary>Top-left key light of a height field: positive where the surface faces up and to the left.</summary>
        private static float[] KeyLight(float[] height)
        {
            float[] light = new float[P];
            for (int i = 0; i < P; i++)
            {
                Sobel(height, i % S, i / S, out float gradientX, out float gradientY);
                light[i] = gradientX + gradientY;
            }

            return light;
        }

        private static void Sobel(float[] field, int x, int y, out float gradientX, out float gradientY)
        {
            float Sample(int dx, int dy) => field[Index(x + dx, y + dy)];
            gradientX = (Sample(1, -1) + 2f * Sample(1, 0) + Sample(1, 1) - Sample(-1, -1) - 2f * Sample(-1, 0)
                         - Sample(-1, 1)) / 8f;
            gradientY = (Sample(-1, 1) + 2f * Sample(0, 1) + Sample(1, 1) - Sample(-1, -1) - 2f * Sample(0, -1)
                         - Sample(1, -1)) / 8f;
        }

        private static int Index(int x, int y)
        {
            return Wrap(y, S) * S + Wrap(x, S);
        }

        private static uint Mix(uint first, uint second, float share)
        {
            uint red = (uint)Math.Round(Lerp((first >> 16) & 0xFF, (second >> 16) & 0xFF, share));
            uint green = (uint)Math.Round(Lerp((first >> 8) & 0xFF, (second >> 8) & 0xFF, share));
            uint blue = (uint)Math.Round(Lerp(first & 0xFF, second & 0xFF, share));
            return (red << 16) | (green << 8) | blue;
        }

        private static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        private static byte ToByte(float value)
        {
            return (byte)(value <= 0f ? 0 : value >= 255f ? 255 : (int)Math.Round(value));
        }

        private static byte Encode(float unit)
        {
            return ToByte(unit * 127.5f + 127.5f);
        }
    }
}
