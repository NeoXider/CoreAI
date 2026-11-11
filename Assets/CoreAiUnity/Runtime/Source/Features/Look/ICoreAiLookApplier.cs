
namespace CoreAI.Look
{
    /// <summary>
    /// Puts a look profile and the effective graphics options onto the running scene. The default comes
    /// from the active render pipeline's optional assembly (the URP applier) or, without one, from
    /// <see cref="RenderSettingsLookApplier"/>; register another implementation to replace it.
    /// </summary>
    public interface ICoreAiLookApplier
    {
        /// <summary>
        /// Applies <paramref name="profile"/> and <paramref name="options"/> under <paramref name="mode"/>.
        /// Called again on every options change and scene load, so it must be idempotent.
        /// <see cref="LookMode.Off"/> changes nothing.
        /// </summary>
        void Apply(CoreAiLookProfile profile, ResolvedGraphicsOptions options, LookMode mode);

        /// <summary>Restores everything the applier changed and destroys what it created. Idempotent.</summary>
        void Revert();
    }
}
