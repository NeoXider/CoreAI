using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>world_command</c> tool: a thin adapter over the existing <see cref="WorldLlmTool"/> so an
    /// external agent spawns, moves, and edits scene objects through the SAME command executor the
    /// in-game agent uses. Present ONLY when a world-command executor resolved in the current
    /// composition (see <c>CoreAiMcpToolProvider</c>). Runs on the Unity main thread.
    /// </summary>
    public sealed class WorldCommandMcpTool : IMcpTool
    {
        private readonly WorldLlmTool _inner;

        /// <param name="inner">The constructed world tool bound to the live command executor.</param>
        public WorldCommandMcpTool(WorldLlmTool inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public string Name => "world_command";

        /// <inheritdoc />
        public string Description =>
            "Manipulate live game-world objects and scenes. 1 unit = 1 meter (cube/sphere unscaled = 1m; " +
            "cylinder/capsule = 2m tall). Rotations are Euler degrees. Actions: spawn, spawn_batch, " +
            "list_prefabs, change, set_color, destroy, load_scene, reload_scene, set_active, play_animation, " +
            "stop_animation, list_animations, play_sound, set_volume, show_text, hide_panel, apply_force, " +
            "set_velocity, list_objects. spawn needs a prefabKey (a registered key or a primitive: cube, " +
            "sphere, cylinder, capsule, empty) and a targetName; call list_prefabs first if unsure. change " +
            "edits only the fields you pass. All results are compact JSON {success,message,action}.";

        /// <inheritdoc />
        public string InputSchemaJson => _inner.ParametersSchema;

        /// <inheritdoc />
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            JObject a = arguments ?? new JObject();
            string action = McpArguments.String(a, "action");
            if (string.IsNullOrEmpty(action))
            {
                return McpToolResult.Failure("world_command: 'action' is required.");
            }

            string json = await _inner.ExecuteAsync(
                action,
                McpArguments.FloatOrNull(a, "x"),
                McpArguments.FloatOrNull(a, "y"),
                McpArguments.FloatOrNull(a, "z"),
                McpArguments.FloatOrNull(a, "fx"),
                McpArguments.FloatOrNull(a, "fy"),
                McpArguments.FloatOrNull(a, "fz"),
                McpArguments.FloatOrNull(a, "scale"),
                McpArguments.FloatOrNull(a, "scaleX"),
                McpArguments.FloatOrNull(a, "scaleY"),
                McpArguments.FloatOrNull(a, "scaleZ"),
                McpArguments.String(a, "prefabKey"),
                McpArguments.String(a, "targetName"),
                McpArguments.String(a, "stringValue"),
                McpArguments.Bool(a, "worldPositionStays"),
                McpArguments.String(a, "animationName"),
                McpArguments.String(a, "textToDisplay"),
                McpArguments.Float(a, "volume", 1f),
                McpArguments.String(a, "itemsJson"),
                cancellationToken).ConfigureAwait(false);

            return new McpToolResult(new[] { McpContent.CreateText(json) });
        }
    }
}
