using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// is set), localhost-only, and unauthenticated. Only ever run it on a trusted machine; never forward
    /// the port off-box.
    /// </para>
    /// This component is the <see cref="IMainThreadDispatcher"/>: every <c>tools/call</c> is queued here
    /// from an HTTP worker thread and drained on the Unity main thread in <see cref="Update"/>, so tool
    /// bodies touch live game state safely.
    /// </summary>
    [AddComponentMenu("CoreAI/CoreAI MCP Server")]
    public sealed class CoreAiMcpServer : MonoBehaviour, IMainThreadDispatcher
    {
        [Tooltip("Loopback TCP port the MCP server listens on. Clients connect to http://127.0.0.1:<port>/mcp.")]
        [SerializeField]
        private int port = 8590;

        [Tooltip("Start the server automatically when this component is enabled. Off by default (opt-in).")]
        [SerializeField]
        private bool startOnEnable;

        [Tooltip("Optional CoreAI/mods LifetimeScope to resolve services from. When empty, the scene is " +
                 "searched for the scope that exposes the Lua executor.")]
        [SerializeField]
        private LifetimeScope scope;

        private readonly ConcurrentQueue<Func<Task>> _mainThreadQueue = new();
        private McpHttpServer _server;
        private McpSessionStore _sessions;

        private static CoreAiMcpServer _active;

        /// <summary>True while the underlying HTTP listener is running.</summary>
        public bool IsRunning => _server is { IsRunning: true };

        /// <summary>The loopback URL clients connect to, or null when not running.</summary>
        public string Url => _server?.Url;

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
            // Drain queued tool invocations on the main thread. Each item is fire-and-forget: its
            // TaskCompletionSource (created in RunOnMainThreadAsync) bridges completion back to the awaiting
            // HTTP worker, and any async continuations resume on Unity's synchronization context.
            while (_mainThreadQueue.TryDequeue(out Func<Task> work))
            {
                _ = work();
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
            _mainThreadQueue.Enqueue(async () =>
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
            });
            return tcs.Task;
        }

        /// <summary>Starts the server, building the tool registry from the current composition.</summary>
        public void StartListening()
        {
            if (IsRunning)
            {
                return;
            }

            IObjectResolver resolver = ResolveContainer();
            if (resolver == null)
            {
                CoreAI.Logging.Log.Instance.Warn(
                    "[CoreAI MCP] No CoreAI LifetimeScope with a built container was found; server not started. " +
                    "Ensure a CoreAILifetimeScope (and CoreAiModsLifetimeScope) is present and built.");
                return;
            }

            McpToolRegistry registry = BuildRegistry(resolver);
            if (registry.Count == 0)
            {
                CoreAI.Logging.Log.Instance.Warn(
                    "[CoreAI MCP] No MCP tools resolved from the composition; server not started.");
                return;
            }

            _sessions = new McpSessionStore();
            McpRpcDispatcher dispatcher = new(registry, _sessions, this);
            _server = new McpHttpServer(port, dispatcher,
                m => CoreAI.Logging.Log.Instance.Info($"[CoreAI MCP] {m}"),
                e => CoreAI.Logging.Log.Instance.Warn($"[CoreAI MCP] {e}"));

            try
            {
                _server.Start();
                _active = this;
            }
            catch (Exception ex)
            {
                _server = null;
                // WHY: HttpListener on Windows can need a URL ACL for a non-admin process; point the user
                // at the fix instead of a bare stack trace.
                CoreAI.Logging.Log.Instance.Error(
                    $"[CoreAI MCP] Failed to start on port {port}: {ex.Message}. " +
                    $"If this is an access error on Windows, reserve the URL once as admin: " +
                    $"netsh http add urlacl url=http://127.0.0.1:{port}/mcp/ user=%USERNAME%");
            }
        }

        /// <summary>Stops the server.</summary>
        public void StopListening()
        {
            _server?.Dispose();
            _server = null;
            if (_active == this)
            {
                _active = null;
            }
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
            IScreenshotSource screenshot = MainCameraScreenshotSource.HasCamera
                ? new MainCameraScreenshotSource()
                : null;

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
        /// GameObject when no component exists yet. Returns the running instance.
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
    }
}
