using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// How each Terrain material is drawn as a voxel cell: a painted block tile (<see cref="TerrainBlockPainter"/>),
    /// one whole tile per cell face on the cell grid, or <see cref="TerrainBlockStyle.Photo"/>, the material
    /// catalog's photographed surface laid across the world as a Part shows it. <see cref="Blocks"/>, the default,
    /// paints every solid material (Mud as tree bark, for trunks); <see cref="PhotoSets"/> draws them all from the
    /// catalog. A drawing rule only: the cell keeps its material for <c>ReadVoxels</c>, raycasts and saved worlds, and
    /// a Part of the same material keeps the catalog's look.
    /// </summary>
    /// <remarks>
    /// WHY a rule for voxel cells only: a 4-stud cell needs a picture made for one block face (stones you can count,
    /// planks, leaves), while a Part of any size needs a surface that keeps its scale across the part; Roblox draws
    /// Terrain smooth, so this is a CoreAI choice for block worlds, with <see cref="PhotoSets"/> as the way back.
    /// </remarks>
    public sealed class TerrainBlockLook
    {
        private readonly TerrainBlockStyle[] _styles;

        private TerrainBlockLook(TerrainBlockStyle[] styles, TerrainWaterLook water, float cornerShade)
        {
            _styles = styles;
            Water = water;
            CornerShade = cornerShade;
        }

        /// <summary>Every material from the material catalog, as before block tiles existed.</summary>
        public static TerrainBlockLook PhotoSets { get; } =
            new(new TerrainBlockStyle[TerrainMaterials.SlotCount], TerrainWaterLook.Catalog, 0f);

        /// <summary>Every solid Terrain material as a painted block; Water keeps its own surface.</summary>
        public static TerrainBlockLook Blocks { get; } = PhotoSets
            .With("WoodPlanks", TerrainBlockStyle.Planks)
            .With("Basalt", TerrainBlockStyle.Basalt)
            .With("Slate", TerrainBlockStyle.Slate)
            .With("CrackedLava", TerrainBlockStyle.CrackedLava)
            .With("Concrete", TerrainBlockStyle.Concrete)
            .With("Limestone", TerrainBlockStyle.Limestone)
            .With("Pavement", TerrainBlockStyle.Pavement)
            .With("Brick", TerrainBlockStyle.Brick)
            .With("Cobblestone", TerrainBlockStyle.Cobblestone)
            .With("Rock", TerrainBlockStyle.Stone)
            .With("Sandstone", TerrainBlockStyle.Sandstone)
            .With("Grass", TerrainBlockStyle.Grass)
            .With("LeafyGrass", TerrainBlockStyle.Leaves)
            .With("Sand", TerrainBlockStyle.Sand)
            .With("Snow", TerrainBlockStyle.Snow)
            // WHY Mud as bark: Roblox Terrain has no log material, so a block world's trunks need one of the 23;
            // Mud is the brown one a block world least needs as itself (Ground is its dirt). DEV candidate.
            .With("Mud", TerrainBlockStyle.Bark)
            .With("Ground", TerrainBlockStyle.Earth)
            .With("Asphalt", TerrainBlockStyle.Asphalt)
            .With("Salt", TerrainBlockStyle.Salt)
            .With("Ice", TerrainBlockStyle.Ice)
            .With("Glacier", TerrainBlockStyle.Glacier)
            .WithWater(TerrainWaterLook.Calm)
            .WithCornerShade(1f);

        /// <summary>The look a renderer uses when it is given none.</summary>
        public static TerrainBlockLook Default => Blocks;

        /// <summary>How the surface of Water cells is tuned; <see cref="TerrainWaterLook.Catalog"/> keeps the catalog's water.</summary>
        public TerrainWaterLook Water { get; }

        /// <summary>
        /// How strongly the painted cells darken where blocks meet (the corner shade the mesh carries,
        /// <see cref="TerrainSkyExposure.GetCornerOcclusion"/>), 0..1; 0 draws none.
        /// </summary>
        public float CornerShade { get; }

        /// <summary>The style <paramref name="slot"/> is drawn with; <see cref="TerrainBlockStyle.Photo"/> for Air and Water.</summary>
        public TerrainBlockStyle StyleOf(byte slot)
        {
            return TerrainMaterials.IsValidSlot(slot) ? _styles[slot] : TerrainBlockStyle.Photo;
        }

        /// <summary>True when at least one material is drawn as a painted block.</summary>
        public bool PaintsAny
        {
            get
            {
                for (int slot = 0; slot < _styles.Length; slot++)
                {
                    if (_styles[slot] != TerrainBlockStyle.Photo)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>A copy with <paramref name="slot"/> drawn as <paramref name="style"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The slot is unknown, or a style other than Photo is given to Air or Water.</exception>
        public TerrainBlockLook With(byte slot, TerrainBlockStyle style)
        {
            if (!TerrainMaterials.IsValidSlot(slot))
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "unknown Terrain material slot");
            }

            if (style != TerrainBlockStyle.Photo && (slot == TerrainMaterials.Air || slot == TerrainMaterials.Water))
            {
                throw new ArgumentOutOfRangeException(nameof(style), style,
                    "Air draws nothing and Water keeps its own surface; only solid materials take a block tile");
            }

            if (!Enum.IsDefined(typeof(TerrainBlockStyle), style))
            {
                throw new ArgumentOutOfRangeException(nameof(style), style, "unknown block style");
            }

            TerrainBlockStyle[] copy = (TerrainBlockStyle[])_styles.Clone();
            copy[slot] = style;
            return new TerrainBlockLook(copy, Water, CornerShade);
        }

        /// <summary>A copy whose Water cells are tuned by <paramref name="water"/>.</summary>
        public TerrainBlockLook WithWater(TerrainWaterLook water)
        {
            return new TerrainBlockLook((TerrainBlockStyle[])_styles.Clone(), water, CornerShade);
        }

        /// <summary>A copy that shades the corners where blocks meet by <paramref name="strength"/> (0..1).</summary>
        /// <exception cref="ArgumentOutOfRangeException">The strength is outside 0..1.</exception>
        public TerrainBlockLook WithCornerShade(float strength)
        {
            if (!(strength >= 0f) || strength > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(strength), strength, "the corner shade is 0..1");
            }

            return new TerrainBlockLook((TerrainBlockStyle[])_styles.Clone(), Water, strength);
        }

        /// <summary>A copy with the material named <paramref name="materialName"/> drawn as <paramref name="style"/>.</summary>
        public TerrainBlockLook With(string materialName, TerrainBlockStyle style)
        {
            if (!TerrainMaterials.TryGetSlot(materialName, out byte slot))
            {
                throw new ArgumentOutOfRangeException(nameof(materialName), materialName,
                    "not a Terrain material; accepted: " + TerrainMaterials.DescribeAccepted());
            }

            return With(slot, style);
        }
    }
}

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// The tuning of the Water surface on Terrain cells, applied to the cells' copy of the catalog water only (a Part
    /// of Water keeps the catalog's): wave scale in cycles per metre, wave strength, smoothness and the water's own
    /// colour (0..1, in the same sRGB terms as the catalog's water colour).
    /// </summary>
    public readonly struct TerrainWaterLook
    {
        /// <summary>A tuning; <paramref name="overrides"/> false keeps the catalog water as it is.</summary>
        public TerrainWaterLook(bool overrides, float waveScale, float waveStrength, float smoothness, float red,
            float green, float blue, float absorption)
        {
            if (overrides && (!(waveScale > 0f) || !(waveStrength >= 0f) || !(smoothness >= 0f) || smoothness > 1f
                              || !(red >= 0f) || !(green >= 0f) || !(blue >= 0f) || !(absorption >= 0f)))
            {
                throw new System.ArgumentOutOfRangeException(nameof(waveScale),
                    "a water tuning needs a positive wave scale, a wave strength, colour and absorption of at least "
                    + "0, and a smoothness in 0..1");
            }

            Overrides = overrides;
            Absorption = absorption;
            WaveScale = waveScale;
            WaveStrength = waveStrength;
            Smoothness = smoothness;
            Red = red;
            Green = green;
            Blue = blue;
        }

        /// <summary>The catalog's water, untouched.</summary>
        public static TerrainWaterLook Catalog => default;

        /// <summary>
        /// The block world's water: a swell about 12 studs long under a gentle chop instead of the 5-stud ripples that
        /// crawled across a lake, slightly less mirror-like, and a turquoise body colour that shallow water already
        /// shows (it absorbs about twice as fast as the catalog's, so a lake over sand reads as water, not clear glass).
        /// </summary>
        public static TerrainWaterLook Calm => new(true, 0.3f, 0.13f, 0.9f, 0.05f, 0.52f, 0.6f, 1.3f);

        /// <summary>True when the cells' water is tuned; false keeps the catalog water.</summary>
        public bool Overrides { get; }

        /// <summary>Wave cycles per metre of the long swell (the chop is a fixed multiple of it).</summary>
        public float WaveScale { get; }

        /// <summary>How far the waves tilt the surface normal.</summary>
        public float WaveStrength { get; }

        /// <summary>Surface smoothness, 0..1.</summary>
        public float Smoothness { get; }

        /// <summary>The water's own colour, red.</summary>
        public float Red { get; }

        /// <summary>The water's own colour, green.</summary>
        public float Green { get; }

        /// <summary>The water's own colour, blue.</summary>
        public float Blue { get; }

        /// <summary>How fast the water takes its own colour with depth, per metre (the catalog's water uses 0.6).</summary>
        public float Absorption { get; }
    }
}
