using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
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
        /// <param name="actorIdentityProvider">Explicit identity assigned to MCP mod calls by the host.</param>
        public ManageModsMcpTool(ILuaModRuntime runtime, ICoreAISettings settings, ILog logger,
            LuaCapabilities grantedCapabilities, IActorIdentityProvider actorIdentityProvider)
            : this(runtime, settings, logger, grantedCapabilities, actorIdentityProvider, null)
        {
        }

        /// <summary>
        /// The tool with a rate limit on its mutating actions: pass the limiter the mods scope registers, so an
        /// external agent spends the same budget of AI world changes as the in-game <c>execute_lua</c> and
        /// <c>manage_mods</c>; null leaves MCP <c>manage_mods</c> unlimited.
        /// </summary>
        public ManageModsMcpTool(ILuaModRuntime runtime, ICoreAISettings settings, ILog logger,
            LuaCapabilities grantedCapabilities, IActorIdentityProvider actorIdentityProvider,
            LuaGenerationRateLimiter rateLimiter)
            : this(runtime, settings, logger, grantedCapabilities, actorIdentityProvider, rateLimiter, null)
        {
        }

        /// <summary>
        /// The tool with the shared rate limit and the mod store's folder list (the one the in-game
        /// <c>manage_mods</c>, the Hub Mods page and the world session use); null keeps one in-memory list.
        /// </summary>
        public ManageModsMcpTool(ILuaModRuntime runtime, ICoreAISettings settings, ILog logger,
            LuaCapabilities grantedCapabilities, IActorIdentityProvider actorIdentityProvider,
            LuaGenerationRateLimiter rateLimiter, CoreAI.Mods.Folders.IModFolderListStore folderList)
            : this(runtime, settings, logger, grantedCapabilities, actorIdentityProvider, rateLimiter, folderList, null)
        {
        }

        /// <summary>
        /// The tool behind the same world mutation gate as the in-game <c>manage_mods</c>: a load, reload or edit
        /// waits for the backup autosave of the world before it runs and is serialized with every other world change,
        /// so an MCP change is as undoable as the in-game agent's. Null runs ungated (no backup).
        /// </summary>
        public ManageModsMcpTool(ILuaModRuntime runtime, ICoreAISettings settings, ILog logger,
            LuaCapabilities grantedCapabilities, IActorIdentityProvider actorIdentityProvider,
            LuaGenerationRateLimiter rateLimiter, CoreAI.Mods.Folders.IModFolderListStore folderList,
            CoreAI.Mods.WorldPackages.IConfirmedWorldMutationGate worldMutationGate)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            if (actorIdentityProvider == null)
            {
                throw new ArgumentNullException(nameof(actorIdentityProvider));
            }

            _inner = new LuaModsLlmTool(
                runtime,
                settings,
                logger,
                grantedCapabilities,
                allowModManagement: true,
                actorIdentityProvider: actorIdentityProvider,
                roleId: BuiltInAgentRoleIds.Programmer,
                worldMutationGate: worldMutationGate,
                rateLimiter: rateLimiter,
                folderList: folderList);
        }

        /// <summary>The limiter the mutating actions spend; null when they are not limited.</summary>
        public LuaGenerationRateLimiter RateLimiter => _inner.RateLimiter;

        /// <inheritdoc />
        public string Name => "manage_mods";

        /// <inheritdoc />
        public string Description =>
            "Inspect and rewrite the running game's persistent Lua mods (long-lived scripts with " +
            "hooks_on/hooks_every handlers). Actions: list (loaded mods), get_source (read a mod's Lua in pages " +
            "of whole lines: from_line/line_count; follow next_from_line until it is absent), edit (replace " +
            "old_text, copied exactly from get_source, with new_text and reload the mod; replace_all for every " +
            "occurrence), load (compile new source AND start it; auto-persists), reload (replace a loaded mod's " +
            "WHOLE code, keeping its store entry and permissions), unload (tear a mod down without deleting storage), " +
            "export/import (move a mod between players via a bundle), forget (unload AND delete from storage), " +
            "versions/revert (list saved revisions and roll back), diagnostics (recent runtime hook/timer errors). " +
            "A mod that keeps throwing is quarantined (still listed, dispatch suspended) until a successful reload " +
            "or edit. " +
            "Call read_skill('Lua Modding') for the full hooks/store/events API before authoring a mod.";

        /// <inheritdoc />
        public string InputSchemaJson => _inner.ParametersSchema;

        /// <inheritdoc />
        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            string action = McpArguments.String(arguments, "action");
            string modId = McpArguments.String(arguments, "mod_id");
            string code = McpArguments.String(arguments, "code");
            string bundle = McpArguments.String(arguments, "bundle");
            int revision = McpArguments.Int(arguments, "revision", -1);
            bool keepObjects = McpArguments.Bool(arguments, "keep_objects");
            // WHY null when absent and never "": move reads an empty folder as the top level.
            string folder = McpArguments.String(arguments, "folder");
            string target = McpArguments.String(arguments, "target");
            int fromLine = McpArguments.Int(arguments, "from_line", 0);
            int lineCount = McpArguments.Int(arguments, "line_count", 0);
            string oldText = McpArguments.String(arguments, "old_text");
            string newText = McpArguments.String(arguments, "new_text");
            bool replaceAll = McpArguments.Bool(arguments, "replace_all");

            string json = await _inner
                .ExecuteAsync(action, modId, code, bundle, revision, cancellationToken, keepObjects, folder, target,
                    fromLine, lineCount, oldText, newText, replaceAll)
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
