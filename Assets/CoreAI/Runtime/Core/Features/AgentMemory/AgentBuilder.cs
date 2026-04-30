using System;
using System.Collections.Generic;
using CoreAI.AgentMemory;

namespace CoreAI.Ai
{
    /// <summary>
    /// Режим поведения агента.
    /// </summary>
    public enum AgentMode
    {
        /// <summary>Агент использует ТОЛЬКО инструменты (не отвечает текстом).</summary>
        ToolsOnly = 0,

        /// <summary>Агент вызывает инструменты И отвечает текстом (по умолчанию).</summary>
        ToolsAndChat = 1,

        /// <summary>Агент только отвечает текстом (без инструментов).</summary>
        ChatOnly = 2
    }

    /// <summary>
    /// Конструктор кастомных агентов. Позволяет легко создать нового агента
    /// с уникальными инструментами и промптом для конкретной игры.
    /// 
    /// Пример:
    /// <code>
    /// var builder = new AgentBuilder("Blacksmith")
    ///     .WithSystemPrompt("You are a blacksmith NPC...")
    ///     .WithTool(new InventoryLlmTool(myInventoryProvider))
    ///     .WithTool(new MemoryLlmTool())
    ///     .WithMode(AgentMode.ToolsAndChat)
    ///     .Build();
    /// 
    /// policy.SetToolsForRole("Blacksmith", builder.Tools);
    /// policy.SetAgentMode("Blacksmith", builder.Mode);
    /// </code>
    /// </summary>
    public sealed class AgentBuilder
    {
        private readonly string _roleId;
        private readonly List<ILlmTool> _tools = new();
        private string _systemPrompt;
        private AgentMode _mode = AgentMode.ToolsAndChat;
        private bool _withChatHistory;
        private int? _contextWindowTokens;
        private bool _persistChatHistory;
        private int _maxChatHistoryMessages = 30;
        private float? _temperature;
        private int? _maxOutputTokens;
        private bool? _allowDuplicateToolCalls;
        private bool? _enableStreaming;
        private MemoryToolAction _memoryDefaultAction = MemoryToolAction.Append;
        private bool _overrideUniversalPrefix;
        private readonly ICoreAISettings _settings;

        public AgentBuilder(string roleId, ICoreAISettings settings = null)
        {
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            _settings = settings;
        }

        /// <summary>
        /// Установить системный промпт агента.
        /// </summary>
        public AgentBuilder WithSystemPrompt(string prompt)
        {
            _systemPrompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            return this;
        }

        /// <summary>
        /// Добавить инструмент агенту.
        /// </summary>
        public AgentBuilder WithTool(ILlmTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            _tools.Add(tool);
            return this;
        }

        /// <summary>
        /// Добавить несколько инструментов.
        /// </summary>
        public AgentBuilder WithTools(IEnumerable<ILlmTool> tools)
        {
            if (tools != null)
            {
                foreach (ILlmTool tool in tools)
                {
                    _tools.Add(tool);
                }
            }

            return this;
        }

        /// <summary>
        /// Установить режим работы агента.
        /// </summary>
        public AgentBuilder WithMode(AgentMode mode)
        {
            _mode = mode;
            return this;
        }

        /// <summary>
        /// Включить историю диалога для агента (контекст текущей сессии).
        /// <para>contextWindowTokens: размер контекста. 0 = минимальный, null = из CoreAISettings (по умолчанию 8192).</para>
        /// <para>persistBetweenSessions: сохранять историю между сессиями (в JSON файл). По умолчанию false (только RAM).</para>
        /// </summary>
        /// <example>
        /// .WithChatHistory()                    // 8192 из конфига, без сохранения, 30 сообщений
        /// .WithChatHistory(4096)                // 4096 токенов, без сохранения, 30 сообщений
        /// .WithChatHistory(0)                   // минимальный контекст, без сохранения
        /// .WithChatHistory(persistBetweenSessions: true)  // 8192 из конфига, сохраняется, 30 сообщений
        /// .WithChatHistory(4096, true, 50)      // 4096 токенов, сохраняется, 50 сообщений максимум
        /// </example>
        public AgentBuilder WithChatHistory(int? contextWindowTokens = null, bool persistBetweenSessions = false, int maxChatHistoryMessages = 30)
        {
            _withChatHistory = true;
            _contextWindowTokens = contextWindowTokens;
            _persistChatHistory = persistBetweenSessions;
            _maxChatHistoryMessages = maxChatHistoryMessages;
            return this;
        }

        /// <summary>
        /// Включить память для агента (добавляет MemoryTool).
        /// </summary>
        public AgentBuilder WithMemory(MemoryToolAction defaultAction = MemoryToolAction.Append)
        {
            _tools.Add(new MemoryLlmTool());
            _memoryDefaultAction = defaultAction;
            return this;
        }

        /// <summary>
        /// Добавить метод/делегат как инструмент (MEAI автоматически сгенерирует JSON-схему аргументов по сигнатуре метода).
        /// </summary>
        public AgentBuilder WithAction(string name, string description, Delegate action)
        {
            _tools.Add(new DelegateLlmTool(name, description, action));
            return this;
        }

        /// <summary>
        /// Добавить инструмент, который публикует событие в CoreAiEvents. 
        /// Отлично подходит для новичков (достаточно написать CoreAiEvents.Subscribe в любом скрипте).
        /// </summary>
        public AgentBuilder WithEventTool(string name, string description, bool hasStringPayload = false)
        {
            if (hasStringPayload)
            {
                _tools.Add(new DelegateLlmTool(name, description,
                    new Action<string>((payload) => CoreAiEvents.Publish(name, payload))));
            }
            else
            {
                _tools.Add(new DelegateLlmTool(name, description, new Action(() => CoreAiEvents.Publish(name))));
            }

            return this;
        }

        /// <summary>
        /// Установить температуру генерации для конкретного агента.
        /// Переопределяет общую температуру из CoreAISettings.Temperature.
        /// <para>0.0 = детерминировано, 1.0 = креативно, 2.0 = максимально случайно.</para>
        /// </summary>
        /// <example>
        /// .WithTemperature(0.0f)   // Для строгого JSON/кода
        /// .WithTemperature(0.3f)   // Для NPC диалогов
        /// .WithTemperature(0.8f)   // Для творческих задач
        /// </example>
        public AgentBuilder WithTemperature(float temperature)
        {
            _temperature = temperature;
            return this;
        }

        /// <summary>
        /// Set a response token cap for this agent. Null or non-positive values clear the per-agent override.
        /// Per-call <see cref="AiTaskRequest.MaxOutputTokens"/> still has higher priority.
        /// </summary>
        /// <example>
        /// .WithMaxOutputTokens(256)   // Short NPC replies
        /// .WithMaxOutputTokens(2048)  // Longer planning agent
        /// </example>
        public AgentBuilder WithMaxOutputTokens(int? tokens)
        {
            _maxOutputTokens = tokens.HasValue && tokens.Value > 0 ? tokens.Value : null;
            return this;
        }

        /// <summary>
        /// Per-agent override for duplicate tool-call detection. Default behaviour is to <b>reject</b>
        /// a tool call whose <c>(name, args)</c> signature exactly matches a previous one within the
        /// same request — this prevents loops where a model re-invokes the same tool forever.
        /// <para>
        /// Pass <c>true</c> to <b>opt out</b> (large/strong models occasionally re-call a tool on
        /// purpose, e.g. polling for state). Pass <c>false</c> to force-enable the guard for this
        /// role even if the global <see cref="ICoreAISettings.AllowDuplicateToolCalls"/> is <c>true</c>.
        /// </para>
        /// <para>
        /// Granularity:
        /// <list type="number">
        ///   <item>Global default: <see cref="ICoreAISettings.AllowDuplicateToolCalls"/> (off — reject).</item>
        ///   <item>Per-role override: this method.</item>
        ///   <item>Per-tool override: <see cref="ILlmTool.AllowDuplicates"/> on the tool itself —
        ///     even when role/global reject duplicates, a tool that returns <c>true</c> here is
        ///     never blocked (useful for read-only "ping" tools).</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <example>
        /// // Allow this agent to call its tool repeatedly (e.g. status-poll loop).
        /// new AgentBuilder("Watchdog").WithAllowDuplicateToolCalls(true).Build();
        /// </example>
        public AgentBuilder WithAllowDuplicateToolCalls(bool allow)
        {
            _allowDuplicateToolCalls = allow;
            return this;
        }

        /// <summary>
        /// Включить/выключить стриминг ответов для этого агента.
        /// Переопределяет глобальный <see cref="ICoreAISettings.EnableStreaming"/>.
        /// Если не вызвано — используется глобальный флаг.
        /// </summary>
        /// <example>
        /// new AgentBuilder("FastChat").WithStreaming(true).Build();
        /// new AgentBuilder("StrictJsonRole").WithStreaming(false).Build();
        /// </example>
        public AgentBuilder WithStreaming(bool enabled)
        {
            _enableStreaming = enabled;
            return this;
        }

        /// <summary>
        /// Отключить universalSystemPromptPrefix из CoreAISettings для этой роли.
        /// Полезно когда роли нужен полностью кастомный системный промпт
        /// без общих правил (например, роль-парсер или роль-валидатор).
        /// </summary>
        /// <example>
        /// new AgentBuilder("JsonParser")
        ///     .WithSystemPrompt("You are a strict JSON parser.")
        ///     .WithOverrideUniversalPrefix()
        ///     .Build();
        /// </example>
        public AgentBuilder WithOverrideUniversalPrefix(bool shouldOverride = true)
        {
            _overrideUniversalPrefix = shouldOverride;
            return this;
        }

        /// <summary>
        /// Сконфигурировать агента в политике.
        /// </summary>
        public AgentConfig Build()
        {
            // Размер контекста: 0 → минимальный, null → из CoreAISettings, явно → использовать явно
            int ctxTokens = _contextWindowTokens ?? _settings?.ContextWindowTokens ?? CoreAISettings.ContextWindowTokens;

            // Температура: null → из ICoreAISettings → из CoreAISettings, явно → использовать явно
            float temp = _temperature ?? _settings?.Temperature ?? CoreAISettings.Temperature;

            // Промпт НЕ включает universalPrefix — он приклеивается в AiPromptComposer
            // при финальной сборке (3-слойная архитектура):
            //   Слой 1: universalSystemPromptPrefix (общие правила)
            //   Слой 2: базовый промпт из Manifest/Resources (.txt файлы)
            //   Слой 3: дополнительный промпт из AgentBuilder (этот)

            return new AgentConfig
            {
                RoleId = _roleId,
                SystemPrompt = _systemPrompt,
                Tools = new List<ILlmTool>(_tools),
                Mode = _mode,
                WithChatHistory = _withChatHistory,
                ContextWindowTokens = ctxTokens,
                PersistChatHistoryBetweenSessions = _persistChatHistory,
                MaxChatHistoryMessages = _maxChatHistoryMessages,
                Temperature = temp,
                MaxOutputTokens = _maxOutputTokens,
                AllowDuplicateToolCalls = _allowDuplicateToolCalls,
                EnableStreaming = _enableStreaming,
                MemoryDefaultAction = _memoryDefaultAction,
                OverrideUniversalPrefix = _overrideUniversalPrefix
            };
        }
    }

    /// <summary>
    /// Конфигурация агента (результат AgentBuilder.Build()).
    /// </summary>
    public sealed class AgentConfig
    {
        public string RoleId { get; internal set; }
        public string SystemPrompt { get; internal set; }
        public IReadOnlyList<ILlmTool> Tools { get; internal set; }
        public AgentMode Mode { get; internal set; }
        public bool WithChatHistory { get; internal set; }
        public int ContextWindowTokens { get; internal set; }
        public bool PersistChatHistoryBetweenSessions { get; internal set; }
        public int MaxChatHistoryMessages { get; internal set; }
        public float Temperature { get; internal set; }
        public int? MaxOutputTokens { get; internal set; }
        public bool? AllowDuplicateToolCalls { get; internal set; }

        /// <summary>Per-role override для стриминга; null = использовать глобальный <see cref="ICoreAISettings.EnableStreaming"/>.</summary>
        public bool? EnableStreaming { get; internal set; }

        public MemoryToolAction MemoryDefaultAction { get; internal set; }
        public bool OverrideUniversalPrefix { get; internal set; }

        /// <summary>
        /// Применить конфигурацию к политике.
        /// </summary>
        public void ApplyToPolicy(AgentMemoryPolicy policy)
        {
            policy.SetToolsForRole(RoleId, Tools);

            // Настраиваем действие памяти по умолчанию и дубликаты
            policy.ConfigureRole(RoleId, defaultAction: MemoryDefaultAction, allowDuplicateToolCalls: AllowDuplicateToolCalls);

            // Если нет инструментов, отключаем MemoryTool
            if (Tools.Count == 0 || !HasMemoryTool())
            {
                policy.DisableMemoryTool(RoleId);
            }

            policy.ConfigureChatHistory(RoleId, WithChatHistory, ContextWindowTokens,
                PersistChatHistoryBetweenSessions, MaxChatHistoryMessages);
            policy.SetMaxOutputTokens(RoleId, MaxOutputTokens);

            // Регистрируем дополнительный системный промпт (слой 3)
            if (!string.IsNullOrWhiteSpace(SystemPrompt))
            {
                policy.SetAdditionalSystemPrompt(RoleId, SystemPrompt);
            }

            // Регистрируем переопределение universalPrefix
            if (OverrideUniversalPrefix)
            {
                policy.SetOverrideUniversalPrefix(RoleId, true);
            }

            // Регистрируем per-role override стриминга:
            // - явный WithStreaming(...) всегда приоритетен;
            // - для режимов с инструментами (ToolsAndChat/ToolsOnly) по умолчанию включаем стриминг,
            //   чтобы работал streaming + tool-calling single-cycle без дополнительной настройки;
            // - для остальных режимов оставляем глобальный fallback.
            bool? streamingOverride = EnableStreaming;
            if (!streamingOverride.HasValue &&
                (Mode == AgentMode.ToolsAndChat || Mode == AgentMode.ToolsOnly))
            {
                streamingOverride = true;
            }
            policy.SetStreamingEnabled(RoleId, streamingOverride);
        }

        private bool HasMemoryTool()
        {
            foreach (ILlmTool tool in Tools)
            {
                if (tool is MemoryLlmTool)
                {
                    return true;
                }
            }

            return false;
        }
    }
}