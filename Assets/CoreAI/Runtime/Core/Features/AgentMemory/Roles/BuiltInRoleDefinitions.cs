using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// The shipped definitions of the nine built-in roles. <see cref="AgentMemoryPolicy"/>'s constructor applies
    /// them, so a fresh policy holds exactly this data; a host changes a built-in role by applying its own
    /// definition over it (start from <see cref="Create"/> to change only some fields).
    /// </summary>
    public static class BuiltInRoleDefinitions
    {
        /// <summary>Returns fresh copies of all nine built-in definitions, in <see cref="BuiltInAgentRoleIds.AllBuiltInRoles"/> order.</summary>
        public static IReadOnlyList<RoleDefinition> CreateAll()
        {
            List<RoleDefinition> definitions = new(BuiltInAgentRoleIds.AllBuiltInRoles.Count);
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                definitions.Add(Create(roleId));
            }

            return definitions.AsReadOnly();
        }

        /// <summary>Returns a fresh copy of the shipped definition of a built-in role.</summary>
        /// <exception cref="ArgumentException">The role is not built in.</exception>
        public static RoleDefinition Create(string roleId)
        {
            if (!TryCreate(roleId, out RoleDefinition definition))
            {
                throw new ArgumentException($"'{roleId}' is not a built-in role.", nameof(roleId));
            }

            return definition;
        }

        /// <summary>Returns a fresh copy of the shipped definition when <paramref name="roleId"/> is built in.</summary>
        public static bool TryCreate(string roleId, out RoleDefinition definition)
        {
            definition = roleId switch
            {
                // WHY unlimited roundtrips (0) for Creator, Builder and Programmer: a build or a Lua
                // generate-run-fix loop takes many tool calls in one turn and must not be cut off mid-task.
                BuiltInAgentRoleIds.Creator => new RoleDefinition(BuiltInAgentRoleIds.Creator)
                {
                    MaxToolCallRoundtrips = 0,
                    ToolProfiles = new[] { BuiltInToolProfileIds.WorldBuilder }
                },
                BuiltInAgentRoleIds.Builder => new RoleDefinition(BuiltInAgentRoleIds.Builder)
                {
                    MaxToolCallRoundtrips = 0,
                    ToolProfiles = new[] { BuiltInToolProfileIds.WorldBuilder }
                },
                BuiltInAgentRoleIds.Analyzer => new RoleDefinition(BuiltInAgentRoleIds.Analyzer)
                {
                    ToolProfiles = new[] { BuiltInToolProfileIds.Observer }
                },
                // WHY history and LLM compaction off: chat-source runs turn history on per run without changing
                // the role (AiOrchestratorHistoryEditModeTests); Full tool results because iterating on Lua needs
                // the exact error text of the previous run.
                BuiltInAgentRoleIds.Programmer => new RoleDefinition(BuiltInAgentRoleIds.Programmer)
                {
                    WithChatHistory = false,
                    UseLlmContextCompaction = false,
                    ToolResultMemory = ToolResultMemoryPolicy.Full,
                    MaxToolCallRoundtrips = 0,
                    ToolProfiles = new[] { BuiltInToolProfileIds.Vision, BuiltInToolProfileIds.LuaBuilder }
                },
                BuiltInAgentRoleIds.AiNpc => new RoleDefinition(BuiltInAgentRoleIds.AiNpc)
                {
                    ToolProfiles = new[] { BuiltInToolProfileIds.Npc }
                },
                BuiltInAgentRoleIds.CoreMechanic => new RoleDefinition(BuiltInAgentRoleIds.CoreMechanic)
                {
                    ToolResultMemory = ToolResultMemoryPolicy.Full
                },
                BuiltInAgentRoleIds.PlainChat => new RoleDefinition(BuiltInAgentRoleIds.PlainChat)
                {
                    UseMemoryTool = false,
                    PersistChatHistory = true
                },
                BuiltInAgentRoleIds.SmartChat => new RoleDefinition(BuiltInAgentRoleIds.SmartChat)
                {
                    PersistChatHistory = true
                },
                BuiltInAgentRoleIds.Merchant => new RoleDefinition(BuiltInAgentRoleIds.Merchant)
                {
                    ToolProfiles = new[] { BuiltInToolProfileIds.Npc, BuiltInToolProfileIds.Inventory }
                },
                _ => null
            };

            return definition != null;
        }
    }
}
