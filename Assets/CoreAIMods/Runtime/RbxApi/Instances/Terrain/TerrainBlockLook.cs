using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// How each Terrain material is drawn as a voxel cell: a painted block tile (<see cref="TerrainBlockPainter"/>),
    /// one whole tile per cell face on the cell grid, or <see cref="TerrainBlockStyle.Photo"/>, the material
    /// catalog's photographed surface laid across the world as a Part shows it. <see cref="Blocks"/>, the default,
    /// paints every solid material; <see cref="PhotoSets"/> draws them all from the catalog. A drawing rule only:
    /// the cell keeps its material for <c>ReadVoxels</c>, raycasts and saved worlds, and a Part of the same material
    /// keeps the catalog's look.
    /// </summary>
    /// <remarks>
    /// WHY a rule for voxel cells only: a 4-stud cell needs a picture made for one block face (stones you can count,
    /// planks, leaves), while a Part of any size needs a surface that keeps its scale across the part; Roblox draws
    /// Terrain smooth, so this is a CoreAI choice for block worlds, with <see cref="PhotoSets"/> as the way back.
    /// </remarks>
    public sealed class TerrainBlockLook
    {
        private readonly TerrainBlockStyle[] _styles;

        private TerrainBlockLook(TerrainBlockStyle[] styles)
        {
            _styles = styles;
        }

        /// <summary>Every material from the material catalog, as before block tiles existed.</summary>
        public static TerrainBlockLook PhotoSets { get; } = new(new TerrainBlockStyle[TerrainMaterials.SlotCount]);

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
            .With("Mud", TerrainBlockStyle.Mud)
            .With("Ground", TerrainBlockStyle.Earth)
            .With("Asphalt", TerrainBlockStyle.Asphalt)
            .With("Salt", TerrainBlockStyle.Salt)
            .With("Ice", TerrainBlockStyle.Ice)
            .With("Glacier", TerrainBlockStyle.Glacier);

        /// <summary>The look a renderer uses when it is given none.</summary>
        public static TerrainBlockLook Default => Blocks;

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
            return new TerrainBlockLook(copy);
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
