using UnityEngine;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>
    /// Optional seam for providers that can draw a part with 0 &lt; <c>Transparency</c> &lt; 1 blended:
    /// the binder asks for the blended twin of the material it resolved and falls back to the opaque
    /// material when the provider has none.
    /// </summary>
    public interface IRbxTransparentMaterialProvider
    {
        /// <summary>
        /// Returns the alpha-blended twin of <paramref name="opaqueMaterial"/>, the material itself when it
        /// already blends (Glass, Water, ForceField), or false when its shader cannot blend (Neon, the
        /// diagnostic fallback). The twin is owned by the provider and lives as long as its source.
        /// </summary>
        bool TryGetTransparentMaterial(Material opaqueMaterial, out Material transparentMaterial);
    }
}
