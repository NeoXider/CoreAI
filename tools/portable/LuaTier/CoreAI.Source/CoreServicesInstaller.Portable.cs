using CoreAI.Authority;

namespace CoreAI.Composition
{
    /// <summary>
    /// Engine-free slice of <c>Assets/CoreAiUnity/Runtime/Source/Composition/CoreServicesInstaller.cs</c>:
    /// only the composition-issued host identity, with the identical initializer.
    /// </summary>
    /// <remarks>
    /// WHY a twin instead of the real file: the real class is a VContainer/MessagePipe installer and
    /// cannot compile without those packages, but tests and the Lua bindings read
    /// <see cref="DefaultLocalHostIdentityProvider"/>. <c>ActorIdentityComposition.CreateLocalHost</c>
    /// (CoreAI.Core) only issues the provider to a private nested type of
    /// <c>CoreAI.Composition.CoreServicesInstaller</c> in an assembly named <c>CoreAI.Source</c>, so
    /// this twin passes exactly the same proof the editor build passes; nothing is bypassed. If the
    /// real initializer changes, this file must change with it.
    /// </remarks>
    public static class CoreServicesInstaller
    {
        private sealed class ActorIdentityCompositionEntryPoint
        {
        }

        /// <summary>Composition-issued host identity used by every unconfigured single-player path.</summary>
        internal static IActorIdentityProvider DefaultLocalHostIdentityProvider { get; } =
            ActorIdentityComposition.CreateLocalHost(new ActorIdentityCompositionEntryPoint());
    }
}
