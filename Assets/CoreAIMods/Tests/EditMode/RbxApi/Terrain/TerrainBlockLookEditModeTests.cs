using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// Engine-free: the painted block tiles and the table that picks one per Terrain material. Every style paints a
    /// whole, opaque, deterministic 16-texel tile with unit normals; the tiles read as what they are named (a green
    /// lawn, brown earth, warm sand, leaves with dark gaps, cobbles you can count between their mortar, planks between
    /// seams, dark brown bark); the default look paints every solid material (Mud as bark) and leaves Air and Water alone; the photo look paints
    /// nothing; and a style given to Water or an unknown material is refused.
    /// </summary>
    [TestFixture]
    public sealed class TerrainBlockLookEditModeTests
    {
        private const int Size = TerrainBlockPainter.TileSize;

        private static IEnumerable<TerrainBlockStyle> PaintedStyles()
        {
            foreach (TerrainBlockStyle style in Enum.GetValues(typeof(TerrainBlockStyle)))
            {
                if (style != TerrainBlockStyle.Photo)
                {
                    yield return style;
                }
            }
        }

        [Test]
        public void EveryStyle_PaintsAWholeOpaqueTileWithUnitNormals()
        {
            foreach (TerrainBlockStyle style in PaintedStyles())
            {
                TerrainBlockTile tile = TerrainBlockPainter.Paint(style);
                Assert.AreEqual(Size, tile.Size, style + " size");
                Assert.AreEqual(Size * Size * 4, tile.Albedo.Length, style + " colour bytes");
                Assert.AreEqual(Size * Size * 4, tile.Normal.Length, style + " normal bytes");
                Assert.AreEqual(Size * Size, tile.Roughness.Length, style + " roughness bytes");
                for (int texel = 0; texel < Size * Size; texel++)
                {
                    Assert.AreEqual(255, tile.Albedo[texel * 4 + 3], style + " is opaque");
                    float x = tile.Normal[texel * 4] / 127.5f - 1f;
                    float y = tile.Normal[texel * 4 + 1] / 127.5f - 1f;
                    float z = tile.Normal[texel * 4 + 2] / 127.5f - 1f;
                    Assert.AreEqual(1f, (float)Math.Sqrt(x * x + y * y + z * z), 0.03f, style + " normal is unit");
                    Assert.Greater(z, 0.3f, style + " normal faces out of the face");
                }
            }
        }

        [Test]
        public void EveryStyle_IsDeterministic_AndTheCacheSharesOneTile()
        {
            foreach (TerrainBlockStyle style in PaintedStyles())
            {
                CollectionAssert.AreEqual(TerrainBlockPainter.Paint(style).Albedo,
                    TerrainBlockPainter.Paint(style).Albedo, style + " paints the same texels every time");
                Assert.AreSame(TerrainBlockPainter.Get(style), TerrainBlockPainter.Get(style), style + " is cached");
            }
        }

        [Test]
        public void TheStyles_AreAllDifferentPictures()
        {
            List<TerrainBlockStyle> styles = new(PaintedStyles());
            for (int first = 0; first < styles.Count; first++)
            {
                for (int second = first + 1; second < styles.Count; second++)
                {
                    CollectionAssert.AreNotEqual(TerrainBlockPainter.Get(styles[first]).Albedo,
                        TerrainBlockPainter.Get(styles[second]).Albedo, styles[first] + " vs " + styles[second]);
                }
            }
        }

        [Test]
        public void ThePhotoStyle_PaintsNothing_NegativeTwin()
        {
            Assert.IsNull(TerrainBlockPainter.Paint(TerrainBlockStyle.Photo));
            Assert.IsNull(TerrainBlockPainter.Get(TerrainBlockStyle.Photo));
        }

        [Test]
        public void TheTiles_ReadAsTheirMaterials()
        {
            Mean(TerrainBlockStyle.Grass, out float grassR, out float grassG, out float grassB);
            Assert.Greater(grassG - (grassR + grassB) * 0.5f, 50f, "the lawn is a saturated green");
            Mean(TerrainBlockStyle.Earth, out float earthR, out float earthG, out float earthB);
            Assert.Greater(earthR, earthG, "earth is brown: red above green");
            Assert.Greater(earthG, earthB, "earth is brown: green above blue");
            Assert.Greater(earthR - earthB, 30f, "earth is brown, not the grey gravel of the photo set");
            Mean(TerrainBlockStyle.Sand, out float sandR, out float sandG, out float sandB);
            Assert.Greater(sandR, 190f, "sand is light");
            Assert.Greater(sandR - sandB, 50f, "sand is warm");
            Mean(TerrainBlockStyle.Leaves, out float leafR, out float leafG, out float leafB);
            Assert.Greater(leafG, leafR + 20f, "leaves are green");
            Assert.Less(Luminance(leafR, leafG, leafB), Luminance(grassR, grassG, grassB),
                "a leaf block is darker than the lawn");
            Assert.Greater(ShareDarkerThan(TerrainBlockStyle.Leaves, 40f), 0.12f,
                "a leaf block shows dark gaps between its clusters");
            Assert.Greater(ShareBrighterThan(TerrainBlockStyle.Leaves, 120f), 0.03f, "and light leaf tips");
            Mean(TerrainBlockStyle.Bark, out float barkR, out float barkG, out float barkB);
            Assert.Greater(barkR, barkB + 15f, "bark is brown");
            Assert.Less(Luminance(barkR, barkG, barkB), Luminance(earthR, earthG, earthB) + 10f, "and dark");
            Mean(TerrainBlockStyle.Stone, out float stoneR, out float stoneG, out float stoneB);
            Assert.Less(Math.Abs(stoneR - stoneB), 12f, "stone is grey, not the mossy green of the photo set");
        }

        [Test]
        public void Cobblestones_CanBeCounted_BetweenTheirMortar()
        {
            TerrainBlockTile tile = TerrainBlockPainter.Get(TerrainBlockStyle.Cobblestone);
            float mortar = ShareDarkerThan(TerrainBlockStyle.Cobblestone, 75f);
            Assert.Greater(mortar, 0.1f, "the stones are set in dark mortar");
            Assert.Less(mortar, 0.4f, "the stones, not the mortar, fill the face");
            int stones = CountRegions(tile, 75f);
            Assert.GreaterOrEqual(stones, 6, "a block shows several stones");
            Assert.LessOrEqual(stones, 16, "a block shows a handful of stones, not tile mosaic");
        }

        [Test]
        public void Planks_RunBetweenDarkSeams()
        {
            TerrainBlockTile tile = TerrainBlockPainter.Get(TerrainBlockStyle.Planks);
            int seams = 0;
            for (int row = 0; row < Size; row++)
            {
                float sum = 0f;
                for (int column = 0; column < Size; column++)
                {
                    int texel = (row * Size + column) * 4;
                    sum += Luminance(tile.Albedo[texel], tile.Albedo[texel + 1], tile.Albedo[texel + 2]);
                }

                if (sum / Size < 60f)
                {
                    seams++;
                }
            }

            Assert.AreEqual(4, seams, "four boards, each closed by a dark seam");
        }

        [Test]
        public void TheDefaultLook_PaintsEverySolidMaterial_AndLeavesAirAndWaterAlone()
        {
            TerrainBlockLook look = TerrainBlockLook.Default;
            Assert.AreSame(TerrainBlockLook.Blocks, look);
            for (byte slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                TerrainBlockStyle style = look.StyleOf(slot);
                if (slot == TerrainMaterials.Air || slot == TerrainMaterials.Water)
                {
                    Assert.AreEqual(TerrainBlockStyle.Photo, style, TerrainMaterials.GetName(slot));
                }
                else
                {
                    Assert.AreNotEqual(TerrainBlockStyle.Photo, style, TerrainMaterials.GetName(slot) + " is painted");
                }
            }

            Assert.AreEqual(TerrainBlockStyle.Leaves, look.StyleOf(Slot("LeafyGrass")), "LeafyGrass cells are leaves");
            Assert.AreEqual(TerrainBlockStyle.Cobblestone, look.StyleOf(Slot("Cobblestone")));
            Assert.AreEqual(TerrainBlockStyle.Earth, look.StyleOf(Slot("Ground")));
            Assert.AreEqual(TerrainBlockStyle.Bark, look.StyleOf(Slot("Mud")), "Mud is the block world's tree bark");
            Assert.IsTrue(look.PaintsAny);
        }

        [Test]
        public void ThePhotoLook_PaintsNothing_NegativeTwin()
        {
            for (byte slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                Assert.AreEqual(TerrainBlockStyle.Photo, TerrainBlockLook.PhotoSets.StyleOf(slot));
            }

            Assert.IsFalse(TerrainBlockLook.PhotoSets.PaintsAny);
            Assert.AreEqual(TerrainBlockStyle.Photo, TerrainBlockLook.Blocks.StyleOf(200), "an unknown slot is not painted");
        }

        [Test]
        public void With_ChangesOneMaterial_AndKeepsTheOriginal()
        {
            TerrainBlockLook custom = TerrainBlockLook.Blocks.With("Rock", TerrainBlockStyle.Cobblestone)
                .With(Slot("Grass"), TerrainBlockStyle.Photo);
            Assert.AreEqual(TerrainBlockStyle.Cobblestone, custom.StyleOf(Slot("Rock")));
            Assert.AreEqual(TerrainBlockStyle.Photo, custom.StyleOf(Slot("Grass")));
            Assert.AreEqual(TerrainBlockStyle.Stone, TerrainBlockLook.Blocks.StyleOf(Slot("Rock")), "the default is unchanged");
        }

        [Test]
        public void With_RefusesWaterAirAnUnknownMaterialAndAnUnknownStyle_NegativeTwin()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainBlockLook.Blocks.With(TerrainMaterials.Water, TerrainBlockStyle.Grass));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainBlockLook.Blocks.With(TerrainMaterials.Air, TerrainBlockStyle.Stone));
            Assert.Throws<ArgumentOutOfRangeException>(() => TerrainBlockLook.Blocks.With("Plastic", TerrainBlockStyle.Stone));
            Assert.Throws<ArgumentOutOfRangeException>(() => TerrainBlockLook.Blocks.With((byte)200, TerrainBlockStyle.Stone));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TerrainBlockLook.Blocks.With("Rock", (TerrainBlockStyle)250));
            Assert.DoesNotThrow(() => TerrainBlockLook.Blocks.With(TerrainMaterials.Water, TerrainBlockStyle.Photo));
        }

        private static byte Slot(string name)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(name, out byte slot), name);
            return slot;
        }

        private static void Mean(TerrainBlockStyle style, out float red, out float green, out float blue)
        {
            byte[] albedo = TerrainBlockPainter.Get(style).Albedo;
            red = 0f;
            green = 0f;
            blue = 0f;
            for (int texel = 0; texel < Size * Size; texel++)
            {
                red += albedo[texel * 4];
                green += albedo[texel * 4 + 1];
                blue += albedo[texel * 4 + 2];
            }

            red /= Size * Size;
            green /= Size * Size;
            blue /= Size * Size;
        }

        private static float Luminance(float red, float green, float blue)
        {
            return 0.2126f * red + 0.7152f * green + 0.0722f * blue;
        }

        private static float ShareDarkerThan(TerrainBlockStyle style, float luminance)
        {
            byte[] albedo = TerrainBlockPainter.Get(style).Albedo;
            int count = 0;
            for (int texel = 0; texel < Size * Size; texel++)
            {
                if (Luminance(albedo[texel * 4], albedo[texel * 4 + 1], albedo[texel * 4 + 2]) < luminance)
                {
                    count++;
                }
            }

            return count / (float)(Size * Size);
        }

        private static float ShareBrighterThan(TerrainBlockStyle style, float luminance)
        {
            return 1f - ShareDarkerThan(style, luminance);
        }

        /// <summary>Connected regions (4-neighbour, wrapping) of texels at least <paramref name="luminance"/> bright.</summary>
        private static int CountRegions(TerrainBlockTile tile, float luminance)
        {
            bool[] seen = new bool[Size * Size];
            int regions = 0;
            Stack<int> stack = new();
            for (int start = 0; start < Size * Size; start++)
            {
                if (seen[start] || !Bright(tile, start, luminance))
                {
                    continue;
                }

                regions++;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int texel = stack.Pop();
                    int x = texel % Size;
                    int y = texel / Size;
                    int[] next =
                    {
                        y * Size + (x + 1) % Size, y * Size + (x + Size - 1) % Size, (y + 1) % Size * Size + x,
                        (y + Size - 1) % Size * Size + x
                    };
                    foreach (int neighbour in next)
                    {
                        if (!seen[neighbour] && Bright(tile, neighbour, luminance))
                        {
                            seen[neighbour] = true;
                            stack.Push(neighbour);
                        }
                    }
                }
            }

            return regions;
        }

        private static bool Bright(TerrainBlockTile tile, int texel, float luminance)
        {
            return Luminance(tile.Albedo[texel * 4], tile.Albedo[texel * 4 + 1], tile.Albedo[texel * 4 + 2]) >= luminance;
        }
    }
}
