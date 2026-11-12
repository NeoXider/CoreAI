namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// What a status surface (the Hub MCP tab) reads about the game's MCP link and how it starts and stops it,
    /// so the surface needs neither the server component nor Unity to be tested. Engine-free.
    /// </summary>
    public interface IMcpLinkControl
    {
        /// <summary>True while the in-game server listens or the bridge link is up.</summary>
        bool IsRunning { get; }

        /// <summary>False where a game cannot listen (a WebGL player); the link is then the bridge.</summary>
        bool ListenerSupported { get; }

        /// <summary><c>http</c> or <c>bridge</c> while running; empty while stopped.</summary>
        string Mode { get; }

        /// <summary>The address a client connects to; null while stopped.</summary>
        string Url { get; }

        /// <summary>The bearer token a client sends to the in-game server; null for the bridge or without auth.</summary>
        string AuthToken { get; }

        /// <summary>Live MCP sessions.</summary>
        int Sessions { get; }

        /// <summary>JSON-RPC messages served since the link started.</summary>
        long Requests { get; }

        /// <summary><c>tools/call</c> requests served.</summary>
        long ToolCalls { get; }

        /// <summary><c>tools/call</c> requests that failed.</summary>
        long ToolErrors { get; }

        /// <summary>The last failure: a failed start, a tool error, a bridge refusal; null when none.</summary>
        string LastError { get; }

        /// <summary>True when the catalog holds <c>manage_mods</c>.</summary>
        bool HostAdminMods { get; }

        /// <summary>Starts the link the way the debug start-up would for this process.</summary>
        void Start();

        /// <summary>Stops the link.</summary>
        void Stop();

        /// <summary>Links to the bridge the developer typed in (a phone), replacing a running link.</summary>
        void StartBridge(McpBridgeEndpoint endpoint);

        /// <summary>The bridge this device remembers and reconnects to at launch; null when none (the default).</summary>
        McpBridgeEndpoint RememberedBridge { get; }

        /// <summary>Remembers <paramref name="endpoint"/> on this device, or forgets the remembered one when null.</summary>
        void Remember(McpBridgeEndpoint endpoint);
    }

    /// <summary>Where a phone (or any player) reaches the MCP bridge, as the developer typed it.</summary>
    public sealed class McpBridgeEndpoint
    {
        /// <param name="host">The bridge's address; null or empty means <c>127.0.0.1</c>.</param>
        /// <param name="port">The bridge's port.</param>
        /// <param name="key">The game key the bridge printed.</param>
        /// <param name="hostAdmin">Grant <c>manage_mods</c> and <c>save_world</c> to this link.</param>
        public McpBridgeEndpoint(string host, int port, string key, bool hostAdmin = true)
        {
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            Port = port;
            Key = key?.Trim() ?? "";
            HostAdmin = hostAdmin;
        }

        /// <summary>The bridge's address.</summary>
        public string Host { get; }

        /// <summary>The bridge's port.</summary>
        public int Port { get; }

        /// <summary>The game key.</summary>
        public string Key { get; }

        /// <summary>Grant <c>manage_mods</c> and <c>save_world</c> to this link.</summary>
        public bool HostAdmin { get; }
    }

    /// <summary>A transport that can say what went wrong with its link (the bridge: refused, unreachable).</summary>
    internal interface IMcpTransportDiagnostics
    {
        /// <summary>The last link problem; null while the link is healthy.</summary>
        string LastLinkError { get; }
    }

    /// <summary>Builds the client command a status surface offers, and its masked form for display.</summary>
    public static class McpConnectCommand
    {
        /// <summary>Characters of the token shown before the mask.</summary>
        public const int VisibleTokenChars = 4;

        /// <summary>The <c>claude mcp add</c> command for <paramref name="url"/> with the full token.</summary>
        public static string Build(string url, string token)
        {
            string command = "claude mcp add --transport http coreai " + (url ?? "");
            return string.IsNullOrEmpty(token)
                ? command
                : command + " --header \"Authorization: Bearer " + token + "\"";
        }

        /// <summary>The same command with the token cut to its first characters, for drawing on screen.</summary>
        public static string BuildMasked(string url, string token)
        {
            return Build(url, Mask(token));
        }

        /// <summary>The first <see cref="VisibleTokenChars"/> characters and <c>****</c>; empty for no token.</summary>
        public static string Mask(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return token;
            }

            return token.Length <= VisibleTokenChars * 2 ? "****" : token.Substring(0, VisibleTokenChars) + "****";
        }
    }
}
