using System;
using System.ComponentModel;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// MEAI <see cref="AIFunction"/> that runs Lua for the Programmer agent, used in the native tool-calling path instead of fenced Lua blocks.
    /// </summary>
    public sealed class LuaTool
    {
        public const string ExecuteLuaToolName = "execute_lua";
        public const string ExecuteLuaDescription =
            "Execute sandboxed Lua code using only globals exposed by the current game. " +
            "Runtime rule slots are changed through the logic_* API: call logic_list() when unsure, then use " +
            "logic_define('slot_name', function(...) return value end); for example " +
            "logic_define('loot_formula', function(bossMaxHp) return 1000 end). " +
            "Full Lua Mode skill: when Full is enabled, first run a diagnostic script and return a compact string/JSON from Output; " +
            "then use manage_mods for persistent hooks. Full scene APIs include unity_list_objects(max), " +
            "unity_find_all(pattern,max), unity_find_by_tag(tag,max), unity_find_by_component(type,max), " +
            "unity_describe_object(id), unity_get_transform(id), unity_set_position(id,x,y,z), " +
            "unity_set_rotation_euler(id,x,y,z), unity_set_scale(id,x,y,z), unity_parent(child,parent,worldPositionStays), " +
            "unity_get_children(id), unity_list_components(id), unity_get_member(id,component,member), " +
            "unity_set_member(id,component,member,value), and unity_call(id,component,method,args). " +
            "WorldEdit APIs do not require Full mode: use coreai_world_spawn, coreai_world_move, coreai_world_rotate, coreai_world_set_transform, coreai_world_destroy, coreai_world_parent, and coreai_world_set_props for safe scene edits. " +
            "For visible spawns, call coreai_world_list_prefabs first, then coreai_world_spawn/coreai_world_spawn_batch with a real prefab key; report() alone is not a spawn. " +
            "Do not hard-code visual recipes; inspect the scene/components first, then use the smallest real API that matches the host. " +
            "Use report(message) to describe the applied change. Do not invent helper globals; " +
            "only call APIs listed by the prompt/tool contract or discovered from the environment. " +
            "Never call invented APIs such as game.rules, game_rules, game.enemies, game.create, game.destroy, or GameObject.Find from Lua.";

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        private readonly ILuaExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaGenerationRateLimiter _rateLimiter;

        public LuaTool(ILuaExecutor executor, ICoreAISettings settings, ILog logger,
            LuaGenerationRateLimiter rateLimiter = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rateLimiter = rateLimiter ?? new LuaGenerationRateLimiter();
        }

        /// <summary>Rate limiter shared with (or mirroring) the envelope pipeline.</summary>
        public LuaGenerationRateLimiter RateLimiter => _rateLimiter;

        /// <summary>Builds the MEAI tool surface for <c>execute_lua</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = ExecuteLuaToolName,
                Description = ExecuteLuaDescription
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Runs Lua returned from the model payload.</summary>
        /// <param name="code">Source to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<string> ExecuteAsync(
            [Description(
                "Lua code to execute. Prefer logic_list(), logic_define(name, function(...) return value end), logic_reset(name), and report(message) when available. WorldEdit mode: use coreai_world_list_prefabs, coreai_world_spawn, coreai_world_move, coreai_world_rotate, coreai_world_set_transform, coreai_world_destroy, coreai_world_parent, and coreai_world_set_props. Full mode: first inspect with unity_list_objects(max), unity_find_all(pattern,max), unity_find_by_tag(tag,max), unity_find_by_component(type,max), unity_describe_object(id), or unity_get_transform(id); then edit with the smallest real API that matches the host. Return compact JSON/string for diagnostics.")]
            string code,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(code))
            {
                return SerializeResult(new LuaResult { Success = false, Error = "Lua code is required" });
            }

            if (!_rateLimiter.TryAcquire(Clock.Elapsed.TotalSeconds))
            {
                string limitError =
                    $"Lua rate limit exceeded ({_rateLimiter.MaxPerWindow} per {_rateLimiter.WindowSeconds:0}s); call rejected.";
                if (_settings.LogToolCallResults)
                {
                    _logger.Warn($"[Tool Call] execute_lua: {limitError}");
                }

                return SerializeResult(new LuaResult { Success = false, Error = limitError });
            }

            if (_settings.LogToolCalls)
            {
                _logger.Info($"[Tool Call] execute_lua: code length={code.Length}");
            }

            if (_settings.LogToolCallArguments)
            {
                string preview = code.Length > 150 ? code.Substring(0, 150) : code;
                _logger.Info($"  code preview: {preview}");
            }

            try
            {
                LuaResult result = await _executor.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);

                if (_settings.LogToolCallResults)
                {
                    string outputPreview =
                        result.Output?.Length > 100 ? result.Output.Substring(0, 100) : result.Output;
                    _logger.Info(
                        $"[Tool Call] execute_lua: {(result.Success ? "SUCCESS" : "FAILED")} - output={outputPreview}");
                }

                return SerializeResult(result);
            }
            catch (Exception ex)
            {
                if (_settings.LogToolCallResults)
                {
                    _logger.Error($"[Tool Call] execute_lua: FAILED - {ex}");
                }

                return SerializeResult(new LuaResult
                {
                    Success = false,
                    Error = $"Lua execution failed: {ex.Message}"
                });
            }
        }

        private static string SerializeResult(LuaResult result)
        {
            return JsonConvert.SerializeObject(result);
        }

        /// <summary>Lua execution outcome for JSON serialization back to the model.</summary>
        public sealed class LuaResult
        {
            public bool Success { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }

        /// <summary>Abstraction over the concrete Lua host (testable without Unity).</summary>
        public interface ILuaExecutor
        {
            Task<LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken);
        }
    }
}
