using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if COREAI_LUA
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Presentation;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// Scene-level host policy for the LiveMechanics mods-chat demo.
    /// LuaModRuntime intentionally does not autoload arbitrary source by itself; this component
    /// saves mods loaded by the chat scene and reloads them on the next scene start.
    /// </summary>
    public sealed class LiveMechanicsModsChatPersistenceController : MonoBehaviour
    {
#if COREAI_LUA
        private const string DefaultModKeyPrefix = "demo.live_mechanics.mods_chat.mod.";
        private const string ActiveFlagSegment = "__active__.";

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [SerializeField]
        private string modKeyPrefix = DefaultModKeyPrefix;

        [SerializeField]
        private string panelTitle = "Lua Mod Manager";

        // F9 toggles the mod manager; F10 is reserved for the Token Budget / usage overlay.
        [Tooltip("Hotkey that toggles the mod manager. Set to None to disable keyboard toggling.")]
        [SerializeField]
        private KeyCode toggleKey = KeyCode.F9;

        [SerializeField]
        private bool showPanel = true;

        [Tooltip("Saved ids that are validation artifacts and must not autoload in the playable demo.")]
        [SerializeField]
        private string[] transientModIds = { "auto_repair_smoke" };


        private ILuaModRuntime _mods;
        private ActorContext _actorContext;
        private ILuaScriptVersionStore _versions;
        private CoreAiLuaModAutoRepair _autoRepair;
        private string _status = "Waiting for CoreAI scope.";
        private int _autoloadedCount;
        private bool _isAutoloading;

        // Cached mod lists. Rebuilt only on ModSourceLoaded/ModSourceUnloaded and after user actions
        // in this panel (Activate/Deactivate/Forget/Save) — never while the panel is being built. See
        // Docs/coreai-mod-system.md §6.
        private List<LuaModInfo> _cachedActiveMods = new();
        private List<ModDescriptor> _cachedActiveDescriptors = new();
        private List<ModDescriptor> _cachedInactiveMods = new();
        private int _cachedActiveCount;
        private int _cachedInactiveCount;

        // Mod source editor state. Non-null _editModId means the editor window is open; the buffer
        // is a private copy, so closing without saving changes nothing anywhere.
        private CoreAI.Demos.Shared.CoreAiDemoPanel _panel;
        private TMPro.TMP_InputField _editor;
        private bool _editorLoaded;
        private string _editModId;
        private string _editBuffer = "";
        private string _editError = "";
        private Rect _editRect = new(120, 60, 620, 480);

        private readonly struct ModDescriptor
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Description;
            public readonly string Source;

            public ModDescriptor(string id, string source)
            {
                Id = id ?? "";
                Source = source ?? "";
                Name = ReadMetadata(Source, "name") ?? Id;
                Description = ReadMetadata(Source, "description") ?? "No description metadata.";
            }
        }

        /// <summary>Programmatic open/close of the mod manager panel (same effect as the hotkey).</summary>
        public bool PanelVisible
        {
            get => showPanel;
            set
            {
                showPanel = value;
                ApplyPanelVisibility();
            }
        }

        /// <summary>Toggle hotkey; <see cref="KeyCode.None"/> disables keyboard toggling.</summary>
        public KeyCode ToggleKey
        {
            get => toggleKey;
            set => toggleKey = value;
        }

        public void Configure(string keyPrefix, string title, Rect rect, bool visible)
        {
            if (!string.IsNullOrWhiteSpace(keyPrefix))
            {
                modKeyPrefix = keyPrefix;
            }

            panelTitle = string.IsNullOrWhiteSpace(title) ? panelTitle : title;
            // WHY the rect is ignored now: the shared panel anchors itself to the screen, so a
            // caller-supplied rectangle would be a setting that changes nothing. The parameter stays
            // so existing scenes and callers still compile.
            showPanel = visible;
            ApplyPanelVisibility();
        }

        private void Update()
        {
            if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
            {
                showPanel = !showPanel;
                ApplyPanelVisibility();
            }
        }

        private void ApplyPanelVisibility()
        {
            if (_panel != null)
            {
                _panel.gameObject.SetActive(showPanel);
            }
        }

        private IEnumerator Start()
        {
            _panel = CoreAI.Demos.Shared.CoreAiDemoPanel.Create(
                panelTitle,
                "Mods the chat loaded, and the ones saved on disk. Edit, activate, forget.");
            ApplyPanelVisibility();

            // LiveMechanicsDemoController declares its logic slots in Start. Wait one frame so
            // saved mods that call logic_define can bind to those slots reliably.
            yield return null;

            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found; mod autoload disabled.";
                Debug.LogError($"[LiveMechanicsModsChatDemo] {_status}");
                enabled = false;
                yield break;
            }

            IObjectResolver luaContainer = CoreAiDemoScope.ResolveModsContainer(coreAiScope);

            IActorIdentityProvider actorIdentityProvider = luaContainer.Resolve<IActorIdentityProvider>();
            _actorContext = actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer);
            _mods = luaContainer.Resolve<ILuaModRuntime>();
            _versions = luaContainer.Resolve<ILuaScriptVersionStore>();
            _autoRepair = FindFirstObjectByType<CoreAiLuaModAutoRepair>();
            _mods.AddModSourceLoadedListener(_actorContext, OnModSourceLoaded);
            _mods.AddModSourceUnloadedListener(_actorContext, OnModSourceUnloaded);

            AutoloadSavedMods();
            RecomputeModLists();
            _status = _autoloadedCount == 0
                ? "Mods-chat persistence ready. Load mods with manage_mods from the chat."
                : $"Mods-chat persistence ready. Autoloaded {_autoloadedCount} saved mod(s).";
        }

        private void OnDestroy()
        {
            if (_mods == null)
            {
                return;
            }

            _mods.RemoveModSourceLoadedListener(_actorContext, OnModSourceLoaded);
            _mods.RemoveModSourceUnloadedListener(_actorContext, OnModSourceUnloaded);
        }

        private void AutoloadSavedMods()
        {
            if (_versions == null || _mods == null)
            {
                return;
            }

            _isAutoloading = true;
            try
            {
                foreach (string key in _versions.GetKnownKeys())
                {
                    if (string.IsNullOrEmpty(key) ||
                        !key.StartsWith(modKeyPrefix, System.StringComparison.Ordinal) ||
                        IsActiveFlagKey(key))
                    {
                        continue;
                    }

                    string modId = key.Substring(modKeyPrefix.Length);
                    if (modId.Length == 0 ||
                        !_versions.TryGetSnapshot(key, out LuaScriptVersionRecord record) ||
                        string.IsNullOrWhiteSpace(record.CurrentLua))
                    {
                        continue;
                    }

                    if (IsTransientModId(modId))
                    {
                        ForgetSavedMod(modId, false);
                        Debug.Log($"[LiveMechanicsModsChatDemo] Ignored transient saved Lua mod '{modId}'.");
                        continue;
                    }

                    if (!ShouldAutoload(modId))
                    {
                        continue;
                    }

                    try
                    {
                        if (_mods.IsLoaded(_actorContext, modId))
                        {
                            _mods.ReloadMod(_actorContext, modId, record.CurrentLua);
                        }
                        else
                        {
                            // Grant the same tier the host composition grants: a Full-tier mod
                            // (unity_* APIs) autoloaded with a hardcoded All silently no-ops.
                            LuaCapabilities autoloadCaps = coreAiScope != null && coreAiScope.FullLuaAccessEnabled
                                ? LuaCapabilities.All | LuaCapabilities.Full
                                : LuaCapabilities.All;
                            _mods.LoadMod(_actorContext, modId, record.CurrentLua, autoloadCaps);
                        }

                        _autoloadedCount++;
                        Debug.Log($"[LiveMechanicsModsChatDemo] Autoloaded saved Lua mod '{modId}'.");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning(
                            $"[LiveMechanicsModsChatDemo] Saved Lua mod '{modId}' failed to autoload: {ex.Message}");
                    }
                }
            }
            finally
            {
                _isAutoloading = false;
            }
        }

        private void OnModSourceLoaded(string modId, string source, LuaCapabilities capabilities)
        {
            // Autoloading recomputes once, after the whole batch, in Start(); recomputing per mod
            // here would be O(n^2) over the saved-mod count at scene start.
            if (!_isAutoloading)
            {
                RecomputeModLists();
            }

            if (_isAutoloading || _versions == null || string.IsNullOrWhiteSpace(modId))
            {
                return;
            }

            if (IsTransientModId(modId))
            {
                ForgetSavedMod(modId, false);
                return;
            }

            string key = MakeModKey(modId);
            _versions.SeedOriginal(key, "", false);
            _versions.RecordSuccessfulExecution(key, source ?? "");
            SetActiveFlag(modId, true);
            _status = $"Saved mod '{modId}' for next scene start.";
        }

        private void OnModSourceUnloaded(string modId, string source, LuaCapabilities capabilities)
        {
            if (!_isAutoloading)
            {
                RecomputeModLists();
            }

            if (_isAutoloading || _versions == null || string.IsNullOrWhiteSpace(modId))
            {
                return;
            }

            string key = MakeModKey(modId);
            _versions.SeedOriginal(key, "", false);
            _versions.RecordSuccessfulExecution(key, source ?? "");
            SetActiveFlag(modId, false);
            _status = $"Deactivated mod '{modId}'. It remains available for activation.";
        }

        private string MakeModKey(string modId)
        {
            return modKeyPrefix + (modId ?? "").Trim();
        }

        private string MakeActiveFlagKey(string modId)
        {
            return modKeyPrefix + ActiveFlagSegment + (modId ?? "").Trim();
        }

        private bool IsActiveFlagKey(string key)
        {
            return key.StartsWith(modKeyPrefix + ActiveFlagSegment, System.StringComparison.Ordinal);
        }

        private void SetActiveFlag(string modId, bool active)
        {
            string key = MakeActiveFlagKey(modId);
            _versions.SeedOriginal(key, "", false);
            _versions.RecordSuccessfulExecution(key, active ? "1" : "0");
        }

        private bool ShouldAutoload(string modId)
        {
            if (_versions.TryGetSnapshot(MakeActiveFlagKey(modId), out LuaScriptVersionRecord flag) &&
                !string.IsNullOrWhiteSpace(flag.CurrentLua))
            {
                return flag.CurrentLua.Trim() == "1";
            }

            return true;
        }

        /// <summary>
        /// Rebuilds the panel: a header, one row per mod with its own actions, and the editor when
        /// a mod is open.
        /// </summary>
        /// <remarks>
        /// WHY the rows are rebuilt rather than updated in place: the mod list changes from
        /// activation, deactivation and chat-driven loads, and an in-place update would need a
        /// diffing pass whose only purpose is to save a few allocations on a click. The rebuild
        /// happens when the list changes, not per frame.
        /// </remarks>
        private void RefreshPanel()
        {
            if (_panel == null)
            {
                return;
            }

            _panel.ClearRows();
            _panel.ClearButtons();

            System.Text.StringBuilder header = new();
            header.AppendLine(_status);
            if (_autoRepair != null)
            {
                header.AppendLine($"<b>Auto-repair:</b> {_autoRepair.StatusLine}");
            }

            if (_mods == null || _versions == null)
            {
                header.AppendLine("Waiting for runtime...");
                _panel.SetLog(header.ToString());
                return;
            }

            header.AppendLine($"active {_cachedActiveCount} / inactive {_cachedInactiveCount}");
            if (!string.IsNullOrEmpty(_editError))
            {
                header.AppendLine($"<color=#FF7070>{_editError}</color>");
            }

            _panel.SetLog(header.ToString());
            BuildActiveRows();
            BuildInactiveRows();
            BuildEditorControls();
        }

        private void BuildActiveRows()
        {
            // Local copies: the deactivate action below replaces the cached fields with new list
            // instances, so this loop keeps iterating its own stable snapshot.
            List<LuaModInfo> active = _cachedActiveMods;
            List<ModDescriptor> descriptors = _cachedActiveDescriptors;
            for (int index = 0; index < active.Count; index++)
            {
                LuaModInfo info = active[index];
                ModDescriptor descriptor = descriptors[index];
                bool logReports = _mods.GetModReportLoggingEnabled(_actorContext, info.Id);
                string label = $"[ACTIVE] <b>{descriptor.Name}</b>  id: {info.Id}  " +
                               $"caps={info.Capabilities}  errors={info.ErrorCount}  " +
                               $"logs={(logReports ? "on" : "off")}";
                _panel.AddRow(label,
                    ("Logs", () => ToggleLogs(info.Id)),
                    ("Edit", () => OpenEditor(info.Id, descriptor.Source)),
                    ("Deactivate", () => Deactivate(info.Id)));
            }
        }

        private void BuildInactiveRows()
        {
            List<ModDescriptor> inactive = _cachedInactiveMods;
            for (int index = 0; index < inactive.Count; index++)
            {
                ModDescriptor descriptor = inactive[index];
                _panel.AddRow($"[ inactive ] <b>{descriptor.Name}</b>  id: {descriptor.Id}",
                    ("Activate", () => ActivateSavedMod(descriptor)),
                    ("Edit", () => OpenEditor(descriptor.Id, descriptor.Source)),
                    ("Forget", () => ForgetSavedMod(descriptor.Id, true)));
            }
        }

        private void BuildEditorControls()
        {
            if (_editModId == null)
            {
                return;
            }

            _editor ??= _panel.AddEditor("mod source");
            _editor.gameObject.SetActive(true);
            if (!_editorLoaded)
            {
                _editor.text = _editBuffer ?? "";
                _editorLoaded = true;
            }

            _panel.AddButton("Save " + _editModId, SaveEditor);
            _panel.AddButton("Close editor", CloseEditor);
        }

        private void ToggleLogs(string modId)
        {
            bool next = !_mods.GetModReportLoggingEnabled(_actorContext, modId);
            _mods.SetModReportLoggingEnabled(_actorContext, modId, next);
            _status = $"Mod '{modId}' logs {(next ? "enabled" : "disabled")}.";
            RefreshPanel();
        }

        private void Deactivate(string modId)
        {
            _mods.UnloadMod(_actorContext, modId);
            RecomputeModLists();
        }

        private void OpenEditor(string modId, string source)
        {
            _editModId = modId;
            _editBuffer = source ?? "";
            _editError = "";
            _editorLoaded = false;
            RefreshPanel();
        }

        private void CloseEditor()
        {
            // Discard: the buffer was a copy, nothing was touched.
            _editModId = null;
            _editorLoaded = false;
            if (_editor != null)
            {
                _editor.gameObject.SetActive(false);
            }

            RefreshPanel();
        }

        private void SaveEditor()
        {
            if (_editModId == null || _mods == null || _versions == null)
            {
                return;
            }

            if (_editor != null)
            {
                _editBuffer = _editor.text;
            }

            if (string.IsNullOrWhiteSpace(_editBuffer))
            {
                _editError = "Source is empty; nothing saved.";
                RefreshPanel();
                return;
            }

            try
            {
                if (_mods.IsLoaded(_actorContext, _editModId))
                {
                    // Reload persists the new source and re-registers hooks; a compile/runtime error
                    // throws here, keeps the old mod running, and leaves the editor open to fix it.
                    _mods.ReloadMod(_actorContext, _editModId, _editBuffer);
                }
                else
                {
                    string key = MakeModKey(_editModId);
                    _versions.SeedOriginal(key, "", false);
                    _versions.RecordSuccessfulExecution(key, _editBuffer);
                }

                _status = $"Saved mod '{_editModId}'.";
                CloseEditor();
                RecomputeModLists();
            }
            catch (System.Exception ex)
            {
                _editError = ex.Message;
                RefreshPanel();
            }
        }

        /// <summary>
        /// Rebuilds the cached active/inactive lists and counts from the runtime and the version
        /// store. This is the only place allowed to call the disk-backed store enumeration
        /// (GetKnownKeys/TryGetSnapshot via GetInactiveSavedMods) and ListMods/TryGetModSource/
        /// ReadMetadata; the panel builders must only read the cached fields.
        /// </summary>
        private void RecomputeModLists()
        {
            if (_mods == null || _versions == null)
            {
                return;
            }

            IReadOnlyList<LuaModInfo> active = _mods.ListMods(_actorContext);
            List<LuaModInfo> activeMods = new(active.Count);
            List<ModDescriptor> activeDescriptors = new(active.Count);
            foreach (LuaModInfo info in active)
            {
                _mods.TryGetModSource(_actorContext, info.Id, out string source);
                activeMods.Add(info);
                activeDescriptors.Add(new ModDescriptor(info.Id, source));
            }

            _cachedActiveMods = activeMods;
            _cachedActiveDescriptors = activeDescriptors;
            _cachedActiveCount = activeMods.Count;

            _cachedInactiveMods = GetInactiveSavedMods();
            _cachedInactiveCount = _cachedInactiveMods.Count;
        }

        private List<ModDescriptor> GetInactiveSavedMods()
        {
            List<ModDescriptor> inactive = new();
            foreach (string key in _versions.GetKnownKeys())
            {
                if (string.IsNullOrEmpty(key) ||
                    !key.StartsWith(modKeyPrefix, System.StringComparison.Ordinal) ||
                    IsActiveFlagKey(key))
                {
                    continue;
                }

                string modId = key.Substring(modKeyPrefix.Length);
                if (string.IsNullOrWhiteSpace(modId) || IsTransientModId(modId) ||
                    _mods.IsLoaded(_actorContext, modId) ||
                    !_versions.TryGetSnapshot(key, out LuaScriptVersionRecord record) ||
                    string.IsNullOrWhiteSpace(record.CurrentLua) ||
                    ShouldAutoload(modId))
                {
                    continue;
                }

                inactive.Add(new ModDescriptor(modId, record.CurrentLua));
            }

            return inactive;
        }

        private void ActivateSavedMod(ModDescriptor descriptor)
        {
            try
            {
                if (_mods.IsLoaded(_actorContext, descriptor.Id))
                {
                    _mods.ReloadMod(_actorContext, descriptor.Id, descriptor.Source);
                }
                else
                {
                    // WHY: Grant the SAME tier the autoload path grants (see LoadSavedMods). A Full-tier mod
                    // (unity_* APIs) loaded with a hardcoded All silently no-ops here, so a mod activated from
                    // the panel button would lose the unity_* calls it has when autoloaded at scene start.
                    LuaCapabilities activateCaps = coreAiScope != null && coreAiScope.FullLuaAccessEnabled
                        ? LuaCapabilities.All | LuaCapabilities.Full
                        : LuaCapabilities.All;
                    _mods.LoadMod(_actorContext, descriptor.Id, descriptor.Source, activateCaps);
                }

                _status = $"Activated mod '{descriptor.Id}'.";
            }
            catch (System.Exception ex)
            {
                _status = $"Activation failed: {ex.Message}";
                Debug.LogWarning($"[LiveMechanicsModsChatDemo] Activation failed for '{descriptor.Id}': {ex}");
            }

            RecomputeModLists();
        }

        private void ForgetSavedMod(string modId, bool updateStatus)
        {
            string key = MakeModKey(modId);
            _versions.SeedOriginal(key, "", false);
            _versions.RecordSuccessfulExecution(key, "");
            SetActiveFlag(modId, false);
            if (updateStatus)
            {
                _status = $"Forgot saved mod '{modId}'.";
            }

            RecomputeModLists();
        }

        private bool IsTransientModId(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId) || transientModIds == null)
            {
                return false;
            }

            string trimmed = modId.Trim();
            for (int i = 0; i < transientModIds.Length; i++)
            {
                string candidate = transientModIds[i];
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(trimmed, candidate.Trim(), System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReadMetadata(string source, string key)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string prefix = "-- " + key + ":";
            string[] lines = source.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }

            return null;
        }
#else
        private void Start()
        {
            Debug.LogWarning(
                "[LiveMechanicsModsChatDemo] COREAI_LUA is not set; demo persistence is inactive.");
            enabled = false;
        }
#endif
    }
}
