using System;
using System.Collections.Generic;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Server;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// The outbound bridge link against a fake bridge: attach, long poll, the answer posted back through the same
    /// dispatcher the loopback server uses (sessions, protocol header, notifications, DELETE), and the link states a
    /// developer sees (bridge not running, key refused, another game attached, replaced), each with its retry
    /// delay counted on the game clock rather than a timer.
    /// </summary>
    public sealed class McpBridgeLinkEditModeTests
    {
        private const string Key = "game-key-0123456789";

        private FakeBridge _bridge;
        private McpRpcDispatcher _dispatcher;
        private double _now;
        private McpBridgeLink _link;
        private readonly List<string> _log = new();

        [SetUp]
        public void SetUp()
        {
            _log.Clear();
            _bridge = new FakeBridge();
            McpToolRegistry registry = new(new IMcpTool[] { new FakeMcpTool("echo") });
            _dispatcher = new McpRpcDispatcher(registry, new McpSessionStore(), new InlineMainThreadDispatcher());
            _now = 100d;
            _link = new McpBridgeLink(8591, Key, _dispatcher, _bridge, new JObject { ["name"] = "Block World" },
                () => _now, _log.Add);
        }

        [Test]
        public void Attach_CarriesTheKeyTheInstanceAndTheGame_ThenPolls()
        {
            _link.Start();
            _link.Tick();

            FakeBridge.Sent attach = _bridge.Take("/bridge/attach");
            Assert.AreEqual(Key, attach.Key);
            Assert.AreEqual(_link.Instance, (string)attach.Body["instance"]);
            Assert.AreEqual("Block World", (string)attach.Body["name"]);
            Assert.AreEqual("http://127.0.0.1:8591/mcp", _link.Url);

            attach.Answer(200, "{\"ok\":true}");
            Assert.IsTrue(_link.IsAttached);
            Assert.IsNull(_link.LastLinkError);
            _link.Tick();
            Assert.AreEqual(_link.Instance, (string)_bridge.Take("/bridge/next").Body["instance"]);
            _link.Tick();
            Assert.AreEqual(0, _bridge.Pending.Count, "one poll at a time");
        }

        [Test]
        public void Initialize_IsAnsweredByTheDispatcher_WithTheIssuedSession()
        {
            AttachAndPoll().Answer(200, Envelope("r1", "POST", null, null,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}"));

            JObject reply = _bridge.Take("/bridge/reply").Body;
            Assert.AreEqual("r1", (string)reply["id"]);
            Assert.AreEqual(_link.Instance, (string)reply["instance"]);
            Assert.AreEqual(200, (int)reply["status"]);
            string session = (string)reply["session"];
            Assert.IsTrue(_dispatcher.IsKnownSession(session));
            Assert.AreEqual(McpServerInfo.Name, (string)JObject.Parse((string)reply["body"])["result"]["serverInfo"]["name"]);
            Assert.AreEqual(1, _link.Served);
        }

        [Test]
        public void ToolCall_RunsTheTool_AndTheNextPollStartsAtOnce()
        {
            FakeBridge.Sent poll = AttachAndPoll();
            poll.Answer(200, Envelope("r2", "POST", null, null,
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"echo\":\"hi\"}}}"));

            JObject reply = _bridge.Take("/bridge/reply").Body;
            JObject rpc = JObject.Parse((string)reply["body"]);
            Assert.AreEqual("echo:hi", (string)rpc["result"]["content"][0]["text"]);
            _link.Tick();
            Assert.IsNotNull(_bridge.Take("/bridge/next"));
        }

        [Test]
        public void UnknownSession_Is404_AndUnsupportedProtocol_Is400_BeforeAnyRouting()
        {
            AttachAndPoll().Answer(200, Envelope("a", "POST", "gone", null, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"));
            JObject expired = _bridge.Take("/bridge/reply").Body;
            Assert.AreEqual(404, (int)expired["status"]);
            Assert.AreEqual(McpTransportRules.ExpiredSessionMessage, (string)expired["body"]);

            _link.Tick();
            _bridge.Take("/bridge/next").Answer(200,
                Envelope("b", "POST", null, "1999-01-01", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"));
            JObject version = _bridge.Take("/bridge/reply").Body;
            Assert.AreEqual(400, (int)version["status"]);
            StringAssert.Contains("Unsupported MCP-Protocol-Version", (string)version["body"]);
        }

        [Test]
        public void Notification_Is202_AndAMalformedBodyIsAParseError()
        {
            AttachAndPoll().Answer(200, Envelope("n", "POST", null, null,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
            Assert.AreEqual(202, (int)_bridge.Take("/bridge/reply").Body["status"]);

            _link.Tick();
            _bridge.Take("/bridge/next").Answer(200, Envelope("p", "POST", null, null, "{not json"));
            JObject parse = JObject.Parse((string)_bridge.Take("/bridge/reply").Body["body"]);
            Assert.AreEqual(JsonRpcErrorCodes.ParseError, (int)parse["error"]["code"]);
        }

        [Test]
        public void Delete_RemovesTheSession_AndNeedsOne()
        {
            AttachAndPoll().Answer(200, Envelope("i", "POST", null, null,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}"));
            string session = (string)_bridge.Take("/bridge/reply").Body["session"];

            _link.Tick();
            _bridge.Take("/bridge/next").Answer(200, Envelope("d", "DELETE", session, null, ""));
            Assert.AreEqual(204, (int)_bridge.Take("/bridge/reply").Body["status"]);
            Assert.IsFalse(_dispatcher.IsKnownSession(session));

            _link.Tick();
            _bridge.Take("/bridge/next").Answer(200, Envelope("e", "DELETE", null, null, ""));
            Assert.AreEqual(400, (int)_bridge.Take("/bridge/reply").Body["status"]);
        }

        [Test]
        public void BridgeNotRunning_SaysHowToStartIt_AndRetriesOnTheGameClock()
        {
            _link.Start();
            _link.Tick();
            _bridge.Take("/bridge/attach").Answer(0, "", "Cannot connect to destination host");

            StringAssert.Contains("not reachable", _link.LastLinkError);
            StringAssert.Contains("coreai_mcp_bridge.py", _link.LastLinkError);
            _link.Tick();
            Assert.AreEqual(0, _bridge.Pending.Count, "no retry before the delay");
            _now += McpBridgeLink.UnreachableRetrySeconds;
            _link.Tick();
            Assert.IsNotNull(_bridge.Take("/bridge/attach"));
            Assert.AreEqual(1, _log.Count, "a state is logged once, not every retry");
        }

        [Test]
        public void SecondGame_IsToldWhichGameHoldsTheBridge()
        {
            _link.Start();
            _link.Tick();
            _bridge.Take("/bridge/attach").Answer(409,
                "{\"ok\":false,\"attached\":{\"name\":\"Block World\",\"platform\":\"WebGLPlayer\",\"instance\":\"abc\"}}");

            Assert.IsFalse(_link.IsAttached);
            StringAssert.Contains("Block World (WebGLPlayer, instance abc)", _link.LastLinkError);
            StringAssert.Contains("--takeover", _link.LastLinkError);
            _now += McpBridgeLink.BusyRetrySeconds - 0.1;
            _link.Tick();
            Assert.AreEqual(0, _bridge.Pending.Count);
        }

        [Test]
        public void RefusedKey_PointsAtTheFragment()
        {
            _link.Start();
            _link.Tick();
            _bridge.Take("/bridge/attach").Answer(401, "Unauthorized");

            StringAssert.Contains("#coreaiMcpKey=", _link.LastLinkError);
        }

        [Test]
        public void ReplacedAtTheBridge_AttachesAgain()
        {
            AttachAndPoll().Answer(409, "{\"ok\":false}");

            Assert.IsFalse(_link.IsAttached);
            _now += McpBridgeLink.BusyRetrySeconds;
            _link.Tick();
            Assert.IsNotNull(_bridge.Take("/bridge/attach"));
        }

        [Test]
        public void Stop_Detaches_AndALateAnswerIsDropped()
        {
            FakeBridge.Sent poll = AttachAndPoll();

            _link.Stop();

            Assert.IsFalse(_link.IsStarted);
            Assert.AreEqual(_link.Instance, (string)_bridge.Take("/bridge/detach").Body["instance"]);
            poll.Answer(200, Envelope("late", "POST", null, null, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"));
            Assert.AreEqual(0, _bridge.Pending.Count, "a request from before the stop is not answered");
        }

        [Test]
        public void LanBridge_IsReachedOnItsAddress_WhileTheClientStaysOnLoopback()
        {
            McpBridgeLink lan = new("192.168.1.20", 8591, Key, _dispatcher, _bridge, null, () => _now);
            lan.Start();
            lan.Tick();

            StringAssert.StartsWith("http://192.168.1.20:8591/bridge/attach", _bridge.Take("/bridge/attach").Url);
            Assert.AreEqual("http://127.0.0.1:8591/mcp", lan.Url, "the client endpoint is the bridge's loopback one");
            Assert.AreEqual("http://192.168.1.20:8591", lan.BridgeUrl);
        }

        [TestCase(null, "127.0.0.1")]
        [TestCase(" ", "127.0.0.1")]
        [TestCase("dev-pc.local", "dev-pc.local")]
        [TestCase("::1", "[::1]")]
        [TestCase("[fe80::1]", "[fe80::1]")]
        public void BridgeAddress_IsFormattedForTheUrl(string host, string expected)
        {
            Assert.AreEqual(expected, McpBridgeLink.FormatHost(host));
        }

        [TestCase("evil.example/path")]
        [TestCase("user@host")]
        [TestCase("host:8591")]
        public void BridgeAddress_RefusesAnythingButAHost(string host)
        {
            Assert.Throws<ArgumentException>(() => McpBridgeLink.FormatHost(host));
        }

        private FakeBridge.Sent AttachAndPoll()
        {
            _link.Start();
            _link.Tick();
            _bridge.Take("/bridge/attach").Answer(200, "{\"ok\":true}");
            _link.Tick();
            return _bridge.Take("/bridge/next");
        }

        private static string Envelope(string id, string method, string session, string protocol, string body)
        {
            return new JObject
            {
                ["id"] = id,
                ["method"] = method,
                ["session"] = session,
                ["protocol"] = protocol,
                ["body"] = body
            }.ToString();
        }

        private sealed class FakeBridge : IMcpBridgeHttp
        {
            public readonly List<Sent> Pending = new();

            public void Post(string url, string json, string bearerKey, int timeoutSeconds,
                Action<McpBridgeHttpResponse> done)
            {
                Pending.Add(new Sent(url, JObject.Parse(json), bearerKey, done));
            }

            public Sent Take(string route)
            {
                Sent found = Pending.Find(post => post.Url.EndsWith(route, StringComparison.Ordinal));
                Assert.IsNotNull(found, "expected a POST to " + route);
                Pending.Remove(found);
                return found;
            }

            public sealed class Sent
            {
                private readonly Action<McpBridgeHttpResponse> _done;

                public Sent(string url, JObject body, string key, Action<McpBridgeHttpResponse> done)
                {
                    Url = url;
                    Body = body;
                    Key = key;
                    _done = done;
                }

                public string Url { get; }

                public JObject Body { get; }

                public string Key { get; }

                public void Answer(int status, string body, string error = null)
                {
                    _done(new McpBridgeHttpResponse(status, body, error));
                }
            }
        }
    }
}
