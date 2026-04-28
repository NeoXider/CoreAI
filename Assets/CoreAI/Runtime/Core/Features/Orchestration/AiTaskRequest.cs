namespace CoreAI.Ai
{
    /// <summary>
    /// Параметры одного вызова <see cref="IAiOrchestrationService.RunTaskAsync"/>:
    /// роль, подсказка для промпта, опции очереди и контекст ремонта Lua.
    /// </summary>
    public sealed class AiTaskRequest
    {
        /// <summary>Id роли: <see cref="BuiltInAgentRoleIds"/> или свой.</summary>
        public string RoleId { get; set; } = BuiltInAgentRoleIds.Creator;

        /// <summary>Короткая метка задачи для user-шаблона и логов (например <c>arena_wave_plan</c>).</summary>
        public string Hint { get; set; } = "";

        /// <summary>Номер попытки исправления Lua (0 — обычная задача Programmer).</summary>
        public int LuaRepairGeneration { get; set; }

        /// <summary>Код Lua, который упал (передаётся обратно в Programmer).</summary>
        public string LuaRepairPreviousCode { get; set; } = "";

        /// <summary>Сообщение об ошибке MoonSharp / выполнения.</summary>
        public string LuaRepairErrorMessage { get; set; } = "";

        /// <summary>Продолжить цепочку логов (ремонт Lua); если пусто — оркестратор создаст новый.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Больше — раньше в очереди <see cref="QueuedAiOrchestrator"/>.</summary>
        public int Priority { get; set; }

        /// <summary>
        /// Машиночитаемый источник задачи для логов, дашборда и телеметрии (например <c>hotkey:F1</c>, <c>arena_director:pre_next_wave</c>).
        /// </summary>
        public string SourceTag { get; set; } = "";

        /// <summary>
        /// Непустой ключ: новая задача с тем же ключом отменяет ожидание/выполнение предыдущей (через <see cref="System.Threading.CancellationToken"/>).
        /// </summary>
        public string CancellationScope { get; set; } = "";

        /// <summary>
        /// Стабильный id слота Lua для <see cref="ILuaScriptVersionStore"/> (оригинал / история / сброс).
        /// Пусто — версионирование для этой задачи не ведётся.
        /// </summary>
        public string LuaScriptVersionKey { get; set; } = "";

        /// <summary>
        /// Ключи оверлеев данных (<see cref="IDataOverlayVersionStore"/>), через запятую или «;» — в промпт Programmer добавляются baseline и текущие JSON.
        /// </summary>
        public string DataOverlayVersionKeysCsv { get; set; } = "";

        /// <summary>
        /// Per-call override of how the model picks tools. <see cref="LlmToolChoiceMode.Auto"/>
        /// is the default and matches the legacy behaviour (model decides).
        /// Application-layer logic (intent classifiers, retry pipelines) sets this when it needs
        /// guaranteed tool emission for the current request without changing the agent definition.
        /// Propagated to <see cref="LlmCompletionRequest.ForcedToolMode"/> by the orchestrator.
        /// </summary>
        public LlmToolChoiceMode ForcedToolMode { get; set; } = LlmToolChoiceMode.Auto;

        /// <summary>
        /// Tool name to require when <see cref="ForcedToolMode"/> is
        /// <see cref="LlmToolChoiceMode.RequireSpecific"/>. Ignored otherwise.
        /// Must match an <see cref="ILlmTool.Name"/> registered for this role.
        /// </summary>
        public string RequiredToolName { get; set; } = "";

        /// <summary>
        /// Optional per-request allowlist of tool names. Empty or null keeps all role tools available.
        /// Combine with <see cref="ForcedToolMode"/> to model context-step policies.
        /// </summary>
        public string[] AllowedToolNames { get; set; }

        /// <summary>
        /// Per-call override of the LLM response token budget. <c>null</c> or <c>0</c> = use the
        /// per-agent/default fallback chain. Positive value wins over per-agent and global defaults.
        /// Propagated to
        /// <see cref="LlmCompletionRequest.MaxOutputTokens"/> by the orchestrator. Honored uniformly
        /// by HTTP and LLMUnity backends.
        /// </summary>
        public int? MaxOutputTokens { get; set; }
    }
}