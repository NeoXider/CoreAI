using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>
    /// The roughness a material's surface never goes below when its catalog entry leaves it to the material
    /// (<see cref="RbxMaterialTextureCatalog.Entry.RoughnessFloor"/> negative, which every entry saved before the
    /// field existed loads as). The shader remaps the scaled roughness map onto floor..1, so the map's own variation
    /// survives above the floor.
    /// </summary>
    /// <remarks>
    /// WHY a floor and not a larger roughness scale: the packaged CC0 roughness maps of the polished materials
    /// average Granite002A 0.023, Foil002 0.051, Marble016 0.115 and Ice003 0.140; with their scales the surfaces
    /// rendered at 0.02 to 0.06, mirror-like, and even the largest scale a profile allows (4) left Granite at 0.09.
    /// Roblox draws these materials satin rather than mirror. The floors put Granite at about 0.39, Marble 0.27,
    /// Foil 0.2 and Ice 0.18.
    /// </remarks>
    public static class RbxMaterialRoughnessFloors
    {
        private static readonly Dictionary<string, float> Floors = new(StringComparer.Ordinal)
        {
            ["Granite"] = 0.38f,
            ["Marble"] = 0.22f,
            ["Foil"] = 0.18f,
            ["Ice"] = 0.12f
        };

        /// <summary>The floor of <paramref name="materialName"/>; 0 (none) for every other material.</summary>
        public static float For(string materialName)
        {
            return !string.IsNullOrEmpty(materialName) && Floors.TryGetValue(materialName, out float floor) ? floor : 0f;
        }
    }
}
