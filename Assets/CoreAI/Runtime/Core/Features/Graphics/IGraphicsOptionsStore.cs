namespace CoreAI.Look
{
    /// <summary>
    /// Persistence boundary for the player's graphics options. Replace it to keep the options somewhere
    /// else (a platform save system, a launcher profile) or keep them for the session only.
    /// </summary>
    public interface IGraphicsOptionsStore
    {
        /// <summary>
        /// Reads the stored options. Never throws: a missing file is <see cref="GraphicsOptions.Default"/>,
        /// and an unreadable or corrupt one is <see cref="GraphicsOptions.Default"/> plus one warning.
        /// </summary>
        GraphicsOptions Load();

        /// <summary>
        /// Stores <paramref name="options"/>. Returns false, and reports why, when the write failed or will
        /// not survive a restart (a WebGL page without automatic persistence).
        /// </summary>
        bool Save(GraphicsOptions options);
    }
}
