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

        /// <summary>
        /// Full Unity scene access via reflection (GameObject/components). Opt-in only — not part of
        /// <see cref="All"/>; hosts must grant explicitly (inspector or per-mod load).
        /// </summary>
        Full = 1 << 4,

        /// <summary>
        /// Every standard tier: <see cref="Read"/> | <see cref="Gameplay"/> | <see cref="WorldEdit"/> |
        /// <see cref="LogicOverride"/>. WARNING: despite the name, All EXCLUDES <see cref="Full"/> —
        /// Full-tier reflection is opt-in and must be OR-ed in explicitly
        /// (<c>LuaCapabilities.All | LuaCapabilities.Full</c>). A mod header that declares
        /// <c>capabilities: All, Full</c> still gets Full masked away unless the host granted it.
        /// WHY: no duplicate-value alias — a second member with this exact value would make
        /// ToString()/serialized headers print an unspecified name on IL2CPP/WebGL.
        /// </summary>
        All = Read | Gameplay | WorldEdit | LogicOverride
    }
}
