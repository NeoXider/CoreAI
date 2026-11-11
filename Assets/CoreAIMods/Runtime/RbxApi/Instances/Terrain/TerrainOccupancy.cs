using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// Converts between Roblox occupancy (a number in 0..1) and the stored byte (0..255). Any positive
    /// occupancy stores at least 1, so a cell a script filled never reads back as empty.
    /// </summary>
    public static class TerrainOccupancy
    {
        /// <summary>The byte of a completely full cell.</summary>
        public const byte Full = byte.MaxValue;

        /// <summary>The stored byte for an occupancy in 0..1 (clamped; NaN is 0).</summary>
        public static byte ToByte(double occupancy)
        {
            if (!(occupancy > 0d))
            {
                return 0;
            }

            if (occupancy >= 1d)
            {
                return Full;
            }

            int quantized = (int)Math.Round(occupancy * Full, MidpointRounding.AwayFromZero);
            return (byte)Math.Max(1, Math.Min(Full, quantized));
        }

        /// <summary>The occupancy a stored byte reads back as.</summary>
        public static double ToDouble(byte occupancy)
        {
            return occupancy / (double)Full;
        }
    }
}
