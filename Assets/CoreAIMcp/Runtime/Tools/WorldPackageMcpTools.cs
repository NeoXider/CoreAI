using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mcp.Protocol;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP adapters over the in-game world-package tools (<see cref="SaveWorldLlmTool"/>,
    /// <see cref="LoadWorldLlmTool"/>, <see cref="ListAutoSavesLlmTool"/>, <see cref="LoadAutoSaveLlmTool"/>):
    /// the same validation, refusals and JSON the in-game Programmer agent sees, with the outcome mapped
    /// to MCP <c>isError</c>. A load is only requested: the player confirms it in the host UI, exactly as
    /// for the in-game agent, so a staged request is a success whose text says confirmation is pending.
    /// </summary>
    public static class WorldPackageMcpTools
    {
        /// <summary>Name of the manual-save tool.</summary>
        public const string SaveWorldToolName = "save_world";

        /// <summary>Name of the manual-load request tool.</summary>
        public const string LoadWorldToolName = "load_world";

        /// <summary>Name of the autosave listing tool.</summary>
        public const string ListAutoSavesToolName = "list_autosaves";

        /// <summary>Name of the autosave load request tool.</summary>
        public const string LoadAutoSaveToolName = "load_autosave";

        /// <summary>Status the world tools report for a load request that waits for the player.</summary>
        public const string ConfirmationRequiredStatus = "player_confirmation_required";

        /// <summary>
        /// Creates the tools. <c>list_autosaves</c>, <c>load_world</c> and <c>load_autosave</c> act as
        /// <paramref name="identityProvider"/>; <c>save_world</c> writes a slot that survives a restart, so
        /// it exists only when <paramref name="hostAdminIdentityProvider"/> is supplied (the same admission
        /// <c>manage_mods</c> requires) and then acts as that identity. Null service or identity yields none.
        /// </summary>
        public static IReadOnlyList<IMcpTool> Create(IRbxWorldRuntimeService service,
            IActorIdentityProvider identityProvider, IActorIdentityProvider hostAdminIdentityProvider = null)
        {
            List<IMcpTool> tools = new();
            IActorIdentityProvider requester = hostAdminIdentityProvider ?? identityProvider;
            if (service == null || requester == null)
            {
                return tools;
            }

            if (hostAdminIdentityProvider != null)
            {
                SaveWorldLlmTool save = new(service, hostAdminIdentityProvider, BuiltInAgentRoleIds.Programmer);
                tools.Add(new JsonResultMcpTool(SaveWorldToolName,
                    "Save the running world (instance tree, exact mod sources, settings, parts and camera) to a " +
                    "new manual slot that survives a restart. Slots are create-once: an existing slot name fails, " +
                    "so choose a new one. Returns {success, path, error}.",
                    save.ParametersSchema,
                    (arguments, token) => save.ExecuteAsync(McpArguments.String(arguments, "slot"), token),
                    IsSaveFailure));
            }

            LoadWorldLlmTool load = new(service, requester, BuiltInAgentRoleIds.Programmer);
            tools.Add(new JsonResultMcpTool(LoadWorldToolName,
                "Request loading a manual world slot saved with save_world. Nothing is applied until the player " +
                "confirms the request in the game UI: a staged request returns isError=false with status '" +
                ConfirmationRequiredStatus + "' and a request_id. A missing or unreadable slot is an error.",
                load.ParametersSchema,
                (arguments, token) => load.ExecuteAsync(McpArguments.String(arguments, "slot"), token),
                IsLoadFailure));

            ListAutoSavesLlmTool list = new(service, requester, BuiltInAgentRoleIds.Programmer);
            tools.Add(new JsonResultMcpTool(ListAutoSavesToolName,
                "List the world's autosave packages (name, trigger, UTC timestamp, size in bytes). Read-only. " +
                "Pass a listed name to load_autosave.",
                list.ParametersSchema,
                (arguments, token) => list.ExecuteAsync(token),
                IsListFailure));

            LoadAutoSaveLlmTool loadAuto = new(service, requester, BuiltInAgentRoleIds.Programmer);
            tools.Add(new JsonResultMcpTool(LoadAutoSaveToolName,
                "Request loading an autosave by the exact name list_autosaves returned. Nothing is applied until " +
                "the player confirms the request in the game UI: a staged request returns isError=false with " +
                "status '" + ConfirmationRequiredStatus + "' and a request_id. A rotated-out or corrupt autosave " +
                "is an error.",
                loadAuto.ParametersSchema,
                (arguments, token) => loadAuto.ExecuteAsync(McpArguments.String(arguments, "name"), token),
                IsLoadFailure));
            return tools;
        }

        /// <summary>A save failed unless the tool reported <c>success: true</c>.</summary>
        internal static bool IsSaveFailure(JObject result) => !IsTrue(result, "success");

        /// <summary>A load request failed unless it succeeded or was staged with a request id for the player.</summary>
        internal static bool IsLoadFailure(JObject result) => !IsTrue(result, "success") && !IsStagedLoad(result);

        /// <summary>A listing failed only when the tool reported <c>success: false</c>.</summary>
        internal static bool IsListFailure(JObject result) => IsFalse(result, "success");

        private static bool IsStagedLoad(JObject result)
        {
            return result["status"]?.Type == JTokenType.String &&
                   (string)result["status"] == ConfirmationRequiredStatus &&
                   !string.IsNullOrEmpty((string)result["request_id"]);
        }

        private static bool IsTrue(JObject result, string field)
        {
            return result[field]?.Type == JTokenType.Boolean && (bool)result[field];
        }

        private static bool IsFalse(JObject result, string field)
        {
            return result[field]?.Type == JTokenType.Boolean && !(bool)result[field];
        }
    }

    /// <summary>
    /// An MCP tool over a body that returns JSON text: the text is passed through unchanged and
    /// <c>isError</c> is decided by a predicate over the parsed object. Text that is not a JSON object is
    /// reported as an error, never as an empty success.
    /// </summary>
    internal sealed class JsonResultMcpTool : IMcpTool
    {
        private readonly Func<JObject, CancellationToken, Task<string>> _invoke;
        private readonly Func<JObject, bool> _isError;

        public JsonResultMcpTool(string name, string description, string inputSchemaJson,
            Func<JObject, CancellationToken, Task<string>> invoke, Func<JObject, bool> isError)
        {
            Name = name;
            Description = description;
            InputSchemaJson = inputSchemaJson;
            _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
            _isError = isError ?? throw new ArgumentNullException(nameof(isError));
        }

        public string Name { get; }

        public string Description { get; }

        public string InputSchemaJson { get; }

        public async Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = await _invoke(arguments ?? new JObject(), cancellationToken).ConfigureAwait(false);
            JObject parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed == null)
            {
                return McpToolResult.Failure($"{Name}: the tool returned no JSON result: '{json}'.");
            }

            return new McpToolResult(new[] { McpContent.CreateText(json) }, _isError(parsed));
        }
    }
}
