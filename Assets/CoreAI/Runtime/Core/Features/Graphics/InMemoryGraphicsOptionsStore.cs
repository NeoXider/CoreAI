namespace CoreAI.Look
{
    /// <summary>
    /// Session-only <see cref="IGraphicsOptionsStore"/>: options live as long as the instance. For hosts
    /// that must not write files and for tests.
    /// </summary>
    public sealed class InMemoryGraphicsOptionsStore : IGraphicsOptionsStore
    {
        private GraphicsOptions _options;

        /// <summary>Creates a store that starts with <paramref name="initial"/> (default: no choice made).</summary>
        public InMemoryGraphicsOptionsStore(GraphicsOptions initial = null)
        {
            _options = initial ?? GraphicsOptions.Default;
        }

        /// <summary>How many times <see cref="Save"/> was called.</summary>
        public int SaveCount { get; private set; }

        /// <inheritdoc />
        public GraphicsOptions Load()
        {
            return _options;
        }

        /// <inheritdoc />
        public bool Save(GraphicsOptions options)
        {
            _options = options ?? GraphicsOptions.Default;
            SaveCount++;
            return true;
        }
    }
}
