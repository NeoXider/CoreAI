using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Capability tiers granted to Lua scripts and mods. Binding providers consult the granted
    /// set and only register API functions whose tier is included, so a "read"-tier script
    /// physically has no world-editing functions in its globals.
    /// </summary>
    [Flags]
    public enum LuaCapabilities
    {
        None = 0,

        /// <summary>World queries, logging, version info — no side effects on the game.</summary>
        Read = 1 << 0,

        /// <summary>Gameplay-level effects: time scale, sounds, animations, UI text.</summary>
        Gameplay = 1 << 1,

        /// <summary>World structure edits: spawn, move, destroy, scenes, batch/level primitives.</summary>
        WorldEdit = 1 << 2,

        /// <summary>Redefining game logic slots (formulas, loot tables) and mod hooks.</summary>
        LogicOverride = 1 << 3,

        All = Read | Gameplay | WorldEdit | LogicOverride
    }
}
