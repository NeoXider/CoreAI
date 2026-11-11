using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// What a cell of one material shows on its faces: a material on top, one on the four sides, one
    /// underneath, and optionally a band of the top material along the upper edge of each side (a grass
    /// block's green fringe). A drawing rule only: the cell still holds its own material for
    /// <c>ReadVoxels</c>, raycasts and saved worlds.
    /// </summary>
    public readonly struct TerrainFaceMaterialRule
    {
        /// <summary>A rule; <paramref name="fringeSixteenths"/> is the band's height in sixteenths of a cell (0 for none).</summary>
        public TerrainFaceMaterialRule(byte top, byte side, byte bottom, byte fringeSixteenths)
        {
            if (!TerrainMaterials.IsValidSlot(top) || !TerrainMaterials.IsValidSlot(side)
                                                   || !TerrainMaterials.IsValidSlot(bottom))
            {
                throw new ArgumentOutOfRangeException(nameof(top), "a face rule names Terrain material slots");
            }

            if (fringeSixteenths > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(fringeSixteenths), fringeSixteenths,
                    "the band is thinner than the cell (0..15 sixteenths)");
            }

            Top = top;
            Side = side;
            Bottom = bottom;
            FringeSixteenths = fringeSixteenths;
        }

        /// <summary>The material drawn on the face that looks up.</summary>
        public byte Top { get; }

        /// <summary>The material drawn on the four side faces.</summary>
        public byte Side { get; }

        /// <summary>The material drawn on the face that looks down.</summary>
        public byte Bottom { get; }

        /// <summary>Height of the band of <see cref="Top"/> along the upper edge of each side, in sixteenths of a cell.</summary>
        public byte FringeSixteenths { get; }

        /// <summary>The rule of a material that shows itself on every face.</summary>
        public static TerrainFaceMaterialRule Plain(byte material)
        {
            return new TerrainFaceMaterialRule(material, material, material, 0);
        }
    }

    /// <summary>
    /// The face rule of every Terrain material, one per slot. <see cref="BlockWorld"/>, the default, draws a
    /// Grass cell as a block-game grass block: grass on top, earth on the sides with a green band along their
    /// top edge, earth underneath; every other material shows itself. <see cref="None"/> draws every material on
    /// all six faces. A host builds its own with <see cref="With"/> (snow on rock, sand on sandstone) and hands it
    /// to <see cref="BlockyTerrainChunkMesher"/>.
    /// </summary>
    /// <remarks>
    /// WHY Grass and not LeafyGrass: the block world's trees are LeafyGrass cells, and leaves are leaves on every
    /// side. Roblox draws Terrain smooth and has no such rule; this is a CoreAI drawing choice for block worlds.
    /// </remarks>
    public sealed class TerrainFaceMaterialRules
    {
        /// <summary>The band on a grass block's sides: three sixteenths of a cell, three quarters of a stud.</summary>
        public const byte GrassFringeSixteenths = 3;

        private readonly TerrainFaceMaterialRule[] _rules;

        private TerrainFaceMaterialRules(TerrainFaceMaterialRule[] rules)
        {
            _rules = rules;
        }

        /// <summary>Every material shows itself on every face.</summary>
        public static TerrainFaceMaterialRules None { get; } = new(PlainRules());

        /// <summary>The block-game look: Grass is a grass block (grass on top, earth on the sides and underneath).</summary>
        public static TerrainFaceMaterialRules BlockWorld { get; } = None.With(Slot("Grass"),
            new TerrainFaceMaterialRule(Slot("Grass"), Slot("Ground"), Slot("Ground"), GrassFringeSixteenths));

        /// <summary>The rules a mesher uses when it is given none.</summary>
        public static TerrainFaceMaterialRules Default => BlockWorld;

        /// <summary>The rule of <paramref name="material"/>.</summary>
        public TerrainFaceMaterialRule For(byte material)
        {
            return _rules[material];
        }

        /// <summary>A copy with <paramref name="material"/>'s rule replaced.</summary>
        public TerrainFaceMaterialRules With(byte material, in TerrainFaceMaterialRule rule)
        {
            if (!TerrainMaterials.IsValidSlot(material))
            {
                throw new ArgumentOutOfRangeException(nameof(material), material, "unknown Terrain material slot");
            }

            TerrainFaceMaterialRule[] copy = (TerrainFaceMaterialRule[])_rules.Clone();
            copy[material] = rule;
            return new TerrainFaceMaterialRules(copy);
        }

        private static TerrainFaceMaterialRule[] PlainRules()
        {
            TerrainFaceMaterialRule[] rules = new TerrainFaceMaterialRule[TerrainMaterials.SlotCount];
            for (int slot = 0; slot < rules.Length; slot++)
            {
                rules[slot] = TerrainFaceMaterialRule.Plain((byte)slot);
            }

            return rules;
        }

        private static byte Slot(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }
    }
}
