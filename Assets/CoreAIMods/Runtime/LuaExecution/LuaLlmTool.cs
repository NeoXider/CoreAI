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
        public string Name => LuaTool.ExecuteLuaToolName;

        /// <inheritdoc />
        // Arbitrary Lua can mutate host/world state non-idempotently, so identical cross-turn echoes
        // must not re-run. AllowDuplicates=false lets ToolExecutionPolicy suppress only a CROSS-TURN
        // byte-identical echo (structured no-op); several DIFFERENT Lua blocks in one turn, intra-turn
        // repeats, and the retry of a FAILED block all still execute.
        public bool AllowDuplicates => false;

        /// <inheritdoc />
        public string Description => LuaTool.ExecuteLuaDescription;

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "  \"type\": \"object\"," +
            "  \"properties\": {" +
            "    \"code\": { \"type\": \"string\", \"description\": \"Lua code to execute. Prefer logic_list(), logic_define(name, function(...) return value end), logic_reset(name), and report(message) when available. WorldEdit mode: use coreai_world_list_prefabs, coreai_world_spawn({prefab,name,x,y,z,rx,ry,rz,scale,scaleX,scaleY,scaleZ,parent}), coreai_world_change(name,{x,y,z,rx,ry,rz,scale,scaleX,scaleY,scaleZ,parent}), coreai_world_set_color, and coreai_world_destroy. Full mode: first inspect with unity_list_objects(max), unity_find_all(pattern,max), unity_find_by_tag(tag,max), unity_find_by_component(type,max), unity_describe_object(id), or unity_get_transform(id); then edit with the smallest real API that matches the host. Return compact JSON/string for diagnostics. Example: logic_define('loot_formula', function(bossMaxHp) return 1000 end) report('Boss reward set to 1000 coins')\" }" +
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