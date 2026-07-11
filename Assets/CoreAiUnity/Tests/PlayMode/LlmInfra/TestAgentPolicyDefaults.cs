#if !COREAI_NO_LLM
using CoreAI.AgentMemory;
using CoreAI.Ai;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Shared PlayMode LLM-test agent configuration mirroring production setup through
    /// <see cref="AgentBuilder"/>, <see cref="AgentMode.ToolsAndChat"/>, and <see cref="MemoryLlmTool"/>.
    /// Call after <c>new AgentMemoryPolicy()</c> for the default LLM scenario role
    /// (<see cref="BuiltInAgentRoleIds.Creator"/>).
    /// </summary>
    public static class TestAgentPolicyDefaults
    {
        /// <summary>
        /// Applies ToolsAndChat plus append-mode memory to the policy for the requested role.
        /// </summary>
        public static void ApplyToolsAndChatWithMemory(AgentMemoryPolicy policy, string roleId = null)
        {
            string id = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            AgentConfig cfg = new AgentBuilder(id)
                .WithMode(AgentMode.ToolsAndChat)
                .WithMemory(MemoryToolAction.Append)
                .Build();
            cfg.ApplyToPolicy(policy);
        }
    }
}
#endif // !COREAI_NO_LLM
