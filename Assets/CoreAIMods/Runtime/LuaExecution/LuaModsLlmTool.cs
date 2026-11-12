using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.WorldPackages;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Folders;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool (<c>manage_mods</c>) that lets an agent inspect and rewrite its own persistent
    /// Lua mods in <c>LuaModRuntime</c>: list mods, read their source in pages of whole lines
    /// (<c>get_source</c> with <c>from_line</c>/<c>line_count</c>), change part of a mod by exact text
    /// replacement (<c>edit</c>), load, reload, unload, export, import, and forget them; list a mod's
    /// revision history and roll back to an earlier revision (<c>versions</c>/<c>revert</c>); and read
    /// recent runtime hook/timer failures (<c>diagnostics</c>) so a mod that throws long after it loaded
    /// can be repaired. Loaded/reloaded/edited mods auto-persist and survive a restart; export/import move
    /// a mod between players via a shareable bundle.
    /// <para>
    /// A <c>get_source</c> page is sized so its serialized result fits <see cref="ICoreAISettings.MaxToolResultChars"/>,
    /// the cut <c>ToolExecutionPolicy</c> applies to every tool result, so a mod of any size can be read whole by
    /// following <c>next_from_line</c>. <c>edit</c> runs the same reload as <c>reload</c>, with the same rules.
    /// </para>
    /// <para>
    /// Mutating actions (<c>edit</c>/<c>load</c>/<c>reload</c>/<c>unload</c>/<c>import</c>/<c>forget</c>/<c>revert</c>)
    /// effectively grant the model the
    /// whole capability tier configured at registration, so hosts that only want introspection
    /// must construct the tool with <c>allowModManagement: false</c>. Loaded mods always receive
    /// the host-configured <see cref="LuaCapabilities"/> tier — the model cannot request a wider
    /// one per call.
    /// </para>
    /// </summary>
    public sealed class LuaModsLlmTool : LlmToolBase, IAIFunctionLlmTool, ILlmToolCallEffects
    {
        /// <summary>
        /// Most source characters one <c>get_source</c> page returns. The page is also kept under
        /// <see cref="ICoreAISettings.MaxToolResultChars"/> as serialized JSON, which is usually the tighter limit.
        /// </summary>
        public const int MaxSourceLengthReturned = 16_000;

        /// <summary>Stable autosave trigger for loading a mod.</summary>
        public const string LoadBackupTrigger = "manage_mods-load";

        /// <summary>Stable autosave trigger for reloading a mod, and for <c>edit</c>, which is a reload of the edited source.</summary>
        public const string ReloadBackupTrigger = "manage_mods-reload";

        /// <summary>Stable autosave trigger for unloading a mod.</summary>
        public const string UnloadBackupTrigger = "manage_mods-unload";

        /// <summary>Stable autosave trigger for importing a mod.</summary>
        public const string ImportBackupTrigger = "manage_mods-import";

        /// <summary>Stable autosave trigger for forgetting a mod.</summary>
        public const string ForgetBackupTrigger = "manage_mods-forget";

        /// <summary>Stable autosave trigger for reverting a mod.</summary>
        public const string RevertBackupTrigger = "manage_mods-revert";

        /// <summary>Stable autosave trigger for moving a mod to another folder.</summary>
        public const string MoveBackupTrigger = "manage_mods-move";

        /// <summary>Stable autosave trigger for creating, renaming, moving or deleting a mod folder.</summary>
        public const string FolderBackupTrigger = "manage_mods-folder";

        private static readonly ConditionalWeakTable<ILuaModRuntime, object> AuthorizationGates = new();

        // WHY keyed by runtime like the authorization gate: every tool instance built over one runtime (one per
        // role) must see the same persisted folders when the host passes no folder list of its own.
        private static readonly ConditionalWeakTable<ILuaModRuntime, IModFolderListStore> DefaultFolderLists = new();

        private readonly ILuaModRuntime _runtime;
        private readonly ICoreAISettings _settings;
        private readonly ILog _logger;
        private readonly LuaCapabilities _grantedCapabilities;
        private readonly bool _allowModManagement;
        private readonly IActorIdentityProvider _actorIdentityProvider;
        private readonly object _authorizationGate;
        private readonly string _roleId;
        private readonly IConfirmedWorldMutationGate _worldMutationGate;
        private readonly LuaGenerationRateLimiter _rateLimiter;
        private readonly IModFolderListStore _folderList;

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
            : this(
                runtime,
                settings,
                logger,
                grantedCapabilities,
                allowModManagement,
                actorIdentityProvider,
                roleId,
                null)
        {
        }

        public LuaModsLlmTool(
            ILuaModRuntime runtime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities grantedCapabilities,
            bool allowModManagement,
            IActorIdentityProvider actorIdentityProvider,
            string roleId,
            IConfirmedWorldMutationGate worldMutationGate)
            : this(
                runtime,
                settings,
                logger,
                grantedCapabilities,
                allowModManagement,
                actorIdentityProvider,
                roleId,
                worldMutationGate,
                null)
        {
        }

        /// <summary>
        /// Creates the tool with a rate limit on its mutating actions. Pass the limiter
        /// <c>execute_lua</c> uses so both tools spend one budget of AI world changes; null leaves
        /// <c>manage_mods</c> unlimited.
        /// </summary>
        public LuaModsLlmTool(
            ILuaModRuntime runtime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities grantedCapabilities,
            bool allowModManagement,
            IActorIdentityProvider actorIdentityProvider,
            string roleId,
            IConfirmedWorldMutationGate worldMutationGate,
            LuaGenerationRateLimiter rateLimiter)
            : this(
                runtime,
                settings,
                logger,
                grantedCapabilities,
                allowModManagement,
                actorIdentityProvider,
                roleId,
                worldMutationGate,
                rateLimiter,
                null)
        {
        }

        /// <summary>
        /// Creates the tool with a rate limit and the store of persisted mod folders. Pass the folder list the
        /// Hub Mods page uses, so a folder the AI creates shows there too; null keeps one in-memory list per
        /// runtime.
        /// </summary>
        public LuaModsLlmTool(
            ILuaModRuntime runtime,
            ICoreAISettings settings,
            ILog logger,
            LuaCapabilities grantedCapabilities,
            bool allowModManagement,
            IActorIdentityProvider actorIdentityProvider,
            string roleId,
            IConfirmedWorldMutationGate worldMutationGate,
            LuaGenerationRateLimiter rateLimiter,
            IModFolderListStore folderList)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _grantedCapabilities = grantedCapabilities;
            _allowModManagement = allowModManagement;
            _actorIdentityProvider = actorIdentityProvider ??
                                     throw new ArgumentNullException(nameof(actorIdentityProvider));
            _authorizationGate = AuthorizationGates.GetValue(_runtime, CreateAuthorizationGate);
            _roleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Programmer : roleId.Trim();
            _worldMutationGate = worldMutationGate;
            _rateLimiter = rateLimiter;
            _folderList = folderList ?? DefaultFolderLists.GetValue(_runtime, _ => new InMemoryModFolderListStore());
        }

        /// <summary>The limiter the mutating actions spend; null when they are not limited.</summary>
        public LuaGenerationRateLimiter RateLimiter => _rateLimiter;

        /// <inheritdoc />
        public override string Name => "manage_mods";

        /// <inheritdoc />
        // WHY: Mutating mod actions (edit/load/reload/unload/import/forget/revert) are non-idempotent, so an
        // WHY: identical cross-turn echo must not re-run. AllowDuplicates=false lets ToolExecutionPolicy
        // WHY: suppress only a CROSS-TURN byte-identical echo (structured no-op) while still allowing
        // WHY: intra-turn repeats and never suppressing the retry of a FAILED call.
        public override bool AllowDuplicates => false;

        /// <inheritdoc />
        public override string Description =>
            "Manage persistent Lua mods (long-lived scripts with hooks_on/hooks_every handlers). " +
            "Actions: list (all loaded mods), get_source (read a mod's Lua in pages of whole lines; while " +
            "next_from_line is set, call again with from_line=next_from_line), " +
            "edit (replace old_text, copied exactly from get_source, with new_text, then reload; a failed edit " +
            "changes nothing), " +
            "load (install new mod), reload (replace a mod's WHOLE code keeping its permissions; by default the " +
            "objects the previous run's main chunk built are removed first so the new code builds into a clean " +
            "world, objects created later by handlers or players are kept, and a failed reload changes nothing; " +
            "pass keep_objects=true to keep every object and build next to them), " +
            "unload (remove a mod), export (get a shareable bundle of a mod to move it to another player), " +
            "import (install a mod from a shareable bundle, passed in the 'bundle' or 'code' param), " +
            "forget (unload a mod and delete it from persistent storage), " +
            "versions (list a mod's saved revisions; revision 0 is the original, newest last), " +
            "revert (roll a mod back to an earlier revision, passed in the 'revision' param), " +
            "diagnostics (read recent runtime errors thrown by mod hooks/timers so you can repair the mod). " +
            "Mods live in folders: a mod's folder is the 'category:' path in its @coreai header (category: Games/Arcade); " +
            "no category = top level. load with 'folder' puts the new mod in that folder; move (mod_id + folder) moves a " +
            "loaded mod without restarting it; create_folder, delete_folder (empty folders only), rename_folder (folder + " +
            "target = new name) and move_folder (folder + target = new parent) manage folders; list shows each mod's " +
            "folder and the folders, and with 'folder' only that branch. A refused folder call returns a code such as " +
            "FOLDER_NOT_EMPTY or INVALID_CHARACTER. " +
            "load/reload/edit auto-persist mods so they survive an app restart; export/import move mods between players. " +
            "Each load/reload/edit records a new revision; use versions then revert to undo a bad edit. " +
            "A hook or timer that starts throwing at runtime is reported via diagnostics (not as an error on the " +
            "call that loaded it) - poll diagnostics, then fix the mod with edit. " +
            "A mod that keeps failing is QUARANTINED, never auto-unloaded: it stays in list (quarantined=true) " +
            "with its handlers/timers suspended, and a successful reload or edit clears the quarantine and resumes it. " +
            "To change a mod: get_source, then edit; use reload only to replace a whole mod. " +
            "Lua callback syntax: hooks_on('event', function(name, payload) ... end) " +
            "and hooks_every(seconds, function() ... end). Do not write hooks_on('event') function() ... end. " +
            "Extra globals inside mods: hooks_on('tick', fn) per-frame alias; store_set(key, value)/store_get(key) " +
            "per-mod persistent strings; events_emit(name, payload) between mods; mods_export(name, valueOrFn), " +
            "mods_get(modId, name), mods_call(modId, fnName, ...), mods_list_exports(modId) for cross-mod " +
            "variables and function calls (by-value; nil/boolean/number/string/plain tables only); " +
            "input_key/input_key_down/input_key_up/input_mouse_button/input_mouse_x/input_mouse_y/input_axis " +
            "to read player input from timers.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("action", "string", true, ActionDescription),
            ("mod_id", "string", false, ModIdDescription),
            ("code", "string", false, CodeDescription),
            ("bundle", "string", false, BundleDescription),
            ("revision", "integer", false, RevisionDescription),
            ("keep_objects", "boolean", false,
                KeepObjectsDescription),
            ("folder", "string", false, FolderDescription),
            ("target", "string", false, TargetDescription),
            ("from_line", "integer", false, FromLineDescription),
            ("line_count", "integer", false, LineCountDescription),
            ("old_text", "string", false, OldTextDescription),
            ("new_text", "string", false, NewTextDescription),
            ("replace_all", "boolean", false, ReplaceAllDescription)
        );

        private const string ActionDescription =
            "One of: list, get_source, edit, load, reload, unload, export, import, forget, versions, revert, " +
            "diagnostics, move, create_folder, rename_folder, move_folder, delete_folder";

        private const string ModIdDescription =
            "Mod id (required for get_source, edit, load, reload, unload, export, forget, versions, revert; optional filter for diagnostics)";

        private const string CodeDescription =
            "Lua source for load/reload. Multi-line code is fully supported: write newlines as standard JSON \\n escapes inside this string (do not double-escape as \\\\n - that reaches Lua as a literal backslash and breaks parsing). Valid callbacks: hooks_on('event', function(name, payload) ... end); hooks_every(seconds, function() ... end). For import, the shareable bundle may be passed here if 'bundle' is omitted.";

        private const string BundleDescription =
            "Shareable mod bundle JSON (as returned by export) for the import action.";

        private const string RevisionDescription =
            "Revision index to roll back to for the revert action (as listed by versions; 0 is the original).";

        private const string FromLineDescription =
            "get_source: first line, 1-based; pass the previous page's next_from_line.";

        private const string LineCountDescription =
            "get_source: most lines (default: all that fit).";

        private const string OldTextDescription =
            "edit: exact text to replace, copied from get_source (whole lines, same indentation); must match once " +
            "unless replace_all.";

        private const string NewTextDescription =
            "edit: replacement for old_text (empty deletes it).";

        private const string ReplaceAllDescription =
            "edit: replace every occurrence of old_text.";

        private const string FolderDescription =
            "Mod folder path like Games/Arcade (letters, digits, spaces and _ - . ( ) & + ! ' , #; up to 8 levels of " +
            "64 characters). Empty = top level. load: the folder the new mod goes in (written into its header as " +
            "category). move: the destination. list: only this branch. create_folder, delete_folder, rename_folder, " +
            "move_folder: the folder acted on.";

        private const string TargetDescription =
            "rename_folder: the new name (one folder name, no '/'). move_folder: the new parent folder (empty = top level).";

        private const string KeepObjectsDescription =
            "reload and edit only. false (default): the objects the previous run's main chunk built are removed " +
            "before the new code runs. true: every object stays and the new code builds next to them.";

        /// <summary>Creates the MEAI function surface for <c>manage_mods</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string, string, string, int, CancellationToken, bool, string, string, int, int, string,
                string, bool, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Executes a mod-management action and returns a JSON result for the model.</summary>
        // WHY: MEAI awaits this task with ConfigureAwait(false) inside its binary. In the WebGL player
        // the result must be published with the host SynchronizationContext cleared, or the model turn
        // never resumes after an asynchronously completed body (see MeaiToolTaskBridge).
        public Task<string> ExecuteAsync(
            [Description(ActionDescription)]
            string action,
            [Description(ModIdDescription)]
            string mod_id = null,
            [Description(CodeDescription)]
            string code = null,
            [Description(BundleDescription)]
            string bundle = null,
            [Description(RevisionDescription)]
            int revision = -1,
            CancellationToken cancellationToken = default,
            [Description(KeepObjectsDescription)]
            bool keep_objects = false,
            [Description(FolderDescription)]
            string folder = null,
            [Description(TargetDescription)]
            string target = null,
            [Description(FromLineDescription)]
            int from_line = 0,
            [Description(LineCountDescription)]
            int line_count = 0,
            [Description(OldTextDescription)]
            string old_text = null,
            [Description(NewTextDescription)]
            string new_text = null,
            [Description(ReplaceAllDescription)]
            bool replace_all = false) =>
            MeaiToolTaskBridge.Publish(
                ExecuteBodyAsync(new ModsCall
                {
                    ModId = mod_id,
                    Code = code,
                    Bundle = bundle,
                    Revision = revision,
                    KeepObjects = keep_objects,
                    Folder = folder,
                    Target = target,
                    FromLine = from_line,
                    LineCount = line_count,
                    OldText = old_text,
                    NewText = new_text,
                    ReplaceAll = replace_all
                }, action, cancellationToken));

        /// <summary>The arguments of one call, past the action.</summary>
        private sealed class ModsCall
        {
            public string ModId;
            public string Code;
            public string Bundle;
            public int Revision;
            public bool KeepObjects;
            public string Folder;
            public string Target;
            public int FromLine;
            public int LineCount;
            public string OldText;
            public string NewText;
            public bool ReplaceAll;
        }

        private async Task<string> ExecuteBodyAsync(ModsCall call, string action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = (action ?? "").Trim().ToLowerInvariant();
            if (_settings.LogToolCalls)
            {
                _logger.Info($"[Tool Call] manage_mods: action={normalized} mod_id={call.ModId}");
            }

            string result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ActorContext actor = _actorIdentityProvider.GetActorContext(_roleId);
                if (!actor.IsTrusted)
                {
                    throw new InvalidOperationException("Actor identity provider returned an untrusted context.");
                }

                if (_allowModManagement
                    && _rateLimiter != null
                    && IsMutatingAction(normalized)
                    && !_rateLimiter.TryAcquire(out LuaRateLimitBudget budget))
                {
                    result = FailRateLimited(normalized, budget);
                }
                else if (TryGetConfirmedBackupTrigger(normalized, out string trigger))
                {
                    result = await _worldMutationGate.ExecuteAttributedAsync(
                        trigger,
                        actor.ActorId,
                        token =>
                        {
                            token.ThrowIfCancellationRequested();
                            cancellationToken.ThrowIfCancellationRequested();
                            return Task.FromResult(ExecuteAuthorizedAction(
                                normalized,
                                actor,
                                call,
                                cancellationToken));
                        },
                        cancellationToken);
                }
                else
                {
                    result = ExecuteAuthorizedAction(
                        normalized,
                        actor,
                        call,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
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

            return result;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Only <c>list</c>, <c>get_source</c>, <c>export</c>, <c>versions</c> and <c>diagnostics</c> read; every other
        /// action, an unknown one included, counts as a change. So a <c>get_source</c> repeated after an
        /// <c>edit</c> in the same request runs again instead of returning the page from before the edit.
        /// </remarks>
        public bool ChangesState(string functionName, IDictionary<string, object> arguments)
        {
            string action = LlmToolCallArguments.GetAction(arguments);
            return action != "list"
                   && action != "get_source"
                   && action != "export"
                   && action != "versions"
                   && action != "diagnostics";
        }

        private static bool IsMutatingAction(string normalizedAction)
        {
            return normalizedAction == "load"
                   || normalizedAction == "reload"
                   || normalizedAction == "edit"
                   || normalizedAction == "unload"
                   || normalizedAction == "import"
                   || normalizedAction == "forget"
                   || normalizedAction == "revert"
                   || normalizedAction == "move"
                   || normalizedAction == "create_folder"
                   || normalizedAction == "rename_folder"
                   || normalizedAction == "move_folder"
                   || normalizedAction == "delete_folder";
        }

        /// <summary>The refusal of a mutating action past the rate limit; it names when the next one is accepted.</summary>
        private static string FailRateLimited(string normalizedAction, LuaRateLimitBudget budget)
        {
            return JsonConvert.SerializeObject(new
            {
                success = false,
                status = "rate_limited",
                message = "manage_mods '" + normalizedAction + "' was NOT executed: " + budget.Describe()
                          + " Wait, then retry.",
                retry_after_seconds = Math.Round(budget.SecondsUntilNextSlot, 1)
            });
        }

        private bool TryGetConfirmedBackupTrigger(
            string normalizedAction,
            out string trigger)
        {
            // WHY: edit shares the reload trigger because an edit IS a reload of the edited source; the undo service
            // WHY: and the authorship tracker key their mod-source rules on the trigger, so a trigger of its own would
            // WHY: make an AI edit look like a non-AI change to both.
            trigger = normalizedAction switch
            {
                "load" => LoadBackupTrigger,
                "reload" => ReloadBackupTrigger,
                "edit" => ReloadBackupTrigger,
                "unload" => UnloadBackupTrigger,
                "import" => ImportBackupTrigger,
                "forget" => ForgetBackupTrigger,
                "revert" => RevertBackupTrigger,
                "move" => MoveBackupTrigger,
                "create_folder" => FolderBackupTrigger,
                "rename_folder" => FolderBackupTrigger,
                "move_folder" => FolderBackupTrigger,
                "delete_folder" => FolderBackupTrigger,
                _ => null
            };
            return _worldMutationGate != null
                   && _allowModManagement
                   && trigger != null;
        }

        private string ExecuteAuthorizedAction(
            string normalized,
            ActorContext actor,
            ModsCall call,
            CancellationToken cancellationToken)
        {
            string modId = call.ModId;
            string code = call.Code;
            string bundle = call.Bundle;
            int revision = call.Revision;
            bool keepObjects = call.KeepObjects;
            string folder = call.Folder;
            string target = call.Target;
            lock (_authorizationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return normalized switch
                {
                    "list" => ListMods(actor, folder),
                    "get_source" => GetSource(actor, modId, call.FromLine, call.LineCount),
                    "edit" => Mutate(() =>
                        Edit(actor, modId, call.OldText, call.NewText, call.ReplaceAll, keepObjects)),
                    "load" => Mutate(() => Load(modId, code, actor, folder)),
                    "reload" => Mutate(() => Reload(actor, modId, code, keepObjects)),
                    "unload" => Mutate(() => Unload(actor, modId)),
                    "export" => Export(actor, modId),
                    "import" => Mutate(() => Import(bundle ?? code, actor)),
                    "forget" => Mutate(() => Forget(actor, modId)),
                    "versions" => Versions(actor, modId),
                    "revert" => Mutate(() => Revert(actor, modId, revision)),
                    "diagnostics" => Diagnostics(modId, actor),
                    "move" => Mutate(() => MoveMod(actor, modId, folder)),
                    "create_folder" => Mutate(() => FolderOperation(actor, "create_folder", folder, null,
                        service => service.CreateFolder(folder))),
                    "rename_folder" => Mutate(() => FolderOperation(actor, "rename_folder", folder, target,
                        service => service.RenameFolder(folder, target))),
                    "move_folder" => Mutate(() => FolderOperation(actor, "move_folder", folder, "",
                        service => service.MoveFolder(folder, target ?? ""))),
                    "delete_folder" => Mutate(() => FolderOperation(actor, "delete_folder", folder, null,
                        service => service.DeleteFolder(folder))),
                    _ => Fail(
                        $"Unknown action '{normalized}'. Valid: list, get_source, edit, load, reload, unload, export, import, forget, versions, revert, diagnostics, move, create_folder, rename_folder, move_folder, delete_folder.")
                };
            }
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

        private string ListMods(ActorContext actor, string folder)
        {
            IReadOnlyList<LuaModInfo> mods = _runtime.ListMods(actor);
            ModFolderPath branch = ModFolderPath.FromStored(folder);
            ModFolderTree tree = ModFolderTree.Build(ToFolderEntries(mods), SafeLoadFolders(), new ModFolderFoldState());
            if (!branch.IsRoot && !tree.TryFindFolder(branch.Value, out _))
            {
                return Fail(ModFolderErrors.Code(ModFolderErrorCode.FolderNotFound) + ": list: no folder '" +
                            branch.Value + "' exists.");
            }

            List<object> items = new(mods.Count);
            int quarantined = 0;
            foreach (LuaModInfo mod in mods)
            {
                if (!ModFolderPath.FromStored(mod.Category).IsSameOrDescendantOf(branch))
                {
                    continue;
                }

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
                    loaded_at_utc = mod.LoadedAtUtc.ToString("O"),
                    folder = ModFolderPath.FromStored(mod.Category).Value,
                    context = mod.Context,
                    script_path = mod.ScriptPath,
                    run_state = mod.RunState
                });
            }

            List<object> folders = new();
            for (int i = 1; i < tree.FolderCount; i++)
            {
                ModFolderNode node = tree.GetFolder(i);
                if (node.Path.IsSameOrDescendantOf(branch))
                {
                    folders.Add(new { path = node.Path.Value, mods = node.ModCount });
                }
            }

            string scope = branch.IsRoot ? "" : $" in '{branch.Value}'";
            string message = quarantined == 0
                ? $"{items.Count} mod(s) loaded{scope}."
                : $"{items.Count} mod(s) loaded{scope}; {quarantined} quarantined (still loaded, dispatch suspended " +
                  "after repeated errors - check diagnostics, fix the code, and reload to resume).";
            return JsonConvert.SerializeObject(new { success = true, message, data = items, folders });
        }

        private static List<ModFolderModEntry> ToFolderEntries(IReadOnlyList<LuaModInfo> mods)
        {
            List<ModFolderModEntry> entries = new(mods.Count);
            foreach (LuaModInfo mod in mods)
            {
                entries.Add(new ModFolderModEntry(mod.Id, mod.Id, mod.Category, "", true));
            }

            return entries;
        }

        private IReadOnlyList<string> SafeLoadFolders()
        {
            try
            {
                return _folderList.Load();
            }
            catch (Exception ex)
            {
                _logger.Warn($"[manage_mods] The mod folder list could not be read: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private ModFolderService FolderService(ActorContext actor)
        {
            return new ModFolderService(new ToolModFolderStore(_runtime, actor, _folderList));
        }

        private string MoveMod(ActorContext actor, string modId, string folder)
        {
            if (string.IsNullOrWhiteSpace(modId) || folder == null)
            {
                return Fail("move: mod_id and folder are required (folder may be empty for the top level).");
            }

            return FolderResult("move", FolderService(actor).MoveMod(modId, folder));
        }

        private string FolderOperation(ActorContext actor, string action, string folder, string target,
            Func<ModFolderService, ModFolderResult> operation)
        {
            if (string.IsNullOrWhiteSpace(folder) || target == null && action == "rename_folder")
            {
                return Fail(action == "rename_folder"
                    ? "rename_folder: folder and target (the new name) are required."
                    : action + ": folder is required.");
            }

            return FolderResult(action, operation(FolderService(actor)));
        }

        private static string FolderResult(string action, ModFolderResult result)
        {
            if (!result.Succeeded)
            {
                return Fail(result.Code + ": " + action + ": " + result.Message);
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                message = result.Warning.Length == 0 ? result.Message : result.Message + " " + result.Warning,
                data = new
                {
                    path = result.Path.Value,
                    previous_path = result.PreviousPath.Value,
                    mods_changed = result.ModsChanged,
                    changed = result.Changed,
                    created_folder = result.CreatedFolder
                }
            });
        }

        /// <summary>
        /// One page of a mod's source: whole lines from <paramref name="fromLine"/>, as many as fit both
        /// <see cref="MaxSourceLengthReturned"/> and, serialized, <see cref="ICoreAISettings.MaxToolResultChars"/>,
        /// and at most <paramref name="lineCount"/> when it is positive. <c>data</c> is the text exactly as stored,
        /// so the pages of a source concatenate back to it; a line too long for any page is shown cut, with a marker.
        /// </summary>
        private string GetSource(ActorContext actor, string modId, int fromLine, int lineCount)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("get_source: mod_id is required.");
            }

            string id = modId.Trim();
            if (!_runtime.TryGetModSource(actor, modId, out string source))
            {
                return Fail($"get_source: mod '{id}' is not loaded.");
            }

            source ??= "";
            IReadOnlyList<int> starts = LuaModSourceEditor.LineStarts(source);
            int total = starts.Count;
            if (total == 0)
            {
                return Ok($"Source of mod '{id}' is empty.", "");
            }

            int first = fromLine < 1 ? 1 : fromLine;
            if (first > total)
            {
                return Fail($"get_source: from_line {first} is past the end of mod '{id}', which has {total} " +
                            $"lines; pass a from_line from 1 to {total}.");
            }

            int cap = _settings.MaxToolResultChars;
            int maxLines = lineCount > 0 ? lineCount : int.MaxValue;
            int textBudget = cap > 0
                ? cap - SourcePage(id, source.Length, total, total, total, total + 1, 0, 0, "").Length
                : int.MaxValue;
            int last = first - 1;
            int rawUsed = 0;
            int jsonUsed = 0;
            while (last < total && last - first + 1 < maxLines)
            {
                string line = LineText(source, starts, last + 1);
                int lineJson = JsonTextLength(line);
                if (rawUsed + line.Length > MaxSourceLengthReturned || jsonUsed + lineJson > textBudget)
                {
                    break;
                }

                rawUsed += line.Length;
                jsonUsed += lineJson;
                last++;
            }

            if (last < first)
            {
                return CutLinePage(id, source, starts, first, cap);
            }

            string page = WholeLinesPage(id, source, starts, first, last);
            while (cap > 0 && page.Length > cap && last > first)
            {
                last--;
                page = WholeLinesPage(id, source, starts, first, last);
            }

            return page;
        }

        private static string WholeLinesPage(string id, string source, IReadOnlyList<int> starts, int first, int last)
        {
            int total = starts.Count;
            int start = starts[first - 1];
            int end = last < total ? starts[last] : source.Length;
            return SourcePage(id, source.Length, first, last, total, last < total ? last + 1 : 0, 0, 0,
                source.Substring(start, end - start));
        }

        /// <summary>
        /// The page for a line that does not fit a page by itself: its longest prefix that fits, then a
        /// <c>--[[ …[+N chars] ]]</c> marker; the next page starts at the following line.
        /// </summary>
        private string CutLinePage(string id, string source, IReadOnlyList<int> starts, int lineNumber, int cap)
        {
            int total = starts.Count;
            string line = LineText(source, starts, lineNumber);
            int next = lineNumber < total ? lineNumber + 1 : 0;
            int budget = cap > 0
                ? cap - SourcePage(id, source.Length, lineNumber, lineNumber, total, next, lineNumber, line.Length, "")
                    .Length - JsonTextLength(CutMarker(line.Length))
                : int.MaxValue;
            int low = 0;
            int high = Math.Min(line.Length, MaxSourceLengthReturned);
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (JsonTextLength(line.Substring(0, middle)) <= budget)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            int shown = low > 0 && char.IsHighSurrogate(line[low - 1]) ? low - 1 : low;
            _logger.Info(
                $"[manage_mods] get_source '{id}': line {lineNumber} ({line.Length} chars) does not fit one page; " +
                $"{shown} chars returned, {line.Length - shown} dropped.");
            return SourcePage(id, source.Length, lineNumber, lineNumber, total, next, lineNumber, shown,
                line.Substring(0, shown) + CutMarker(line.Length - shown));
        }

        private static string CutMarker(int dropped)
        {
            return "\n--[[ " + TruncationMarker.Format(dropped) + " ]]";
        }

        private static string LineText(string source, IReadOnlyList<int> starts, int lineNumber)
        {
            int start = starts[lineNumber - 1];
            int end = lineNumber < starts.Count ? starts[lineNumber] : source.Length;
            return source.Substring(start, end - start);
        }

        /// <summary>The characters <paramref name="text"/> takes inside a serialized JSON string, quotes excluded.</summary>
        private static int JsonTextLength(string text)
        {
            return JsonConvert.ToString(text).Length - 2;
        }

        /// <summary>
        /// The serialized <c>get_source</c> result. The numbers come before <c>data</c>, so a reader that cuts the
        /// result still sees which lines it holds and where the next page starts.
        /// </summary>
        private static string SourcePage(string id, int totalChars, int first, int last, int total, int nextFromLine,
            int cutLine, int cutShown, string text)
        {
            StringBuilder message = new();
            message.Append("Source of mod '").Append(id).Append("', lines ").Append(first).Append('-').Append(last)
                .Append(" of ").Append(total).Append(" (").Append(totalChars).Append(" chars)");
            if (nextFromLine > 0)
            {
                message.Append(". NOT the whole source: call get_source with from_line=").Append(nextFromLine)
                    .Append(" for the next part.");
            }
            else
            {
                message.Append(first == 1 ? ", the whole source." : ", to the end.");
            }

            if (cutLine > 0)
            {
                message.Append(" Line ").Append(cutLine).Append(" is too long for one result: only its first ")
                    .Append(cutShown).Append(" chars are shown, so take old_text for edit from that part only.");
            }

            message.Append(" To change the mod call edit with old_text copied exactly from data.");
            JObject page = new()
            {
                ["success"] = true,
                ["message"] = message.ToString(),
                ["first_line"] = first,
                ["last_line"] = last,
                ["total_lines"] = total,
                ["total_chars"] = totalChars
            };
            if (nextFromLine > 0)
            {
                page["next_from_line"] = nextFromLine;
            }

            if (cutLine > 0)
            {
                page["cut_line"] = cutLine;
            }

            page["data"] = text;
            return page.ToString(Formatting.None);
        }

        private string Load(string modId, string code, ActorContext actor, string folder)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(code))
            {
                return Fail("load: mod_id and code are required.");
            }

            // WHY blank reads as no folder here (move takes empty as the top level): a caller that forwards an
            // absent argument as an empty string must not clear the category the mod's own header declares.
            string requested = string.IsNullOrWhiteSpace(folder) ? null : folder;
            if (requested == null && ModFolderHeaderEditor.ReadCategory(code).Length == 0)
            {
                requested = DefaultFolder();
            }

            string source = code;
            string placed = "";
            if (requested != null)
            {
                ModFolderResult resolved = FolderService(actor).ResolveFolder(requested);
                if (!resolved.Succeeded)
                {
                    return Fail(resolved.Code + ": load: " + resolved.Message);
                }

                ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(code, resolved.Path.Value);
                if (!edit.Succeeded)
                {
                    return Fail(ModFolderErrors.Code(edit.Error) + ": load: " + edit.Message);
                }

                source = edit.Source;
                placed = resolved.Path.IsRoot ? "" : $" in folder '{resolved.Path.Value}'";
            }

            _runtime.LoadMod(actor, modId, source, _grantedCapabilities);
            return Ok($"Mod '{modId.Trim()}' loaded (capabilities={_grantedCapabilities}){placed}.");
        }

        /// <summary>
        /// The configured folder for new AI mods (<see cref="ICoreAISettings.AiModsDefaultFolder"/>), or null
        /// for the top level, which leaves the source untouched. A value the folder rules refuse is logged and
        /// treated as the top level.
        /// </summary>
        private string DefaultFolder()
        {
            string configured = _settings.AiModsDefaultFolder;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            ModFolderPathParse parse = ModFolderPath.Parse(configured);
            if (!parse.Succeeded)
            {
                _logger.Warn($"[manage_mods] AiModsDefaultFolder '{configured}' is not a valid folder path " +
                             $"({ModFolderErrors.Code(parse.Error)}); new mods stay at the top level.");
                return null;
            }

            return parse.Path.Value;
        }

        private string Reload(ActorContext actor, string modId, string code, bool keepObjects)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(code))
            {
                return Fail("reload: mod_id and code are required.");
            }

            ModReloadReport report = ReloadSource(actor, modId, code, keepObjects);

            return report == null
                ? Ok($"Mod '{modId.Trim()}' reloaded.")
                : Ok($"Mod '{modId.Trim()}' reloaded; {report.Describe()}.", new
                {
                    mode = ReloadModeName(report),
                    cleaned_objects = report.CleanedObjects,
                    kept_objects = report.KeptObjects,
                    rescued_objects = report.RescuedObjects
                });
        }

        /// <summary>The one reload <c>reload</c> and <c>edit</c> share; a failure throws and leaves the mod as it was.</summary>
        private ModReloadReport ReloadSource(ActorContext actor, string modId, string code, bool keepObjects)
        {
            return _runtime.ReloadMod(
                actor, modId, code, keepObjects ? ModReloadMode.KeepObjects : ModReloadMode.CleanStartupObjects);
        }

        private static string ReloadModeName(ModReloadReport report)
        {
            return report.Mode == ModReloadMode.KeepObjects ? "keep_objects" : "clean_startup_objects";
        }

        /// <summary>
        /// Replaces <paramref name="oldText"/> with <paramref name="newText"/> in the mod's current source and reloads
        /// the result through <see cref="ReloadSource"/>, so authorization, the rate limit, the backup trigger, the
        /// revision, persistence and <c>keep_objects</c> are those of <c>reload</c>. Every refusal changes nothing and
        /// says what to send next.
        /// </summary>
        private string Edit(ActorContext actor, string modId, string oldText, string newText, bool replaceAll,
            bool keepObjects)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("edit: mod_id is required.");
            }

            string id = modId.Trim();
            if (string.IsNullOrEmpty(oldText))
            {
                return Fail("edit: old_text is empty. Copy the exact lines to change from get_source data, " +
                            "indentation included, into old_text, and their replacement into new_text.");
            }

            if (newText == null)
            {
                return Fail("edit: new_text is required (pass an empty string to delete old_text).");
            }

            if (LuaModSourceEditor.IsSameText(oldText, newText))
            {
                return Fail("edit: old_text and new_text are the same, so there is nothing to change. Put the " +
                            "changed text in new_text.");
            }

            if (!_runtime.TryGetModSource(actor, modId, out string source))
            {
                return Fail($"edit: mod '{id}' is not loaded.");
            }

            LuaModSourceEdit edit = LuaModSourceEditor.Replace(source, oldText, newText, replaceAll);
            switch (edit.Status)
            {
                case LuaModSourceEditStatus.NotFound:
                    return Fail($"edit: old_text was not found in mod '{id}' ({edit.TotalLines} lines); nothing " +
                                "changed. It must match the current source exactly, spaces and indentation " +
                                "included." + (edit.Lines.Count > 0
                                    ? $" Its first line appears at line(s) {JoinLines(edit.Lines)}: call get_source " +
                                      $"with from_line={edit.Lines[0]} and copy old_text from there."
                                    : " Call get_source to read the current text and copy old_text from it."));
                case LuaModSourceEditStatus.Ambiguous:
                    return Fail($"edit: old_text occurs {edit.Occurrences} times in mod '{id}' (at line(s) " +
                                $"{JoinLines(edit.Lines)}); nothing changed. Add surrounding lines to old_text so " +
                                "it matches once, or pass replace_all=true to change every occurrence.");
                case LuaModSourceEditStatus.NoChange:
                    return Fail("edit: old_text and new_text are the same, so there is nothing to change.");
            }

            ModReloadReport report;
            try
            {
                report = ReloadSource(actor, modId, edit.Source, keepObjects);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail($"edit: the edited source of mod '{id}' failed to load, so nothing changed and the mod " +
                            $"keeps running its previous source: {ex.Message} Fix new_text and call edit again.");
            }

            int revision = LatestRevision(actor, modId);
            string lines = edit.FirstLine == edit.LastLine
                ? "line " + edit.FirstLine
                : "lines " + edit.FirstLine + "-" + edit.LastLine;
            string message = $"Mod '{id}' edited and reloaded: " +
                             (edit.Occurrences == 1 ? "" : edit.Occurrences + " replacements, ") +
                             $"now {lines} of {edit.TotalLines}" +
                             (revision >= 0 ? $", revision {revision}" : "") +
                             (edit.ReadCrlfAsLf ? " (old_text matched once CRLF line endings were read as LF)" : "") +
                             (report == null ? "." : $"; {report.Describe()}.");
            return Ok(message, new
            {
                replacements = edit.Occurrences,
                first_line = edit.FirstLine,
                last_line = edit.LastLine,
                total_lines = edit.TotalLines,
                revision = revision >= 0 ? (int?)revision : null,
                mode = report == null ? null : ReloadModeName(report),
                cleaned_objects = report?.CleanedObjects,
                kept_objects = report?.KeptObjects,
                rescued_objects = report?.RescuedObjects
            });
        }

        /// <summary>The index of the mod's newest recorded revision, or -1 when no history is kept.</summary>
        private int LatestRevision(ActorContext actor, string modId)
        {
            // WHY: caught because the edit has already been applied; a history that cannot be read must not turn
            // WHY: its result into a failure the model would retry.
            try
            {
                IReadOnlyList<LuaScriptRevision> history = _runtime.ListModVersions(actor, modId);
                return history == null || history.Count == 0 ? -1 : history[history.Count - 1].Index;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[manage_mods] edit: the revision history of '{modId.Trim()}' could not be read: {ex.Message}");
                return -1;
            }
        }

        private static string JoinLines(IReadOnlyList<int> lines)
        {
            StringBuilder text = new();
            for (int index = 0; index < lines.Count; index++)
            {
                if (index > 0)
                {
                    text.Append(", ");
                }

                text.Append(lines[index]);
            }

            return text.ToString();
        }

        private string Unload(ActorContext actor, string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("unload: mod_id is required.");
            }

            return _runtime.UnloadMod(actor, modId)
                ? Ok($"Mod '{modId.Trim()}' unloaded.")
                : Fail($"unload: mod '{modId.Trim()}' is not loaded.");
        }

        private string Export(ActorContext actor, string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("export: mod_id is required.");
            }

            string bundle = _runtime.ExportMod(actor, modId);
            if (bundle == null)
            {
                return Fail($"export: mod '{modId.Trim()}' is not loaded or stored.");
            }

            string result = Ok($"Bundle for mod '{modId.Trim()}'. Pass it to import on another player.", bundle);
            int cap = _settings.MaxToolResultChars;
            return cap > 0 && result.Length > cap
                ? Ok($"Bundle for mod '{modId.Trim()}'. It is longer than one tool result ({result.Length} > {cap} " +
                     "chars) and reaches you cut: a cut bundle cannot be imported. To change this mod use get_source " +
                     "and edit instead.", bundle)
                : result;
        }

        private string Import(string bundle, ActorContext actor)
        {
            if (string.IsNullOrWhiteSpace(bundle))
            {
                return Fail("import: bundle (or code) with the shareable mod JSON is required.");
            }

            return _runtime.ImportMod(actor, bundle, _grantedCapabilities, false)
                ? Ok($"Mod imported and loaded (capabilities masked to {_grantedCapabilities}).")
                : Fail("import: failed to import bundle (invalid JSON, missing source, or load error).");
        }

        private string Forget(ActorContext actor, string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("forget: mod_id is required.");
            }

            return _runtime.ForgetMod(actor, modId)
                ? Ok($"Mod '{modId.Trim()}' forgotten (unloaded and deleted from storage).")
                : Fail($"forget: mod '{modId.Trim()}' is not loaded or stored.");
        }

        /// <summary>Max length of the per-revision source preview returned by <c>versions</c>.</summary>
        private const int RevisionPreviewLength = 240;

        /// <summary>Cap on the number of recent runtime errors returned by <c>diagnostics</c>.</summary>
        private const int MaxDiagnosticsReturned = 20;

        private string Versions(ActorContext actor, string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("versions: mod_id is required.");
            }

            IReadOnlyList<LuaScriptRevision> history = _runtime.ListModVersions(actor, modId);
            if (history.Count == 0)
            {
                return Fail(
                    $"versions: mod '{modId.Trim()}' has no recorded revisions (no version history is tracked).");
            }

            List<object> items = new(history.Count);
            foreach (LuaScriptRevision rev in history)
            {
                string source = rev.Source ?? "";
                string preview = TruncationMarker.ClipPrefix(source, RevisionPreviewLength, out _);
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

        private string Revert(ActorContext actor, string modId, int revision)
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
                reverted = _runtime.TryRevertMod(actor, modId, revision, out restored);
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
            IReadOnlyList<LuaModHandlerError> errors = _runtime.GetRecentHandlerErrors(actor, modId);
            int total = errors.Count;
            List<object> items = new(Math.Min(total, MaxDiagnosticsReturned));

            // WHY: Return the newest entries (GetRecentHandlerErrors is oldest-first).
            int start = total > MaxDiagnosticsReturned ? total - MaxDiagnosticsReturned : 0;
            for (int i = start; i < total; i++)
            {
                LuaModHandlerError err = errors[i];
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

        /// <summary>
        /// The folder operations' view of the runtime for one caller: the loaded mods it lists, moves through
        /// <see cref="ILuaModSourceRetitler"/> (the mod keeps running), off through unload. It cannot switch a
        /// dormant mod on, because the tool does not list dormant mods.
        /// </summary>
        private sealed class ToolModFolderStore : IModFolderStore
        {
            private readonly ILuaModRuntime _runtime;
            private readonly ActorContext _actor;
            private readonly IModFolderListStore _folders;

            public ToolModFolderStore(ILuaModRuntime runtime, ActorContext actor, IModFolderListStore folders)
            {
                _runtime = runtime;
                _actor = actor;
                _folders = folders;
            }

            public IReadOnlyList<ModFolderModEntry> ListMods()
            {
                return ToFolderEntries(_runtime.ListMods(_actor));
            }

            public IReadOnlyList<string> LoadFolders()
            {
                return _folders.Load();
            }

            public void SaveFolders(IReadOnlyList<string> folders)
            {
                _folders.Save(folders);
            }

            public void SetModFolder(string modId, string folderPath)
            {
                if (!_runtime.TryGetModSource(_actor, modId, out string source))
                {
                    throw new InvalidOperationException($"mod '{modId}' is not loaded.");
                }

                ModFolderHeaderEdit edit = ModFolderHeaderEditor.SetCategory(source, folderPath);
                if (!edit.Succeeded)
                {
                    throw new ArgumentException(ModFolderErrors.Code(edit.Error) + ": " + edit.Message);
                }

                if (string.Equals(edit.Source, source, StringComparison.Ordinal))
                {
                    return;
                }

                if (!(_runtime is ILuaModSourceRetitler retitler) ||
                    !retitler.TryRetitleModSource(_actor, modId, edit.Source))
                {
                    throw new InvalidOperationException($"this mod runtime cannot move the running mod '{modId}'.");
                }
            }

            public void SetModEnabled(string modId, bool enabled)
            {
                if (enabled)
                {
                    throw new NotSupportedException("manage_mods cannot switch a stored mod on; load it instead.");
                }

                _runtime.UnloadMod(_actor, modId);
            }
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
