using CoreAI.Ai;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Обёртка LuaTool для ILlmTool интерфейса.
    /// Позволяет использовать Lua tool в оркестраторе через MEAI function calling.
    /// </summary>
    public sealed class LuaLlmTool : ILlmTool
    {
        private readonly LuaTool.ILuaExecutor _executor;

        public LuaLlmTool(LuaTool.ILuaExecutor executor)
        {
            _executor = executor;
        }

        /// <inheritdoc />
        public string Name => "execute_lua";

        /// <inheritdoc />
        public string Description =>
            "Execute Lua code to perform game actions, create items, modify state, report events. " +
            "Use functions like create_item(), report(), add(), etc. available in the Lua environment.";

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "  \"type\": \"object\"," +
            "  \"properties\": {" +
            "    \"code\": { \"type\": \"string\", \"description\": \"Lua code to execute. Use create_item(name, type, quality) and report(message) functions.\" }" +
            "  }," +
            "  \"required\": [\"code\"]" +
            "}";

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            LuaTool tool = new(_executor);
            return tool.CreateAIFunction();
        }
    }
}