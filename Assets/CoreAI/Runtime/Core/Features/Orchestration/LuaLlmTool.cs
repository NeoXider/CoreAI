using CoreAI.Ai;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool wrapper that exposes the sandboxed Lua executor as <c>execute_lua</c>.
    /// </summary>
    public sealed class LuaLlmTool : IAIFunctionLlmTool
    {
        private readonly LuaTool.ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaGenerationRateLimiter _rateLimiter;

        public LuaLlmTool(LuaTool.ILuaExecutor executor, ICoreAISettings settings, ILog logger,
            LuaGenerationRateLimiter rateLimiter = null)
        {
            _executor = executor;
            _settings = settings;
            _logger = logger;
            // Owned here (not in CreateAIFunction) so the sliding window survives repeated
            // AIFunction creation; pass the envelope pipeline's limiter to share one budget.
            _rateLimiter = rateLimiter ?? new LuaGenerationRateLimiter();
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
        /// Creates the MEAI function delegated to <see cref="LuaTool"/>.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            LuaTool tool = new(_executor, _settings, _logger, _rateLimiter);
            return tool.CreateAIFunction();
        }
    }
}