using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Classifies agent role ids that produce user-facing natural language (chat), as opposed to
    /// JSON/Lua machine outputs. Used by <see cref="StubLlmClient"/> and <see cref="OfflineLlmClient"/>
    /// to avoid echoing large composed user payloads when no live model is available.
    /// </summary>
    public static class LlmConversationalRolePolicy
    {
        /// <summary>Returns true when replies for <paramref name="roleId"/> should be short user text, not structured stubs.</summary>
        public static bool IsConversationalUserFacingRole(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            string trimmed = roleId.Trim();

            if (trimmed.Equals(BuiltInAgentRoleIds.PlayerChat, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(BuiltInAgentRoleIds.AiNpc, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string r = trimmed.ToLowerInvariant();
            return r.Contains("teacher") ||
                   r.Contains("mentor") ||
                   r.Contains("tutor") ||
                   r.EndsWith("chat", StringComparison.Ordinal) ||
                   (r.Contains("chat") && !r.Contains("merchant"));
        }
    }
}
