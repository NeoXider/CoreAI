namespace CoreAI.Ai
{
    /// <summary>
    /// Политика включения памяти по ролям.
    /// По умолчанию память включена только для Creator.
    /// </summary>
    public sealed class AgentMemoryPolicy
    {
        public bool IsMemoryEnabled(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
                roleId = BuiltInAgentRoleIds.Creator;
            roleId = roleId.Trim();
            return roleId == BuiltInAgentRoleIds.Creator;
        }
    }
}

