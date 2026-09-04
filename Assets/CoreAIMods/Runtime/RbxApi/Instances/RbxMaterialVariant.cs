using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Script-authored material override parented to MaterialService. A part selects
    /// it by name through BasePart.MaterialVariant; empty selects the plain BaseMaterial.</summary>
    public sealed class RbxMaterialVariant : RbxInstance
    {
        internal RbxMaterialVariant(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "MaterialVariant";
            BaseMaterial = RbxMaterialId.Plastic;
        }

        /// <summary>The Enum.Material item this variant overrides.</summary>
        public RbxMaterialId BaseMaterial { get; set; }

        /// <summary>Texture reference for the albedo slot; empty selects no override.</summary>
        public string ColorMap { get; set; } = string.Empty;

        /// <summary>Texture reference for the normal slot; empty selects no override.</summary>
        public string NormalMap { get; set; } = string.Empty;

        /// <summary>Texture reference for the roughness slot; empty selects no override.</summary>
        public string RoughnessMap { get; set; } = string.Empty;

        /// <summary>Texture reference for the metalness slot; empty selects no override.</summary>
        public string MetalnessMap { get; set; } = string.Empty;

        /// <summary>World studs covered by one texture tile (Roblox StudsPerTile).</summary>
        public float StudsPerTile { get; set; } = 1f;

        /// <summary>Engine-free snapshot the render-side provider consumes without imports.</summary>
        public RbxMaterialVariantData ToData()
        {
            return new RbxMaterialVariantData(
                BaseMaterial, ColorMap ?? string.Empty, NormalMap ?? string.Empty,
                RoughnessMap ?? string.Empty, MetalnessMap ?? string.Empty, StudsPerTile);
        }
    }
}
