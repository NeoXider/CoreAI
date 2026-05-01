#if !COREAI_NO_LLM && !UNITY_WEBGL
using CoreAI.AgentMemory;
using CoreAI.Ai;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Единая конфигурация агента для PlayMode LLM-тестов: как в проде через
    /// <see cref="AgentBuilder"/> — <see cref="AgentMode.ToolsAndChat"/> + <see cref="MemoryLlmTool"/>.
    /// Вызывайте после <c>new AgentMemoryPolicy()</c> для роли по умолчанию LLM-сценариев (<see cref="BuiltInAgentRoleIds.Creator"/>).
    /// </summary>
    public static class TestAgentPolicyDefaults
    {
        /// <summary>
        /// Применяет ToolsAndChat + memory (append) к политике для указанной роли.
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
#endif
