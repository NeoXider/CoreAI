using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>What happened to the instance bound to a source (<see cref="IScriptInstanceHost.ScriptInstanceChanged"/>).</summary>
    public enum ScriptInstanceChange
    {
        /// <summary>A load created the instance, or adopted an unbound one at its placement, and bound it.</summary>
        Bound,

        /// <summary>A load moved the bound instance or changed its <c>RunContext</c>, or an undo put either back.</summary>
        Updated,

        /// <summary>The binding ended without a destroy: forgotten with something left inside it, or a failed load undone.</summary>
        Released,

        /// <summary>The bound instance left the world (a Destroy, the forget of a default-placed one, a failed first load).</summary>
        Destroyed
    }

    /// <summary>What <see cref="IScriptInstanceHost.Release"/> did with the instance of a forgotten source.</summary>
    public enum ScriptReleaseOutcome
    {
        /// <summary>No live instance was bound to the source.</summary>
        NothingBound,

        /// <summary>The instance sat at its default placement with nothing inside it, so it was destroyed.</summary>
        Destroyed,

        /// <summary>The instance was moved or holds children, so it stays in the world, unbound.</summary>
        Unbound
    }

    /// <summary>
    /// One placement <see cref="IScriptInstanceHost.Place"/> made: the instance, and what it took to put it there, so a
    /// failed build can put the world back with <see cref="IScriptInstanceHost.UndoPlacement"/>.
    /// </summary>
    public sealed class ScriptInstancePlacement
    {
        internal ScriptInstancePlacement(string sourceId, RbxLuaSourceContainer script, bool created, bool adopted,
            RbxInstance previousParent, int previousSiblingIndex, int previousRunContext,
            List<RbxInstance> createdFolders)
        {
            SourceId = sourceId;
            Script = script;
            Created = created;
            Adopted = adopted;
            PreviousParent = previousParent;
            PreviousSiblingIndex = previousSiblingIndex;
            PreviousRunContext = previousRunContext;
            CreatedFolders = createdFolders;
        }

        public string SourceId { get; }

        /// <summary>The instance now bound to <see cref="SourceId"/>.</summary>
        public RbxLuaSourceContainer Script { get; }

        /// <summary>True when the placement created the instance.</summary>
        public bool Created { get; }

        /// <summary>True when it bound an unbound instance that already sat at the placement (a version 1 save).</summary>
        public bool Adopted { get; }

        /// <summary>True when the instance was bound to the source before the placement.</summary>
        public bool Reused => !Created && !Adopted;

        internal RbxInstance PreviousParent { get; }

        internal int PreviousSiblingIndex { get; }

        internal int PreviousRunContext { get; }

        internal List<RbxInstance> CreatedFolders { get; }

        internal bool Undone { get; set; }
    }

    /// <summary>
    /// The world side of script contexts (MVP4 SC-1, SC-6; step C1): which world-owned instance is bound to a source,
    /// placing it for a load (created, adopted or reused), undoing that placement after a failed build, and releasing it
    /// when the source is forgotten. One per world; the mod runtime is its only caller.
    /// </summary>
    /// <remarks>
    /// WHY a seam and not runtime code: placement needs the world tree, and the runtime reaches the world only through
    /// the Roblox bindings; a different composition (a replica, a test) supplies its own host.
    /// </remarks>
    public interface IScriptInstanceHost
    {
        /// <summary>
        /// The live world-owned instance bound to <paramref name="sourceId"/>, if any. A mod's synthetic legacy script is
        /// runtime infrastructure and is never reported here.
        /// </summary>
        bool TryGetBoundScript(string sourceId, out RbxLuaSourceContainer script);

        /// <summary>
        /// Applies a durable <paramref name="plan"/>: creates the instance (or adopts an unbound one of the planned class
        /// and name at the placement), or moves and updates the bound one. Throws BAD_ARGUMENT for a <c>parent:</c> path
        /// that does not resolve, or that names a parent the world would not save with it, and changes nothing then.
        /// </summary>
        ScriptInstancePlacement Place(string sourceId, ScriptPlacementPlan plan, string ownerActorId,
            bool ownerHasHostAuthority);

        /// <summary>
        /// Reverses <paramref name="placement"/> after a failed build: a created instance is destroyed, an adopted one is
        /// unbound, a reused one goes back to its parent and <c>RunContext</c>; folders the placement created are
        /// destroyed when they are empty. A second call does nothing.
        /// </summary>
        void UndoPlacement(ScriptInstancePlacement placement);

        /// <summary>
        /// Ends the binding of a forgotten source: the instance is destroyed when it is still at the default placement
        /// for <paramref name="folderSegments"/> and has no children, and unbound (kept in the world) otherwise.
        /// </summary>
        ScriptReleaseOutcome Release(string sourceId, IReadOnlyList<string> folderSegments);

        /// <summary>
        /// Moves the instance bound to <paramref name="sourceId"/> from <paramref name="fromDefaultPath"/> to
        /// <paramref name="toDefaultPath"/> when it still sits exactly at the first (a mod changed folder); an instance a
        /// player or script moved elsewhere stays. True when it moved.
        /// </summary>
        bool MoveFromDefaultPlacement(string sourceId, IReadOnlyList<string> fromDefaultPath,
            IReadOnlyList<string> toDefaultPath);

        /// <summary>Raised after each change of a binding: (source id, change). Subscribers are isolated.</summary>
        event Action<string, ScriptInstanceChange> ScriptInstanceChanged;
    }
}
