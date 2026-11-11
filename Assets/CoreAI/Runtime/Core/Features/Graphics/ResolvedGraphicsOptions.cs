using System;
using System.Globalization;

namespace CoreAI.Look
{
    /// <summary>
    /// The effective graphics values after the preset and the overrides are combined: every member is
    /// set, and the render scale is already clamped. This is what a look applier consumes.
    /// </summary>
    public sealed class ResolvedGraphicsOptions : IEquatable<ResolvedGraphicsOptions>
    {
        /// <summary>Creates a fully specified value set.</summary>
        public ResolvedGraphicsOptions(
            GraphicsQualityPreset preset,
            bool postProcessing,
            GraphicsShadowQuality shadows,
            GraphicsAntiAliasing antiAliasing,
            float renderScale,
            bool fog,
            bool sky)
        {
            Preset = preset;
            PostProcessing = postProcessing;
            Shadows = shadows;
            AntiAliasing = antiAliasing;
            RenderScale = renderScale;
            Fog = fog;
            Sky = sky;
        }

        /// <summary>The preset the values started from.</summary>
        public GraphicsQualityPreset Preset { get; }

        /// <summary>Whether cameras render post-processing.</summary>
        public bool PostProcessing { get; }

        /// <summary>Directional-light shadow mode.</summary>
        public GraphicsShadowQuality Shadows { get; }

        /// <summary>Camera anti-aliasing filter.</summary>
        public GraphicsAntiAliasing AntiAliasing { get; }

        /// <summary>Render scale inside [<see cref="GraphicsOptionsResolver.MinimumRenderScale"/>, 1].</summary>
        public float RenderScale { get; }

        /// <summary>Whether distance fog is on.</summary>
        public bool Fog { get; }

        /// <summary>Whether the sky is drawn.</summary>
        public bool Sky { get; }

        /// <inheritdoc />
        public bool Equals(ResolvedGraphicsOptions other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return other != null &&
                   Preset == other.Preset &&
                   PostProcessing == other.PostProcessing &&
                   Shadows == other.Shadows &&
                   AntiAliasing == other.AntiAliasing &&
                   RenderScale.Equals(other.RenderScale) &&
                   Fog == other.Fog &&
                   Sky == other.Sky;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as ResolvedGraphicsOptions);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)Preset;
                hash = hash * 31 + PostProcessing.GetHashCode();
                hash = hash * 31 + (int)Shadows;
                hash = hash * 31 + (int)AntiAliasing;
                hash = hash * 31 + RenderScale.GetHashCode();
                hash = hash * 31 + Fog.GetHashCode();
                hash = hash * 31 + Sky.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "ResolvedGraphicsOptions(preset=" + Preset +
                   ", postProcessing=" + PostProcessing +
                   ", shadows=" + Shadows +
                   ", antiAliasing=" + AntiAliasing +
                   ", renderScale=" + RenderScale.ToString("0.###", CultureInfo.InvariantCulture) +
                   ", fog=" + Fog +
                   ", sky=" + Sky + ")";
        }
    }
}
