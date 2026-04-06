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

        public AgentBuilder(string roleId)
        {
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
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
        /// .WithChatHistory()                    // 8192 из конфига, без сохранения
        /// .WithChatHistory(4096)                // 4096 токенов, без сохранения
        /// .WithChatHistory(0)                   // минимальный контекст, без сохранения
        /// .WithChatHistory(persistBetweenSessions: true)  // 8192 из конфига, сохраняется между сессиями
        /// .WithChatHistory(4096, true)          // 4096 токенов, сохраняется между сессиями
        /// </example>
        public AgentBuilder WithChatHistory(int? contextWindowTokens = null, bool persistBetweenSessions = false)
        {
            _withChatHistory = true;
            _contextWindowTokens = contextWindowTokens;
            _persistChatHistory = persistBetweenSessions;
            return this;
        }

        /// <summary>
        /// Включить память для агента (добавляет MemoryTool).
        /// </summary>
        public AgentBuilder WithMemory(MemoryToolAction defaultAction = MemoryToolAction.Append)
        {
            _tools.Add(new MemoryLlmTool());
            return this;
        }

        /// <summary>
        /// Сконфигурировать агента в политике.
        /// </summary>
        public AgentConfig Build()
        {
            // Размер контекста: 0 → минимальный, null → из CoreAISettings, явно → использовать явно
            int ctxTokens = _contextWindowTokens ?? CoreAI.CoreAISettings.ContextWindowTokens;

            return new AgentConfig
            {
                RoleId = _roleId,
                SystemPrompt = _systemPrompt,
                Tools = new List<ILlmTool>(_tools),
                Mode = _mode,
                WithChatHistory = _withChatHistory,
                ContextWindowTokens = ctxTokens,
                PersistChatHistoryBetweenSessions = _persistChatHistory
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

        /// <summary>
        /// Применить конфигурацию к политике.
        /// </summary>
        public void ApplyToPolicy(AgentMemoryPolicy policy)
        {
            policy.SetToolsForRole(RoleId, Tools);

            // Если нет инструментов, режим = ChatOnly
            if (Tools.Count == 0)
            {
                policy.DisableMemoryTool(RoleId);
            }
        }
    }

    /// <summary>
    /// Действия для MemoryTool.
    /// </summary>
    public enum MemoryToolAction
    {
        /// <summary>Полная замена памяти.</summary>
        Write = 0,

        /// <summary>Добавление к существующей памяти.</summary>
        Append = 1,

        /// <summary>Очистка памяти.</summary>
        Clear = 2
    }
}