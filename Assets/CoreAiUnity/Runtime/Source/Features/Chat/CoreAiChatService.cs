using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// Application service that sends chat requests through the AI orchestrator.
    /// </summary>
    public class CoreAiChatService
    {
        private readonly IAiOrchestrationService _orchestrator;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly ICoreAISettings _settings;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly IGameLogger _logger;
        private readonly ILlmClient _llmClient;
        private readonly Vision.IAgentCameraService _cameraService;

        public CoreAiChatService(
            IAiOrchestrationService orchestrator,
            AgentMemoryPolicy memoryPolicy = null,
            ICoreAISettings settings = null,
            IAgentMemoryStore memoryStore = null,
            IGameLogger logger = null,
            ILlmClient llmClient = null,
            Vision.IAgentCameraService cameraService = null)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _memoryPolicy = memoryPolicy;
            _settings = settings;
            _memoryStore = memoryStore;
            _logger = logger;
            _llmClient = llmClient;
            _cameraService = cameraService;
        }

        /// <summary>
        /// Attempts to create a chat service from the active CoreAI lifetime scope in the scene.
        /// </summary>
        public static CoreAiChatService TryCreateFromScene()
        {
            CoreAILifetimeScope scope =
                UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            if (scope?.Container == null)
            {
                return null;
            }

            try
            {
                IAiOrchestrationService orchestrator =
                    (IAiOrchestrationService)scope.Container.Resolve(typeof(IAiOrchestrationService));
                AgentMemoryPolicy policy = null;
                ICoreAISettings settings = null;
                IAgentMemoryStore memStore = null;
                IGameLogger logger = null;
                ILlmClient llmClient = null;
                Vision.IAgentCameraService cameraService = null;

                try
                {
                    policy = (AgentMemoryPolicy)scope.Container.Resolve(typeof(AgentMemoryPolicy));
                }
                catch (Exception ex)
                {
                    GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatService] Resolve AgentMemoryPolicy: {ex.Message}");
                }

                try
                {
                    settings = (ICoreAISettings)scope.Container.Resolve(typeof(ICoreAISettings));
                }
                catch (Exception ex)
                {
                    GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatService] Resolve ICoreAISettings: {ex.Message}");
                }

                try
                {
                    memStore = (IAgentMemoryStore)scope.Container.Resolve(typeof(IAgentMemoryStore));
                }
                catch (Exception ex)
                {
                    GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatService] Resolve IAgentMemoryStore: {ex.Message}");
                }

                try
                {
                    logger = (IGameLogger)scope.Container.Resolve(typeof(IGameLogger));
                }
                catch (Exception ex)
                {
                    GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatService] Resolve IGameLogger: {ex.Message}");
                }

                try
                {
                    llmClient = (ILlmClient)scope.Container.Resolve(typeof(ILlmClient));
                }
                catch (Exception ex)
                {
                    GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.Core,
                        $"[CoreAiChatService] Resolve ILlmClient: {ex.Message}");
                }

                // WHY: agent-vision is optional wiring (WorldCommandsInstaller.RegisterAgentVision registers
                // it). Minimal containers without a world executor simply resolve null, so the camera tool is
                // silently skipped and text-only chat is unaffected.
                try
                {
                    cameraService =
                        (Vision.IAgentCameraService)scope.Container.Resolve(typeof(Vision.IAgentCameraService));
                }
                catch (Exception ex)
                {
                    // WHY: optional service — absent whenever the scene has no world executor; Debug, not Warning.
                    GameLoggerUnscopedFallback.Instance.LogDebug(GameLogFeature.Core,
                        $"[CoreAiChatService] Resolve IAgentCameraService (optional): {ex.Message}");
                }

                return new CoreAiChatService(
                    orchestrator, policy, settings, memStore, logger, llmClient, cameraService);
            }
            catch (Exception ex)
            {
                GameLoggerUnscopedFallback.Instance.LogError(GameLogFeature.Core,
                    $"[CoreAiChatService] Failed to create from scene: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Sends a chat message through the AI orchestrator and returns the final response text.
        /// </summary>
        public System.Threading.Tasks.Task<string> SendMessageAsync(
            string userText,
            string roleId,
            CancellationToken ct = default)
        {
            AiTaskRequest request = new()
            {
                RoleId = roleId,
                Hint = userText,
                SourceTag = "Chat"
            };

            return SendMessageAsync(request, ct);
        }

        /// <summary>
        /// Sends a chat message through the AI orchestrator and returns the final response text.
        /// </summary>
        /// <remarks>
        /// Timeout is enforced here via <c>CancelAfterSlim</c> (UniTask, PlayerLoop-based)
        /// so WebGL builds do not rely on thread-pool timers.
        /// Exceptions are NOT swallowed; callers (e.g. <c>CoreAiChatPanel</c>) are responsible
        /// for catching and displaying errors to the user.
        /// </remarks>
        public async System.Threading.Tasks.Task<string> SendMessageAsync(
            AiTaskRequest request,
            CancellationToken ct = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            float timeoutSec = 0f;
            CancellationTokenSource timeoutCts = null;
            IDisposable timerHandle = null;
            CancellationToken effectiveCt = ct;
            try
            {
                timeoutSec = _settings?.LlmRequestTimeoutSeconds ?? 0f;
                if (timeoutSec > 0)
                {
                    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timerHandle = timeoutCts.CancelAfterSlim(
                        TimeSpan.FromSeconds(timeoutSec), DelayType.Realtime);
                    effectiveCt = timeoutCts.Token;
                }

                string result = await _orchestrator.RunTaskAsync(request, effectiveCt);
                // Orchestrator + LLM stack use ConfigureAwait(false); marshal to player loop for UI.
                // WebGL player: see CoreAiWebGlUiThreadMarshaling (Editor WebGL keeps full switch).
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                return result ?? "";
            }
            catch (OperationCanceledException) when (timeoutSec > 0f && !ct.IsCancellationRequested)
            {
                // Linked-token / CancelAfterSlim may not set CTS.IsCancellationRequested in the same
                // and the caller token instead of probing the linked source.
                throw new LlmOperationTimeoutException();
            }
            finally
            {
                timerHandle?.Dispose();
                timeoutCts?.Dispose();
            }
        }

        /// <summary>
        /// Sends a chat message through the AI orchestrator and streams response chunks.
        /// </summary>
        public IAsyncEnumerable<LlmStreamChunk> SendMessageStreamingAsync(
            string userText,
            string roleId,
            CancellationToken ct = default)
        {
            AiTaskRequest request = new()
            {
                RoleId = roleId,
                Hint = userText,
                SourceTag = "Chat"
            };

            return SendMessageStreamingAsync(request, ct);
        }

        /// <summary>
        /// Sends a chat message through the AI orchestrator and streams response chunks.
        /// </summary>
        /// <remarks>
        /// Timeout is enforced here via <c>CancelAfterSlim</c> (UniTask, PlayerLoop-based)
        /// and cancellation is propagated to the underlying async enumerator.
        /// </remarks>
        public async IAsyncEnumerable<LlmStreamChunk> SendMessageStreamingAsync(
            AiTaskRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            float timeoutSec = 0f;
            CancellationTokenSource timeoutCts = null;
            IDisposable timerHandle = null;
            CancellationToken effectiveCt = ct;
            IAsyncEnumerator<LlmStreamChunk> streamEnumerator = null;
            try
            {
                timeoutSec = _settings?.LlmRequestTimeoutSeconds ?? 0f;
                if (timeoutSec > 0)
                {
                    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timerHandle = timeoutCts.CancelAfterSlim(
                        TimeSpan.FromSeconds(timeoutSec), DelayType.Realtime);
                    effectiveCt = timeoutCts.Token;
                }

                streamEnumerator = _orchestrator.RunStreamingAsync(request, effectiveCt).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await streamEnumerator.MoveNextAsync();
                        }
                        catch (OperationCanceledException) when (timeoutSec > 0f && !ct.IsCancellationRequested)
                        {
                            throw new LlmOperationTimeoutException();
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        yield return streamEnumerator.Current;
                    }
                }
                finally
                {
                    if (streamEnumerator != null)
                    {
                        await streamEnumerator.DisposeAsync();
                    }
                }
            }
            finally
            {
                timerHandle?.Dispose();
                timeoutCts?.Dispose();
            }
        }

        /// <summary>
        /// Gives the chat agent <paramref name="roleId"/> the runtime <c>camera</c> tool when
        /// <paramref name="enabled"/> is true and the agent-vision service is available. Idempotent and
        /// silently degrades (returns false) when disabled, when no vision service was resolved, or when the
        /// role already has the tool. Vision-capable models can then see and refine their own work; text-only
        /// models never call it. See <see cref="CoreAiChatCameraTools"/>.
        /// </summary>
        public bool TryEnsureCameraToolForRole(string roleId, bool enabled)
        {
            return CoreAiChatCameraTools.TryAttachCameraTool(_memoryPolicy, _cameraService, roleId, enabled);
        }

        /// <summary>Clears the stored chat history for the selected agent role.</summary>
        public void ClearHistory(string roleId)
        {
            _memoryStore?.ClearChatHistory(roleId);
        }

        /// <summary>
        /// Attempts to get persisted chat history and returns whether the operation succeeded.
        /// </summary>
        public bool TryGetPersistedChatHistory(string roleId, out Ai.ChatMessage[] messages, int maxMessages = 0)
        {
            messages = Array.Empty<Ai.ChatMessage>();
            if (_memoryStore == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            Ai.ChatMessage[] raw = _memoryStore.GetChatHistory(roleId.Trim(), maxMessages);
            if (raw == null || raw.Length == 0)
            {
                return false;
            }

            messages = raw;
            return true;
        }

        /// <summary>Requests cancellation of active work for the selected agent role.</summary>
        public void StopAgent(string roleId)
        {
            CoreAi.StopAgent(roleId);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// WebGL player: incremental SSE needs the native fetch bridge; otherwise UnityWebRequest delivers one chunk.
        /// Checks DI <see cref="ICoreAISettings"/> when it is a <see cref="CoreAISettingsAsset"/>, then falls back to
        /// <see cref="CoreAISettingsAsset.Instance"/> so streaming is not accidentally disabled when settings are only
        /// injected on <c>CoreAILifetimeScope</c> and Resources holds a different asset.
        /// </summary>
        private static bool WebGlNativeStreamingBridgeEnabled(ICoreAISettings settings)
        {
            if (settings is CoreAISettingsAsset fromDi && fromDi.WebGlNativeStreaming)
            {
                return true;
            }

            CoreAISettingsAsset inst = CoreAISettingsAsset.Instance;
            return inst != null && inst.WebGlNativeStreaming;
        }

        private static bool s_loggedWebGlNativeStreamingDisabled;

        /// <summary>
        /// WebGL player: one-time hint when UI/agent would allow streaming but the fetch bridge is off.
        /// </summary>
        private void MaybeLogWebGlStreamingRequiresNativeFetch(bool uiAllowsStreaming)
        {
            if (!uiAllowsStreaming || s_loggedWebGlNativeStreamingDisabled)
            {
                return;
            }

            s_loggedWebGlNativeStreamingDisabled = true;
            const string body =
                "WebGL player: incremental chat streaming needs CoreAISettingsAsset.WebGlNativeStreaming = true " +
                "(fetch + ReadableStream via CoreAiSseFetch.jslib). While it is off, UnityWebRequest buffers the full " +
                "response and this service uses non-streaming mode; streaming markers are absent and " +
                "the reply appears all at once.";
            (_logger ?? GameLoggerUnscopedFallback.Instance)
                .LogWarning(GameLogFeature.Core, "[CoreAiChatService] " + body);
        }
#endif

        /// <summary>
        /// Resolves whether the chat turn should stream by applying WebGL transport support,
        /// the UI fallback flag, per-role memory policy, and global settings in that order.
        /// <list type="number">
        /// <item><description>WebGL requires the native fetch bridge for incremental SSE.</description></item>
        /// <item><description>A disabled UI fallback always disables streaming.</description></item>
        /// <item><description>Per-role policy wins over the global setting when available.</description></item>
        /// </list>
        /// </summary>
        public bool IsStreamingEnabled(string roleId, bool uiFallback = true)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGlNativeStreamingBridgeEnabled(_settings))
            {
                MaybeLogWebGlStreamingRequiresNativeFetch(uiFallback);
                return false;
            }
#endif
            if (!uiFallback)
            {
                return false;
            }

            if (_memoryPolicy != null)
            {
                return _memoryPolicy.IsStreamingEnabled(roleId, _settings);
            }

            if (_settings != null)
            {
                return _settings.EnableStreaming;
            }

            return CoreAISettings.EnableStreaming;
        }

        /// <summary>
        /// Resolves whether the chat turn should stream when the UI may provide an explicit override.
        /// <para>
        /// A value of <c>false</c> disables streaming immediately; <c>true</c> and <c>null</c>
        /// continue through per-role policy and then <see cref="ICoreAISettings.EnableStreaming"/>.
        /// </para>
        /// </summary>
        public bool IsStreamingEnabled(string roleId, bool? uiOverride = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGlNativeStreamingBridgeEnabled(_settings))
            {
                MaybeLogWebGlStreamingRequiresNativeFetch(uiOverride != false);
                return false;
            }
#endif
            if (uiOverride == false)
            {
                return false;
            }

            if (_memoryPolicy != null)
            {
                return _memoryPolicy.IsStreamingEnabled(roleId, _settings);
            }

            if (_settings != null)
            {
                return _settings.EnableStreaming;
            }

            return CoreAISettings.EnableStreaming;
        }

        /// <summary>
        /// Sends a chat turn using streaming when enabled, otherwise falls back to buffered completion.
        /// </summary>
        public async System.Threading.Tasks.Task<string> SendMessageSmartAsync(
            string userText,
            string roleId,
            Action<LlmStreamChunk> onChunk = null,
            bool? uiStreamingOverride = null,
            CancellationToken ct = default)
        {
            if (IsStreamingEnabled(roleId, uiStreamingOverride))
            {
                StringBuilder sb = new();
                await foreach (LlmStreamChunk chunk in SendMessageStreamingAsync(userText, roleId, ct))
                {
                    onChunk?.Invoke(chunk);
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        sb.Append(chunk.Text);
                    }
                }

                return sb.ToString();
            }

            string full = await SendMessageAsync(userText, roleId, ct);
            if (onChunk != null && !string.IsNullOrEmpty(full))
            {
                onChunk(new LlmStreamChunk { Text = full });
                onChunk(new LlmStreamChunk { IsDone = true });
            }

            return full;
        }

        /// <summary>
        /// Sends a prepared AI task using streaming when enabled, otherwise falls back to buffered completion.
        /// </summary>
        public async System.Threading.Tasks.Task<string> SendMessageSmartAsync(
            AiTaskRequest request,
            Action<LlmStreamChunk> onChunk = null,
            bool? uiStreamingOverride = null,
            CancellationToken ct = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (IsStreamingEnabled(request.RoleId, uiStreamingOverride))
            {
                StringBuilder sb = new();
                await foreach (LlmStreamChunk chunk in SendMessageStreamingAsync(request, ct))
                {
                    onChunk?.Invoke(chunk);
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        sb.Append(chunk.Text);
                    }
                }

                return sb.ToString();
            }

            string full = await SendMessageAsync(request, ct);
            if (onChunk != null && !string.IsNullOrEmpty(full))
            {
                onChunk(new LlmStreamChunk { Text = full });
                onChunk(new LlmStreamChunk { IsDone = true });
            }

            return full;
        }

        // ===== Vision / multimodal (P2) =====

        /// <summary>
        /// Whether the configured model can receive images. Reads
        /// <see cref="CoreAISettingsAsset.IsVisionEnabled"/> when settings are a
        /// <see cref="CoreAISettingsAsset"/>, otherwise infers from <see cref="VisionCapability"/> using the
        /// model name where exposed. Both the camera send path and any gated vision-tool registration check
        /// this; when it is <c>false</c> no image is attached and vision tools are omitted.
        /// </summary>
        public bool IsVisionEnabled()
        {
            if (_settings is CoreAISettingsAsset asset)
            {
                return asset.IsVisionEnabled;
            }

            CoreAISettingsAsset inst = CoreAISettingsAsset.Instance;
            return inst != null && inst.IsVisionEnabled;
        }

        /// <summary>
        /// Captures <paramref name="cameraName"/> and sends it to a vision-capable model as a single USER
        /// message (prompt text + the JPEG screenshot as an <c>image_url</c> part). This is the working,
        /// provider-safe camera → model path. Resolves the camera on the main thread, then issues one
        /// <see cref="ILlmClient.CompleteAsync"/> call carrying the image via
        /// <see cref="LlmCompletionRequest.ChatHistory"/> (a user <see cref="Microsoft.Extensions.AI.ChatMessage"/>
        /// whose <see cref="DataContent"/> is serialized to OpenAI <c>image_url</c> by the provider client).
        /// <para>
        /// Gated by <see cref="IsVisionEnabled"/>: when the configured model is text-only this throws
        /// <see cref="InvalidOperationException"/> (the caller should not reach this path for text-only
        /// models). Returns the model's text reply.
        /// </para>
        /// </summary>
        public System.Threading.Tasks.Task<string> AskWithCameraAsync(
            string prompt,
            string cameraName = "main",
            string roleId = BuiltInAgentRoleIds.SmartChat,
            int width = 512,
            int height = 512,
            CancellationToken ct = default)
        {
            return AskWithCameraInternalAsync(prompt, cameraName, null, roleId, width, height, ct);
        }

        /// <summary>
        /// Camera overload that accepts an already-resolved <see cref="Camera"/> (no name lookup). See
        /// <see cref="AskWithCameraAsync(string,string,string,int,int,CancellationToken)"/>.
        /// </summary>
        public System.Threading.Tasks.Task<string> AskWithCameraAsync(
            string prompt,
            Camera camera,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            int width = 512,
            int height = 512,
            CancellationToken ct = default)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            return AskWithCameraInternalAsync(prompt, null, camera, roleId, width, height, ct);
        }

        private async System.Threading.Tasks.Task<string> AskWithCameraInternalAsync(
            string prompt,
            string cameraName,
            Camera camera,
            string roleId,
            int width,
            int height,
            CancellationToken ct)
        {
            if (!IsVisionEnabled())
            {
                throw new InvalidOperationException(
                    "CoreAiChatService.AskWithCameraAsync: the configured model is text-only " +
                    "(VisionSupport=Off or the model name is not vision-capable). " +
                    "Gate camera sends on IsVisionEnabled() and fall back to AskAsync for text-only models.");
            }

            await UniTask.SwitchToMainThread(ct);
            DataContent image;
            try
            {
                Camera targetCam = camera != null ? camera : CameraLlmTool.ResolveCamera(cameraName);
                if (targetCam == null)
                {
                    throw new InvalidOperationException(
                        $"CoreAiChatService.AskWithCameraAsync: no camera matching '{cameraName}' was found.");
                }

                image = CameraLlmTool.CaptureCameraImageContent(targetCam, width, height);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }

            return await SendUserImageMessageAsync(prompt, image, roleId, ct);
        }

        /// <summary>
        /// Autonomous-tool follow-up lift. OpenAI tool-result messages cannot carry images, so after the
        /// model calls <c>capture_camera</c> the host lifts the returned image into a follow-up USER
        /// <c>image_url</c> message before the next model call. Pass the raw <c>capture_camera</c> tool
        /// result JSON (e.g. <c>LlmToolCallCompleted.ResultJson</c> from <c>CoreAi.OnToolCallCompleted</c>);
        /// the image is extracted via <see cref="CameraLlmTool.TryExtractImageContentFromResult"/> and sent
        /// with <paramref name="followUpPrompt"/>. Returns <c>null</c> when the result carries no usable
        /// image (so the caller can keep the plain text turn). Gated by <see cref="IsVisionEnabled"/>.
        /// </summary>
        public async System.Threading.Tasks.Task<string> AskWithImageFollowUpAsync(
            string followUpPrompt,
            string captureCameraResultJson,
            string roleId = BuiltInAgentRoleIds.SmartChat,
            CancellationToken ct = default)
        {
            if (!IsVisionEnabled())
            {
                return null;
            }

            if (!CameraLlmTool.TryExtractImageContentFromResult(captureCameraResultJson, out DataContent image))
            {
                return null;
            }

            return await SendUserImageMessageAsync(followUpPrompt, image, roleId, ct);
        }

        /// <summary>
        /// Sends a single USER message carrying <paramref name="prompt"/> plus <paramref name="image"/> to
        /// the LLM client. The image rides through <see cref="LlmCompletionRequest.ChatHistory"/> as a user
        /// <see cref="Microsoft.Extensions.AI.ChatMessage"/> with a <see cref="DataContent"/>, which the provider
        /// client serializes to an OpenAI <c>image_url</c> content part. Honors the configured request timeout.
        /// </summary>
        private async System.Threading.Tasks.Task<string> SendUserImageMessageAsync(
            string prompt,
            DataContent image,
            string roleId,
            CancellationToken ct)
        {
            if (_llmClient == null)
            {
                throw new InvalidOperationException(
                    "CoreAiChatService: ILlmClient was not resolved; the vision send path is unavailable. " +
                    "Ensure CoreAILifetimeScope.RegisterLlmPipeline() ran before resolving the chat service.");
            }

            Microsoft.Extensions.AI.ChatMessage userMessage = new(ChatRole.User, new List<AIContent>
            {
                new TextContent(prompt ?? ""),
                image
            });

            LlmCompletionRequest request = new()
            {
                AgentRoleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.SmartChat : roleId,
                SystemPrompt = "",
                UserPayload = "",
                ChatHistory = new List<Microsoft.Extensions.AI.ChatMessage> { userMessage },
                ContextWindowTokens = _settings?.ContextWindowTokens ?? CoreAISettings.DefaultContextWindowTokens
            };

            float timeoutSec = _settings?.LlmRequestTimeoutSeconds ?? 0f;
            CancellationTokenSource timeoutCts = null;
            IDisposable timerHandle = null;
            CancellationToken effectiveCt = ct;
            try
            {
                if (timeoutSec > 0)
                {
                    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timerHandle = timeoutCts.CancelAfterSlim(
                        TimeSpan.FromSeconds(timeoutSec), DelayType.Realtime);
                    effectiveCt = timeoutCts.Token;
                }

                LlmCompletionResult result = await _llmClient.CompleteAsync(request, effectiveCt);
                await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional(CancellationToken.None);
                if (result == null)
                {
                    return "";
                }

                if (!result.Ok)
                {
                    throw new InvalidOperationException(
                        $"CoreAiChatService vision request failed: {result.Error}");
                }

                return result.Content ?? "";
            }
            catch (OperationCanceledException) when (timeoutSec > 0f && !ct.IsCancellationRequested)
            {
                throw new LlmOperationTimeoutException();
            }
            finally
            {
                timerHandle?.Dispose();
                timeoutCts?.Dispose();
            }
        }
    }
}
