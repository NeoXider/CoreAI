using System;
using System.Globalization;
using CoreAI.Hub;
using CoreAI.Mcp.Server;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Mcp.Hub
{
    /// <summary>
    /// Hub tab for the game's MCP link: whether the in-game server or the bridge link runs, the address a client
    /// connects to, sessions and requests served, the last error, a switch that starts and stops the link, and the
    /// client command with the token masked (Copy puts the full command on the clipboard; the token is drawn in
    /// clear only after "Show token"). In a WebGL player it explains why the link is the bridge.
    /// </summary>
    public sealed class HubMcpPage : HubPageBase
    {
        /// <summary>Registry id of the MCP page.</summary>
        public const string DefaultPageId = "coreai.hub.mcp";

        /// <summary>Tab order: after the built-in pages, before About.</summary>
        public const int DefaultOrder = 900;

        /// <summary>Text shown while nothing runs.</summary>
        public const string StoppedCommandText = "Switch the link on to get the client command.";

        /// <summary>Why a WebGL player links to a bridge instead of listening.</summary>
        public const string WebGlNote =
            "A browser page cannot listen on a port, so this game links out to a local bridge: run " +
            "python tools/McpBridge/coreai_mcp_bridge.py, open the page with ?coreaiMcp=8591 and the key the " +
            "bridge prints after #coreaiMcpKey=, and connect the client to the bridge.";

        /// <summary>Shown in bridge mode, where the game does not hold the client's token.</summary>
        public const string BridgeNote =
            "Linked through the bridge: the client connects to the bridge's address with the token the bridge " +
            "printed when it started.";

        /// <summary>Shown while the in-game server runs.</summary>
        public const string ServerNote =
            "Whoever holds this token controls the game (Lua, mods, screenshots). Never share it or forward the port.";

        /// <summary>How a phone reaches the bridge on the developer's machine.</summary>
        public const string PhoneNote =
            "Phone: start the bridge on your computer. Android with USB debugging: run adb reverse tcp:8591 tcp:8591 " +
            "and keep 127.0.0.1. iOS, or Android without adb: start the bridge with --lan <your computer's address> " +
            "and type that address. Then type the key the bridge printed and connect.";

        private const long RefreshMilliseconds = 500;

        private static readonly Color Accent = new(0.302f, 0.816f, 0.882f, 1f);
        private static readonly Color Text = new(0.863f, 0.91f, 0.941f, 1f);
        private static readonly Color Muted = new(0.77f, 0.86f, 0.91f, 0.7f);
        private static readonly Color ErrorColor = new(1f, 0.55f, 0.5f, 1f);

        private readonly IMcpLinkControl _control;
        private readonly Action<string> _copyToClipboard;
        private bool _showToken;

        /// <summary>Creates the page.</summary>
        /// <param name="control">The link to show and switch; null uses <see cref="CoreAiMcpLinkControl.Shared"/>.</param>
        /// <param name="copyToClipboard">Clipboard writer; null writes <c>GUIUtility.systemCopyBuffer</c>.</param>
        /// <param name="pageId">Registry id.</param>
        /// <param name="displayName">Tab title.</param>
        /// <param name="order">Tab order.</param>
        public HubMcpPage(IMcpLinkControl control = null, Action<string> copyToClipboard = null,
            string pageId = DefaultPageId, string displayName = "MCP", int order = DefaultOrder)
            : base(pageId, displayName, order)
        {
            _control = control ?? CoreAiMcpLinkControl.Shared;
            _copyToClipboard = copyToClipboard ?? (text => GUIUtility.systemCopyBuffer = text);
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        internal Label StateValue { get; private set; }

        internal Label ModeValue { get; private set; }

        internal Label AddressValue { get; private set; }

        internal Label SessionsValue { get; private set; }

        internal Label RequestsValue { get; private set; }

        internal Label ModsValue { get; private set; }

        internal Label LastErrorValue { get; private set; }

        internal Toggle RunningToggle { get; private set; }

        internal Toggle ShowTokenToggle { get; private set; }

        internal Label CommandLabel { get; private set; }

        internal Button CopyButton { get; private set; }

        internal Label NoteLabel { get; private set; }

        internal TextField BridgeHostField { get; private set; }

        internal TextField BridgePortField { get; private set; }

        internal TextField BridgeKeyField { get; private set; }

        internal Toggle HostAdminToggle { get; private set; }

        internal Toggle RememberToggle { get; private set; }

        internal Button ConnectBridgeButton { get; private set; }

        /// <inheritdoc />
        public override void OnActivated()
        {
            Refresh();
        }

        /// <summary>
        /// Links to the bridge typed in the bridge fields, replacing a running link; remembers it on this device when
        /// "Remember" is ticked and forgets any remembered one otherwise. Returns false (and says why) for a bad port.
        /// </summary>
        internal bool ConnectBridge()
        {
            if (!int.TryParse(BridgePortField.value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int bridgePort) || bridgePort <= 1 || bridgePort > 65535)
            {
                LastErrorValue.text = "The bridge port must be a number from 2 to 65535.";
                LastErrorValue.style.color = ErrorColor;
                return false;
            }

            McpBridgeEndpoint endpoint = new(BridgeHostField.value, bridgePort, BridgeKeyField.value,
                HostAdminToggle.value);
            try
            {
                _control.Remember(RememberToggle.value ? endpoint : null);
                _control.StartBridge(endpoint);
            }
            catch (Exception ex)
            {
                LastErrorValue.text = ex.Message;
                LastErrorValue.style.color = ErrorColor;
                return false;
            }

            Refresh();
            return true;
        }

        /// <summary>Unticking forgets the remembered bridge at once; ticking takes effect on the next Connect.</summary>
        internal void SetRemember(bool remember)
        {
            if (!remember)
            {
                _control.Remember(null);
            }
        }

        /// <summary>Starts or stops the link, then shows the result.</summary>
        internal void SetRunning(bool running)
        {
            try
            {
                if (running && !_control.IsRunning) _control.Start();
                else if (!running && _control.IsRunning) _control.Stop();
            }
            catch (Exception ex)
            {
                if (LastErrorValue != null) LastErrorValue.text = ex.Message;
            }

            Refresh();
        }

        /// <summary>Draws the token in clear (true) or masked (false, the default).</summary>
        internal void SetShowToken(bool show)
        {
            _showToken = show;
            Refresh();
        }

        /// <summary>Copies the full client command and returns it; null while nothing runs.</summary>
        internal string CopyCommand()
        {
            if (!_control.IsRunning)
            {
                return null;
            }

            string command = FullCommand();
            _copyToClipboard(command);
            return command;
        }

        /// <summary>Reads the link and updates every control.</summary>
        internal void Refresh()
        {
            if (StateValue == null)
            {
                return;
            }

            bool running = _control.IsRunning;
            bool bridge = string.Equals(_control.Mode, "bridge", StringComparison.Ordinal);
            StateValue.text = running ? "Running" : "Stopped";
            StateValue.style.color = running ? Accent : Muted;
            ModeValue.text = running
                ? bridge ? "Bridge link (outbound)" : "In-game server (loopback HTTP)"
                : _control.ListenerSupported ? "In-game server when switched on" : "Bridge link when switched on";
            AddressValue.text = running ? _control.Url ?? "-" : "-";
            SessionsValue.text = running ? _control.Sessions.ToString(CultureInfo.InvariantCulture) : "-";
            RequestsValue.text = running
                ? _control.Requests.ToString(CultureInfo.InvariantCulture) + " requests, " +
                  _control.ToolCalls.ToString(CultureInfo.InvariantCulture) + " tool calls, " +
                  _control.ToolErrors.ToString(CultureInfo.InvariantCulture) + " failed"
                : "-";
            ModsValue.text = running ? (_control.HostAdminMods ? "manage_mods granted" : "read-only (no manage_mods)") : "-";
            string error = _control.LastError;
            LastErrorValue.text = string.IsNullOrEmpty(error) ? "none" : error;
            LastErrorValue.style.color = string.IsNullOrEmpty(error) ? Muted : ErrorColor;
            RunningToggle.SetValueWithoutNotify(running);

            string token = _control.AuthToken;
            ShowTokenToggle.SetEnabled(running && !string.IsNullOrEmpty(token));
            CopyButton.SetEnabled(running);
            CommandLabel.text = !running
                ? StoppedCommandText
                : _showToken ? FullCommand() : McpConnectCommand.BuildMasked(_control.Url, token);
            NoteLabel.text = !_control.ListenerSupported && !running
                ? WebGlNote
                : bridge ? BridgeNote : running ? ServerNote : "";
        }

        private string FullCommand()
        {
            return McpConnectCommand.Build(_control.Url, _control.AuthToken);
        }

        private object BuildContent()
        {
            ScrollView scroll = new(ScrollViewMode.Vertical) { name = "coreai-hub-mcp" };
            scroll.style.flexGrow = 1f;
            VisualElement body = scroll.contentContainer;

            body.Add(Title("MCP"));
            RunningToggle = new Toggle("MCP link on") { name = "coreai-hub-mcp-running" };
            RunningToggle.RegisterValueChangedCallback(evt => SetRunning(evt.newValue));
            body.Add(RunningToggle);

            body.Add(Section("Link"));
            body.Add(Row("State", out Label state));
            StateValue = state;
            body.Add(Row("Mode", out Label mode));
            ModeValue = mode;
            body.Add(Row("Address", out Label address));
            AddressValue = address;
            body.Add(Row("Sessions", out Label sessions));
            SessionsValue = sessions;
            body.Add(Row("Served", out Label requests));
            RequestsValue = requests;
            body.Add(Row("Mods", out Label mods));
            ModsValue = mods;
            body.Add(Row("Last error", out Label lastError));
            LastErrorValue = lastError;

            body.Add(Section("Connect a client"));
            CommandLabel = new Label { name = "coreai-hub-mcp-command" };
            CommandLabel.style.color = Text;
            CommandLabel.style.fontSize = 13f;
            CommandLabel.style.whiteSpace = WhiteSpace.Normal;
            CommandLabel.selection.isSelectable = true;
            body.Add(CommandLabel);

            VisualElement actions = new();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 6f;
            CopyButton = new Button(() => CopyCommand()) { text = "Copy command" };
            CopyButton.AddToClassList("coreai-hub-action-button");
            actions.Add(CopyButton);
            ShowTokenToggle = new Toggle("Show token") { name = "coreai-hub-mcp-show-token" };
            ShowTokenToggle.style.marginLeft = 12f;
            ShowTokenToggle.RegisterValueChangedCallback(evt => SetShowToken(evt.newValue));
            actions.Add(ShowTokenToggle);
            body.Add(actions);

            NoteLabel = new Label { name = "coreai-hub-mcp-note" };
            NoteLabel.style.color = Muted;
            NoteLabel.style.fontSize = 12f;
            NoteLabel.style.marginTop = 12f;
            NoteLabel.style.whiteSpace = WhiteSpace.Normal;
            body.Add(NoteLabel);

            BuildBridgeSection(body);

            // WHY scheduled on the body: it ticks only while the page is on a panel, so a closed Hub costs nothing.
            body.schedule.Execute(Refresh).Every(RefreshMilliseconds);
            Refresh();
            return scroll;
        }

        private void BuildBridgeSection(VisualElement body)
        {
            McpBridgeEndpoint remembered = _control.RememberedBridge;
            body.Add(Section("Bridge link (phones, WebGL)"));
            BridgeHostField = new TextField("Bridge address") { name = "coreai-hub-mcp-bridge-host" };
            BridgeHostField.SetValueWithoutNotify(remembered?.Host ?? "127.0.0.1");
            body.Add(BridgeHostField);
            BridgePortField = new TextField("Port") { name = "coreai-hub-mcp-bridge-port" };
            BridgePortField.SetValueWithoutNotify((remembered?.Port ?? CoreAiMcpDebugOptions.DefaultBridgePort)
                .ToString(CultureInfo.InvariantCulture));
            body.Add(BridgePortField);
            BridgeKeyField = new TextField("Bridge key") { name = "coreai-hub-mcp-bridge-key", isPasswordField = true };
            BridgeKeyField.SetValueWithoutNotify(remembered?.Key ?? "");
            body.Add(BridgeKeyField);
            HostAdminToggle = new Toggle("Allow mod management (manage_mods)") { name = "coreai-hub-mcp-bridge-admin" };
            HostAdminToggle.SetValueWithoutNotify(remembered?.HostAdmin ?? true);
            body.Add(HostAdminToggle);
            RememberToggle = new Toggle("Remember on this device and reconnect at launch")
            {
                name = "coreai-hub-mcp-bridge-remember"
            };
            RememberToggle.SetValueWithoutNotify(remembered != null);
            RememberToggle.RegisterValueChangedCallback(evt => SetRemember(evt.newValue));
            body.Add(RememberToggle);
            ConnectBridgeButton = new Button(() => ConnectBridge()) { text = "Connect to bridge" };
            ConnectBridgeButton.AddToClassList("coreai-hub-action-button");
            ConnectBridgeButton.style.marginTop = 6f;
            body.Add(ConnectBridgeButton);
            Label hint = new(PhoneNote) { name = "coreai-hub-mcp-phone-note" };
            hint.style.color = Muted;
            hint.style.fontSize = 12f;
            hint.style.marginTop = 6f;
            hint.style.whiteSpace = WhiteSpace.Normal;
            body.Add(hint);
        }

        private static Label Title(string text)
        {
            Label title = new(text);
            title.style.color = Accent;
            title.style.fontSize = 22f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 12f;
            return title;
        }

        private static Label Section(string text)
        {
            Label section = new(text);
            section.style.color = Accent;
            section.style.fontSize = 16f;
            section.style.unityFontStyleAndWeight = FontStyle.Bold;
            section.style.marginTop = 14f;
            section.style.marginBottom = 6f;
            return section;
        }

        private static VisualElement Row(string label, out Label value)
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 3f;
            Label key = new(label);
            key.style.color = Muted;
            key.style.fontSize = 14f;
            value = new Label("-");
            value.style.color = Text;
            value.style.fontSize = 14f;
            value.style.marginLeft = 12f;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            value.style.whiteSpace = WhiteSpace.Normal;
            value.style.flexShrink = 1f;
            row.Add(key);
            row.Add(value);
            return row;
        }
    }
}
