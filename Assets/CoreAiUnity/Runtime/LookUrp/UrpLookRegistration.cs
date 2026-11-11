using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting;

namespace CoreAI.Look.Urp
{
    /// <summary>
    /// Offers <see cref="UrpLookApplier"/> as the default look applier while URP is the active render
    /// pipeline. A project that installs URP but renders with another pipeline keeps the
    /// render-settings-only default.
    /// </summary>
    public static class UrpLookRegistration
    {
        /// <summary>
        /// Creates the URP applier when URP is the active pipeline, otherwise null (the registry then falls
        /// back to the render-settings applier).
        /// </summary>
        public static ICoreAiLookApplier CreateWhenUrpActive()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset
                ? new UrpLookApplier()
                : null;
        }

        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Register()
        {
            CoreAiLookApplierRegistry.RegisterPipelineDefault(CreateWhenUrpActive);
        }
    }
}
