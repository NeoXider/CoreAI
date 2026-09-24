using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Script-authored material override parented to MaterialService. A part selects
    /// it by name through BasePart.MaterialVariant; empty selects the plain BaseMaterial. Every
    /// property change fires <c>Changed</c> with the property name and that property's
    /// <c>GetPropertyChangedSignal</c>; an equal write fires nothing.</summary>
    public sealed class RbxMaterialVariant : RbxInstance
    {
        private RbxMaterialId _baseMaterial = RbxMaterialId.Plastic;
        private string _colorMap = string.Empty;
        private string _normalMap = string.Empty;
        private string _roughnessMap = string.Empty;
        private string _metalnessMap = string.Empty;
        private float _studsPerTile = 1f;

        internal RbxMaterialVariant(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "MaterialVariant";
        }

        /// <summary>The Enum.Material item this variant overrides.</summary>
        public RbxMaterialId BaseMaterial
        {
            get => _baseMaterial;
            set
            {
                if (_baseMaterial.Equals(value))
                {
                    return;
                }

                _baseMaterial = value;
                OnChanged(nameof(BaseMaterial));
            }
        }

        /// <summary>Texture reference for the albedo slot; empty selects no override.</summary>
        public string ColorMap
        {
            get => _colorMap;
            set => SetMap(ref _colorMap, value, nameof(ColorMap));
        }

        /// <summary>Texture reference for the normal slot; empty selects no override.</summary>
        public string NormalMap
        {
            get => _normalMap;
            set => SetMap(ref _normalMap, value, nameof(NormalMap));
        }

        /// <summary>Texture reference for the roughness slot; empty selects no override.</summary>
        public string RoughnessMap
        {
            get => _roughnessMap;
            set => SetMap(ref _roughnessMap, value, nameof(RoughnessMap));
        }

        /// <summary>Texture reference for the metalness slot; empty selects no override.</summary>
        public string MetalnessMap
        {
            get => _metalnessMap;
            set => SetMap(ref _metalnessMap, value, nameof(MetalnessMap));
        }

        /// <summary>How many property changes this variant has had; the Lua write path repaints
        /// the parts wearing it only when a write moved this.</summary>
        internal int ChangeCount { get; private set; }

        /// <summary>World studs covered by one texture tile (Roblox StudsPerTile).</summary>
        public float StudsPerTile
        {
            get => _studsPerTile;
            set
            {
                if (_studsPerTile.Equals(value))
                {
                    return;
                }

                _studsPerTile = value;
                OnChanged(nameof(StudsPerTile));
            }
        }

        /// <summary>Engine-free snapshot the render-side provider consumes without imports.</summary>
        public RbxMaterialVariantData ToData()
        {
            return new RbxMaterialVariantData(
                BaseMaterial, ColorMap ?? string.Empty, NormalMap ?? string.Empty,
                RoughnessMap ?? string.Empty, MetalnessMap ?? string.Empty, StudsPerTile);
        }

        /// <summary>Stores a map reference (null reads back as empty) and notifies on a change.</summary>
        private void SetMap(ref string field, string value, string propertyName)
        {
            string next = value ?? string.Empty;
            if (string.Equals(field, next, System.StringComparison.Ordinal))
            {
                return;
            }

            field = next;
            OnChanged(propertyName);
        }

        private void OnChanged(string propertyName)
        {
            ChangeCount++;
            NotifyPropertyChanged(propertyName);
        }
    }
}
