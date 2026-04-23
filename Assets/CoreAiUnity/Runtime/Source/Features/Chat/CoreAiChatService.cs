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
        private readonly ILlmClient _llmClient;
        private readonly IAgentSystemPromptProvider _promptProvider;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly ICoreAISettings _settings;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly IGameLogger _logger;

        public CoreAiChatService(
            ILlmClient llmClient,
            IAgentSystemPromptProvider promptProvider = null,
            AgentMemoryPolicy memoryPolicy = null,
            ICoreAISettings settings = null,
            IAgentMemoryStore memoryStore = null,
            IGameLogger logger = null)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
            _promptProvider = promptProvider;
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
                var llmClient = (ILlmClient)scope.Container.Resolve(typeof(ILlmClient));
                IAgentSystemPromptProvider prompts = null;
                AgentMemoryPolicy policy = null;
                ICoreAISettings settings = null;
                IAgentMemoryStore memStore = null;
                IGameLogger logger = null;

                try { prompts = (IAgentSystemPromptProvider)scope.Container.Resolve(typeof(IAgentSystemPromptProvider)); } catch { }
                try { policy = (AgentMemoryPolicy)scope.Container.Resolve(typeof(AgentMemoryPolicy)); } catch { }
                try { settings = (ICoreAISettings)scope.Container.Resolve(typeof(ICoreAISettings)); } catch { }
                try { memStore = (IAgentMemoryStore)scope.Container.Resolve(typeof(IAgentMemoryStore)); } catch { }
                try { logger = (IGameLogger)scope.Container.Resolve(typeof(IGameLogger)); } catch { }

                return new CoreAiChatService(llmClient, prompts, policy, settings, memStore, logger);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAiChatService] Failed to create from scene: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Отправить сообщение и получить полный ответ (без стриминга).
        /// </summary>
        public async System.Threading.Tasks.Task<string> SendMessageAsync(
            string userText,
            string roleId,
            CancellationToken ct = default)
        {
            LlmCompletionRequest request = BuildRequest(userText, roleId);
            LlmCompletionResult result = await _llmClient.CompleteAsync(request, ct);

            if (result == null || !result.Ok)
            {
                string error = result?.Error ?? "null result";
                _logger?.LogWarning(GameLogFeature.Llm, $"[CoreAiChatService] Error: {error}");
                return null;
            }

            string response = result.Content ?? "";
            SaveToChatHistory(roleId, userText, response);
            return response;
        }

        /// <summary>
        /// Стриминг ответа — возвращает чанки текста по мере генерации.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> SendMessageStreamingAsync(
            string userText,
            string roleId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            LlmCompletionRequest request = BuildRequest(userText, roleId);
            StringBuilder fullResponse = new();

            await foreach (LlmStreamChunk chunk in _llmClient.CompleteStreamingAsync(request, ct))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    fullResponse.Append(chunk.Text);
                }
                yield return chunk;
            }

            string response = fullResponse.ToString();
            if (!string.IsNullOrEmpty(response))
            {
                SaveToChatHistory(roleId, userText, response);
            }
        }

        /// <summary>Очистить историю чата для роли.</summary>
        public void ClearHistory(string roleId)
        {
            _memoryStore?.ClearChatHistory(roleId);
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

        private LlmCompletionRequest BuildRequest(string userText, string roleId)
        {
            string systemPrompt = ComposeSystemPrompt(roleId);
            List<Microsoft.Extensions.AI.ChatMessage> chatHistory = LoadChatHistory(roleId);

            return new LlmCompletionRequest
            {
                AgentRoleId = roleId,
                SystemPrompt = systemPrompt,
                UserPayload = userText,
                ChatHistory = chatHistory,
                TraceId = Guid.NewGuid().ToString("N")
            };
        }

        private string ComposeSystemPrompt(string roleId)
        {
            // Проверяем, переопределяет ли роль universalPrefix
            bool skipPrefix = _memoryPolicy != null &&
                              _memoryPolicy.IsUniversalPrefixOverridden(roleId);

            // Слой 1: universalPrefix (если не переопределён)
            string prefix = skipPrefix ? "" : (_settings?.UniversalSystemPromptPrefix ?? "");

            // Слой 2: базовый промпт из провайдера
            string basePrompt = $"You are agent \"{roleId}\".";
            if (_promptProvider != null &&
                _promptProvider.TryGetSystemPrompt(roleId, out string sp) &&
                !string.IsNullOrWhiteSpace(sp))
            {
                basePrompt = sp.Trim();
            }

            // Слой 3: доп. промпт из AgentBuilder
            string additional = "";
            if (_memoryPolicy != null &&
                _memoryPolicy.TryGetAdditionalSystemPrompt(roleId, out string extra) &&
                !string.IsNullOrWhiteSpace(extra))
            {
                additional = extra.Trim();
            }

            // Memory injection
            string memory = "";
            if (_memoryPolicy != null && _memoryPolicy.IsMemoryEnabled(roleId) &&
                _memoryStore != null &&
                _memoryStore.TryLoad(roleId, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem?.Memory))
            {
                memory = "\n\n## Memory\n" + mem.Memory.Trim();
            }

            StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(prefix)) { sb.Append(prefix.TrimEnd()); sb.Append('\n'); }
            sb.Append(basePrompt);
            if (!string.IsNullOrEmpty(additional)) { sb.Append("\n\n"); sb.Append(additional); }
            if (!string.IsNullOrEmpty(memory)) { sb.Append(memory); }

            return sb.ToString();
        }

        private List<Microsoft.Extensions.AI.ChatMessage> LoadChatHistory(string roleId)
        {
            if (_memoryStore == null || _memoryPolicy == null) return null;

            AgentMemoryPolicy.RoleMemoryConfig cfg = _memoryPolicy.GetRoleConfig(roleId);
            if (!cfg.WithChatHistory) return null;

            int max = cfg.MaxChatHistoryMessages > 0 ? cfg.MaxChatHistoryMessages : 30;
            ChatMessage[] history = _memoryStore.GetChatHistory(roleId, max);
            if (history == null || history.Length == 0) return null;

            var result = new List<Microsoft.Extensions.AI.ChatMessage>(history.Length);
            foreach (ChatMessage msg in history)
            {
                var aiRole = msg.Role == "user"
                    ? Microsoft.Extensions.AI.ChatRole.User
                    : Microsoft.Extensions.AI.ChatRole.Assistant;
                result.Add(new Microsoft.Extensions.AI.ChatMessage(aiRole, msg.Content));
            }
            return result;
        }

        private void SaveToChatHistory(string roleId, string userText, string response)
        {
            if (_memoryStore == null || _memoryPolicy == null) return;

            AgentMemoryPolicy.RoleMemoryConfig cfg = _memoryPolicy.GetRoleConfig(roleId);
            if (!cfg.WithChatHistory) return;

            _memoryStore.AppendChatMessage(roleId, "user", userText, cfg.PersistChatHistory);
            _memoryStore.AppendChatMessage(roleId, "assistant", response, cfg.PersistChatHistory);
        }
    }
}
