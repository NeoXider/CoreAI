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
            "Execute sandboxed Lua code using only globals exposed by the current game. " +
            "For game-rule changes, call logic_list() when unsure, then use " +
            "logic_define('slot_name', function(...) return value end); for example " +
            "logic_define('loot_formula', function(bossMaxHp) return 1000 end). " +
            "Use report(message) to describe the applied change. Do not invent helper globals; " +
            "only call APIs listed by the prompt/tool contract or discovered from the environment.";

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "  \"type\": \"object\"," +
            "  \"properties\": {" +
            "    \"code\": { \"type\": \"string\", \"description\": \"Lua code to execute. Prefer logic_list(), logic_define(name, function(...) return value end), logic_reset(name), and report(message) when available. Example: logic_define('loot_formula', function(bossMaxHp) return 1000 end) report('Boss reward set to 1000 coins')\" }" +
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
