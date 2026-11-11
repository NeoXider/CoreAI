using UnityEngine;

namespace CoreAI.Look
{
    /// <summary>The graphics preset a player gets before choosing one, per platform.</summary>
    public static class CoreAiGraphicsPlatform
    {
        /// <summary>
        /// <see cref="GraphicsQualityPreset.Medium"/> on WebGL and mobile,
        /// <see cref="GraphicsQualityPreset.High"/> everywhere else.
        /// </summary>
        public static GraphicsQualityPreset DefaultPresetFor(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WebGLPlayer:
                case RuntimePlatform.Android:
                case RuntimePlatform.IPhonePlayer:
                    return GraphicsQualityPreset.Medium;
                default:
                    return GraphicsQualityPreset.High;
            }
        }
    }
}
