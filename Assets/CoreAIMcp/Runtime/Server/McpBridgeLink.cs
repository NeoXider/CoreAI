using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Server
{
    /// <summary>The outcome of one HTTP exchange with the bridge: status 0 means the bridge was not reached.</summary>
    public sealed class McpBridgeHttpResponse
    {
        /// <param name="status">HTTP status; 0 when no response arrived.</param>
        /// <param name="body">Response body; may be empty.</param>
        /// <param name="error">Transport error text when <paramref name="status"/> is 0.</param>
        public McpBridgeHttpResponse(int status, string body, string error = null)
        {
            Status = status;
            Body = body ?? "";
            Error = error;
        }

        /// <summary>HTTP status; 0 when no response arrived.</summary>
        public int Status { get; }

        /// <summary>Response body.</summary>
        public string Body { get; }

        /// <summary>Transport error text, or null.</summary>
        public string Error { get; }
    }

    /// <summary>
    /// The HTTP the bridge link needs from its host: one JSON POST with a bearer key, answered through a callback on
    /// the game thread. Unity implements it with <c>UnityWebRequest</c>, which works in a WebGL player without a
    /// browser plugin; tests implement it with a fake bridge.
    /// </summary>
    public interface IMcpBridgeHttp
    {
        /// <summary>Posts <paramref name="json"/> to <paramref name="url"/> and calls <paramref name="done"/> once.</summary>
        void Post(string url, string json, string bearerKey, int timeoutSeconds, Action<McpBridgeHttpResponse> done);
    }

    /// <summary>
    /// The game side of the outbound MCP link for a player that cannot listen (WebGL). The game attaches to a local
    /// bridge process, long-polls it for the next MCP request, routes it through the same
    /// <see cref="McpRpcDispatcher"/> the loopback server uses, and posts the answer back. The bridge exposes the
    /// usual MCP endpoint to the client, so the client sees an ordinary MCP server.
    /// <para>
    /// WHY long polling over HTTP and not a WebSocket: it needs nothing but <c>UnityWebRequest</c>, which a WebGL
    /// player has without a JavaScript plugin, the same code runs (and is tested) in the editor, and the bridge stays
    /// a standard-library script. The cost is one request held open at a time, which a debugging session does not
    /// notice.
    /// </para>
    /// <para>
    /// Driven by <see cref="Tick"/> from the game loop and by the HTTP callbacks; it starts no thread and waits on
    /// nothing, so it runs on the single thread of a WebGL player. Retries after a refusal or an unreachable bridge
    /// are timed by <see cref="Tick"/>, not by timers.
    /// </para>
    /// </summary>
    public sealed class McpBridgeLink
    {
        /// <summary>HTTP timeout of one long poll; longer than the bridge holds a poll.</summary>
        public const int PollTimeoutSeconds = 30;

        /// <summary>HTTP timeout of attach and reply requests.</summary>
        public const int RequestTimeoutSeconds = 30;

        /// <summary>Seconds before another attempt after the bridge was not reached.</summary>
        public const double UnreachableRetrySeconds = 2d;

        /// <summary>Seconds before another attempt while another game holds the bridge.</summary>
        public const double BusyRetrySeconds = 3d;

        /// <summary>Seconds before another attempt after the bridge refused the key.</summary>
        public const double RefusedRetrySeconds = 5d;

        private readonly string _baseUrl;
        private readonly string _clientUrl;
        private readonly string _key;
        private readonly McpRpcDispatcher _dispatcher;
        private readonly IMcpBridgeHttp _http;
        private readonly JObject _gameInfo;
        private readonly Func<double> _clock;
        private readonly Action<string> _log;
        private CancellationTokenSource _lifetime;
        private bool _requestInFlight;
        private double _nextAttemptAt;
        private int _generation;
        private string _lastLogged;
        private long _served;

        /// <param name="bridgePort">Loopback port of the bridge.</param>
        /// <param name="key">The key the bridge expects from the game (from the page URL fragment); may be null.</param>
        /// <param name="dispatcher">The router that answers MCP requests.</param>
        /// <param name="http">The host's HTTP.</param>
        /// <param name="gameInfo">What the bridge shows about this game (name, platform, versions); may be null.</param>
        /// <param name="clock">Monotonic seconds; drives the retries.</param>
        /// <param name="log">Receives one line per change of link state; may be null.</param>
        public McpBridgeLink(int bridgePort, string key, McpRpcDispatcher dispatcher, IMcpBridgeHttp http,
            JObject gameInfo, Func<double> clock, Action<string> log = null)
            : this(null, bridgePort, key, dispatcher, http, gameInfo, clock, log)
        {
        }

        /// <param name="bridgeHost">
        /// The bridge's address: null or empty for <c>127.0.0.1</c> (WebGL, desktop, an Android phone behind
        /// <c>adb reverse</c>), or the LAN address of the developer's machine for a phone without adb. A host name,
        /// an IPv4 address or an IPv6 address; anything else is refused.
        /// </param>
        /// <param name="bridgePort">Port of the bridge.</param>
        /// <param name="key">The key the bridge expects from the game; may be null.</param>
        /// <param name="dispatcher">The router that answers MCP requests.</param>
        /// <param name="http">The host's HTTP.</param>
        /// <param name="gameInfo">What the bridge shows about this game; may be null.</param>
        /// <param name="clock">Monotonic seconds; drives the retries.</param>
        /// <param name="log">Receives one line per change of link state; may be null.</param>
        public McpBridgeLink(string bridgeHost, int bridgePort, string key, McpRpcDispatcher dispatcher,
            IMcpBridgeHttp http, JObject gameInfo, Func<double> clock, Action<string> log = null)
        {
            if (bridgePort <= 0 || bridgePort > 65535) throw new ArgumentOutOfRangeException(nameof(bridgePort));
            _baseUrl = "http://" + FormatHost(bridgeHost) + ":" + bridgePort;
            _clientUrl = "http://127.0.0.1:" + bridgePort + "/mcp";
            _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _gameInfo = gameInfo ?? new JObject();
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? (_ => { });
            Instance = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// The MCP endpoint a client connects to: the bridge's loopback endpoint on the developer's machine, also when
        /// the game reaches the bridge through a LAN address.
        /// </summary>
        public string Url => _clientUrl;

        /// <summary>Where the game reaches the bridge.</summary>
        public string BridgeUrl => _baseUrl;

        /// <summary>This game's id at the bridge; a new link (a page reload) gets a new one.</summary>
        public string Instance { get; }

        /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
        public bool IsStarted { get; private set; }

        /// <summary>True while the bridge holds this game as the attached one.</summary>
        public bool IsAttached { get; private set; }

        /// <summary>What is wrong with the link right now; null while attached.</summary>
        public string LastLinkError { get; private set; }

        /// <summary>MCP requests answered through the bridge.</summary>
        public long Served => Interlocked.Read(ref _served);

        /// <summary>Starts attaching on the next <see cref="Tick"/>.</summary>
        public void Start()
        {
            if (IsStarted) return;
            IsStarted = true;
            _generation++;
            _lifetime = new CancellationTokenSource();
            _nextAttemptAt = 0d;
            _requestInFlight = false;
            IsAttached = false;
        }

        /// <summary>Stops polling, cancels requests in progress and tells the bridge this game left.</summary>
        public void Stop()
        {
            if (!IsStarted) return;
            bool wasAttached = IsAttached;
            IsStarted = false;
            IsAttached = false;
            _generation++;
            CancellationTokenSource lifetime = _lifetime;
            _lifetime = null;
            try
            {
                lifetime?.Cancel();
            }
            finally
            {
                lifetime?.Dispose();
            }

            if (wasAttached)
            {
                _http.Post(_baseUrl + "/bridge/detach", Json(new JObject { ["instance"] = Instance }), _key,
                    RequestTimeoutSeconds, _ => { });
            }
        }

        /// <summary>Attaches or polls when no request is in flight and no retry is pending. Call every frame.</summary>
        public void Tick()
        {
            if (!IsStarted || _requestInFlight || _clock() < _nextAttemptAt)
            {
                return;
            }

            _requestInFlight = true;
            int generation = _generation;
            if (IsAttached)
            {
                _http.Post(_baseUrl + "/bridge/next", Json(new JObject { ["instance"] = Instance }), _key,
                    PollTimeoutSeconds, response => OnPolled(generation, response));
            }
            else
            {
                JObject attach = (JObject)_gameInfo.DeepClone();
                attach["instance"] = Instance;
                _http.Post(_baseUrl + "/bridge/attach", Json(attach), _key, RequestTimeoutSeconds,
                    response => OnAttached(generation, response));
            }
        }

        private void OnAttached(int generation, McpBridgeHttpResponse response)
        {
            if (generation != _generation) return;
            _requestInFlight = false;
            if (response.Status == 200)
            {
                IsAttached = true;
                LastLinkError = null;
                _nextAttemptAt = 0d;
                Note("[CoreAI MCP] Attached to the MCP bridge; clients connect to " + Url + ".");
                return;
            }

            Fail(response);
        }

        private void OnPolled(int generation, McpBridgeHttpResponse response)
        {
            if (generation != _generation) return;
            _requestInFlight = false;
            if (response.Status == 204)
            {
                _nextAttemptAt = 0d;
                return;
            }

            if (response.Status == 200)
            {
                _nextAttemptAt = 0d;
                JObject envelope;
                try
                {
                    envelope = JObject.Parse(response.Body);
                }
                catch (JsonException)
                {
                    return;
                }

                _ = ServeAsync(envelope, generation);
                return;
            }

            IsAttached = false;
            Fail(response);
        }

        private void Fail(McpBridgeHttpResponse response)
        {
            double now = _clock();
            switch (response.Status)
            {
                case 401:
                    LastLinkError = "The MCP bridge at " + _baseUrl + " refused this game's key: open the page with " +
                                    "#coreaiMcpKey=<the key the bridge printed> (or set COREAI_MCP_BRIDGE_KEY).";
                    _nextAttemptAt = now + RefusedRetrySeconds;
                    break;
                case 409:
                    LastLinkError = "Another game is attached to the MCP bridge" + DescribeAttached(response.Body) +
                                    ". Close it, or restart the bridge with --takeover.";
                    _nextAttemptAt = now + BusyRetrySeconds;
                    break;
                case 0:
                    LastLinkError = "The MCP bridge at " + _baseUrl + " is not reachable" +
                                    (string.IsNullOrEmpty(response.Error) ? "" : " (" + response.Error + ")") +
                                    "; start it with python tools/McpBridge/coreai_mcp_bridge.py (from a phone: " +
                                    "adb reverse for 127.0.0.1, or the bridge's --lan address).";
                    _nextAttemptAt = now + UnreachableRetrySeconds;
                    break;
                default:
                    LastLinkError = "The MCP bridge answered HTTP " + response.Status + ": " + Clip(response.Body);
                    _nextAttemptAt = now + UnreachableRetrySeconds;
                    break;
            }

            Note("[CoreAI MCP] " + LastLinkError);
        }

        private async Task ServeAsync(JObject envelope, int generation)
        {
            string id = (string)envelope["id"];
            if (string.IsNullOrEmpty(id)) return;
            JObject reply;
            try
            {
                reply = await AnswerAsync(envelope);
            }
            catch (Exception ex)
            {
                reply = Text(500, "The game failed to answer the request: " + ex.Message);
            }

            if (generation != _generation) return;
            reply["instance"] = Instance;
            reply["id"] = id;
            Interlocked.Increment(ref _served);
            _http.Post(_baseUrl + "/bridge/reply", Json(reply), _key, RequestTimeoutSeconds, _ => { });
        }

        // WHY no ConfigureAwait(false): in a WebGL player the continuation must come back through the game thread's
        // synchronization context; a context-free continuation would be handed to a thread pool that is not there.
        private async Task<JObject> AnswerAsync(JObject envelope)
        {
            string method = (string)envelope["method"] ?? "POST";
            string session = (string)envelope["session"];
            if (string.IsNullOrEmpty(session)) session = null;
            string protocol = (string)envelope["protocol"];
            if (string.IsNullOrEmpty(protocol)) protocol = null;

            if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                if (session == null) return Text(400, McpTransportRules.DeleteNeedsSessionMessage);
                _dispatcher.RemoveSession(session);
                return new JObject { ["status"] = 204 };
            }

            int rejected = McpTransportRules.Check(_dispatcher, session, protocol, out string rejection);
            if (rejected != 0) return Text(rejected, rejection);

            if (!JsonRpc.TryParse((string)envelope["body"], out JObject request, out JObject parseError))
            {
                return JsonReply(parseError, null);
            }

            CancellationToken token = _lifetime?.Token ?? new CancellationToken(true);
            McpDispatchResult result = await _dispatcher.DispatchAsync(request, session, token);
            return result.IsNotification
                ? new JObject { ["status"] = 202 }
                : JsonReply(result.Response, result.IssuedSessionId);
        }

        private void Note(string line)
        {
            if (string.Equals(line, _lastLogged, StringComparison.Ordinal)) return;
            _lastLogged = line;
            _log(line);
        }

        private static JObject JsonReply(JObject payload, string issuedSession)
        {
            JObject reply = new()
            {
                ["status"] = 200,
                ["content_type"] = "application/json",
                ["body"] = (payload ?? new JObject()).ToString(Formatting.None)
            };
            if (!string.IsNullOrEmpty(issuedSession)) reply["session"] = issuedSession;
            return reply;
        }

        private static JObject Text(int status, string message)
        {
            return new JObject
            {
                ["status"] = status,
                ["content_type"] = "text/plain; charset=utf-8",
                ["body"] = message ?? ""
            };
        }

        private static string DescribeAttached(string body)
        {
            try
            {
                JObject attached = JObject.Parse(body)["attached"] as JObject;
                if (attached == null) return "";
                return ": " + ((string)attached["name"] ?? "unnamed") + " (" + ((string)attached["platform"] ?? "?") +
                       ", instance " + ((string)attached["instance"] ?? "?") + ")";
            }
            catch (JsonException)
            {
                return "";
            }
        }

        private static string Clip(string text)
        {
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }

        private static string Json(JObject value) => value.ToString(Formatting.None);

        /// <summary>The URL authority for <paramref name="host"/>; throws for anything but a host name or address.</summary>
        internal static string FormatHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "127.0.0.1";
            string trimmed = host.Trim();
            string bare = trimmed.Trim('[', ']');
            if (bare.IndexOf(':') >= 0)
            {
                foreach (char c in bare)
                {
                    if (!Uri.IsHexDigit(c) && c != ':' && c != '.')
                        throw new ArgumentException("The bridge address '" + host + "' is not a host name or address.",
                            nameof(host));
                }

                return "[" + bare + "]";
            }

            if (Uri.CheckHostName(bare) == UriHostNameType.Unknown)
            {
                throw new ArgumentException("The bridge address '" + host + "' is not a host name or address.",
                    nameof(host));
            }

            return bare;
        }
    }
}
