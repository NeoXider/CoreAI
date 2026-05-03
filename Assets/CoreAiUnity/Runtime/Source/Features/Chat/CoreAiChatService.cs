using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// Сервис чата CoreAI: streaming и non-streaming отправка сообщений,
    /// автоматическая работа с chat history и prompt composition.
    /// Не зависит от UI — можно использовать программно.
    /// </summary>
    public class CoreAiChatService
    {
        private readonly IAiOrchestrationService _orchestrator;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly ICoreAISettings _settings;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly IGameLogger _logger;

        public CoreAiChatService(
            IAiOrchestrationService orchestrator,
            AgentMemoryPolicy memoryPolicy = null,
            ICoreAISettings settings = null,
            IAgentMemoryStore memoryStore = null,
            IGameLogger logger = null)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _memoryPolicy = memoryPolicy;
            _settings = settings;
            _memoryStore = memoryStore;
            _logger = logger;
        }

        /// <summary>
        /// Попытка создать сервис из CoreAILifetimeScope (авто-резолв из DI).
        /// Возвращает null если скоуп не найден.
        /// </summary>
        public static CoreAiChatService TryCreateFromScene()
        {
            var scope = UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            if (scope?.Container == null) return null;

            try
            {
                var orchestrator = (IAiOrchestrationService)scope.Container.Resolve(typeof(IAiOrchestrationService));
                AgentMemoryPolicy policy = null;
                ICoreAISettings settings = null;
                IAgentMemoryStore memStore = null;
                IGameLogger logger = null;

                try { policy = (AgentMemoryPolicy)scope.Container.Resolve(typeof(AgentMemoryPolicy)); }
                catch (Exception ex) { Debug.LogWarning($"[CoreAiChatService] Resolve AgentMemoryPolicy: {ex.Message}"); }
                try { settings = (ICoreAISettings)scope.Container.Resolve(typeof(ICoreAISettings)); }
                catch (Exception ex) { Debug.LogWarning($"[CoreAiChatService] Resolve ICoreAISettings: {ex.Message}"); }
                try { memStore = (IAgentMemoryStore)scope.Container.Resolve(typeof(IAgentMemoryStore)); }
                catch (Exception ex) { Debug.LogWarning($"[CoreAiChatService] Resolve IAgentMemoryStore: {ex.Message}"); }
                try { logger = (IGameLogger)scope.Container.Resolve(typeof(IGameLogger)); }
                catch (Exception ex) { Debug.LogWarning($"[CoreAiChatService] Resolve IGameLogger: {ex.Message}"); }

                return new CoreAiChatService(orchestrator, policy, settings, memStore, logger);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAiChatService] Failed to create from scene: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Отправить сообщение и получить полный ответ (без стриминга).
        /// Свёрнутая обёртка над <see cref="SendMessageAsync(AiTaskRequest, CancellationToken)"/>
        /// для типичного chat-сценария (RoleId + текст пользователя).
        /// </summary>
        public System.Threading.Tasks.Task<string> SendMessageAsync(
            string userText,
            string roleId,
            CancellationToken ct = default)
        {
            AiTaskRequest request = new AiTaskRequest
            {
                RoleId = roleId,
                Hint = userText,
                SourceTag = "Chat"
            };

            return SendMessageAsync(request, ct);
        }

        /// <summary>
        /// Отправить сообщение, заданное полным <see cref="AiTaskRequest"/>.
        /// Используется UI-панелью или прикладным слоем, когда нужно прокинуть тонкие
        /// настройки запроса (например <see cref="AiTaskRequest.ForcedToolMode"/> для
        /// детерминированного tool-calling) без потери остальной chat-механики.
        /// </summary>
        /// <remarks>
        /// Timeout is enforced here via <c>CancelAfterSlim</c> (UniTask, PlayerLoop-based)
        /// — fully compatible with WebGL's single-threaded execution model.
        /// Exceptions are NOT swallowed; callers (e.g. <c>CoreAiChatPanel</c>) are responsible
        /// for catching and displaying errors to the user.
        /// </remarks>
        public async System.Threading.Tasks.Task<string> SendMessageAsync(
            AiTaskRequest request,
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

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
                    timerHandle = timeoutCts.CancelAfterSlim(TimeSpan.FromSeconds(timeoutSec));
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
                // order of operations as the awaiter sees the OCE — key off the configured window
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
        /// Стриминг ответа — возвращает чанки текста по мере генерации.
        /// Тонкая обёртка над <see cref="SendMessageStreamingAsync(AiTaskRequest, CancellationToken)"/>.
        /// </summary>
        public IAsyncEnumerable<LlmStreamChunk> SendMessageStreamingAsync(
            string userText,
            string roleId,
            CancellationToken ct = default)
        {
            AiTaskRequest request = new AiTaskRequest
            {
                RoleId = roleId,
                Hint = userText,
                SourceTag = "Chat"
            };

            return SendMessageStreamingAsync(request, ct);
        }

        /// <summary>
        /// Стриминг ответа на полный <see cref="AiTaskRequest"/>. См.
        /// <see cref="SendMessageAsync(AiTaskRequest, CancellationToken)"/> о применении.
        /// </summary>
        /// <remarks>
        /// Timeout is enforced here via <c>CancelAfterSlim</c> (UniTask, PlayerLoop-based)
        /// — fully compatible with WebGL's single-threaded execution model.
        /// </remarks>
        public async IAsyncEnumerable<LlmStreamChunk> SendMessageStreamingAsync(
            AiTaskRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

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
                    timerHandle = timeoutCts.CancelAfterSlim(TimeSpan.FromSeconds(timeoutSec));
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

        /// <summary>Очистить историю чата для роли.</summary>
        public void ClearHistory(string roleId)
        {
            _memoryStore?.ClearChatHistory(roleId);
        }

        /// <summary>
        /// Прочитать сохранённую историю чата (тип 2: ChatHistory в <see cref="IAgentMemoryStore"/>).
        /// Удобно для UI при старте сцену. <paramref name="maxMessages"/>: 0 = без лимита (как в store).
        /// </summary>
        public bool TryGetPersistedChatHistory(string roleId, out ChatMessage[] messages, int maxMessages = 0)
        {
            messages = Array.Empty<ChatMessage>();
            if (_memoryStore == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            ChatMessage[] raw = _memoryStore.GetChatHistory(roleId.Trim(), maxMessages);
            if (raw == null || raw.Length == 0)
            {
                return false;
            }

            messages = raw;
            return true;
        }

        /// <summary>Остановить все текущие и ожидающие задачи для роли.</summary>
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
#endif

        /// <summary>
        /// Вычислить эффективный флаг стриминга для роли с учётом иерархии настроек:
        /// <list type="number">
        /// <item>per-role override из <see cref="AgentMemoryPolicy"/> (через <c>AgentBuilder.WithStreaming()</c>)</item>
        /// <item>глобальный <see cref="ICoreAISettings.EnableStreaming"/></item>
        /// <item>fallback-параметр <paramref name="uiFallback"/> (например, из <c>CoreAiChatConfig.EnableStreaming</c>)</item>
        /// </list>
        /// Если UI-флаг выключен — стриминг принудительно выключается независимо от остальных слоёв.
        /// </summary>
        public bool IsStreamingEnabled(string roleId, bool uiFallback = true)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGlNativeStreamingBridgeEnabled(_settings))
            {
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
        /// Вычислить эффективный флаг стриминга для роли с учётом настроек
        /// агента (per-role override в <see cref="AgentMemoryPolicy"/>) и глобального
        /// <see cref="ICoreAISettings.EnableStreaming"/>.
        /// <para>
        /// Если передан <paramref name="uiOverride"/> (например, <c>CoreAiChatConfig.EnableStreaming</c>)
        /// и он <c>false</c> — стриминг выключается независимо от остальных настроек;
        /// если <c>true</c> — наследуется из агента/глобала.
        /// </para>
        /// </summary>
        public bool IsStreamingEnabled(string roleId, bool? uiOverride = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGlNativeStreamingBridgeEnabled(_settings))
            {
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
        /// «Умная» отправка сообщения: сам решает, использовать ли стриминг,
        /// исходя из <see cref="IsStreamingEnabled"/>. Если стриминг включён —
        /// делегирует в <see cref="SendMessageStreamingAsync"/> и аккумулирует
        /// чанки в итоговую строку; иначе — вызывает <see cref="SendMessageAsync"/>.
        /// Удобно для программных интеграций, которым нужна единая точка вызова.
        /// </summary>
        public async System.Threading.Tasks.Task<string> SendMessageSmartAsync(
            string userText,
            string roleId,
            System.Action<LlmStreamChunk> onChunk = null,
            bool? uiStreamingOverride = null,
            CancellationToken ct = default)
        {
            if (IsStreamingEnabled(roleId, uiStreamingOverride))
            {
                var sb = new StringBuilder();
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
        /// «Умная» отправка для произвольного <see cref="AiTaskRequest"/>: автоматически
        /// выбирает streaming/non-streaming по политике для роли. Этот overload используется,
        /// когда вызывающий код хочет явно прокинуть <see cref="AiTaskRequest.ForcedToolMode"/>
        /// или другие тонкие поля запроса.
        /// </summary>
        public async System.Threading.Tasks.Task<string> SendMessageSmartAsync(
            AiTaskRequest request,
            System.Action<LlmStreamChunk> onChunk = null,
            bool? uiStreamingOverride = null,
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (IsStreamingEnabled(request.RoleId, uiStreamingOverride))
            {
                var sb = new StringBuilder();
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

    }
}
