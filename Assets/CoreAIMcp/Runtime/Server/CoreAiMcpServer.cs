using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Mcp.Tools;
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
    public sealed class CoreAiMcpServer : MonoBehaviour, IMainThreadDispatcher
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

        private readonly object _callsGate = new();
        private readonly LinkedList<QueuedMainThreadCall> _mainThreadQueue = new();
        private int _runningCalls;

        /// <summary>Calls occupying admission capacity, including running work which has not actually finished.</summary>
        public int AdmittedMainThreadCalls { get { lock (_callsGate) return _mainThreadQueue.Count + _runningCalls; } }
        private McpHttpServer _server;
        private McpSessionStore _sessions;

        /// <summary>Live host catalog while listening. AddOrReplace, Remove and Replace take effect without restarting.</summary>
        public McpToolRegistry Registry { get; private set; }
        private string _activeAuthToken;
        private IActorIdentityProvider _hostAdminActorIdentityProvider;

        private static CoreAiMcpServer _active;

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

        private void Update()
        {
            PumpMainThreadQueue();
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
                call.QueueExited.TrySetResult(true);
                _ = call.Body();
            }
        }

        /// <inheritdoc />
        public Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            QueuedMainThreadCall call = new(
                () => ExecuteMainThreadCallAsync(work, tcs),
                error => tcs.TrySetException(error));

            lock (_callsGate)
            {
                if (_mainThreadQueue.Count + _runningCalls >= MainThreadCallCapacity)
                    return Task.FromException<T>(new InvalidOperationException(
                        "The CoreAI MCP main-thread admission capacity is full; wait for active calls to finish."));
                call.Node = _mainThreadQueue.AddLast(call);
            }

            float timeout = mainThreadTimeoutSeconds;
            if (timeout > 0f)
            {
                _ = FailOnTimeoutAsync(call, TimeSpan.FromSeconds(timeout));
            }

            return tcs.Task;
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
        public void StartListening(McpToolRegistry registry, int? listenPort = null)
        {
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
                    "[CoreAI MCP] The MCP server is not available in WebGL builds; server not started.");
                return;
            }

            if (!isActiveAndEnabled)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] Starting while this component is disabled (or its GameObject is " +
                    "inactive): Update() will not run, so no tools/call can be executed and every call " +
                    $"will fail after {mainThreadTimeoutSeconds}s. Enable the component and its GameObject.");
            }

            if (registry == null)
            {
                IObjectResolver resolver = ResolveContainer();
                if (resolver == null)
                {
                    Log.Instance.Warn("[CoreAI MCP] No built CoreAI LifetimeScope; server not started.");
                    return;
                }
                registry = BuildRegistry(resolver);
            }
            if (listenPort.HasValue) port = listenPort.Value;
            Registry = registry;

            string token = ResolveAuthToken();
            _sessions = new McpSessionStore();
            McpRpcDispatcher dispatcher = new(registry, _sessions, this);
            _server = new McpHttpServer(port, dispatcher,
                m => Log.Instance.Info($"[CoreAI MCP] {m}"),
                e => Log.Instance.Warn($"[CoreAI MCP] {e}"),
                token);

            try
            {
                _server.Start();
                _activeAuthToken = token;
                _active = this;
                LogAccessInstructions(token);
            }
            catch (Exception ex)
            {
                _server?.Dispose();
                _server = null;
                Registry = null;
                _activeAuthToken = null;
                // WHY: HttpListener on Windows can need a URL ACL for a non-admin process; point the user
                // at the fix instead of a bare stack trace.
                Log.Instance.Error(
                    $"[CoreAI MCP] Failed to start on port {port}: {ex.Message}. " +
                    $"If this is an access error on Windows, reserve the URL once as admin: " +
                    $"netsh http add urlacl url=http://127.0.0.1:{port}/mcp/ user=%USERNAME%");
            }
        }

        /// <summary>Stops the server and fails every call still waiting for the main thread.</summary>
        public void StopListening()
        {
            _server?.Dispose();
            _server = null;
            Registry = null;
            _activeAuthToken = null;
            FailPendingMainThreadCalls();
            if (_active == this)
            {
                _active = null;
            }
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
                call.QueueExited.TrySetResult(true);
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
            call.QueueExited.TrySetResult(true);
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

            IActorIdentityProvider modActorIdentityProvider = BuildMcpModIdentityProvider(resolver, modRuntime);

            WorldLlmTool worldTool = BuildWorldTool(resolver, settings);
            IReadOnlyList<SkillSet> skills = ResolveSkills(resolver);
            // WHY: register screenshot unconditionally. Probing for a camera at START-UP made the tool
            // vanish from tools/list for the whole session when the server booted from a bootstrap scene;
            // the source now reports a missing camera per call instead.
            IScreenshotSource screenshot = new MainCameraScreenshotSource();

            return CoreAiMcpToolProvider.Build(
                luaExecutor,
                modRuntime,
                settings,
                logger,
                LuaCapabilities.All,
                modActorIdentityProvider,
                logService,
                worldTool,
                skills,
                screenshot);
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
            if (actorId.Length == 0)
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

            if (!string.Equals(actor.ActorId, actorId, StringComparison.Ordinal))
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

        private IObjectResolver ResolveContainer()
        {
            if (scope != null && scope.Container != null)
            {
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
                    return candidate.Container;
                }
            }

            return fallback?.Container;
        }

        /// <summary>
        /// Starts (or reuses) a server on <paramref name="port"/> from anywhere, creating a hidden host
        /// GameObject when no component exists yet. Returns the running instance; read
        /// <see cref="AuthToken"/> on it to obtain the bearer token clients must send.
        /// </summary>
        public static CoreAiMcpServer StartServer(int port = 8590)
        {
            if (_active != null && _active.IsRunning)
            {
                return _active;
            }

            CoreAiMcpServer instance = _active;
            if (instance == null)
            {
                GameObject host = new("CoreAI_McpServer");
                DontDestroyOnLoad(host);
                instance = host.AddComponent<CoreAiMcpServer>();
            }

            instance.port = port;
            instance.StartListening();
            return instance;
        }

        /// <summary>Stops the active server started via the static API (or any tracked instance).</summary>
        public static void StopServer()
        {
            _active?.StopListening();
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
