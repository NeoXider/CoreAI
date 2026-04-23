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
    /// Универсальная одна-строчная точка входа CoreAI: синхронный и стриминговый вызов LLM,
    /// интеграция с <see cref="IAiOrchestrationService"/> (очередь, метрики, publish command),
    /// без необходимости вручную доставать сервисы из VContainer или писать свой singleton.
    ///
    /// <para>
    /// <b>Быстрый старт.</b> Добавьте <see cref="CoreAILifetimeScope"/> на сцену и вызывайте:
    /// </para>
    /// <code>
    /// // Синхронно (одним строкой):
    /// string answer = await CoreAi.AskAsync("Привет!", roleId: "PlayerChat");
    ///
    /// // Стриминг (чанки по мере генерации):
    /// await foreach (string chunk in CoreAi.StreamAsync("Расскажи анекдот", "PlayerChat"))
    ///     label.text += chunk;
    ///
    /// // Smart — сам решает (стрим если включено в settings/agent/UI):
    /// await CoreAi.SmartAskAsync("Вопрос", "PlayerChat", onChunk: c => label.text += c);
    ///
    /// // Полный оркестратор-пайплайн (память, authority, метрики, publish):
    /// var task = new AiTaskRequest { RoleId = "Creator", Hint = "Дай JSON-команду" };
    /// string result = await CoreAi.OrchestrateAsync(task);
    /// await foreach (var chunk in CoreAi.OrchestrateStreamAsync(task))
    ///     Debug.Log(chunk.Text);
    /// </code>
    ///
    /// <para>
    /// Сервисы резолвятся лениво из первой найденной <see cref="CoreAILifetimeScope"/>. При смене
    /// сцены или пересборке scope вызывайте <see cref="Invalidate"/>, иначе используется
    /// кэшированный контейнер.
    /// </para>
    /// </summary>
    public static class CoreAi
    {
        private static readonly object SyncRoot = new();
        private static CoreAILifetimeScope? _scope;
        private static CoreAiChatService? _chatService;
        private static IAiOrchestrationService? _orchestrator;
        private static ICoreAISettings? _settings;

        /// <summary>Сервисы CoreAI найдены и готовы к использованию.</summary>
        public static bool IsReady => TryResolve(out _, out _, out _);

        /// <summary>
        /// Сбросить кэш сервисов. Вызывайте после смены сцены, пересборки VContainer-scope
        /// или в тестах между фикстурами. Безопасно вызывать многократно.
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

        // ===================== Chat API (простой слой) =====================

        /// <summary>
        /// Отправить сообщение и дождаться полного ответа. Простой синхронный путь —
        /// без чанков, без стриминга, сохраняет chat history для <paramref name="roleId"/>
        /// (если у роли включён ChatHistory).
        /// </summary>
        public static async Task<string?> AskAsync(
            string userMessage,
            string roleId = "PlayerChat",
            CancellationToken cancellationToken = default)
        {
            CoreAiChatService svc = RequireChatService();
            return await svc.SendMessageAsync(userMessage, roleId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Стриминг ответа LLM как последовательность строковых чанков. Чанки уже очищены
        /// от <c>&lt;think&gt;</c>-блоков. Последний terminal-чанк (<c>IsDone=true</c>)
        /// не эмитится как строка — стрим просто заканчивается.
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
        /// Стриминг ответа как <see cref="IAsyncEnumerable{T}"/> чанков (с метаданными:
        /// <see cref="LlmStreamChunk.IsDone"/>, <see cref="LlmStreamChunk.Error"/>, usage).
        /// Удобно когда нужен детальный контроль терминала/ошибок.
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
        /// «Умная» отправка: сама выбирает стриминг или non-streaming исходя из иерархии
        /// флагов <see cref="CoreAiChatService.IsStreamingEnabled(string, bool?)"/>
        /// (UI / per-agent / global). <paramref name="onChunk"/> вызывается на каждый
        /// текстовый чанк (может быть <c>null</c>). Возвращает полный текст.
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

        // ===================== Orchestrator API (полный пайплайн) =====================

        /// <summary>
        /// Полный пайплайн оркестратора: snapshot сессии, prompt composer, авторити, очередь,
        /// retry/structured policy, publish <c>ApplyAiGameCommand</c>, метрики. Возвращает
        /// финальный текст ответа или <c>null</c>, если задача не прошла authority/валидацию.
        /// </summary>
        public static Task<string?> OrchestrateAsync(
            AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            IAiOrchestrationService svc = RequireOrchestrator();
            return svc.RunTaskAsync(task, cancellationToken)!;
        }

        /// <summary>
        /// Стриминговый вариант <see cref="OrchestrateAsync"/>. Эмитит чанки по мере генерации,
        /// очищает <c>&lt;think&gt;</c>, после окончания стрима выполняет structured validation
        /// и публикует <c>ApplyAiGameCommand</c>. Если стрим закончился ошибкой или валидация
        /// провалилась — эмитится терминальный чанк с <see cref="LlmStreamChunk.Error"/>.
        /// </summary>
        public static IAsyncEnumerable<LlmStreamChunk> OrchestrateStreamAsync(
            AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            IAiOrchestrationService svc = RequireOrchestrator();
            return svc.RunStreamingAsync(task, cancellationToken);
        }

        /// <summary>
        /// Удобный хелпер: запустить оркестратор в стриминговом режиме и собрать полный текст,
        /// попутно вызывая <paramref name="onChunk"/> на каждый новый фрагмент. Удобно, когда
        /// нужен одновременно «живой» UI и финальный результат для логики.
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

        // ===================== Service accessors =====================

        /// <summary>Получить (и при необходимости резолвнуть) сервис чата.</summary>
        public static CoreAiChatService GetChatService() => RequireChatService();

        /// <summary>
        /// Безопасный резолв чата без исключения: <c>true</c> если на сцене есть
        /// <see cref="CoreAILifetimeScope"/> и доступен <see cref="ILlmClient"/>.
        /// Удобно в UI (кнопка «Спросить AI»), загрузочных экранах и тестах.
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

        /// <summary>Получить (и при необходимости резолвнуть) оркестратор.</summary>
        public static IAiOrchestrationService GetOrchestrator() => RequireOrchestrator();

        /// <summary>
        /// Безопасный резолв оркестратора без исключения: <c>true</c> если зарегистрирован
        /// <see cref="IAiOrchestrationService"/> (обычно после <c>RegisterCorePortable()</c>).
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

        /// <summary>Получить глобальные настройки CoreAI (из DI или fallback).</summary>
        public static ICoreAISettings? GetSettings()
        {
            lock (SyncRoot)
            {
                if (_settings != null) return _settings;
                TryResolve(out _, out _, out _settings);
                return _settings;
            }
        }

        // ===================== Internals =====================

        private static CoreAiChatService RequireChatService()
        {
            lock (SyncRoot)
            {
                if (_chatService != null) return _chatService;
                if (!TryResolve(out _chatService, out _, out _settings) || _chatService == null)
                {
                    throw new InvalidOperationException(
                        "CoreAi: CoreAILifetimeScope не найден на сцене или ILlmClient не зарегистрирован. " +
                        "Добавьте CoreAILifetimeScope или вызовите CoreAi.Invalidate() после смены сцены.");
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
                        "CoreAi: IAiOrchestrationService не зарегистрирован в CoreAILifetimeScope. " +
                        "Убедитесь что builder.RegisterCorePortable() вызывается в Configure().");
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
            catch
            {
                /* optional */
            }

            try
            {
                settings = (ICoreAISettings)_scope.Container.Resolve(typeof(ICoreAISettings));
            }
            catch
            {
                /* optional */
            }

            // ChatService резолвим только если есть ILlmClient (без него он бесполезен).
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
