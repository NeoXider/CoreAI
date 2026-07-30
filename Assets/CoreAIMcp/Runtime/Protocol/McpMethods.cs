namespace CoreAI.Mcp.Protocol
{
    /// <summary>The JSON-RPC method names the MCP server understands.</summary>
    public static class McpMethods
    {
        /// <summary>Handshake: client announces itself, server replies with capabilities + serverInfo.</summary>
        public const string Initialize = "initialize";

        /// <summary>Post-handshake notification from the client; a no-op with no response.</summary>
        public const string InitializedNotification = "notifications/initialized";

        /// <summary>Lists the available tools with their JSON Schemas.</summary>
        public const string ToolsList = "tools/list";

        /// <summary>Invokes a tool by name with an arguments object.</summary>
        public const string ToolsCall = "tools/call";

        /// <summary>Optional client keep-alive; answered with an empty result.</summary>
        public const string Ping = "ping";
    }

    /// <summary>Static server identity and protocol constants surfaced during <c>initialize</c>.</summary>
    public static class McpServerInfo
    {
        /// <summary>Advertised server name.</summary>
        public const string Name = "coreai";

        /// <summary>
        /// Advertised server version. MUST equal the <c>version</c> field of the package manifest;
        /// <c>McpPackageVersionEditModeTests</c> fails the build when the two drift apart.
        /// </summary>
        public const string Version = "6.11.1";

        /// <summary>
        /// Protocol version echoed when the client omits one. The server echoes the client's requested
        /// version verbatim when present, per the MCP version-negotiation rule.
        /// </summary>
        public const string DefaultProtocolVersion = "2025-06-18";

        /// <summary>HTTP header carrying the MCP session id across calls.</summary>
        public const string SessionHeader = "Mcp-Session-Id";
    }
}
