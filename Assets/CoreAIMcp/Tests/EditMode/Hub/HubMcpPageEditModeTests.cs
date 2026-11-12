using CoreAI.Hub;
using CoreAI.Mcp.Hub;
using CoreAI.Mcp.Server;
using NUnit.Framework;

namespace CoreAI.Mcp.Hub.Tests
{
    /// <summary>
    /// The Hub MCP tab over a fake link. Its callbacks are called directly: a detached UI Toolkit element fires no
    /// <c>ChangeEvent</c>. The token is never drawn in clear unless asked, Copy hands over the full command, the
    /// switch starts and stops the link, and a WebGL player says why it links to a bridge.
    /// </summary>
    public sealed class HubMcpPageEditModeTests
    {
        private const string Token = "Qk3xSECRETtoken0123456789";

        [Test]
        public void Stopped_ShowsNoCommand_AndCopiesNothing()
        {
            FakeLink link = new();
            string copied = null;
            HubMcpPage page = Build(link, text => copied = text);

            Assert.AreEqual("Stopped", page.StateValue.text);
            Assert.AreEqual(HubMcpPage.StoppedCommandText, page.CommandLabel.text);
            Assert.IsNull(page.CopyCommand());
            Assert.IsNull(copied);
            Assert.IsFalse(page.RunningToggle.value);
        }

        [Test]
        public void RunningServer_MasksTheToken_AndCopyHandsOverTheFullCommand()
        {
            FakeLink link = Running("http", "http://127.0.0.1:8590/mcp", Token);
            string copied = null;
            HubMcpPage page = Build(link, text => copied = text);

            StringAssert.DoesNotContain(Token, page.CommandLabel.text, "the token is not drawn in clear by default");
            StringAssert.Contains("Bearer Qk3x****", page.CommandLabel.text);
            Assert.AreEqual(McpConnectCommand.Build(link.Url, Token), page.CopyCommand());
            Assert.AreEqual(McpConnectCommand.Build(link.Url, Token), copied);
            Assert.AreEqual("Running", page.StateValue.text);
            Assert.AreEqual("http://127.0.0.1:8590/mcp", page.AddressValue.text);
            Assert.AreEqual(HubMcpPage.ServerNote, page.NoteLabel.text);
            Assert.IsTrue(page.RunningToggle.value);
        }

        [Test]
        public void ShowToken_DrawsItInClear_AndHidesItAgain()
        {
            HubMcpPage page = Build(Running("http", "http://127.0.0.1:8590/mcp", Token), null);

            page.SetShowToken(true);
            StringAssert.Contains(Token, page.CommandLabel.text);

            page.SetShowToken(false);
            StringAssert.DoesNotContain(Token, page.CommandLabel.text);
        }

        [Test]
        public void Switch_StartsAndStopsTheLink()
        {
            FakeLink link = new();
            HubMcpPage page = Build(link, null);

            page.SetRunning(true);
            Assert.AreEqual(1, link.Starts);
            Assert.AreEqual("Running", page.StateValue.text);

            page.SetRunning(false);
            Assert.AreEqual(1, link.Stops);
            Assert.AreEqual("Stopped", page.StateValue.text);
        }

        [Test]
        public void Counters_AndLastError_AreShown()
        {
            FakeLink link = Running("http", "http://127.0.0.1:8590/mcp", Token);
            link.Requests = 12;
            link.ToolCalls = 5;
            link.ToolErrors = 1;
            link.Sessions = 2;
            link.LastError = "Tool 'x' failed: boom";
            HubMcpPage page = Build(link, null);

            Assert.AreEqual("12 requests, 5 tool calls, 1 failed", page.RequestsValue.text);
            Assert.AreEqual("2", page.SessionsValue.text);
            Assert.AreEqual("Tool 'x' failed: boom", page.LastErrorValue.text);
        }

        [Test]
        public void WebGl_Stopped_ExplainsTheBridge()
        {
            FakeLink link = new() { ListenerSupported = false };
            HubMcpPage page = Build(link, null);

            Assert.AreEqual(HubMcpPage.WebGlNote, page.NoteLabel.text);
            Assert.AreEqual("Bridge link when switched on", page.ModeValue.text);
        }

        [Test]
        public void Bridge_Running_HasNoTokenHeader_AndSaysWhereTheTokenIs()
        {
            FakeLink link = Running("bridge", "http://127.0.0.1:8591/mcp", null);
            link.ListenerSupported = false;
            HubMcpPage page = Build(link, null);

            Assert.AreEqual(McpConnectCommand.Build("http://127.0.0.1:8591/mcp", null), page.CommandLabel.text);
            Assert.AreEqual(HubMcpPage.BridgeNote, page.NoteLabel.text);
            Assert.AreEqual("Bridge link (outbound)", page.ModeValue.text);
        }

        [Test]
        public void BridgeFields_DefaultToLoopback_AndNothingIsRemembered()
        {
            FakeLink link = new();
            HubMcpPage page = Build(link, null);

            Assert.AreEqual("127.0.0.1", page.BridgeHostField.value);
            Assert.AreEqual("8591", page.BridgePortField.value);
            Assert.AreEqual("", page.BridgeKeyField.value);
            Assert.IsTrue(page.BridgeKeyField.isPasswordField, "the key is not drawn in clear");
            Assert.IsFalse(page.RememberToggle.value, "nothing is remembered unless the developer ticks it");
            Assert.IsTrue(page.HostAdminToggle.value);
        }

        [Test]
        public void ConnectBridge_LinksToTheTypedBridge_WithoutRemembering()
        {
            FakeLink link = new();
            HubMcpPage page = Build(link, null);
            page.BridgeHostField.value = "192.168.1.20";
            page.BridgePortField.value = "9000";
            page.BridgeKeyField.value = "key-0123456789abcdef";
            page.HostAdminToggle.value = false;

            Assert.IsTrue(page.ConnectBridge());

            Assert.AreEqual("192.168.1.20", link.StartedBridge.Host);
            Assert.AreEqual(9000, link.StartedBridge.Port);
            Assert.AreEqual("key-0123456789abcdef", link.StartedBridge.Key);
            Assert.IsFalse(link.StartedBridge.HostAdmin);
            Assert.IsNull(link.RememberedBridge);
            Assert.AreEqual("Bridge link (outbound)", page.ModeValue.text);
        }

        [Test]
        public void ConnectBridge_RemembersWhenTicked_AndUntickingForgets()
        {
            FakeLink link = new();
            HubMcpPage page = Build(link, null);
            page.BridgeKeyField.value = "key-0123456789abcdef";
            page.RememberToggle.value = true;

            page.ConnectBridge();
            Assert.AreEqual("key-0123456789abcdef", link.RememberedBridge.Key);

            page.SetRemember(false);
            Assert.IsNull(link.RememberedBridge);
        }

        [Test]
        public void RememberedBridge_FillsTheFields()
        {
            FakeLink link = new() { RememberedBridge = new McpBridgeEndpoint("10.0.0.5", 8600, "k-0123456789", false) };
            HubMcpPage page = Build(link, null);

            Assert.AreEqual("10.0.0.5", page.BridgeHostField.value);
            Assert.AreEqual("8600", page.BridgePortField.value);
            Assert.IsTrue(page.RememberToggle.value);
            Assert.IsFalse(page.HostAdminToggle.value);
        }

        [Test]
        public void ConnectBridge_RefusesABadPort()
        {
            FakeLink link = new();
            HubMcpPage page = Build(link, null);
            page.BridgePortField.value = "port";

            Assert.IsFalse(page.ConnectBridge());
            Assert.IsNull(link.StartedBridge);
            StringAssert.Contains("port", page.LastErrorValue.text);
        }

        [Test]
        public void Binder_RegistersThePageOnce()
        {
            HubPageRegistry registry = new();

            CoreAiMcpHubBinder.Register(registry);
            CoreAiMcpHubBinder.Register(registry);

            Assert.IsTrue(registry.TryGet(HubMcpPage.DefaultPageId, out System.Func<IHubPage> factory));
            Assert.IsInstanceOf<HubMcpPage>(factory());
        }

        private static HubMcpPage Build(FakeLink link, System.Action<string> clipboard)
        {
            HubMcpPage page = new(link, clipboard ?? (_ => { }));
            Assert.IsNotNull(page.CreatePageContent());
            return page;
        }

        private static FakeLink Running(string mode, string url, string token)
        {
            return new FakeLink { IsRunning = true, Mode = mode, Url = url, AuthToken = token };
        }

        private sealed class FakeLink : IMcpLinkControl
        {
            public bool IsRunning { get; set; }
            public bool ListenerSupported { get; set; } = true;
            public string Mode { get; set; } = "";
            public string Url { get; set; }
            public string AuthToken { get; set; }
            public int Sessions { get; set; }
            public long Requests { get; set; }
            public long ToolCalls { get; set; }
            public long ToolErrors { get; set; }
            public string LastError { get; set; }
            public bool HostAdminMods { get; set; }
            public int Starts { get; private set; }
            public int Stops { get; private set; }

            public void Start()
            {
                Starts++;
                IsRunning = true;
                Mode = "http";
                Url = "http://127.0.0.1:8590/mcp";
                AuthToken = Token;
            }

            public void Stop()
            {
                Stops++;
                IsRunning = false;
                Mode = "";
            }

            public McpBridgeEndpoint StartedBridge { get; private set; }

            public McpBridgeEndpoint RememberedBridge { get; set; }

            public int RememberCalls { get; private set; }

            public void StartBridge(McpBridgeEndpoint endpoint)
            {
                StartedBridge = endpoint;
                IsRunning = true;
                Mode = "bridge";
                Url = "http://127.0.0.1:" + endpoint.Port + "/mcp";
                AuthToken = null;
            }

            public void Remember(McpBridgeEndpoint endpoint)
            {
                RememberCalls++;
                RememberedBridge = endpoint;
            }
        }
    }
}
