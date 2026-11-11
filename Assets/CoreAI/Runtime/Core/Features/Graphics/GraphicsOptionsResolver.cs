using System;

namespace CoreAI.Look
{
    /// <summary>
    /// Pure combination rules for graphics options: preset table, override layering and validation.
    /// Engine-free, so the same rules hold in a player, a test and a plain .NET host.
    /// </summary>
    public static class GraphicsOptionsResolver
    {
        /// <summary>The lowest render scale a stored option can request.</summary>
        public const float MinimumRenderScale = 0.5f;

        /// <summary>The highest render scale a stored option can request.</summary>
        public const float MaximumRenderScale = 1f;

        /// <summary>The preset used when neither the options nor the caller name a valid one.</summary>
        public const GraphicsQualityPreset FallbackPreset = GraphicsQualityPreset.Medium;

        /// <summary>The values a preset stands for, with no overrides applied.</summary>
        public static ResolvedGraphicsOptions PresetValues(GraphicsQualityPreset preset)
        {
            switch (preset)
            {
                case GraphicsQualityPreset.Low:
                    return new ResolvedGraphicsOptions(GraphicsQualityPreset.Low, false,
                        GraphicsShadowQuality.Hard, GraphicsAntiAliasing.Off, 0.75f, true, true);
                case GraphicsQualityPreset.High:
                    return new ResolvedGraphicsOptions(GraphicsQualityPreset.High, true,
                        GraphicsShadowQuality.Soft, GraphicsAntiAliasing.Smaa, 1f, true, true);
                case GraphicsQualityPreset.Medium:
                    return new ResolvedGraphicsOptions(GraphicsQualityPreset.Medium, true,
                        GraphicsShadowQuality.Soft, GraphicsAntiAliasing.Fxaa, 1f, true, true);
                default:
                    return PresetValues(FallbackPreset);
            }
        }

        /// <summary>
        /// Combines <paramref name="options"/> with the preset it names, or with
        /// <paramref name="platformDefault"/> when it names none (or an undefined one). An override that
        /// is out of range is ignored for enums and clamped for the render scale.
        /// </summary>
        public static ResolvedGraphicsOptions Resolve(GraphicsOptions options,
            GraphicsQualityPreset platformDefault)
        {
            GraphicsOptions source = options ?? GraphicsOptions.Default;
            GraphicsQualityPreset fallback = IsDefined(platformDefault) ? platformDefault : FallbackPreset;
            GraphicsQualityPreset preset = source.Preset.HasValue && IsDefined(source.Preset.Value)
                ? source.Preset.Value
                : fallback;
            ResolvedGraphicsOptions basis = PresetValues(preset);

            GraphicsShadowQuality shadows = source.Shadows.HasValue && IsDefined(source.Shadows.Value)
                ? source.Shadows.Value
                : basis.Shadows;
            GraphicsAntiAliasing antiAliasing =
                source.AntiAliasing.HasValue && IsDefined(source.AntiAliasing.Value)
                    ? source.AntiAliasing.Value
                    : basis.AntiAliasing;
            float renderScale = source.RenderScale.HasValue && IsFinite(source.RenderScale.Value)
                ? ClampRenderScale(source.RenderScale.Value)
                : basis.RenderScale;

            return new ResolvedGraphicsOptions(
                preset,
                source.PostProcessing ?? basis.PostProcessing,
                shadows,
                antiAliasing,
                renderScale,
                source.Fog ?? basis.Fog,
                source.Sky ?? basis.Sky);
        }

        /// <summary>Clamps a finite render scale into the supported range.</summary>
        public static float ClampRenderScale(float value)
        {
            if (!IsFinite(value))
            {
                return MaximumRenderScale;
            }

            return Math.Min(MaximumRenderScale, Math.Max(MinimumRenderScale, value));
        }

        /// <summary>True when <paramref name="preset"/> is a declared preset value.</summary>
        public static bool IsDefined(GraphicsQualityPreset preset)
        {
            return preset == GraphicsQualityPreset.Low || preset == GraphicsQualityPreset.Medium ||
                   preset == GraphicsQualityPreset.High;
        }

        private static bool IsDefined(GraphicsShadowQuality value)
        {
            return value == GraphicsShadowQuality.Off || value == GraphicsShadowQuality.Hard ||
                   value == GraphicsShadowQuality.Soft;
        }

        private static bool IsDefined(GraphicsAntiAliasing value)
        {
            return value == GraphicsAntiAliasing.Off || value == GraphicsAntiAliasing.Fxaa ||
                   value == GraphicsAntiAliasing.Smaa;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
