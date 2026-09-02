using CoreAI.Ai;
using Microsoft.Extensions.AI;
using CoreAI.Logging;
using CoreAI.Authority;

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
        public string ParametersSchema => "{" +
              "  \"type\": \"object\"," +
              "  \"properties\": {" +
              "    \"code\": { \"type\": \"string\", \"description\": \"Lua code to execute. Prefer logic_list(), logic_define(name, function(...) return value end), logic_reset(name), and report(message) when available. Build world objects Roblox-style (Instance.new('Part'), game/workspace) - see read_skill('Rbx API'). Inspect the scene with coreai_world_find(pattern), coreai_world_pos(name), coreai_world_exists(name). Full reflection is a rarely-needed backup - see read_skill('Full Lua'). Return compact JSON/string for diagnostics. Example: logic_define('loot_formula', function(bossMaxHp) return 1000 end) report('Boss reward set to 1000 coins')\" }" +
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
