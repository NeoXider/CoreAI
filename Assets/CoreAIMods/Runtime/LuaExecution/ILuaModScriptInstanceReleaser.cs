using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional mod-runtime contract for a host that deletes a mod's package itself (the Hub) instead of asking the
    /// runtime to forget it (MVP4 step C1-5): it ends the binding of the mod's world-owned script instance the way a
    /// forget does. A runtime implements it next to <see cref="ILuaModRuntime"/>; callers test for it, so test fakes and
    /// custom runtimes keep compiling without it.
    /// </summary>
    public interface ILuaModScriptInstanceReleaser
    {
        /// <summary>
        /// Ends the binding of the instance bound to <paramref name="id"/>: destroyed when it still sits at its default
        /// placement with nothing inside it, left in the world unbound otherwise. Call it before the package is
        /// deleted, which tells the placement's folder. False when no instance is bound (every mod that declares no
        /// context). Throws <see cref="System.UnauthorizedAccessException"/> when <paramref name="caller"/> may not
        /// forget the mod.
        /// </summary>
        bool ReleaseScriptInstance(ActorContext caller, string id);
    }
}
