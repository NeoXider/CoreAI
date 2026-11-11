using CoreAI.Look;
using UnityEngine;

namespace CoreAI.Composition
{
    /// <summary>
    /// The play world's look slot: the default look (tonemapping, Neon bloom, anti-aliasing, sky, fog) of a CoreAI
    /// scope the bootstrap has not built yet, with an optional profile asset. The look itself is applied by the
    /// scope (<c>RegisterCoreAiLook</c>); this module only chooses the preset.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoreAiPlayWorldLookModule : MonoBehaviour, ICoreAiPlayWorldModule
    {
        [Tooltip("Apply: the default look. FillMissing: only what the scene did not author. Off: the game owns "
                 + "rendering.")]
        [SerializeField]
        private LookMode mode = LookMode.Apply;

        [Tooltip("Optional look profile; empty uses the code defaults.")]
        [SerializeField]
        private CoreAiLookProfile profile;

        /// <inheritdoc />
        public void OnComposing(CoreAiPlayWorldComposition composition)
        {
            CoreAILifetimeScope core = composition?.CoreScope;
            if (core == null || core.Container != null)
            {
                return;
            }

            core.Look.Mode = mode;
            if (profile != null)
            {
                core.Look.Profile = profile;
            }
        }

        /// <inheritdoc />
        public void OnComposed(CoreAiPlayWorldComposition composition)
        {
        }
    }
}
