using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Container for script-authored MaterialVariants, resolved as
    /// game:GetService("MaterialService"). Also fronts the engine-free variant lookup port
    /// so the binder can hand variant resolution to the material provider.</summary>
    public sealed class RbxMaterialService : RbxInstance, IRbxMaterialVariantSource
    {
        internal RbxMaterialService(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>First child MaterialVariant carrying this name, enforced by ordinal match.</summary>
        public bool TryGetVariant(string name, out RbxMaterialVariantData data)
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (RbxInstance child in GetChildren())
                {
                    if (child is RbxMaterialVariant variant
                        && string.Equals(variant.Name, name, StringComparison.Ordinal))
                    {
                        data = variant.ToData();
                        return true;
                    }
                }
            }

            data = default;
            return false;
        }
    }
}
