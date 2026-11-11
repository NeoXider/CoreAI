using System;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>How a textured Rbx material emits light.</summary>
    public enum RbxEmissionMode
    {
        /// <summary>Use the built-in choice for the material (CrackedLava glows, the rest does not).</summary>
        MaterialDefault = 0,

        /// <summary>No emission.</summary>
        None = 1,

        /// <summary>
        /// Emit from the hot texels of the colour map: red, bright texels above the threshold glow in the
        /// emission colour. Needs no extra texture.
        /// </summary>
        FromAlbedo = 2
    }

    /// <summary>The resolved emission of one textured material.</summary>
    public readonly struct RbxMaterialEmission
    {
        /// <summary>Creates a resolved emission.</summary>
        public RbxMaterialEmission(RbxEmissionMode mode, Color color, float threshold)
        {
            Mode = mode;
            Color = color;
            Threshold = threshold;
        }

        /// <summary>No emission.</summary>
        public static RbxMaterialEmission None => new(RbxEmissionMode.None, Color.black, 0f);

        /// <summary>The emission mode, never <see cref="RbxEmissionMode.MaterialDefault"/>.</summary>
        public RbxEmissionMode Mode { get; }

        /// <summary>HDR emission colour; values above 1 reach the bloom threshold.</summary>
        public Color Color { get; }

        /// <summary>Linear red level of the colour map where the glow starts, 0..1.</summary>
        public float Threshold { get; }

        /// <summary>True when the material emits.</summary>
        public bool Emits => Mode == RbxEmissionMode.FromAlbedo && Color.maxColorComponent > 0f;
    }

    /// <summary>Built-in emission choices of the Enum.Material items.</summary>
    public static class RbxMaterialEmissionDefaults
    {
        /// <summary>
        /// HDR glow of CrackedLava. The packaged Lava004 colour map is red rock throughout (median linear
        /// red 0.43, 95th percentile 0.76), so the glow starts at 0.5 and only the brightest quarter of the
        /// texels emits strongly.
        /// </summary>
        public static readonly Color CrackedLavaColor = new(3.2f, 0.95f, 0.22f, 1f);

        /// <summary>Threshold of the CrackedLava glow.</summary>
        public const float CrackedLavaThreshold = 0.5f;

        /// <summary>The emission a material emits when its catalog entry leaves the choice to the default.</summary>
        public static RbxMaterialEmission For(string materialName)
        {
            return string.Equals(materialName, "CrackedLava", StringComparison.Ordinal)
                ? new RbxMaterialEmission(RbxEmissionMode.FromAlbedo, CrackedLavaColor, CrackedLavaThreshold)
                : RbxMaterialEmission.None;
        }

        /// <summary>Resolves an entry's emission settings to the effective emission.</summary>
        public static RbxMaterialEmission Resolve(string materialName, RbxEmissionMode mode, Color color,
            float threshold)
        {
            switch (mode)
            {
                case RbxEmissionMode.None:
                    return RbxMaterialEmission.None;
                case RbxEmissionMode.FromAlbedo:
                    return new RbxMaterialEmission(RbxEmissionMode.FromAlbedo, color, Mathf.Clamp01(threshold));
                default:
                    return For(materialName);
            }
        }
    }
}
