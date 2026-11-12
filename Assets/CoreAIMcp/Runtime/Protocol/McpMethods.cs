using System;
using System.Collections.Generic;

namespace CoreAI.Mcp.Protocol
{
    /// <summary>The JSON-RPC method names the MCP server understands.</summary>
    public static class McpMethods
    {
        /// <summary>Handshake: client announces itself, server replies with capabilities + serverInfo.</summary>
        public const string Initialize = "initialize";

        /// <summary>Post-handshake notification from the client; a no-op with no response.</summary>
        public const string InitializedNotification = "notifications/initialized";

        /// <summary>
        /// Client notification asking the server to stop a request it sent earlier
        /// (<c>params.requestId</c>, optional <c>params.reason</c>). A queued call is withdrawn before it
        /// reaches the game thread; a running body receives the cancellation through its token.
        /// </summary>
        public const string CancelledNotification = "notifications/cancelled";

        /// <summary>Lists the available tools with their JSON Schemas.</summary>
        public const string ToolsList = "tools/list";

        /// <summary>Server notification telling clients to refresh available tools.</summary>
        public const string ToolsListChangedNotification = "notifications/tools/list_changed";

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
        public const string Version = "7.56.0";

        /// <summary>
        /// Protocol version answered when the client requests one this server does not negotiate (or none).
        /// A requested version listed in <see cref="NegotiatedProtocolVersions"/> is echoed instead.
        /// </summary>
        public const string DefaultProtocolVersion = "2025-06-18";

        /// <summary>Newest handshake-based protocol revision this server negotiates.</summary>
        public const string LatestProtocolVersion = "2025-11-25";

        /// <summary>
        /// Revision assumed for a request without an <c>MCP-Protocol-Version</c> header and without a session.
        /// It is accepted on the wire but never negotiated: that revision requires receiving JSON-RPC
        /// batches, which this server does not, so a client asking for it is answered
        /// <see cref="DefaultProtocolVersion"/>.
        /// </summary>
        public const string HeaderlessProtocolVersion = "2025-03-26";

        /// <summary>HTTP header carrying the MCP session id across calls.</summary>
        public const string SessionHeader = "Mcp-Session-Id";

        /// <summary>HTTP header carrying the negotiated protocol version on every request after initialize.</summary>
        public const string ProtocolVersionHeader = "MCP-Protocol-Version";

        /// <summary>
        /// Default <c>instructions</c> returned by <c>initialize</c>: how an external agent should start
        /// working with a live game. Hosts replace it through <c>CoreAiMcpServer.Instructions</c>.
        /// </summary>
        public const string DefaultInstructions =
            "This server runs inside a live CoreAI game; every tool acts on the running world. Start with " +
            "game_status. Before writing Lua call read_skill: 'Lua Modding' for mods, 'Rbx API' for anything " +
            "visible; each returns its core and a section list, so read only the sections the task needs. " +
            "Rules the in-game Programmer follows: build Roblox-style (Instance.new('Part'), workspace, " +
            "Vector3/CFrame/Color3, Parent = workspace); use only APIs the skills name, never invented ones " +
            "(game.create, game.rules, GameObject.Find); pass function values to hooks_on/logic_define; no " +
            "io/os/load/debug. execute_lua is one-off; behaviour that lasts is a mod: manage_mods load (the " +
            "optional @coreai header, context: and category:, is in the skills). To change a mod: get_source " +
            "(follow next_from_line), then edit with old_text copied " +
            "exactly; reload replaces the WHOLE mod. After a change: get_mod_logs with mod_id, since_reload=true, " +
            "wait_seconds=5, then screenshot (screen=true includes the UI). Other tools, including every tool " +
            "the in-game agents have, are reached through coreai_tools (list, describe, call). isError=true " +
            "means it did not happen as asked; the text says what to do next.";

        /// <summary>Revisions answered by echoing them back from <c>initialize</c>, newest first.</summary>
        public static IReadOnlyList<string> NegotiatedProtocolVersions { get; } =
            Array.AsReadOnly(new[] { LatestProtocolVersion, DefaultProtocolVersion });

        /// <summary>
        /// The version <c>initialize</c> answers: the requested one when this server negotiates it,
        /// otherwise <see cref="DefaultProtocolVersion"/>.
        /// </summary>
        public static string NegotiateProtocolVersion(string requested)
        {
            foreach (string supported in NegotiatedProtocolVersions)
            {
                if (string.Equals(requested, supported, StringComparison.Ordinal))
                {
                    return supported;
                }
            }

            return DefaultProtocolVersion;
        }

        /// <summary>True when an <c>MCP-Protocol-Version</c> header value names a revision this server serves.</summary>
        public static bool IsAcceptedProtocolVersionHeader(string value)
        {
            return string.Equals(value, HeaderlessProtocolVersion, StringComparison.Ordinal) ||
                   string.Equals(NegotiateProtocolVersion(value), value, StringComparison.Ordinal);
        }
    }
}
