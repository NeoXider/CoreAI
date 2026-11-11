using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Names of the tool profiles the shipped installers register tools into and the built-in role definitions
    /// reference. A profile is a named set of tools and skills (<see cref="AgentMemoryPolicy.AddToolToProfile(string, ILlmTool)"/>);
    /// a role receives every tool of every profile its <see cref="RoleDefinition.ToolProfiles"/> lists. Hosts
    /// may register into these profiles or invent their own names.
    /// </summary>
    public static class BuiltInToolProfileIds
    {
        /// <summary>
        /// Lua and Rbx world building: <c>execute_lua</c>, <c>manage_mods</c>, <c>get_mod_logs</c>, the world-package
        /// tools and the Lua Modding / Rbx API / Full Lua skills (filled by <c>CoreAiModsInstaller</c>).
        /// Referenced by Programmer.
        /// </summary>
        public const string LuaBuilder = "lua-builder";

        /// <summary>Native world edits: <c>world_command</c> (filled by <c>WorldCommandsInstaller</c>). Referenced by Creator and Builder.</summary>
        public const string WorldBuilder = "world-builder";

        /// <summary>Agent vision: the <c>camera</c> tool (filled by <c>WorldCommandsInstaller</c>). Referenced by Programmer.</summary>
        public const string Vision = "vision";

        /// <summary>In-world character tools shared by NPC roles; empty by default. Referenced by AINpc and Merchant.</summary>
        public const string Npc = "npc";

        /// <summary>Read-only observation and reporting tools; empty by default. Referenced by Analyzer.</summary>
        public const string Observer = "observer";

        /// <summary>
        /// Shop inventory: <c>get_inventory</c>, filled when an inventory provider is registered. Referenced by Merchant.
        /// </summary>
        public const string Inventory = "inventory";

        /// <summary>Every built-in profile name.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            LuaBuilder,
            WorldBuilder,
            Vision,
            Npc,
            Observer,
            Inventory
        };
    }
}
