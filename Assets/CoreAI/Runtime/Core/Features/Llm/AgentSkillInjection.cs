namespace CoreAI.Ai
{
    /// <summary>
    /// Host-side helper to <b>preload (force-inject)</b> a <see cref="SkillSet"/> into an agent's
    /// conversation history — exactly as if the agent had already called <c>read_skill</c> for it — without
    /// running a model turn. Use it to make a skill's instructions and tools available to the agent at any
    /// moment; the agent does not start writing a response, the content is just pushed into its history and
    /// is read on the next turn it takes.
    /// <para>
    /// The payload is stored with the internal <c>"tool"</c> history role, so it is replayed to the model
    /// (the orchestrator maps <c>"tool"</c> history to a user-role context message) but hidden from the
    /// visible chat, keeping the conversation clean.
    /// </para>
    /// </summary>
    public static class AgentSkillInjection
    {
        /// <summary>
        /// Appends <paramref name="skill"/>'s <c>read_skill</c> payload (instructions + tool schemas) to
        /// <paramref name="roleId"/>'s history in <paramref name="store"/>. Does NOT trigger a model turn.
        /// Returns true when the message was appended; false for a null store/skill or a blank role id.
        /// </summary>
        /// <param name="store">Agent memory store backing the role's chat history.</param>
        /// <param name="roleId">Target agent role whose history receives the skill.</param>
        /// <param name="skill">Skill to preload (the host already holds the instance).</param>
        /// <param name="persistToDisk">Persist the appended history immediately (default true).</param>
        public static bool InjectSkillIntoHistory(
            IAgentMemoryStore store,
            string roleId,
            SkillSet skill,
            bool persistToDisk = true)
        {
            if (store == null || string.IsNullOrWhiteSpace(roleId) || skill == null)
            {
                return false;
            }

            string payload = ReadSkillLlmTool.BuildSkillPayloadJson(skill);
            if (string.IsNullOrEmpty(payload))
            {
                return false;
            }

            // Mirror what the agent would see after calling read_skill itself, so the model treats it as a
            // loaded skill. Stored as the "tool" role: replayed to the model, hidden from the visible chat.
            string message = $"read_skill(\"{skill.Name}\") [preloaded by host]: {payload}";
            store.AppendChatMessage(roleId, "tool", message, persistToDisk);
            return true;
        }
    }
}
