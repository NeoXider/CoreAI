namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// Decides whether a tool is always resident or reached through the broker.
    /// FOR: letting the HOST control per-tool context cost — a tool must not decide its own
    /// cost, so this port lives outside <see cref="IMcpTool"/> and third-party tools
    /// implementing that interface keep compiling unchanged.
    /// <para>
    /// Precedence (highest first): an explicit <c>COREAI_MCP_NATIVE</c> variable entry wins
    /// over an explicit <c>COREAI_MCP_DYNAMIC</c> entry for the same name; an explicit
    /// variable entry wins over the host-supplied <see cref="IMcpToolResidencyPolicy"/>;
    /// a tool listed nowhere falls back to the host policy, and a null host policy means
    /// the default — every tool Native. See <see cref="McpToolResidencyPolicies"/>.
    /// </para>
    /// </summary>
    public interface IMcpToolResidencyPolicy
    {
        /// <summary>Resolves the residency for one tool.</summary>
        McpToolResidency ResolveFor(IMcpTool tool);
    }
}
