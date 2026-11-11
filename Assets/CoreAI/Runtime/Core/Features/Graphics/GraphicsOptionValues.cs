namespace CoreAI.Look
{
    /// <summary>A named bundle of graphics option values; explicit overrides are layered on top.</summary>
    public enum GraphicsQualityPreset
    {
        /// <summary>Weak devices: no post-processing, hard shadows, reduced render scale.</summary>
        Low = 0,

        /// <summary>The WebGL and mobile default: post-processing, soft shadows, FXAA.</summary>
        Medium = 1,

        /// <summary>The desktop default: post-processing, soft shadows, SMAA.</summary>
        High = 2
    }

    /// <summary>How the scene's directional lights cast shadows.</summary>
    public enum GraphicsShadowQuality
    {
        /// <summary>No shadows.</summary>
        Off = 0,

        /// <summary>Hard-edged shadows.</summary>
        Hard = 1,

        /// <summary>Soft (filtered) shadows where the render pipeline supports them.</summary>
        Soft = 2
    }

    /// <summary>The camera anti-aliasing filter.</summary>
    public enum GraphicsAntiAliasing
    {
        /// <summary>No anti-aliasing.</summary>
        Off = 0,

        /// <summary>Fast approximate anti-aliasing: cheapest, slightly soft.</summary>
        Fxaa = 1,

        /// <summary>Subpixel morphological anti-aliasing: sharper, costs more than FXAA.</summary>
        Smaa = 2
    }

    /// <summary>
    /// How the default look is applied to a scene. A host game that owns its rendering chooses
    /// <see cref="Off"/>.
    /// </summary>
    public enum LookMode
    {
        /// <summary>
        /// Apply the look profile and the graphics options everywhere. A Volume the scene authored still
        /// wins, because the default Volume sits below priority 0.
        /// </summary>
        Apply = 0,

        /// <summary>
        /// Add only what the scene did not author: a global Volume, URP camera data, a skybox and fog.
        /// Scene-authored cameras, lights and pipeline settings are left untouched.
        /// </summary>
        FillMissing = 1,

        /// <summary>Touch nothing: the host game owns rendering, and the Hub Graphics page says so.</summary>
        Off = 2
    }
}
