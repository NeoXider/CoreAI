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
using UnityEngine;

namespace CoreAI
{
    /// <summary>
    /// One-line CoreAI entrypoint: buffered (<c>await</c>) and streaming LLM calls over
    /// <see cref="IAiOrchestrationService"/> (queue, metrics, command publish) without hand-wiring VContainer.
    /// <para><b>Quick start.</b> Add <see cref="CoreAILifetimeScope"/> to a scene, then:</para>
    /// <code>
    /// // Buffered reply (always await; do not use .Result/.Wait on Unity’s main thread):
    /// string answer = await CoreAi.AskAsync("Hello!", roleId: "PlayerChat");
    ///
    /// // Streaming string chunks:
    /// await foreach (string chunk in CoreAi.StreamAsync("Tell a joke", "PlayerChat"))
    ///     label.text += chunk;
    ///
    /// // Smart path (streaming if enabled in settings / agent / UI):
    /// await CoreAi.SmartAskAsync("Question", "PlayerChat", onChunk: c => label.text += c);
    ///
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
        private static ICoreAISettings? _settings;

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
                _settings = null;
            }
        }

        /// <summary>
        /// Sends a chat turn and waits for the full model text (non-streaming). Persists history for
        /// <paramref name="roleId"/> when ChatHistory is enabled.
        /// </summary>
        /// <remarks>Use <c>await</c> only; blocking the Unity main thread via <c>.Result</c>/<c>.Wait()</c> risks deadlocks with MEAI marshaling.</remarks>
        public static async Task<string?> AskAsync(
            string userMessage,
            string roleId = "PlayerChat",
            CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            return await svc.SendMessageAsync(userMessage, roleId, cancellationToken);
        }

        /// <summary>
        /// Streams model text as stripped string chunks (<c>&lt;think&gt;</c> filtered). Terminal empty chunks are not yielded.
        /// </summary>
        public static async IAsyncEnumerable<string> StreamAsync(
            string userMessage,
            string roleId = "PlayerChat",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            await foreach (LlmStreamChunk chunk in svc.SendMessageStreamingAsync(userMessage, roleId, cancellationToken))
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
            string roleId = "PlayerChat",
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
            string roleId = "PlayerChat",
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

        /// <summary>Gets or resolves the chat service.</summary>
        public static CoreAiChatService GetChatService() => RequireChatService();

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

        /// <summary>Gets or resolves <see cref="IAiOrchestrationService"/>.</summary>
        public static IAiOrchestrationService GetOrchestrator() => RequireOrchestrator();

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
                if (_settings != null) return _settings;
                TryResolve(out _, out _, out _settings);
                return _settings;
            }
        }

        /// <summary>Delegate for global tool lifecycle notifications.</summary>
        /// <param name="roleId">Agent role id.</param>
        /// <param name="toolName">Tool name.</param>
        /// <param name="arguments">Model-provided arguments (optional).</param>
        /// <param name="result">Tool return payload (optional).</param>
        public delegate void ToolExecutedHandler(string roleId, string toolName, IDictionary<string, object?>? arguments, object? result);

        /// <summary>
        /// Raised after the MEAI stack executes a tool (VFX, audio, analytics, etc.).
        /// <code>
        /// CoreAi.OnToolExecuted += (role, tool, args, result) => Debug.Log($"{role} used {tool}");
        /// </code>
        /// </summary>
        public static event ToolExecutedHandler? OnToolExecuted;

        /// <summary>Internal hook for <c>SmartToolCallingChatClient</c> to surface tool calls to <see cref="OnToolExecuted"/>.</summary>
        internal static void NotifyToolExecuted(string roleId, string toolName, IDictionary<string, object?>? arguments, object? result)
        {
            try
            {
                OnToolExecuted?.Invoke(roleId, toolName, arguments, result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoreAi] OnToolExecuted handler error: {ex.Message}");
            }
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
                        var memStore = (IAgentMemoryStore)_scope.Container.Resolve(typeof(IAgentMemoryStore));
                        if (memStore != null)
                        {
                            if (clearLongTermMemory) memStore.Clear(roleId);
                            if (clearChatHistory) memStore.ClearChatHistory(roleId);
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[CoreAi] ClearContext memory resolve: {ex.Message}");
                    }
                }
            }
        }

        private static CoreAiChatService RequireChatService()
        {
            lock (SyncRoot)
            {
                if (_chatService != null) return _chatService;
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
                if (_orchestrator != null) return _orchestrator;
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

            if (_scope == null || _scope.Container == null)
            {
                _scope = UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            }

            if (_scope == null || _scope.Container == null)
            {
                return false;
            }

            try
            {
                orchestrator = (IAiOrchestrationService)_scope.Container.Resolve(typeof(IAiOrchestrationService));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CoreAi] Resolve IAiOrchestrationService: {ex.Message}");
            }

            try
            {
                settings = (ICoreAISettings)_scope.Container.Resolve(typeof(ICoreAISettings));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CoreAi] Resolve ICoreAISettings: {ex.Message}");
            }

            if (_chatService == null)
            {
                _chatService = CoreAiChatService.TryCreateFromScene();
            }

            chatService = _chatService;
            _orchestrator = orchestrator;
            _settings = settings;
            return chatService != null || orchestrator != null;
        }
    }
}
