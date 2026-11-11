using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// The <c>Enum.Material</c> items a Terrain cell can hold, as one byte slot each. The set is the
    /// mirror's: exactly the items <c>Material.yaml</c> marks "Applies to Class.Terrain" (21 solids,
    /// <c>Air</c> and <c>Water</c>). Slot 0 is <c>Air</c>, the empty cell.
    /// </summary>
    /// <remarks>
    /// WHY a slot table and not the enum value: the value runs to 2048, so a cell would need two
    /// bytes, and the codec stores the slot-to-value table in every payload, so a later reorder or
    /// extension never misreads a saved world.
    /// </remarks>
    public static class TerrainMaterials
    {
        /// <summary>The empty cell.</summary>
        public const byte Air = 0;

        /// <summary>Liquid cells: drawn as a transparent surface, no collider.</summary>
        public const byte Water = 1;

        /// <summary>Marks a value that names no Terrain material in <see cref="TryGetSlot(int, out byte)"/>.</summary>
        private const byte NoSlot = byte.MaxValue;

        private static readonly (string Name, int Value)[] Slots =
        {
            ("Air", 1792), ("Water", 2048),
            ("WoodPlanks", 528), ("Basalt", 788), ("Slate", 800), ("CrackedLava", 804),
            ("Concrete", 816), ("Limestone", 820), ("Pavement", 836), ("Brick", 848),
            ("Cobblestone", 880), ("Rock", 896), ("Sandstone", 912), ("Grass", 1280),
            ("LeafyGrass", 1284), ("Sand", 1296), ("Snow", 1328), ("Mud", 1344), ("Ground", 1360),
            ("Asphalt", 1376), ("Salt", 1392), ("Ice", 1536), ("Glacier", 1552)
        };

        private static readonly byte[] SlotByValue = BuildSlotByValue();

        private static readonly RbxMaterialId[] MaterialIds = BuildMaterialIds();

        /// <summary>Number of slots; every slot below it is valid.</summary>
        public static int SlotCount => Slots.Length;

        /// <summary>The slot of the item with this <c>Enum.Material</c> value; false for a non-Terrain material.</summary>
        public static bool TryGetSlot(int enumValue, out byte slot)
        {
            if (enumValue < 0 || enumValue >= SlotByValue.Length || SlotByValue[enumValue] == NoSlot)
            {
                slot = Air;
                return false;
            }

            slot = SlotByValue[enumValue];
            return true;
        }

        /// <summary>The slot of the item with this canonical name; false for a non-Terrain material.</summary>
        public static bool TryGetSlot(string name, out byte slot)
        {
            for (int index = 0; index < Slots.Length; index++)
            {
                if (string.Equals(Slots[index].Name, name, StringComparison.Ordinal))
                {
                    slot = (byte)index;
                    return true;
                }
            }

            slot = Air;
            return false;
        }

        /// <summary>True when <paramref name="slot"/> names a slot of this table.</summary>
        public static bool IsValidSlot(byte slot)
        {
            return slot < Slots.Length;
        }

        /// <summary>The <c>Enum.Material</c> value of a slot.</summary>
        public static int GetEnumValue(byte slot)
        {
            return Slots[RequireSlot(slot)].Value;
        }

        /// <summary>The canonical <c>Enum.Material</c> item name of a slot.</summary>
        public static string GetName(byte slot)
        {
            return Slots[RequireSlot(slot)].Name;
        }

        /// <summary>The engine-free material identity render adapters resolve.</summary>
        public static RbxMaterialId ToMaterialId(byte slot)
        {
            return MaterialIds[RequireSlot(slot)];
        }

        /// <summary>The accepted item names, for a BAD_ARGUMENT fix.</summary>
        public static string DescribeAccepted()
        {
            string[] names = new string[Slots.Length];
            for (int index = 0; index < Slots.Length; index++)
            {
                names[index] = Slots[index].Name;
            }

            return string.Join(", ", names);
        }

        private static int RequireSlot(byte slot)
        {
            if (slot >= Slots.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    "Terrain material slot out of range (0.." + (Slots.Length - 1) + ")");
            }

            return slot;
        }

        private static byte[] BuildSlotByValue()
        {
            int maxValue = 0;
            for (int index = 0; index < Slots.Length; index++)
            {
                maxValue = Math.Max(maxValue, Slots[index].Value);
            }

            byte[] table = new byte[maxValue + 1];
            for (int index = 0; index < table.Length; index++)
            {
                table[index] = NoSlot;
            }

            for (int index = 0; index < Slots.Length; index++)
            {
                table[Slots[index].Value] = (byte)index;
            }

            return table;
        }

        private static RbxMaterialId[] BuildMaterialIds()
        {
            RbxMaterialId[] ids = new RbxMaterialId[Slots.Length];
            for (int index = 0; index < Slots.Length; index++)
            {
                ids[index] = new RbxMaterialId(Slots[index].Name, Slots[index].Value);
            }

            return ids;
        }
    }
}
