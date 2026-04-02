namespace CoreAI.Ai
{
    public sealed class AiTaskRequest
    {
        /// <summary>Id роли: <see cref="BuiltInAgentRoleIds"/> или свой.</summary>
        public string RoleId { get; set; } = BuiltInAgentRoleIds.Creator;

        public string Hint { get; set; } = "";

        /// <summary>Номер попытки исправления Lua (0 — обычная задача Programmer).</summary>
        public int LuaRepairGeneration { get; set; }

        /// <summary>Код Lua, который упал (передаётся обратно в Programmer).</summary>
        public string LuaRepairPreviousCode { get; set; } = "";

        /// <summary>Сообщение об ошибке MoonSharp / выполнения.</summary>
        public string LuaRepairErrorMessage { get; set; } = "";

        /// <summary>Продолжить цепочку логов (ремонт Lua); если пусто — оркестратор создаст новый.</summary>
        public string TraceId { get; set; } = "";
    }
}
