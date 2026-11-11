using System;

namespace CoreAI.Look
{
    /// <summary>
    /// Process-local attachment point through which an optional render-pipeline assembly offers its
    /// default <see cref="ICoreAiLookApplier"/>. A container registration or
    /// <see cref="CoreAiLookSettings.ApplierOverride"/> still wins; this only decides the default.
    /// </summary>
    /// <remarks>
    /// WHY a static point and not a container registration: the optional URP assembly references this
    /// one and compiles only when URP is installed, so the composition root cannot name its types. The
    /// URP assembly writes its factory here once, at subsystem registration, before any scope builds.
    /// </remarks>
    public static class CoreAiLookApplierRegistry
    {
        // TODO: fold into the composition once presets compose optional pipeline modules: the URP assembly then
        // contributes its applier as an ICoreAiLookApplier registration of a URP look module (an
        // ICoreAiPlayWorldModule or a scope module list), and this static and its subsystem hook go away.
        private static Func<ICoreAiLookApplier> _pipelineDefault;

        /// <summary>True when a render-pipeline assembly registered a default applier.</summary>
        public static bool HasPipelineDefault => _pipelineDefault != null;

        /// <summary>The registered factory, so a test can put it back after replacing it.</summary>
        internal static Func<ICoreAiLookApplier> PipelineDefault => _pipelineDefault;

        /// <summary>
        /// Registers the default applier of the active render pipeline. A later call replaces an earlier
        /// one; null removes it.
        /// </summary>
        public static void RegisterPipelineDefault(Func<ICoreAiLookApplier> factory)
        {
            _pipelineDefault = factory;
        }

        /// <summary>
        /// Creates the registered pipeline default, or a <see cref="RenderSettingsLookApplier"/> when none is
        /// registered or the factory returns null.
        /// </summary>
        public static ICoreAiLookApplier CreateDefault()
        {
            ICoreAiLookApplier applier = _pipelineDefault?.Invoke();
            return applier ?? new RenderSettingsLookApplier();
        }
    }
}
