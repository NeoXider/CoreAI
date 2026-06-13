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
            "Full Lua Mode skill: when Full is enabled, first run a diagnostic script and return a compact string/JSON from Output; " +
            "then use manage_mods for persistent hooks. Full scene APIs include unity_list_objects(max), " +
            "unity_find_all(pattern,max), unity_find_by_tag(tag,max), unity_find_by_component(type,max), " +
            "unity_describe_object(id), unity_get_transform(id), unity_set_position(id,x,y,z), " +
            "unity_set_rotation_euler(id,x,y,z), unity_set_scale(id,x,y,z), unity_parent(child,parent,worldPositionStays), " +
            "unity_get_children(id), unity_list_components(id), unity_get_member(id,component,member), " +
            "unity_set_member(id,component,member,value), and unity_call(id,component,method,args). " +
            "For night, find a Light and set Light.intensity through unity_set_member. " +
            "For visible spawns, call coreai_world_list_prefabs first, then coreai_world_spawn/coreai_world_spawn_batch with a real prefab key; report() alone is not a spawn. " +
            "Use report(message) to describe the applied change. Do not invent helper globals; " +
            "only call APIs listed by the prompt/tool contract or discovered from the environment. " +
            "Never call invented APIs such as game.enemies, game.create, game.destroy, or GameObject.Find from Lua.";

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "  \"type\": \"object\"," +
            "  \"properties\": {" +
            "    \"code\": { \"type\": \"string\", \"description\": \"Lua code to execute. Prefer logic_list(), logic_define(name, function(...) return value end), logic_reset(name), and report(message) when available. Full mode: first inspect with unity_list_objects(max), unity_find_all(pattern,max), unity_find_by_tag(tag,max), unity_find_by_component(type,max), unity_describe_object(id), or unity_get_transform(id); then edit with unity_set_position, unity_set_rotation_euler, unity_set_scale, unity_parent, unity_get_children, unity_set_member, or unity_call. For visible spawns use coreai_world_list_prefabs then coreai_world_spawn with a real key. Return compact JSON/string for diagnostics. Example: logic_define('loot_formula', function(bossMaxHp) return 1000 end) report('Boss reward set to 1000 coins')\" }" +
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
