using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>Engine-free identity of one Enum.Material item. The Lua enum registry supplies
    /// the canonical name and value; render adapters use this value without importing Unity.
    /// The optional variant selects a MaterialVariant override without extending Enum.Material.</summary>
    public readonly struct RbxMaterialId : IEquatable<RbxMaterialId>
    {
        public const int PlasticValue = 256;

        public string Name { get; }

        public int Value { get; }

        /// <summary>MaterialVariant override name; null renders the plain Enum.Material.</summary>
        public string Variant { get; }

        public RbxMaterialId(string name, int value)
            : this(name, value, null)
        {
        }

        public RbxMaterialId(string name, int value, string variant)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
            Variant = variant;
        }

        /// <summary>Roblox BasePart default material.</summary>
        public static RbxMaterialId Plastic => new("Plastic", PlasticValue);

        public bool Equals(RbxMaterialId other)
        {
            return Value == other.Value
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(Variant, other.Variant, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is RbxMaterialId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Name, Value, Variant);
        }

        public static bool operator ==(RbxMaterialId left, RbxMaterialId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RbxMaterialId left, RbxMaterialId right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Variant == null
                ? "Enum.Material." + Name
                : "Enum.Material." + Name + "[\"" + Variant + "\"]";
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

    /// <summary>Implemented by render adapters that can resolve MaterialVariant overrides.</summary>
    public interface IRbxMaterialVariantConsumer
    {
        /// <summary>Variant lookup port the binder points at the world's MaterialService;
        /// null renders every part plain.</summary>
        IRbxMaterialVariantSource VariantSource { get; set; }
    }
}
