using System;
using System.Globalization;

namespace CoreAI.Look
{
    /// <summary>
    /// The player's stored graphics choice: an optional preset plus optional per-option overrides. A null
    /// member means "no choice made"; <see cref="GraphicsOptionsResolver"/> fills it from the preset, and
    /// an absent preset from the platform default. Immutable: every change returns a new instance.
    /// </summary>
    public sealed class GraphicsOptions : IEquatable<GraphicsOptions>
    {
        /// <summary>No preset and no overrides: everything follows the platform default preset.</summary>
        public static readonly GraphicsOptions Default = new();

        /// <summary>Creates options; every argument is optional and null means "not overridden".</summary>
        public GraphicsOptions(
            GraphicsQualityPreset? preset = null,
            bool? postProcessing = null,
            GraphicsShadowQuality? shadows = null,
            GraphicsAntiAliasing? antiAliasing = null,
            float? renderScale = null,
            bool? fog = null,
            bool? sky = null)
        {
            Preset = preset;
            PostProcessing = postProcessing;
            Shadows = shadows;
            AntiAliasing = antiAliasing;
            RenderScale = renderScale;
            Fog = fog;
            Sky = sky;
        }

        /// <summary>The chosen preset, or null for the platform default.</summary>
        public GraphicsQualityPreset? Preset { get; }

        /// <summary>Post-processing (tonemapping, bloom, colour grading) override.</summary>
        public bool? PostProcessing { get; }

        /// <summary>Directional-light shadow override.</summary>
        public GraphicsShadowQuality? Shadows { get; }

        /// <summary>Camera anti-aliasing override.</summary>
        public GraphicsAntiAliasing? AntiAliasing { get; }

        /// <summary>Render-scale override; the resolver clamps it into the supported range.</summary>
        public float? RenderScale { get; }

        /// <summary>Distance fog override.</summary>
        public bool? Fog { get; }

        /// <summary>Sky override; off replaces the skybox with a flat ambient.</summary>
        public bool? Sky { get; }

        /// <summary>True when at least one option is overridden on top of the preset.</summary>
        public bool HasOverrides => PostProcessing.HasValue || Shadows.HasValue || AntiAliasing.HasValue ||
                                    RenderScale.HasValue || Fog.HasValue || Sky.HasValue;

        /// <summary>Options that select <paramref name="preset"/> and drop every override.</summary>
        public static GraphicsOptions ForPreset(GraphicsQualityPreset? preset)
        {
            return new GraphicsOptions(preset);
        }

        /// <summary>A copy with the post-processing override replaced.</summary>
        public GraphicsOptions WithPostProcessing(bool? value)
        {
            return new GraphicsOptions(Preset, value, Shadows, AntiAliasing, RenderScale, Fog, Sky);
        }

        /// <summary>A copy with the shadow override replaced.</summary>
        public GraphicsOptions WithShadows(GraphicsShadowQuality? value)
        {
            return new GraphicsOptions(Preset, PostProcessing, value, AntiAliasing, RenderScale, Fog, Sky);
        }

        /// <summary>A copy with the anti-aliasing override replaced.</summary>
        public GraphicsOptions WithAntiAliasing(GraphicsAntiAliasing? value)
        {
            return new GraphicsOptions(Preset, PostProcessing, Shadows, value, RenderScale, Fog, Sky);
        }

        /// <summary>A copy with the render-scale override replaced.</summary>
        public GraphicsOptions WithRenderScale(float? value)
        {
            return new GraphicsOptions(Preset, PostProcessing, Shadows, AntiAliasing, value, Fog, Sky);
        }

        /// <summary>A copy with the fog override replaced.</summary>
        public GraphicsOptions WithFog(bool? value)
        {
            return new GraphicsOptions(Preset, PostProcessing, Shadows, AntiAliasing, RenderScale, value, Sky);
        }

        /// <summary>A copy with the sky override replaced.</summary>
        public GraphicsOptions WithSky(bool? value)
        {
            return new GraphicsOptions(Preset, PostProcessing, Shadows, AntiAliasing, RenderScale, Fog, value);
        }

        /// <inheritdoc />
        public bool Equals(GraphicsOptions other)
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
                   Nullable.Equals(RenderScale, other.RenderScale) &&
                   Fog == other.Fog &&
                   Sky == other.Sky;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as GraphicsOptions);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Preset.GetHashCode();
                hash = hash * 31 + PostProcessing.GetHashCode();
                hash = hash * 31 + Shadows.GetHashCode();
                hash = hash * 31 + AntiAliasing.GetHashCode();
                hash = hash * 31 + RenderScale.GetHashCode();
                hash = hash * 31 + Fog.GetHashCode();
                hash = hash * 31 + Sky.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "GraphicsOptions(preset=" + Format(Preset) +
                   ", postProcessing=" + Format(PostProcessing) +
                   ", shadows=" + Format(Shadows) +
                   ", antiAliasing=" + Format(AntiAliasing) +
                   ", renderScale=" + (RenderScale.HasValue
                       ? RenderScale.Value.ToString("0.###", CultureInfo.InvariantCulture)
                       : "default") +
                   ", fog=" + Format(Fog) +
                   ", sky=" + Format(Sky) + ")";
        }

        private static string Format<T>(T? value) where T : struct
        {
            return value.HasValue ? value.Value.ToString() : "default";
        }
    }
}
