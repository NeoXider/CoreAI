namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>Engine-free snapshot of one MaterialVariant a render adapter turns into a
    /// native shared material without importing the Rbx instance tree.</summary>
    public readonly struct RbxMaterialVariantData
    {
        public RbxMaterialVariantData(RbxMaterialId baseMaterial, string colorMap,
            string normalMap, string roughnessMap, string metalnessMap, float studsPerTile)
        {
            BaseMaterial = baseMaterial;
            ColorMap = colorMap;
            NormalMap = normalMap;
            RoughnessMap = roughnessMap;
            MetalnessMap = metalnessMap;
            StudsPerTile = studsPerTile;
        }

        /// <summary>The Enum.Material item this variant overrides.</summary>
        public RbxMaterialId BaseMaterial { get; }

        /// <summary>Texture reference for the albedo slot; empty selects no override.</summary>
        public string ColorMap { get; }

        /// <summary>Texture reference for the normal slot; empty selects no override.</summary>
        public string NormalMap { get; }

        /// <summary>Texture reference for the roughness slot; empty selects no override.</summary>
        public string RoughnessMap { get; }

        /// <summary>Texture reference for the metalness slot; empty selects no override.</summary>
        public string MetalnessMap { get; }

        /// <summary>World studs covered by one texture tile (Roblox StudsPerTile).</summary>
        public float StudsPerTile { get; }
    }

    /// <summary>Engine-free lookup port over the registered MaterialVariants. The binder
    /// hands this to the material provider so variant resolution never imports RbxInstance.</summary>
    public interface IRbxMaterialVariantSource
    {
        /// <summary>True with the variant snapshot when a MaterialVariant carries this name.</summary>
        bool TryGetVariant(string name, out RbxMaterialVariantData data);
    }
}
