using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mcp.Server
{
    /// <summary>How the debug start-up connects the game to an MCP client.</summary>
    public enum CoreAiMcpDebugMode
    {
        /// <summary>Nothing was asked for; the game starts no MCP link.</summary>
        Off = 0,

        /// <summary>The loopback HTTP listener inside the game (editor, desktop players).</summary>
        Server = 1,

        /// <summary>The outbound link to a local bridge process (WebGL players; desktop for testing the bridge).</summary>
        Bridge = 2
    }

    /// <summary>
    /// What the MCP debug start-up was asked to do, read from a player or editor command line, from environment
    /// variables and from a WebGL page URL. Off unless one of them asks. Sources apply in order - environment, URL,
    /// command line - so the command line wins. Engine-free.
    /// <list type="bullet">
    /// <item><c>-coreaiMcp [port]</c>, <c>COREAI_MCP=1|port</c>: start the in-game server (port 8590).</item>
    /// <item><c>-coreaiMcpBridge [port]</c>, <c>COREAI_MCP_BRIDGE=port</c>, page URL <c>?coreaiMcp=port</c>: link to the
    /// local bridge on that port (8591). The bridge key comes from the URL fragment <c>#coreaiMcpKey=...</c> or
    /// <c>COREAI_MCP_BRIDGE_KEY</c>, never from the query string.</item>
    /// <item><c>-coreaiMcpNoAdmin</c>, <c>COREAI_MCP_ADMIN=0</c>, URL <c>coreaiMcpNoAdmin</c>: do not grant
    /// <c>manage_mods</c> / <c>save_world</c>. The debug start-up grants them by default.</item>
    /// </list>
    /// </summary>
    public sealed class CoreAiMcpDebugOptions
    {
        /// <summary>Command-line switch that starts the in-game server; its optional value is the port.</summary>
        public const string ServerSwitch = "-coreaiMcp";

        /// <summary>Command-line switch that starts the bridge link; its optional value is the bridge port.</summary>
        public const string BridgeSwitch = "-coreaiMcpBridge";

        /// <summary>Command-line switch that withholds host-admin mod management.</summary>
        public const string NoAdminSwitch = "-coreaiMcpNoAdmin";

        /// <summary>Environment variable that starts the in-game server (<c>1</c>, <c>true</c> or a port).</summary>
        public const string ServerVariable = "COREAI_MCP";

        /// <summary>Environment variable that starts the bridge link (a port, or <c>1</c> for the default).</summary>
        public const string BridgeVariable = "COREAI_MCP_BRIDGE";

        /// <summary>Environment variable holding the bridge key on a desktop player.</summary>
        public const string BridgeKeyVariable = "COREAI_MCP_BRIDGE_KEY";

        /// <summary>Environment variable that withholds host-admin mod management when <c>0</c> or <c>false</c>.</summary>
        public const string AdminVariable = "COREAI_MCP_ADMIN";

        /// <summary>URL query key (bridge port) and fragment key (bridge key) of a WebGL page.</summary>
        public const string UrlKey = "coreaiMcp";

        /// <summary>URL fragment key carrying the bridge key.</summary>
        public const string UrlBridgeKey = "coreaiMcpKey";

        /// <summary>URL query key that withholds host-admin mod management.</summary>
        public const string UrlNoAdminKey = "coreaiMcpNoAdmin";

        /// <summary>Port of the in-game server when none is given.</summary>
        public const int DefaultServerPort = 8590;

        /// <summary>Port of the bridge when none is given.</summary>
        public const int DefaultBridgePort = 8591;

        /// <summary>What to start.</summary>
        public CoreAiMcpDebugMode Mode { get; private set; }

        /// <summary>True when any source asked for an MCP link.</summary>
        public bool Requested => Mode != CoreAiMcpDebugMode.Off;

        /// <summary>The server port (<see cref="CoreAiMcpDebugMode.Server"/>) or the bridge port.</summary>
        public int Port { get; private set; }

        /// <summary>Grant <c>manage_mods</c> and <c>save_world</c> to the composition's actor (default true).</summary>
        public bool HostAdmin { get; private set; } = true;

        /// <summary>The key the bridge expects from the game; null when none was given.</summary>
        public string BridgeKey { get; private set; }

        /// <summary>True when a bridge key was found in the query string, where it is ignored.</summary>
        public bool BridgeKeyInQueryIgnored { get; private set; }

        /// <summary>True when the command line asked (an editor in batch mode honours only this source).</summary>
        public bool RequestedOnCommandLine { get; private set; }

        /// <summary>Which source decided <see cref="Mode"/>: <c>command line</c>, <c>url</c> or <c>environment</c>.</summary>
        public string Source { get; private set; } = "";

        /// <summary>
        /// Whether a start-up should start a link: always for a command-line switch; in an editor batch run (the test
        /// runner) only then, so an inherited environment variable or the editor toggle never binds a port during
        /// automated tests; otherwise when any source asked or the editor toggle is on.
        /// </summary>
        public bool ShouldStart(bool isEditor, bool isBatchMode, bool editorToggle)
        {
            if (RequestedOnCommandLine)
            {
                return true;
            }

            if (isEditor && isBatchMode)
            {
                return false;
            }

            return Requested || (isEditor && editorToggle);
        }

        /// <summary>Reads the options from the three sources; any of them may be null.</summary>
        /// <param name="commandLine">The process arguments.</param>
        /// <param name="environment">Environment lookup.</param>
        /// <param name="url">The page URL of a WebGL player.</param>
        public static CoreAiMcpDebugOptions Read(IReadOnlyList<string> commandLine, Func<string, string> environment,
            string url)
        {
            CoreAiMcpDebugOptions options = new();
            if (environment != null) options.ApplyEnvironment(environment);
            if (!string.IsNullOrEmpty(url)) options.ApplyUrl(url);
            if (commandLine != null) options.ApplyCommandLine(commandLine);
            return options;
        }

        private void ApplyEnvironment(Func<string, string> environment)
        {
            string server = environment(ServerVariable);
            if (IsOn(server))
            {
                Set(CoreAiMcpDebugMode.Server, ParsePort(server, DefaultServerPort), "environment");
            }

            string bridge = environment(BridgeVariable);
            if (IsOn(bridge))
            {
                Set(CoreAiMcpDebugMode.Bridge, ParsePort(bridge, DefaultBridgePort), "environment");
            }

            string key = environment(BridgeKeyVariable);
            if (!string.IsNullOrWhiteSpace(key)) BridgeKey = key.Trim();
            if (IsOff(environment(AdminVariable))) HostAdmin = false;
        }

        private void ApplyUrl(string url)
        {
            int hash = url.IndexOf('#');
            int question = url.IndexOf('?');
            if (question >= 0 && (hash < 0 || question < hash))
            {
                string query = hash < 0 ? url.Substring(question + 1) : url.Substring(question + 1, hash - question - 1);
                foreach (KeyValuePair<string, string> pair in Pairs(query))
                {
                    if (pair.Key == UrlKey)
                        Set(CoreAiMcpDebugMode.Bridge, ParsePort(pair.Value, DefaultBridgePort), "url");
                    else if (pair.Key == UrlNoAdminKey) HostAdmin = false;
                    else if (pair.Key == UrlBridgeKey) BridgeKeyInQueryIgnored = true;
                }
            }

            if (hash >= 0)
            {
                foreach (KeyValuePair<string, string> pair in Pairs(url.Substring(hash + 1)))
                {
                    if (pair.Key == UrlBridgeKey && pair.Value.Length > 0) BridgeKey = pair.Value;
                }
            }
        }

        private void ApplyCommandLine(IReadOnlyList<string> commandLine)
        {
            for (int index = 0; index < commandLine.Count; index++)
            {
                string argument = commandLine[index];
                string value = index + 1 < commandLine.Count && commandLine[index + 1] != null &&
                               !commandLine[index + 1].StartsWith("-", StringComparison.Ordinal)
                    ? commandLine[index + 1]
                    : "";
                if (string.Equals(argument, ServerSwitch, StringComparison.Ordinal))
                {
                    Set(CoreAiMcpDebugMode.Server, ParsePort(value, DefaultServerPort), "command line");
                    RequestedOnCommandLine = true;
                }
                else if (string.Equals(argument, BridgeSwitch, StringComparison.Ordinal))
                {
                    Set(CoreAiMcpDebugMode.Bridge, ParsePort(value, DefaultBridgePort), "command line");
                    RequestedOnCommandLine = true;
                }
                else if (string.Equals(argument, NoAdminSwitch, StringComparison.Ordinal))
                {
                    HostAdmin = false;
                }
            }
        }

        private void Set(CoreAiMcpDebugMode mode, int port, string source)
        {
            Mode = mode;
            Port = port;
            Source = source;
        }

        private static IEnumerable<KeyValuePair<string, string>> Pairs(string text)
        {
            foreach (string part in text.Split('&'))
            {
                if (part.Length == 0) continue;
                int equals = part.IndexOf('=');
                string key = equals < 0 ? part : part.Substring(0, equals);
                string value = equals < 0 ? "" : part.Substring(equals + 1);
                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(value.Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    decoded = value;
                }

                yield return new KeyValuePair<string, string>(key, decoded.Trim());
            }
        }

        private static bool IsOn(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !IsOff(value);
        }

        private static bool IsOff(string value)
        {
            if (value == null) return false;
            string trimmed = value.Trim();
            return trimmed == "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParsePort(string value, int fallback)
        {
            return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) &&
                   port > 1 && port <= 65535
                ? port
                : fallback;
        }
    }
}
