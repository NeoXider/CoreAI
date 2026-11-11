using System;

namespace CoreAI.Look
{
    /// <summary>
    /// The live graphics options of one scope: what the player chose, what that resolves to, and a change
    /// notification for the look applier and the Hub Graphics page. Main-thread only.
    /// </summary>
    public interface IGraphicsOptionsService
    {
        /// <summary>The stored choice (preset plus overrides).</summary>
        GraphicsOptions Options { get; }

        /// <summary>The effective values of <see cref="Options"/> on this platform.</summary>
        ResolvedGraphicsOptions Effective { get; }

        /// <summary>The preset used when the player has not chosen one.</summary>
        GraphicsQualityPreset PlatformDefaultPreset { get; }

        /// <summary>False when the last save did not reach durable storage.</summary>
        bool LastSaveSucceeded { get; }

        /// <summary>Raised after <see cref="Options"/> changed, with the new <see cref="Effective"/> values.</summary>
        event Action<ResolvedGraphicsOptions> Changed;

        /// <summary>
        /// Replaces the choice, stores it and raises <see cref="Changed"/>. An equal choice changes, stores
        /// and raises nothing.
        /// </summary>
        void SetOptions(GraphicsOptions options);

        /// <summary>Drops the preset and every override, back to the platform default.</summary>
        void ResetToDefaults();
    }
}
