#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI
{
    /// <summary>
    /// One-line CoreAI entrypoint: buffered (<c>await</c>) and streaming LLM calls over
    /// <see cref="IAiOrchestrationService"/> (queue, metrics, command publish) without hand-wiring VContainer.
    /// <para><b>Quick start.</b> Add <see cref="CoreAILifetimeScope"/> to a scene, then:</para>
    /// <code>
    /// string answer = await CoreAi.AskAsync("Hello!", roleId: "SmartChat");
    /// // Streaming string chunks:
    /// await foreach (string chunk in CoreAi.StreamAsync("Tell a joke", "SmartChat"))
    ///     label.text += chunk;
    /// // Smart path (streaming if enabled in settings / agent / UI):
    /// await CoreAi.SmartAskAsync("Question", "SmartChat", onChunk: c => label.text += c);
    /// // Full orchestrator (memory, authority, metrics, publish):
    /// var task = new AiTaskRequest { RoleId = "Creator", Hint = "Emit a JSON command" };
    /// string result = await CoreAi.OrchestrateAsync(task);
    /// await foreach (var chunk in CoreAi.OrchestrateStreamAsync(task))
    ///     Debug.Log(chunk.Text);
    /// </code>
    /// <para>
    /// Services resolve lazily from the first located <see cref="CoreAILifetimeScope"/>.
    /// Call <see cref="Invalidate"/> after scene changes or container rebuilds so cached instances are dropped.
    /// </para>
    /// </summary>
    public static class CoreAi
    {
        private static readonly object SyncRoot = new();
        private static CoreAILifetimeScope? _scope;
        private static CoreAiChatService? _chatService;
        private static IAiOrchestrationService? _orchestrator;
        private static Func<IAiOrchestrationService>? _orchestratorResolver;
        private static ICoreAISettings? _settings;
        private static readonly object ToolCallSyncRoot = new();
        private static readonly InMemoryLlmToolCallHistory ToolCallHistory = new(512);
        private static event Action<LlmToolCallRecord>? OnToolCallRecord;

        // Static facade: no DI scope is guaranteed here, so route through the shared fallback
        // game logger instead of UnityEngine.Debug (keeps feature filtering and sink routing).
        private static void LogFacadeWarning(string message)
        {
            GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core, message);
        }

        /// <summary>True when CoreAI services resolved successfully.</summary>
        public static bool IsReady => TryResolve(out _, out _, out _);

        /// <summary>
        /// Clears cached service references. Call after scene loads, container rebuilds, or between test fixtures; safe to repeat.
        /// </summary>
        public static void Invalidate()
        {
            lock (SyncRoot)
            {
                _scope = null;
                _chatService = null;
                _orchestrator = null;
                _orchestratorResolver = null;
                _settings = null;
            }

            // CoreAI.Core is UnityEngine-free, so its static facade state is reset here
            // (this runs on SubsystemRegistration) to survive Enter Play Mode without Domain Reload.
            CoreAIAgent.Reset();
        }

        /// <summary>
        /// Overrides orchestrator resolver for tests/CI.
        /// </summary>
        public static void SetResolver(Func<IAiOrchestrationService> resolver)
        {
            if (resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            lock (SyncRoot)
            {
                _orchestratorResolver = resolver;
                _orchestrator = null;
            }
        }

        /// <summary>
        /// Sends a chat turn and waits for the full model text (non-streaming). Persists history for
        /// <paramref name="roleId"/> when ChatHistory is enabled.
        /// </summary>
        /// <remarks>Use <c>await</c> only; blocking the Unity main thread via <c>.Result</c>/<c>.Wait()</c> risks deadlocks with MEAI marshaling.</remarks>
        public static async Task<string?> AskAsync(
            string userMessage,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            return await svc.SendMessageAsync(userMessage, roleId, cancellationToken);
        }

        /// <summary>
        /// Whether the configured model can receive images (vision / multimodal). Gates the camera send
        /// path and any vision-tool registration: when <c>false</c>, callers should fall back to
        /// <see cref="AskAsync"/> and omit images/tools. Resolved from <c>CoreAISettingsAsset.VisionSupport</c>
        /// (On / Off / Auto-by-model-name).
        /// </summary>
        public static bool IsVisionEnabled()
        {
            return RequireChatService().IsVisionEnabled();
        }

        /// <summary>
        /// Captures the named camera and sends it to a vision-capable model as a single USER message
        /// (prompt + JPEG screenshot serialized to OpenAI <c>image_url</c>). The provider-safe camera →
        /// model path. Throws when the configured model is text-only — check <see cref="IsVisionEnabled"/>
        /// first. Returns the model's text reply.
        /// <code>
        /// if (CoreAi.IsVisionEnabled())
        ///     string answer = await CoreAi.AskWithCameraAsync("What is on screen?", "main", "SmartChat");
        /// </code>
        /// </summary>
        public static Task<string> AskWithCameraAsync(
            string prompt,
            string cameraName = "main",
            string roleId = BuiltInAgentRoleIds.SmartChat,
            int width = 512,
            int height = 512,
            CancellationToken cancellationToken = default)
        {
            return RequireChatService()
                .AskWithCameraAsync(prompt, cameraName, roleId, width, height, cancellationToken);
        }

        /// <summary>
        /// Camera overload taking an already-resolved <see cref="UnityEngine.Camera"/> (no name lookup).
        /// See <see cref="AskWithCameraAsync(string,string,string,int,int,CancellationToken)"/>.
        /// </summary>
        public static Task<string> AskWithCameraAsync(
            string prompt,
            Camera camera,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            int width = 512,
            int height = 512,
            CancellationToken cancellationToken = default)
        {
            return RequireChatService().AskWithCameraAsync(prompt, camera, roleId, width, height, cancellationToken);
        }

        /// <summary>
        /// Registers <c>CameraLlmTool</c> (the <c>capture_camera</c> tool) on <paramref name="roleId"/> so a
        /// vision-capable model can autonomously request a screenshot — but only when
        /// <see cref="IsVisionEnabled"/> is <c>true</c>. For text-only models this is a no-op (the tool is
        /// omitted), satisfying the capability gate on the tool-registration side. Returns whether the tool
        /// was registered.
        /// <para>
        /// Because OpenAI tool results cannot carry images, after the tool runs lift the screenshot into a
        /// follow-up user message with <see cref="AskWithImageFollowUpAsync"/> (subscribe to
        /// <see cref="OnToolCallCompleted"/>, match <c>capture_camera</c>, pass its <c>ResultJson</c>).
        /// </para>
        /// </summary>
        public static bool RegisterCameraVisionTool(string roleId = BuiltInAgentRoleIds.SmartChat)
        {
            if (string.IsNullOrWhiteSpace(roleId) || !IsVisionEnabled())
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!TryResolve(out _, out _, out _) || _scope?.Container == null)
                {
                    LogFacadeWarning("[CoreAi] RegisterCameraVisionTool: CoreAI services not resolved.");
                    return false;
                }

                try
                {
                    AgentMemoryPolicy policy = (AgentMemoryPolicy)_scope.Container.Resolve(typeof(AgentMemoryPolicy));
                    if (policy == null)
                    {
                        return false;
                    }

                    policy.AddToolForRole(roleId.Trim(), new Infrastructure.World.CameraLlmTool());
                    return true;
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] RegisterCameraVisionTool failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Registers the on-demand <c>game_state</c> tool on an agent role so the model can pull the current
        /// host telemetry (wave, score, mode, player stats, ...) when it needs it — instead of relying on
        /// state baked into earlier messages, which goes stale. The tool reads the live
        /// <see cref="CoreAI.Session.ISessionTelemetryProvider"/> the game updates from gameplay. Returns
        /// whether it was registered.
        /// </summary>
        public static bool RegisterGameStateTool(string roleId = BuiltInAgentRoleIds.SmartChat)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!TryResolve(out _, out _, out _) || _scope?.Container == null)
                {
                    LogFacadeWarning("[CoreAi] RegisterGameStateTool: CoreAI services not resolved.");
                    return false;
                }

                try
                {
                    AgentMemoryPolicy policy = (AgentMemoryPolicy)_scope.Container.Resolve(typeof(AgentMemoryPolicy));
                    CoreAI.Session.ISessionTelemetryProvider telemetry =
                        (CoreAI.Session.ISessionTelemetryProvider)_scope.Container.Resolve(
                            typeof(CoreAI.Session.ISessionTelemetryProvider));
                    if (policy == null || telemetry == null)
                    {
                        return false;
                    }

                    policy.AddToolForRole(roleId.Trim(), new CoreAI.Ai.GameStateLlmTool(telemetry));
                    return true;
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] RegisterGameStateTool failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Adds an on-demand skill to an agent role at runtime (the code path; the inspector path
        /// is CoreAILifetimeScope's Role Skills list). The role gets a <c>read_skill</c> catalog on
        /// first use; skills added later — even mid-session — are immediately readable. Build the
        /// <see cref="SkillSet"/> from code, <see cref="SkillSet.FromTextContent"/> (e.g. a
        /// <c>TextAsset.text</c>), <see cref="SkillSet.FromFile"/>, or
        /// <c>SkillSetAsset.BuildSkillSet()</c>. Returns whether the skill was registered.
        /// </summary>
        public static bool AddSkillForRole(string roleId, SkillSet skill)
        {
            if (string.IsNullOrWhiteSpace(roleId) || skill == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!TryResolve(out _, out _, out _) || _scope?.Container == null)
                {
                    LogFacadeWarning("[CoreAi] AddSkillForRole: CoreAI services not resolved.");
                    return false;
                }

                try
                {
                    AgentMemoryPolicy policy = (AgentMemoryPolicy)_scope.Container.Resolve(typeof(AgentMemoryPolicy));
                    if (policy == null)
                    {
                        return false;
                    }

                    policy.AddSkillForRole(roleId.Trim(), skill);
                    return true;
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] AddSkillForRole failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Autonomous-tool follow-up lift: after the model calls <c>capture_camera</c>, OpenAI tool results
        /// cannot carry images, so the host lifts the returned image into a follow-up USER <c>image_url</c>
        /// message before the next model call. Pass the raw <c>capture_camera</c> result JSON (e.g.
        /// <see cref="LlmToolCallCompleted.ResultJson"/> from <see cref="OnToolCallCompleted"/>). Returns
        /// <c>null</c> when the result carries no usable image or vision is disabled.
        /// </summary>
        public static Task<string> AskWithImageFollowUpAsync(
            string followUpPrompt,
            string captureCameraResultJson,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            return RequireChatService()
                .AskWithImageFollowUpAsync(followUpPrompt, captureCameraResultJson, roleId, cancellationToken);
        }

        /// <summary>
        /// Streams model text as stripped string chunks (<c>&lt;think&gt;</c> filtered). Terminal empty chunks are not yielded.
        /// </summary>
        public static async IAsyncEnumerable<string> StreamAsync(
            string userMessage,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            await foreach (LlmStreamChunk chunk in
                           svc.SendMessageStreamingAsync(userMessage, roleId, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    throw new InvalidOperationException($"CoreAi.StreamAsync failed: {chunk.Error}");
                }

                if (chunk.IsDone && string.IsNullOrEmpty(chunk.Text))
                {
                    yield break;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    yield return chunk.Text;
                }
            }
        }

        /// <summary>
        /// Raw streaming enumeration with <see cref="LlmStreamChunk"/> metadata (completion flag, errors, usage).
        /// </summary>
        public static IAsyncEnumerable<LlmStreamChunk> StreamChunksAsync(
            string userMessage,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            return svc.SendMessageStreamingAsync(userMessage, roleId, cancellationToken);
        }

        /// <summary>
        /// Streaming turn with a full <see cref="AiTaskRequest"/> (same path as
        /// <see cref="SendMessageStreamingAsync"/> for UI). Use this when the host must pass
        /// per-call fields such as <see cref="AiTaskRequest.AllowedToolNames"/> and
        /// <see cref="AiTaskRequest.ForcedToolMode"/> so streaming matches
        /// <see cref="OrchestrateAsync"/> semantics.
        /// </summary>
        public static IAsyncEnumerable<LlmStreamChunk> StreamChunksAsync(
            AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            return svc.SendMessageStreamingAsync(task, cancellationToken);
        }

        /// <summary>
        /// Chooses streaming vs buffered mode using <see cref="CoreAiChatService.IsStreamingEnabled(string, bool?)"/>.
        /// Optional <paramref name="onChunk"/> receives live text fragments; returns the full concatenated string.
        /// </summary>
        public static Task<string?> SmartAskAsync(
            string userMessage,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            Action<string>? onChunk = null,
            bool? uiStreamingOverride = null,
            CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            Action<LlmStreamChunk>? adapter = onChunk == null
                ? null
                : new Action<LlmStreamChunk>(chunk =>
                {
                    if (!string.IsNullOrEmpty(chunk.Text) && !chunk.IsDone)
                    {
                        onChunk(chunk.Text);
                    }
                });
            return svc.SendMessageSmartAsync(userMessage, roleId, adapter, uiStreamingOverride, cancellationToken)!;
        }

        /// <summary>
        /// Full orchestrator pass: telemetry snapshot, prompt composition, authority, queued execution,
        /// structured-response policy, <c>ApplyAiGameCommand</c> publication, metrics. Returns <c>null</c> when authority/validation blocks the turn.
        /// </summary>
        public static Task<string?> OrchestrateAsync(
            AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            IAiOrchestrationService svc = RequireOrchestrator();
            return svc.RunTaskAsync(task, cancellationToken)!;
        }

        /// <summary>
        /// Streaming counterpart to <see cref="OrchestrateAsync"/> emitting deltas, filtering <c>&lt;think&gt;</c>,
        /// then validating structure and publishing <c>ApplyAiGameCommand</c>. Failures surface as terminal <see cref="LlmStreamChunk.Error"/> payloads.
        /// </summary>
        public static IAsyncEnumerable<LlmStreamChunk> OrchestrateStreamAsync(
            AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            IAiOrchestrationService svc = RequireOrchestrator();
            return svc.RunStreamingAsync(task, cancellationToken);
        }

        /// <summary>
        /// Streams an orchestrator turn while accumulating the concatenated assistant text and forwarding fragments to <paramref name="onChunk"/>.
        /// </summary>
        public static async Task<string> OrchestrateStreamCollectAsync(
            AiTaskRequest task,
            Action<string>? onChunk = null,
            CancellationToken cancellationToken = default)
        {
            StringBuilder sb = new();
            await foreach (LlmStreamChunk chunk in OrchestrateStreamAsync(task, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    throw new InvalidOperationException($"CoreAi.OrchestrateStream failed: {chunk.Error}");
                }

                if (chunk.IsDone)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    sb.Append(chunk.Text);
                    onChunk?.Invoke(chunk.Text);
                }
            }

            return sb.ToString();
        }

        /// <summary>Returns the resolved chat service or throws when the CoreAI lifetime scope is unavailable.</summary>
        public static CoreAiChatService GetChatService()
        {
            return RequireChatService();
        }

        /// <summary>
        /// Non-throwing resolver: succeeds when <see cref="CoreAILifetimeScope"/> is present with a usable <see cref="ILlmClient"/>.
        /// </summary>
        public static bool TryGetChatService(out CoreAiChatService? chatService)
        {
            lock (SyncRoot)
            {
                if (!TryResolve(out chatService, out _, out _) || chatService == null)
                {
                    chatService = null;
                    return false;
                }

                return true;
            }
        }

        /// <summary>Returns the resolved orchestration service or throws when the CoreAI lifetime scope is unavailable.</summary>
        public static IAiOrchestrationService GetOrchestrator()
        {
            return RequireOrchestrator();
        }

        /// <summary>
        /// Non-throwing orchestrator resolver (expects <see cref="IAiOrchestrationService"/> after <c>RegisterCorePortable()</c>).
        /// </summary>
        public static bool TryGetOrchestrator(out IAiOrchestrationService? orchestrator)
        {
            lock (SyncRoot)
            {
                TryResolve(out _, out orchestrator, out _);
                if (orchestrator == null)
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>Reads host <see cref="ICoreAISettings"/> from DI when available.</summary>
        public static ICoreAISettings? GetSettings()
        {
            lock (SyncRoot)
            {
                if (_settings != null)
                {
                    return _settings;
                }

                TryResolve(out _, out _, out _settings);
                return _settings;
            }
        }

        /// <summary>Delegate for global tool lifecycle notifications.</summary>
        /// <param name="roleId">Agent role id.</param>
        /// <param name="toolName">Tool name.</param>
        /// <param name="arguments">Model-provided arguments (optional).</param>
        /// <param name="result">Tool return payload (optional).</param>
        public delegate void ToolExecutedHandler(string roleId, string toolName,
            IDictionary<string, object?>? arguments, object? result);

        /// <summary>
        /// Raised after the MEAI stack executes a tool (VFX, audio, analytics, etc.).
        /// <code>
        /// CoreAi.OnToolExecuted += (role, tool, args, result) => Debug.Log($"{role} used {tool}");
        /// </code>
        /// </summary>
        public static event ToolExecutedHandler? OnToolExecuted;

        /// <summary>Raised immediately before a model-requested tool call is executed.</summary>
        public static event Action<LlmToolCallStarted>? OnToolCallStarted;

        /// <summary>Raised after a model-requested tool call completes successfully.</summary>
        public static event Action<LlmToolCallCompleted>? OnToolCallCompleted;

        /// <summary>Raised after a model-requested tool call fails or returns an unsuccessful result.</summary>
        public static event Action<LlmToolCallFailed>? OnToolCallFailed;

        /// <summary>
        /// Subscribes to all tool-call lifecycle records through a disposable handle.
        /// Use this for gameplay observers, analytics, QA probes, and tests that need real tool execution data.
        /// </summary>
        /// <param name="handler">Called for started/completed/failed records.</param>
        /// <param name="replayExisting">When true, immediately replays the current bounded history snapshot.</param>
        /// <returns>Disposable subscription handle.</returns>
        public static IDisposable SubscribeToolCalls(Action<LlmToolCallRecord> handler, bool replayExisting = false)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (ToolCallSyncRoot)
            {
                OnToolCallRecord += handler;
            }

            if (replayExisting)
            {
                foreach (LlmToolCallRecord record in GetToolCallHistorySnapshot())
                {
                    InvokeToolCallRecordHandler(handler, record);
                }
            }

            return new DisposableAction(() =>
            {
                lock (ToolCallSyncRoot)
                {
                    OnToolCallRecord -= handler;
                }
            });
        }

        /// <summary>Returns a bounded snapshot of recent tool-call lifecycle records.</summary>
        public static IReadOnlyList<LlmToolCallRecord> GetToolCallHistorySnapshot()
        {
            return ToolCallHistory.Snapshot();
        }

        /// <summary>Clears the public tool-call history snapshot. Active subscribers remain registered.</summary>
        public static void ClearToolCallHistory()
        {
            ToolCallHistory.Clear();
        }

        /// <summary>Internal hook for <c>SmartToolCallingChatClient</c> to surface tool calls to <see cref="OnToolExecuted"/>.</summary>
        internal static void NotifyToolExecuted(string roleId, string toolName, IDictionary<string, object?>? arguments,
            object? result)
        {
            ToolExecutedHandler? handlers = OnToolExecuted;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((ToolExecutedHandler)handler).Invoke(roleId, toolName, arguments, result);
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] OnToolExecuted handler error: {ex.Message}");
                }
            }
        }

        /// <summary>Internal hook for tool-call lifecycle publishers.</summary>
        internal static void NotifyToolCallStarted(LlmToolCallStarted evt)
        {
            ToolCallHistory.RecordStarted(evt);
            PublishToolCallRecord(new LlmToolCallRecord { Info = evt.Info, Status = "started" });
            PublishToolCallEvent(OnToolCallStarted, evt, "OnToolCallStarted");
        }

        /// <summary>Internal hook for tool-call lifecycle publishers.</summary>
        internal static void NotifyToolCallCompleted(LlmToolCallCompleted evt)
        {
            ToolCallHistory.RecordCompleted(evt);
            PublishToolCallRecord(new LlmToolCallRecord
            {
                Info = evt.Info,
                Status = "completed",
                ResultJson = evt.ResultJson,
                DurationMs = evt.DurationMs
            });
            PublishToolCallEvent(OnToolCallCompleted, evt, "OnToolCallCompleted");
        }

        /// <summary>Internal hook for tool-call lifecycle publishers.</summary>
        internal static void NotifyToolCallFailed(LlmToolCallFailed evt)
        {
            ToolCallHistory.RecordFailed(evt);
            PublishToolCallRecord(new LlmToolCallRecord
            {
                Info = evt.Info,
                Status = "failed",
                Error = evt.Error,
                DurationMs = evt.DurationMs
            });
            PublishToolCallEvent(OnToolCallFailed, evt, "OnToolCallFailed");
        }

        /// <summary>
        /// Cancels queued/in-flight orchestrator work for <paramref name="cancellationScope"/> (typically a role id).
        /// </summary>
        public static void StopAgent(string cancellationScope)
        {
            if (TryGetOrchestrator(out IAiOrchestrationService? orchestrator) && orchestrator != null)
            {
                orchestrator.CancelTasks(cancellationScope);
            }
        }

        /// <summary>Clears chat history and/or long-term memory state for <paramref name="roleId"/>.</summary>
        /// <param name="roleId">Role id.</param>
        /// <param name="clearChatHistory">Drop persisted chat turns.</param>
        /// <param name="clearLongTermMemory">Drop MemoryTool state / saved agent memory.</param>
        public static void ClearContext(string roleId, bool clearChatHistory = true, bool clearLongTermMemory = true)
        {
            lock (SyncRoot)
            {
                if (TryResolve(out _, out _, out _) && _scope?.Container != null)
                {
                    try
                    {
                        IAgentMemoryStore? memStore =
                            (IAgentMemoryStore)_scope.Container.Resolve(typeof(IAgentMemoryStore));
                        if (memStore != null)
                        {
                            if (clearLongTermMemory)
                            {
                                memStore.Clear(roleId);
                            }

                            if (clearChatHistory)
                            {
                                memStore.ClearChatHistory(roleId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFacadeWarning($"[CoreAi] ClearContext memory resolve: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Force-injects a <paramref name="skill"/> into <paramref name="roleId"/>'s conversation history
        /// — as if the agent had already called <c>read_skill</c> — without running a model turn. The agent
        /// does not start writing a response; the skill's instructions and tools are just pushed into its
        /// history and read on its next turn. Stored with the internal "tool" role, so the model sees it but
        /// the visible chat stays clean. Returns false when CoreAI is not resolved or the skill is null.
        /// </summary>
        /// <param name="roleId">Target agent role.</param>
        /// <param name="skill">Skill to preload (the host already holds the instance).</param>
        /// <param name="persistToDisk">Persist the appended history immediately (default true).</param>
        public static bool InjectSkillIntoHistory(string roleId, SkillSet skill, bool persistToDisk = true)
        {
            if (skill == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!TryResolve(out _, out _, out _) || _scope?.Container == null)
                {
                    LogFacadeWarning("[CoreAi] InjectSkillIntoHistory: CoreAI services not resolved.");
                    return false;
                }

                try
                {
                    IAgentMemoryStore memStore =
                        (IAgentMemoryStore)_scope.Container.Resolve(typeof(IAgentMemoryStore));
                    return AgentSkillInjection.InjectSkillIntoHistory(memStore, roleId, skill, persistToDisk);
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] InjectSkillIntoHistory failed: {ex.Message}");
                    return false;
                }
            }
        }

        private static CoreAiChatService RequireChatService()
        {
            lock (SyncRoot)
            {
                if (_chatService != null)
                {
                    return _chatService;
                }

                if (!TryResolve(out _chatService, out _, out _settings) || _chatService == null)
                {
                    throw new InvalidOperationException(
                        "CoreAi: CoreAILifetimeScope was not found or ILlmClient is not registered. " +
                        "Add CoreAILifetimeScope to the scene or call CoreAi.Invalidate() after changing scenes.");
                }

                return _chatService;
            }
        }

        private static IAiOrchestrationService RequireOrchestrator()
        {
            lock (SyncRoot)
            {
                if (_orchestrator != null)
                {
                    return _orchestrator;
                }

                if (!TryResolve(out _, out _orchestrator, out _settings) || _orchestrator == null)
                {
                    throw new InvalidOperationException(
                        "CoreAi: IAiOrchestrationService is not registered on CoreAILifetimeScope. " +
                        "Ensure builder.RegisterCorePortable() runs inside Configure().");
                }

                return _orchestrator;
            }
        }

        private static bool TryResolve(
            out CoreAiChatService? chatService,
            out IAiOrchestrationService? orchestrator,
            out ICoreAISettings? settings)
        {
            chatService = null;
            orchestrator = null;
            settings = null;

            if (_orchestratorResolver != null)
            {
                try
                {
                    orchestrator = _orchestratorResolver.Invoke();
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] Resolve orchestrator from custom resolver failed: {ex.Message}");
                }
            }

            if (_scope == null || _scope.Container == null)
            {
                if (_orchestratorResolver != null && orchestrator != null)
                {
                    chatService = null;
                    _orchestrator = orchestrator;
                    return true;
                }

                _scope = UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            }

            if (_scope == null || _scope.Container == null)
            {
                if (_orchestratorResolver != null && orchestrator != null)
                {
                    chatService = null;
                    _orchestrator = orchestrator;
                    return true;
                }

                return false;
            }

            try
            {
                orchestrator = (IAiOrchestrationService)_scope.Container.Resolve(typeof(IAiOrchestrationService));
            }
            catch (Exception ex)
            {
                LogFacadeWarning($"[CoreAi] Resolve IAiOrchestrationService: {ex.Message}");
            }

            try
            {
                settings = (ICoreAISettings)_scope.Container.Resolve(typeof(ICoreAISettings));
            }
            catch (Exception ex)
            {
                LogFacadeWarning($"[CoreAi] Resolve ICoreAISettings: {ex.Message}");
            }

            if (_chatService == null)
            {
                _chatService = CoreAiChatService.TryCreateFromScene();
            }

            chatService = _chatService;
            if (orchestrator != null)
            {
                _orchestrator = orchestrator;
            }

            _settings = settings;
            return chatService != null || orchestrator != null;
        }

        private static void PublishToolCallRecord(LlmToolCallRecord record)
        {
            Action<LlmToolCallRecord>? handlers;
            lock (ToolCallSyncRoot)
            {
                handlers = OnToolCallRecord;
            }

            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                InvokeToolCallRecordHandler((Action<LlmToolCallRecord>)handler, record);
            }
        }

        private static void InvokeToolCallRecordHandler(Action<LlmToolCallRecord> handler, LlmToolCallRecord record)
        {
            try
            {
                handler(record);
            }
            catch (Exception ex)
            {
                LogFacadeWarning($"[CoreAi] Tool-call subscriber error: {ex.Message}");
            }
        }

        private static void PublishToolCallEvent<T>(Action<T>? handlers, T evt, string eventName)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)handler).Invoke(evt);
                }
                catch (Exception ex)
                {
                    LogFacadeWarning($"[CoreAi] {eventName} handler error: {ex.Message}");
                }
            }
        }

        private sealed class DisposableAction : IDisposable
        {
            private Action? _dispose;

            public DisposableAction(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _dispose, null)?.Invoke();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSubsystemRegistration()
        {
            Invalidate();
        }
    }
}