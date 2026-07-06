using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
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
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        private const string DefaultModKeyPrefix = "demo.live_mechanics.mods_chat.mod.";
        private const string ActiveFlagSegment = "__active__.";

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [SerializeField] private string modKeyPrefix = DefaultModKeyPrefix;

        [SerializeField] private string panelTitle = "Lua Mod Manager";

        // F9 toggles the mod manager; F10 is reserved for the Token Budget / usage overlay.
        [Tooltip("Hotkey that toggles the mod manager. Set to None to disable keyboard toggling.")] [SerializeField]
        private KeyCode toggleKey = KeyCode.F9;

        [SerializeField] private Rect panelRect = new(24, 92, 430, 460);

        [SerializeField] private bool showPanel = true;

        [Tooltip("Saved ids that are validation artifacts and must not autoload in the playable demo.")]
        [SerializeField]
        private string[] transientModIds = { "auto_repair_smoke" };

        private const int WindowId = 0x10D_0001;
        private const int EditWindowId = 0x10D_0003;

        private LuaModRuntime _mods;
        private ILuaScriptVersionStore _versions;
        private CoreAiLuaModAutoRepair _autoRepair;
        private string _status = "Waiting for CoreAI scope.";
        private int _autoloadedCount;
        private bool _isAutoloading;
        private Vector2 _scroll;
        private GUIStyle _richLabel;

        // Cached mod lists. Rebuilt only on ModSourceLoaded/ModSourceUnloaded and after user actions
        // in this panel (Activate/Deactivate/Forget/Save) — never from OnGUI or a Draw method. See
        // Docs/coreai-mod-system.md §6.
        private List<LuaModInfo> _cachedActiveMods = new();
        private List<ModDescriptor> _cachedActiveDescriptors = new();
        private List<ModDescriptor> _cachedInactiveMods = new();
        private int _cachedActiveCount;
        private int _cachedInactiveCount;

        // Mod source editor state. Non-null _editModId means the editor window is open; the buffer
        // is a private copy, so closing without saving changes nothing anywhere.
        private string _editModId;
        private string _editBuffer = "";
        private string _editError = "";
        private Vector2 _editScroll;
        private Rect _editRect = new(120, 60, 620, 480);
        private GUIStyle _activeBadge;
        private GUIStyle _inactiveBadge;

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
            set => showPanel = value;
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
            panelRect = rect;
            showPanel = visible;
        }

        private void Update()
        {
            if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
            {
                showPanel = !showPanel;
            }
        }

        private IEnumerator Start()
        {
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

            _mods = coreAiScope.Container.Resolve<LuaModRuntime>();
            _versions = coreAiScope.Container.Resolve<ILuaScriptVersionStore>();
            _autoRepair = FindFirstObjectByType<CoreAiLuaModAutoRepair>();
            _mods.ModSourceLoaded += OnModSourceLoaded;
            _mods.ModSourceUnloaded += OnModSourceUnloaded;

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

            _mods.ModSourceLoaded -= OnModSourceLoaded;
            _mods.ModSourceUnloaded -= OnModSourceUnloaded;
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
                        if (_mods.IsLoaded(modId))
                        {
                            _mods.ReloadMod(modId, record.CurrentLua);
                        }
                        else
                        {
                            // Grant the same tier the host composition grants: a Full-tier mod
                            // (unity_* APIs) autoloaded with a hardcoded All silently no-ops.
                            LuaCapabilities autoloadCaps = coreAiScope != null && coreAiScope.FullLuaAccessEnabled
                                ? LuaCapabilities.All | LuaCapabilities.Full
                                : LuaCapabilities.All;
                            _mods.LoadMod(modId, record.CurrentLua, autoloadCaps);
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

        private void OnGUI()
        {
            if (!showPanel)
            {
                return;
            }

            EnsureStyles();

            // Keep the window reachable, then let the user drag it anywhere.
            panelRect.x = Mathf.Clamp(panelRect.x, 0f, Mathf.Max(0f, Screen.width - 120f));
            panelRect.y = Mathf.Clamp(panelRect.y, 0f, Mathf.Max(0f, Screen.height - 40f));

            string title =
                $"{panelTitle}  ({toggleKey})   active {_cachedActiveCount} / inactive {_cachedInactiveCount}";
            panelRect = GUILayout.Window(WindowId, panelRect, DrawWindow, title);

            if (_editModId != null)
            {
                _editRect.x = Mathf.Clamp(_editRect.x, 0f, Mathf.Max(0f, Screen.width - 160f));
                _editRect.y = Mathf.Clamp(_editRect.y, 0f, Mathf.Max(0f, Screen.height - 60f));
                _editRect = GUILayout.Window(EditWindowId, _editRect, DrawEditWindow, $"Edit mod: {_editModId}");
            }
        }

        private void OpenEditor(string modId, string source)
        {
            _editModId = modId;
            _editBuffer = source ?? "";
            _editError = "";
            _editScroll = Vector2.zero;
        }

        private void DrawEditWindow(int id)
        {
            _editScroll = GUILayout.BeginScrollView(_editScroll, GUILayout.ExpandHeight(true));
            _editBuffer = GUILayout.TextArea(_editBuffer, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_editError))
            {
                GUILayout.Label($"<color=#FF7070>{_editError}</color>", _richLabel);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", GUILayout.Width(90)))
            {
                SaveEditor();
            }

            if (GUILayout.Button("Close", GUILayout.Width(90)))
            {
                // Discard: the buffer was a copy, nothing was touched.
                _editModId = null;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(_mods != null && _mods.IsLoaded(_editModId ?? "")
                ? "Save reloads the running mod and persists the new source."
                : "Save updates the saved source (mod stays inactive).", _richLabel);
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, _editRect.width, 22f));
        }

        private void SaveEditor()
        {
            if (_editModId == null || _mods == null || _versions == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_editBuffer))
            {
                _editError = "Source is empty; nothing saved.";
                return;
            }

            try
            {
                if (_mods.IsLoaded(_editModId))
                {
                    // Reload persists the new source and re-registers hooks; a compile/runtime error
                    // throws here, keeps the old mod running, and leaves the editor open to fix it.
                    _mods.ReloadMod(_editModId, _editBuffer);
                }
                else
                {
                    string key = MakeModKey(_editModId);
                    _versions.SeedOriginal(key, "", false);
                    _versions.RecordSuccessfulExecution(key, _editBuffer);
                }

                _status = $"Saved mod '{_editModId}'.";
                _editModId = null;
                RecomputeModLists();
            }
            catch (System.Exception ex)
            {
                _editError = ex.Message;
            }
        }

        private void DrawWindow(int id)
        {
            if (GUI.Button(new Rect(panelRect.width - 58f, 2f, 52f, 18f), "Hide"))
            {
                showPanel = false;
            }

            GUILayout.Label(_status, _richLabel);
            if (_autoRepair != null)
            {
                GUILayout.Label($"<b>Auto-repair:</b> {_autoRepair.StatusLine}", _richLabel);
            }

            GUILayout.Space(4);

            if (_mods == null || _versions == null)
            {
                GUILayout.Label("Waiting for runtime...", _richLabel);
                GUI.DragWindow(new Rect(0, 0, panelRect.width, 22f));
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            DrawActiveMods();
            GUILayout.Space(8);
            DrawSavedInactiveMods();
            GUILayout.EndScrollView();

            GUI.DragWindow(new Rect(0, 0, panelRect.width, 22f));
        }

        private void EnsureStyles()
        {
            if (_richLabel != null)
            {
                return;
            }

            _richLabel = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            _activeBadge = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.40f, 0.85f, 0.45f) }
            };
            _inactiveBadge = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.65f, 0.65f, 0.68f) }
            };
        }

        private void DrawActiveMods()
        {
            // Local copies: RecomputeModLists() (triggered by Deactivate below) replaces the cached
            // fields with new list instances, so this loop keeps iterating its own stable snapshot.
            List<LuaModInfo> active = _cachedActiveMods;
            List<ModDescriptor> descriptors = _cachedActiveDescriptors;
            GUILayout.Label($"<b>Active mods</b>  ({active.Count})", _richLabel);
            if (active.Count == 0)
            {
                GUILayout.Label("No active mods. Load one with manage_mods from chat.", _richLabel);
                return;
            }

            for (int i = 0; i < active.Count; i++)
            {
                LuaModInfo info = active[i];
                ModDescriptor descriptor = descriptors[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label("[ACTIVE]", _activeBadge, GUILayout.Width(64));
                GUILayout.Label($"<b>{descriptor.Name}</b>", _richLabel);
                GUILayout.FlexibleSpace();
                bool logReports = _mods.GetModReportLoggingEnabled(info.Id);
                bool nextLogReports = GUILayout.Toggle(logReports, "Logs", GUILayout.Width(58));
                if (nextLogReports != logReports)
                {
                    _mods.SetModReportLoggingEnabled(info.Id, nextLogReports);
                    _status = $"Mod '{info.Id}' logs {(nextLogReports ? "enabled" : "disabled")}.";
                }

                if (GUILayout.Button("Edit", GUILayout.Width(44)))
                {
                    OpenEditor(info.Id, descriptor.Source);
                }

                if (GUILayout.Button("Deactivate", GUILayout.Width(86)))
                {
                    _mods.UnloadMod(info.Id);
                    RecomputeModLists();
                }

                GUILayout.EndHorizontal();
                GUILayout.Label(
                    $"id: {info.Id}  caps={info.Capabilities}  handlers={info.HandlerCount}  timers={info.TimerCount}  errors={info.ErrorCount}  logs={(info.LogReports ? "on" : "off")}",
                    _richLabel);
                GUILayout.Label(descriptor.Description, _richLabel);
                GUILayout.EndVertical();
            }
        }

        private void DrawSavedInactiveMods()
        {
            // Local copy: see the comment in DrawActiveMods.
            List<ModDescriptor> inactive = _cachedInactiveMods;
            GUILayout.Label($"<b>Saved / inactive mods</b>  ({inactive.Count})", _richLabel);
            if (inactive.Count == 0)
            {
                GUILayout.Label("No saved inactive mods.", _richLabel);
                return;
            }

            foreach (ModDescriptor descriptor in inactive)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label("[ inactive ]", _inactiveBadge, GUILayout.Width(78));
                GUILayout.Label($"<b>{descriptor.Name}</b>", _richLabel);
                GUILayout.EndHorizontal();
                GUILayout.Label($"id: {descriptor.Id}", _richLabel);
                GUILayout.Label(descriptor.Description, _richLabel);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Activate"))
                {
                    ActivateSavedMod(descriptor);
                }

                if (GUILayout.Button("Edit", GUILayout.Width(44)))
                {
                    OpenEditor(descriptor.Id, descriptor.Source);
                }

                if (GUILayout.Button("Forget", GUILayout.Width(72)))
                {
                    ForgetSavedMod(descriptor.Id, true);
                }

                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        /// <summary>
        /// Rebuilds the cached active/inactive lists and counts from the runtime and the version
        /// store. This is the only place allowed to call the disk-backed store enumeration
        /// (GetKnownKeys/TryGetSnapshot via GetInactiveSavedMods) and ListMods/TryGetModSource/
        /// ReadMetadata; OnGUI and the Draw* methods must only read the cached fields.
        /// </summary>
        private void RecomputeModLists()
        {
            if (_mods == null || _versions == null)
            {
                return;
            }

            IReadOnlyList<LuaModInfo> active = _mods.ListMods();
            List<LuaModInfo> activeMods = new(active.Count);
            List<ModDescriptor> activeDescriptors = new(active.Count);
            foreach (LuaModInfo info in active)
            {
                _mods.TryGetModSource(info.Id, out string source);
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
                if (string.IsNullOrWhiteSpace(modId) || IsTransientModId(modId) || _mods.IsLoaded(modId) ||
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
                if (_mods.IsLoaded(descriptor.Id))
                {
                    _mods.ReloadMod(descriptor.Id, descriptor.Source);
                }
                else
                {
                    _mods.LoadMod(descriptor.Id, descriptor.Source, LuaCapabilities.All);
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
                "[LiveMechanicsModsChatDemo] MoonSharp is unavailable or COREAI_NO_LUA is set; demo persistence is inactive.");
            enabled = false;
        }
#endif
    }
}