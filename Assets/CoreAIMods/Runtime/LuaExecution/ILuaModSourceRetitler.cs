using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional mod-runtime contract for moving a loaded mod to another folder without running it again: the
    /// folder is the <c>category:</c> entry of the mod's <c>@coreai</c> header, every manifest writer derives
    /// the stored category from that header, and the Hub editor reads the live source, so a move must change
    /// the live text too. A runtime implements it next to <see cref="ILuaModRuntime"/>; callers test for it,
    /// so test fakes and custom runtimes keep compiling without it.
    /// </summary>
    public interface ILuaModSourceRetitler
    {
        /// <summary>
        /// Replaces the live source of a loaded mod with <paramref name="newSource"/>, which must differ from
        /// it only in the header's category entry (<c>ModFolderHeaderEditor.DiffersOnlyInCategory</c>), and
        /// stores the new source and manifest the way a reload would (load order and seed lineage kept). The
        /// mod is not run, its objects and handlers are untouched, and no revision is recorded. Returns false
        /// when no mod with this id is loaded; throws <see cref="System.ArgumentException"/> for any other
        /// change and <see cref="System.UnauthorizedAccessException"/> when <paramref name="caller"/> may not
        /// reload the mod.
        /// </summary>
        bool TryRetitleModSource(ActorContext caller, string id, string newSource);
    }
}
