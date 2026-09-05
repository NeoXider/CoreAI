namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Decides how an MCP tool is exposed: always listed, or hidden behind the broker.
    /// FOR: cutting the permanent per-turn <c>tools/list</c> context cost — a Dynamic tool's
    /// name, description, and full JSON Schema are omitted from the listing and served on
    /// demand through the <c>coreai_tools</c> broker instead.
    /// </summary>
    public enum McpToolResidency
    {
        /// <summary>Appears in <c>tools/list</c> exactly as today.</summary>
        Native = 0,

        /// <summary>Hidden from <c>tools/list</c>; reachable via the broker, or directly by name.</summary>
        Dynamic = 1,
    }
}
