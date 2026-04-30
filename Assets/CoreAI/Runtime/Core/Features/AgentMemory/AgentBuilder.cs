using System;
using System.Collections.Generic;
using CoreAI.AgentMemory;
using CoreAI.Logging;

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
        /// <summary>Null = default true (LLM-assisted compaction when global setting allows).</summary>
        private bool? _useLlmContextCompaction;

        private readonly ICoreAISettings _settings;

        public AgentBuilder(string roleId, ICoreAISettings settings = null)
        {
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            _settings = settings;
        }

        /// <summary>
        /// Sets the system prompt for this agent (Layer 3 in CoreAI's prompt composition).
        /// </summary>
        /// <remarks>
        /// <para>The final system prompt sent to the model is composed of THREE layers, in order:</para>
        /// <list type="number">
        ///   <item><b>Layer 1 — Universal Prefix.</b> Project-wide rules from
        ///   <see cref="ICoreAISettings.UniversalSystemPromptPrefix"/> (e.g. style, safety, output format).
        ///   Skip this layer for the current role with <see cref="WithOverrideUniversalPrefix"/>.</item>
        ///   <item><b>Layer 2 — Role base prompt.</b> Loaded by
        ///   <c>AiPromptComposer</c> from the <c>AgentPromptsManifest</c> ScriptableObject (Unity) or
        ///   <c>Resources/Prompts/{RoleId}.txt</c>. For built-in roles
        ///   (<see cref="BuiltInAgentRoleIds"/>) there is also a code-side fallback string.</item>
        ///   <item><b>Layer 3 — This builder's prompt.</b> The text passed to <c>WithSystemPrompt</c>
        ///   is appended after Layer 2 as additional role guidance.</item>
        /// </list>
        /// <para>This means the literal string you pass here is <i>not</i> the full prompt the model sees;
        /// it is concatenated with the universal prefix and the role base prompt. To inspect the final
        /// composed prompt at runtime, enable <c>logLlmInput</c> on <c>CoreAISettingsAsset</c> or read
        /// <c>AgentTurnTrace.SystemPrompt</c>.</para>
        /// <para>See <c>DEVELOPER_GUIDE.md → Prompt Layers</c> for the full breakdown.</para>
        /// </remarks>
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
        /// Enables or disables LLM-assisted folding of overflowing chat history for this agent.
        /// When disabled, only deterministic compaction applies. Default when not called: enabled (still gated by global <see cref="ICoreAISettings.EnableLlmContextCompaction"/>).
        /// </summary>
        public AgentBuilder WithLlmContextCompaction(bool enabled)
        {
            _useLlmContextCompaction = enabled;
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
        /// Builds the <see cref="AgentConfig"/>. Emits non-fatal warnings via <see cref="Log.Instance"/>
        /// for likely misconfigurations (empty system prompt, tool-using mode without tools, compaction
        /// without the global gate). Warnings never throw — gameplay code continues to work.
        /// </summary>
        /// <remarks>
        /// Set <see cref="SuppressBuildWarnings"/> to <c>true</c> to silence validation (e.g. for tests
        /// that intentionally build minimal agents). Use <see cref="ValidateOnBuild"/> for the full set
        /// of issue codes if you want to assert on them in your own checks.
        /// </remarks>
        public AgentConfig Build()
        {
            // Context size: 0 → minimal, null → fall back to CoreAISettings, explicit → use as-is.
            int ctxTokens = _contextWindowTokens ?? _settings?.ContextWindowTokens ?? CoreAISettings.ContextWindowTokens;

            // Temperature: null → fall back to ICoreAISettings → CoreAISettings, explicit → use as-is.
            float temp = _temperature ?? _settings?.Temperature ?? CoreAISettings.Temperature;

            // Prompt does NOT include universalPrefix — it is appended by AiPromptComposer at
            // final composition time (three-layer architecture):
            //   Layer 1: universalSystemPromptPrefix (project-wide rules)
            //   Layer 2: role base prompt from Manifest / Resources (.txt files)
            //   Layer 3: extra prompt from this builder (the one above)

            if (!SuppressBuildWarnings)
            {
                EmitBuildWarnings();
            }

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
                OverrideUniversalPrefix = _overrideUniversalPrefix,
                UseLlmContextCompaction = _useLlmContextCompaction ?? true
            };
        }

        /// <summary>
        /// When <c>true</c>, <see cref="Build"/> does not emit validation warnings.
        /// Default <c>false</c>. Useful for unit tests that intentionally construct partial agents.
        /// </summary>
        public bool SuppressBuildWarnings { get; set; }

        /// <summary>
        /// Returns the validation issues that <see cref="Build"/> would emit, without actually building.
        /// Useful for editor tooling and tests. Returns an empty list when there is nothing to flag.
        /// </summary>
        public IReadOnlyList<AgentBuilderIssue> ValidateOnBuild()
        {
            List<AgentBuilderIssue> issues = new();
            CollectIssues(issues);
            return issues;
        }

        private void EmitBuildWarnings()
        {
            List<AgentBuilderIssue> issues = new();
            CollectIssues(issues);
            if (issues.Count == 0)
            {
                return;
            }

            ILog log = Log.Instance ?? NullLog.Instance;
            foreach (AgentBuilderIssue issue in issues)
            {
                log.Warn($"[AgentBuilder:{_roleId}] {issue.Code}: {issue.Message}", LogTag.Core);
            }
        }

        private void CollectIssues(List<AgentBuilderIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(_systemPrompt))
            {
                bool hasManifestFallback = !string.IsNullOrEmpty(_roleId)
                    && BuiltInAgentRoleIds.IsBuiltIn(_roleId);
                if (!hasManifestFallback)
                {
                    issues.Add(new AgentBuilderIssue(
                        AgentBuilderIssueCode.MissingSystemPrompt,
                        "WithSystemPrompt(...) was not called and the role has no built-in fallback. " +
                        "The agent will rely solely on the universal prefix (Layer 1) and any manifest entry (Layer 2). " +
                        "If neither exists, the model gets an empty role prompt."));
                }
            }

            if ((_mode == AgentMode.ToolsAndChat || _mode == AgentMode.ToolsOnly) && _tools.Count == 0)
            {
                issues.Add(new AgentBuilderIssue(
                    AgentBuilderIssueCode.NoToolsForToolMode,
                    $"Mode is {_mode} but no tools were registered. " +
                    "Add tools with WithTool(...), WithAction(...), WithEventTool(...), or WithMemory(), " +
                    "or switch to AgentMode.ChatOnly."));
            }

            if (_mode == AgentMode.ToolsOnly && _tools.Count == 0)
            {
                // ToolsOnly without tools is degenerate — the agent has nothing to do.
                // Already reported by the rule above; no extra issue here.
            }

            if (_useLlmContextCompaction == true)
            {
                bool? globalGate = _settings?.EnableLlmContextCompaction;
                bool effective = globalGate ?? CoreAISettings.EnableLlmContextCompaction;
                if (!effective)
                {
                    issues.Add(new AgentBuilderIssue(
                        AgentBuilderIssueCode.CompactionGateDisabled,
                        "WithLlmContextCompaction(true) was requested but the global gate " +
                        "ICoreAISettings.EnableLlmContextCompaction is false. Compaction will fall back to " +
                        "deterministic-only behavior. Enable the global gate on CoreAISettingsAsset to opt in."));
                }
            }

            if (_maxChatHistoryMessages <= 0 && _withChatHistory)
            {
                issues.Add(new AgentBuilderIssue(
                    AgentBuilderIssueCode.InvalidChatHistorySize,
                    $"WithChatHistory was enabled with maxChatHistoryMessages={_maxChatHistoryMessages}. " +
                    "Use a positive value (default 30) or omit the parameter."));
            }

            if (_temperature is < 0f or > 2f)
            {
                issues.Add(new AgentBuilderIssue(
                    AgentBuilderIssueCode.TemperatureOutOfRange,
                    $"WithTemperature({_temperature}) is outside the typical 0.0–2.0 range. " +
                    "Most providers clamp or reject values outside this range."));
            }
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

        /// <summary>LLM-assisted transcript compaction for this agent (global gate still applies).</summary>
        public bool UseLlmContextCompaction { get; internal set; }

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
            policy.ConfigureLlmContextCompaction(RoleId, UseLlmContextCompaction);
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