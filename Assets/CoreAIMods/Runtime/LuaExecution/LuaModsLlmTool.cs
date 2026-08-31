using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool (<c>manage_mods</c>) that lets an agent inspect and rewrite its own persistent
    /// Lua mods in <c>LuaModRuntime</c>: list mods, read their source, load, reload, unload,
    /// export, import, and forget them; list a mod's revision history and roll back to an earlier
    /// revision (<c>versions</c>/<c>revert</c>); and read recent runtime hook/timer failures
    /// (<c>diagnostics</c>) so a mod that throws long after it loaded can be repaired. Loaded/reloaded
    /// mods auto-persist and survive a restart; export/import move a mod between players via a
    /// shareable bundle.
    /// <para>
    /// Mutating actions (<c>load</c>/<c>reload</c>/<c>unload</c>/<c>import</c>/<c>forget</c>)
    /// effectively grant the model the
    /// whole capability tier configured at registration, so hosts that only want introspection
    /// must construct the tool with <c>allowModManagement: false</c>. Loaded mods always receive
    /// the host-configured <see cref="LuaCapabilities"/> tier — the model cannot request a wider
    /// one per call.
    /// </para>
    /// </summary>
    public sealed class LuaModsLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        /// <summary>Cap on the source text returned by <c>get_source</c>.</summary>
        public const int MaxSourceLengthReturned = 16_000;

        private static readonly ConditionalWeakTable<ILuaModRuntime, object> AuthorizationGates = new();

        private readonly ILuaModRuntime _runtime;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaCapabilities _grantedCapabilities;
        private readonly bool _allowModManagement;
        private readonly IActorIdentityProvider _actorIdentityProvider;
        private readonly object _authorizationGate;
        private readonly string _roleId;

        /// <param name="runtime">Mod runtime to manage.</param>
        /// <param name="settings">Settings driving tool-call logging.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="grantedCapabilities">
        /// Capability tier applied to every mod loaded through this tool. The model cannot widen it.
        /// </param>
        /// <param name="allowModManagement">
        /// When false the tool is read-only and all mutating actions are refused.
        /// </param>
        public LuaModsLlmTool(
            ILuaModRuntime runtime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities grantedCapabilities = LuaCapabilities.All,
            bool allowModManagement = true)
            : this(
                runtime,
                settings,
                logger,
                grantedCapabilities,
                allowModManagement,
                LocalActorIdentityProvider.Default,
                BuiltInAgentRoleIds.Programmer)
        {
        }

        /// <summary>Creates an actor-aware mod tool that resolves identity at every call boundary.</summary>
        /// <param name="runtime">Mod runtime to manage.</param>
        /// <param name="settings">Settings driving tool-call logging.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="grantedCapabilities">Capability tier applied to loaded mods.</param>
        /// <param name="allowModManagement">Whether mutating actions are enabled.</param>
        /// <param name="actorIdentityProvider">Trusted actor resolver used at each tool-call boundary.</param>
        /// <param name="roleId">Role through which this tool is invoked.</param>
        public LuaModsLlmTool(
            ILuaModRuntime runtime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities grantedCapabilities,
            bool allowModManagement,
            IActorIdentityProvider actorIdentityProvider,
            string roleId)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _grantedCapabilities = grantedCapabilities;
            _allowModManagement = allowModManagement;
            _actorIdentityProvider = actorIdentityProvider ?? LocalActorIdentityProvider.Default;
            _authorizationGate = AuthorizationGates.GetValue(_runtime, CreateAuthorizationGate);
            _roleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Programmer : roleId.Trim();
        }

        /// <inheritdoc />
        public override string Name => "manage_mods";

        /// <inheritdoc />
        // WHY: Mutating mod actions (load/reload/unload/import/forget/revert) are non-idempotent, so an
        // identical cross-turn echo must not re-run. AllowDuplicates=false lets ToolExecutionPolicy
        // suppress only a CROSS-TURN byte-identical echo (structured no-op) while still allowing
        // intra-turn repeats and never suppressing the retry of a FAILED call.
        public override bool AllowDuplicates => false;

        /// <inheritdoc />
        public override string Description =>
            "Manage persistent Lua mods (long-lived scripts with hooks_on/hooks_every handlers). " +
            "Actions: list (all loaded mods), get_source (read a mod's Lua code), " +
            "load (install new mod), reload (replace a mod's code keeping its permissions), " +
            "unload (remove a mod), export (get a shareable bundle of a mod to move it to another player), " +
            "import (install a mod from a shareable bundle, passed in the 'bundle' or 'code' param), " +
            "forget (unload a mod and delete it from persistent storage), " +
            "versions (list a mod's saved revisions; revision 0 is the original, newest last), " +
            "revert (roll a mod back to an earlier revision, passed in the 'revision' param), " +
            "diagnostics (read recent runtime errors thrown by mod hooks/timers so you can repair the mod). " +
            "load/reload auto-persist mods so they survive an app restart; export/import move mods between players. " +
            "Each load/reload records a new revision; use versions then revert to undo a bad edit. " +
            "A hook or timer that starts throwing at runtime is reported via diagnostics (not as an error on the " +
            "call that loaded it) - poll diagnostics, fix the mod, and reload it. " +
            "A mod that keeps failing is QUARANTINED, never auto-unloaded: it stays in list (quarantined=true) " +
            "with its handlers/timers suspended, and a successful reload clears the quarantine and resumes it. " +
            "Use get_source before reload to edit existing behavior. " +
            "MoonSharp/Lua callback syntax: hooks_on('event', function(name, payload) ... end) " +
            "and hooks_every(seconds, function() ... end). Do not write hooks_on('event') function() ... end. " +
            "Extra globals inside mods: hooks_on('tick', fn) per-frame alias; store_set(key, value)/store_get(key) " +
            "per-mod persistent strings; events_emit(name, payload) between mods; mods_export(name, valueOrFn), " +
            "mods_get(modId, name), mods_call(modId, fnName, ...), mods_list_exports(modId) for cross-mod " +
            "variables and function calls (by-value; nil/boolean/number/string/plain tables only); " +
            "input_key/input_key_down/input_key_up/input_mouse_button/input_mouse_x/input_mouse_y/input_axis " +
            "to read player input from timers.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("action", "string", true,
                "One of: list, get_source, load, reload, unload, export, import, forget, versions, revert, diagnostics"),
            ("mod_id", "string", false,
                "Mod id (required for get_source, load, reload, unload, export, forget, versions, revert; optional filter for diagnostics)"),
            ("code", "string", false,
                "Lua source for load/reload. Multi-line code is fully supported: write newlines as standard JSON \\n escapes inside this string (do not double-escape as \\\\n - that reaches Lua as a literal backslash and breaks parsing). Valid callbacks: hooks_on('event', function(name, payload) ... end); hooks_every(seconds, function() ... end). For import, the shareable bundle may be passed here if 'bundle' is omitted."),
            ("bundle", "string", false,
                "Shareable mod bundle JSON (as returned by export) for the import action."),
            ("revision", "integer", false,
                "Revision index to roll back to for the revert action (as listed by versions; 0 is the original).")
        );

        /// <summary>Creates the MEAI function surface for <c>manage_mods</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string, string, string, int, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Executes a mod-management action and returns a JSON result for the model.</summary>
        public Task<string> ExecuteAsync(
            [Description(
                "One of: list, get_source, load, reload, unload, export, import, forget, versions, revert, diagnostics")]
            string action,
            [Description(
                "Mod id (required for get_source, load, reload, unload, export, forget, versions, revert; optional filter for diagnostics)")]
            string mod_id = null,
            [Description(
                "Lua source for load/reload. Multi-line code is fully supported: write newlines as standard JSON \\n escapes inside this string (do not double-escape as \\\\n - that reaches Lua as a literal backslash and breaks parsing). Valid callbacks: hooks_on('event', function(name, payload) ... end); hooks_every(seconds, function() ... end). For import, the shareable bundle may be passed here if 'bundle' is omitted.")]
            string code = null,
            [Description("Shareable mod bundle JSON (as returned by export) for the import action.")]
            string bundle = null,
            [Description(
                "Revision index to roll back to for the revert action (as listed by versions; 0 is the original).")]
            int revision = -1,
            CancellationToken cancellationToken = default)
        {
            string normalized = (action ?? "").Trim().ToLowerInvariant();
            if (_settings.LogToolCalls)
            {
                _logger.Info($"[Tool Call] manage_mods: action={normalized} mod_id={mod_id}");
            }

            string result;
            try
            {
                ActorContext actor = _actorIdentityProvider.GetActorContext(_roleId);
                if (!actor.IsTrusted)
                {
                    throw new InvalidOperationException("Actor identity provider returned an untrusted context.");
                }

                lock (_authorizationGate)
                {
                    result = normalized switch
                    {
                        "list" => ListMods(),
                        "get_source" => ReadOwned(actor, normalized, mod_id, () => GetSource(mod_id)),
                        "load" => Mutate(() => Load(mod_id, code, actor)),
                        "reload" => MutateOwned(actor, normalized, mod_id, () => Reload(mod_id, code)),
                        "unload" => MutateOwned(actor, normalized, mod_id, () => Unload(mod_id)),
                        "export" => ReadOwned(actor, normalized, mod_id, () => Export(mod_id)),
                        "import" => Mutate(() => Import(bundle ?? code, actor)),
                        "forget" => MutateOwned(actor, normalized, mod_id, () => Forget(mod_id)),
                        "versions" => ReadOwned(actor, normalized, mod_id, () => Versions(mod_id)),
                        "revert" => MutateOwned(actor, normalized, mod_id, () => Revert(mod_id, revision)),
                        "diagnostics" => Diagnostics(mod_id, actor),
                        _ => Fail(
                            $"Unknown action '{normalized}'. Valid: list, get_source, load, reload, unload, export, import, forget, versions, revert, diagnostics.")
                    };
                }
            }
            catch (Exception ex)
            {
                result = Fail($"manage_mods '{normalized}' failed: {ex.Message}");
            }

            if (_settings.LogToolCallResults)
            {
                string preview = result.Length > 200 ? result.Substring(0, 200) : result;
                _logger.Info($"[Tool Call] manage_mods: {preview}");
            }

            return Task.FromResult(result);
        }

        private static object CreateAuthorizationGate(ILuaModRuntime runtime)
        {
            return new object();
        }

        private string Mutate(Func<string> action)
        {
            if (!_allowModManagement)
            {
                return Fail(
                    "Mod management is disabled for this agent (read-only). Allowed actions: list, get_source.");
            }

            return action();
        }

        private string MutateOwned(ActorContext actor, string actionName, string modId, Func<string> action)
        {
            return Mutate(() => ReadOwned(actor, actionName, modId, action));
        }

        private string ReadOwned(ActorContext actor, string actionName, string modId, Func<string> action)
        {
            string denial = OwnershipDenial(actor, actionName, modId);
            return denial ?? action();
        }

        private string OwnershipDenial(ActorContext actor, string actionName, string modId)
        {
            if (actor.Grants.IsUnrestricted || string.IsNullOrWhiteSpace(modId))
            {
                return null;
            }

            string normalizedModId = modId.Trim();
            string ownerActorId = _runtime.GetModOwnerActorId(normalizedModId);
            if (ownerActorId == null || string.Equals(ownerActorId, actor.ActorId, StringComparison.Ordinal))
            {
                return null;
            }

            string reason = ownerActorId.Length == 0
                ? "it is owned by the host/system"
                : $"it is owned by actor '{ownerActorId}'";
            return Fail(
                $"{actionName}: actor '{actor.ActorId}' is not authorized to access mod '{normalizedModId}' because {reason}.");
        }

        private string ListMods()
        {
            IReadOnlyList<LuaModInfo> mods = _runtime.ListMods();
            List<object> items = new(mods.Count);
            int quarantined = 0;
            foreach (LuaModInfo mod in mods)
            {
                if (mod.Quarantined)
                {
                    quarantined++;
                }

                items.Add(new
                {
                    id = mod.Id,
                    capabilities = mod.Capabilities.ToString(),
                    handlers = mod.HandlerCount,
                    timers = mod.TimerCount,
                    errors = mod.ErrorCount,
                    quarantined = mod.Quarantined,
                    log_reports = mod.LogReports,
                    loaded_at_utc = mod.LoadedAtUtc.ToString("O")
                });
            }

            string message = quarantined == 0
                ? $"{items.Count} mod(s) loaded."
                : $"{items.Count} mod(s) loaded; {quarantined} quarantined (still loaded, dispatch suspended " +
                  "after repeated errors - check diagnostics, fix the code, and reload to resume).";
            return Ok(message, items);
        }

        private string GetSource(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("get_source: mod_id is required.");
            }

            if (!_runtime.TryGetModSource(modId, out string source))
            {
                return Fail($"get_source: mod '{modId.Trim()}' is not loaded.");
            }

            if (source.Length > MaxSourceLengthReturned)
            {
                source = source.Substring(0, MaxSourceLengthReturned) + "\n--[[ ...(truncated) ]]";
            }

            return Ok($"Source of mod '{modId.Trim()}'.", source);
        }

        private string Load(string modId, string code, ActorContext actor)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(code))
            {
                return Fail("load: mod_id and code are required.");
            }

            string denial = OwnershipDenial(actor, "load", modId);
            if (denial != null)
            {
                return denial;
            }

            _runtime.LoadModForActor(modId, code, actor.ActorId, _grantedCapabilities);
            return Ok($"Mod '{modId.Trim()}' loaded (capabilities={_grantedCapabilities}).");
        }

        private string Reload(string modId, string code)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(code))
            {
                return Fail("reload: mod_id and code are required.");
            }

            _runtime.ReloadMod(modId, code);
            return Ok($"Mod '{modId.Trim()}' reloaded.");
        }

        private string Unload(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("unload: mod_id is required.");
            }

            return _runtime.UnloadMod(modId)
                ? Ok($"Mod '{modId.Trim()}' unloaded.")
                : Fail($"unload: mod '{modId.Trim()}' is not loaded.");
        }

        private string Export(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("export: mod_id is required.");
            }

            string bundle = _runtime.ExportMod(modId);
            if (bundle == null)
            {
                return Fail($"export: mod '{modId.Trim()}' is not loaded or stored.");
            }

            return Ok($"Bundle for mod '{modId.Trim()}'. Pass it to import on another player.", bundle);
        }

        private string Import(string bundle, ActorContext actor)
        {
            if (string.IsNullOrWhiteSpace(bundle))
            {
                return Fail("import: bundle (or code) with the shareable mod JSON is required.");
            }

            string modId;
            try
            {
                JObject parsed = JObject.Parse(bundle);
                modId = parsed["manifest"]?["id"]?.Value<string>();
            }
            catch (JsonException)
            {
                return Fail("import: failed to import bundle (invalid JSON, missing source, or load error).");
            }

            string denial = OwnershipDenial(actor, "import", modId);
            if (denial != null)
            {
                return denial;
            }

            return _runtime.ImportModForActor(bundle, actor.ActorId, _grantedCapabilities, false)
                ? Ok($"Mod imported and loaded (capabilities masked to {_grantedCapabilities}).")
                : Fail("import: failed to import bundle (invalid JSON, missing source, or load error).");
        }

        private string Forget(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("forget: mod_id is required.");
            }

            return _runtime.ForgetMod(modId)
                ? Ok($"Mod '{modId.Trim()}' forgotten (unloaded and deleted from storage).")
                : Fail($"forget: mod '{modId.Trim()}' is not loaded or stored.");
        }

        /// <summary>Max length of the per-revision source preview returned by <c>versions</c>.</summary>
        private const int RevisionPreviewLength = 240;

        /// <summary>Cap on the number of recent runtime errors returned by <c>diagnostics</c>.</summary>
        private const int MaxDiagnosticsReturned = 20;

        private string Versions(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("versions: mod_id is required.");
            }

            IReadOnlyList<LuaScriptRevision> history = _runtime.ListModVersions(modId);
            if (history.Count == 0)
            {
                return Fail(
                    $"versions: mod '{modId.Trim()}' has no recorded revisions (no version history is tracked).");
            }

            List<object> items = new(history.Count);
            foreach (LuaScriptRevision rev in history)
            {
                string source = rev.Source ?? "";
                string preview = source.Length > RevisionPreviewLength
                    ? source.Substring(0, RevisionPreviewLength) + "..."
                    : source;
                items.Add(new
                {
                    revision = rev.Index,
                    length = source.Length,
                    saved_at_utc = new DateTime(rev.UtcTicks, DateTimeKind.Utc).ToString("O"),
                    preview
                });
            }

            return Ok(
                $"{items.Count} revision(s) of mod '{modId.Trim()}' (0 = original, newest last). Use revert with a revision index to roll back.",
                items);
        }

        private string Revert(string modId, int revision)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("revert: mod_id is required.");
            }

            if (revision < 0)
            {
                return Fail("revert: a non-negative 'revision' index is required (see versions).");
            }

            bool reverted;
            string restored;
            try
            {
                reverted = _runtime.TryRevertMod(modId, revision, out restored);
            }
            catch (Exception ex)
            {
                // WHY: The restored revision failed to reload; the live mod is left untouched.
                return Fail($"revert: mod '{modId.Trim()}' revision {revision} failed to reload: {ex.Message}");
            }

            return reverted
                ? Ok($"Mod '{modId.Trim()}' reverted to revision {revision}.")
                : Fail($"revert: mod '{modId.Trim()}' has no revision {revision} (see versions).");
        }

        private string Diagnostics(string modId, ActorContext actor)
        {
            string denial = OwnershipDenial(actor, "diagnostics", modId);
            if (denial != null)
            {
                return denial;
            }

            IReadOnlyList<LuaModHandlerError> errors = _runtime.GetRecentHandlerErrors(modId);
            List<LuaModHandlerError> visible = new(errors.Count);
            foreach (LuaModHandlerError error in errors)
            {
                if (actor.Grants.IsUnrestricted ||
                    string.Equals(error.OwnerActorId, actor.ActorId, StringComparison.Ordinal))
                {
                    visible.Add(error);
                }
            }

            int total = visible.Count;
            List<object> items = new(Math.Min(total, MaxDiagnosticsReturned));

            // WHY: Return the newest entries (GetRecentHandlerErrors is oldest-first).
            int start = total > MaxDiagnosticsReturned ? total - MaxDiagnosticsReturned : 0;
            for (int i = start; i < total; i++)
            {
                LuaModHandlerError err = visible[i];
                items.Add(new
                {
                    mod_id = err.ModId,
                    error = err.Error,
                    consecutive_errors = err.ConsecutiveCount,
                    at_utc = err.AtUtc.ToString("O")
                });
            }

            string scope = string.IsNullOrWhiteSpace(modId) ? "all mods" : $"mod '{modId.Trim()}'";
            string message = total == 0
                ? $"No recent runtime handler errors for {scope}."
                : $"{total} recent runtime handler error(s) for {scope} (newest last). Fix the mod and reload it. " +
                  "A mod past its error threshold is quarantined (still loaded, dispatch suspended) - " +
                  "reload clears the quarantine.";
            return Ok(message, items);
        }

        private static string Ok(string message, object data = null)
        {
            return JsonConvert.SerializeObject(new { success = true, message, data });
        }

        private static string Fail(string message)
        {
            return JsonConvert.SerializeObject(new { success = false, message });
        }
    }
}
