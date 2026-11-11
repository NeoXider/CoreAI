using System;

namespace CoreAI.Look
{
    /// <summary>
    /// Default <see cref="IGraphicsOptionsService"/>: loads the choice once from an
    /// <see cref="IGraphicsOptionsStore"/>, resolves it with <see cref="GraphicsOptionsResolver"/>, and
    /// writes every change back before notifying subscribers.
    /// </summary>
    public sealed class GraphicsOptionsService : IGraphicsOptionsService
    {
        private readonly IGraphicsOptionsStore _store;

        /// <summary>Loads the stored choice from <paramref name="store"/>.</summary>
        /// <param name="store">Where the choice is kept. Required.</param>
        /// <param name="platformDefaultPreset">The preset of a player who has not chosen one.</param>
        public GraphicsOptionsService(IGraphicsOptionsStore store, GraphicsQualityPreset platformDefaultPreset)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            PlatformDefaultPreset = GraphicsOptionsResolver.IsDefined(platformDefaultPreset)
                ? platformDefaultPreset
                : GraphicsOptionsResolver.FallbackPreset;
            Options = _store.Load() ?? GraphicsOptions.Default;
            Effective = GraphicsOptionsResolver.Resolve(Options, PlatformDefaultPreset);
        }

        /// <inheritdoc />
        public GraphicsOptions Options { get; private set; }

        /// <inheritdoc />
        public ResolvedGraphicsOptions Effective { get; private set; }

        /// <inheritdoc />
        public GraphicsQualityPreset PlatformDefaultPreset { get; }

        /// <inheritdoc />
        public bool LastSaveSucceeded { get; private set; } = true;

        /// <inheritdoc />
        public event Action<ResolvedGraphicsOptions> Changed;

        /// <inheritdoc />
        public void SetOptions(GraphicsOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.Equals(Options))
            {
                return;
            }

            Options = options;
            Effective = GraphicsOptionsResolver.Resolve(Options, PlatformDefaultPreset);
            LastSaveSucceeded = _store.Save(Options);
            Changed?.Invoke(Effective);
        }

        /// <inheritdoc />
        public void ResetToDefaults()
        {
            SetOptions(GraphicsOptions.Default);
        }
    }
}
