using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>One <see cref="Enum.Material" /> slot and the CC0 set folder that surfaces it.</summary>
    internal readonly struct RbxCc0TextureSet
    {
        public RbxCc0TextureSet(string materialName, string folder)
        {
            MaterialName = materialName;
            Folder = folder;
        }

        public string MaterialName { get; }
        public string Folder { get; }
    }

    /// <summary>
    /// The single Enum.Material to CC0 texture-set table.
    /// <para>
    /// WHY: this mapping used to live in two places — the ambientCG downloader and the local-catalog
    /// importer — which silently drifted when defective sets were replaced in only one of them. Both
    /// readers now consume this table, so a replacement can never land in one list and not the other.
    /// </para>
    /// </summary>
    internal static class RbxCc0TextureSets
    {
        internal const string AmbientCgPrefix = "ambientCG/";
        internal const string PolyHavenPrefix = "polyhaven/";

        // WHY: every id here was fetched and its 2K maps unpacked onto disk before being written
        // down, and every material was then photographed on its own and inspected — see
        // dev-docs/MATERIAL_DEFECT_AUDIT_2026-09-04.md. Sixteen entries are replacements for sets
        // whose albedo and normal were both too flat to show any surface at all.
        private static readonly ReadOnlyCollection<RbxCc0TextureSet> SetsList =
            Array.AsReadOnly(new[]
            {
                new RbxCc0TextureSet("Cobblestone", "ambientCG/PavingStones151"),
                new RbxCc0TextureSet("Brick", "ambientCG/Bricks104"),
                new RbxCc0TextureSet("Slate", "polyhaven/patterned_slate_tiles"),
                new RbxCc0TextureSet("Limestone", "ambientCG/Tiles139"),
                new RbxCc0TextureSet("Sandstone", "ambientCG/Rock029"),
                new RbxCc0TextureSet("Granite", "ambientCG/Granite002A"),
                new RbxCc0TextureSet("Basalt", "polyhaven/volcanic_rock_tiles"),
                new RbxCc0TextureSet("Rock", "ambientCG/Rock064"),
                new RbxCc0TextureSet("Concrete", "ambientCG/Concrete034"),
                new RbxCc0TextureSet("Marble", "ambientCG/Marble016"),
                new RbxCc0TextureSet("Plaster", "ambientCG/Plaster005"),
                new RbxCc0TextureSet("Pavement", "ambientCG/PavingStones150"),
                new RbxCc0TextureSet("Pebble", "ambientCG/Gravel041"),
                new RbxCc0TextureSet("CeramicTiles", "ambientCG/Tiles141"),
                new RbxCc0TextureSet("ClayRoofTiles", "ambientCG/RoofingTiles014A"),
                new RbxCc0TextureSet("RoofShingles", "ambientCG/RoofingTiles003"),
                new RbxCc0TextureSet("Wood", "ambientCG/Wood095"),
                new RbxCc0TextureSet("WoodPlanks", "ambientCG/WoodFloor034"),
                new RbxCc0TextureSet("Metal", "ambientCG/Metal063"),
                new RbxCc0TextureSet("CorrodedMetal", "ambientCG/Metal021"),
                new RbxCc0TextureSet("DiamondPlate", "ambientCG/DiamondPlate008C"),
                new RbxCc0TextureSet("Foil", "ambientCG/Foil002"),
                new RbxCc0TextureSet("Grass", "ambientCG/Grass004"),
                new RbxCc0TextureSet("LeafyGrass", "ambientCG/Grass001"),
                new RbxCc0TextureSet("Ground", "ambientCG/Ground110"),
                new RbxCc0TextureSet("Mud", "ambientCG/Ground109"),
                new RbxCc0TextureSet("Sand", "ambientCG/Ground025"),
                new RbxCc0TextureSet("Snow", "ambientCG/Snow010A"),
                new RbxCc0TextureSet("Ice", "ambientCG/Ice003"),
                new RbxCc0TextureSet("CrackedLava", "ambientCG/Lava004"),
                new RbxCc0TextureSet("Asphalt", "ambientCG/Asphalt016"),
                new RbxCc0TextureSet("Fabric", "ambientCG/Fabric048"),
                new RbxCc0TextureSet("Carpet", "ambientCG/Carpet014"),
                new RbxCc0TextureSet("Leather", "ambientCG/Leather008"),
                new RbxCc0TextureSet("Cardboard", "ambientCG/Cardboard001"),
                new RbxCc0TextureSet("Rubber", "ambientCG/Rubber003")
            });

        /// <summary>All 36 CC0 sets, both ambientCG and Poly Haven.</summary>
        internal static IReadOnlyList<RbxCc0TextureSet> Sets => SetsList;

        /// <summary>Whether the set is fetched from ambientCG (Poly Haven sets are not).</summary>
        internal static bool IsAmbientCg(RbxCc0TextureSet set)
        {
            return set.Folder.StartsWith(AmbientCgPrefix, StringComparison.Ordinal);
        }

        /// <summary>The ambientCG asset id (the folder name after the prefix).</summary>
        internal static string AssetId(RbxCc0TextureSet set)
        {
            return set.Folder.Substring(AmbientCgPrefix.Length);
        }
    }
}
