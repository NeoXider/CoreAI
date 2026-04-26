using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
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

                try { policy = (AgentMemoryPolicy)scope.Container.Resolve(typeof(AgentMemoryPolicy)); } catch { }
                try { settings = (ICoreAISettings)scope.Container.Resolve(typeof(ICoreAISettings)); } catch { }
                try { memStore = (IAgentMemoryStore)scope.Container.Resolve(typeof(IAgentMemoryStore)); } catch { }
                try { logger = (IGameLogger)scope.Container.Resolve(typeof(IGameLogger)); } catch { }

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
        public async System.Threading.Tasks.Task<string> SendMessageAsync(
            AiTaskRequest request,
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                string result = await _orchestrator.RunTaskAsync(request, ct);
                return result ?? "";
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(GameLogFeature.Llm, $"[CoreAiChatService] Error: {ex.Message}");
                return null;
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
        public async IAsyncEnumerable<LlmStreamChunk> SendMessageStreamingAsync(
            AiTaskRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            await foreach (LlmStreamChunk chunk in _orchestrator.RunStreamingAsync(request, ct))
            {
                yield return chunk;
            }
        }

        /// <summary>Очистить историю чата для роли.</summary>
        public void ClearHistory(string roleId)
        {
            _memoryStore?.ClearChatHistory(roleId);
        }

        /// <summary>Остановить все текущие и ожидающие задачи для роли.</summary>
        public void StopAgent(string roleId)
        {
            CoreAi.StopAgent(roleId);
        }

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
