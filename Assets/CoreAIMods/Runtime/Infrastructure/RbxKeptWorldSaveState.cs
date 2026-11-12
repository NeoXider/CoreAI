namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// Where a world kept between starts stands with its record for the next start
    /// (<see cref="RbxWorldRuntimeSessionController.KeptWorldSaveState"/>). Mods read it as the Workspace attribute
    /// <see cref="RbxWorldRuntimeSessionController.WorldSaveStateAttribute"/>, whose values are the lowercase names.
    /// </summary>
    public enum RbxKeptWorldSaveState
    {
        /// <summary>The world is not kept between starts, or not yet (the startup restore still runs); no attribute.</summary>
        NotKept = 0,

        /// <summary>Every change so far is recorded; in a browser the page's storage has also finished writing it.</summary>
        Saved = 1,

        /// <summary>A change waits for the world to be quiet before it is recorded: a reload now loses it.</summary>
        Pending = 2,

        /// <summary>A record is being captured, encoded or written, or the browser is still storing it.</summary>
        Saving = 3,

        /// <summary>
        /// The latest change was not recorded, or the saved world could not be opened at this start;
        /// <see cref="RbxWorldRuntimeSessionController.KeptWorldSaveNote"/> says why.
        /// </summary>
        Failed = 4
    }
}
