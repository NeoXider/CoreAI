using CoreAI.Ai;
using Microsoft.Extensions.AI;
using CoreAI.Logging;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool wrapper that exposes the sandboxed Lua executor as <c>execute_lua</c>.
    /// </summary>
    public sealed class LuaLlmTool : IAIFunctionLlmTool, ILlmToolResultCutAdvice
    {
        /// <inheritdoc />
        public string AdviseOnCut(string functionName, System.Collections.Generic.IDictionary<string, object> arguments)
        {
            return "Return less from the Lua: a count, a few fields or the first items, not a whole table.";
        }

        private readonly LuaTool.ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaGenerationRateLimiter _rateLimiter;
        private readonly IActorIdentityProvider _actorIdentityProvider;
        private readonly string _roleId;

        public LuaLlmTool(LuaTool.ILuaExecutor executor, ICoreAISettings settings, ILog logger,
            LuaGenerationRateLimiter rateLimiter = null,
            IActorIdentityProvider actorIdentityProvider = null,
            string roleId = null)
        {
            _executor = executor;
            _settings = settings;
            _logger = logger;
            // WHY: Owned here (not in CreateAIFunction) so the sliding window survives repeated
            // AIFunction creation; pass the envelope pipeline's limiter to share one budget.
            _rateLimiter = rateLimiter ?? new LuaGenerationRateLimiter();
            _actorIdentityProvider = actorIdentityProvider;
            _roleId = roleId;
        }

        /// <inheritdoc />
        public string Name => LuaTool.ExecuteLuaToolName;

        /// <inheritdoc />
        // WHY: Arbitrary Lua can mutate host/world state non-idempotently, so identical cross-turn echoes
        // must not re-run. AllowDuplicates=false lets ToolExecutionPolicy suppress only a CROSS-TURN
        // byte-identical echo (structured no-op); several DIFFERENT Lua blocks in one turn, intra-turn
        // repeats, and the retry of a FAILED block all still execute.
        public bool AllowDuplicates => false;

        /// <inheritdoc />
        public string Description => LuaTool.ExecuteLuaDescription;

        /// <inheritdoc />
        // WHY the same text as the bound function's parameter: the text tool contract and the retry hints read this
        // schema, the provider reads the function's; two wordings drifted apart before.
        public string ParametersSchema => "{" +
              "  \"type\": \"object\"," +
              "  \"properties\": {" +
              "    \"code\": { \"type\": \"string\", \"description\": \"" +
              LuaTool.ExecuteLuaCodeParameterDescription + "\" }" +
              "  }," +
              "  \"required\": [\"code\"]" +
              "}";

        /// <summary>
        /// Creates the MEAI function delegated to <see cref="LuaTool"/>.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            LuaTool tool = new(
                _executor,
                _settings,
                _logger,
                _rateLimiter,
                _actorIdentityProvider,
                _roleId);
            return tool.CreateAIFunction();
        }
    }
}
