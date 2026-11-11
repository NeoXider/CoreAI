using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.WorldPackages;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Folders;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

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
    /// Mutating actions (<c>load</c>/<c>reload</c>/<c>unload</c>/<c>import</c>/<c>forget</c>/<c>revert</c>)
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

        /// <summary>Stable autosave trigger for loading a mod.</summary>
        public const string LoadBackupTrigger = "manage_mods-load";

        /// <summary>Stable autosave trigger for reloading a mod.</summary>
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
        // WHY: Mutating mod actions (load/reload/unload/import/forget/revert) are non-idempotent, so an
        // WHY: identical cross-turn echo must not re-run. AllowDuplicates=false lets ToolExecutionPolicy
        // WHY: suppress only a CROSS-TURN byte-identical echo (structured no-op) while still allowing
        // WHY: intra-turn repeats and never suppressing the retry of a FAILED call.
        public override bool AllowDuplicates => false;

        /// <inheritdoc />
        public override string Description =>
            "Manage persistent Lua mods (long-lived scripts with hooks_on/hooks_every handlers). " +
            "Actions: list (all loaded mods), get_source (read a mod's Lua code), " +
            "load (install new mod), reload (replace a mod's code keeping its permissions; by default the " +
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
            "load/reload auto-persist mods so they survive an app restart; export/import move mods between players. " +
            "Each load/reload records a new revision; use versions then revert to undo a bad edit. " +
            "A hook or timer that starts throwing at runtime is reported via diagnostics (not as an error on the " +
            "call that loaded it) - poll diagnostics, fix the mod, and reload it. " +
            "A mod that keeps failing is QUARANTINED, never auto-unloaded: it stays in list (quarantined=true) " +
            "with its handlers/timers suspended, and a successful reload clears the quarantine and resumes it. " +
            "Use get_source before reload to edit existing behavior. " +
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
            ("mod_id", "string", false,
                "Mod id (required for get_source, load, reload, unload, export, forget, versions, revert; optional filter for diagnostics)"),
            ("code", "string", false,
                "Lua source for load/reload. Multi-line code is fully supported: write newlines as standard JSON \\n escapes inside this string (do not double-escape as \\\\n - that reaches Lua as a literal backslash and breaks parsing). Valid callbacks: hooks_on('event', function(name, payload) ... end); hooks_every(seconds, function() ... end). For import, the shareable bundle may be passed here if 'bundle' is omitted."),
            ("bundle", "string", false,
                "Shareable mod bundle JSON (as returned by export) for the import action."),
            ("revision", "integer", false,
                "Revision index to roll back to for the revert action (as listed by versions; 0 is the original)."),
            ("keep_objects", "boolean", false,
                KeepObjectsDescription),
            ("folder", "string", false, FolderDescription),
            ("target", "string", false, TargetDescription)
        );

        private const string ActionDescription =
            "One of: list, get_source, load, reload, unload, export, import, forget, versions, revert, diagnostics, " +
            "move, create_folder, rename_folder, move_folder, delete_folder";

        private const string FolderDescription =
            "Mod folder path like Games/Arcade (letters, digits, spaces and _ - . ( ) & + ! ' , #; up to 8 levels of " +
            "64 characters). Empty = top level. load: the folder the new mod goes in (written into its header as " +
            "category). move: the destination. list: only this branch. create_folder, delete_folder, rename_folder, " +
            "move_folder: the folder acted on.";

        private const string TargetDescription =
            "rename_folder: the new name (one folder name, no '/'). move_folder: the new parent folder (empty = top level).";

        private const string KeepObjectsDescription =
            "reload only. false (default): the objects the previous run's main chunk built are removed before " +
            "the new code runs. true: every object stays and the new code builds next to them.";

        /// <summary>Creates the MEAI function surface for <c>manage_mods</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string, string, string, int, CancellationToken, bool, string, string, Task<string>> func =
                ExecuteAsync;
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
            CancellationToken cancellationToken = default,
            [Description(KeepObjectsDescription)]
            bool keep_objects = false,
            [Description(FolderDescription)]
            string folder = null,
            [Description(TargetDescription)]
            string target = null) =>
            MeaiToolTaskBridge.Publish(
                ExecuteBodyAsync(action, mod_id, code, bundle, revision, keep_objects, folder, target,
                    cancellationToken));

        private async Task<string> ExecuteBodyAsync(string action, string mod_id, string code, string bundle,
            int revision, bool keepObjects, string folder, string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = (action ?? "").Trim().ToLowerInvariant();
            if (_settings.LogToolCalls)
            {
                _logger.Info($"[Tool Call] manage_mods: action={normalized} mod_id={mod_id}");
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
                                mod_id,
                                code,
                                bundle,
                                revision,
                                keepObjects,
                                folder,
                                target,
                                cancellationToken));
                        },
                        cancellationToken);
                }
                else
                {
                    result = ExecuteAuthorizedAction(
                        normalized,
                        actor,
                        mod_id,
                        code,
                        bundle,
                        revision,
                        keepObjects,
                        folder,
                        target,
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

        private static bool IsMutatingAction(string normalizedAction)
        {
            return normalizedAction == "load"
                   || normalizedAction == "reload"
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
            trigger = normalizedAction switch
            {
                "load" => LoadBackupTrigger,
                "reload" => ReloadBackupTrigger,
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
            string modId,
            string code,
            string bundle,
            int revision,
            bool keepObjects,
            string folder,
            string target,
            CancellationToken cancellationToken)
        {
            lock (_authorizationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return normalized switch
                {
                    "list" => ListMods(actor, folder),
                    "get_source" => GetSource(actor, modId),
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
                        $"Unknown action '{normalized}'. Valid: list, get_source, load, reload, unload, export, import, forget, versions, revert, diagnostics, move, create_folder, rename_folder, move_folder, delete_folder.")
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

        private string GetSource(ActorContext actor, string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return Fail("get_source: mod_id is required.");
            }

            if (!_runtime.TryGetModSource(actor, modId, out string source))
            {
                return Fail($"get_source: mod '{modId.Trim()}' is not loaded.");
            }

            if (source.Length > MaxSourceLengthReturned)
            {
                int cut = char.IsHighSurrogate(source[MaxSourceLengthReturned - 1])
                    ? MaxSourceLengthReturned - 1
                    : MaxSourceLengthReturned;
                int dropped = source.Length - cut;
                _logger.Info(
                    $"[manage_mods] get_source '{modId.Trim()}': {source.Length} chars total -> {cut} returned, " +
                    $"{dropped} dropped (limit {MaxSourceLengthReturned}).");
                source = source.Substring(0, cut) + "\n--[[ " + TruncationMarker.Format(dropped) + " ]]";
            }

            return Ok($"Source of mod '{modId.Trim()}'.", source);
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

            ModReloadReport report = _runtime.ReloadMod(
                actor, modId, code, keepObjects ? ModReloadMode.KeepObjects : ModReloadMode.CleanStartupObjects);

            return report == null
                ? Ok($"Mod '{modId.Trim()}' reloaded.")
                : Ok($"Mod '{modId.Trim()}' reloaded; {report.Describe()}.", new
                {
                    mode = report.Mode == ModReloadMode.KeepObjects ? "keep_objects" : "clean_startup_objects",
                    cleaned_objects = report.CleanedObjects,
                    kept_objects = report.KeptObjects,
                    rescued_objects = report.RescuedObjects
                });
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

            return Ok($"Bundle for mod '{modId.Trim()}'. Pass it to import on another player.", bundle);
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
