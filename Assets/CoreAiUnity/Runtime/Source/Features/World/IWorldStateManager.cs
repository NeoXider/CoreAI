using System;

namespace CoreAI.Infrastructure.World
{
    public interface IWorldStateManager
    {
        bool HasSavedState { get; }
        void Save();
        bool TryLoad(string sceneName = null);
        void Reset();
        event Action StateReset;

        /// <summary>
        /// True once the startup world restore performed by <see cref="Composition.WorldStateEntryPoint"/>
        /// has finished (whether it loaded a snapshot or found none to load). See
        /// <c>WORLD_COMMANDS.md</c> §7 "Mod rehydrate ordering guarantee" — anything that spawns objects
        /// on startup (e.g. Lua mod rehydrate) must wait for this to be true before running, or it can
        /// race the clean-slate restore (double-spawn, or have its own spawns destroyed).
        /// </summary>
        bool WorldRestoreCompleted { get; }

        /// <summary>Raised once, the moment <see cref="WorldRestoreCompleted"/> becomes true.</summary>
        event Action RestoreCompleted;

        /// <summary>
        /// (Re)starts the periodic auto-save loop at the given interval (cancelling any previous one).
        /// An interval &lt;= 0 stops periodic saving. The manager already starts this itself at the
        /// default interval during <see cref="Composition.WorldStateEntryPoint"/> startup; callers only
        /// need this to override the interval for a specific scene.
        /// </summary>
        void StartAutoSave(float intervalSeconds);
    }
}
