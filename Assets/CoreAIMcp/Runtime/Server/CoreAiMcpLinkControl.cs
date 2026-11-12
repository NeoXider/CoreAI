namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// <see cref="IMcpLinkControl"/> over the process's active <see cref="CoreAiMcpServer"/> (the one the static API,
    /// the debug start-up or a scene component started). Start uses the debug start-up's options for this process,
    /// so a page switch starts the same link a command-line switch would. Main thread only.
    /// </summary>
    public sealed class CoreAiMcpLinkControl : IMcpLinkControl
    {
        /// <summary>The control over this process's active server.</summary>
        public static CoreAiMcpLinkControl Shared { get; } = new();

        private static CoreAiMcpServer Server
        {
            get
            {
                CoreAiMcpServer server = CoreAiMcpServer.Active;
                return server != null && server.IsRunning ? server : null;
            }
        }

        /// <inheritdoc />
        public bool IsRunning => Server != null;

        /// <inheritdoc />
        public bool ListenerSupported => McpHttpServer.IsSupportedOnThisPlatform && !CoreAiMcpServer.IsWebGlPlayer;

        /// <inheritdoc />
        public string Mode => Server != null ? Server.TransportKind : "";

        /// <inheritdoc />
        public string Url => Server?.Url;

        /// <inheritdoc />
        public string AuthToken => Server?.AuthToken;

        /// <inheritdoc />
        public int Sessions => Server != null ? Server.SessionCount : 0;

        /// <inheritdoc />
        public long Requests => Server?.Dispatcher?.RequestCount ?? 0;

        /// <inheritdoc />
        public long ToolCalls => Server?.Dispatcher?.ToolCallCount ?? 0;

        /// <inheritdoc />
        public long ToolErrors => Server?.Dispatcher?.ToolErrorCount ?? 0;

        /// <inheritdoc />
        public string LastError
        {
            get
            {
                CoreAiMcpServer server = Server;
                string link = server?.LinkError;
                if (!string.IsNullOrEmpty(link)) return link;
                string tool = server?.Dispatcher?.LastError;
                return !string.IsNullOrEmpty(tool) ? tool : CoreAiMcpServer.LastStartError;
            }
        }

        /// <inheritdoc />
        public bool HostAdminMods => Server?.Registry != null && Server.Registry.Contains("manage_mods");

        /// <inheritdoc />
        public void Start()
        {
            CoreAiMcpDebugBootstrap.Start(CoreAiMcpDebugBootstrap.LastOptions ?? CoreAiMcpDebugBootstrap.ReadOptions());
        }

        /// <inheritdoc />
        public void Stop()
        {
            CoreAiMcpServer.StopServer();
        }

        /// <inheritdoc />
        public void StartBridge(McpBridgeEndpoint endpoint)
        {
            if (endpoint == null) throw new System.ArgumentNullException(nameof(endpoint));
            if (Server != null) CoreAiMcpServer.StopServer();
            CoreAiMcpDebugBootstrap.Start(CoreAiMcpDebugOptions.ForBridge(endpoint.Host, endpoint.Port, endpoint.Key,
                endpoint.HostAdmin));
        }

        /// <summary>PlayerPrefs key prefix of the remembered bridge.</summary>
        public const string PrefsPrefix = "CoreAI.Mcp.Bridge.";

        /// <inheritdoc />
        public McpBridgeEndpoint RememberedBridge
        {
            get
            {
                if (UnityEngine.PlayerPrefs.GetInt(PrefsPrefix + "Remember", 0) != 1)
                {
                    return null;
                }

                return new McpBridgeEndpoint(
                    UnityEngine.PlayerPrefs.GetString(PrefsPrefix + "Host", "127.0.0.1"),
                    UnityEngine.PlayerPrefs.GetInt(PrefsPrefix + "Port", CoreAiMcpDebugOptions.DefaultBridgePort),
                    UnityEngine.PlayerPrefs.GetString(PrefsPrefix + "Key", ""),
                    UnityEngine.PlayerPrefs.GetInt(PrefsPrefix + "HostAdmin", 1) == 1);
            }
        }

        /// <inheritdoc />
        public void Remember(McpBridgeEndpoint endpoint)
        {
            // WHY PlayerPrefs: the only per-device store every player has, phones and WebGL included. The key sits
            // there in plain text, which is why nothing is stored until the developer ticks the box.
            if (endpoint == null)
            {
                foreach (string name in new[] { "Remember", "Host", "Port", "Key", "HostAdmin" })
                {
                    UnityEngine.PlayerPrefs.DeleteKey(PrefsPrefix + name);
                }
            }
            else
            {
                UnityEngine.PlayerPrefs.SetInt(PrefsPrefix + "Remember", 1);
                UnityEngine.PlayerPrefs.SetString(PrefsPrefix + "Host", endpoint.Host);
                UnityEngine.PlayerPrefs.SetInt(PrefsPrefix + "Port", endpoint.Port);
                UnityEngine.PlayerPrefs.SetString(PrefsPrefix + "Key", endpoint.Key);
                UnityEngine.PlayerPrefs.SetInt(PrefsPrefix + "HostAdmin", endpoint.HostAdmin ? 1 : 0);
            }

            UnityEngine.PlayerPrefs.Save();
        }
    }
}
