using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Diagnostics;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Mcp.Tools;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Opt-in entry-point component that runs the CoreAI MCP server inside a live game session. Drop it
    /// into a scene (or call <see cref="StartServer"/>) so an external agent - Claude Code, Codex,
    /// opencode, LM Studio, or any MCP client - can drive the running game over localhost.
    /// <para>
    /// WHY (security): OFF by default (nothing starts until this component is added or <c>startOnEnable</c>
    /// is set), loopback-only, and - unless you turn it off - protected by a bearer token printed to the
    /// console at start. Loopback alone is NOT enough: a web page can POST to 127.0.0.1 cross-origin, and
    /// any other local process can call the port. Never forward the port off-box.
    /// </para>
    /// This component is the <see cref="IMainThreadDispatcher"/>: every <c>tools/call</c> is queued here
    /// from an HTTP worker thread and drained on the Unity main thread in <see cref="Update"/>, so tool
    /// bodies touch live game state safely.
    /// </summary>
    [AddComponentMenu("CoreAI/CoreAI MCP Server")]
    public sealed class CoreAiMcpServer : MonoBehaviour, ICancellableMainThreadDispatcher
    {
        /// <summary>Environment variable read when no token is set on the component.</summary>
        public const string AuthTokenEnvironmentVariable = "COREAI_MCP_TOKEN";

        /// <summary>Default seconds a <c>tools/call</c> waits for the main thread before failing.</summary>
        public const float DefaultMainThreadTimeoutSeconds = 30f;

        [Tooltip("Loopback TCP port the MCP server listens on. Clients connect to http://127.0.0.1:<port>/mcp.")]
        [SerializeField]
        private int port = 8590;

        [Tooltip("Start the server automatically when this component is enabled. Off by default (opt-in).")]
        [SerializeField]
        private bool startOnEnable;

        [Tooltip("Require clients to send 'Authorization: Bearer <token>'. Keep this ON: loopback alone " +
                 "stops neither a malicious local process nor a web page posting to 127.0.0.1.")]
        [SerializeField]
        private bool requireAuthToken = true;

        [Tooltip("Fixed bearer token. Leave EMPTY to take it from the COREAI_MCP_TOKEN environment " +
                 "variable, or - when that is unset too - to generate a fresh random token each start and " +
                 "print it to the console.")]
        [SerializeField]
        private string authToken = "";

        [Tooltip("Expose manage_mods to MCP as an unrestricted host administrator. Off by default. " +
                 "Enable only when every client holding the bearer token is trusted with every mod.")]
        [SerializeField]
        private bool enableHostAdminModManagement;

        [Tooltip("Required durable actor id used for MCP manage_mods calls when host-admin mod management " +
                 "is enabled. The tool is omitted when this is blank.")]
        [SerializeField]
        private string hostAdminActorId = "";

        [Tooltip("Seconds a tools/call may wait for the Unity main thread before it fails with a clear " +
                 "error. Guards against a paused game or a disabled component hanging the client forever. " +
                 "0 disables the timeout.")]
        [SerializeField]
        private float mainThreadTimeoutSeconds = DefaultMainThreadTimeoutSeconds;

        [Tooltip("Milliseconds available to start queued tools in one frame. Running synchronous tool code must yield cooperatively.")]
        [SerializeField, Min(0f)]
        private float mainThreadPumpBudgetMilliseconds = 2f;

        [Tooltip("Optional CoreAI/mods LifetimeScope to resolve services from. When empty, the scene is " +
                 "searched for the scope that exposes the Lua executor.")]
        [SerializeField]
        private LifetimeScope scope;

        /// <summary>Maximum queued plus still-running calls admitted by one host, including across restart.</summary>
        public const int MainThreadCallCapacity = 64;

        /// <summary>Seconds between searches for a new CoreAI scope after the one the tools came from was destroyed.</summary>
        public const double CompositionProbeIntervalSeconds = 0.5;

        private readonly object _callsGate = new();
        private readonly LinkedList<QueuedMainThreadCall> _mainThreadQueue = new();
        private int _runningCalls;
        private bool _retiringAutomaticHost;
        private string _instructions = Protocol.McpServerInfo.DefaultInstructions;

        private bool _catalogFollowsComposition;
        private bool _compositionWithdrawn;
        private IObjectResolver _compositionResolver;
        private LifetimeScope _compositionScope;
        private IReadOnlyCollection<string> _compositionToolNames = Array.Empty<string>();
        private double _nextCompositionProbe;

        /// <summary>
        /// Test seam replacing the scene search for a CoreAI container. When set, it is also how liveness of
        /// the composition is judged: the composition is current while it returns the container the tools
        /// were built from.
        /// </summary>
        internal Func<IObjectResolver> CompositionResolverOverride { get; set; }

        /// <summary>Calls occupying admission capacity, including running work which has not actually finished.</summary>
        public int AdmittedMainThreadCalls { get { lock (_callsGate) return _mainThreadQueue.Count + _runningCalls; } }
        private IMcpServerTransport _server;
        private McpSessionStore _sessions;
        private McpRpcDispatcher _dispatcher;
        private string _transportKind = "http";
        private bool _hostAdminFromComposition;

        /// <summary>The router of the running server (request counters, sessions); null while stopped.</summary>
        public McpRpcDispatcher Dispatcher => _server != null ? _dispatcher : null;

        /// <summary><c>http</c> for the loopback listener, <c>bridge</c> for the outbound WebGL link.</summary>
        public string TransportKind => _transportKind;

        /// <summary>Live session ids of the running server; 0 while stopped.</summary>
        public int SessionCount => _server != null && _sessions != null ? _sessions.Count : 0;

        /// <summary>
        /// When true and no CoreAI scope is built yet, <see cref="StartListening()"/> starts with an empty catalog and
        /// publishes the composition's tools as soon as a scope is built (searched every
        /// <see cref="CompositionProbeIntervalSeconds"/>), instead of refusing to start. The debug start-up uses it, so
        /// a client can connect before the first scene has composed CoreAI.
        /// </summary>
        public bool WaitForComposition { get; set; }

        /// <summary>Live host catalog while listening. AddOrReplace, Remove and Replace take effect without restarting.</summary>
        public McpToolRegistry Registry { get; private set; }
        private string _activeAuthToken;
        private IActorIdentityProvider _hostAdminActorIdentityProvider;

        private static CoreAiMcpServer _active;
        private static CoreAiMcpServer _automaticHost;

        /// <summary>True while the underlying HTTP listener is running.</summary>
        public bool IsRunning => _server is { IsRunning: true };

        /// <summary>
        /// True in a WebGL player, where a loopback HttpListener socket cannot be opened from inside
        /// the browser sandbox, so the dev-time MCP server can never come up there.
        /// </summary>
        internal static bool IsWebGlPlayer => Application.platform == RuntimePlatform.WebGLPlayer;

        /// <summary>The loopback URL clients connect to, or null when not running.</summary>
        public string Url => _server?.Url;

        /// <summary>
        /// The bearer token the running server requires, or null when token auth is off / not started.
        /// Read it from code (or from the console line logged at start) to configure an MCP client.
        /// </summary>
        public string AuthToken => _activeAuthToken;

        /// <summary>Seconds a <c>tools/call</c> waits for the main thread; 0 disables the timeout.</summary>
        public float MainThreadTimeoutSeconds
        {
            get => mainThreadTimeoutSeconds;
            set => mainThreadTimeoutSeconds = value;
        }

        /// <summary>
        /// Guidance returned as <c>instructions</c> by <c>initialize</c> (defaults to
        /// <see cref="Protocol.McpServerInfo.DefaultInstructions"/>; null or blank omits it). Read at start.
        /// </summary>
        public string Instructions
        {
            get => _instructions;
            set => _instructions = value;
        }

        /// <summary>
        /// True when the live catalog was built from a CoreAI scope and is rebuilt when that scope is
        /// replaced (scene load). False for a catalog the host passed to <c>StartListening</c>.
        /// </summary>
        public bool CatalogFollowsComposition => _catalogFollowsComposition;

        /// <summary>Whether MCP explicitly receives unrestricted host-admin mod authority.</summary>
        public bool HostAdminModManagementEnabled => enableHostAdminModManagement;

        /// <summary>The explicit durable actor id assigned to MCP host-admin mod calls.</summary>
        public string HostAdminActorId => hostAdminActorId?.Trim() ?? "";

        /// <summary>Explicitly enables unrestricted MCP mod management for the supplied host actor.</summary>
        public void ConfigureHostAdminModManagement(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new ArgumentException("Host-admin actor id is required.", nameof(actorId));
            }

            if (IsRunning)
            {
                throw new InvalidOperationException(
                    "Stop the MCP server before changing host-admin mod authority.");
            }

            hostAdminActorId = actorId.Trim();
            enableHostAdminModManagement = true;
        }

        /// <summary>Enables MCP mod management with an unrestricted actor received from host composition.</summary>
        public void ConfigureHostAdminModManagement(IActorIdentityProvider actorIdentityProvider)
        {
            if (actorIdentityProvider == null)
            {
                throw new ArgumentNullException(nameof(actorIdentityProvider));
            }

            if (IsRunning)
            {
                throw new InvalidOperationException(
                    "Stop the MCP server before changing host-admin mod authority.");
            }

            ActorContext actor = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            if (!actor.IsTrusted || !actor.Grants.IsUnrestricted)
            {
                throw new ArgumentException(
                    "MCP host-admin mod management requires an unrestricted actor from host composition.",
                    nameof(actorIdentityProvider));
            }

            _hostAdminActorIdentityProvider = actorIdentityProvider;
            hostAdminActorId = actor.ActorId;
            enableHostAdminModManagement = true;
        }

        /// <summary>
        /// Enables MCP mod management with the actor the CoreAI composition provides (its
        /// <see cref="IActorIdentityProvider"/>), resolved every time the catalog is built, so it follows scene loads.
        /// The actor must still be trusted and unrestricted, and must match <see cref="HostAdminActorId"/> when one is
        /// set. The debug start-up (<see cref="CoreAiMcpDebugBootstrap"/>) uses it unless told not to.
        /// </summary>
        public void ConfigureHostAdminModManagementFromComposition()
        {
            if (IsRunning)
            {
                throw new InvalidOperationException(
                    "Stop the MCP server before changing host-admin mod authority.");
            }

            _hostAdminFromComposition = true;
            enableHostAdminModManagement = true;
        }

        /// <summary>The bearer token of the server started via the static API, or null.</summary>
        public static string ActiveAuthToken => _active != null ? _active._activeAuthToken : null;

        private void OnEnable()
        {
            if (startOnEnable)
            {
                StartListening();
            }
        }

        private void OnDisable()
        {
            StopListening();
        }

        private void OnDestroy()
        {
            if (_automaticHost == this) _automaticHost = null;
            if (_active == this) _active = null;
        }

        private void Update()
        {
            PumpMainThreadQueue();
            MonitorComposition(Time.realtimeSinceStartupAsDouble);
            if (_automaticHost != this || _server != null) return;
            lock (_callsGate)
            {
                if (_mainThreadQueue.Count != 0 || _runningCalls != 0) return;
                _retiringAutomaticHost = true;
            }
            _automaticHost = null;
            Destroy(gameObject);
        }

        /// <summary>
        /// Drains queued tool invocations on the main thread. Called from <c>Update</c>; public so a host
        /// driving its own loop (and the EditMode tests) can pump the queue explicitly.
        /// </summary>
        public void PumpMainThreadQueue() => PumpMainThreadQueue(
            TimeSpan.FromMilliseconds(Math.Max(0, mainThreadPumpBudgetMilliseconds)));

        /// <summary>
        /// Starts one snapshot of queued work within the host's frame budget. Zero starts at most one call;
        /// this cannot preempt a tool's synchronous body or move Unity work off the game thread.
        /// </summary>
        public void PumpMainThreadQueue(TimeSpan budget)
        {
            if (budget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
            QueuedMainThreadCall[] snapshot;
            lock (_callsGate)
            {
                if (_mainThreadQueue.Count == 0) return;
                snapshot = new QueuedMainThreadCall[_mainThreadQueue.Count];
                _mainThreadQueue.CopyTo(snapshot, 0);
            }
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool startedAny = false;
            // WHY: claiming and timeout removal share one lock, but user work never runs under it.
            foreach (QueuedMainThreadCall call in snapshot)
            {
                if (startedAny && elapsed.Elapsed >= budget) return;
                lock (_callsGate)
                {
                    if (call.Node == null) continue;
                    _mainThreadQueue.Remove(call.Node);
                    call.Node = null;
                    _runningCalls++;
                }
                startedAny = true;
                call.ExitQueue();
                _ = call.Body();
            }
        }

        /// <inheritdoc />
        public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work) =>
            RunOnMainThreadAsync(work, CancellationToken.None);

        /// <inheritdoc />
        public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<T>(cancellationToken);
            }

            TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            QueuedMainThreadCall call = new(
                () => ExecuteMainThreadCallAsync(work, tcs),
                error => tcs.TrySetException(error));

            lock (_callsGate)
            {
                if (_retiringAutomaticHost)
                    return Task.FromException<T>(new ObjectDisposedException(nameof(CoreAiMcpServer),
                        "The automatically owned MCP host has been retired."));
                if (_mainThreadQueue.Count + _runningCalls >= MainThreadCallCapacity)
                    return Task.FromException<T>(new InvalidOperationException(
                        "The CoreAI MCP main-thread admission capacity is full; wait for active calls to finish."));
                call.Node = _mainThreadQueue.AddLast(call);
            }

            if (cancellationToken.CanBeCanceled)
            {
                call.SetRegistration(cancellationToken.Register(() => WithdrawQueuedCall(call,
                    new OperationCanceledException(
                        "the client cancelled this call before it reached the Unity main thread; it did not run.",
                        cancellationToken))));
            }

            float timeout = mainThreadTimeoutSeconds;
            if (timeout > 0f)
            {
                _ = FailOnTimeoutAsync(call, TimeSpan.FromSeconds(timeout));
            }

            return tcs.Task;
        }

        private void WithdrawQueuedCall(QueuedMainThreadCall call, Exception failure)
        {
            lock (_callsGate)
            {
                if (call.Node == null) return;
                _mainThreadQueue.Remove(call.Node);
                call.Node = null;
            }

            call.ExitQueue();
            call.Fail(failure);
        }

        private async Task ExecuteMainThreadCallAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> completion)
        {
            T result = default;
            Exception failure = null;
            try { result = await work().ConfigureAwait(true); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                // WHY: Stop and queue deadlines cannot release a running body's lease; only actual completion can.
                lock (_callsGate) _runningCalls--;
            }
            if (failure is OperationCanceledException) completion.TrySetCanceled();
            else if (failure != null) completion.TrySetException(failure);
            else completion.TrySetResult(result);
        }

        /// <summary>Starts the server, building the tool registry from the current composition.</summary>
        public void StartListening() => StartListening(null);

        /// <summary>Starts with an explicit live host catalog, or resolves composition when null. Port is optional.</summary>
        public void StartListening(McpToolRegistry registry, int? listenPort = null) =>
            StartListening(registry, listenPort, null);

        internal void StartListening(McpToolRegistry registry, int? listenPort,
            Func<int, McpRpcDispatcher, string, IMcpServerTransport> transportFactory)
        {
            if (_retiringAutomaticHost)
                throw new ObjectDisposedException(nameof(CoreAiMcpServer));
            if (IsRunning)
            {
                return;
            }

            if (IsWebGlPlayer)
            {
                // WHY: HttpListener cannot bind a loopback socket from inside the browser sandbox, so
                // the dev-time MCP server can never come up in a WebGL player; degrade to a logged
                // no-op instead of shipping the listener and Task.Run accept loop there.
                Log.Instance.Warn(
                    "[CoreAI MCP] The MCP server is not available in WebGL builds (a browser page cannot open " +
                    "a listening socket); server not started. Connect MCP clients to an editor or desktop build.");
                return;
            }

            if (!isActiveAndEnabled)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] Starting while this component is disabled (or its GameObject is " +
                    "inactive): Update() will not run, so no tools/call can be executed and every call " +
                    $"will fail after {mainThreadTimeoutSeconds}s. Enable the component and its GameObject.");
            }

            bool followsComposition = registry == null;
            bool waitingForComposition = false;
            IObjectResolver compositionResolver = null;
            LifetimeScope compositionScope = null;
            if (followsComposition)
            {
                compositionResolver = ResolveComposition(out compositionScope);
                if (compositionResolver == null && !WaitForComposition)
                {
                    Log.Instance.Warn("[CoreAI MCP] No built CoreAI LifetimeScope; server not started.");
                    return;
                }

                waitingForComposition = compositionResolver == null;
                registry = waitingForComposition
                    ? new McpToolRegistry(null, null, true)
                    : BuildRegistry(compositionResolver);
            }
            if (listenPort.HasValue) port = listenPort.Value;
            int listeningPort = port;
            Registry = registry;
            TrackComposition(followsComposition, compositionResolver, compositionScope);
            if (waitingForComposition)
            {
                // WHY: the probe in MonitorComposition publishes the tools once a scope is built.
                _compositionWithdrawn = true;
                Log.Instance.Info("[CoreAI MCP] No CoreAI scope is built yet; the tools appear when one is.");
            }

            string token = ResolveAuthToken();
            RedactFromRuntimeLog(token);
            _sessions = new McpSessionStore();
            McpRpcDispatcher dispatcher = new(registry, _sessions, this) { Instructions = _instructions };
            _dispatcher = dispatcher;
            _transportKind = transportFactory == null ? "http" : _transportKind;
            IMcpServerTransport transport = null;
            try
            {
                transport = transportFactory == null
                    ? new McpHttpServer(listeningPort, dispatcher,
                        m => Log.Instance.Info($"[CoreAI MCP] {m}"),
                        e => Log.Instance.Warn($"[CoreAI MCP] {e}"), token)
                    : transportFactory(listeningPort, dispatcher, token);
                if (transport == null)
                    throw new InvalidOperationException("The MCP transport factory returned no transport.");
                _server = transport;
                transport.Start();
                if (!ReferenceEquals(_server, transport)) return;
                _activeAuthToken = token;
                _active = this;
                LogAccessInstructions(token);
            }
            catch (Exception ex)
            {
                try
                {
                    if (ReferenceEquals(_server, transport))
                    {
                        _server = null;
                        Registry = null;
                        TrackComposition(false, null, null);
                        _activeAuthToken = null;
                        if (_active == this) _active = null;
                        FailPendingMainThreadCalls();
                        transport?.Dispose();
                    }
                }
                finally
                {
                    // WHY: A reentrant restart may change the component's port while the failed transport is disposed.
                    Log.Instance.Error(
                        $"[CoreAI MCP] Failed to start on port {listeningPort}: {ex.Message}. " +
                        $"If this is an access error on Windows, reserve the URL once as admin: " +
                        $"netsh http add urlacl url={McpHttpServer.GetListeningPrefix(listeningPort)} user=%USERNAME%");
                }
            }
        }

        /// <summary>Stops the server and fails every call still waiting for the main thread.</summary>
        public void StopListening()
        {
            IMcpServerTransport stopped = _server;
            _server = null;
            Registry = null;
            TrackComposition(false, null, null);
            _activeAuthToken = null;
            FailPendingMainThreadCalls();
            if (_active == this)
            {
                _active = null;
            }
            // WHY: Cancellation callbacks may restart this host; detach the stopped generation before invoking them.
            stopped?.Dispose();
        }

        private string ResolveAuthToken()
        {
            if (!requireAuthToken)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(authToken))
            {
                return authToken.Trim();
            }

            string fromEnvironment = Environment.GetEnvironmentVariable(AuthTokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }

            return McpRequestGuard.GenerateToken();
        }

        // WHY: the start line prints the token for the developer, and the captured Unity log is readable by in-game
        // agents whose prompts may go to a cloud provider; the capture keeps the line with the token blanked.
        private static void RedactFromRuntimeLog(string token)
        {
            if (token != null) UnityRuntimeLogCapture.Shared.Buffer.AddRedaction(token);
        }

        private void LogAccessInstructions(string token)
        {
            if (token == null)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] Token auth is DISABLED. Any local process - and any web page that POSTs " +
                    "to this port from the user's browser - can run Lua and load mods in this game. Only " +
                    "do this on a machine you fully trust.");
                return;
            }

            Log.Instance.Info(
                $"[CoreAI MCP] Auth token: {token}\n" +
                $"  claude mcp add --transport http coreai {_server.Url} --header \"Authorization: Bearer {token}\"\n" +
                $"  Set {AuthTokenEnvironmentVariable} (or the Auth Token field) to keep the token stable " +
                "across runs; otherwise a new one is generated every start.");
        }

        private void FailPendingMainThreadCalls()
        {
            List<QueuedMainThreadCall> pending;
            lock (_callsGate)
            {
                pending = new List<QueuedMainThreadCall>(_mainThreadQueue);
                _mainThreadQueue.Clear();
                foreach (QueuedMainThreadCall call in pending) call.Node = null;
            }
            foreach (QueuedMainThreadCall call in pending)
            {
                call.ExitQueue();
                call.Fail(new OperationCanceledException(
                    "the CoreAI MCP server stopped before this call reached the Unity main thread."));
            }
        }

        private async Task FailOnTimeoutAsync(QueuedMainThreadCall call, TimeSpan timeout)
        {
            using CancellationTokenSource timer = new();
            Task finished = await Task.WhenAny(call.QueueExited.Task, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);
            if (finished == call.QueueExited.Task)
            {
                timer.Cancel();
                return;
            }
            lock (_callsGate)
            {
                if (call.Node == null) return;
                _mainThreadQueue.Remove(call.Node);
                call.Node = null;
            }
            call.ExitQueue();
            call.Fail(new TimeoutException(
                $"the Unity main thread never drained the MCP queue within {timeout.TotalSeconds:0.#}s - " +
                "the game is paused, the CoreAiMcpServer component is disabled, or its GameObject is inactive."));
        }

        internal McpToolRegistry BuildRegistry(IObjectResolver resolver)
        {
            resolver.TryResolve(out LuaTool.ILuaExecutor luaExecutor);
            resolver.TryResolve(out ILuaModRuntime modRuntime);
            resolver.TryResolve(out ICoreAISettings settings);
            resolver.TryResolve(out ILog logger);
            resolver.TryResolve(out ILuaLogService logService);
            resolver.TryResolve(out LuaGenerationRateLimiter modRateLimiter);
            resolver.TryResolve(out CoreAI.Mods.Folders.IModFolderListStore modFolderList);

            IActorIdentityProvider modActorIdentityProvider = BuildMcpModIdentityProvider(resolver, modRuntime);

            WorldLlmTool worldTool = BuildWorldTool(resolver, settings);
            IReadOnlyList<SkillSet> skills = ResolveSkills(resolver);
            // WHY: register screenshot unconditionally. Probing for a camera at START-UP made the tool
            // vanish from tools/list for the whole session when the server booted from a bootstrap scene;
            // the source now reports a missing camera per call instead.
            IScreenshotSource screenshot = new MainCameraScreenshotSource();
            IReadOnlyList<IMcpTool> diagnostics = BuildDiagnosticsTools(resolver, modRuntime);

            return CoreAiMcpToolProvider.BuildWithModFolders(
                luaExecutor,
                modRuntime,
                settings,
                logger,
                LuaCapabilities.All,
                modActorIdentityProvider,
                logService,
                worldTool,
                skills,
                screenshot,
                null,
                null,
                null,
                BuildWorldPackageTools(resolver, modActorIdentityProvider),
                modRateLimiter,
                modFolderList,
                diagnostics);
        }

        private IReadOnlyList<IMcpTool> BuildDiagnosticsTools(IObjectResolver resolver, ILuaModRuntime modRuntime)
        {
            IRuntimeLogSource logs = resolver.ResolveOrDefault<IRuntimeLogSource>();
            IRuntimePerformanceSource performance = resolver.ResolveOrDefault<IRuntimePerformanceSource>();
            IActorIdentityProvider identity = resolver.ResolveOrDefault<IActorIdentityProvider>();
            RbxWorldHost worldHost = null;
            try
            {
                worldHost = resolver.ResolveOrDefault<RbxWorldHost>();
            }
            catch (VContainerException)
            {
            }

            GameStatusSources status = new()
            {
                Engine = CoreAiGameStatusSources.Engine,
                Mods = modRuntime != null && identity != null
                    ? () => CoreAiGameStatusSources.Mods(modRuntime, identity)
                    : null,
                World = worldHost != null ? () => CoreAiGameStatusSources.World(worldHost) : null,
                Server = DescribeTransport
            };
            return McpDiagnosticsTools.Create(logs, performance, status);
        }

        /// <summary>What <c>game_status</c> reports about the MCP link itself.</summary>
        internal JObject DescribeTransport()
        {
            McpRpcDispatcher dispatcher = Dispatcher;
            return new JObject
            {
                ["transport"] = _server != null ? _transportKind : "stopped",
                ["url"] = Url,
                ["auth"] = _activeAuthToken != null,
                ["host_admin_mods"] = Registry != null && Registry.Contains("manage_mods"),
                ["requests"] = dispatcher?.RequestCount ?? 0,
                ["tool_calls"] = dispatcher?.ToolCallCount ?? 0,
                ["queued_or_running"] = AdmittedMainThreadCalls
            };
        }

        private static IReadOnlyList<IMcpTool> BuildWorldPackageTools(IObjectResolver resolver,
            IActorIdentityProvider hostAdminIdentityProvider)
        {
            // WHY: the same identity the in-game Programmer tools receive; save_world additionally needs the
            // host-admin admission, like manage_mods, because the slot it writes survives a restart.
            try
            {
                IRbxWorldRuntimeService service = resolver.ResolveOrDefault<IRbxWorldRuntimeService>();
                IActorIdentityProvider identity = resolver.ResolveOrDefault<IActorIdentityProvider>();
                return WorldPackageMcpTools.Create(service, identity, hostAdminIdentityProvider);
            }
            catch (VContainerException ex)
            {
                Log.Instance.Warn($"[CoreAI MCP] World save/load tools omitted: {ex.Message}");
                return Array.Empty<IMcpTool>();
            }
        }

        private IActorIdentityProvider BuildMcpModIdentityProvider(
            IObjectResolver resolver,
            ILuaModRuntime modRuntime)
        {
            if (modRuntime == null)
            {
                return null;
            }

            if (!enableHostAdminModManagement)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] manage_mods omitted: MCP has no authenticated actor identity. " +
                    "Explicitly enable Host Admin Mod Management and set Host Admin Actor Id only for " +
                    "clients trusted with every mod.");
                return null;
            }

            string actorId = HostAdminActorId;
            if (actorId.Length == 0 && !_hostAdminFromComposition)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] manage_mods omitted: Host Admin Mod Management is enabled but Host " +
                    "Admin Actor Id is blank.");
                return null;
            }

            IActorIdentityProvider actorIdentityProvider = _hostAdminActorIdentityProvider ??
                                                           resolver.ResolveOrDefault<IActorIdentityProvider>();
            if (actorIdentityProvider == null)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] manage_mods omitted: host composition did not provide an actor identity.");
                return null;
            }

            ActorContext actor = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            if (!actor.IsTrusted || !actor.Grants.IsUnrestricted)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] manage_mods omitted: the composed actor is not unrestricted.");
                return null;
            }

            if (actorId.Length == 0)
            {
                actorId = actor.ActorId;
            }
            else if (!string.Equals(actor.ActorId, actorId, StringComparison.Ordinal))
            {
                Log.Instance.Warn(
                    $"[CoreAI MCP] manage_mods omitted: configured Host Admin Actor Id '{actorId}' does not " +
                    $"match composed actor '{actor.ActorId}'.");
                return null;
            }

            Log.Instance.Warn(
                $"[CoreAI MCP] manage_mods enabled with unrestricted host-admin identity '{actorId}'. " +
                "Every client holding the MCP bearer token receives this authority.");
            return actorIdentityProvider;
        }

        private static WorldLlmTool BuildWorldTool(IObjectResolver resolver, ICoreAISettings settings)
        {
            // WHY: world_command is present only when a world-command executor AND its logger resolve in
            // this composition; a Lua-only game simply omits the tool from tools/list.
            if (!resolver.TryResolve(out ICoreAiWorldCommandExecutor executor) || executor == null)
            {
                return null;
            }

            if (settings == null || !resolver.TryResolve(out IGameLogger gameLogger))
            {
                return null;
            }

            return new WorldLlmTool(executor, settings, gameLogger);
        }

        private static IReadOnlyList<SkillSet> ResolveSkills(IObjectResolver resolver)
        {
            if (resolver.TryResolve(out AgentMemoryPolicy policy) && policy != null)
            {
                return policy.GetSkillsForRole(BuiltInAgentRoleIds.Programmer);
            }

            return Array.Empty<SkillSet>();
        }

        private IObjectResolver ResolveComposition(out LifetimeScope owner)
        {
            owner = null;
            if (CompositionResolverOverride != null)
            {
                return CompositionResolverOverride();
            }

            if (scope != null && scope.Container != null)
            {
                owner = scope;
                return scope.Container;
            }

            // Prefer the innermost scope that can resolve the Lua executor (the mods child scope), else any
            // scope with a built container. This is an entry-point component, so an object scan is allowed
            // (unlike installers, which the architecture rules forbid from scanning).
            LifetimeScope[] scopes =
                FindObjectsByType<LifetimeScope>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            LifetimeScope fallback = null;
            foreach (LifetimeScope candidate in scopes)
            {
                if (candidate == null || candidate.Container == null)
                {
                    continue;
                }

                fallback ??= candidate;
                if (candidate.Container.TryResolve(out LuaTool.ILuaExecutor _))
                {
                    owner = candidate;
                    return candidate.Container;
                }
            }

            owner = fallback;
            return fallback?.Container;
        }

        private void TrackComposition(bool follows, IObjectResolver resolver, LifetimeScope owner)
        {
            _catalogFollowsComposition = follows;
            _compositionWithdrawn = false;
            _compositionResolver = follows ? resolver : null;
            _compositionScope = follows ? owner : null;
            _compositionToolNames = follows && Registry != null ? Registry.ToolNames : Array.Empty<string>();
            _nextCompositionProbe = 0;
        }

        private bool IsCompositionCurrent()
        {
            if (CompositionResolverOverride != null)
            {
                return ReferenceEquals(CompositionResolverOverride(), _compositionResolver);
            }

            // WHY: both checks - a scene load destroys the scope (Unity null), and a scope that rebuilt its
            // container keeps the component but hands out a new resolver.
            return _compositionScope != null && ReferenceEquals(_compositionScope.Container, _compositionResolver);
        }

        /// <summary>
        /// Keeps a composition-built catalog bound to a live CoreAI scope. When the scope the tools were built
        /// from is destroyed (a scene load, <c>world_command load_scene</c> / <c>reload_scene</c>) or rebuilds
        /// its container, the tools are withdrawn at once so no call reaches disposed services, and the
        /// catalog is rebuilt from the next built scope, searched at most every
        /// <see cref="CompositionProbeIntervalSeconds"/>. Every change is published as
        /// <c>notifications/tools/list_changed</c>. A host-supplied catalog is never touched. Called from
        /// <c>Update</c>; public for hosts that drive their own loop.
        /// </summary>
        /// <param name="realtimeSeconds">Monotonic time in seconds, e.g. <c>Time.realtimeSinceStartupAsDouble</c>.</param>
        public void MonitorComposition(double realtimeSeconds)
        {
            McpToolRegistry catalog = Registry;
            if (!_catalogFollowsComposition || catalog == null)
            {
                return;
            }

            if (!_compositionWithdrawn)
            {
                if (IsCompositionCurrent())
                {
                    return;
                }

                _compositionWithdrawn = true;
                _compositionResolver = null;
                _compositionScope = null;
                _nextCompositionProbe = realtimeSeconds;
                catalog.Rebind(_compositionToolNames, null);
                _compositionToolNames = Array.Empty<string>();
                Log.Instance.Warn(
                    "[CoreAI MCP] The CoreAI scope the MCP tools were built from is gone (scene load or " +
                    "container rebuild); its tools are withdrawn until a new CoreAI scope is built.");
            }

            if (realtimeSeconds < _nextCompositionProbe)
            {
                return;
            }

            _nextCompositionProbe = realtimeSeconds + CompositionProbeIntervalSeconds;
            IObjectResolver next = ResolveComposition(out LifetimeScope owner);
            if (next == null)
            {
                return;
            }

            try
            {
                _compositionToolNames = catalog.Rebind(_compositionToolNames, BuildRegistry(next));
            }
            catch (Exception ex)
            {
                // WHY: back off instead of rebuilding a broken composition twice a second.
                _nextCompositionProbe = realtimeSeconds + 10 * CompositionProbeIntervalSeconds;
                Log.Instance.Error($"[CoreAI MCP] Rebuilding the MCP tools from the new CoreAI scope failed: {ex.Message}");
                return;
            }

            _compositionWithdrawn = false;
            _compositionResolver = next;
            _compositionScope = owner;
            Log.Instance.Info($"[CoreAI MCP] MCP tools rebuilt from the new CoreAI scope ({catalog.Count} tools).");
        }

        /// <summary>
        /// Starts (or reuses) a server on <paramref name="port"/>, creating an automatically owned
        /// persistent host when needed. A stopped host is reused while its running bodies drain, then
        /// destroyed by the player loop. Returns the instance; read
        /// <see cref="AuthToken"/> on it to obtain the bearer token clients must send.
        /// </summary>
        public static CoreAiMcpServer StartServer(int port = 8590) => StartServer(port, null, null);

        internal static CoreAiMcpServer StartServer(int port, McpToolRegistry registry,
            Func<int, McpRpcDispatcher, string, IMcpServerTransport> transportFactory)
        {
            if (_active != null && _active.IsRunning)
            {
                return _active;
            }

            CoreAiMcpServer instance = AcquireAutomaticHost();
            instance.port = port;
            instance.StartListening(registry, null, transportFactory);
            return instance;
        }

        /// <summary>
        /// Starts (or reuses) the automatic host the way the debug start-up does: it listens at once and publishes the
        /// composition's tools when a CoreAI scope is built (<see cref="WaitForComposition"/>), and, unless
        /// <paramref name="hostAdmin"/> is false, grants <c>manage_mods</c> and <c>save_world</c> to the composition's
        /// unrestricted actor (<see cref="ConfigureHostAdminModManagementFromComposition"/>). Main thread only.
        /// </summary>
        public static CoreAiMcpServer StartDebugServer(int port = 8590, bool hostAdmin = true) =>
            StartDebugServer(port, hostAdmin, null);

        internal static CoreAiMcpServer StartDebugServer(int port, bool hostAdmin,
            Func<int, McpRpcDispatcher, string, IMcpServerTransport> transportFactory)
        {
            if (_active != null && _active.IsRunning)
            {
                return _active;
            }

            CoreAiMcpServer instance = AcquireAutomaticHost();
            instance.PrepareDebugStart(port, hostAdmin);
            instance.StartListening(null, null, transportFactory);
            return instance;
        }

        /// <summary>
        /// Links the automatic host to a local MCP bridge process on <paramref name="bridgePort"/> instead of listening
        /// (the only way in a WebGL player). See the package README, "Connect to a WebGL build".
        /// </summary>
        public static CoreAiMcpServer StartDebugBridge(int bridgePort, string bridgeKey, bool hostAdmin)
        {
            Log.Instance.Warn("[CoreAI MCP] The bridge link is not available in this version yet; port " + bridgePort +
                              " ignored.");
            return null;
        }

        private void PrepareDebugStart(int listenPort, bool hostAdmin)
        {
            port = listenPort;
            WaitForComposition = true;
            if (hostAdmin)
            {
                ConfigureHostAdminModManagementFromComposition();
            }
            else
            {
                _hostAdminFromComposition = false;
                enableHostAdminModManagement = false;
            }
        }

        private static CoreAiMcpServer AcquireAutomaticHost()
        {
            CoreAiMcpServer instance = _automaticHost;
            if (instance == null)
            {
                GameObject host = new("CoreAI_McpServer");
                DontDestroyOnLoad(host);
                instance = host.AddComponent<CoreAiMcpServer>();
                _automaticHost = instance;
            }

            return instance;
        }

        /// <summary>The server started through the static API or the debug start-up, or null.</summary>
        public static CoreAiMcpServer Active => _active;

        /// <summary>Stops the active server started via the static API (or any tracked instance).</summary>
        public static void StopServer()
        {
            CoreAiMcpServer instance = _active != null ? _active : _automaticHost;
            instance?.StopListening();
        }

        /// <summary>
        /// One queued main-thread invocation. Exactly one party runs it: the pump, the timeout watchdog,
        /// or shutdown - whoever claims it first.
        /// </summary>
        private sealed class QueuedMainThreadCall
        {
            private readonly Action<Exception> _fail;
            public LinkedListNode<QueuedMainThreadCall> Node { get; set; }
            public TaskCompletionSource<bool> QueueExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _registrationGate = new();
            private CancellationTokenRegistration _registration;
            private bool _exited;

            /// <summary>Keeps the cancellation registration until the call leaves the queue.</summary>
            public void SetRegistration(CancellationTokenRegistration registration)
            {
                bool alreadyExited;
                lock (_registrationGate)
                {
                    alreadyExited = _exited;
                    if (!alreadyExited) _registration = registration;
                }

                if (alreadyExited) registration.Dispose();
            }

            /// <summary>Marks the call as no longer queued and drops its cancellation registration.</summary>
            public void ExitQueue()
            {
                QueueExited.TrySetResult(true);
                CancellationTokenRegistration registration;
                lock (_registrationGate)
                {
                    _exited = true;
                    registration = _registration;
                    _registration = default;
                }

                registration.Dispose();
            }

            public QueuedMainThreadCall(Func<Task> body, Action<Exception> fail)
            {
                Body = body;
                _fail = fail;
            }

            /// <summary>The work to run on the main thread.</summary>
            public Func<Task> Body { get; }

            /// <summary>Completes the awaiting HTTP worker with an error.</summary>
            public void Fail(Exception error)
            {
                _fail(error);
            }
        }
    }
}
