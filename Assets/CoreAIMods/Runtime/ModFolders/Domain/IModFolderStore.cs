using System.Collections.Generic;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// The mod store as the folder operations see it: list the mods, read and write the persisted folders,
    /// change one mod's folder, switch one mod on or off. A host implements it over its own store and
    /// runtime (the CoreAI mod service: <c>ILuaModSourceStore</c> for manifests and sources, the Hub or
    /// runtime enable/disable for <see cref="SetModEnabled"/>).
    /// <para>
    /// Every write may throw to refuse; <see cref="ModFolderService"/> turns a failure into
    /// <see cref="ModFolderErrorCode.StoreFailed"/>, undoes what it can, and rethrows only
    /// <see cref="System.OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public interface IModFolderStore
    {
        /// <summary>Every known mod (stored and loaded) with its stored folder, on state and load order.</summary>
        IReadOnlyList<ModFolderModEntry> ListMods();

        /// <summary>The persisted folders (created or kept folders, empty ones included); empty when none.</summary>
        IReadOnlyList<string> LoadFolders();

        /// <summary>Replaces the persisted folders with <paramref name="folders"/> (normalized, sorted, no top level).</summary>
        void SaveFolders(IReadOnlyList<string> folders);

        /// <summary>
        /// Moves one mod to <paramref name="folderPath"/> (normalized; empty is the top level) so that the move
        /// survives a restart and every later save. The CoreAI implementation rewrites the header with
        /// <see cref="ModFolderHeaderEditor.SetCategory"/> and saves source and manifest together, and swaps
        /// the live text of a loaded mod without running it again.
        /// </summary>
        void SetModFolder(string modId, string folderPath);

        /// <summary>Switches one mod on (load and mark active) or off (unload and mark dormant, source kept).</summary>
        void SetModEnabled(string modId, bool enabled);
    }
}
