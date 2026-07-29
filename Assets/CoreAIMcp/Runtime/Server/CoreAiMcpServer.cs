using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
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

        [Tooltip("Seconds a tools/call may wait for the Unity main thread before it fails with a clear " +
                 "error. Guards against a paused game or a disabled component hanging the client forever. " +
                 "0 disables the timeout.")]
        [SerializeField]
        private float mainThreadTimeoutSeconds = DefaultMainThreadTimeoutSeconds;

        [Tooltip("Optional CoreAI/mods LifetimeScope to resolve services from. When empty, the scene is " +
                 "searched for the scope that exposes the Lua executor.")]
        [SerializeField]
        private LifetimeScope scope;

        private readonly ConcurrentQueue<QueuedMainThreadCall> _mainThreadQueue = new();
        private McpHttpServer _server;
        private McpSessionStore _sessions;
        private string _activeAuthToken;

        private static CoreAiMcpServer _active;

        /// <summary>True while the underlying HTTP listener is running.</summary>
        public bool IsRunning => _server is { IsRunning: true };

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
        public void PumpMainThreadQueue()
        {
            // Each item is fire-and-forget: its TaskCompletionSource (created in RunOnMainThreadAsync)
            // bridges completion back to the awaiting HTTP worker, and any async continuations resume on
            // Unity's synchronization context. Claiming skips items already failed by timeout or shutdown.
            while (_mainThreadQueue.TryDequeue(out QueuedMainThreadCall call))
            {
                if (call.TryClaim())
                {
                    _ = call.Body();
                }
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
                async () =>
                {
                    try
                    {
                        T result = await work().ConfigureAwait(true);
                        tcs.TrySetResult(result);
                    }
                    catch (OperationCanceledException)
                    {
                        tcs.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                error => tcs.TrySetException(error));

            _mainThreadQueue.Enqueue(call);

            float timeout = mainThreadTimeoutSeconds;
            if (timeout > 0f)
            {
                _ = FailOnTimeoutAsync(call, tcs.Task, TimeSpan.FromSeconds(timeout));
            }

            return tcs.Task;
        }

        /// <summary>Starts the server, building the tool registry from the current composition.</summary>
        public void StartListening()
        {
            if (IsRunning)
            {
                return;
            }

            if (!isActiveAndEnabled)
            {
                // WHY: Update() is what drains the main-thread queue; on a disabled component or an
                // inactive GameObject every tools/call would sit in the queue until it times out.
                Log.Instance.Warn(
                    "[CoreAI MCP] Starting while this component is disabled (or its GameObject is " +
                    "inactive): Update() will not run, so no tools/call can be executed and every call " +
                    $"will fail after {mainThreadTimeoutSeconds}s. Enable the component and its GameObject.");
            }

            IObjectResolver resolver = ResolveContainer();
            if (resolver == null)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] No CoreAI LifetimeScope with a built container was found; server not started. " +
                    "Ensure a CoreAILifetimeScope (and CoreAiModsLifetimeScope) is present and built.");
                return;
            }

            McpToolRegistry registry = BuildRegistry(resolver);
            if (registry.Count == 0)
            {
                Log.Instance.Warn(
                    "[CoreAI MCP] No MCP tools resolved from the composition; server not started.");
                return;
            }

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
                _server = null;
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
            // WHY: nothing will ever drain the queue after a stop, so resolve every pending call instead of
            // leaking its TaskCompletionSource and leaving the HTTP worker awaiting forever.
            while (_mainThreadQueue.TryDequeue(out QueuedMainThreadCall call))
            {
                if (call.TryClaim())
                {
                    call.Fail(new OperationCanceledException(
                        "the CoreAI MCP server stopped before this call reached the Unity main thread."));
                }
            }
        }

        private async Task FailOnTimeoutAsync(QueuedMainThreadCall call, Task pending, TimeSpan timeout)
        {
            Task finished = await Task.WhenAny(pending, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished == pending)
            {
                return;
            }

            // TryClaim wins only when the call is still queued, which tells the two causes apart.
            bool neverDequeued = call.TryClaim();
            string reason = neverDequeued
                ? $"the Unity main thread never drained the MCP queue within {timeout.TotalSeconds:0.#}s - " +
                  "the game is paused, the CoreAiMcpServer component is disabled, or its GameObject is inactive"
                : $"the tool did not finish within {timeout.TotalSeconds:0.#}s";

            call.Fail(new TimeoutException($"{reason}."));
        }

        private McpToolRegistry BuildRegistry(IObjectResolver resolver)
        {
            resolver.TryResolve(out LuaTool.ILuaExecutor luaExecutor);
            resolver.TryResolve(out ILuaModRuntime modRuntime);
            resolver.TryResolve(out ICoreAISettings settings);
            resolver.TryResolve(out ILog logger);
            resolver.TryResolve(out ILuaLogService logService);

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
                logService,
                worldTool,
                skills,
                screenshot);
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
            private int _claimed;

            public QueuedMainThreadCall(Func<Task> body, Action<Exception> fail)
            {
                Body = body;
                _fail = fail;
            }

            /// <summary>The work to run on the main thread.</summary>
            public Func<Task> Body { get; }

            /// <summary>True for the first caller only; everyone else must leave the call alone.</summary>
            public bool TryClaim()
            {
                return Interlocked.Exchange(ref _claimed, 1) == 0;
            }

            /// <summary>Completes the awaiting HTTP worker with an error.</summary>
            public void Fail(Exception error)
            {
                _fail(error);
            }
        }
    }
}
