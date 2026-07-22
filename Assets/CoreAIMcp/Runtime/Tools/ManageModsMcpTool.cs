using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>manage_mods</c> tool: a thin adapter over the existing <see cref="LuaModsLlmTool"/> so an
    /// external agent inspects and rewrites the running game's persistent Lua mods exactly like the
    /// in-game agent. Runs on the Unity main thread (dispatcher-marshalled).
    /// </summary>
    public sealed class ManageModsMcpTool : IMcpTool
    {
        private readonly LuaModsLlmTool _inner;

        /// <param name="runtime">The live mod runtime.</param>
        /// <param name="settings">Settings driving tool-call logging.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="grantedCapabilities">Capability tier applied to mods loaded through this tool.</param>
        public ManageModsMcpTool(ILuaModRuntime runtime, ICoreAISettings settings, ILog logger,
            LuaCapabilities grantedCapabilities)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            _inner = new LuaModsLlmTool(runtime, settings, logger, grantedCapabilities);
        }

        /// <inheritdoc />
        public string Name => "manage_mods";

        /// <inheritdoc />
        public string Description =>
            "Inspect and rewrite the running game's persistent Lua mods (long-lived scripts with " +
            "hooks_on/hooks_every handlers). Actions: list (loaded mods), get_source (read a mod's Lua), " +
            "load (compile new source AND start it; auto-persists), reload (replace a loaded mod's code, " +
            "keeping its store entry and permissions), unload (tear a mod down without deleting storage), " +
            "export/import (move a mod between players via a bundle), forget (unload AND delete from storage), " +
            "versions/revert (list saved revisions and roll back), diagnostics (recent runtime hook/timer errors). " +
            "A mod that keeps throwing is quarantined (still listed, dispatch suspended) until a successful reload. " +
            "Call read_skill('Lua Modding') for the full hooks/store/events API before authoring a mod.";

        /// <inheritdoc />
        public string InputSchemaJson => _inner.ParametersSchema;

        /// <inheritdoc />
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            string action = arguments?["action"]?.ToString();
            string modId = arguments?["mod_id"]?.ToString();
            string code = arguments?["code"]?.ToString();
            string bundle = arguments?["bundle"]?.ToString();
            int revision = arguments?["revision"] != null ? arguments["revision"].Value<int>() : -1;

            string json = await _inner
                .ExecuteAsync(action, modId, code, bundle, revision, cancellationToken)
                .ConfigureAwait(false);

            bool isError = TryReadSuccessFalse(json);
            return new McpToolResult(new[] { McpContent.CreateText(json) }, isError);
        }

        // WHY: LuaModsLlmTool encodes failures as {"success":false,...}; surface that as an MCP isError
        // so a client can distinguish a rejected action from a completed one without parsing prose.
        private static bool TryReadSuccessFalse(string json)
        {
            try
            {
                JObject obj = JObject.Parse(json);
                return obj["success"]?.Type == JTokenType.Boolean && obj["success"].Value<bool>() == false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
