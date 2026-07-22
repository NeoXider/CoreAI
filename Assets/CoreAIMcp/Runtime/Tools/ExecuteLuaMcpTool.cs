using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>execute_lua</c> tool: runs a one-off Lua snippet through the SAME sandboxed executor the
    /// in-game <c>execute_lua</c> LLM tool uses (<see cref="LuaTool.ILuaExecutor"/>), so an external
    /// agent drives the running game exactly like the on-board agent. Body runs on the Unity main
    /// thread (marshalled by the dispatcher) because Lua bindings touch live game state.
    /// </summary>
    public sealed class ExecuteLuaMcpTool : IMcpTool
    {
        private readonly LuaTool.ILuaExecutor _executor;

        /// <param name="executor">The shared one-off Lua executor resolved from the mod stack.</param>
        public ExecuteLuaMcpTool(LuaTool.ILuaExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        /// <inheritdoc />
        public string Name => "execute_lua";

        /// <inheritdoc />
        public string Description =>
            "Run a one-off snippet in the game's sandboxed Lua 5.2 VM (Lua-CSharp), the same executor " +
            "the in-game agent uses. State does not persist between calls - use manage_mods for long-lived " +
            "hooks. Survival-minimum globals: report(message) to describe what you did; " +
            "coreai_world_list_prefabs(), coreai_world_spawn({prefab,name,x,y,z,rx,ry,rz,scale,scaleX,scaleY,scaleZ,parent}), " +
            "coreai_world_change(name,{...}), coreai_world_set_color(name,html), coreai_world_destroy(name) for safe scene " +
            "edits; logic_list()/logic_define(name, function(...) return v end) for rule slots. Return a compact " +
            "string/JSON for diagnostics. Do NOT invent globals (no game.create, GameObject.Find, etc.). " +
            "Call read_skill('Lua Modding') or read_skill('Rbx API') FIRST for the full API reference.";

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\"," +
            "\"properties\":{\"code\":{\"type\":\"string\"," +
            "\"description\":\"Lua source to execute in the sandbox. Multi-line supported (use real newlines).\"}}," +
            "\"required\":[\"code\"]}";

        /// <inheritdoc />
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            string code = arguments?["code"]?.ToString();
            if (string.IsNullOrEmpty(code))
            {
                return McpToolResult.Failure("execute_lua: 'code' is required.");
            }

            LuaTool.LuaResult result = await _executor.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);
            string payload = JsonConvert.SerializeObject(result ?? new LuaTool.LuaResult
            {
                Success = false,
                Error = "Lua executor returned null."
            });

            return new McpToolResult(
                new[] { McpContent.CreateText(payload) },
                isError: result == null || !result.Success);
        }
    }
}
