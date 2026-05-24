using CoreAI.Ai;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool implementation for lua llm operations.
    /// </summary>
    public sealed class LuaLlmTool : ILlmTool
    {
        private readonly LuaTool.ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;

        public LuaLlmTool(LuaTool.ILuaExecutor executor, ICoreAISettings settings, ILog logger)
        {
            _executor = executor;
            _settings = settings;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "execute_lua";

        /// <inheritdoc />
        public bool AllowDuplicates => true;

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
/// Executes CreateAIFunction API operation.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            LuaTool tool = new(_executor, _settings, _logger);
            return tool.CreateAIFunction();
        }
    }
}
