using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>Engine-free identity of one Enum.Material item. The Lua enum registry supplies
    /// the canonical name and value; render adapters use this value without importing Unity.</summary>
    public readonly struct RbxMaterialId
    {
        public const int PlasticValue = 256;

        public string Name { get; }

        public int Value { get; }

        public RbxMaterialId(string name, int value)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
        }

        /// <summary>Roblox BasePart default material.</summary>
        public static RbxMaterialId Plastic => new("Plastic", PlasticValue);

        public override string ToString()
        {
            return "Enum.Material." + Name;
        }
    }

    /// <summary>Engine-free material lookup port. A render adapter closes
    /// <typeparamref name="TMaterial"/> over its native shared-material handle; catalog data can
    /// therefore change without changing Lua bindings or the Part binder.</summary>
    public interface IRbxMaterialProvider<TMaterial>
    {
        /// <summary>Visible diagnostic material used when the catalog has no valid entry.</summary>
        TMaterial FallbackMaterial { get; }

        /// <summary>Resolves a shared material handle. False requests the documented fallback.</summary>
        bool TryGetMaterial(in RbxMaterialId material, out TMaterial visualMaterial);
    }
}
