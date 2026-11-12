using CoreAI.Mcp.Protocol;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// The request checks every MCP transport applies after its own admission (address, token, content type) and
    /// before routing: an unknown or expired session is <c>404</c>, an <c>MCP-Protocol-Version</c> this server does
    /// not serve is <c>400</c>. The loopback listener and the bridge link share them, so both answer alike.
    /// Engine-free.
    /// </summary>
    internal static class McpTransportRules
    {
        /// <summary>Body of the <c>404</c> for a session the server no longer knows.</summary>
        internal const string ExpiredSessionMessage =
            "The MCP session expired or was removed; initialize again without Mcp-Session-Id.";

        /// <summary>Body of the <c>400</c> for a DELETE without a session id.</summary>
        internal const string DeleteNeedsSessionMessage = "DELETE terminates a session: send its Mcp-Session-Id.";

        /// <summary>Body of the <c>400</c> for an unsupported protocol version header.</summary>
        internal static string UnsupportedProtocolMessage(string protocolVersion)
        {
            return $"Unsupported MCP-Protocol-Version '{protocolVersion}'. This server negotiates " +
                   string.Join(", ", McpServerInfo.NegotiatedProtocolVersions) +
                   " through initialize (and serves " + McpServerInfo.HeaderlessProtocolVersion +
                   " clients that send no header); initialize again and send the version it returns.";
        }

        /// <summary>
        /// Returns 0 when the request may be routed, else the HTTP status to answer with and its plain-text body.
        /// A known session counts as used.
        /// </summary>
        internal static int Check(McpRpcDispatcher dispatcher, string sessionId, string protocolVersion,
            out string message)
        {
            if (sessionId != null && !dispatcher.IsKnownSession(sessionId))
            {
                message = ExpiredSessionMessage;
                return 404;
            }

            // WHY: an absent header is legal only for clients older than the header itself (2025-03-26).
            // The rejection stays plain text: a dual-era client probing with a modern request reads a 400
            // without a modern JSON-RPC error body as "legacy server" and falls back to initialize.
            if (protocolVersion != null && !McpServerInfo.IsAcceptedProtocolVersionHeader(protocolVersion))
            {
                message = UnsupportedProtocolMessage(protocolVersion);
                return 400;
            }

            message = null;
            return 0;
        }
    }
}
