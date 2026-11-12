using CoreAI.Ai.Npc;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The host game's framework settings for AI from Lua: whether mods may create agents and call models
    /// (<c>AIService:CreateAgent</c>, <c>TextGenerator:GenerateTextAsync</c>), and the limits that apply when they
    /// may. Off by default. A game that turns it on turns it on for every world it runs; a world has no switch of its
    /// own, and a mod cannot turn it on. With it off, both calls fail with an error that names
    /// <see cref="SettingName"/>.
    /// </summary>
    public sealed class CoreAiLuaAiOptions
    {
        /// <summary>The name every refusal quotes, so a reader knows exactly which setting to change.</summary>
        public const string SettingName = "CoreAiLuaAiOptions.AllowAgentsFromLua";

        /// <summary>
        /// The framework setting: mods may create agents and call models. False (the default) refuses both with an
        /// error naming <see cref="SettingName"/>.
        /// </summary>
        public bool AllowAgentsFromLua { get; set; }

        /// <summary>The name the player's-assistant refusal quotes.</summary>
        public const string AssistantSettingName = "CoreAiLuaAiOptions.AllowPlayerAssistantFromLua";

        /// <summary>
        /// The framework setting for <c>AIService:GetAssistant()</c>: mods may talk to the player's own agent (the
        /// role the in-world chat talks to, with the player's tools), which is what a chat made in Lua needs. Off by
        /// default and separate from <see cref="AllowAgentsFromLua"/>, because that agent acts with the player's
        /// tools; a host turns it on together with a Lua chat.
        /// </summary>
        public bool AllowPlayerAssistantFromLua { get; set; }

        /// <summary>
        /// Also require the per-mod <see cref="LuaAiCapability.Ai"/> capability bit, for a host that grants AI to
        /// some mods only. False (the default) lets the framework setting alone decide, as the owner's rule says.
        /// </summary>
        public bool RequireAiCapability { get; set; }

        /// <summary><c>GenerateTextAsync</c> requests a world may start per minute; the mirror's 100 by default, 0 is unlimited.</summary>
        public int TextGeneratorRequestsPerMinute { get; set; } = CoreAI.Mods.Rbx.Instances.RbxTextGenerationQuota
            .MirrorRequestsPerMinute;

        /// <summary>Agent role that answers <c>GenerateTextAsync</c>; the script's <c>SystemPrompt</c> replaces its base prompt.</summary>
        public string TextGeneratorRoleId { get; set; } = BuiltInAgentRoleIds.AiNpc;

        /// <summary>Optional routing profile of <c>GenerateTextAsync</c>; empty follows the role.</summary>
        public string TextGeneratorRoutingProfileId { get; set; } = "";

        /// <summary>Response token cap of <c>GenerateTextAsync</c> when the request names none; also its upper bound.</summary>
        public int TextGeneratorMaxTokens { get; set; } = 512;

        /// <summary>Queue priority of <c>GenerateTextAsync</c> (below the player's own agents at 0).</summary>
        public int TextGeneratorPriority { get; set; } = -10;

        /// <summary>Conversations (<c>ContextToken</c>s) a world keeps; the least recently used is dropped.</summary>
        public int MaxContextTokens { get; set; } = 256;

        /// <summary>Exchanges a <c>ContextToken</c> remembers; the oldest is dropped.</summary>
        public int MaxContextTurns { get; set; } = 16;

        /// <summary>Requests (generations and agent questions) that may wait for an answer at once.</summary>
        public int MaxOutstandingRequests { get; set; } = 32;

        /// <summary>Agents one mod may hold at once.</summary>
        public int MaxAgentsPerMod { get; set; } = 16;

        /// <summary>Defaults of an agent a mod creates; the mod's own values override them.</summary>
        public NpcAddressingRules AgentAddressing { get; set; } = new();

        /// <summary>Budget defaults of an agent a mod creates; the mod's own values override them.</summary>
        public NpcBudget AgentBudget { get; set; } = new();

        /// <summary>True when the framework setting allows AI from Lua.</summary>
        public bool IsEnabled => AllowAgentsFromLua;
    }

    /// <summary>
    /// The reserved per-mod capability bit for AI from Lua (roadmap section 2, "AI-call reservations"), excluded from
    /// <see cref="LuaCapabilities.All"/> like <see cref="LuaCapabilities.Full"/>.
    /// </summary>
    public static class LuaAiCapability
    {
        /// <summary>The bit value, <see cref="LuaCapabilities.Ai"/>.</summary>
        public const LuaCapabilities Ai = LuaCapabilities.Ai;

        /// <summary>True when <paramref name="capabilities"/> carry the AI bit.</summary>
        public static bool IsGranted(LuaCapabilities capabilities)
        {
            return (capabilities & Ai) != 0;
        }
    }
}
