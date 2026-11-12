using System;
using CoreAI.Diagnostics;
using CoreAI.Logging;
using UnityEngine;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Starts an MCP link for debugging without touching any scene, when a command-line switch, an environment
    /// variable or a WebGL page URL asks for it (<see cref="CoreAiMcpDebugOptions"/>), or when the editor menu
    /// <c>CoreAI/MCP/Debug Server In Play Mode</c> is ticked. Off by default: a build started without any of them
    /// starts nothing. The Unity log capture starts before the first scene, so start-up errors are kept.
    /// <para>
    /// In the editor, a batch-mode run (the test runner) honours only an explicit command-line switch, so an
    /// inherited environment variable or the menu toggle never binds the port during automated tests.
    /// </para>
    /// </summary>
    public static class CoreAiMcpDebugBootstrap
    {
        /// <summary>EditorPrefs key of the editor menu toggle.</summary>
        public const string EditorToggleKey = "CoreAI.Mcp.DebugServerInPlayMode";

        /// <summary>The options the last start-up read; null before the first play session.</summary>
        public static CoreAiMcpDebugOptions LastOptions { get; private set; }

        /// <summary>Reads the options from this process: command line, environment and, in WebGL, the page URL.</summary>
        public static CoreAiMcpDebugOptions ReadOptions()
        {
            string url = Application.platform == RuntimePlatform.WebGLPlayer ? Application.absoluteURL : null;
            return CoreAiMcpDebugOptions.Read(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable,
                url);
        }

        /// <summary>
        /// Starts the link <paramref name="options"/> asks for and returns its host, or null when nothing was
        /// started. A <see cref="CoreAiMcpDebugMode.Off"/> request starts the in-game server with the defaults (the
        /// editor toggle). Main thread only.
        /// </summary>
        public static CoreAiMcpServer Start(CoreAiMcpDebugOptions options)
        {
            options ??= CoreAiMcpDebugOptions.Read(null, null, null);
            UnityRuntimeLogCapture.Shared.EnsureStarted();
            UnityRuntimePerformanceMonitor.Shared.EnsureStarted();
            if (options.BridgeKeyInQueryIgnored)
            {
                Log.Instance.Warn("[CoreAI MCP] The bridge key was found in the URL query and ignored: pass it in the " +
                                  "fragment (#" + CoreAiMcpDebugOptions.UrlBridgeKey + "=...) so the page server " +
                                  "never receives it.");
            }

            bool webGl = Application.platform == RuntimePlatform.WebGLPlayer;
            if (options.Mode == CoreAiMcpDebugMode.Bridge || webGl)
            {
                int bridgePort = options.Mode == CoreAiMcpDebugMode.Bridge
                    ? options.Port
                    : CoreAiMcpDebugOptions.DefaultBridgePort;
                return CoreAiMcpServer.StartDebugBridge(bridgePort, options.BridgeKey, options.HostAdmin,
                    options.BridgeHost);
            }

            int serverPort = options.Mode == CoreAiMcpDebugMode.Server
                ? options.Port
                : CoreAiMcpDebugOptions.DefaultServerPort;
            return CoreAiMcpServer.StartDebugServer(serverPort, options.HostAdmin, options.PrintToken);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CaptureEarly()
        {
            LastOptions = ReadOptions();
            if (LastOptions.ShouldStart(Application.isEditor, Application.isBatchMode, ReadEditorToggle()))
            {
                UnityRuntimeLogCapture.Shared.EnsureStarted();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartWhenRequested()
        {
            CoreAiMcpDebugOptions options = LastOptions ?? ReadOptions();
            if (!options.ShouldStart(Application.isEditor, Application.isBatchMode, ReadEditorToggle()))
            {
                // WHY: a phone has no command line; a bridge the developer chose to remember in the Hub MCP tab is
                // the only switch it has, and only when nothing else was asked and this is not an automated run.
                McpBridgeEndpoint remembered = Application.isEditor || Application.isBatchMode
                    ? null
                    : CoreAiMcpLinkControl.Shared.RememberedBridge;
                if (remembered == null)
                {
                    return;
                }

                options = CoreAiMcpDebugOptions.ForBridge(remembered.Host, remembered.Port, remembered.Key,
                    remembered.HostAdmin);
            }

            try
            {
                CoreAiMcpServer server = Start(options);
                Log.Instance.Info("[CoreAI MCP] Debug start-up (" +
                                  (options.Requested ? options.Source == "hub" ? "remembered bridge" : options.Source
                                      : "editor menu") + "): " +
                                  (server != null && server.IsRunning ? server.TransportKind + " " + server.Url : "not running"));
            }
            catch (Exception ex)
            {
                Log.Instance.Error("[CoreAI MCP] Debug start-up failed: " + ex.Message);
            }
        }

        private static bool ReadEditorToggle()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorPrefs.GetBool(EditorToggleKey, false);
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        private const string MenuPath = "CoreAI/MCP/Debug Server In Play Mode";

        [UnityEditor.MenuItem(MenuPath, false, 400)]
        private static void ToggleEditorServer()
        {
            bool enabled = !UnityEditor.EditorPrefs.GetBool(EditorToggleKey, false);
            UnityEditor.EditorPrefs.SetBool(EditorToggleKey, enabled);
            if (Application.isPlaying)
            {
                if (enabled) Start(CoreAiMcpDebugOptions.Read(null, null, null));
                else CoreAiMcpServer.StopServer();
            }
        }

        [UnityEditor.MenuItem(MenuPath, true)]
        private static bool ToggleEditorServerValidate()
        {
            UnityEditor.Menu.SetChecked(MenuPath, UnityEditor.EditorPrefs.GetBool(EditorToggleKey, false));
            return true;
        }
#endif
    }
}
