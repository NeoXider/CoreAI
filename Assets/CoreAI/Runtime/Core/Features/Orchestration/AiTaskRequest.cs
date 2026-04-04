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
    }
}